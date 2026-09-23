import OrganizerKit
import SwiftUI
import WidgetKit

// Up next on the Organizer calendar: today's date and what's coming, holidays included.
struct OrganizerCalendarWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "OrganizerCalendar", provider: OrganizerProvider()) { entry in
            CalendarWidgetView(entry: entry)
                .containerBackground(.fill.tertiary, for: .widget)
                .widgetURL(WidgetPane.calendar.url)
        }
        .configurationDisplayName("Calendar")
        .description("What's next on your Organizer calendar.")
        .supportedFamilies([.systemSmall, .systemMedium, .systemLarge, .accessoryRectangular, .accessoryInline])
    }
}

struct CalendarWidgetView: View {
    @Environment(\.widgetFamily) private var family
    let entry: OrganizerEntry

    private var events: [OrganizerWidgetSnapshot.Event] { entry.snapshot.upcomingEvents(at: entry.date) }

    var body: some View {
        if !entry.snapshot.enabled {
            switch family {
            case .accessoryInline: Text("Organizer not set up")
            case .accessoryRectangular: Text("Open Sentinel Core to set up the Organizer").font(.caption)
            default: OrganizerOffView(icon: "calendar")
            }
        } else {
            switch family {
            case .accessoryInline: inline
            case .accessoryRectangular: rectangular
            case .systemSmall: small
            case .systemMedium: medium
            default: large
            }
        }
    }

    // Lock Screen, one line above the clock.
    @ViewBuilder private var inline: some View {
        if let next = events.first(where: { !$0.holiday }) {
            Text("\(WidgetText.eventTime(next, now: entry.date, withDay: !Calendar.current.isDate(next.start, inSameDayAs: entry.date))) \(next.title)")
        } else {
            Text("No upcoming events")
        }
    }

    @ViewBuilder private var rectangular: some View {
        if let next = events.first(where: { !$0.holiday }) {
            VStack(alignment: .leading, spacing: 1) {
                Text(next.title).font(.headline).lineLimit(1).widgetAccentable()
                Text(WidgetText.eventTime(next, now: entry.date, withDay: true)).lineLimit(1)
                if let location = next.location { Text(location).lineLimit(1).foregroundStyle(.secondary) }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        } else {
            Text("No upcoming events").foregroundStyle(.secondary)
        }
    }

    private var dateBadge: some View {
        VStack(alignment: .leading, spacing: -2) {
            Text(entry.date.formatted(.dateTime.weekday(.wide)).uppercased())
                .font(.caption2.weight(.bold))
                .foregroundStyle(WidgetTheme.red)
                .widgetAccentable()
            Text(entry.date.formatted(.dateTime.day()))
                .font(.system(size: 34, weight: .semibold, design: .rounded))
        }
    }

    private var small: some View {
        VStack(alignment: .leading, spacing: 6) {
            dateBadge
            if events.isEmpty {
                Spacer(minLength: 0)
                Text("Nothing coming up").font(.caption).foregroundStyle(.secondary)
            } else {
                ForEach(events.prefix(2)) { row($0, withDay: !Calendar.current.isDate($0.start, inSameDayAs: entry.date)) }
            }
            Spacer(minLength: 0)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var medium: some View {
        HStack(alignment: .top, spacing: 14) {
            VStack(alignment: .leading) {
                dateBadge
                Spacer(minLength: 0)
                Text(entry.date.formatted(.dateTime.month(.wide))).font(.caption).foregroundStyle(.secondary)
            }
            .frame(width: 88, alignment: .leading)
            VStack(alignment: .leading, spacing: 6) {
                if events.isEmpty {
                    Text("Nothing in the next two weeks").font(.caption).foregroundStyle(.secondary)
                }
                ForEach(events.prefix(4)) { row($0, withDay: !Calendar.current.isDate($0.start, inSameDayAs: entry.date)) }
                Spacer(minLength: 0)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    // An agenda: a heading per day, as many events as fit.
    private var large: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline) {
                Text(entry.date.formatted(.dateTime.weekday(.wide).month(.wide).day()))
                    .font(.headline)
                Spacer()
                Image(systemName: "calendar").foregroundStyle(WidgetTheme.cyan).widgetAccentable()
            }
            if events.isEmpty {
                Spacer()
                Text("Nothing in the next two weeks").font(.callout).foregroundStyle(.secondary).frame(maxWidth: .infinity)
            }
            ForEach(agenda(height: 320), id: \.day) { group in
                Text(WidgetText.day(group.day, now: entry.date).uppercased())
                    .font(.caption2.weight(.bold))
                    .foregroundStyle(Calendar.current.isDate(group.day, inSameDayAs: entry.date) ? WidgetTheme.red : .secondary)
                    .padding(.top, 2)
                ForEach(group.events) { row($0, withDay: false) }
            }
            Spacer(minLength: 0)
        }
    }

    private func row(_ event: OrganizerWidgetSnapshot.Event, withDay: Bool) -> some View {
        HStack(alignment: .top, spacing: 6) {
            RoundedRectangle(cornerRadius: 1.5)
                .fill(WidgetTheme.color(event))
                .frame(width: 3)
            VStack(alignment: .leading, spacing: 0) {
                Text(event.title).font(.caption.weight(.semibold)).lineLimit(1)
                Text(WidgetText.eventTime(event, now: entry.date, withDay: withDay))
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
        }
        .fixedSize(horizontal: false, vertical: true)
    }

    // Upcoming events by the day they're on (an event already under way counts as today), as many
    // as fit in roughly `height` points: a day heading takes about 22, an event about 35.
    private func agenda(height: Double) -> [(day: Date, events: [OrganizerWidgetSnapshot.Event])] {
        let calendar = Calendar.current
        let today = calendar.startOfDay(for: entry.date)
        let heading = 22.0, row = 35.0
        var groups: [(day: Date, events: [OrganizerWidgetSnapshot.Event])] = []
        var used = 0.0
        for event in events {
            let day = max(calendar.startOfDay(for: event.start), today)
            if groups.last?.day != day {
                // A heading only with room for at least one event under it.
                guard used + heading + row <= height else { break }
                groups.append((day, []))
                used += heading
            }
            guard used + row <= height else { break }
            groups[groups.count - 1].events.append(event)
            used += row
        }
        return groups
    }
}

#Preview(as: .systemMedium) {
    OrganizerCalendarWidget()
} timeline: {
    OrganizerEntry(date: Date(), snapshot: .sample)
}
