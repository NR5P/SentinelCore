import CryptoKit
import Foundation

// Bringing reminders in from Apple Reminders - the "inbox": a list the person picks (ideally
// their default list, so "Hey Siri, remind me…" lands there) is emptied into an Organizer space.
// The app reads EventKit and removes what it brought in (RemindersInbox); this is the part that
// decides what each one becomes, kept here where `swift test` reaches it.

// One Apple reminder, as plain values.
public struct AppleReminder: Equatable, Sendable {
    public var externalId: String
    public var title: String
    public var notes: String?
    public var url: URL?
    // Apple's due date: with an hour it's a moment, without one a whole day.
    public var due: DateComponents?
    // EventKit's scale: 0 none, 1-4 high, 5 medium, 6-9 low.
    public var priority: Int
    // Alarms relative to the due date, in seconds (negative is before it).
    public var relativeAlarms: [TimeInterval]
    public var absoluteAlarms: [Date]
    public var hasLocationAlarm: Bool
    public var recurrence: RecurrenceRule?
    // A repeat the Organizer can't express, like "the second Tuesday of every month".
    public var hasUnsupportedRecurrence: Bool

    public init(
        externalId: String, title: String, notes: String? = nil, url: URL? = nil, due: DateComponents? = nil,
        priority: Int = 0, relativeAlarms: [TimeInterval] = [], absoluteAlarms: [Date] = [],
        hasLocationAlarm: Bool = false, recurrence: RecurrenceRule? = nil, hasUnsupportedRecurrence: Bool = false
    ) {
        self.externalId = externalId
        self.title = title
        self.notes = notes
        self.url = url
        self.due = due
        self.priority = priority
        self.relativeAlarms = relativeAlarms
        self.absoluteAlarms = absoluteAlarms
        self.hasLocationAlarm = hasLocationAlarm
        self.recurrence = recurrence
        self.hasUnsupportedRecurrence = hasUnsupportedRecurrence
    }
}

public enum AppleReminderImport {
    public enum Outcome: Equatable, Sendable {
        case bring(id: String, fields: [String: JSONValue])
        // Left in Apple Reminders, which can do something the Organizer can't.
        case leave(reason: String)
    }

    // The same on every device for the same Apple reminder: with iCloud, the iPhone and the Mac
    // both see it, and whichever gets there second updates the same item instead of adding a copy.
    public static func itemId(for externalId: String) -> String {
        "apple-" + SHA256.hash(data: Data(externalId.utf8)).prefix(16).map { String(format: "%02x", $0) }.joined()
    }

    public static func outcome(for reminder: AppleReminder, calendar: Calendar = .current) -> Outcome {
        if reminder.hasLocationAlarm { return .leave(reason: "alerts by location") }
        if reminder.hasUnsupportedRecurrence { return .leave(reason: "repeats in a way the Organizer can't") }

        let title = reminder.title.trimmingCharacters(in: .whitespacesAndNewlines)
        var fields: [String: JSONValue] = [
            "title": .string(title.isEmpty ? "Reminder" : title),
            "completed": false,
            "appleReminderId": .string(reminder.externalId),
        ]
        if let notes = reminder.notes?.trimmingCharacters(in: .whitespacesAndNewlines), !notes.isEmpty {
            fields["notes"] = .string(notes)
        }
        if let url = reminder.url {
            fields["links"] = .array([.object(["url": .string(url.absoluteString)])])
        }
        let priority = organizerPriority(reminder.priority)
        if priority > 0 { fields["priority"] = .number(Double(priority)) }

        // The due date, and alerts as minutes before it.
        var due: Date?
        var allDay = false
        if let components = reminder.due, let date = calendar.date(from: components) {
            due = date
            allDay = components.hour == nil
        }
        let absolute = reminder.absoluteAlarms.sorted()
        if let first = absolute.first, due == nil || allDay {
            // A time to be reminded with no due time: that time is the due time. Apple lets a
            // day-only reminder alert at a set time, which the Organizer would otherwise move to
            // its all-day 9 AM.
            due = first
            allDay = false
        }
        var offsets: Set<Int> = []
        if let due, !allDay {
            for alarm in absolute {
                let minutes = Int((due.timeIntervalSince(alarm) / 60).rounded())
                if minutes >= 0 { offsets.insert(minutes) }
            }
        }
        if due != nil {
            for offset in reminder.relativeAlarms where offset <= 0 {
                offsets.insert(Int((-offset / 60).rounded()))
            }
        }
        if let due {
            fields["due"] = .string(allDay ? dayValue(reminder.due, fallback: due, calendar: calendar) : OrganizerDates.isoLocal(due, timeZone: calendar.timeZone))
            fields["allDay"] = .bool(allDay)
            // An alert at the due time is what a due date already means (AlertPlanner).
            if !offsets.isEmpty, offsets != [0] {
                fields["alerts"] = .array(offsets.sorted().map { .number(Double($0)) })
            }
            if let rule = reminder.recurrence { fields["recurrence"] = .string(rule.rruleValue) }
        }
        return .bring(id: itemId(for: reminder.externalId), fields: fields)
    }

    // Apple's 1-9 (1 highest) to the Organizer's 0-3 (3 highest).
    public static func organizerPriority(_ value: Int) -> Int {
        switch value {
        case 1...4: 3
        case 5: 2
        case 6...9: 1
        default: 0
        }
    }

    // A whole day straight from Apple's own year/month/day, so no time zone can move it.
    private static func dayValue(_ components: DateComponents?, fallback: Date, calendar: Calendar) -> String {
        if let year = components?.year, let month = components?.month, let day = components?.day {
            return String(format: "%04d-%02d-%02d", year, month, day)
        }
        return OrganizerDates.ymd(fallback, calendar: calendar)
    }
}
