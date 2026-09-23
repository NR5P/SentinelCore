import Foundation

// The date formats inside Organizer fields (ORGANIZER_SYNC.md): a timed moment is ISO 8601 with
// its offset ("2026-09-23T17:00:00-05:00"), an all-day one is a bare "2026-09-23". Other clients
// (the dashboard, Android, Windows) write the same strings, so every conversion lives here.
public enum OrganizerDates {
    public static func parseTimestamp(_ value: String) -> Date? {
        if let date = try? Date.ISO8601FormatStyle(includingFractionalSeconds: true).parse(value) { return date }
        if let date = try? Date.ISO8601FormatStyle(includingFractionalSeconds: false).parse(value) { return date }
        // .NET writes up to seven fractional digits, which the formatter above can reject.
        if let dot = value.firstIndex(of: "."), let end = value[dot...].firstIndex(where: { $0 == "Z" || $0 == "+" || $0 == "-" }) {
            let trimmed = String(value[..<dot]) + String(value[end...])
            return try? Date.ISO8601FormatStyle(includingFractionalSeconds: false).parse(trimmed)
        }
        return nil
    }

    public static func timestamp(_ date: Date) -> String {
        date.formatted(Date.ISO8601FormatStyle(includingFractionalSeconds: true))
    }

    // A field value to a moment. All-day values are local midnight of that calendar date.
    public static func parse(_ value: String?, allDay: Bool, calendar: Calendar = .current) -> Date? {
        guard let value, !value.isEmpty else { return nil }
        if allDay || value.count == 10 {
            return day(value, calendar: calendar)
        }
        return parseTimestamp(value)
    }

    public static func day(_ value: String, calendar: Calendar = .current) -> Date? {
        let parts = value.prefix(10).split(separator: "-").compactMap { Int($0) }
        guard parts.count == 3 else { return nil }
        return calendar.date(from: DateComponents(year: parts[0], month: parts[1], day: parts[2]))
    }

    public static func ymd(_ date: Date, calendar: Calendar = .current) -> String {
        let c = calendar.dateComponents([.year, .month, .day], from: date)
        return String(format: "%04d-%02d-%02d", c.year ?? 0, c.month ?? 0, c.day ?? 0)
    }

    // Local wall-clock time with its offset, the same shape the dashboard writes.
    public static func isoLocal(_ date: Date, timeZone: TimeZone = .current) -> String {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        let c = calendar.dateComponents([.year, .month, .day, .hour, .minute, .second], from: date)
        let offset = timeZone.secondsFromGMT(for: date) / 60
        let sign = offset >= 0 ? "+" : "-"
        return String(format: "%04d-%02d-%02dT%02d:%02d:%02d%@%02d:%02d",
                      c.year ?? 0, c.month ?? 0, c.day ?? 0, c.hour ?? 0, c.minute ?? 0, c.second ?? 0,
                      sign, abs(offset) / 60, abs(offset) % 60)
    }

    // What a field should hold for this date: "YYYY-MM-DD" when all-day, isoLocal otherwise.
    public static func fieldValue(_ date: Date, allDay: Bool, calendar: Calendar = .current) -> String {
        allDay ? ymd(date, calendar: calendar) : isoLocal(date, timeZone: calendar.timeZone)
    }
}
