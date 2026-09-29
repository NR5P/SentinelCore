import OrganizerKit
import SwiftUI
import UniformTypeIdentifiers

// Board automations: a no-code rule builder for one board, the dashboard's
// organizer-automations.js in SwiftUI. People tap (or drag) blocks into WHEN / ONLY IF / THEN,
// fill in the blanks, and read the rule back as a plain sentence. The rules run on the server,
// so a card scheduled for 7am appears with every device asleep; nothing here is kept offline.
// The wording, recipes and checks live in OrganizerKit (BoardAutomations.swift) with tests.

// MARK: - what the editor knows about the board

struct AutomationBoardContext {
    struct OtherBoard: Identifiable {
        let id: String
        let title: String
        let columns: [OrganizerItem]
    }

    let board: OrganizerItem
    let columns: [OrganizerItem]
    // Other boards' lists, for "move it to another board".
    let otherBoards: [OtherBoard]
    let labels: [String]
    let people: [OrganizerPerson]
    let pushReady: Bool
    let emailReady: Bool
    let wording: AutomationWording
    let makeContext: AutomationMakeContext

    @MainActor
    init(engine: OrganizerEngine, board: OrganizerItem, people: [OrganizerPerson], pushReady: Bool, emailReady: Bool) {
        func columnsOf(_ id: String) -> [OrganizerItem] {
            engine.children(of: id, kind: .column).sorted { $0.sortPosition < $1.sortPosition }
        }
        self.board = board
        columns = columnsOf(board.id)
        otherBoards = engine.items(.board)
            .filter { $0.id != board.id && !$0.bool("archived") }
            .sorted { $0.sortPosition < $1.sortPosition }
            .map { OtherBoard(id: $0.id, title: $0.title.isEmpty ? "board" : $0.title, columns: columnsOf($0.id)) }
            .filter { !$0.columns.isEmpty }
        var names = Set<String>()
        for column in columns {
            for card in engine.children(of: column.id, kind: .card) {
                for label in card.fields["labels"]?.array ?? [] {
                    if let name = label.object?["name"]?.string, !name.isEmpty { names.insert(name) }
                }
            }
        }
        labels = names.sorted()
        self.people = people
        self.pushReady = pushReady
        self.emailReady = emailReady
        let titles = Dictionary(engine.items(.column).map { ($0.id, $0.title) }, uniquingKeysWith: { first, _ in first })
        wording = AutomationWording(columnTitle: { titles[$0] }, people: people)
        makeContext = AutomationMakeContext(firstColumnId: columns.first?.id ?? "", lastColumnId: columns.last?.id ?? "",
                                            timeZone: TimeZone.current.identifier, people: people)
    }

    func knows(column id: String) -> Bool {
        columns.contains { $0.id == id } || otherBoards.contains { $0.columns.contains { $0.id == id } }
    }
}

// What a dragged block carries: a palette block to add, or a placed step being reordered. A
// private type rather than plain text, so a text field under the drop point can't swallow it.
struct RuleBlockDrag: Codable, Transferable {
    var zone: String
    var type: String?
    var from: Int?

    static var transferRepresentation: some TransferRepresentation {
        CodableRepresentation(contentType: .organizerRuleBlock)
    }
}

extension UTType {
    // Declared in Info.plist (UTExportedTypeDeclarations).
    static let organizerRuleBlock = UTType(exportedAs: "com.sentinelcore.client.organizer-rule-block")
}

extension AutomationZone {
    var color: Color {
        switch self {
        case .trigger: SentinelTheme.amber
        case .conditions: SentinelTheme.cyan
        case .actions: SentinelTheme.green
        }
    }
}

// MARK: - the list of rules

struct BoardAutomationsView: View {
    @Environment(OrganizerEngine.self) private var engine
    @Environment(\.dismiss) private var dismiss
    let board: OrganizerItem

    @State private var rules: [BoardAutomation] = []
    @State private var loaded = false
    @State private var offline = false
    @State private var pushReady = true
    @State private var emailReady = true
    @State private var people: [OrganizerPerson] = []
    @State private var draft: BoardAutomation?
    @State private var notice: Notice?
    @State private var deleting: BoardAutomation?
    @State private var history: [String: HistoryState] = [:]
    @State private var running: Set<String> = []

    struct Notice: Equatable {
        var text: String
        var ok: Bool
    }

    enum HistoryState {
        case loading
        case runs([BoardAutomationRun])
        case failed(String)
    }

    var body: some View {
        let context = AutomationBoardContext(engine: engine, board: board, people: people, pushReady: pushReady, emailReady: emailReady)
        NavigationStack {
            Group {
                if draft != nil {
                    AutomationRuleEditor(
                        draft: Binding(get: { draft ?? BoardAutomation() }, set: { draft = $0 }),
                        context: context,
                        back: { draft = nil },
                        saved: {
                            draft = nil
                            show("Rule saved.", ok: true)
                            Task { await load() }
                        })
                } else {
                    list(context)
                        .navigationTitle("Automations")
                        .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } } }
                }
            }
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
        }
        .task {
            if engine.status?.personalSpaces == true { people = (try? await engine.people()) ?? [] }
            await load()
        }
        .confirmationDialog("Delete the rule \"\(deleting?.name ?? "")\"?",
                            isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } }),
                            titleVisibility: .visible, presenting: deleting) { rule in
            Button("Delete Rule", role: .destructive) { Task { await remove(rule) } }
        } message: { _ in
            Text("Cards it already made stay where they are.")
        }
    }

    @ViewBuilder
    private func list(_ context: AutomationBoardContext) -> some View {
        if !loaded {
            ProgressView("Loading…").frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if offline {
            ContentUnavailableView {
                Label("Automations", systemImage: "bolt.slash")
            } description: {
                Text(BoardAutomations.offlineMessage)
            } actions: {
                Button("Try Again") { Task { await load() } }
            }
        } else {
            List {
                Section {
                    Text("Rules that look after \(board.title.isEmpty ? "this board" : "“\(board.title)”") for you. They run on the server, so they happen even when every phone and computer is off.")
                        .foregroundStyle(.secondary)
                    if let notice {
                        Label(notice.text, systemImage: notice.ok ? "checkmark.circle" : "exclamationmark.triangle")
                            .foregroundStyle(notice.ok ? SentinelTheme.green : SentinelTheme.red)
                    }
                }
                Section {
                    Button("Build a Rule", systemImage: "plus") { draft = BoardAutomation.draft(editing: nil, boardId: board.id) }
                } header: {
                    Text("Start a rule")
                }
                Section {
                    let recipes = BoardAutomations.recipes(
                        firstColumnId: context.makeContext.firstColumnId, lastColumnId: context.makeContext.lastColumnId,
                        lastColumnName: context.wording.columnName(context.makeContext.lastColumnId), timeZone: TimeZone.current.identifier)
                    ForEach(recipes) { recipe in
                        Button {
                            draft = BoardAutomation.draft(editing: nil, recipe: recipe.rule, boardId: board.id)
                        } label: {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(recipe.name).bold()
                                Text(recipe.why).font(.caption).foregroundStyle(.secondary)
                            }
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                    }
                } header: {
                    Text("Or start from a recipe")
                }
                Section("Rules") {
                    if rules.isEmpty {
                        Text("No rules yet. Build one, or pick a recipe above and change it to suit.").foregroundStyle(.secondary)
                    }
                    ForEach(rules) { rule in
                        row(rule, context)
                    }
                }
            }
            #if os(macOS)
            .listStyle(.inset)
            #endif
        }
    }

    private func row(_ rule: BoardAutomation, _ context: AutomationBoardContext) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .top, spacing: 10) {
                Toggle("On", isOn: Binding(get: { rule.enabled }, set: { on in Task { await setEnabled(rule, on) } }))
                    .labelsHidden()
                    .toggleStyle(.switch)
                    .help("on / off")
                VStack(alignment: .leading, spacing: 4) {
                    Text(rule.name).font(.headline)
                    Text(context.wording.sentence(rule)).font(.subheadline).foregroundStyle(.secondary)
                    status(rule, context)
                }
                Spacer(minLength: 0)
            }
            HStack(spacing: 14) {
                if rule.isScheduled {
                    Button("Run Now", systemImage: "play") { Task { await runNow(rule) } }
                        .disabled(running.contains(rule.id))
                        .help("Do it right now, to try it out")
                }
                Button("Edit", systemImage: "pencil") { draft = BoardAutomation.draft(editing: rule, boardId: board.id) }
                Button("History", systemImage: "clock.arrow.circlepath") { Task { await toggleHistory(rule) } }
                Spacer(minLength: 0)
                Button("Delete", systemImage: "trash", role: .destructive) { deleting = rule }
            }
            .font(.caption)
            .buttonStyle(.borderless)
            if let state = history[rule.id] {
                historyView(state, context)
            }
        }
        .padding(.vertical, 4)
        .opacity(rule.enabled ? 1 : 0.6)
    }

    @ViewBuilder
    private func status(_ rule: BoardAutomation, _ context: AutomationBoardContext) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            if rule.isScheduled, rule.enabled, let next = rule.nextRunAt {
                Text("next: \(context.wording.formatWhen(next))").font(.caption).foregroundStyle(SentinelTheme.cyan)
            }
            if let last = rule.lastRunAt {
                Text("\(rule.lastOk ? "✓" : "!") \(context.wording.formatWhen(last)): \(rule.lastResult)")
                    .font(.caption)
                    .foregroundStyle(rule.lastOk ? SentinelTheme.green : SentinelTheme.red)
            } else {
                Text("hasn't run yet").font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    @ViewBuilder
    private func historyView(_ state: HistoryState, _ context: AutomationBoardContext) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            switch state {
            case .loading:
                ProgressView().controlSize(.small)
            case let .failed(message):
                Text(message).foregroundStyle(SentinelTheme.red)
            case let .runs(runs):
                if runs.isEmpty { Text("Nothing yet.").foregroundStyle(.secondary) }
                ForEach(runs) { run in
                    Text("\(run.ok ? "✓" : "!") \(context.wording.formatWhen(run.at)) - \(run.message)")
                        .foregroundStyle(run.ok ? Color.primary : SentinelTheme.red)
                }
            }
        }
        .font(.caption)
        .padding(8)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.primary.opacity(0.04), in: RoundedRectangle(cornerRadius: 8))
    }

    // MARK: talking to the server

    private func load() async {
        do {
            let list = try await engine.automations(boardId: board.id)
            rules = list.automations
            pushReady = list.pushReady
            emailReady = list.emailReady
            offline = false
        } catch {
            offline = true
        }
        loaded = true
    }

    private func setEnabled(_ rule: BoardAutomation, _ enabled: Bool) async {
        if let index = rules.firstIndex(where: { $0.id == rule.id }) { rules[index].enabled = enabled }
        do {
            try await engine.setAutomationEnabled(rule, enabled)
        } catch {
            show(BoardAutomations.messages(for: error).joined(separator: " "), ok: false)
        }
        await load()
    }

    private func runNow(_ rule: BoardAutomation) async {
        running.insert(rule.id)
        defer { running.remove(rule.id) }
        do {
            // The engine syncs after a run, so a card it made is on the board when this returns.
            let run = try await engine.runAutomation(id: rule.id)
            show(run.message.isEmpty ? "Done." : run.message, ok: run.ok)
            if history[rule.id] != nil { history[rule.id] = nil }
        } catch {
            show(BoardAutomations.messages(for: error).joined(separator: " "), ok: false)
        }
        await load()
    }

    private func remove(_ rule: BoardAutomation) async {
        do {
            try await engine.deleteAutomation(id: rule.id)
        } catch {
            show(BoardAutomations.messages(for: error).joined(separator: " "), ok: false)
        }
        await load()
    }

    private func toggleHistory(_ rule: BoardAutomation) async {
        if history[rule.id] != nil {
            history[rule.id] = nil
            return
        }
        history[rule.id] = .loading
        do {
            history[rule.id] = .runs(try await engine.automationRuns(id: rule.id))
        } catch {
            history[rule.id] = .failed(BoardAutomations.messages(for: error).joined(separator: " "))
        }
    }

    private func show(_ text: String, ok: Bool) {
        let shown = Notice(text: text, ok: ok)
        notice = shown
        Task {
            try? await Task.sleep(for: .seconds(3.5))
            if notice == shown { notice = nil }
        }
    }
}

// MARK: - the editor

// Which text box a {token} goes into: a block and one of its fields.
struct AutomationTextTarget: Hashable {
    var block: UUID
    var key: String
}

struct AutomationRuleEditor: View {
    @Environment(OrganizerEngine.self) private var engine
    @Binding var draft: BoardAutomation
    let context: AutomationBoardContext
    let back: () -> Void
    let saved: () -> Void

    @State private var errors: [String] = []
    @State private var saving = false
    @State private var nextRuns: String?
    @State private var nextRunsBad = false
    @State private var dropTarget: String?
    @State private var lastTextTarget: AutomationTextTarget?
    @State private var tokenHint: String?
    @FocusState private var focus: AutomationTextTarget?

    // The fields a {token} can go into, as the dashboard marks them.
    static let tokenFields: Set<String> = ["createCard.title", "addComment.text", "notify.title", "notify.text", "email.title", "email.text"]

    var body: some View {
        GeometryReader { geometry in
            let wide = geometry.size.width >= 680
            HStack(alignment: .top, spacing: 0) {
                if wide {
                    palette
                        .frame(width: 250)
                    Divider()
                }
                ScrollView {
                    VStack(alignment: .leading, spacing: 18) {
                        TextField("Name", text: $draft.name, prompt: Text("Name this rule, e.g. Eggs every morning"))
                            .labelsHidden()
                            .multilineTextAlignment(.leading)
                            .textFieldStyle(.roundedBorder)
                            .font(.title3)
                        if !wide { tokenStrip }
                        zone(.trigger)
                        zone(.conditions)
                        zone(.actions)
                        summary
                    }
                    .padding()
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
        }
        .navigationTitle(draft.id.isEmpty ? "New Rule" : "Edit Rule")
        .toolbar {
            ToolbarItem(placement: .cancellationAction) { Button("Back") { back() } }
            ToolbarItem(placement: .confirmationAction) {
                Button("Save") { Task { await save() } }.disabled(saving)
            }
        }
        .onChange(of: focus) { _, target in
            if let target, takesTokens(target) { lastTextTarget = target }
        }
        .task(id: previewKey) { await preview() }
    }

    // MARK: palette

    private var palette: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                Text("Drag a block into the rule, or click it.").font(.caption).foregroundStyle(.secondary)
                ForEach(AutomationZone.allCases, id: \.self) { zone in
                    VStack(alignment: .leading, spacing: 6) {
                        Text(zone.title).font(.caption.bold()).foregroundStyle(zone.color)
                        ForEach(BoardAutomations.palette(zone, hasPeople: !context.people.isEmpty)) { kind in
                            Button { add(zone, kind.type, at: nil) } label: { paletteTile(kind) }
                                .buttonStyle(.plain)
                                .draggable(RuleBlockDrag(zone: zone.rawValue, type: kind.type)) { paletteTile(kind).frame(width: 220) }
                        }
                    }
                }
                VStack(alignment: .leading, spacing: 6) {
                    Text("WORDS THAT FILL THEMSELVES IN").font(.caption.bold()).foregroundStyle(.secondary)
                    tokenChips
                }
            }
            .padding()
        }
        .background(Color.primary.opacity(0.03))
    }

    private func paletteTile(_ kind: AutomationBlockKind) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: kind.symbol).foregroundStyle(kind.zone.color).frame(width: 20)
            VStack(alignment: .leading, spacing: 1) {
                Text(kind.label).font(.callout.bold())
                if let hint = kind.hint { Text(hint).font(.caption2).foregroundStyle(.secondary) }
            }
            Spacer(minLength: 0)
        }
        .padding(8)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(kind.zone.color.opacity(0.08), in: RoundedRectangle(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(kind.zone.color.opacity(0.3)))
        .contentShape(Rectangle())
    }

    private var tokenStrip: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Words that fill themselves in - tap one to add it to the last title or message you typed in.")
                .font(.caption).foregroundStyle(.secondary)
            tokenChips
        }
    }

    private var tokenChips: some View {
        VStack(alignment: .leading, spacing: 4) {
            AutomationFlowLayout {
                ForEach(BoardAutomations.tokens, id: \.token) { token in
                    Button(token.token) { insert(token.token) }
                        .buttonStyle(.plain)
                        .font(.caption.monospaced())
                        .padding(.horizontal, 8)
                        .padding(.vertical, 4)
                        .background(SentinelTheme.cyan.opacity(0.12), in: Capsule())
                        .help("\(token.what) - drag into a title or message, or click")
                        // Plain text on purpose: dropped on a text box, it types itself in.
                        .draggable(token.token)
                }
            }
            if let tokenHint { Text(tokenHint).font(.caption).foregroundStyle(SentinelTheme.amber) }
        }
    }

    // MARK: the rule

    @ViewBuilder
    private func zone(_ zone: AutomationZone) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 6) {
                Text(zone.title).font(.caption.bold()).foregroundStyle(zone.color)
                switch zone {
                case .trigger: EmptyView()
                case .conditions: Text("(optional)").font(.caption).foregroundStyle(.secondary)
                case .actions: Text("(in this order)").font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Menu {
                    ForEach(BoardAutomations.palette(zone, hasPeople: !context.people.isEmpty)) { kind in
                        Button(kind.label, systemImage: kind.symbol) { add(zone, kind.type, at: nil) }
                    }
                } label: {
                    Label(zone == .trigger && draft.trigger != nil ? "Change" : "Add", systemImage: "plus.circle")
                        .font(.caption)
                }
                .menuStyle(.button)
                .buttonStyle(.borderless)
                .fixedSize()
            }

            switch zone {
            case .trigger:
                if let trigger = draft.trigger {
                    placed(zone, index: 0, block: trigger)
                }
            case .conditions:
                ForEach(Array(draft.conditions.enumerated()), id: \.element.id) { index, block in
                    placed(zone, index: index, block: block)
                }
            case .actions:
                ForEach(Array(draft.actions.enumerated()), id: \.element.id) { index, block in
                    placed(zone, index: index, block: block)
                }
            }

            if zone != .trigger || draft.trigger == nil {
                Text(placeholder(zone))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, minHeight: 40)
                    .background(dropTarget == zone.rawValue ? zone.color.opacity(0.15) : Color.clear, in: RoundedRectangle(cornerRadius: 8))
                    .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(zone.color.opacity(0.4), style: StrokeStyle(lineWidth: 1, dash: [4, 3])))
                    .dropDestination(for: RuleBlockDrag.self) { drags, _ in
                        drop(drags, into: zone, at: nil)
                    } isTargeted: { dropTarget = $0 ? zone.rawValue : (dropTarget == zone.rawValue ? nil : dropTarget) }
            }
        }
    }

    private func placeholder(_ zone: AutomationZone) -> String {
        switch zone {
        case .trigger: "Add a WHEN block - what starts the rule?"
        case .conditions: draft.conditions.isEmpty ? "Add ONLY IF blocks here to narrow it down" : "Add another to narrow it further"
        case .actions: draft.actions.isEmpty ? "Add THEN blocks here - what should happen?" : "Add another step here"
        }
    }

    private func placed(_ zone: AutomationZone, index: Int, block: AutomationBlock) -> some View {
        let kind = BoardAutomations.kind(zone, block.type)
        let warning: String? = switch zone {
        case .trigger: nil
        case .conditions: BoardAutomations.conditionWarning(index, in: draft)
        case .actions: BoardAutomations.actionWarning(index, in: draft)
        }
        let count = zone == .actions ? draft.actions.count : draft.conditions.count
        let targetKey = "\(zone.rawValue).\(block.id)"

        return VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                if zone == .actions {
                    Image(systemName: "line.3.horizontal")
                        .foregroundStyle(.secondary)
                        .help("Drag to reorder")
                        .draggable(RuleBlockDrag(zone: zone.rawValue, from: index)) {
                            Label(kind?.label ?? block.type, systemImage: kind?.symbol ?? "questionmark.square").padding(6)
                        }
                }
                Image(systemName: kind?.symbol ?? "questionmark.square").foregroundStyle(zone.color)
                Text(kind?.label ?? block.type).bold()
                Spacer()
                if zone == .actions {
                    Button { moveAction(index, to: index - 1) } label: { Image(systemName: "chevron.up") }
                        .disabled(index == 0)
                        .accessibilityLabel("Move up")
                    Button { moveAction(index, to: index + 2) } label: { Image(systemName: "chevron.down") }
                        .disabled(index >= count - 1)
                        .accessibilityLabel("Move down")
                }
                Button { remove(zone, block.id) } label: { Image(systemName: "xmark") }
                    .accessibilityLabel("Remove")
            }
            .buttonStyle(.borderless)

            if kind == nil {
                Text("This block came from a newer version and is kept as it is.").font(.caption).foregroundStyle(.secondary)
            } else {
                AutomationBlockFields(block: binding(zone, block.id), zone: zone, context: context, focus: $focus,
                                      nextRuns: zone == .trigger ? nextRuns : nil, nextRunsBad: nextRunsBad)
            }
            if let warning {
                Label(warning, systemImage: "exclamationmark.triangle").font(.caption).foregroundStyle(SentinelTheme.amber)
            }
        }
        .padding(10)
        .background((dropTarget == targetKey ? zone.color.opacity(0.18) : zone.color.opacity(0.06)), in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(warning != nil ? SentinelTheme.amber : zone.color.opacity(0.35)))
        // Dropping on a placed block puts the new one just above it.
        .dropDestination(for: RuleBlockDrag.self) { drags, _ in
            drop(drags, into: zone, at: zone == .trigger ? nil : index)
        } isTargeted: { dropTarget = $0 ? targetKey : (dropTarget == targetKey ? nil : dropTarget) }
    }

    private var summary: some View {
        VStack(alignment: .leading, spacing: 6) {
            (Text("In plain words: ").foregroundStyle(.secondary) + Text(context.wording.draftSentence(draft)))
                .font(.callout)
                .textSelection(.enabled)
            ForEach(errors, id: \.self) { error in
                Label(error, systemImage: "exclamationmark.circle").font(.caption).foregroundStyle(SentinelTheme.red)
            }
        }
        .padding(10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.primary.opacity(0.04), in: RoundedRectangle(cornerRadius: 10))
    }

    // MARK: editing

    private func binding(_ zone: AutomationZone, _ id: UUID) -> Binding<AutomationBlock> {
        Binding {
            switch zone {
            case .trigger: draft.trigger ?? AutomationBlock([:])
            case .conditions: draft.conditions.first { $0.id == id } ?? AutomationBlock([:])
            case .actions: draft.actions.first { $0.id == id } ?? AutomationBlock([:])
            }
        } set: { block in
            switch zone {
            case .trigger: if draft.trigger?.id == id { draft.trigger = block }
            case .conditions: if let i = draft.conditions.firstIndex(where: { $0.id == id }) { draft.conditions[i] = block }
            case .actions: if let i = draft.actions.firstIndex(where: { $0.id == id }) { draft.actions[i] = block }
            }
        }
    }

    private func add(_ zone: AutomationZone, _ type: String, at index: Int?) {
        let block = BoardAutomations.make(zone, type, context: context.makeContext)
        withAnimation(.snappy) {
            switch zone {
            case .trigger:
                draft.trigger = block
                if draft.name.isEmpty { draft.name = BoardAutomations.kind(zone, type)?.label ?? "" }
            case .conditions:
                draft.conditions.insert(block, at: min(index ?? draft.conditions.count, draft.conditions.count))
            case .actions:
                draft.actions.insert(block, at: min(index ?? draft.actions.count, draft.actions.count))
            }
        }
        errors = []
    }

    private func remove(_ zone: AutomationZone, _ id: UUID) {
        withAnimation(.snappy) {
            switch zone {
            case .trigger: draft.trigger = nil
            case .conditions: draft.conditions.removeAll { $0.id == id }
            case .actions: draft.actions.removeAll { $0.id == id }
            }
        }
    }

    // `to` is an insertion point in the list as it was, before `from` came out.
    private func moveAction(_ from: Int, to: Int) {
        guard draft.actions.indices.contains(from) else { return }
        withAnimation(.snappy) {
            let moved = draft.actions.remove(at: from)
            let at = to > from ? to - 1 : to
            draft.actions.insert(moved, at: max(0, min(at, draft.actions.count)))
        }
    }

    private func drop(_ drags: [RuleBlockDrag], into zone: AutomationZone, at index: Int?) -> Bool {
        dropTarget = nil
        guard let drag = drags.first, drag.zone == zone.rawValue else { return false }
        if let from = drag.from {
            guard zone == .actions else { return false }
            moveAction(from, to: index ?? draft.actions.count)
        } else if let type = drag.type {
            add(zone, type, at: index)
        }
        return true
    }

    private func takesTokens(_ target: AutomationTextTarget) -> Bool {
        guard let block = draft.actions.first(where: { $0.id == target.block }) else { return false }
        return Self.tokenFields.contains("\(block.type).\(target.key)")
    }

    // Adds a {token} to the end of the last title or message typed in - or the first one there is.
    private func insert(_ token: String) {
        let target = lastTextTarget.flatMap { takesTokens($0) ? $0 : nil } ?? draft.actions.lazy.compactMap { block in
            ["title", "text"].map { AutomationTextTarget(block: block.id, key: $0) }.first(where: takesTokens)
        }.first
        guard let target, let index = draft.actions.firstIndex(where: { $0.id == target.block }) else {
            tokenHint = "Add a card title or comment first, then tap the word to put it in."
            return
        }
        tokenHint = nil
        draft.actions[index].set(target.key, .string((draft.actions[index].string(target.key) ?? "") + token))
        lastTextTarget = target
        focus = target
    }

    private var previewKey: String? {
        guard draft.trigger?.type == "schedule", let schedule = draft.trigger?.schedule,
              let data = try? JSONEncoder().encode(schedule) else { return nil }
        return String(data: data, encoding: .utf8)
    }

    // The next few times it will run, from the server, so they're what will really happen.
    private func preview() async {
        guard previewKey != nil, let schedule = draft.trigger?.schedule else {
            nextRuns = nil
            return
        }
        try? await Task.sleep(for: .milliseconds(250))
        guard !Task.isCancelled else { return }
        do {
            let runs = try await engine.previewSchedule(schedule)
            nextRunsBad = runs.isEmpty
            nextRuns = runs.isEmpty
                ? "this won't run - that time has passed"
                : "next: \(runs.map(context.wording.formatWhen).joined(separator: "  ·  "))"
        } catch {
            nextRuns = nil
        }
    }

    private func save() async {
        draft.name = draft.name.trimmingCharacters(in: .whitespacesAndNewlines)
        errors = BoardAutomations.problems(draft)
        guard errors.isEmpty else { return }
        saving = true
        defer { saving = false }
        do {
            try await engine.saveAutomation(draft, timeZone: TimeZone.current.identifier)
            saved()
        } catch OrganizerError.offline {
            errors = [BoardAutomations.offlineMessage]
        } catch {
            errors = BoardAutomations.messages(for: error)
        }
    }
}

// MARK: - one block's blanks

private struct AutomationBlockFields: View {
    @Binding var block: AutomationBlock
    let zone: AutomationZone
    let context: AutomationBoardContext
    var focus: FocusState<AutomationTextTarget?>.Binding
    let nextRuns: String?
    let nextRunsBad: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            switch block.type {
            // WHEN
            case "schedule":
                AutomationScheduleFields(schedule: Binding(get: { block.schedule ?? AutomationBlock([:]) }, set: { block.schedule = $0 }))
                if let nextRuns {
                    Text(nextRuns).font(.caption).foregroundStyle(nextRunsBad ? SentinelTheme.red : SentinelTheme.cyan)
                }
            case "cardAdded":
                row("to") { columnPicker("columnId", allowAny: true) }
            case "cardMoved":
                row("into") { columnPicker("columnId", allowAny: true) }
            case "cardCompleted", "checklistCompleted":
                EmptyView()
            case "cardDue":
                Picker("When", selection: int("minutesBefore", 0)) {
                    ForEach(BoardAutomations.dueOffsets, id: \.minutes) { Text($0.label).tag($0.minutes) }
                }
                .labelsHidden()
                .fixedSize()
                row("for cards in") { columnPicker("columnId", allowAny: true) }
                Text("A card with a date but no time counts as due at 9:00 AM that day. Finished cards are left alone.")
                    .font(.caption).foregroundStyle(.secondary)
            case "labelAdded":
                row("called") { labelField("label", "any label") }

            // ONLY IF
            case "titleContains":
                row("the card's title contains") { text("text", "e.g. asap") }
            case "hasLabel":
                row("the card has the label") { labelField("text", "label") }
            case "lacksLabel":
                row("the card does not have the label") { labelField("text", "label") }
            case "inColumn":
                row("the card is in") { columnPicker("columnId", allowAny: false) }
            case "onDays":
                row("today is") { dayChips("days") }

            // THEN
            case "createCard":
                row("called") { text("title", "e.g. pick up eggs") }
                row("in") { columnPicker("columnId", allowAny: false) }
                row("at the") { positionPicker(defaultTop: false) }
                Toggle("Skip if an unfinished card with this title is already there", isOn: flag("skipIfExists", false))
                dueToggle("Due")
                if block.int("inDays") != nil { dueFields }
                row("labels") { AutomationListField(values: strings("labels"), placeholder: "comma separated", lines: false) }
                row("checklist") { AutomationListField(values: strings("items"), placeholder: "one item per line", lines: true) }
                if !context.people.isEmpty {
                    row("assign to") { peopleSelect(allowNone: true) }
                }
            case "moveCard":
                row("to") { columnPicker("columnId", allowAny: false) }
                row("at the") { positionPicker(defaultTop: true) }
            case "addLabel":
                row("label") { labelField("name", "label") }
                ColorPicker("Colour", selection: Binding(
                    get: { Color(hex: block.string("color") ?? "") ?? Color(hex: "#39ff9c")! },
                    set: { block.set("color", .from($0.hexString)) }), supportsOpacity: false)
                    .fixedSize()
            case "removeLabel":
                row("label") { labelField("name", "label") }
            case "setDue":
                dueToggle("Set one (untick to clear it)")
                if block.int("inDays") != nil { dueFields }
            case "complete":
                row("as") {
                    Picker("Done", selection: Binding(get: { block.bool("done") != false }, set: { block.set("done", .bool($0)) })) {
                        Text("done").tag(true)
                        Text("not done").tag(false)
                    }
                    .labelsHidden()
                    .fixedSize()
                }
            case "addChecklist":
                AutomationListField(values: strings("items"), placeholder: "one item per line", lines: true)
            case "resetChecklist":
                Text("so it can be done again").font(.caption).foregroundStyle(.secondary)
            case "addComment":
                text("text", "e.g. finished on {date}")
            case "assign":
                peopleSelect(allowNone: false)
            case "deleteCard":
                Text("for good").font(.caption).foregroundStyle(.secondary)
            case "notify":
                if !context.pushReady {
                    notReady("Push notifications aren't set up on this server yet (Settings → Push), so this won't reach anyone until they are.")
                }
                row("to") { peopleChips(everyone: true) }
                row("title") { text("title", "optional - the board's name if empty") }
                row("message") { text("text", "e.g. {card} is due {due}") }
            case "email":
                if !context.emailReady {
                    notReady("Email isn't set up on this server yet (Settings → Email), so nothing will be sent until it is.")
                }
                if !context.people.isEmpty {
                    row("to") { peopleChips(everyone: false) }
                }
                row(context.people.isEmpty ? "to" : "and") {
                    AutomationListField(values: strings("addresses"), placeholder: "other addresses, comma separated", lines: false)
                }
                row("subject") { text("title", "optional") }
                row("message") { text("text", "e.g. {card} is due {due}", multiline: true) }
            case "moveAllCards":
                row("from") { columnPicker("columnId", allowAny: false) }
                row("to") { columnPicker("toColumnId", allowAny: false) }
                Toggle("Only the ones marked done", isOn: flag("onlyCompleted", false))
            case "deleteAllCards":
                columnPicker("columnId", allowAny: false)
                Toggle("Only the cards marked done", isOn: flag("onlyCompleted", false))
            default:
                EmptyView()
            }
        }
        #if os(macOS)
        .toggleStyle(.checkbox)
        #endif
    }

    // MARK: pieces

    private func row(_ word: String, @ViewBuilder _ content: () -> some View) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Text(word).font(.callout).foregroundStyle(.secondary)
            content()
        }
    }

    private func notReady(_ text: String) -> some View {
        Label(text, systemImage: "exclamationmark.triangle").font(.caption).foregroundStyle(SentinelTheme.amber)
    }

    private func str(_ key: String) -> Binding<String> {
        Binding(get: { block.string(key) ?? "" }, set: { block.set(key, .string($0)) })
    }

    private func int(_ key: String, _ fallback: Int) -> Binding<Int> {
        Binding(get: { block.int(key) ?? fallback }, set: { block.set(key, .from($0)) })
    }

    private func flag(_ key: String, _ fallback: Bool) -> Binding<Bool> {
        Binding(get: { block.bool(key) ?? fallback }, set: { block.set(key, .bool($0)) })
    }

    private func strings(_ key: String) -> Binding<[String]> {
        Binding(get: { block.strings(key) }, set: { block.set(key, .from($0)) })
    }

    // Labels hidden and leading-aligned: in a grouped container on the Mac a titled field keeps
    // its title on the left and right-aligns what you type.
    @ViewBuilder
    private func text(_ key: String, _ placeholder: String, multiline: Bool = false) -> some View {
        let field = multiline
            ? TextField(placeholder, text: str(key), prompt: Text(placeholder), axis: .vertical)
            : TextField(placeholder, text: str(key), prompt: Text(placeholder))
        field
            .labelsHidden()
            .multilineTextAlignment(.leading)
            .textFieldStyle(.roundedBorder)
            .lineLimit(multiline ? 3...8 : 1...1)
            .focused(focus, equals: AutomationTextTarget(block: block.id, key: key))
    }

    private func labelField(_ key: String, _ placeholder: String) -> some View {
        HStack(spacing: 4) {
            text(key, placeholder)
            if !context.labels.isEmpty {
                Menu {
                    ForEach(context.labels, id: \.self) { label in
                        Button(label) { block.set(key, .string(label)) }
                    }
                } label: {
                    Image(systemName: "tag")
                }
                .menuStyle(.button)
                .buttonStyle(.borderless)
                .fixedSize()
                .help("Labels already on this board")
            }
        }
    }

    // This board's lists first; other boards' lists underneath, except where "any list" is a choice.
    private func columnPicker(_ key: String, allowAny: Bool) -> some View {
        let selected = block.string(key) ?? ""
        return Picker("List", selection: str(key)) {
            if allowAny {
                Text("any list").tag("")
            } else if selected.isEmpty || !context.knows(column: selected) {
                Text("pick a list").tag(selected)
            }
            ForEach(context.columns) { column in
                Text(column.title.isEmpty ? "list" : column.title).tag(column.id)
            }
            if !allowAny {
                ForEach(context.otherBoards) { other in
                    Section("on \(other.title)") {
                        ForEach(other.columns) { column in
                            Text(column.title.isEmpty ? "list" : column.title).tag(column.id)
                        }
                    }
                }
            }
        }
        .labelsHidden()
        .pickerStyle(.menu)
        .fixedSize()
    }

    private func positionPicker(defaultTop: Bool) -> some View {
        Picker("Position", selection: Binding(
            get: { block.string("position") ?? (defaultTop ? "top" : "bottom") },
            set: { block.set("position", .string($0)) })) {
            Text("top").tag("top")
            Text("bottom").tag("bottom")
        }
        .labelsHidden()
        .fixedSize()
    }

    private func peopleSelect(allowNone: Bool) -> some View {
        Picker("Person", selection: str("username")) {
            if allowNone { Text("nobody").tag("") }
            ForEach(context.people) { person in Text(person.label).tag(person.username) }
        }
        .labelsHidden()
        .fixedSize()
    }

    // Chips for choosing people. For a notification there's an "everyone" chip too; for email,
    // people with no address on their account are shown but can't be picked.
    private func peopleChips(everyone allowEveryone: Bool) -> some View {
        let everyone = block.bool("everyone") == true
        let chosen = block.strings("people")
        return AutomationFlowLayout {
            if allowEveryone {
                AutomationChip(title: "everyone", on: everyone) {
                    block.set("everyone", .bool(!everyone))
                    if !everyone { block.set("people", []) }
                }
                .disabled(context.people.isEmpty)
                .help(context.people.isEmpty ? "With login off, every phone gets it" : "")
            }
            ForEach(context.people) { person in
                let noEmail = !allowEveryone && person.hasEmail == false
                AutomationChip(title: person.label + (noEmail ? " (no email)" : ""), on: !everyone && chosen.contains(person.username)) {
                    block.set("people", .from(chosen.contains(person.username) ? chosen.filter { $0 != person.username } : chosen + [person.username]))
                    block.set("everyone", false)
                }
                .disabled(noEmail)
                .help(noEmail ? "No email address on this account - add one under Accounts" : "")
            }
        }
    }

    private func dayChips(_ key: String) -> some View {
        let days = block.ints(key)
        return AutomationFlowLayout {
            ForEach(0..<7, id: \.self) { day in
                AutomationChip(title: BoardAutomations.dayShort[day], on: days.contains(day)) {
                    block.set(key, .array(BoardAutomations.toggling(day, in: days).map { .number(Double($0)) }))
                }
            }
        }
    }

    private func dueToggle(_ title: String) -> some View {
        Toggle(title, isOn: Binding(
            get: { block.int("inDays") != nil },
            set: { on in
                block.set("inDays", on ? 1 : .null)
                if !on { block.set("time", "") }
            }))
    }

    private var dueFields: some View {
        HStack(spacing: 10) {
            Picker("Due", selection: int("inDays", 1)) {
                ForEach(BoardAutomations.dueDays, id: \.days) { Text($0.label).tag($0.days) }
            }
            .labelsHidden()
            .fixedSize()
            AutomationOptionalTime(time: str("time"))
        }
    }
}

// MARK: - schedule

private struct AutomationScheduleFields: View {
    @Binding var schedule: AutomationBlock

    var body: some View {
        let mode = BoardAutomations.scheduleMode(schedule)
        VStack(alignment: .leading, spacing: 8) {
            Picker("How often", selection: Binding(get: { mode }, set: { BoardAutomations.applyScheduleMode($0, to: &schedule) })) {
                ForEach(BoardAutomations.scheduleModes, id: \.mode) { Text($0.label).tag($0.mode) }
            }
            .labelsHidden()
            .fixedSize()

            switch mode {
            case "days":
                let days = schedule.ints("days")
                AutomationFlowLayout {
                    ForEach(0..<7, id: \.self) { day in
                        AutomationChip(title: BoardAutomations.dayShort[day], on: days.contains(day)) {
                            schedule.set("days", .array(BoardAutomations.toggling(day, in: days).map { .number(Double($0)) }))
                        }
                    }
                }
            case "monthly":
                Picker("Day of the month", selection: Binding(
                    get: { let day = schedule.int("dayOfMonth") ?? 0; return day == 0 ? -1 : day },
                    set: { schedule.set("dayOfMonth", .from($0)) })) {
                    ForEach(1...31, id: \.self) { Text("on day \($0)").tag($0) }
                    Text("on the last day").tag(-1)
                }
                .labelsHidden()
                .fixedSize()
            case "hourly":
                Picker("Every", selection: Binding(
                    get: { let hours = schedule.int("everyHours") ?? 0; return hours == 0 ? 1 : hours },
                    set: { schedule.set("everyHours", .from($0)) })) {
                    ForEach(BoardAutomations.hourChoices, id: \.self) { Text("every \($0) hour\($0 == 1 ? "" : "s") starting").tag($0) }
                }
                .labelsHidden()
                .fixedSize()
            case "once":
                DatePicker("On", selection: Binding(
                    get: { schedule.string("date").flatMap { OrganizerDates.day($0) } ?? Date() },
                    set: { schedule.set("date", .string(OrganizerDates.ymd($0))) }), displayedComponents: .date)
                    .fixedSize()
            default:
                EmptyView()
            }

            DatePicker("At", selection: Binding(
                get: { automationTime(schedule.string("time")) ?? automationTime("07:00")! },
                set: { schedule.set("time", .string(automationHHmm($0))) }), displayedComponents: .hourAndMinute)
                .fixedSize()
        }
    }
}

// A time of day or none - the dashboard's empty time box.
private struct AutomationOptionalTime: View {
    @Binding var time: String

    var body: some View {
        HStack(spacing: 6) {
            Toggle("at a time", isOn: Binding(get: { !time.isEmpty }, set: { time = $0 ? "09:00" : "" }))
                .fixedSize()
            if !time.isEmpty {
                DatePicker("Time", selection: Binding(
                    get: { automationTime(time) ?? Date() },
                    set: { time = automationHHmm($0) }), displayedComponents: .hourAndMinute)
                    .labelsHidden()
                    .fixedSize()
            }
        }
    }
}

// "07:30" <-> today at 7:30.
private func automationTime(_ hhmm: String?) -> Date? {
    guard let parts = hhmm?.split(separator: ":").compactMap({ Int($0) }), parts.count == 2 else { return nil }
    return Calendar.current.date(bySettingHour: parts[0], minute: parts[1], second: 0, of: Date())
}

private func automationHHmm(_ date: Date) -> String {
    let parts = Calendar.current.dateComponents([.hour, .minute], from: date)
    return String(format: "%02d:%02d", parts.hour ?? 0, parts.minute ?? 0)
}

// A comma- or line-separated list typed as text. Keeps its own text so "eggs, " isn't tidied
// away mid-word; the list itself updates as you type.
private struct AutomationListField: View {
    @Binding var values: [String]
    let placeholder: String
    let lines: Bool
    @State private var text = ""
    @State private var loaded = false

    var body: some View {
        Group {
            if lines {
                TextField(placeholder, text: $text, prompt: Text(placeholder), axis: .vertical).lineLimit(2...8)
            } else {
                TextField(placeholder, text: $text, prompt: Text(placeholder))
            }
        }
        .labelsHidden()
        .multilineTextAlignment(.leading)
        .textFieldStyle(.roundedBorder)
        .onAppear {
            guard !loaded else { return }
            text = values.joined(separator: lines ? "\n" : ", ")
            loaded = true
        }
        .onChange(of: text) { _, new in
            let parsed = new.split(whereSeparator: { lines ? $0.isNewline : $0 == "," })
                .map { $0.trimmingCharacters(in: .whitespaces) }
                .filter { !$0.isEmpty }
            if parsed != values { values = parsed }
        }
    }
}

private struct AutomationChip: View {
    let title: String
    let on: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Text(title)
                .font(.caption)
                .padding(.horizontal, 10)
                .padding(.vertical, 5)
                .background(on ? SentinelTheme.green.opacity(0.28) : Color.primary.opacity(0.07), in: Capsule())
                .overlay(Capsule().stroke(on ? SentinelTheme.green : Color.clear))
                .contentShape(Capsule())
        }
        .buttonStyle(.plain)
    }
}

// Chips that wrap onto as many rows as they need.
private struct AutomationFlowLayout: Layout {
    var spacing: CGFloat = 6

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let rows = arrange(subviews, width: proposal.width ?? .infinity)
        let width = rows.map { $0.width }.max() ?? 0
        let height = rows.map(\.height).reduce(0, +) + spacing * CGFloat(max(0, rows.count - 1))
        return CGSize(width: min(width, proposal.width ?? width), height: height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var y = bounds.minY
        for row in arrange(subviews, width: bounds.width) {
            var x = bounds.minX
            for index in row.indices {
                let size = subviews[index].sizeThatFits(.unspecified)
                subviews[index].place(at: CGPoint(x: x, y: y), proposal: ProposedViewSize(size))
                x += size.width + spacing
            }
            y += row.height + spacing
        }
    }

    private struct Row {
        var indices: [Int] = []
        var width: CGFloat = 0
        var height: CGFloat = 0
    }

    private func arrange(_ subviews: Subviews, width: CGFloat) -> [Row] {
        var rows: [Row] = []
        var row = Row()
        for index in subviews.indices {
            let size = subviews[index].sizeThatFits(.unspecified)
            if !row.indices.isEmpty, row.width + spacing + size.width > width {
                rows.append(row)
                row = Row()
            }
            row.width += (row.indices.isEmpty ? 0 : spacing) + size.width
            row.height = max(row.height, size.height)
            row.indices.append(index)
        }
        if !row.indices.isEmpty { rows.append(row) }
        return rows
    }
}
