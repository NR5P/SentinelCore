import Foundation

// What the home-screen and Lock Screen widgets show: the next few weeks of events, the open
// reminders and the latest notes, written by the app into the App Group container every time
// its copy changes. A widget never opens the Organizer database or reaches the server itself -
// it reads this file, so it shows the same thing with no signal as with one.
//
// Times are stored as moments, not "is it overdue" flags: a widget lays out entries hours ahead,
// and each entry works out what's past from its own date.
public struct OrganizerWidgetSnapshot: Codable, Equatable, Sendable {
    public struct Event: Codable, Equatable, Sendable, Identifiable {
        // One occurrence of a repeating event is its own row, so the id carries the start.
        public var id: String
        public var itemId: String
        public var title: String
        public var start: Date
        public var end: Date
        public var allDay: Bool
        public var location: String?
        // "#rrggbb" from the event or its space; otherwise a slot in the app's palette.
        public var color: String?
        public var paletteIndex: Int
        public var holiday: Bool

        public init(id: String, itemId: String, title: String, start: Date, end: Date, allDay: Bool, location: String?,
                    color: String?, paletteIndex: Int, holiday: Bool) {
            self.id = id
            self.itemId = itemId
            self.title = title
            self.start = start
            self.end = end
            self.allDay = allDay
            self.location = location
            self.color = color
            self.paletteIndex = paletteIndex
            self.holiday = holiday
        }
    }

    public struct Reminder: Codable, Equatable, Sendable, Identifiable {
        public var id: String
        public var title: String
        public var due: Date?
        // The due field exactly as it was, so a check-off from the widget can tell whether the
        // reminder has moved on since (see OrganizerWidgetAction).
        public var dueField: String?
        public var allDay: Bool
        public var flagged: Bool
        public var priority: Int
        public var recurring: Bool

        public init(id: String, title: String, due: Date?, dueField: String?, allDay: Bool, flagged: Bool, priority: Int, recurring: Bool) {
            self.id = id
            self.title = title
            self.due = due
            self.dueField = dueField
            self.allDay = allDay
            self.flagged = flagged
            self.priority = priority
            self.recurring = recurring
        }

        public func isOverdue(at now: Date, calendar: Calendar = .current) -> Bool {
            guard let due else { return false }
            return allDay ? calendar.startOfDay(for: due) < calendar.startOfDay(for: now) : due < now
        }

        public func isDueToday(at now: Date, calendar: Calendar = .current) -> Bool {
            guard let due else { return false }
            return calendar.isDate(due, inSameDayAs: now)
        }
    }

    public struct Note: Codable, Equatable, Sendable, Identifiable {
        public var id: String
        public var title: String
        public var preview: String
        public var pinned: Bool
        public var updatedAt: Date?

        public init(id: String, title: String, preview: String, pinned: Bool, updatedAt: Date?) {
            self.id = id
            self.title = title
            self.preview = preview
            self.pinned = pinned
            self.updatedAt = updatedAt
        }
    }

    public var generatedAt: Date
    // False when the Organizer is off on the server or the app hasn't been set up; the widgets
    // say so instead of looking empty.
    public var enabled: Bool
    public var events: [Event]
    public var reminders: [Reminder]
    public var notes: [Note]

    public init(generatedAt: Date, enabled: Bool, events: [Event] = [], reminders: [Reminder] = [], notes: [Note] = []) {
        self.generatedAt = generatedAt
        self.enabled = enabled
        self.events = events
        self.reminders = reminders
        self.notes = notes
    }

    public static let empty = OrganizerWidgetSnapshot(generatedAt: .distantPast, enabled: false)

    // Events still running or yet to start at `now`, in order.
    public func upcomingEvents(at now: Date) -> [Event] {
        events.filter { $0.end > now }
    }
}

// A reminder checked off on a widget. The widget can't edit the Organizer, so it leaves one of
// these for the app, which applies it the next time it runs - see OrganizerWidgetStore.
public struct OrganizerWidgetAction: Codable, Equatable, Sendable {
    public var reminderId: String
    // The due field the widget showed. A repeating reminder moves on to its next date when it's
    // done; if it already has (done on another device, or this action applied twice), the action
    // is stale and dropped rather than skipping a second occurrence.
    public var dueField: String?
    public var at: Date

    public init(reminderId: String, dueField: String?, at: Date) {
        self.reminderId = reminderId
        self.dueField = dueField
        self.at = at
    }
}

// The two files the app and its widgets share, in the App Group container.
public struct OrganizerWidgetStore: Sendable {
    public static let appGroup = "group.com.sentinelcore.client"
    // Posted (Darwin notification, crosses processes) when a widget leaves an action, so a
    // running app applies it at once instead of on its next launch.
    public static let actionsPosted = "com.sentinelcore.client.organizer-widget-action"

    public let directory: URL

    public init(directory: URL) {
        self.directory = directory
    }

    public init?() {
        guard let url = FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: Self.appGroup) else { return nil }
        self.init(directory: url)
    }

    private var snapshotURL: URL { directory.appendingPathComponent("organizer-widgets.json") }
    private var actionsURL: URL { directory.appendingPathComponent("organizer-widget-actions.json") }

    public func readSnapshot() -> OrganizerWidgetSnapshot {
        guard let data = try? Data(contentsOf: snapshotURL),
              let snapshot = try? Self.decoder.decode(OrganizerWidgetSnapshot.self, from: data) else { return .empty }
        return snapshot
    }

    public func writeSnapshot(_ snapshot: OrganizerWidgetSnapshot) {
        guard let data = try? Self.encoder.encode(snapshot) else { return }
        coordinate(snapshotURL) { try? data.write(to: $0, options: .atomic) }
    }

    public func pendingActions() -> [OrganizerWidgetAction] {
        var actions: [OrganizerWidgetAction] = []
        coordinate(actionsURL) { actions = Self.read($0) }
        return actions
    }

    // Records the check-off and takes the reminder out of the snapshot, so every widget drops it
    // straight away rather than waiting for the app.
    public func complete(_ reminder: OrganizerWidgetSnapshot.Reminder, at now: Date = Date()) {
        coordinate(actionsURL) { url in
            var actions = Self.read(url).filter { $0.reminderId != reminder.id }
            actions.append(OrganizerWidgetAction(reminderId: reminder.id, dueField: reminder.dueField, at: now))
            if let data = try? Self.encoder.encode(actions) { try? data.write(to: url, options: .atomic) }
        }
        var snapshot = readSnapshot()
        snapshot.reminders.removeAll { $0.id == reminder.id }
        writeSnapshot(snapshot)
    }

    // Every waiting action, cleared in the same coordinated step, so one a widget adds meanwhile
    // is never lost.
    public func takeActions() -> [OrganizerWidgetAction] {
        var actions: [OrganizerWidgetAction] = []
        coordinate(actionsURL) { url in
            actions = Self.read(url)
            if !actions.isEmpty { try? FileManager.default.removeItem(at: url) }
        }
        return actions
    }

    private static func read(_ url: URL) -> [OrganizerWidgetAction] {
        guard let data = try? Data(contentsOf: url) else { return [] }
        return (try? decoder.decode([OrganizerWidgetAction].self, from: data)) ?? []
    }

    // The app and a widget extension are separate processes writing the same files.
    private func coordinate(_ url: URL, _ body: (URL) -> Void) {
        var error: NSError?
        NSFileCoordinator().coordinate(writingItemAt: url, options: [], error: &error, byAccessor: body)
    }

    private static let encoder: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .secondsSince1970
        return encoder
    }()

    private static let decoder: JSONDecoder = {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .secondsSince1970
        return decoder
    }()
}

extension OrganizerEngine {
    // Everything the widgets could want, from this device's copy. Kept small - a widget has a
    // few megabytes of memory - so: events for two weeks, the first 40 reminders, 12 notes.
    public func widgetSnapshot(enabled: Bool, now: Date = Date(), calendar: Calendar = .current, days: Int = 14,
                               skippingReminders skipped: Set<String> = []) -> OrganizerWidgetSnapshot {
        let from = calendar.startOfDay(for: now)
        let to = calendar.date(byAdding: .day, value: days, to: from) ?? from.addingTimeInterval(Double(days) * 86_400)
        let spaces = visibleSpaces

        var events: [OrganizerWidgetSnapshot.Event] = []
        for (holiday, start, end) in holidays(from: from, to: to) {
            events.append(.init(id: "holiday-\(holiday.id)", itemId: holiday.id, title: holiday.name, start: start, end: end,
                                allDay: true, location: nil, color: HolidayColors.hex(holiday.setId), paletteIndex: 0, holiday: true))
        }
        for event in items(.event) {
            let spaceIndex = spaces.firstIndex { $0.id == event.spaceId } ?? 0
            let color = event.string("color") ?? spaces.first { $0.id == event.spaceId }?.color
            for occurrence in Recurrence.occurrences(of: event, from: from, to: to, calendar: calendar) where occurrence.end > now {
                events.append(.init(id: "\(event.id)-\(Int(occurrence.start.timeIntervalSince1970))", itemId: event.id,
                                    title: event.title.isEmpty ? "Event" : event.title, start: occurrence.start, end: occurrence.end,
                                    allDay: event.bool("allDay"), location: event.string("location").flatMap { $0.isEmpty ? nil : $0 },
                                    color: color, paletteIndex: spaceIndex, holiday: false))
            }
        }
        events.sort { ($0.start, $0.holiday ? 0 : 1, $0.title) < ($1.start, $1.holiday ? 0 : 1, $1.title) }

        let open = items(.reminder).filter { $0.parentId == nil && !$0.bool("completed") && !skipped.contains($0.id) }
        let reminders = open.map { reminder -> OrganizerWidgetSnapshot.Reminder in
            let allDay = reminder.bool("allDay")
            let dueField = reminder.string("due").flatMap { $0.isEmpty ? nil : $0 }
            return .init(id: reminder.id, title: reminder.title.isEmpty ? "Reminder" : reminder.title,
                         due: OrganizerDates.parse(dueField, allDay: allDay, calendar: calendar), dueField: dueField,
                         allDay: allDay, flagged: reminder.bool("flagged"), priority: Int(reminder.number("priority") ?? 0),
                         recurring: reminder.string("recurrence") != nil)
        }
        .sorted { a, b in
            switch (a.due, b.due) {
            case let (x?, y?): x != y ? x < y : a.priority > b.priority
            case (.some, nil): true
            case (nil, .some): false
            case (nil, nil): (a.flagged ? 1 : 0, a.priority, b.title) > (b.flagged ? 1 : 0, b.priority, a.title)
            }
        }

        let notes = items(.note)
            .filter { !$0.bool("archived") }
            .sorted { ($0.bool("pinned") ? 1 : 0, $0.updatedAt ?? .distantPast) > ($1.bool("pinned") ? 1 : 0, $1.updatedAt ?? .distantPast) }
            .prefix(12)
            .map { note in
                OrganizerWidgetSnapshot.Note(id: note.id, title: note.title, preview: Self.widgetPreview(note.string("body") ?? ""),
                                             pinned: note.bool("pinned"), updatedAt: note.updatedAt)
            }

        return OrganizerWidgetSnapshot(generatedAt: now, enabled: enabled, events: Array(events.prefix(60)),
                                       reminders: Array(reminders.prefix(40)), notes: Array(notes))
    }

    // A note's body as plain lines: the Markdown marks a widget would otherwise show literally.
    static func widgetPreview(_ body: String) -> String {
        let lines = body.split(whereSeparator: \.isNewline).map { line -> String in
            var text = line.trimmingCharacters(in: .whitespaces)
            for prefix in ["- [ ] ", "- [x] ", "- [X] ", "* ", "- ", "> "] where text.hasPrefix(prefix) {
                text.removeFirst(prefix.count)
                break
            }
            while text.hasPrefix("#") { text.removeFirst() }
            return text.replacingOccurrences(of: "**", with: "").replacingOccurrences(of: "`", with: "")
                .trimmingCharacters(in: .whitespaces)
        }
        return String(lines.filter { !$0.isEmpty }.joined(separator: "\n").prefix(300))
    }
}
