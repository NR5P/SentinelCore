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
        .navigationTitle("Feed")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.large)
        #endif
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
        .padding(.vertical, 6)
        .overlay(alignment: .leading) {
            if event.notified {
                Rectangle().fill(SentinelTheme.green).frame(width: 3).padding(.vertical, 5)
            }
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(event.camera), \(event.summary), \(event.occurredAt.formatted())")
    }

    private var eventImage: some View {
        SentinelRemoteImage(url: imageURL)
            .clipShape(RoundedRectangle(cornerRadius: 6))
            .clipped()
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
            Text(event.summary.isEmpty ? "No description" : event.summary)
                .multilineTextAlignment(.leading)
                .lineLimit(horizontalSizeClass == .compact ? 4 : 3)
            HStack(spacing: 10) {
                Text(event.eventType.replacingOccurrences(of: "_", with: " ").capitalized)
                    .foregroundStyle(SentinelTheme.cyan)
                    .lineLimit(1)
                Spacer(minLength: 4)
                if event.notified {
                    Image(systemName: "bell.fill")
                        .foregroundStyle(SentinelTheme.green)
                        .accessibilityLabel("Notification sent")
                }
                Text(event.confidence, format: .percent.precision(.fractionLength(0)))
                    .foregroundStyle(.secondary)
            }
            .font(.caption)
        }
    }

    private var imageURL: URL? {
        event.hasImage ? model.client.url(path: "/api/events/\(event.id.uuidString)/image") : nil
    }
}
