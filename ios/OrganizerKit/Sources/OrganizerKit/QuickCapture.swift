import Foundation

// One line said or typed into Quick Add (the widget's microphone and keyboard buttons): "add a
// reminder to take out the trash at 7am tomorrow", "note the garage code is 4412", "dentist Friday
// from 2 to 3pm on my calendar". The words that say which of the three it is come off the front
// (or the end), and the rest goes to VoicePhrase for the date, the same rules Siri's intents use.
// Nothing said about which is a reminder; the sheet lets the person switch before adding.
//
// No model or server: plain rules, instant and offline. The Android app has the same rules in
// organizer/QuickCapture.kt, and both test suites read core/docs/quick-capture-cases.json. Change
// them together.
public struct QuickCapture: Equatable, Sendable {
    public enum Kind: String, CaseIterable, Sendable {
        case reminder, note, event
    }

    public var kind: Kind
    // True when the words named the kind ("add a note…"); false for the reminder default.
    public var named: Bool
    // What's left once the words naming the kind are gone.
    public var text: String

    public static func parse(_ text: String) -> QuickCapture {
        let said = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if let rest = Rules.strip(Rules.noteLead, from: said) { return QuickCapture(kind: .note, named: true, text: rest) }
        if let rest = Rules.strip(Rules.eventLead, from: said) {
            // "Schedule dentist on my calendar Friday": the calendar words go too.
            return QuickCapture(kind: .event, named: true, text: Rules.strip(Rules.calendarWords, from: rest) ?? rest)
        }
        if let rest = Rules.strip(Rules.todoLead, from: said) { return QuickCapture(kind: .reminder, named: true, text: rest) }
        // "Remind me…" and "add a reminder…" stay on: VoicePhrase takes them off around the date.
        if Rules.matches(Rules.reminderLead, said) { return QuickCapture(kind: .reminder, named: true, text: said) }
        // "Put dentist on my calendar Friday from 2 to 3pm": the calendar words in the middle.
        if Rules.matches(Rules.calendarAsk, said), let rest = Rules.strip(Rules.calendarWords, from: said) {
            return QuickCapture(kind: .event, named: true, text: Rules.strip(Rules.addVerb, from: rest) ?? rest)
        }
        if let rest = Rules.strip(Rules.eventTail, from: said) {
            return QuickCapture(kind: .event, named: true, text: Rules.strip(Rules.addVerb, from: rest) ?? rest)
        }
        if let rest = Rules.strip(Rules.reminderTail, from: said) {
            return QuickCapture(kind: .reminder, named: true, text: Rules.strip(Rules.addVerb, from: rest) ?? rest)
        }
        return QuickCapture(kind: .reminder, named: false, text: said)
    }

    // What would be added, as `kind` or as whatever the words said.
    public func draft(as chosen: Kind? = nil, now: Date = Date(), calendar: Calendar = .current) -> Draft {
        switch chosen ?? kind {
        case .note:
            let words = (Rules.strip(Rules.reminderLead, from: text) ?? text).trimmingCharacters(in: .whitespacesAndNewlines)
            let spoken = words.prefix(1).uppercased() + words.dropFirst()
            let title = NoteText.title(for: spoken)
            return Draft(kind: .note, title: title, body: title == spoken ? "" : spoken)
        case .reminder:
            let phrase = VoicePhrase.parse(text, now: now, calendar: calendar)
            return Draft(kind: .reminder, title: phrase.title, start: phrase.due, hasTime: phrase.hasTime)
        case .event:
            return EventWords(text: text, now: now, calendar: calendar).draft()
        }
    }

    public struct Draft: Equatable, Sendable {
        public var kind: Kind
        public var title: String
        // A note's text when it's longer than its title.
        public var body: String = ""
        // A reminder's due date, or an event's start. An event always has one: today, all day,
        // when no day was said.
        public var start: Date?
        public var end: Date?
        // False for a date with no time, which is all-day.
        public var hasTime: Bool = false

        public var organizerKind: OrganizerKind {
            switch kind {
            case .reminder: .reminder
            case .note: .note
            case .event: .event
            }
        }

        public var isEmpty: Bool { title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }

        // The fields for OrganizerEngine.create (ORGANIZER_SYNC.md), the same ones the editors write.
        public func fields(calendar: Calendar = .current) -> [String: JSONValue] {
            switch kind {
            case .note:
                return ["title": .string(title), "body": .string(body)]
            case .reminder:
                var fields: [String: JSONValue] = ["title": .string(title), "completed": false]
                if let start {
                    fields["due"] = .string(OrganizerDates.fieldValue(start, allDay: !hasTime, calendar: calendar))
                    fields["allDay"] = .bool(!hasTime)
                }
                return fields
            case .event:
                let begins = start ?? calendar.startOfDay(for: Date())
                let ends = end ?? begins.addingTimeInterval(3600)
                return [
                    "title": .string(title),
                    "allDay": .bool(!hasTime),
                    "start": .string(OrganizerDates.fieldValue(begins, allDay: !hasTime, calendar: calendar)),
                    "end": .string(OrganizerDates.fieldValue(ends, allDay: !hasTime, calendar: calendar)),
                    "timeZone": .string(calendar.timeZone.identifier),
                ]
            }
        }
    }
}

private enum Rules {
    static func regex(_ pattern: String) -> NSRegularExpression {
        try! NSRegularExpression(pattern: pattern, options: [.caseInsensitive])
    }

    static func matches(_ expression: NSRegularExpression, _ text: String) -> Bool {
        expression.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)) != nil
    }

    // The text without the match, or nil when there is none.
    static func strip(_ expression: NSRegularExpression, from text: String) -> String? {
        guard let match = expression.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)),
              let range = Range(match.range, in: text) else { return nil }
        var rest = text
        rest.replaceSubrange(range, with: " ")
        return rest.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    // "Add a note that…", "take a note:", "note to self…", "jot down…".
    static let noteLead = regex(#"^\s*(?:please\s+)?(?:(?:(?:add|take|make|write|create|save|new)\s+(?:a\s+|an?\s+new\s+|new\s+)?)?note\b(?:\s+to\s+self\b)?(?:\s*[:,\-]\s*|\s+(?:that\s+says|saying|that|about|to|of)\b)?|(?:jot|write)\s+down\b)"#)
    // "Add an event…", "new calendar event:", "add to my calendar…", "schedule…", and "add a
    // meeting…" - where "meeting" stays, since it's what the event is.
    static let eventLead = regex(#"^\s*(?:please\s+)?(?:(?:(?:add|create|make|new|put|schedule|set\s+up)\s+(?:an?\s+new\s+|an?\s+|new\s+)?)?(?:calendar\s+)?event\b(?:\s*[:,\-]\s*|\s+(?:for|called|named|to)\b)?|(?:add|put)\s+(?:to|on|in)\s+(?:my\s+|the\s+|our\s+)?calendar\b\s*[:,\-]?|schedule\s+(?:an?\s+)?|(?:add|create|make|book|set\s+up)\s+(?:an?\s+|new\s+)?(?=(?:appointment|meeting)\b))"#)
    // "To-do: …", "task: …". "Remind me…" is reminderLead, which VoicePhrase strips itself.
    static let todoLead = regex(#"^\s*(?:please\s+)?(?:(?:add|new|create)\s+(?:an?\s+)?)?(?:to-?do|task)\b(?:\s*[:,\-]\s*|\s+to\b)?"#)
    static let reminderLead = regex(#"^\s*(?:please\s+)?(?:remind\s+me\b(?:\s+(?:to|that)\b)?|(?:(?:add|set|create|make|new|put)\s+(?:a\s+|an?\s+new\s+|new\s+)?)?reminder\b(?:\s+(?:to|that|for(?:\s+me\s+to)?)\b)?)"#)
    // "…to my calendar", "…on the calendar".
    static let eventTail = regex(#"\s+(?:to|on|in|onto)\s+(?:my\s+|the\s+|our\s+)?(?:family\s+)?calendar\s*[.!]?\s*$"#)
    // "Add/put/schedule <something> on my calendar <when>": a verb, then the calendar words anywhere.
    static let calendarAsk = regex(#"^\s*(?:please\s+)?(?:add|put|schedule)\s+\S.*?\s(?:to|on|in|onto)\s+(?:my\s+|the\s+|our\s+)?(?:family\s+)?calendar\b"#)
    // The first "on my calendar", "to the calendar:", wherever it is.
    static let calendarWords = regex(#"(?:^\s*|\s+)(?:to|on|in|onto)\s+(?:my\s+|the\s+|our\s+)?(?:family\s+)?calendar\b\s*[:,.!\-]?"#)
    // "…to my reminders", "…on the to-do list".
    static let reminderTail = regex(#"\s+(?:to|on|in|onto)\s+(?:my\s+|the\s+|our\s+)?(?:reminders?|to-?dos?|to\s+do|tasks?)(?:\s+list)?\s*[.!]?\s*$"#)
    // The verb left at the front once a tail named the kind: "add … to my calendar".
    static let addVerb = regex(#"^\s*(?:please\s+)?(?:add|put|schedule)\b(?:\s+an?\s+event\b)?"#)
}

// An event's day and time, and how long it runs: "from 2 to 3pm", "11-1pm", "for 90 minutes".
// The start goes back through VoicePhrase, so an event and a reminder read a day the same way.
private struct EventWords {
    let text: String
    let now: Date
    let calendar: Calendar

    private static let number = #"(an?|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|fifteen|twenty|thirty|forty[- ]five|ninety|\d+)"#
    private static let duration = Rules.regex(#"\bfor\s+(?:(half\s+an\s+hour)|(an?\s+hour\s+and\s+a\s+half)|"# + number + #"\s+(minutes?|mins?|hours?|hrs?))\b"#)
    // Counts only with "from" or an AM/PM, so "call 2 to 3 people" isn't a time.
    private static let range = Rules.regex(#"\b(from\s+)?(?:at\s+)?(\d{1,2})(?::(\d{2}))?(?:\s*([ap])\.?\s?m\b\.?)?\s*(?:to|until|till|through|-|–)\s*(\d{1,2})(?::(\d{2}))?(?![\d:])(?:\s*([ap])\.?\s?m\b\.?)?"#)

    private struct Clock {
        var hour: Int
        var minute: Int
        var meridiem: String?
    }

    func draft() -> QuickCapture.Draft {
        var words = text
        var minutes = 60
        if let match = Self.duration.firstMatch(in: words, range: NSRange(words.startIndex..., in: words)) {
            if group(match, 1, in: words) != nil {
                minutes = 30
            } else if group(match, 2, in: words) != nil {
                minutes = 90
            } else if let amount = group(match, 3, in: words).flatMap({ Int($0) ?? VoicePhrase.numberWords[$0] }),
                      let unit = group(match, 4, in: words) {
                minutes = unit.hasPrefix("h") ? amount * 60 : amount
            }
            if let range = Range(match.range, in: words) { words.replaceSubrange(range, with: " ") }
        }

        var endClock: Clock?
        if let match = Self.range.firstMatch(in: words, range: NSRange(words.startIndex..., in: words)),
           let startHour = group(match, 2, in: words).flatMap(Int.init),
           let endHour = group(match, 5, in: words).flatMap(Int.init) {
            var start = Clock(hour: startHour, minute: group(match, 3, in: words).flatMap(Int.init) ?? 0, meridiem: group(match, 4, in: words))
            let end = Clock(hour: endHour, minute: group(match, 6, in: words).flatMap(Int.init) ?? 0, meridiem: group(match, 7, in: words))
            let counts = group(match, 1, in: words) != nil || start.meridiem != nil || end.meridiem != nil
            if counts, valid(start), valid(end), let range = Range(match.range, in: words) {
                // "2 to 3pm" is 2 PM; "11 to 1pm" is 11 AM.
                if start.meridiem == nil, let said = end.meridiem, start.hour <= 12 {
                    start.meridiem = said == "p" && start.hour != 12 && start.hour > end.hour ? "a" : said
                }
                let at = String(format: " at %d:%02d", start.hour, start.minute) + (start.meridiem.map { "\($0)m " } ?? " ")
                words.replaceSubrange(range, with: at)
                endClock = end
            }
        }

        let phrase = VoicePhrase.parse(words, now: now, calendar: calendar)
        guard let begins = phrase.due else {
            let today = calendar.startOfDay(for: now)
            return QuickCapture.Draft(kind: .event, title: phrase.title, start: today, end: calendar.date(byAdding: .day, value: 1, to: today), hasTime: false)
        }
        guard phrase.hasTime else {
            return QuickCapture.Draft(kind: .event, title: phrase.title, start: begins, end: calendar.date(byAdding: .day, value: 1, to: begins), hasTime: false)
        }
        let ends = endClock.flatMap { end(at: $0, after: begins) } ?? begins.addingTimeInterval(TimeInterval(minutes * 60))
        return QuickCapture.Draft(kind: .event, title: phrase.title, start: begins, end: ends, hasTime: true)
    }

    private func valid(_ clock: Clock) -> Bool {
        clock.minute <= 59 && (clock.meridiem == nil ? clock.hour <= 23 : (1...12).contains(clock.hour))
    }

    // The first time the clock reads `clock` after the start, on the start's day or the next.
    private func end(at clock: Clock, after start: Date) -> Date? {
        let hours: [Int]
        if let meridiem = clock.meridiem {
            hours = [clock.hour % 12 + (meridiem == "p" ? 12 : 0)]
        } else {
            hours = clock.hour < 12 ? [clock.hour, clock.hour + 12] : [clock.hour]
        }
        let day = calendar.startOfDay(for: start)
        for hour in hours {
            if let date = calendar.date(bySettingHour: hour, minute: clock.minute, second: 0, of: day), date > start { return date }
        }
        guard let next = calendar.date(byAdding: .day, value: 1, to: day) else { return nil }
        return calendar.date(bySettingHour: hours[0], minute: clock.minute, second: 0, of: next)
    }

    private func group(_ match: NSTextCheckingResult, _ index: Int, in text: String) -> String? {
        let range = match.range(at: index)
        guard range.location != NSNotFound, let swiftRange = Range(range, in: text) else { return nil }
        return text[swiftRange].lowercased()
    }
}
