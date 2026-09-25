import Foundation

// A spoken reminder split into what to do and when: "call mom tomorrow at 5pm" is "Call mom", due
// tomorrow at 5 PM. Siri hands the app the words as said, so the date has to come out of them here.
//
// The Android app has the same rules in organizer/VoicePhrase.kt, and both test suites check the
// same examples - a phrase means the same thing whichever phone heard it. Change them together.
public struct VoicePhrase: Equatable, Sendable {
    public var title: String
    public var due: Date?
    // False for a date with no time ("dentist Friday"), which is stored as an all-day due date.
    public var hasTime: Bool

    public init(title: String, due: Date? = nil, hasTime: Bool = false) {
        self.title = title
        self.due = due
        self.hasTime = hasTime
    }

    // The value for a reminder's `due` field (ORGANIZER_SYNC.md), or nil for none.
    public func dueField(calendar: Calendar = .current) -> String? {
        due.map { OrganizerDates.fieldValue($0, allDay: !hasTime, calendar: calendar) }
    }

    public static func parse(_ text: String, now: Date = Date(), calendar: Calendar = .current) -> VoicePhrase {
        var parser = Parser(text: text, now: now, calendar: calendar)
        return parser.run()
    }

    // Morning, afternoon, evening and night as clock hours - Apple Reminders' choices.
    static let periodHours = ["morning": 9, "afternoon": 14, "evening": 18, "night": 20, "tonight": 20]

    static let numberWords = [
        "a": 1, "an": 1, "one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6, "seven": 7,
        "eight": 8, "nine": 9, "ten": 10, "eleven": 11, "twelve": 12, "fifteen": 15, "twenty": 20,
        "thirty": 30, "forty-five": 45, "forty five": 45, "ninety": 90,
    ]

    static let weekdays = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"]

    static let months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"]
}

private struct Parser {
    let text: String
    let now: Date
    let calendar: Calendar
    var removed: [NSRange] = []

    init(text: String, now: Date, calendar: Calendar) {
        self.text = text
        self.now = now
        self.calendar = calendar
    }

    private static func regex(_ pattern: String) -> NSRegularExpression {
        try! NSRegularExpression(pattern: pattern, options: [.caseInsensitive])
    }

    private static let number = #"(half an|an?|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|fifteen|twenty|thirty|forty[- ]five|ninety|\d+)"#
    private static let relative = regex(#"\b(?:in|within)\s+"# + number + #"\s+(minutes?|mins?|hours?|hrs?|days?|weeks?)\b"#)
    private static let day = regex(#"\b(?:(?:on|by|this|next)\s+)?(today|tonight|tomorrow|sunday|monday|tuesday|wednesday|thursday|friday|saturday)(?:\s+(morning|afternoon|evening|night))?\b"#)
    private static let thisPeriod = regex(#"\bthis\s+(morning|afternoon|evening)\b"#)
    private static let monthDate = regex(#"\b(?:(?:on|by)\s+)?(january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sept|sep|oct|nov|dec)\.?\s+(\d{1,2})(?:st|nd|rd|th)?\b"#)
    private static let ordinalDay = regex(#"\b(?:(?:on|by)\s+)?the\s+(\d{1,2})(?:st|nd|rd|th)\b"#)
    private static let meridiemTime = regex(#"\b(?:(?:at|by|around)\s+)?(\d{1,2})(?::(\d{2}))?\s*([ap])\.?\s?m\b\.?"#)
    private static let atTime = regex(#"\b(?:at|by|around)\s+(\d{1,2})(?::(\d{2}))?(?:\s*o'?clock)?\b"#)
    private static let namedTime = regex(#"\b(?:(?:at|by|around)\s+)?(noon|midnight)\b"#)
    private static let periodTime = regex(#"\b(?:in\s+the\s+(morning|afternoon|evening)|at\s+(night))\b"#)
    private static let clockTime = regex(#"\b(\d{1,2}):(\d{2})\b"#)
    // "Remind me to…", "add a reminder to…": the words that mean "this is a reminder" rather than
    // what it is about. The in-app microphone hears them too, since people say the whole thing.
    private static let lead = regex(#"^\s*(?:please\s+)?(?:remind\s+me\s+(?:to\s+|that\s+)?|(?:(?:add|set|create|make|new|put)\s+(?:a\s+|an?\s+new\s+|new\s+)?)?reminder\s+(?:to\s+|that\s+|for\s+(?:me\s+to\s+)?)?)"#)
    private static let dangling = regex(#"\s+(?:at|on|by|in|around|due)\s*$"#)

    private func first(_ expression: NSRegularExpression, avoiding: NSRange? = nil) -> NSTextCheckingResult? {
        expression.matches(in: text, range: NSRange(text.startIndex..., in: text)).first { match in
            avoiding.map { NSIntersectionRange($0, match.range).length == 0 } ?? true
        }
    }

    private func group(_ match: NSTextCheckingResult, _ index: Int) -> String? {
        let range = match.range(at: index)
        guard range.location != NSNotFound, let swiftRange = Range(range, in: text) else { return nil }
        return text[swiftRange].lowercased()
    }

    private var today: Date { calendar.startOfDay(for: now) }

    private func at(_ day: Date, hour: Int, minute: Int) -> Date? {
        calendar.date(bySettingHour: hour, minute: minute, second: 0, of: day)
    }

    mutating func run() -> VoicePhrase {
        var due: Date?
        var hasTime = false

        if let match = first(Self.relative), let unit = group(match, 2), let amount = amount(group(match, 1), unit: unit) {
            removed.append(match.range)
            if group(match, 1) == "half an" {
                return finish(due: roundedToMinute(now.addingTimeInterval(TimeInterval(amount * 60))), hasTime: true)
            }
            if unit.hasPrefix("m") {
                due = now.addingTimeInterval(TimeInterval(amount * 60))
                hasTime = true
            } else if unit.hasPrefix("h") {
                due = now.addingTimeInterval(TimeInterval(amount * 3600))
                hasTime = true
            } else {
                due = calendar.date(byAdding: .day, value: unit.hasPrefix("w") ? amount * 7 : amount, to: today)
            }
            if let due, hasTime { return finish(due: roundedToMinute(due), hasTime: true) }
            return finish(due: due, hasTime: false)
        }

        // Which day, and a time of day the words carried with it ("tomorrow evening").
        var day: Date?
        var dayPeriod: Int?
        var dayRange: NSRange?
        if let match = first(Self.day), let word = group(match, 1) {
            dayRange = match.range
            dayPeriod = group(match, 2).flatMap { VoicePhrase.periodHours[$0] }
            switch word {
            case "today": day = today
            case "tonight": day = today; dayPeriod = dayPeriod ?? VoicePhrase.periodHours["tonight"]
            case "tomorrow": day = calendar.date(byAdding: .day, value: 1, to: today)
            default:
                if let index = VoicePhrase.weekdays.firstIndex(of: word) {
                    let current = calendar.component(.weekday, from: today)
                    var ahead = (index + 1 - current + 7) % 7
                    if ahead == 0 { ahead = 7 }
                    day = calendar.date(byAdding: .day, value: ahead, to: today)
                }
            }
        } else if let match = first(Self.thisPeriod), let word = group(match, 1) {
            dayRange = match.range
            day = today
            dayPeriod = VoicePhrase.periodHours[word]
        } else if let match = first(Self.monthDate), let name = group(match, 1), let number = group(match, 2).flatMap(Int.init),
                  let month = VoicePhrase.months.firstIndex(where: { name.hasPrefix($0) }) {
            let year = calendar.component(.year, from: today)
            if let date = validDate(year: year, month: month + 1, day: number) {
                dayRange = match.range
                day = date < today ? validDate(year: year + 1, month: month + 1, day: number) : date
            }
        } else if let match = first(Self.ordinalDay), let number = group(match, 1).flatMap(Int.init) {
            let parts = calendar.dateComponents([.year, .month, .day], from: today)
            if let year = parts.year, let month = parts.month {
                if number >= (parts.day ?? 1), let date = validDate(year: year, month: month, day: number) {
                    dayRange = match.range
                    day = date
                } else if let next = calendar.date(byAdding: .month, value: 1, to: today) {
                    let nextParts = calendar.dateComponents([.year, .month], from: next)
                    if let date = validDate(year: nextParts.year ?? year, month: nextParts.month ?? month, day: number) {
                        dayRange = match.range
                        day = date
                    }
                }
            }
        }
        if let dayRange { removed.append(dayRange) }

        // The time of day: hour and minute, and whether AM/PM was said.
        var time: (hour: Int, minute: Int, certain: Bool)?
        if let match = first(Self.meridiemTime, avoiding: dayRange), let hour = group(match, 1).flatMap(Int.init), hour >= 1, hour <= 12 {
            let minute = group(match, 2).flatMap(Int.init) ?? 0
            let pm = group(match, 3) == "p"
            time = (hour % 12 + (pm ? 12 : 0), minute, true)
            removed.append(match.range)
        } else if let match = first(Self.namedTime, avoiding: dayRange) {
            time = (group(match, 1) == "noon" ? 12 : 0, 0, true)
            removed.append(match.range)
        } else if let match = first(Self.atTime, avoiding: dayRange), let hour = group(match, 1).flatMap(Int.init), hour <= 23 {
            time = (hour, group(match, 2).flatMap(Int.init) ?? 0, hour == 0 || hour > 12)
            removed.append(match.range)
        } else if let match = first(Self.clockTime, avoiding: dayRange), let hour = group(match, 1).flatMap(Int.init), hour <= 23 {
            time = (hour, group(match, 2).flatMap(Int.init) ?? 0, hour == 0 || hour > 12)
            removed.append(match.range)
        } else if let match = first(Self.periodTime, avoiding: dayRange), let word = group(match, 1) ?? group(match, 2) {
            time = (VoicePhrase.periodHours[word] ?? 9, 0, true)
            removed.append(match.range)
        }
        if var time, time.minute > 59 { time.minute = 0 }

        switch (day, time) {
        case (nil, nil):
            return finish(due: nil, hasTime: false)
        case (let day?, nil):
            if let dayPeriod, let date = at(day, hour: dayPeriod, minute: 0) {
                // "Tonight" said after 8 PM means soon, not a time already gone.
                if date <= now, calendar.isDate(day, inSameDayAs: today) {
                    return finish(due: roundedToMinute(now.addingTimeInterval(3600)), hasTime: true)
                }
                return finish(due: date, hasTime: true)
            }
            return finish(due: day, hasTime: false)
        case (let day?, let time?):
            let hour = time.certain ? time.hour : guessHour(time.hour, period: dayPeriod)
            return finish(due: at(day, hour: hour, minute: time.minute), hasTime: true)
        case (nil, let time?):
            // No day: the next time the clock reads that, today or tomorrow.
            let hours = time.certain ? [time.hour] : candidateHours(time.hour)
            for hour in hours {
                if let date = at(today, hour: hour, minute: time.minute), date > now { return finish(due: date, hasTime: true) }
            }
            let tomorrow = calendar.date(byAdding: .day, value: 1, to: today) ?? today
            return finish(due: at(tomorrow, hour: hours[0], minute: time.minute), hasTime: true)
        }
    }

    // "At 5" is 5 PM - nobody sets a 5 AM reminder without saying so. 7 to 11 could be either.
    private func candidateHours(_ hour: Int) -> [Int] {
        switch hour {
        case 12: [12]
        case 1...6: [hour + 12]
        default: [hour, hour + 12]
        }
    }

    private func guessHour(_ hour: Int, period: Int?) -> Int {
        if let period, period >= 12, hour < 12 { return hour + 12 }
        return candidateHours(hour)[0]
    }

    // "Half an hour" is the one fraction people say; it comes back as minutes.
    private func amount(_ word: String?, unit: String) -> Int? {
        guard let word else { return nil }
        if word == "half an" { return unit.hasPrefix("h") ? 30 : nil }
        return Int(word) ?? VoicePhrase.numberWords[word]
    }

    private func validDate(year: Int, month: Int, day: Int) -> Date? {
        let components = DateComponents(year: year, month: month, day: day)
        guard let date = calendar.date(from: components),
              calendar.component(.day, from: date) == day, calendar.component(.month, from: date) == month else { return nil }
        return date
    }

    private func roundedToMinute(_ date: Date) -> Date {
        calendar.dateInterval(of: .minute, for: date)?.start ?? date
    }

    private func finish(due: Date?, hasTime: Bool) -> VoicePhrase {
        var title = text
        for range in removed.sorted(by: { $0.location > $1.location }) {
            if let swiftRange = Range(range, in: title) { title.replaceSubrange(swiftRange, with: " ") }
        }
        title = Self.lead.stringByReplacingMatches(in: title, range: NSRange(title.startIndex..., in: title), withTemplate: "")
        title = title.split(whereSeparator: \.isWhitespace).joined(separator: " ")
        while true {
            let trimmed = Self.dangling.stringByReplacingMatches(in: title, range: NSRange(title.startIndex..., in: title), withTemplate: "")
                .trimmingCharacters(in: CharacterSet(charactersIn: " ,.;:-"))
            if trimmed == title { break }
            title = trimmed
        }
        if title.isEmpty {
            title = text.trimmingCharacters(in: .whitespacesAndNewlines)
        }
        if let first = title.first { title = first.uppercased() + title.dropFirst() }
        return VoicePhrase(title: title, due: due, hasTime: due != nil && hasTime)
    }
}
