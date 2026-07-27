import SwiftUI

struct EventFeedView: View {
    @EnvironmentObject private var model: AppModel
    @State private var search = ""
    @State private var notifiedOnly = false

    private var filteredEvents: [SecurityEvent] {
        model.events.filter { event in
            (!notifiedOnly || event.notified) &&
            (search.isEmpty ||
             event.summary.localizedCaseInsensitiveContains(search) ||
             event.camera.localizedCaseInsensitiveContains(search) ||
             event.eventType.localizedCaseInsensitiveContains(search))
        }
    }

    var body: some View {
        ScrollView {
            LazyVStack(spacing: 12) {
                sectionHeader
                if filteredEvents.isEmpty {
                    ContentUnavailableView(
                        emptyTitle,
                        systemImage: model.isLoading ? "arrow.triangle.2.circlepath" : "shield.slash",
                        description: Text(emptyDescription)
                    )
                    .foregroundStyle(SentinelTheme.textDim)
                    .padding(.top, 60)
                } else {
                    ForEach(filteredEvents) { event in
                        NavigationLink(value: event) {
                            EventCard(event: event)
                        }
                        .buttonStyle(.plain)
                    }
                }
                if let lastError = model.lastError {
                    Label(lastError, systemImage: "exclamationmark.triangle.fill")
                        .font(.caption)
                        .foregroundStyle(SentinelTheme.amber)
                        .sentinelPanel()
                }
            }
            .padding()
            .frame(maxWidth: 1000)
            .frame(maxWidth: .infinity)
        }
        .background(SentinelTheme.background)
        .navigationTitle("EVENT_FEED")
        .navigationDestination(for: SecurityEvent.self) { EventDetailView(event: $0) }
        .searchable(text: $search, prompt: "Search camera activity")
        .refreshable { await model.connect() }
        .toolbar {
            ToolbarItem {
                Toggle(isOn: $notifiedOnly) {
                    Label("Notified only", systemImage: "bell.fill")
                }
                .toggleStyle(.button)
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
                try? await Task.sleep(for: .seconds(30))
                await model.connect()
            }
        }
    }

    private var sectionHeader: some View {
        HStack {
            VStack(alignment: .leading, spacing: 3) {
                Text("\(filteredEvents.count) EVENTS")
                    .foregroundStyle(SentinelTheme.textDim)
                    .font(.caption)
                if let lastUpdated = model.lastUpdated {
                    Text("UPDATED \(lastUpdated.formatted(date: .omitted, time: .shortened))")
                        .foregroundStyle(SentinelTheme.textDim.opacity(0.75))
                        .font(.caption2)
                }
            }
            Spacer()
            ConnectionBadge().frame(maxWidth: 180)
        }
        .padding(.bottom, 4)
    }

    private var emptyTitle: String {
        if model.isLoading { return "Loading feed…" }
        if !search.isEmpty || notifiedOnly { return "No matching events" }
        return "No events"
    }

    private var emptyDescription: String {
        if !search.isEmpty || notifiedOnly {
            return "Try changing your search or notification filter."
        }
        return "Alerts received by Sentinel Core will appear here."
    }
}

struct EventCard: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
    let event: SecurityEvent

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
        .sentinelPanel()
        .overlay(alignment: .leading) {
            if event.notified {
                Rectangle().fill(SentinelTheme.green).frame(width: 2).padding(.vertical, 5)
            }
        }
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(event.camera), \(event.summary), \(event.occurredAt.formatted())")
    }

    private var eventImage: some View {
        SentinelRemoteImage(url: imageURL)
            .background(SentinelTheme.panel)
            .clipShape(RoundedRectangle(cornerRadius: 5))
            .clipped()
    }

    private var eventDetails: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text(event.camera).foregroundStyle(SentinelTheme.green).bold()
                Spacer()
                Text(event.occurredAt, style: .relative)
                    .foregroundStyle(SentinelTheme.textDim)
                    .font(.caption)
            }
            Text(event.summary.isEmpty ? "No description" : event.summary)
                .foregroundStyle(SentinelTheme.text)
                .multilineTextAlignment(.leading)
                .lineLimit(horizontalSizeClass == .compact ? 4 : 3)
            HStack(spacing: 10) {
                Text(event.eventType.replacingOccurrences(of: "_", with: " ").uppercased())
                    .foregroundStyle(SentinelTheme.cyan)
                    .lineLimit(1)
                Spacer(minLength: 4)
                if event.notified {
                    Image(systemName: "bell.fill")
                        .foregroundStyle(SentinelTheme.green)
                        .accessibilityLabel("Notification sent")
                }
                Text(event.confidence, format: .percent.precision(.fractionLength(0)))
                    .foregroundStyle(SentinelTheme.textDim)
            }
            .font(.caption)
        }
    }

    private var imageURL: URL? {
        event.hasImage ? model.client.url(path: "/api/events/\(event.id.uuidString)/image") : nil
    }
}
