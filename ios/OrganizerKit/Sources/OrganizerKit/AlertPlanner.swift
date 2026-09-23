import Foundation

// Which reminder and event alerts this device should have scheduled as local notifications.
// Local rather than pushed so they fire with no signal and with the server down - the whole point
// of keeping the Organizer on the device.
//
// iOS keeps at most 64 pending local notifications per app, so this plans only the nearest ones
// and the app re-plans after every sync.
public struct PlannedAlert: Hashable, Sendable, Identifiable {
    public var id: String
    public var itemId: String
    public var fireDate: Date
    public var title: String
    public var body: String
}

public enum AlertPlanner {
    // All-day things alert relative to this time on their day, as Apple Calendar and Reminders do.
    public static let allDayHour = 9

    public static func plan(
        items: [OrganizerItem], now: Date = Date(), horizon: TimeInterval = 14 * 86_400, limit: Int = 48,
        calendar: Calendar = .current
    ) -> [PlannedAlert] {
        let end = now.addingTimeInterval(horizon)
        var alerts: [PlannedAlert] = []

        func base(_ date: Date, allDay: Bool) -> Date {
            allDay ? calendar.date(bySettingHour: allDayHour, minute: 0, second: 0, of: date) ?? date : date
        }

        func minutes(_ item: OrganizerItem) -> [Int] {
            (item.fields["alerts"]?.array ?? []).compactMap(\.int)
        }

        let timeFormat = Date.FormatStyle(date: .omitted, time: .shortened, timeZone: calendar.timeZone)
        for item in items where !item.deleted {
            switch item.kind {
            case .event:
                let offsets = minutes(item)
                guard !offsets.isEmpty else { continue }
                let latest = end.addingTimeInterval(TimeInterval((offsets.max() ?? 0) * 60))
                for occurrence in Recurrence.occurrences(of: item, from: now, to: latest, calendar: calendar) {
                    let allDay = item.bool("allDay")
                    for offset in offsets {
                        let fire = base(occurrence.start, allDay: allDay).addingTimeInterval(TimeInterval(-offset * 60))
                        guard fire > now, fire <= end else { continue }
                        var body = allDay ? "All day" : occurrence.start.formatted(timeFormat)
                        if let location = item.string("location"), !location.isEmpty { body += " · \(location)" }
                        alerts.append(PlannedAlert(
                            id: "organizer.\(item.id).\(Int64(fire.timeIntervalSince1970)).\(offset)",
                            itemId: item.id, fireDate: fire, title: item.title.isEmpty ? "Event" : item.title, body: body))
                    }
                }
            case .reminder:
                guard !item.bool("completed"),
                      let due = OrganizerDates.parse(item.string("due"), allDay: item.bool("allDay"), calendar: calendar) else { continue }
                // A reminder with a due date alerts at that time unless told otherwise - that is
                // what setting a due date on a reminder is for.
                let offsets = minutes(item).isEmpty ? [0] : minutes(item)
                for offset in offsets {
                    let fire = base(due, allDay: item.bool("allDay")).addingTimeInterval(TimeInterval(-offset * 60))
                    guard fire > now, fire <= end else { continue }
                    alerts.append(PlannedAlert(
                        id: "organizer.\(item.id).\(Int64(fire.timeIntervalSince1970)).\(offset)",
                        itemId: item.id, fireDate: fire, title: item.title.isEmpty ? "Reminder" : item.title,
                        body: item.string("notes").flatMap { $0.split(separator: "\n").first.map(String.init) } ?? "Reminder"))
                }
            default:
                continue
            }
        }
        return Array(alerts.sorted { $0.fireDate < $1.fireDate }.prefix(limit))
    }
}
