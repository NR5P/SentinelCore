import OrganizerKit
import SwiftUI
import UniformTypeIdentifiers

// MARK: - reminders

struct OrganizerRemindersView: View {
    @Environment(OrganizerEngine.self) private var engine
    let spaceFilter: String?

    @State private var newTitle = ""
    @State private var editing: OrganizerItem?
    @State private var creating = false
    @State private var listening = false
    @State private var spokenDraft: VoiceDraft?
    @State private var editingDraft: VoiceDraft?
    @State private var showCompleted = false
    @FocusState private var addFocused: Bool

    var body: some View {
        let groups = grouped()
        List {
            Section {
                HStack {
                    Image(systemName: "plus.circle.fill").foregroundStyle(SentinelTheme.green)
                    TextField("New reminder", text: $newTitle)
                        .focused($addFocused)
                        .onSubmit(add)
                    // Or say it: "add a reminder to walk the dog tomorrow at 1".
                    Button { listening = true } label: {
                        Image(systemName: "mic.fill").foregroundStyle(SentinelTheme.green)
                    }
                    .buttonStyle(.borderless)
                    .accessibilityLabel("Speak a reminder")
                    .help("Speak a reminder")
                    Button("Details") { creating = true }
                        .buttonStyle(.borderless)
                        .font(.caption)
                }
            }
            section("Overdue", groups.overdue, tint: SentinelTheme.red)
            section("Today", groups.today)
            section("Upcoming", groups.upcoming)
            section("No Date", groups.someday)
            if groups.overdue.isEmpty && groups.today.isEmpty && groups.upcoming.isEmpty && groups.someday.isEmpty {
                ContentUnavailableView("Nothing to do", systemImage: "checkmark.circle", description: Text("Add a reminder above."))
            }
            if !groups.done.isEmpty {
                Section {
                    DisclosureGroup("Completed (\(groups.done.count))", isExpanded: $showCompleted) {
                        ForEach(groups.done.prefix(200)) { row($0) }
                    }
                }
            }
        }
        #if os(iOS)
        .listStyle(.insetGrouped)
        #endif
        .sheet(item: $editing) { reminder in
            NavigationStack { ReminderEditor(reminder: reminder, parentId: nil) }
                .environment(engine)
                #if os(macOS)
                .frame(minWidth: 480, minHeight: 600)
                #endif
        }
        .sheet(isPresented: $creating) {
            NavigationStack { ReminderEditor(reminder: nil, parentId: nil) }
                .environment(engine)
                #if os(macOS)
                .frame(minWidth: 480, minHeight: 600)
                #endif
        }
        // "Edit" on the voice sheet hands the draft over here, and the editor opens once that sheet
        // has gone - two sheets can't swap places in one go.
        .sheet(isPresented: $listening, onDismiss: { editingDraft = spokenDraft; spokenDraft = nil }) {
            VoiceReminderSheet(spaceId: spaceFilter) { spokenDraft = $0 }
                .environment(engine)
                #if os(macOS)
                .frame(minWidth: 440, minHeight: 360)
                #endif
        }
        .sheet(item: $editingDraft) { draft in
            NavigationStack { ReminderEditor(reminder: nil, parentId: nil, draft: draft.phrase) }
                .environment(engine)
                #if os(macOS)
                .frame(minWidth: 480, minHeight: 600)
                #endif
        }
    }

    @ViewBuilder
    private func section(_ title: String, _ reminders: [OrganizerItem], tint: Color? = nil) -> some View {
        if !reminders.isEmpty {
            Section {
                ForEach(reminders) { row($0) }
                    .onDelete { offsets in offsets.forEach { engine.deleteTree(reminders[$0].id) } }
            } header: {
                Text(title).foregroundStyle(tint ?? SentinelTheme.cyan)
            }
        }
    }

    private func row(_ reminder: OrganizerItem) -> some View {
        let done = reminder.bool("completed")
        let steps = engine.children(of: reminder.id, kind: .reminder)
        return HStack(alignment: .top, spacing: 12) {
            Button {
                completeReminder(reminder, done: !done, engine: engine)
            } label: {
                Image(systemName: done ? "checkmark.circle.fill" : "circle")
                    .font(.title3)
                    .foregroundStyle(done ? SentinelTheme.green : .secondary)
            }
            .buttonStyle(.borderless)
            .accessibilityLabel(done ? "Mark not done" : "Mark done")

            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 4) {
                    if let priority = reminder.number("priority"), priority > 0 {
                        Text(String(repeating: "!", count: Int(priority))).foregroundStyle(SentinelTheme.amber).bold()
                    }
                    Text(reminder.title.isEmpty ? "Reminder" : reminder.title)
                        .strikethrough(done)
                        .foregroundStyle(done ? .secondary : .primary)
                    if reminder.bool("flagged") { Image(systemName: "flag.fill").foregroundStyle(SentinelTheme.amber).font(.caption) }
                }
                HStack(spacing: 10) {
                    if let label = dueLabel(reminder) {
                        Text(label).foregroundStyle(isOverdue(reminder) ? SentinelTheme.red : .secondary)
                    }
                    if reminder.string("recurrence") != nil { Image(systemName: "repeat") }
                    if !steps.isEmpty {
                        Text("\(steps.filter { $0.bool("completed") }.count)/\(steps.count) steps")
                    }
                    if !engine.children(of: reminder.id, kind: .attachment).isEmpty { Image(systemName: "paperclip") }
                    SpaceTag(spaceId: reminder.spaceId)
                }
                .font(.caption)
                .foregroundStyle(.secondary)
            }
            Spacer(minLength: 0)
        }
        .contentShape(Rectangle())
        .onTapGesture { editing = reminder }
    }

    private func add() {
        let title = newTitle.trimmingCharacters(in: .whitespaces)
        guard !title.isEmpty else { return }
        engine.create(.reminder, fields: ["title": .string(title), "completed": false], spaceId: spaceFilter ?? engine.defaultSpaceId)
        newTitle = ""
        addFocused = true
    }

    private struct Groups { var overdue: [OrganizerItem] = [], today: [OrganizerItem] = [], upcoming: [OrganizerItem] = [], someday: [OrganizerItem] = [], done: [OrganizerItem] = [] }

    private func grouped() -> Groups {
        var groups = Groups()
        let calendar = Calendar.current
        for reminder in engine.items(.reminder) where reminder.parentId == nil && (spaceFilter == nil || reminder.spaceId == spaceFilter) {
            if reminder.bool("completed") { groups.done.append(reminder); continue }
            guard let due = OrganizerDates.parse(reminder.string("due"), allDay: reminder.bool("allDay")) else {
                groups.someday.append(reminder)
                continue
            }
            if isOverdue(reminder) { groups.overdue.append(reminder) }
            else if calendar.isDateInToday(due) { groups.today.append(reminder) }
            else { groups.upcoming.append(reminder) }
        }
        let byDue: (OrganizerItem, OrganizerItem) -> Bool = { a, b in
            let da = OrganizerDates.parse(a.string("due"), allDay: a.bool("allDay")) ?? .distantFuture
            let db = OrganizerDates.parse(b.string("due"), allDay: b.bool("allDay")) ?? .distantFuture
            return da != db ? da < db : (a.number("priority") ?? 0) > (b.number("priority") ?? 0)
        }
        groups.overdue.sort(by: byDue)
        groups.today.sort(by: byDue)
        groups.upcoming.sort(by: byDue)
        groups.someday.sort { $0.sortPosition < $1.sortPosition }
        groups.done.sort { ($0.string("completedAt") ?? "") > ($1.string("completedAt") ?? "") }
        return groups
    }

    private func isOverdue(_ reminder: OrganizerItem) -> Bool {
        guard !reminder.bool("completed"), let due = OrganizerDates.parse(reminder.string("due"), allDay: reminder.bool("allDay")) else { return false }
        return reminder.bool("allDay") ? Calendar.current.startOfDay(for: due) < Calendar.current.startOfDay(for: Date()) : due < Date()
    }

    private func dueLabel(_ reminder: OrganizerItem) -> String? {
        guard let due = OrganizerDates.parse(reminder.string("due"), allDay: reminder.bool("allDay")) else { return nil }
        let calendar = Calendar.current
        let day = calendar.isDateInToday(due) ? "Today" : calendar.isDateInTomorrow(due) ? "Tomorrow" : due.formatted(.dateTime.weekday(.abbreviated).month(.abbreviated).day())
        return reminder.bool("allDay") ? day : "\(day) \(due.formatted(date: .omitted, time: .shortened))"
    }
}

// MARK: - notes

struct OrganizerNotesView: View {
    @Environment(OrganizerEngine.self) private var engine
    let spaceFilter: String?
    @State private var search = ""
    @State private var selection: String?

    var body: some View {
        let notes = engine.items(.note)
            .filter { spaceFilter == nil || $0.spaceId == spaceFilter }
            .filter { !$0.bool("archived") }
            .filter { search.isEmpty || "\($0.title)\n\($0.string("body") ?? "")".localizedCaseInsensitiveContains(search) }
            .sorted { ($0.bool("pinned") ? 1 : 0, $0.updatedAt ?? .distantPast) > ($1.bool("pinned") ? 1 : 0, $1.updatedAt ?? .distantPast) }

        List {
            ForEach(notes) { note in
                NavigationLink {
                    NoteEditorView(noteId: note.id)
                } label: {
                    VStack(alignment: .leading, spacing: 3) {
                        HStack {
                            if note.bool("pinned") { Image(systemName: "pin.fill").foregroundStyle(SentinelTheme.amber).font(.caption) }
                            Text(note.title.isEmpty ? "Untitled" : note.title).font(.headline).lineLimit(1)
                            if !engine.children(of: note.id, kind: .attachment).isEmpty { Image(systemName: "paperclip").font(.caption) }
                        }
                        Text(preview(note)).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                        HStack {
                            SpaceTag(spaceId: note.spaceId)
                            let progress = NoteChecklist.progress(note.string("body") ?? "")
                            if progress.total > 0 {
                                Text("☑ \(progress.done)/\(progress.total)").font(.caption2).foregroundStyle(.secondary)
                            }
                            if let updated = note.updatedAt {
                                Text(updated.formatted(.relative(presentation: .named))).font(.caption2).foregroundStyle(.secondary)
                            }
                        }
                    }
                }
            }
            .onDelete { offsets in offsets.forEach { engine.deleteTree(notes[$0].id) } }
            if notes.isEmpty {
                ContentUnavailableView(search.isEmpty ? "No notes yet" : "No matches", systemImage: "note.text")
            }
            if engine.policy.mode != .full {
                Section {
                    Button("Show notes not kept on this device") {
                        Task { try? await engine.loadArchive(.note) }
                    }
                    .font(.caption)
                }
            }
        }
        .searchable(text: $search, prompt: "Search notes")
        .toolbar {
            ToolbarItem {
                NavigationLink {
                    NewNoteView(spaceId: spaceFilter ?? engine.defaultSpaceId, checklist: true)
                } label: {
                    Label("New Checklist", systemImage: "checklist")
                }
            }
            ToolbarItem {
                NavigationLink {
                    NewNoteView(spaceId: spaceFilter ?? engine.defaultSpaceId)
                } label: {
                    Label("New Note", systemImage: "square.and.pencil")
                }
            }
        }
    }

    private func preview(_ note: OrganizerItem) -> String {
        (note.string("body") ?? "")
            .replacingOccurrences(of: "- [ ] ", with: "☐ ")
            .replacingOccurrences(of: "- [x] ", with: "☑ ")
            .replacingOccurrences(of: "#", with: "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

// Creates the note the moment it's opened, so typing starts straight away and nothing is lost.
// A new checklist opens ready to tick: name it, press Return, then type item after item.
struct NewNoteView: View {
    @Environment(OrganizerEngine.self) private var engine
    let spaceId: String
    var checklist = false
    @State private var noteId: String?

    var body: some View {
        Group {
            if let noteId { NoteEditorView(noteId: noteId, startAsChecklist: checklist) } else { ProgressView() }
        }
        .onAppear {
            if noteId == nil { noteId = engine.create(.note, fields: ["title": "", "body": ""], spaceId: spaceId).id }
        }
    }
}

struct NoteEditorView: View {
    @Environment(OrganizerEngine.self) private var engine
    @Environment(\.dismiss) private var dismiss
    let noteId: String
    var startAsChecklist = false

    @State private var title = ""
    @State private var body_ = ""
    @State private var links: [OrganizerLink] = []
    // The rendered note, which is also where a checklist gets ticked, added to and cleared.
    @State private var preview = false
    @State private var loaded = false
    @State private var saveTask: Task<Void, Never>?
    @State private var confirmingDelete = false
    @State private var newItem = ""
    @FocusState private var bodyFocused: Bool
    @FocusState private var titleFocused: Bool
    @FocusState private var addItemFocused: Bool

    var body: some View {
        let note = engine.item(noteId)
        let progress = NoteChecklist.progress(body_)
        Form {
            Section {
                TextField("Title", text: $title)
                    .font(.title2.bold())
                    .focused($titleFocused)
                    .onChange(of: title) { queueSave() }
                    // On a checklist Return goes from the name straight to the first item.
                    .onSubmit { if preview { addItemFocused = true } }
                if preview {
                    if !body_.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                        MarkdownNoteView(source: body_) { line in
                            edit { NoteChecklist.toggle($0, line: line) }
                        } remove: { line in
                            edit { NoteChecklist.removeLine($0, index: line) }
                        }
                        .frame(minHeight: progress.total > 0 ? nil : 200, alignment: .topLeading)
                    }
                    // Return adds the item and stays here, so a list goes in one line after another.
                    HStack {
                        Image(systemName: "plus.circle.fill").foregroundStyle(SentinelTheme.green)
                        TextField("Add item", text: $newItem, prompt: Text("Add item"))
                            .labelsHidden()
                            .multilineTextAlignment(.leading)
                            .focused($addItemFocused)
                            .onSubmit(addItem)
                    }
                    if progress.done > 0 {
                        Button("Clear Checked (\(progress.done))", systemImage: "trash") {
                            edit(NoteChecklist.clearChecked)
                        }
                    }
                } else {
                    TextEditor(text: $body_)
                        .focused($bodyFocused)
                        .frame(minHeight: 280)
                        .font(.body)
                        .onChange(of: body_) { queueSave() }
                }
            } footer: {
                Text("Markdown: # heading, **bold**. Checklist turns lines into tick boxes.")
            }
            if let note {
                Section {
                    SpacePicker(spaceId: Binding(get: { note.spaceId }, set: { engine.update(noteId, ["spaceId": .string($0)]) }))
                    Toggle("Pinned", isOn: Binding(get: { note.bool("pinned") }, set: { engine.update(noteId, ["pinned": .bool($0)]) }))
                    Toggle("Keep on this device", isOn: Binding(get: { engine.keptOffline.contains(noteId) }, set: { engine.setKeptOffline(noteId, $0) }))
                } footer: {
                    if let updatedBy = note.updatedBy, let updated = note.updatedAt {
                        Text("Last edited by \(updatedBy), \(updated.formatted(.relative(presentation: .named)))")
                    }
                }
            }
            LinksSection(links: Binding(get: { links }, set: { links = $0; saveNow() }))
            AttachmentsSection(parentId: noteId) { noteId }
            Section {
                Button("Delete Note", role: .destructive) { confirmingDelete = true }
            }
        }
        .formStyle(.grouped)
        .navigationTitle(title.isEmpty ? "Note" : title)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            if !preview {
                ToolbarItem {
                    Button("Checklist", systemImage: "checklist", action: makeChecklist)
                        .help("Turn each line into a tick box")
                }
            }
            ToolbarItem {
                Button(preview ? "Edit" : "Preview", systemImage: preview ? "pencil" : "eye") {
                    saveNow()
                    preview.toggle()
                }
            }
        }
        .confirmationDialog("Delete this note?", isPresented: $confirmingDelete, titleVisibility: .visible) {
            Button("Delete", role: .destructive) {
                saveTask?.cancel()
                engine.deleteTree(noteId)
                dismiss()
            }
        }
        .onAppear(perform: load)
        .onDisappear(perform: saveNow)
        // Another device changed this note: take its text unless this person is typing.
        .onChange(of: note?.fields["body"]) { _, _ in refreshFromRemote() }
        .onChange(of: note?.fields["title"]) { _, _ in refreshFromRemote() }
    }

    private func load() {
        guard !loaded, let note = engine.item(noteId) else { return }
        loaded = true
        title = note.title
        body_ = note.string("body") ?? ""
        links = OrganizerLink.from(note.fields["links"])
        // A note with a checklist opens ready to tick; anything else opens to write in.
        preview = startAsChecklist || NoteChecklist.progress(body_).total > 0
        if note.title.isEmpty && body_.isEmpty { titleFocused = true }
    }

    // A tick, delete or clear on the rendered note is saved straight away, like any other tap.
    private func edit(_ change: (String) -> String) {
        body_ = change(body_)
        saveNow()
    }

    private func addItem() {
        edit { NoteChecklist.addItem($0, text: newItem) }
        newItem = ""
        addItemFocused = true
    }

    private func makeChecklist() {
        edit(NoteChecklist.makeChecklist)
        preview = true
        // The Add item field only exists once the rendered note is on screen.
        Task { addItemFocused = true }
    }

    private func refreshFromRemote() {
        guard saveTask == nil, let note = engine.item(noteId) else { return }
        if !titleFocused, note.title != title { title = note.title }
        if !bodyFocused, (note.string("body") ?? "") != body_ { body_ = note.string("body") ?? "" }
    }

    private func queueSave() {
        guard loaded else { return }
        saveTask?.cancel()
        saveTask = Task {
            try? await Task.sleep(for: .milliseconds(600))
            guard !Task.isCancelled else { return }
            saveNow()
        }
    }

    private func saveNow() {
        saveTask?.cancel()
        saveTask = nil
        guard loaded, let note = engine.item(noteId) else { return }
        var changes: [String: JSONValue] = [:]
        if note.title != title { changes["title"] = .string(title) }
        if (note.string("body") ?? "") != body_ { changes["body"] = .string(body_) }
        let linksJSON = OrganizerLink.json(links)
        if (note.fields["links"] ?? .null) != linksJSON { changes["links"] = linksJSON }
        if !changes.isEmpty { engine.update(noteId, changes) }
    }
}

// MARK: - boards

struct OrganizerBoardsView: View {
    @Environment(OrganizerEngine.self) private var engine
    let spaceFilter: String?

    @AppStorage("organizerBoard") private var selectedBoardId = ""
    @State private var editingCard: OrganizerItem?
    @State private var naming: NamingTarget?
    @State private var nameText = ""
    @State private var confirmingDelete = false
    @State private var targetedBoardId: String?

    private enum NamingTarget: Identifiable, Equatable {
        case newBoard, renameBoard(String), newColumn(String), renameColumn(String)
        var id: String { String(describing: self) }
    }

    var body: some View {
        let boards = engine.items(.board)
            .filter { spaceFilter == nil || $0.spaceId == spaceFilter }
            .filter { !$0.bool("archived") }
            .sorted { $0.sortPosition < $1.sortPosition }
        let board = boards.first { $0.id == selectedBoardId } ?? boards.first

        VStack(alignment: .leading, spacing: 10) {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack {
                    ForEach(boards) { item in
                        Button {
                            selectedBoardId = item.id
                        } label: {
                            HStack(spacing: 6) {
                                Circle().fill(engine.color(forSpace: item.spaceId)).frame(width: 8, height: 8)
                                Text(item.title.isEmpty ? "Board" : item.title)
                            }
                            .padding(.horizontal, 10)
                            .padding(.vertical, 5)
                            .background(chipFill(item, selected: item.id == board?.id), in: Capsule())
                        }
                        .buttonStyle(.plain)
                        // Hovering a dragged card over a board opens it, so the card can go into
                        // any of its columns; letting go on the chip itself files it in the first.
                        .springLoadingBehavior(.enabled)
                        .dropDestination(for: CardDrag.self) { drags, _ in
                            guard let drag = drags.first,
                                  let column = engine.children(of: item.id, kind: .column).min(by: { $0.sortPosition < $1.sortPosition })
                            else { return false }
                            engine.moveCardToEnd(drag.id, of: column)
                            return true
                        } isTargeted: { targetedBoardId = $0 ? item.id : (targetedBoardId == item.id ? nil : targetedBoardId) }
                    }
                    Button("New Board", systemImage: "plus") { nameText = ""; naming = .newBoard }
                        .buttonStyle(.bordered)
                }
                .padding(.horizontal)
            }

            if let board {
                HStack {
                    Text(board.title).font(.title3.bold())
                    SpaceTag(spaceId: board.spaceId)
                    Spacer()
                    Menu {
                        Button("Rename", systemImage: "pencil") { nameText = board.title; naming = .renameBoard(board.id) }
                        Menu("Share With") {
                            ForEach(engine.visibleSpaces) { space in
                                Button(space.name) { engine.update(board.id, ["spaceId": .string(space.id)]) }
                            }
                        }
                        Button("Add Column", systemImage: "rectangle.stack.badge.plus") { nameText = ""; naming = .newColumn(board.id) }
                        Button("Delete Board", systemImage: "trash", role: .destructive) { confirmingDelete = true }
                    } label: {
                        Image(systemName: "ellipsis.circle")
                    }
                }
                .padding(.horizontal)

                ScrollView(.horizontal) {
                    HStack(alignment: .top, spacing: 12) {
                        let columns = engine.children(of: board.id, kind: .column).sorted { $0.sortPosition < $1.sortPosition }
                        ForEach(columns) { column in
                            ColumnView(column: column, columns: columns, boards: boards, editCard: { editingCard = $0 }, rename: {
                                nameText = column.title
                                naming = .renameColumn(column.id)
                            })
                        }
                        Button {
                            nameText = ""
                            naming = .newColumn(board.id)
                        } label: {
                            Label("Add Column", systemImage: "plus").frame(width: 200, height: 60)
                        }
                        .buttonStyle(.bordered)
                    }
                    .padding(.horizontal)
                    .padding(.bottom)
                }
                .confirmationDialog("Delete \"\(board.title)\" and every card on it?", isPresented: $confirmingDelete, titleVisibility: .visible) {
                    Button("Delete Board", role: .destructive) { engine.deleteTree(board.id) }
                }
            } else {
                ContentUnavailableView("No boards yet", systemImage: "rectangle.split.3x1",
                                       description: Text("Make one to track anything in columns - chores, projects, a move."))
                Spacer(minLength: 0)
            }
        }
        .padding(.top, 8)
        .sheet(item: $editingCard) { card in
            NavigationStack { CardEditor(card: card) }
                .environment(engine)
                #if os(macOS)
                .frame(minWidth: 480, minHeight: 620)
                #endif
        }
        .alert(namingTitle, isPresented: Binding(get: { naming != nil }, set: { if !$0 { naming = nil } })) {
            TextField("Name", text: $nameText)
            Button("Cancel", role: .cancel) { naming = nil }
            Button("Save") { commitName() }
        }
    }

    private func chipFill(_ item: OrganizerItem, selected: Bool) -> Color {
        if item.id == targetedBoardId { return SentinelTheme.green.opacity(0.35) }
        return selected ? SentinelTheme.green.opacity(0.18) : Color.primary.opacity(0.06)
    }

    private var namingTitle: String {
        switch naming {
        case .newBoard: "New Board"
        case .renameBoard: "Rename Board"
        case .newColumn: "New Column"
        case .renameColumn: "Rename Column"
        case nil: ""
        }
    }

    private func commitName() {
        let name = nameText.trimmingCharacters(in: .whitespaces)
        defer { naming = nil }
        guard !name.isEmpty else { return }
        switch naming {
        case .newBoard:
            let board = engine.create(.board, fields: ["title": .string(name), "position": .number(Date().timeIntervalSince1970)], spaceId: spaceFilter ?? engine.defaultSpaceId)
            for (index, column) in ["To do", "Doing", "Done"].enumerated() {
                engine.create(.column, fields: ["title": .string(column), "position": .number(Double(index + 1))], parentId: board.id)
            }
            selectedBoardId = board.id
        case let .renameBoard(id), let .renameColumn(id):
            engine.update(id, ["title": .string(name)])
        case let .newColumn(boardId):
            let last = engine.children(of: boardId, kind: .column).map(\.sortPosition).filter { $0 < 1e9 }.max() ?? 0
            engine.create(.column, fields: ["title": .string(name), "position": .number(last + 1)], parentId: boardId)
        case nil:
            break
        }
    }
}

// What a dragged card carries. Its own type rather than a plain string, so a text field under the
// drop point ("Add a card") can't take it and type the card's id instead of moving the card.
struct CardDrag: Codable, Transferable {
    let id: String

    static var transferRepresentation: some TransferRepresentation {
        CodableRepresentation(contentType: .organizerCard)
    }
}

extension UTType {
    // Declared in Info.plist (UTExportedTypeDeclarations).
    static let organizerCard = UTType(exportedAs: "com.sentinelcore.client.organizer-card")
}

extension OrganizerEngine {
    // Puts a card into a column at a position - the column may be on another board, even one in
    // another space. The server would move the card's comments and attachments into the new space
    // anyway; sending the space too lets this device do the same straight away.
    func moveCard(_ cardId: String, to column: OrganizerItem, position: Double) {
        guard let card = item(cardId), card.kind == .card else { return }
        var changes: [String: JSONValue] = ["position": .number(position)]
        if card.parentId != column.id { changes["parentId"] = .string(column.id) }
        if card.spaceId != column.spaceId { changes["spaceId"] = .string(column.spaceId) }
        update(cardId, changes)
    }

    func moveCardToEnd(_ cardId: String, of column: OrganizerItem) {
        let last = children(of: column.id, kind: .card).filter { $0.id != cardId }.map(\.sortPosition).filter { $0 < 1e9 }.max() ?? 0
        moveCard(cardId, to: column, position: last + 1)
    }
}

private struct ColumnView: View {
    @Environment(OrganizerEngine.self) private var engine
    let column: OrganizerItem
    let columns: [OrganizerItem]
    let boards: [OrganizerItem]
    var editCard: (OrganizerItem) -> Void
    var rename: () -> Void

    @State private var newCard = ""
    @State private var targeted = false

    var body: some View {
        let cards = engine.children(of: column.id, kind: .card).sorted { $0.sortPosition < $1.sortPosition }
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text(column.title).font(.headline).foregroundStyle(SentinelTheme.cyan)
                Text("\(cards.count)").font(.caption).foregroundStyle(.secondary)
                Spacer()
                Menu {
                    Button("Rename", systemImage: "pencil", action: rename)
                    if let index = columns.firstIndex(of: column) {
                        if index > 0 { Button("Move Left", systemImage: "arrow.left") { move(by: -1) } }
                        if index < columns.count - 1 { Button("Move Right", systemImage: "arrow.right") { move(by: 1) } }
                    }
                    Button("Delete Column", systemImage: "trash", role: .destructive) { engine.deleteTree(column.id) }
                } label: {
                    Image(systemName: "ellipsis").frame(width: 24, height: 24)
                }
                .menuStyle(.borderlessButton)
                .fixedSize()
            }
            // The column runs the height of the board, so the whole of it - not just the space the
            // cards happen to take - is somewhere to drop a card; long columns scroll on their own.
            ScrollView(.vertical) {
                VStack(alignment: .leading, spacing: 8) {
                    ForEach(cards) { card in
                        cardTile(card, in: cards)
                    }
                    TextField("Add a card", text: $newCard)
                        .textFieldStyle(.roundedBorder)
                        .onSubmit {
                            let title = newCard.trimmingCharacters(in: .whitespaces)
                            guard !title.isEmpty else { return }
                            let last = cards.map(\.sortPosition).filter { $0 < 1e9 }.max() ?? 0
                            engine.create(.card, fields: ["title": .string(title), "position": .number(last + 1)], parentId: column.id)
                            newCard = ""
                        }
                }
            }
            .scrollBounceBehavior(.basedOnSize)
        }
        .padding(10)
        .frame(width: 270, alignment: .topLeading)
        .frame(maxHeight: .infinity, alignment: .top)
        .background(targeted ? SentinelTheme.green.opacity(0.1) : Color.primary.opacity(0.04), in: RoundedRectangle(cornerRadius: 10))
        .dropDestination(for: CardDrag.self) { drags, _ in
            guard let drag = drags.first else { return false }
            engine.moveCardToEnd(drag.id, of: column)
            return true
        } isTargeted: { targeted = $0 }
    }

    private func cardTile(_ card: OrganizerItem, in cards: [OrganizerItem]) -> some View {
        CardTile(card: card)
            .onTapGesture { editCard(card) }
            // A tap gesture alone is invisible to VoiceOver; this makes the tile a button.
            .accessibilityElement(children: .combine)
            .accessibilityAddTraits(.isButton)
            .accessibilityAction { editCard(card) }
            .draggable(CardDrag(id: card.id)) {
                CardTile(card: card).frame(width: 250).environment(engine)
            }
            .dropDestination(for: CardDrag.self) { drags, _ in
                guard let drag = drags.first else { return false }
                place(drag.id, before: card, in: cards)
                return true
            }
            .contextMenu {
                Menu("Move To", systemImage: "arrow.right") {
                    ForEach(columns.filter { $0.id != column.id }) { other in
                        Button(other.title) { engine.moveCardToEnd(card.id, of: other) }
                    }
                }
                let otherBoards = boards.filter { $0.id != column.parentId }
                if !otherBoards.isEmpty {
                    Menu("Move to Board", systemImage: "rectangle.split.3x1") {
                        ForEach(otherBoards) { board in
                            Menu(board.title.isEmpty ? "Board" : board.title) {
                                ForEach(engine.children(of: board.id, kind: .column).sorted { $0.sortPosition < $1.sortPosition }) { target in
                                    Button(target.title) { engine.moveCardToEnd(card.id, of: target) }
                                }
                            }
                        }
                    }
                }
                Button(card.bool("completed") ? "Mark Not Done" : "Mark Done") {
                    engine.update(card.id, ["completed": .bool(!card.bool("completed"))])
                }
                Button("Delete", systemImage: "trash", role: .destructive) { engine.deleteTree(card.id) }
            }
    }

    private func move(by offset: Int) {
        var ordered = columns
        guard let index = ordered.firstIndex(of: column) else { return }
        let target = index + offset
        guard ordered.indices.contains(target) else { return }
        ordered.swapAt(index, target)
        for (position, item) in ordered.enumerated() where item.number("position") != Double(position + 1) {
            engine.update(item.id, ["position": .number(Double(position + 1))])
        }
    }

    // Only the moved card is written: it takes a position between its new neighbours.
    private func place(_ cardId: String, before target: OrganizerItem, in cards: [OrganizerItem]) {
        let others = cards.filter { $0.id != cardId }
        guard cardId != target.id, let index = others.firstIndex(of: target) else { return }
        let previous = index > 0 ? others[index - 1].sortPosition : target.sortPosition - 1
        engine.moveCard(cardId, to: column, position: (previous + target.sortPosition) / 2)
    }
}

private struct CardTile: View {
    @Environment(OrganizerEngine.self) private var engine
    let card: OrganizerItem

    var body: some View {
        let attachments = engine.children(of: card.id, kind: .attachment)
        let cover = attachments.first { $0.string("contentType")?.hasPrefix("image/") == true }
        let checklist = card.fields["checklist"]?.array ?? []
        let done = checklist.filter { $0.object?["done"]?.bool == true }.count
        VStack(alignment: .leading, spacing: 6) {
            if let hash = cover?.string("hash") {
                OrganizerImage(hash: hash)
                    .frame(height: 110)
                    .frame(maxWidth: .infinity)
                    .clipShape(RoundedRectangle(cornerRadius: 6))
            }
            let labels = (card.fields["labels"]?.array ?? []).compactMap { $0.object?["name"]?.string }
            if !labels.isEmpty {
                HStack(spacing: 4) {
                    ForEach(labels, id: \.self) { label in
                        Text(label)
                            .font(.caption2.bold())
                            .padding(.horizontal, 6)
                            .padding(.vertical, 2)
                            .background(OrganizerPalette.color(forLabel: label).opacity(0.3), in: Capsule())
                    }
                }
            }
            Text(card.title.isEmpty ? "Card" : card.title)
                .strikethrough(card.bool("completed"))
                .foregroundStyle(card.bool("completed") ? .secondary : .primary)
            HStack(spacing: 10) {
                if let dueText = card.string("due"), let due = OrganizerDates.parse(dueText, allDay: dueText.count == 10) {
                    Label(due.formatted(.dateTime.month(.abbreviated).day()), systemImage: "clock")
                        .foregroundStyle(!card.bool("completed") && due < Calendar.current.startOfDay(for: Date()) ? SentinelTheme.red : .secondary)
                }
                if !checklist.isEmpty { Label("\(done)/\(checklist.count)", systemImage: "checklist") }
                if !attachments.isEmpty { Label("\(attachments.count)", systemImage: "paperclip") }
                let comments = engine.children(of: card.id, kind: .comment).count
                if comments > 0 { Label("\(comments)", systemImage: "bubble.left") }
                if card.string("description") != nil { Image(systemName: "text.alignleft") }
                ForEach((card.fields["assignees"]?.array ?? []).compactMap(\.string), id: \.self) { person in
                    Text(person.prefix(2).uppercased())
                        .font(.system(size: 9, weight: .bold))
                        .frame(width: 20, height: 20)
                        .overlay { Circle().stroke(SentinelTheme.green) }
                }
            }
            .font(.caption)
            .foregroundStyle(.secondary)
            .labelStyle(.titleAndIcon)
        }
        .padding(10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.background, in: RoundedRectangle(cornerRadius: 8))
        .overlay { RoundedRectangle(cornerRadius: 8).stroke(.quaternary) }
        .contentShape(RoundedRectangle(cornerRadius: 8))
    }
}
