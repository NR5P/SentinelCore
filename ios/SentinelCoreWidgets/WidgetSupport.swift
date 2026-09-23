import OrganizerKit
import SwiftUI
import UIKit
import WidgetKit

// What every Organizer widget shares: reading the snapshot the app leaves in the App Group
// container (OrganizerWidgetStore), a timeline that redraws when an event starts or ends or a
// reminder falls due, the app's colours, and the links back into the app.

struct OrganizerEntry: TimelineEntry {
    let date: Date
    let snapshot: OrganizerWidgetSnapshot
}

struct OrganizerProvider: TimelineProvider {
    func placeholder(in context: Context) -> OrganizerEntry {
        OrganizerEntry(date: Date(), snapshot: .sample)
    }

    func getSnapshot(in context: Context, completion: @escaping (OrganizerEntry) -> Void) {
        let snapshot = OrganizerWidgetStore()?.readSnapshot() ?? .empty
        // The widget gallery shows something worth picking even before the app has run.
        completion(OrganizerEntry(date: Date(), snapshot: context.isPreview && !snapshot.enabled ? .sample : snapshot))
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<OrganizerEntry>) -> Void) {
        let snapshot = OrganizerWidgetStore()?.readSnapshot() ?? .empty
        let now = Date()
        let horizon = now.addingTimeInterval(12 * 3600)
        var moments: Set<Date> = [now]
        for event in snapshot.events {
            for moment in [event.start, event.end] where moment > now && moment < horizon { moments.insert(moment) }
        }
        for reminder in snapshot.reminders {
            if let due = reminder.due, due > now, due < horizon { moments.insert(due) }
        }
        if let midnight = Calendar.current.date(byAdding: .day, value: 1, to: Calendar.current.startOfDay(for: now)), midnight < horizon {
            moments.insert(midnight)
        }
        let entries = moments.sorted().prefix(40).map { OrganizerEntry(date: $0, snapshot: snapshot) }
        // The app asks for a reload whenever its copy changes; this only keeps the clock moving.
        completion(Timeline(entries: Array(entries), policy: .after(horizon)))
    }
}

enum WidgetPane: String {
    case calendar, reminders, notes

    // Opens the app on this pane of the Organizer - RootView handles it.
    var url: URL { URL(string: "sentinelcore://organizer/\(rawValue)")! }
}

enum WidgetTheme {
    static let green = Color(light: UIColor(red: 0.02, green: 0.55, blue: 0.32, alpha: 1), dark: UIColor(red: 0.22, green: 1.0, blue: 0.61, alpha: 1))
    static let cyan = Color(light: UIColor(red: 0.0, green: 0.55, blue: 0.62, alpha: 1), dark: UIColor(red: 0.29, green: 0.91, blue: 1.0, alpha: 1))
    static let amber = Color(light: UIColor(red: 0.62, green: 0.42, blue: 0.0, alpha: 1), dark: UIColor(red: 1.0, green: 0.71, blue: 0.27, alpha: 1))
    static let red = Color(light: UIColor(red: 0.75, green: 0.11, blue: 0.17, alpha: 1), dark: UIColor(red: 1.0, green: 0.30, blue: 0.37, alpha: 1))

    // The app's space palette (OrganizerPalette), in the same order.
    static let palette: [Color] = [green, cyan, amber, .purple, .pink, .blue, .orange]

    static func color(_ event: OrganizerWidgetSnapshot.Event) -> Color {
        if let hex = event.color, let color = Color(hex: hex) { return color }
        return palette[event.paletteIndex % palette.count]
    }
}

extension Color {
    init(light: UIColor, dark: UIColor) {
        self.init(uiColor: UIColor { $0.userInterfaceStyle == .dark ? dark : light })
    }

    init?(hex: String) {
        var text = hex.trimmingCharacters(in: .whitespaces)
        if text.hasPrefix("#") { text.removeFirst() }
        guard text.count == 6, let value = UInt32(text, radix: 16) else { return nil }
        self.init(red: Double((value >> 16) & 0xFF) / 255, green: Double((value >> 8) & 0xFF) / 255, blue: Double(value & 0xFF) / 255)
    }
}

enum WidgetText {
    // "Today", "Tomorrow", "Yesterday", "Fri", or "Oct 3" further out.
    static func day(_ date: Date, now: Date) -> String {
        let calendar = Calendar.current
        if calendar.isDate(date, inSameDayAs: now) { return "Today" }
        let days = calendar.dateComponents([.day], from: calendar.startOfDay(for: now), to: calendar.startOfDay(for: date)).day ?? 0
        switch days {
        case 1: return "Tomorrow"
        case -1: return "Yesterday"
        case 2..<7: return date.formatted(.dateTime.weekday(.abbreviated))
        default: return date.formatted(.dateTime.month(.abbreviated).day())
        }
    }

    static func time(_ date: Date) -> String { date.formatted(date: .omitted, time: .shortened) }

    // An event's time as its row shows it: on a day heading already, so no day unless asked.
    static func eventTime(_ event: OrganizerWidgetSnapshot.Event, now: Date, withDay: Bool) -> String {
        let prefix = withDay ? day(max(event.start, Calendar.current.startOfDay(for: now)), now: now) : ""
        if event.allDay { return withDay ? prefix : "All day" }
        if event.start <= now { return "Now – \(time(event.end))" }
        return withDay ? "\(prefix) \(time(event.start))" : time(event.start)
    }

    static func due(_ reminder: OrganizerWidgetSnapshot.Reminder, now: Date) -> String? {
        guard let due = reminder.due else { return nil }
        return reminder.allDay ? day(due, now: now) : "\(day(due, now: now)) \(time(due))"
    }
}

// Shown in place of the content when there is nothing the app has shared yet.
struct OrganizerOffView: View {
    let icon: String

    var body: some View {
        VStack(spacing: 6) {
            Image(systemName: icon).font(.title2).foregroundStyle(WidgetTheme.green)
            Text("Open Sentinel Core to set up the Organizer")
                .font(.caption)
                .multilineTextAlignment(.center)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

struct WidgetHeader: View {
    let title: String
    let icon: String
    let tint: Color
    var trailing: String?

    var body: some View {
        HStack(spacing: 5) {
            Image(systemName: icon)
            Text(title)
            Spacer(minLength: 0)
            if let trailing { Text(trailing).foregroundStyle(.secondary) }
        }
        .font(.caption.weight(.semibold))
        .foregroundStyle(tint)
        .widgetAccentable()
        .lineLimit(1)
    }
}

extension OrganizerWidgetSnapshot {
    // For the widget gallery and placeholders, before the app has written anything real.
    static var sample: OrganizerWidgetSnapshot {
        let now = Date()
        let calendar = Calendar.current
        let today = calendar.startOfDay(for: now)
        func hour(_ day: Int, _ hour: Double) -> Date {
            (calendar.date(byAdding: .day, value: day, to: today) ?? today).addingTimeInterval(hour * 3600)
        }
        let nextHour = calendar.date(bySetting: .minute, value: 0, of: now.addingTimeInterval(3600)) ?? now
        return OrganizerWidgetSnapshot(
            generatedAt: now, enabled: true,
            events: [
                .init(id: "e1", itemId: "e1", title: "Dentist", start: nextHour, end: nextHour.addingTimeInterval(3600), allDay: false,
                      location: "Main St", color: nil, paletteIndex: 1, holiday: false),
                .init(id: "e2", itemId: "e2", title: "Soccer practice", start: hour(1, 17.5), end: hour(1, 19), allDay: false,
                      location: nil, color: nil, paletteIndex: 0, holiday: false),
                .init(id: "e3", itemId: "e3", title: "Grandma's birthday", start: hour(3, 0), end: hour(4, 0), allDay: true,
                      location: nil, color: nil, paletteIndex: 4, holiday: false),
            ],
            reminders: [
                .init(id: "r1", title: "Take out the bins", due: hour(0, 19), dueField: nil, allDay: false, flagged: false, priority: 0, recurring: true),
                .init(id: "r2", title: "Renew registration", due: hour(2, 0), dueField: nil, allDay: true, flagged: true, priority: 2, recurring: false),
                .init(id: "r3", title: "Call the plumber", due: nil, dueField: nil, allDay: false, flagged: false, priority: 0, recurring: false),
            ],
            notes: [
                .init(id: "n1", title: "Wi-Fi for guests", preview: "Network: Home-Guest", pinned: true, updatedAt: now),
                .init(id: "n2", title: "Groceries", preview: "Milk\nEggs\nCoffee", pinned: false, updatedAt: now),
            ])
    }
}
