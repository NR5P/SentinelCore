import AppIntents
import OrganizerKit
import SwiftUI
import WidgetKit

// Open Organizer reminders, overdue first, each with a circle that checks it off right there.
struct OrganizerRemindersWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "OrganizerReminders", provider: OrganizerProvider()) { entry in
            RemindersWidgetView(entry: entry)
                .containerBackground(.fill.tertiary, for: .widget)
                .widgetURL(WidgetPane.reminders.url)
        }
        .configurationDisplayName("Reminders")
        .description("Your open Organizer reminders. Tap the circle to check one off.")
        .supportedFamilies([.systemSmall, .systemMedium, .systemLarge, .accessoryCircular, .accessoryRectangular, .accessoryInline])
    }
}

// Runs in the widget's own process, which can't edit the Organizer: it leaves the check-off for
// the app (OrganizerWidgetStore) and nudges it in case it's running. The reminder leaves every
// widget at once; the app does the real edit, and a repeating one moves on to its next date.
struct CompleteReminderIntent: AppIntent {
    static let title: LocalizedStringResource = "Complete Reminder"
    static let isDiscoverable = false

    @Parameter(title: "Reminder") var reminderId: String

    init() {}

    init(reminderId: String) {
        self.reminderId = reminderId
    }

    func perform() async throws -> some IntentResult {
        guard let store = OrganizerWidgetStore(),
              let reminder = store.readSnapshot().reminders.first(where: { $0.id == reminderId }) else { return .result() }
        store.complete(reminder)
        CFNotificationCenterPostNotification(
            CFNotificationCenterGetDarwinNotifyCenter(), CFNotificationName(OrganizerWidgetStore.actionsPosted as CFString), nil, nil, true)
        WidgetCenter.shared.reloadAllTimelines()
        return .result()
    }
}

struct RemindersWidgetView: View {
    @Environment(\.widgetFamily) private var family
    let entry: OrganizerEntry

    private var reminders: [OrganizerWidgetSnapshot.Reminder] { entry.snapshot.reminders }

    // Overdue or due today - the number worth a glance.
    private var dueCount: Int {
        reminders.filter { $0.isOverdue(at: entry.date) || $0.isDueToday(at: entry.date) }.count
    }

    var body: some View {
        if !entry.snapshot.enabled {
            switch family {
            case .accessoryInline, .accessoryCircular: Image(systemName: "checklist")
            case .accessoryRectangular: Text("Open Sentinel Core to set up the Organizer").font(.caption)
            default: OrganizerOffView(icon: "checklist")
            }
        } else {
            switch family {
            case .accessoryInline:
                Text(dueCount > 0 ? "\(dueCount) reminder\(dueCount == 1 ? "" : "s") due" : "No reminders due")
            case .accessoryCircular: circular
            case .accessoryRectangular: rectangular
            case .systemSmall: list(rows: 4, compact: true)
            case .systemMedium: list(rows: 3, compact: false)
            default: list(rows: 9, compact: false)
            }
        }
    }

    private var circular: some View {
        ZStack {
            AccessoryWidgetBackground()
            VStack(spacing: 0) {
                Image(systemName: "checklist").font(.caption)
                Text("\(dueCount)").font(.title3.weight(.semibold))
            }
        }
        .widgetAccentable()
    }

    private var rectangular: some View {
        VStack(alignment: .leading, spacing: 1) {
            Label(dueCount > 0 ? "\(dueCount) due" : "Reminders", systemImage: "checklist")
                .font(.headline)
                .widgetAccentable()
            if reminders.isEmpty {
                Text("All done").foregroundStyle(.secondary)
            }
            ForEach(reminders.prefix(2)) { Text($0.title).lineLimit(1) }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func list(rows: Int, compact: Bool) -> some View {
        VStack(alignment: .leading, spacing: compact ? 6 : 7) {
            WidgetHeader(title: "Reminders", icon: "checklist", tint: WidgetTheme.amber,
                         trailing: reminders.isEmpty ? nil : "\(reminders.count)")
            if reminders.isEmpty {
                Spacer(minLength: 0)
                HStack {
                    Spacer()
                    VStack(spacing: 4) {
                        Image(systemName: "checkmark.circle").font(.title2).foregroundStyle(WidgetTheme.green)
                        Text("All done").font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                }
            }
            ForEach(reminders.prefix(rows)) { row($0, compact: compact) }
            Spacer(minLength: 0)
        }
    }

    private func row(_ reminder: OrganizerWidgetSnapshot.Reminder, compact: Bool) -> some View {
        HStack(alignment: .top, spacing: 7) {
            Button(intent: CompleteReminderIntent(reminderId: reminder.id)) {
                Image(systemName: "circle")
                    .font(compact ? .callout : .body)
                    .foregroundStyle(reminder.priority > 0 ? WidgetTheme.amber : .secondary)
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Mark \(reminder.title) done")
            VStack(alignment: .leading, spacing: 0) {
                HStack(spacing: 3) {
                    if reminder.priority > 0 {
                        Text(String(repeating: "!", count: min(reminder.priority, 3))).foregroundStyle(WidgetTheme.amber).bold()
                    }
                    Text(reminder.title).lineLimit(1)
                    if reminder.flagged { Image(systemName: "flag.fill").font(.caption2).foregroundStyle(WidgetTheme.amber) }
                }
                .font(compact ? .caption : .subheadline)
                // No room for the date on the small widget, so overdue shows in the title.
                .foregroundStyle(compact && reminder.isOverdue(at: entry.date) ? WidgetTheme.red : .primary)
                if !compact, let due = WidgetText.due(reminder, now: entry.date) {
                    HStack(spacing: 3) {
                        Text(due)
                        if reminder.recurring { Image(systemName: "repeat") }
                    }
                    .font(.caption2)
                    .foregroundStyle(reminder.isOverdue(at: entry.date) ? WidgetTheme.red : .secondary)
                }
            }
            Spacer(minLength: 0)
        }
    }
}

#Preview(as: .systemMedium) {
    OrganizerRemindersWidget()
} timeline: {
    OrganizerEntry(date: Date(), snapshot: .sample)
}
