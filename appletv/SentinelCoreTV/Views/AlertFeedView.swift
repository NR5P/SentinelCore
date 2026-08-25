import SwiftUI

// tvOS port of ios/SentinelCore/Views/EventFeedView.swift - no search field (typing on a Siri
// Remote is painful), just the "Notified only" and "Show hidden" filters.
//
// This used to be two tabs, Alerts and Triggers, which put a camera that forwards to AI analysis
// on both - once with the description, once with the clip. The server now joins them (see
// ActivityEndpoints), so a row here is a thing that happened, carrying whatever exists for it.
struct AlertFeedView: View {
    @EnvironmentObject private var model: AppModel
    @State private var notifiedOnly = false

    private var filteredEvents: [ActivityItem] {
        // A motion row has no notification decision to satisfy, so filtering on one excludes it -
        // the same rule the server applies to its own analysis-only filters.
        model.activity.filter { !notifiedOnly || $0.notified }
    }

    var body: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 24) {
                sectionHeader

                if filteredEvents.isEmpty {
                    ContentUnavailableView(
                        emptyTitle,
                        systemImage: model.isLoading ? "arrow.triangle.2.circlepath" : "shield.slash",
                        description: Text(emptyDescription)
                    )
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity)
                    .padding(.top, 60)
                } else {
                    ForEach(filteredEvents) { event in
                        NavigationLink(value: event) {
                            AlertCard(event: event)
                        }
                        .buttonStyle(.card)
                    }
                }
            }
            .padding(.horizontal, 60)
            .padding(.bottom, 60)
        }
        .navigationDestination(for: ActivityItem.self) { ActivityDetailView(event: $0) }
        .toolbar {
            ToolbarItem {
                // ToggleStyle.button isn't available on tvOS - a plain Button flipping the
                // filter directly gives the same one-click behavior in a toolbar item.
                Button {
                    notifiedOnly.toggle()
                } label: {
                    // A bare Image rather than a Label: tvOS gives a toolbar item far less
                    // width than these titles need and truncates them mid-word, and
                    // .labelStyle(.iconOnly) doesn't survive the toolbar's own styling.
                    Image(systemName: notifiedOnly ? "bell.fill" : "bell")
                        .accessibilityLabel("Notified only")
                }
            }
            // Changing this changes what the server is asked for, so it reloads rather than
            // filtering what's already here.
            ToolbarItem {
                Button {
                    model.showHiddenActivity.toggle()
                    Task { await model.connect() }
                } label: {
                    Image(systemName: model.showHiddenActivity ? "eye.slash.fill" : "eye.slash")
                        .accessibilityLabel("Show hidden")
                }
            }
            ToolbarItem {
                Button { Task { await model.connect(resetNetworkSession: true) } } label: {
                    Image(systemName: "arrow.clockwise").accessibilityLabel("Refresh")
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
            VStack(alignment: .leading, spacing: 4) {
                Text("\(filteredEvents.count) events").font(.headline).foregroundStyle(.secondary)
                if let lastUpdated = model.lastUpdated {
                    Text("Updated \(lastUpdated.formatted(date: .omitted, time: .shortened))")
                        .foregroundStyle(.secondary.opacity(0.75))
                        .font(.caption)
                }
            }
            Spacer()
            ConnectionBadge()
        }
    }

    private var emptyTitle: String {
        if model.isLoading { return "Loading events…" }
        if notifiedOnly { return "No matching events" }
        return "No events"
    }

    private var emptyDescription: String {
        if notifiedOnly { return "Try turning off the notification filter." }
        if !model.showHiddenActivity {
            return "Anything the cameras catch will appear here. Motion the AI rejected is hidden - turn on Show hidden to see it."
        }
        return "Anything the cameras catch will appear here."
    }
}

private struct AlertCard: View {
    let event: ActivityItem
    @EnvironmentObject private var model: AppModel

    var body: some View {
        HStack(alignment: .top, spacing: 24) {
            SentinelRemoteImage(url: imageURL)
                .frame(width: 260, height: 146)
                .clipShape(RoundedRectangle(cornerRadius: 10))
                .clipped()
                .overlay(alignment: .bottomLeading) {
                    clipBadge
                }

            VStack(alignment: .leading, spacing: 10) {
                HStack {
                    Text(event.camera).foregroundStyle(SentinelTheme.green).bold()
                    Spacer()
                    // "5 minutes ago" rather than style: .relative's "5 min, 24 sec", which
                    // reads as a duration with no direction and puts second-level precision on
                    // a screen being read from across the room.
                    Text(event.occurredAt.formatted(.relative(presentation: .named)))
                        .foregroundStyle(.secondary)
                }
                // Nothing analysed a motion row, so the zone that caught it stands in for a
                // description - dimmed, so a mixed list reads as "described" vs "not looked at".
                if event.isAnalyzed {
                    Text(event.summary?.isEmpty == false ? event.summary! : "No description")
                        .lineLimit(3)
                } else {
                    Text(event.zoneName ?? "Motion in zone")
                        .italic()
                        .foregroundStyle(.secondary)
                        .lineLimit(2)
                }
                HStack(spacing: 14) {
                    Text(footerLabel)
                        .foregroundStyle(event.isAnalyzed ? SentinelTheme.cyan : .secondary)
                    // A camera whose frames are never analyzed produces motion rows that look
                    // exactly like a camera which has stopped seeing anything worth describing.
                    if event.isAiAnalysisOff {
                        Label("AI off", systemImage: "eye.slash")
                            .foregroundStyle(SentinelTheme.amber)
                    }
                    if event.notified {
                        Label("Notified", systemImage: "bell.fill").foregroundStyle(SentinelTheme.green)
                    }
                    if let confirmation = event.confirmation, confirmation != "confirmed" {
                        Label(
                            confirmation == "rejected" ? "Nothing found" : "Checking",
                            systemImage: confirmation == "rejected" ? "exclamationmark.triangle.fill" : "ellipsis.circle"
                        )
                        .foregroundStyle(SentinelTheme.amber)
                    }
                    Spacer()
                    if let confidence = event.confidence, event.isAnalyzed {
                        Text(confidence, format: .percent.precision(.fractionLength(0)))
                            .foregroundStyle(.secondary)
                    }
                }
                .font(.callout)
            }
        }
        .padding(20)
    }

    // Showing nothing where a clip would be cannot be told apart from a clip still being written,
    // or a camera that was never recording them - and those want different reactions. Same set of
    // states the dashboard has distinguished since clipState was added.
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
            // "off" is a deliberate setting and "none" never applied to this row; neither earns a
            // badge on every card. hasClip covers a server too old to send clipState.
            if event.clip == nil && event.hasClip {
                badge("Clip", icon: "play.fill", tint: SentinelTheme.cyan)
            }
        }
    }

    private func badge(_ text: String, icon: String, tint: Color) -> some View {
        Label(text, systemImage: icon)
            .font(.caption2)
            .padding(.horizontal, 8)
            .padding(.vertical, 4)
            .background(.black.opacity(0.6), in: Capsule())
            .foregroundStyle(tint)
            .padding(8)
    }

    private var footerLabel: String {
        guard event.isAnalyzed else { return "Motion only" }
        return (event.eventType ?? "unknown").replacingOccurrences(of: "_", with: " ").capitalized
    }

    private var imageURL: URL? {
        event.imageUrl.flatMap { model.client.url(path: $0) }
    }
}
