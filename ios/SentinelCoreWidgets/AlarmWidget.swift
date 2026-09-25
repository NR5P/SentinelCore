import OrganizerKit
import SwiftUI
import WidgetKit

// The alarm at a glance: armed or not, and what's open. Shows the last state the app saw (see
// AlarmWidgetSnapshot); tapping it opens the Alarm section, where arming and disarming live.
// Arming from the widget itself would need the app's login inside the widget process, which is
// exactly what a widget doesn't have.
struct AlarmWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "Alarm", provider: AlarmProvider()) { entry in
            AlarmWidgetView(entry: entry)
                .containerBackground(.fill.tertiary, for: .widget)
                .widgetURL(URL(string: "sentinelcore://alarm")!)
        }
        .configurationDisplayName("Alarm")
        .description("Whether the home alarm is armed, and what's open.")
        .supportedFamilies([.systemSmall, .systemMedium, .accessoryCircular, .accessoryRectangular, .accessoryInline])
    }
}

struct AlarmEntry: TimelineEntry {
    let date: Date
    let snapshot: AlarmWidgetSnapshot
}

struct AlarmProvider: TimelineProvider {
    func placeholder(in context: Context) -> AlarmEntry { AlarmEntry(date: Date(), snapshot: .sample) }

    func getSnapshot(in context: Context, completion: @escaping (AlarmEntry) -> Void) {
        let snapshot = AlarmWidgetStore()?.read() ?? .empty
        completion(AlarmEntry(date: Date(), snapshot: context.isPreview && !snapshot.enabled ? .sample : snapshot))
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<AlarmEntry>) -> Void) {
        let snapshot = AlarmWidgetStore()?.read() ?? .empty
        let now = Date()
        var entries = [AlarmEntry(date: now, snapshot: snapshot)]
        // A delay that runs out changes the word on the widget even if the app never says so.
        for deadline in [snapshot.exitUntil, snapshot.pendingUntil].compactMap({ $0 }) where deadline > now {
            entries.append(AlarmEntry(date: deadline, snapshot: snapshot))
        }
        completion(Timeline(entries: entries, policy: .after(now.addingTimeInterval(30 * 60))))
    }
}

struct AlarmWidgetView: View {
    @Environment(\.widgetFamily) private var family
    let entry: AlarmEntry

    private var snapshot: AlarmWidgetSnapshot { entry.snapshot }

    // Past a deadline the widget assumes the server moved on the way it always does.
    private var status: String {
        if snapshot.status == "arming", let until = snapshot.exitUntil, until <= entry.date { return "armed" }
        if snapshot.status == "pending", let until = snapshot.pendingUntil, until <= entry.date { return "triggered" }
        return snapshot.status
    }

    private var headline: String {
        switch status {
        case "arming": "Arming"
        case "pending": "Entry delay"
        case "triggered": "Alarm!"
        case "armed": "Armed \(snapshot.modeLabel.lowercased())"
        default: "Disarmed"
        }
    }

    private var color: Color {
        switch status {
        case "armed": WidgetTheme.green
        case "arming", "pending": WidgetTheme.amber
        case "triggered": WidgetTheme.red
        default: .secondary
        }
    }

    private var icon: String {
        switch status {
        case "triggered": "light.beacon.max.fill"
        case "disarmed": "lock.open.fill"
        default: "lock.shield.fill"
        }
    }

    private var detail: String {
        if status == "triggered", let by = snapshot.triggeredBy { return by }
        var parts: [String] = []
        if snapshot.openCount > 0 { parts.append("\(snapshot.openCount) open") }
        if snapshot.problemCount > 0 { parts.append("\(snapshot.problemCount) need attention") }
        if parts.isEmpty { parts.append(snapshot.sensorCount == 0 ? "No sensors yet" : "All \(snapshot.sensorCount) sensors closed") }
        return parts.joined(separator: " · ")
    }

    var body: some View {
        if !snapshot.enabled {
            switch family {
            case .accessoryInline: Text("Alarm: open Sentinel Core")
            case .accessoryCircular: Image(systemName: "lock.shield")
            default:
                VStack(spacing: 6) {
                    Image(systemName: "lock.shield").font(.title2).foregroundStyle(.secondary)
                    Text("Open Sentinel Core once to show the alarm here.").font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center)
                }
            }
        } else {
            switch family {
            case .accessoryInline:
                Label(headline, systemImage: icon)
            case .accessoryCircular:
                VStack(spacing: 2) {
                    Image(systemName: icon).font(.title3)
                    Text(status == "disarmed" ? "off" : snapshot.modeLabel.lowercased()).font(.caption2)
                }
                .widgetAccentable()
            case .accessoryRectangular:
                VStack(alignment: .leading, spacing: 2) {
                    Label(headline, systemImage: icon).font(.headline).widgetAccentable()
                    Text(detail).font(.caption).foregroundStyle(.secondary)
                }
            default:
                VStack(alignment: .leading, spacing: 8) {
                    HStack {
                        Image(systemName: icon).font(.title2).foregroundStyle(color)
                        Spacer()
                        Text("ALARM").font(.caption2.weight(.bold)).foregroundStyle(.secondary)
                    }
                    Spacer(minLength: 0)
                    Text(headline)
                        .font(family == .systemSmall ? .title3.weight(.bold) : .title2.weight(.bold))
                        .foregroundStyle(color)
                        .minimumScaleFactor(0.7)
                        .lineLimit(2)
                    Text(detail).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
            }
        }
    }
}
