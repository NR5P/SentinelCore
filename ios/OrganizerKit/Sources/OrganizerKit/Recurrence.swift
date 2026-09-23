import Foundation

// Repeating events and reminders: the subset of RFC 5545 RRULE every client supports (see
// ORGANIZER_SYNC.md) - FREQ DAILY/WEEKLY/MONTHLY/YEARLY with INTERVAL, BYDAY, BYMONTHDAY, COUNT
// and UNTIL. This is a port of the dashboard's organizer.js so that a repeating event lands on the
// same days on every device; RecurrenceTests pins the cases where the two could drift.
//
// Occurrences are walked in local calendar fields, not fixed seconds, so a weekly 9am meeting is
// still at 9am after a daylight-saving change.
public struct RecurrenceRule: Hashable, Sendable {
    public enum Frequency: String, Sendable { case daily = "DAILY", weekly = "WEEKLY", monthly = "MONTHLY", yearly = "YEARLY" }

    public var frequency: Frequency
    public var interval: Int = 1
    public var count: Int?
    public var until: Date?
    public var byWeekday: [Int]?      // 0 = Sunday ... 6 = Saturday
    public var byMonthDay: [Int]?

    private static let weekdayCodes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"]

    public init(frequency: Frequency, interval: Int = 1, count: Int? = nil, until: Date? = nil, byWeekday: [Int]? = nil, byMonthDay: [Int]? = nil) {
        self.frequency = frequency
        self.interval = max(1, interval)
        self.count = count
        self.until = until
        self.byWeekday = byWeekday
        self.byMonthDay = byMonthDay
    }

    public init?(_ text: String?, calendar: Calendar = .current) {
        guard let text, !text.isEmpty else { return nil }
        var parts: [String: String] = [:]
        for part in text.split(separator: ";") {
            let pair = part.split(separator: "=", maxSplits: 1)
            if pair.count == 2 { parts[pair[0].uppercased()] = String(pair[1]) }
        }
        guard let frequency = parts["FREQ"].flatMap(Frequency.init(rawValue:)) else { return nil }
        self.frequency = frequency
        interval = max(1, Int(parts["INTERVAL"] ?? "") ?? 1)
        count = parts["COUNT"].flatMap { Int($0) }
        until = parts["UNTIL"].flatMap { Self.parseUntil($0, calendar: calendar) }
        byWeekday = parts["BYDAY"].map { value in
            value.split(separator: ",").compactMap { Self.weekdayCodes.firstIndex(of: String($0.suffix(2))) }
        }
        byMonthDay = parts["BYMONTHDAY"].map { $0.split(separator: ",").compactMap { Int($0) } }
    }

    public var rruleValue: String {
        var parts = ["FREQ=\(frequency.rawValue)"]
        if interval > 1 { parts.append("INTERVAL=\(interval)") }
        if let byWeekday, !byWeekday.isEmpty { parts.append("BYDAY=" + byWeekday.map { Self.weekdayCodes[$0] }.joined(separator: ",")) }
        if let byMonthDay, !byMonthDay.isEmpty { parts.append("BYMONTHDAY=" + byMonthDay.map(String.init).joined(separator: ",")) }
        if let count { parts.append("COUNT=\(count)") }
        if let until {
            var calendar = Calendar(identifier: .gregorian)
            calendar.timeZone = .current
            let c = calendar.dateComponents([.year, .month, .day], from: until)
            parts.append(String(format: "UNTIL=%04d%02d%02d", c.year ?? 0, c.month ?? 0, c.day ?? 0))
        }
        return parts.joined(separator: ";")
    }

    // "20261231" means the end of that local day; "20261231T000000Z" is an exact UTC instant.
    static func parseUntil(_ text: String, calendar: Calendar) -> Date? {
        let digits = text.filter(\.isNumber)
        guard digits.count >= 8,
              let year = Int(digits.prefix(4)),
              let month = Int(digits.dropFirst(4).prefix(2)),
              let day = Int(digits.dropFirst(6).prefix(2)) else { return nil }
        if digits.count >= 14,
           let hour = Int(digits.dropFirst(8).prefix(2)),
           let minute = Int(digits.dropFirst(10).prefix(2)),
           let second = Int(digits.dropFirst(12).prefix(2)) {
            var zoned = calendar
            if text.hasSuffix("Z") { zoned.timeZone = TimeZone(identifier: "UTC")! }
            return zoned.date(from: DateComponents(year: year, month: month, day: day, hour: hour, minute: minute, second: second))
        }
        return calendar.date(from: DateComponents(year: year, month: month, day: day, hour: 23, minute: 59, second: 59))
    }

    // Every start of the series from `start` on, in order, up to `limit` steps of the rule.
    public func starts(from start: Date, calendar: Calendar = .current, limit: Int = 3000) -> AnySequence<Date> {
        let rule = self
        return AnySequence { () -> AnyIterator<Date> in
            var step = 0
            var produced = 0
            var buffer: [Date] = []
            var finished = false
            let time = calendar.dateComponents([.hour, .minute, .second], from: start)

            func candidates(for step: Int) -> [Date] {
                switch rule.frequency {
                case .daily:
                    return [calendar.date(byAdding: .day, value: step * rule.interval, to: start)].compactMap { $0 }
                case .weekly:
                    let weekday = calendar.component(.weekday, from: start) - 1
                    guard let weekStart = calendar.date(byAdding: .day, value: step * 7 * rule.interval - weekday, to: calendar.startOfDay(for: start)) else { return [] }
                    let days = (rule.byWeekday?.isEmpty == false ? rule.byWeekday! : [weekday]).sorted()
                    return days.compactMap { day in
                        calendar.date(byAdding: .day, value: day, to: weekStart).flatMap {
                            calendar.date(bySettingHour: time.hour ?? 0, minute: time.minute ?? 0, second: time.second ?? 0, of: $0)
                        }
                    }
                case .monthly:
                    guard let month = calendar.date(byAdding: .month, value: step * rule.interval, to: calendar.date(from: calendar.dateComponents([.year, .month], from: start))!) else { return [] }
                    let range = calendar.range(of: .day, in: .month, for: month) ?? 1..<29
                    let last = range.count
                    let wanted = rule.byMonthDay?.isEmpty == false ? rule.byMonthDay! : [calendar.component(.day, from: start)]
                    let components = calendar.dateComponents([.year, .month], from: month)
                    return wanted.map { $0 < 0 ? last + $0 + 1 : $0 }
                        .filter { $0 >= 1 && $0 <= last }
                        .sorted()
                        .compactMap { day in
                            calendar.date(from: DateComponents(year: components.year, month: components.month, day: day, hour: time.hour, minute: time.minute, second: time.second))
                        }
                case .yearly:
                    let c = calendar.dateComponents([.year, .month, .day], from: start)
                    let year = (c.year ?? 0) + step * rule.interval
                    guard let date = calendar.date(from: DateComponents(year: year, month: c.month, day: c.day, hour: time.hour, minute: time.minute, second: time.second)) else { return [] }
                    // 29 February only happens in leap years; other years skip it rather than
                    // sliding to 1 March.
                    return calendar.component(.month, from: date) == c.month ? [date] : []
                }
            }

            return AnyIterator {
                while !finished {
                    if !buffer.isEmpty {
                        let date = buffer.removeFirst()
                        if date < start { continue }
                        if let until = rule.until, date > until { finished = true; return nil }
                        produced += 1
                        if let count = rule.count, produced > count { finished = true; return nil }
                        return date
                    }
                    guard step < limit else { finished = true; return nil }
                    buffer = candidates(for: step)
                    step += 1
                }
                return nil
            }
        }
    }
}

public struct Occurrence: Hashable, Sendable {
    public var item: OrganizerItem
    public var start: Date
    public var end: Date
    // The start this occurrence has in its series, in field format - what goes in exdates or a
    // detached copy's originalStart. Nil for an event that doesn't repeat.
    public var originalStart: String?
}

public enum Recurrence {
    // An event's span: all-day ends are exclusive; a missing or backwards end becomes an hour (or
    // a day, for all-day).
    public static func span(of event: OrganizerItem, calendar: Calendar = .current) -> (start: Date, end: Date)? {
        let allDay = event.bool("allDay")
        guard let start = OrganizerDates.parse(event.string("start"), allDay: allDay, calendar: calendar) else { return nil }
        var end = OrganizerDates.parse(event.string("end"), allDay: allDay, calendar: calendar)
        if end == nil || end! <= start {
            end = allDay ? calendar.date(byAdding: .day, value: 1, to: start) : start.addingTimeInterval(3600)
        }
        return (start, end!)
    }

    public static func occurrences(of event: OrganizerItem, from: Date, to: Date, calendar: Calendar = .current) -> [Occurrence] {
        guard let (start, end) = span(of: event, calendar: calendar) else { return [] }
        let duration = end.timeIntervalSince(start)
        let allDay = event.bool("allDay")
        guard let rule = RecurrenceRule(event.string("recurrence"), calendar: calendar) else {
            return start < to && end > from ? [Occurrence(item: event, start: start, end: end, originalStart: nil)] : []
        }

        let skipped = Set((event.fields["exdates"]?.array ?? []).compactMap { value -> String? in
            guard let text = value.string, let date = OrganizerDates.parse(text, allDay: allDay, calendar: calendar) else { return nil }
            return key(date, allDay: allDay, calendar: calendar)
        })
        var out: [Occurrence] = []
        for occurrenceStart in rule.starts(from: start, calendar: calendar) {
            if occurrenceStart >= to { break }
            let occurrenceEnd = occurrenceStart.addingTimeInterval(duration)
            if occurrenceEnd <= from || skipped.contains(key(occurrenceStart, allDay: allDay, calendar: calendar)) { continue }
            out.append(Occurrence(
                item: event, start: occurrenceStart, end: occurrenceEnd,
                originalStart: OrganizerDates.fieldValue(occurrenceStart, allDay: allDay, calendar: calendar)))
        }
        return out
    }

    // When the last occurrence of this event ends: .forever for an open-ended series, nil when
    // the event has no usable start.
    public enum LastEnd: Hashable, Sendable { case forever, at(Date) }

    public static func lastEnd(of event: OrganizerItem, calendar: Calendar = .current) -> LastEnd? {
        guard let (start, end) = span(of: event, calendar: calendar) else { return nil }
        guard let rule = RecurrenceRule(event.string("recurrence"), calendar: calendar) else { return .at(end) }
        if rule.until == nil && rule.count == nil { return .forever }
        var last = start
        for date in rule.starts(from: start, calendar: calendar) { last = date }
        return .at(last.addingTimeInterval(end.timeIntervalSince(start)))
    }

    // The due date after completing a repeating reminder: the occurrence after the one just done,
    // the same as the dashboard and Apple Reminders - so a weekly chore ticked off late doesn't
    // silently skip a week.
    public static func nextDue(of reminder: OrganizerItem, calendar: Calendar = .current) -> String? {
        let allDay = reminder.bool("allDay")
        guard let due = OrganizerDates.parse(reminder.string("due"), allDay: allDay, calendar: calendar),
              let rule = RecurrenceRule(reminder.string("recurrence"), calendar: calendar) else { return nil }
        for date in rule.starts(from: due, calendar: calendar) where date > due {
            return OrganizerDates.fieldValue(date, allDay: allDay, calendar: calendar)
        }
        return nil
    }

    static func key(_ date: Date, allDay: Bool, calendar: Calendar) -> String {
        allDay ? OrganizerDates.ymd(date, calendar: calendar) : String(Int64(date.timeIntervalSince1970))
    }
}
