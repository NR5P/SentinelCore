import SwiftUI
import WidgetKit

// Quick Add: buttons that open the app straight onto its Quick Add sheet (QuickCaptureSheet), to
// say or type "add a reminder to take out the trash at 7am tomorrow" and have it land in the
// Organizer as a reminder, note or event. A widget can't listen or take typing itself, so every
// button is a link into the app - sentinelcore://capture, handled in RootView.
//
// Both widget extensions compile this file: SentinelCoreWidgets on iPhone and iPad, and
// SentinelCoreMacWidgets on the Mac. It reads nothing from the app, so it needs no App Group.
struct QuickAddWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "QuickAdd", provider: QuickAddProvider()) { _ in
            QuickAddView()
                .containerBackground(.fill.tertiary, for: .widget)
        }
        .configurationDisplayName("Quick Add")
        .description("Say or type a reminder, note or event, like \u{201C}take out the trash at 7am tomorrow\u{201D}.")
        #if os(iOS)
        .supportedFamilies([.systemSmall, .systemMedium, .accessoryCircular, .accessoryRectangular])
        #else
        .supportedFamilies([.systemSmall, .systemMedium])
        #endif
    }
}

struct QuickAddEntry: TimelineEntry {
    let date: Date
}

// Nothing changes, so one entry for good.
struct QuickAddProvider: TimelineProvider {
    func placeholder(in context: Context) -> QuickAddEntry { QuickAddEntry(date: Date()) }

    func getSnapshot(in context: Context, completion: @escaping (QuickAddEntry) -> Void) {
        completion(QuickAddEntry(date: Date()))
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<QuickAddEntry>) -> Void) {
        completion(Timeline(entries: [QuickAddEntry(date: Date())], policy: .never))
    }
}

enum QuickAddLink {
    case voice, type

    // On a phone the microphone comes first; at a Mac, the keyboard. The sheet has both either way.
    #if os(iOS)
    static let preferred = QuickAddLink.voice
    #else
    static let preferred = QuickAddLink.type
    #endif

    func url(kind: String? = nil) -> URL {
        var components = URLComponents()
        components.scheme = "sentinelcore"
        components.host = "capture"
        components.queryItems = [URLQueryItem(name: "mode", value: self == .voice ? "voice" : "type")]
        if let kind { components.queryItems?.append(URLQueryItem(name: "kind", value: kind)) }
        return components.url!
    }
}

struct QuickAddView: View {
    @Environment(\.widgetFamily) private var family

    // The app's green (SentinelTheme.green), without UIKit or AppKit so both extensions share it.
    private static let green = Color(red: 0.13, green: 0.80, blue: 0.48)

    var body: some View {
        switch family {
        #if os(iOS)
        case .accessoryCircular:
            ZStack {
                AccessoryWidgetBackground()
                Image(systemName: "mic.fill").font(.title2).widgetAccentable()
            }
            .widgetURL(QuickAddLink.voice.url())
            .accessibilityLabel("Quick Add by voice")
        case .accessoryRectangular:
            HStack(spacing: 8) {
                Image(systemName: "mic.circle.fill").font(.title).widgetAccentable()
                VStack(alignment: .leading, spacing: 0) {
                    Text("Quick Add").font(.headline).widgetAccentable()
                    Text("Reminder, note or event").font(.caption).foregroundStyle(.secondary)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .widgetURL(QuickAddLink.voice.url())
        #endif
        case .systemMedium:
            medium
        default:
            small
        }
    }

    // One button: the whole widget.
    private var small: some View {
        VStack(spacing: 8) {
            header
            Spacer(minLength: 0)
            Image(systemName: QuickAddLink.preferred == .voice ? "mic.circle.fill" : "square.and.pencil.circle.fill")
                .font(.system(size: 52))
                .foregroundStyle(Self.green)
            Text(QuickAddLink.preferred == .voice ? "Tap and say it" : "Click and type it")
                .font(.caption)
                .foregroundStyle(.secondary)
            Spacer(minLength: 0)
        }
        .widgetURL(QuickAddLink.preferred.url())
    }

    private var medium: some View {
        VStack(alignment: .leading, spacing: 8) {
            header
            Spacer(minLength: 0)
            HStack(spacing: 12) {
                Link(destination: QuickAddLink.voice.url()) {
                    Image(systemName: "mic.circle.fill")
                        .font(.system(size: 54))
                        .foregroundStyle(Self.green)
                }
                .accessibilityLabel("Say it")
                VStack(alignment: .leading, spacing: 8) {
                    Link(destination: QuickAddLink.type.url()) {
                        HStack(spacing: 6) {
                            Image(systemName: "keyboard")
                            Text("Type it\u{2026}")
                            Spacer(minLength: 0)
                        }
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                        .padding(.horizontal, 10)
                        .padding(.vertical, 7)
                        .background(Color.primary.opacity(0.08), in: Capsule())
                    }
                    HStack(spacing: 6) {
                        chip("Reminder", icon: "checklist", kind: "reminder")
                        chip("Note", icon: "note.text", kind: "note")
                        chip("Event", icon: "calendar", kind: "event")
                    }
                }
            }
            Spacer(minLength: 0)
        }
    }

    private var header: some View {
        HStack(spacing: 4) {
            Image(systemName: "plus.circle.fill").foregroundStyle(Self.green)
            Text("Quick Add").font(.caption.weight(.semibold))
            Spacer(minLength: 0)
        }
    }

    // Straight to that kind, so whatever is said is taken as one.
    private func chip(_ title: String, icon: String, kind: String) -> some View {
        Link(destination: QuickAddLink.preferred.url(kind: kind)) {
            Label(title, systemImage: icon)
                .labelStyle(.titleAndIcon)
                .font(.caption2.weight(.medium))
                .lineLimit(1)
                .padding(.horizontal, 7)
                .padding(.vertical, 5)
                .background(Self.green.opacity(0.18), in: Capsule())
        }
    }
}

#Preview(as: .systemMedium) {
    QuickAddWidget()
} timeline: {
    QuickAddEntry(date: Date())
}
