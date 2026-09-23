import OrganizerKit
import SwiftUI
import WidgetKit

// Organizer notes, pinned ones first, then the most recently changed.
struct OrganizerNotesWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "OrganizerNotes", provider: OrganizerProvider()) { entry in
            NotesWidgetView(entry: entry)
                .containerBackground(.fill.tertiary, for: .widget)
                .widgetURL(WidgetPane.notes.url)
        }
        .configurationDisplayName("Notes")
        .description("Your pinned and latest Organizer notes.")
        .supportedFamilies([.systemSmall, .systemMedium, .systemLarge, .accessoryRectangular])
    }
}

struct NotesWidgetView: View {
    @Environment(\.widgetFamily) private var family
    let entry: OrganizerEntry

    private var notes: [OrganizerWidgetSnapshot.Note] { entry.snapshot.notes }

    var body: some View {
        if !entry.snapshot.enabled {
            if family == .accessoryRectangular {
                Text("Open Sentinel Core to set up the Organizer").font(.caption)
            } else {
                OrganizerOffView(icon: "note.text")
            }
        } else {
            switch family {
            case .accessoryRectangular: rectangular
            case .systemSmall: small
            case .systemMedium: list(count: 3, previewLines: 1)
            default: list(count: 5, previewLines: 2)
            }
        }
    }

    private var rectangular: some View {
        VStack(alignment: .leading, spacing: 1) {
            if let note = notes.first {
                Text(title(note)).font(.headline).lineLimit(1).widgetAccentable()
                Text(note.preview).lineLimit(2).foregroundStyle(.secondary)
            } else {
                Text("No notes").foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    // One note, as much of it as fits.
    private var small: some View {
        VStack(alignment: .leading, spacing: 4) {
            WidgetHeader(title: "Notes", icon: "note.text", tint: WidgetTheme.green)
            if let note = notes.first {
                HStack(spacing: 3) {
                    if note.pinned { Image(systemName: "pin.fill").font(.caption2).foregroundStyle(WidgetTheme.amber) }
                    Text(title(note)).font(.subheadline.weight(.semibold)).lineLimit(2)
                }
                Text(note.preview).font(.caption).foregroundStyle(.secondary).lineLimit(5)
            } else {
                empty
            }
            Spacer(minLength: 0)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func list(count: Int, previewLines: Int) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            WidgetHeader(title: "Notes", icon: "note.text", tint: WidgetTheme.green)
            if notes.isEmpty { empty }
            ForEach(Array(notes.prefix(count).enumerated()), id: \.element.id) { index, note in
                if index > 0 { Divider() }
                VStack(alignment: .leading, spacing: 1) {
                    HStack(spacing: 3) {
                        if note.pinned { Image(systemName: "pin.fill").font(.caption2).foregroundStyle(WidgetTheme.amber) }
                        Text(title(note)).font(.caption.weight(.semibold)).lineLimit(1)
                        Spacer(minLength: 0)
                        if let updated = note.updatedAt {
                            Text(WidgetText.day(updated, now: entry.date)).font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                    if !note.preview.isEmpty {
                        Text(note.preview.replacingOccurrences(of: "\n", with: " · "))
                            .font(.caption2)
                            .foregroundStyle(.secondary)
                            .lineLimit(previewLines)
                    }
                }
            }
            Spacer(minLength: 0)
        }
    }

    private var empty: some View {
        Text("No notes yet").font(.caption).foregroundStyle(.secondary)
    }

    private func title(_ note: OrganizerWidgetSnapshot.Note) -> String {
        if !note.title.isEmpty { return note.title }
        return note.preview.split(separator: "\n").first.map(String.init) ?? "Untitled"
    }
}

#Preview(as: .systemMedium) {
    OrganizerNotesWidget()
} timeline: {
    OrganizerEntry(date: Date(), snapshot: .sample)
}
