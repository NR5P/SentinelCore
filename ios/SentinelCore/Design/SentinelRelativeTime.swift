import Foundation

// SwiftUI's `Text(date, style: .relative)` renders a running two-unit duration - "29 min,
// 42 sec" - with no "ago" and a seconds field that ticks every second. For an event feed that
// reads like a countdown rather than a timestamp, and it doesn't match the dashboard, which
// shows "29m ago". This gives the same one-unit, past-tense phrasing as the web UI.
// MainActor-isolated because RelativeDateTimeFormatter isn't Sendable and this is only ever
// read from a SwiftUI view body, which is already on the main actor. Reusing one instance
// matters: allocating a formatter per row is measurably slow in a scrolling feed.
@MainActor
enum SentinelRelativeTime {
    private static let formatter: RelativeDateTimeFormatter = {
        let formatter = RelativeDateTimeFormatter()
        formatter.unitsStyle = .abbreviated
        formatter.dateTimeStyle = .numeric
        return formatter
    }()

    static func string(for date: Date, relativeTo now: Date = Date()) -> String {
        // Clock skew between the server and the phone can put a just-received event a second or
        // two into the future, which would otherwise read "in 2s". Anything that close is "now".
        if date > now && date.timeIntervalSince(now) < 60 {
            return "now"
        }
        return formatter.localizedString(for: date, relativeTo: now)
    }
}
