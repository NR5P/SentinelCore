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
                        model.isLoading ? "Loading feed…" : "No events",
                        systemImage: "shield.slash",
                        description: Text("Alerts received by Sentinel Core will appear here.")
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
                Button { Task { await model.connect() } } label: {
                    Label("Refresh", systemImage: "arrow.clockwise")
                }
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
            Text("\(filteredEvents.count) EVENTS")
                .foregroundStyle(SentinelTheme.textDim)
                .font(.caption)
            Spacer()
            ConnectionBadge().frame(maxWidth: 180)
        }
        .padding(.bottom, 4)
    }
}

struct EventCard: View {
    @EnvironmentObject private var model: AppModel
    let event: SecurityEvent

    var body: some View {
        HStack(alignment: .top, spacing: 14) {
            AsyncImage(url: imageURL) { phase in
                switch phase {
                case let .success(image):
                    image.resizable().scaledToFill()
                case .failure:
                    Image(systemName: "photo").foregroundStyle(SentinelTheme.textDim)
                default:
                    ProgressView()
                }
            }
            .frame(width: 132, height: 88)
            .background(SentinelTheme.panel)
            .clipShape(RoundedRectangle(cornerRadius: 5))
            .clipped()

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
                    .lineLimit(3)
                HStack {
                    Text(event.eventType.replacingOccurrences(of: "_", with: " ").uppercased())
                        .foregroundStyle(SentinelTheme.cyan)
                    Spacer()
                    if event.notified {
                        Label("NOTIFIED", systemImage: "bell.fill")
                            .foregroundStyle(SentinelTheme.green)
                    }
                    Text(event.confidence, format: .percent.precision(.fractionLength(0)))
                        .foregroundStyle(SentinelTheme.textDim)
                }
                .font(.caption)
            }
        }
        .sentinelPanel()
        .overlay(alignment: .leading) {
            if event.notified {
                Rectangle().fill(SentinelTheme.green).frame(width: 2).padding(.vertical, 5)
            }
        }
    }

    private var imageURL: URL? {
        event.hasImage ? model.client.url(path: "/api/events/\(event.id.uuidString)/image") : nil
    }
}
