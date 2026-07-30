import SwiftUI

// Simplified tvOS port of ios/SentinelCore/Views/EventFeedView.swift - no search field (typing
// on a Siri Remote is painful), just the "Notified only" filter. Tapping a card reuses the same
// model.selectedEvent push-detail path the alert-takeover flow drives, instead of a separate
// navigationDestination(for:).
struct AlertFeedView: View {
    @EnvironmentObject private var model: AppModel
    @State private var notifiedOnly = false

    private var filteredEvents: [SecurityEvent] {
        model.events.filter { !notifiedOnly || $0.notified }
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
                        Button { model.selectedEvent = event } label: {
                            AlertCard(event: event)
                        }
                        .buttonStyle(.card)
                    }
                }
            }
            .padding(.horizontal, 60)
            .padding(.bottom, 60)
        }
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
            ToolbarItem {
                Button { Task { await model.connect(resetNetworkSession: true) } } label: {
                    Image(systemName: "arrow.clockwise").accessibilityLabel("Refresh")
                }
                .disabled(model.isLoading)
            }
        }
        .task {
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(30))
                await model.connect()
            }
        }
    }

    private var sectionHeader: some View {
        HStack {
            VStack(alignment: .leading, spacing: 4) {
                Text("\(filteredEvents.count) alerts").font(.headline).foregroundStyle(.secondary)
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
        if model.isLoading { return "Loading alerts…" }
        if notifiedOnly { return "No matching alerts" }
        return "No alerts"
    }

    private var emptyDescription: String {
        if notifiedOnly { return "Try turning off the notification filter." }
        return "AI-verified camera activity will appear here."
    }
}

private struct AlertCard: View {
    let event: SecurityEvent
    @EnvironmentObject private var model: AppModel

    var body: some View {
        HStack(alignment: .top, spacing: 24) {
            SentinelRemoteImage(url: imageURL)
                .frame(width: 260, height: 146)
                .clipShape(RoundedRectangle(cornerRadius: 10))
                .clipped()

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
                Text(event.summary.isEmpty ? "No description" : event.summary)
                    .lineLimit(3)
                HStack(spacing: 14) {
                    Text(event.eventType.replacingOccurrences(of: "_", with: " ").capitalized)
                        .foregroundStyle(SentinelTheme.cyan)
                    if event.notified {
                        Label("Notified", systemImage: "bell.fill").foregroundStyle(SentinelTheme.green)
                    }
                    Spacer()
                    Text(event.confidence, format: .percent.precision(.fractionLength(0)))
                        .foregroundStyle(.secondary)
                }
                .font(.callout)
            }
        }
        .padding(20)
    }

    private var imageURL: URL? {
        event.hasImage ? model.client.url(path: "/api/events/\(event.id.uuidString)/image") : nil
    }
}
