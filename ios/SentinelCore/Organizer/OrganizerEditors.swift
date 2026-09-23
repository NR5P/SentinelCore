import OrganizerKit
import SwiftUI

// Editors for events, reminders and board cards. Each keeps its own draft and writes only the
// fields that changed when saved - so saving an event doesn't stamp a fresh clock on every field
// and quietly win against another device's edit to a field this person never touched.

enum EventEditorTarget: Identifiable {
    case new(day: Date)
    case existing(Occurrence)

    var id: String {
        switch self {
        case let .new(day): "new-\(day.timeIntervalSince1970)"
        case let .existing(occurrence): "\(occurrence.item.id)-\(occurrence.start.timeIntervalSince1970)"
        }
    }
}

private func changedFields(_ before: [String: JSONValue], _ after: [String: JSONValue]) -> [String: JSONValue] {
    after.filter { name, value in (before[name] ?? .null) != value }
}

struct EventEditor: View {
    @Environment(OrganizerEngine.self) private var engine
    @Environment(\.dismiss) private var dismiss
    let target: EventEditorTarget

    @State private var saved: OrganizerItem?
    @State private var title = ""
    @State private var allDay = false
    @State private var start = Date()
    @State private var end = Date()
    @State private var repeatChoice = RepeatChoice.never
    @State private var hasUntil = false
    @State private var until = Date()
    @State private var alert = AlertChoice.none
    @State private var location = ""
    @State private var notes = ""
    @State private var spaceId = ""
    @State private var links: [OrganizerLink] = []
    @State private var loaded = false
    @State private var askingScope: ScopeAction?
    @State private var confirmingSeriesDelete = false

    private enum ScopeAction: Identifiable { case save, delete; var id: Self { self } }

    // Opened from one occurrence of a repeating event: edits can apply to that one or the series.
    private var occurrence: Occurrence? {
        if case let .existing(occurrence) = target, occurrence.originalStart != nil { return occurrence }
        return nil
    }

    var body: some View {
        Form {
            Section {
                TextField("Title", text: $title)
                    .font(.title3)
                TextField("Location", text: $location)
            }
            Section {
                Toggle("All day", isOn: $allDay)
                DatePicker("Starts", selection: startBinding, displayedComponents: allDay ? [.date] : [.date, .hourAndMinute])
                DatePicker("Ends", selection: $end, in: start..., displayedComponents: allDay ? [.date] : [.date, .hourAndMinute])
                Picker("Repeat", selection: $repeatChoice) {
                    ForEach(RepeatChoice.allCases.filter { $0 != .custom || repeatChoice == .custom }) { Text($0.label).tag($0) }
                }
                if repeatChoice != .never && repeatChoice != .custom {
                    Toggle("End repeat", isOn: $hasUntil)
                    if hasUntil { DatePicker("On", selection: $until, in: start..., displayedComponents: .date) }
                }
                Picker("Alert", selection: $alert) {
                    ForEach(AlertChoice.allCases) { Text($0.label).tag($0) }
                }
            }
            Section {
                if occurrence == nil { SpacePicker(spaceId: $spaceId) }
                TextField("Notes", text: $notes, axis: .vertical).lineLimit(3...10)
            }
            LinksSection(links: $links)
            AttachmentsSection(parentId: saved?.id) { ensureSaved() }
            if saved != nil {
                Section {
                    Button("Delete Event", role: .destructive) {
                        if occurrence != nil { askingScope = .delete }
                        else if saved?.string("recurrence") != nil { confirmingSeriesDelete = true }
                        else { deleteSeries(); dismiss() }
                    }
                }
            }
        }
        .formStyle(.grouped)
        .navigationTitle(saved == nil ? "New Event" : "Event")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
            ToolbarItem(placement: .confirmationAction) {
                Button("Save") { occurrence != nil ? (askingScope = .save) : save(scope: nil) }
            }
        }
        .confirmationDialog(askingScope == .delete ? "Delete which events?" : "Change which events?",
                            isPresented: Binding(get: { askingScope != nil }, set: { if !$0 { askingScope = nil } }),
                            titleVisibility: .visible, presenting: askingScope) { action in
            Button("Only This Event") { perform(action, onlyThis: true) }
            Button("All Events") { perform(action, onlyThis: false) }
        }
        .confirmationDialog("Delete every occurrence of this repeating event?", isPresented: $confirmingSeriesDelete, titleVisibility: .visible) {
            Button("Delete All", role: .destructive) { deleteSeries(); dismiss() }
        }
        .onAppear(perform: load)
    }

    // Moving the start drags the end along, keeping the length - through the binding rather than
    // onChange, which would also fire when load() fills the form in.
    private var startBinding: Binding<Date> {
        Binding(get: { start }, set: { newValue in
            end = end.addingTimeInterval(newValue.timeIntervalSince(start))
            start = newValue
        })
    }

    private func load() {
        guard !loaded else { return }
        loaded = true
        switch target {
        case let .new(day):
            let calendar = Calendar.current
            let hour = calendar.component(.hour, from: Date()) + 1
            start = calendar.date(bySettingHour: min(hour, 23), minute: 0, second: 0, of: day) ?? day
            end = start.addingTimeInterval(3600)
            spaceId = engine.defaultSpaceId
        case let .existing(occurrence):
            let item = occurrence.item
            saved = item
            title = item.title
            allDay = item.bool("allDay")
            start = occurrence.start
            // All-day ends are exclusive on the wire; the form shows the last day.
            end = allDay ? occurrence.end.addingTimeInterval(-86_400) : occurrence.end
            repeatChoice = RepeatChoice(item.string("recurrence"))
            if let rule = RecurrenceRule(item.string("recurrence")), let date = rule.until {
                hasUntil = true
                until = date
            }
            alert = AlertChoice(item.fields["alerts"])
            location = item.string("location") ?? ""
            notes = item.string("notes") ?? ""
            spaceId = item.spaceId
            links = OrganizerLink.from(item.fields["links"])
        }
    }

    private var fields: [String: JSONValue] {
        let calendar = Calendar.current
        let endValue: Date = allDay
            ? calendar.date(byAdding: .day, value: 1, to: calendar.startOfDay(for: max(end, start)))!
            : (end > start ? end : start.addingTimeInterval(3600))
        return [
            "title": .string(title.trimmingCharacters(in: .whitespaces).isEmpty ? "Untitled" : title.trimmingCharacters(in: .whitespaces)),
            "allDay": .bool(allDay),
            "start": .string(OrganizerDates.fieldValue(allDay ? calendar.startOfDay(for: start) : start, allDay: allDay)),
            "end": .string(OrganizerDates.fieldValue(endValue, allDay: allDay)),
            "timeZone": .string(TimeZone.current.identifier),
            "recurrence": repeatChoice.rule(until: hasUntil ? until : nil, keeping: saved?.string("recurrence")),
            "alerts": alert.json,
            "location": location.isEmpty ? .null : .string(location),
            "notes": notes.isEmpty ? .null : .string(notes),
            "links": OrganizerLink.json(links),
        ]
    }

    @discardableResult
    private func ensureSaved() -> String {
        if let saved { return saved.id }
        let item = engine.create(.event, fields: fields, spaceId: spaceId)
        saved = item
        return item.id
    }

    private func perform(_ action: ScopeAction, onlyThis: Bool) {
        switch action {
        case .save: save(scope: onlyThis)
        case .delete:
            if onlyThis, let occurrence, let original = occurrence.originalStart {
                excludeFromSeries(original)
            } else {
                deleteSeries()
            }
            dismiss()
        }
    }

    private func save(scope onlyThis: Bool?) {
        defer { dismiss() }
        guard let item = saved.flatMap({ engine.item($0.id) }) else {
            ensureSaved()
            return
        }
        if onlyThis == true, let occurrence, let original = occurrence.originalStart {
            // One occurrence: take it out of the series and give it a life of its own.
            excludeFromSeries(original)
            var detached = fields
            detached["recurrence"] = .null
            detached["seriesId"] = .string(item.id)
            detached["originalStart"] = .string(original)
            engine.create(.event, fields: detached, spaceId: item.spaceId)
            return
        }
        var changes = changedFields(item.fields, fields)
        if onlyThis == false, let occurrence {
            // Every occurrence: keep the series' own first date and move only the time of day and
            // length, rather than dragging the whole series to the day that was tapped.
            let calendar = Calendar.current
            if let seriesStart = OrganizerDates.parse(item.string("start"), allDay: item.bool("allDay")) {
                let shift = start.timeIntervalSince(occurrence.start)
                let newStart = seriesStart.addingTimeInterval(shift)
                let length = (allDay ? calendar.date(byAdding: .day, value: 1, to: calendar.startOfDay(for: end))! : end).timeIntervalSince(start)
                changes["start"] = .string(OrganizerDates.fieldValue(newStart, allDay: allDay))
                changes["end"] = .string(OrganizerDates.fieldValue(newStart.addingTimeInterval(length), allDay: allDay))
                changes = changedFields(item.fields, item.fields.merging(changes) { $1 })
            }
        }
        if spaceId != item.spaceId, occurrence == nil { changes["spaceId"] = .string(spaceId) }
        if !changes.isEmpty { engine.update(item.id, changes) }
    }

    private func excludeFromSeries(_ original: String) {
        guard let item = saved else { return }
        let existing = engine.item(item.id)?.fields["exdates"]?.array ?? []
        engine.update(item.id, ["exdates": .array(existing + [.string(original)])])
    }

    private func deleteSeries() {
        guard let item = saved else { return }
        engine.deleteTree(item.id)
        for detached in engine.items(.event) where detached.string("seriesId") == item.id {
            engine.deleteTree(detached.id)
        }
    }
}

// MARK: - reminders

struct ReminderEditor: View {
    @Environment(OrganizerEngine.self) private var engine
    @Environment(\.dismiss) private var dismiss
    let reminder: OrganizerItem?
    let parentId: String?

    @State private var saved: OrganizerItem?
    @State private var title = ""
    @State private var hasDue = false
    @State private var due = Date()
    @State private var hasTime = false
    @State private var repeatChoice = RepeatChoice.never
    @State private var priority = 0
    @State private var alert = AlertChoice.none
    @State private var flagged = false
    @State private var notes = ""
    @State private var spaceId = ""
    @State private var links: [OrganizerLink] = []
    @State private var newStep = ""
    @State private var loaded = false

    var body: some View {
        Form {
            Section {
                TextField("Title", text: $title).font(.title3)
                TextField("Notes", text: $notes, axis: .vertical).lineLimit(2...8)
            }
            Section {
                Toggle("Date", isOn: $hasDue.animation())
                if hasDue {
                    DatePicker("Due", selection: $due, displayedComponents: hasTime ? [.date, .hourAndMinute] : [.date])
                    Toggle("Time", isOn: $hasTime)
                    Picker("Repeat", selection: $repeatChoice) {
                        ForEach(RepeatChoice.allCases.filter { $0 != .custom || repeatChoice == .custom }) { Text($0.label).tag($0) }
                    }
                    Picker("Alert", selection: $alert) {
                        ForEach(AlertChoice.allCases) { Text($0 == .none ? "At due time" : $0.label).tag($0) }
                    }
                }
                Picker("Priority", selection: $priority) {
                    Text("None").tag(0)
                    Text("Low").tag(1)
                    Text("Medium").tag(2)
                    Text("High").tag(3)
                }
                Toggle("Flagged", isOn: $flagged)
                if parentId == nil && reminder?.parentId == nil { SpacePicker(spaceId: $spaceId) }
            }
            if let saved, saved.parentId == nil {
                Section("Steps") {
                    ForEach(engine.children(of: saved.id, kind: .reminder).sorted { $0.sortPosition < $1.sortPosition }) { step in
                        HStack {
                            Button {
                                engine.update(step.id, ["completed": .bool(!step.bool("completed"))])
                            } label: {
                                Image(systemName: step.bool("completed") ? "checkmark.circle.fill" : "circle")
                                    .foregroundStyle(step.bool("completed") ? SentinelTheme.green : .secondary)
                            }
                            .buttonStyle(.borderless)
                            Text(step.title).strikethrough(step.bool("completed"))
                        }
                    }
                    .onDelete { offsets in
                        let steps = engine.children(of: saved.id, kind: .reminder).sorted { $0.sortPosition < $1.sortPosition }
                        for index in offsets { engine.delete(steps[index].id) }
                    }
                    TextField("Add a step", text: $newStep)
                        .onSubmit {
                            let text = newStep.trimmingCharacters(in: .whitespaces)
                            guard !text.isEmpty else { return }
                            engine.create(.reminder, fields: ["title": .string(text), "completed": false, "position": .number(Date().timeIntervalSince1970)], parentId: saved.id)
                            newStep = ""
                        }
                }
            }
            LinksSection(links: $links)
            AttachmentsSection(parentId: saved?.id) { ensureSaved() }
            if let saved {
                Section {
                    Button("Delete Reminder", role: .destructive) {
                        engine.deleteTree(saved.id)
                        dismiss()
                    }
                }
            }
        }
        .formStyle(.grouped)
        .navigationTitle(saved == nil ? "New Reminder" : "Reminder")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
            ToolbarItem(placement: .confirmationAction) { Button("Save") { save(); dismiss() } }
        }
        .onAppear(perform: load)
    }

    private func load() {
        guard !loaded else { return }
        loaded = true
        spaceId = engine.defaultSpaceId
        guard let reminder else { return }
        saved = reminder
        title = reminder.title
        let allDay = reminder.bool("allDay")
        if let date = OrganizerDates.parse(reminder.string("due"), allDay: allDay) {
            hasDue = true
            due = date
            hasTime = !allDay
        }
        repeatChoice = RepeatChoice(reminder.string("recurrence"))
        priority = reminder.number("priority").map(Int.init) ?? 0
        alert = AlertChoice(reminder.fields["alerts"])
        flagged = reminder.bool("flagged")
        notes = reminder.string("notes") ?? ""
        spaceId = reminder.spaceId
        links = OrganizerLink.from(reminder.fields["links"])
    }

    private var fields: [String: JSONValue] {
        [
            "title": .string(title.trimmingCharacters(in: .whitespaces).isEmpty ? "Reminder" : title.trimmingCharacters(in: .whitespaces)),
            "due": hasDue ? .string(OrganizerDates.fieldValue(due, allDay: !hasTime)) : .null,
            "allDay": hasDue ? .bool(!hasTime) : .null,
            "recurrence": hasDue ? repeatChoice.rule(until: nil, keeping: saved?.string("recurrence")) : .null,
            "priority": .number(Double(priority)),
            "alerts": hasDue ? alert.json : .null,
            "flagged": .bool(flagged),
            "notes": notes.isEmpty ? .null : .string(notes),
            "links": OrganizerLink.json(links),
        ]
    }

    @discardableResult
    private func ensureSaved() -> String {
        if let saved { return saved.id }
        var initial = fields
        initial["completed"] = false
        let item = parentId.map { engine.create(.reminder, fields: initial, parentId: $0) }
            ?? engine.create(.reminder, fields: initial, spaceId: spaceId)
        saved = item
        return item.id
    }

    private func save() {
        guard let item = saved.flatMap({ engine.item($0.id) }) else {
            ensureSaved()
            return
        }
        var changes = changedFields(item.fields, fields)
        if item.parentId == nil, spaceId != item.spaceId { changes["spaceId"] = .string(spaceId) }
        if !changes.isEmpty { engine.update(item.id, changes) }
    }
}

// Ticks a reminder off - or, for a repeating one, moves it on to its next date, the way Apple
// Reminders does.
@MainActor
func completeReminder(_ reminder: OrganizerItem, done: Bool, engine: OrganizerEngine) {
    if done, let next = Recurrence.nextDue(of: reminder) {
        engine.update(reminder.id, ["due": .string(next), "completed": false, "lastCompletedAt": .string(OrganizerDates.timestamp(Date()))])
        return
    }
    engine.update(reminder.id, ["completed": .bool(done), "completedAt": done ? .string(OrganizerDates.timestamp(Date())) : .null])
}

// MARK: - cards

struct CardEditor: View {
    @Environment(OrganizerEngine.self) private var engine
    @Environment(\.dismiss) private var dismiss
    let card: OrganizerItem

    @State private var title = ""
    @State private var done = false
    @State private var hasDue = false
    @State private var due = Date()
    @State private var labels = ""
    @State private var description = ""
    @State private var checklist: [ChecklistEntry] = []
    @State private var newCheck = ""
    @State private var assignees: Set<String> = []
    @State private var people: [OrganizerPerson] = []
    @State private var links: [OrganizerLink] = []
    @State private var loaded = false

    struct ChecklistEntry: Identifiable, Hashable {
        var id: String
        var text: String
        var done: Bool
    }

    var body: some View {
        Form {
            Section {
                TextField("Title", text: $title).font(.title3)
                Toggle("Done", isOn: $done)
                Toggle("Due date", isOn: $hasDue.animation())
                if hasDue { DatePicker("Due", selection: $due, displayedComponents: .date) }
                TextField("Labels, comma separated", text: $labels)
            }
            Section("Description") {
                TextField("Details", text: $description, axis: .vertical).lineLimit(3...12)
            }
            Section("Checklist") {
                ForEach($checklist) { $entry in
                    Toggle(isOn: $entry.done) { Text(entry.text).strikethrough(entry.done) }
                        #if os(iOS)
                        .toggleStyle(.switch)
                        #endif
                }
                .onDelete { checklist.remove(atOffsets: $0) }
                TextField("Add an item", text: $newCheck)
                    .onSubmit {
                        let text = newCheck.trimmingCharacters(in: .whitespaces)
                        guard !text.isEmpty else { return }
                        checklist.append(ChecklistEntry(id: UUID().uuidString.prefix(8).lowercased(), text: text, done: false))
                        newCheck = ""
                    }
            }
            if !people.isEmpty {
                Section("People") {
                    ForEach(people) { person in
                        Toggle(person.label, isOn: Binding(
                            get: { assignees.contains(person.username) },
                            set: { if $0 { assignees.insert(person.username) } else { assignees.remove(person.username) } }))
                    }
                }
            }
            LinksSection(links: $links)
            AttachmentsSection(parentId: card.id) { card.id }
            CardCommentsSection(cardId: card.id, people: people)
            Section {
                Button("Delete Card", role: .destructive) {
                    engine.deleteTree(card.id)
                    dismiss()
                }
            }
        }
        .formStyle(.grouped)
        .navigationTitle(engine.item(card.parentId ?? "")?.title ?? "Card")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
            ToolbarItem(placement: .confirmationAction) { Button("Save") { save(); dismiss() } }
        }
        .onAppear(perform: load)
        .task {
            if engine.status?.personalSpaces == true { people = (try? await engine.people()) ?? [] }
        }
    }

    private func load() {
        guard !loaded else { return }
        loaded = true
        let item = engine.item(card.id) ?? card
        title = item.title
        done = item.bool("completed")
        if let date = OrganizerDates.parse(item.string("due"), allDay: (item.string("due") ?? "").count == 10) {
            hasDue = true
            due = date
        }
        labels = (item.fields["labels"]?.array ?? []).compactMap { $0.object?["name"]?.string }.joined(separator: ", ")
        description = item.string("description") ?? ""
        checklist = (item.fields["checklist"]?.array ?? []).compactMap { entry in
            guard let object = entry.object, let text = object["text"]?.string else { return nil }
            return ChecklistEntry(id: object["id"]?.string ?? UUID().uuidString, text: text, done: object["done"]?.bool ?? false)
        }
        assignees = Set((item.fields["assignees"]?.array ?? []).compactMap(\.string))
        links = OrganizerLink.from(item.fields["links"])
    }

    private func save() {
        guard let item = engine.item(card.id) else { return }
        let existingLabels = Dictionary((item.fields["labels"]?.array ?? []).compactMap { entry -> (String, JSONValue)? in
            guard let name = entry.object?["name"]?.string else { return nil }
            return (name, entry)
        }, uniquingKeysWith: { first, _ in first })
        let labelValues: [JSONValue] = labels.split(separator: ",")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
            .map { existingLabels[$0] ?? .object(["name": .string($0)]) }
        var fields: [String: JSONValue] = [
            "title": .string(title.trimmingCharacters(in: .whitespaces).isEmpty ? "Card" : title.trimmingCharacters(in: .whitespaces)),
            "completed": .bool(done),
            "due": hasDue ? .string(OrganizerDates.ymd(due)) : .null,
            "labels": labelValues.isEmpty ? .null : .array(labelValues),
            "description": description.isEmpty ? .null : .string(description),
            "checklist": checklist.isEmpty ? .null : .array(checklist.map { .object(["id": .string($0.id), "text": .string($0.text), "done": .bool($0.done)]) }),
            "links": OrganizerLink.json(links),
        ]
        if !people.isEmpty { fields["assignees"] = assignees.isEmpty ? .null : .array(assignees.sorted().map(JSONValue.string)) }
        let changes = changedFields(item.fields, fields)
        if !changes.isEmpty { engine.update(item.id, changes) }
    }
}

// A card's comments. Each is its own item, posted the moment it's written rather than with the
// card's Save, and the list follows sync while the card is open - the engine is observable, so
// other people's comments appear as they arrive. An edit in progress lives in this view's state,
// so a redraw from sync never wipes it. Only the author may edit (the server enforces it);
// anyone may delete.
struct CardCommentsSection: View {
    @Environment(OrganizerEngine.self) private var engine
    let cardId: String
    let people: [OrganizerPerson]

    @State private var draft = ""
    @State private var editingId: String?
    @State private var editText = ""
    @State private var confirmingDelete: OrganizerItem?

    var body: some View {
        let comments = engine.comments(on: cardId)
        let me = engine.status?.username
        Section("Comments") {
            if comments.isEmpty {
                Text("No comments yet.").foregroundStyle(.secondary)
            }
            ForEach(comments) { comment in
                VStack(alignment: .leading, spacing: 6) {
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        // No author at all with login off (one shared household): just the date.
                        if let author = name(of: comment.createdBy) {
                            Text(author).font(.subheadline.bold())
                        }
                        Text(stamp(comment)).font(.caption).foregroundStyle(.secondary)
                        Spacer()
                        if comment.isAuthored(by: me), editingId != comment.id {
                            Button("Edit") {
                                editText = comment.string("body") ?? ""
                                editingId = comment.id
                            }
                            .font(.caption)
                        }
                        Button {
                            confirmingDelete = comment
                        } label: {
                            Image(systemName: "trash")
                        }
                        .font(.caption)
                        .accessibilityLabel("Delete comment")
                    }
                    .buttonStyle(.borderless)
                    if editingId == comment.id {
                        TextField("Comment", text: $editText, axis: .vertical)
                            .lineLimit(2...8)
                            .textFieldStyle(.roundedBorder)
                        HStack {
                            Button("Cancel") { editingId = nil }
                            Spacer()
                            Button("Save") {
                                engine.editComment(comment.id, body: editText)
                                editingId = nil
                            }
                            .disabled(editText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                        }
                        .buttonStyle(.borderless)
                    } else {
                        Text(comment.string("body") ?? "")
                            .textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                }
                .padding(.vertical, 2)
            }
            HStack(alignment: .bottom) {
                TextField("Write a comment", text: $draft, axis: .vertical)
                    .lineLimit(1...6)
                Button("Post") {
                    if engine.postComment(draft, on: cardId) != nil { draft = "" }
                }
                .buttonStyle(.borderless)
                .disabled(draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
        .onChange(of: comments.map(\.id)) { _, ids in
            // Someone deleted the comment being edited.
            if let editingId, !ids.contains(editingId) { self.editingId = nil }
        }
        .confirmationDialog("Delete this comment?", isPresented: Binding(get: { confirmingDelete != nil }, set: { if !$0 { confirmingDelete = nil } }),
                            titleVisibility: .visible, presenting: confirmingDelete) { comment in
            Button("Delete", role: .destructive) {
                if editingId == comment.id { editingId = nil }
                engine.delete(comment.id)
            }
        }
    }

    private func name(of username: String?) -> String? {
        guard let username, !username.isEmpty else { return nil }
        return people.first { $0.username.caseInsensitiveCompare(username) == .orderedSame }?.label ?? username
    }

    private func stamp(_ comment: OrganizerItem) -> String {
        let when = comment.createdAt.map { $0.formatted(date: .abbreviated, time: .shortened) } ?? ""
        return comment.editedAt == nil ? when : "\(when) (edited)"
    }
}
