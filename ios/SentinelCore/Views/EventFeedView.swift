import SwiftUI

// The one list of everything the cameras caught. This used to be two screens - "Feed" for
// AI-analysed alerts and "Triggers" for motion - which meant a camera that forwards to AI
// analysis put the same moment on both, once with the description and once with the clip. The
// server now joins them (see ActivityEndpoints), so a row here is a thing that happened, and it
// carries whatever exists for it: a snapshot always, a clip when one was recorded, a description
// when something actually looked at the frame.
struct EventFeedView: View {
    @EnvironmentObject private var model: AppModel
    @State private var search = ""
    @State private var notifiedOnly = false

    private var filteredEvents: [ActivityItem] {
        model.activity.filter { event in
            // A motion row has no notification decision to satisfy, so filtering on one excludes
            // it - the same rule the server applies to its own analysis-only filters.
            (!notifiedOnly || event.notified) &&
            (search.isEmpty ||
             (event.summary ?? "").localizedCaseInsensitiveContains(search) ||
             event.camera.localizedCaseInsensitiveContains(search) ||
             (event.zoneName ?? "").localizedCaseInsensitiveContains(search) ||
             (event.eventType ?? "").localizedCaseInsensitiveContains(search))
        }
    }

    var body: some View {
        List {
            sectionHeader
                .listRowSeparator(.hidden)
                .listRowInsets(EdgeInsets())
                .listRowBackground(Color.clear)

            if filteredEvents.isEmpty {
                ContentUnavailableView(
                    emptyTitle,
                    systemImage: model.isLoading ? "arrow.triangle.2.circlepath" : "shield.slash",
                    description: Text(emptyDescription)
                )
                .foregroundStyle(.secondary)
                .listRowSeparator(.hidden)
                .listRowBackground(Color.clear)
            } else {
                ForEach(filteredEvents) { event in
                    NavigationLink(value: event) {
                        EventCard(event: event)
                    }
                }
            }

            if let lastError = model.lastError {
                Label(lastError, systemImage: "exclamationmark.triangle.fill")
                    .font(.caption)
                    .foregroundStyle(SentinelTheme.amber)
                    .listRowSeparator(.hidden)
                    .listRowBackground(Color.clear)
            }
        }
        .listStyle(.plain)
        .navigationTitle("Events")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.large)
        #endif
        .navigationDestination(for: ActivityItem.self) { ActivityDetailView(event: $0) }
        .searchable(text: $search, prompt: "Search camera activity")
        .refreshable { await model.connect() }
        .toolbar {
            ToolbarItem {
                Toggle(isOn: $notifiedOnly) {
                    Label("Notified only", systemImage: "bell.fill")
                }
                .toggleStyle(.button)
            }
            // Changing this changes what the server is asked for, so it reloads rather than
            // filtering what's already here.
            ToolbarItem {
                Toggle(isOn: $model.showHiddenActivity) {
                    Label("Show hidden", systemImage: "eye.slash.fill")
                }
                .toggleStyle(.button)
                .onChange(of: model.showHiddenActivity) { _, _ in
                    Task { await model.connect() }
                }
            }
            ToolbarItem {
                Button { Task { await model.connect(resetNetworkSession: true) } } label: {
                    Label("Refresh", systemImage: "arrow.clockwise")
                }
                .disabled(model.isLoading)
            }
        }
        .task {
            while !Task.isCancelled {
                do { try await Task.sleep(for: .seconds(30)) } catch { return }
                guard !Task.isCancelled else { return }
                await model.connect()
            }
        }
    }

    private var sectionHeader: some View {
        HStack {
            VStack(alignment: .leading, spacing: 3) {
                Text("\(filteredEvents.count) events")
                    .foregroundStyle(.secondary)
                    .font(.caption)
                if let lastUpdated = model.lastUpdated {
                    Text("Updated \(lastUpdated.formatted(date: .omitted, time: .shortened))")
                        .foregroundStyle(.secondary.opacity(0.75))
                        .font(.caption2)
                }
            }
            Spacer()
            ConnectionBadge().frame(maxWidth: 180)
        }
        .padding(.horizontal)
        .padding(.bottom, 4)
    }

    private var emptyTitle: String {
        if model.isLoading { return "Loading events…" }
        if !search.isEmpty || notifiedOnly { return "No matching events" }
        return "No events"
    }

    private var emptyDescription: String {
        if !search.isEmpty || notifiedOnly {
            return "Try changing your search or notification filter."
        }
        if !model.showHiddenActivity {
            return "Anything the cameras catch will appear here. Motion the AI rejected is hidden - turn on Show hidden to see it."
        }
        return "Anything the cameras catch will appear here."
    }
}

struct EventCard: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
    let event: ActivityItem

    var body: some View {
        Group {
            if horizontalSizeClass == .compact {
                VStack(alignment: .leading, spacing: 12) {
                    eventImage
                        .aspectRatio(16 / 9, contentMode: .fit)
                    eventDetails
                }
            } else {
                HStack(alignment: .top, spacing: 14) {
                    eventImage
                        .frame(width: 156, height: 96)
                    eventDetails
                }
            }
        }
        .padding(.vertical, 6)
        // The gutter is reserved on every card, not just notified ones, so the bar can sit in
        // it without the snapshot and the text running into its edge - and so a notified card
        // still lines up with the ones above and below it.
        .padding(.leading, 10)
        .overlay(alignment: .leading) {
            if event.notified {
                Rectangle().fill(SentinelTheme.green).frame(width: 3).padding(.vertical, 5)
            }
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel(accessibilityDescription)
    }

    private var eventImage: some View {
        SentinelRemoteImage(url: imageURL)
            .clipShape(RoundedRectangle(cornerRadius: 6))
            .clipped()
            .overlay(alignment: .bottomLeading) {
                clipBadge
            }
    }

    // A card that simply shows nothing where a clip would be cannot be told apart from one whose
    // clip is still being written, or one on a camera that was never recording them - and those
    // want three different reactions. The dashboard has distinguished them since clipState was
    // added; this is the same set, in the same words.
    @ViewBuilder
    private var clipBadge: some View {
        switch event.clip {
        case .ready:
            badge("Clip", icon: "play.fill", tint: SentinelTheme.cyan)
        case .recording:
            badge("Recording…", icon: "record.circle", tint: SentinelTheme.amber)
        case .missing:
            badge("No clip", icon: "exclamationmark.triangle.fill", tint: SentinelTheme.amber)
        case .off, .none:
            // "off" is a deliberate setting and "none" is a row clips never applied to; neither is
            // worth a badge on every card. hasClip covers a server too old to send clipState.
            if event.clip == nil && event.hasClip {
                badge("Clip", icon: "play.fill", tint: SentinelTheme.cyan)
            }
        }
    }

    private func badge(_ text: String, icon: String, tint: Color) -> some View {
        Label(text, systemImage: icon)
            .font(.caption2)
            .padding(.horizontal, 6)
            .padding(.vertical, 3)
            .background(.black.opacity(0.6), in: Capsule())
            .foregroundStyle(tint)
            .padding(6)
    }

    private var eventDetails: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text(event.camera).foregroundStyle(SentinelTheme.green).bold()
                Spacer()
                Text(SentinelRelativeTime.string(for: event.occurredAt))
                    .foregroundStyle(.secondary)
                    .font(.caption)
            }
            // Nothing analysed a motion row, so the zone that caught it stands in for a
            // description - dimmed, so a mixed list reads as "described" vs "not looked at".
            if event.isAnalyzed {
                Text(event.summary?.isEmpty == false ? event.summary! : "No description")
                    .multilineTextAlignment(.leading)
                    .lineLimit(horizontalSizeClass == .compact ? 4 : 3)
            } else {
                Text(event.zoneName ?? "Motion in zone")
                    .italic()
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
            }
            HStack(spacing: 10) {
                Text(footerLabel)
                    .foregroundStyle(event.isAnalyzed ? SentinelTheme.cyan : .secondary)
                    .lineLimit(1)
                // A camera whose frames are never analyzed produces motion rows that look exactly
                // like a camera which has stopped seeing anything worth describing. Saying so on
                // the row is the only place someone would notice.
                if event.isAiAnalysisOff {
                    Text("AI off")
                        .font(.caption2)
                        .padding(.horizontal, 5)
                        .padding(.vertical, 2)
                        .background(SentinelTheme.amber.opacity(0.18), in: Capsule())
                        .foregroundStyle(SentinelTheme.amber)
                        .accessibilityLabel("AI analysis is off for this camera")
                }
                if let confirmation = event.confirmation, confirmation != "confirmed" {
                    Image(systemName: confirmation == "rejected" ? "exclamationmark.triangle.fill" : "ellipsis.circle")
                        .foregroundStyle(SentinelTheme.amber)
                        .accessibilityLabel(confirmation == "rejected" ? "AI found nothing" : "Awaiting AI verdict")
                }
                Spacer(minLength: 4)
                if event.notified {
                    Image(systemName: "bell.fill")
                        .foregroundStyle(SentinelTheme.green)
                        .accessibilityLabel("Notification sent")
                }
                if let confidence = event.confidence, event.isAnalyzed {
                    Text(confidence, format: .percent.precision(.fractionLength(0)))
                        .foregroundStyle(.secondary)
                }
            }
            .font(.caption)
        }
    }

    private var footerLabel: String {
        guard event.isAnalyzed else { return "Motion only" }
        return (event.eventType ?? "unknown").replacingOccurrences(of: "_", with: " ").capitalized
    }

    private var accessibilityDescription: String {
        let detail = event.isAnalyzed ? (event.summary ?? "") : "Motion only, \(event.zoneName ?? "in zone")"
        let aiOff = event.isAiAnalysisOff ? ", AI analysis off for this camera" : ""
        return "\(event.camera), \(detail)\(aiOff), \(event.occurredAt.formatted())"
    }

    private var imageURL: URL? {
        event.imageUrl.flatMap { model.client.url(path: $0) }
    }
}
