import Foundation

// Holiday calendars - US federal holidays, Christian, Jewish, Islamic and other religious
// holidays - that people can add to the calendar. The server works the dates out (it has the
// Hebrew, Umm al-Qura and Chinese calendars); this is what a device keeps of them. See
// ORGANIZER_SYNC.md, "Holiday calendars". None is shown unless the person turns it on.

public struct HolidaySet: Codable, Hashable, Sendable, Identifiable {
    public var id: String
    public var name: String
    public var description: String
}

public struct Holiday: Codable, Hashable, Sendable, Identifiable {
    public var id: String
    public var setId: String
    public var name: String
    // "yyyy-MM-dd", all-day.
    public var date: String
    public var days: Int?
    public var note: String?

    public var dayCount: Int { max(1, days ?? 1) }

    // Local midnight on its first day, and on the day after its last.
    public func span(in calendar: Calendar) -> (start: Date, end: Date)? {
        let parts = date.split(separator: "-").compactMap { Int($0) }
        guard parts.count == 3,
              let start = calendar.date(from: DateComponents(year: parts[0], month: parts[1], day: parts[2])),
              let end = calendar.date(byAdding: .day, value: dayCount, to: start) else { return nil }
        return (start, end)
    }
}

// What this device keeps: the chosen sets (and whether that choice has reached the server yet),
// the list of sets there are, and the days themselves for last year through two years ahead.
public struct HolidayState: Codable, Sendable, Equatable {
    public var sets: [String] = []
    public var dirty = false
    public var catalog: [HolidaySet]?
    public var key: String?
    public var list: [Holiday] = []

    public init() {}
}

// One color per holiday calendar, the same in every app.
public enum HolidayColors {
    public static func hex(_ setId: String) -> String {
        switch setId {
        case "us-federal": "#4a9dff"
        case "christian": "#c98bff"
        case "orthodox": "#ff9a52"
        case "jewish": "#4ae8ff"
        case "islamic": "#7fd67f"
        case "hindu": "#ffd84a"
        case "chinese": "#ff4d5e"
        default: "#93a2ad"
        }
    }
}

struct HolidaySetsResponse: Decodable { var sets: [HolidaySet] }
struct HolidaysResponse: Decodable { var holidays: [Holiday] }
struct HolidayPreferences: Codable { var holidaySets: [String]? }
