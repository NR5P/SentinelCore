import SwiftUI

struct RootView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
    @State private var selection: AppSection? = .feed

    var body: some View {
        Group {
            if !model.isConfigured {
                ServerSetupView()
            } else if model.requiresLogin {
                LoginView()
            } else {
                if horizontalSizeClass == .compact {
                    compactNavigation
                } else {
                    splitNavigation
                }
            }
        }
        .tint(SentinelTheme.green)
        // Reconnect whenever setup/settings changes the address. Keeping this task on the
        // persistent root prevents SwiftUI from cancelling it when ServerSetupView disappears.
        .task(id: model.serverURL) {
            await model.connect(resetNetworkSession: true)
        }
        .onChange(of: model.selectedEvent?.id) { _, eventId in
            if eventId != nil { selection = .feed }
        }
    }

    private var splitNavigation: some View {
        NavigationSplitView {
            VStack(spacing: 0) {
                BrandHeader()
                // On macOS, Settings lives in the app's Settings scene (Cmd+,) instead of a
                // sidebar row - see SentinelCoreApp. iPadOS has no such scene, so it keeps all
                // sections here.
                List(AppSection.sidebarCases, selection: $selection) { section in
                    Label(section.title, systemImage: section.icon).tag(section)
                }
                .listStyle(.sidebar)

                ConnectionBadge()
                    .padding()
            }
            .navigationSplitViewColumnWidth(min: 190, ideal: 220)
        } detail: {
            NavigationStack {
                sectionView(selection ?? .feed)
                    .navigationDestination(item: $model.selectedEvent) { event in
                        EventDetailView(event: event)
                    }
            }
        }
    }

    private var compactNavigation: some View {
        TabView(selection: compactSelection) {
            ForEach(AppSection.allCases) { section in
                NavigationStack {
                    sectionView(section)
                        .navigationDestination(item: $model.selectedEvent) { event in
                            EventDetailView(event: event)
                        }
                }
                .tabItem { Label(section.title, systemImage: section.icon) }
                .tag(section)
            }
        }
    }

    private var compactSelection: Binding<AppSection> {
        Binding(
            get: { selection ?? .feed },
            set: { selection = $0 }
        )
    }

    @ViewBuilder private func sectionView(_ section: AppSection) -> some View {
        switch section {
        case .feed: EventFeedView()
        case .timeline: RecordingTimelineView()
        case .live: LiveCamerasView(isActive: selection == .live)
        case .triggers: TriggerFeedView()
        case .settings: SettingsView()
        }
    }
}

enum AppSection: String, CaseIterable, Identifiable {
    case feed, timeline, live, triggers, settings
    var id: Self { self }
    var title: String {
        switch self {
        case .feed: "Feed"
        case .timeline: "Timeline"
        case .live: "Live"
        case .triggers: "Triggers"
        case .settings: "Settings"
        }
    }
    var icon: String {
        switch self {
        case .feed: "shield.lefthalf.filled"
        case .timeline: "clock.arrow.circlepath"
        case .live: "video.fill"
        case .triggers: "wave.3.right.circle.fill"
        case .settings: "gearshape.fill"
        }
    }

    // macOS gets a real Settings scene (Cmd+,) instead of a sidebar row - see SentinelCoreApp.
    static var sidebarCases: [AppSection] {
        #if os(macOS)
        allCases.filter { $0 != .settings }
        #else
        allCases
        #endif
    }
}

// The ">_" wordmark is the one place that keeps the terminal-style monospace font on purpose -
// a logotype, not body text, so it stays a deliberate accent rather than the whole app's font.
struct BrandHeader: View {
    var body: some View {
        HStack(spacing: 8) {
            Text(">_").foregroundStyle(SentinelTheme.green)
            Text("SENTINEL_CORE").foregroundStyle(SentinelTheme.green).bold()
            Spacer()
        }
        .font(.headline)
        .fontDesign(.monospaced)
        .shadow(color: SentinelTheme.green.opacity(0.5), radius: 6)
        .padding()
    }
}

struct ConnectionBadge: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        HStack(spacing: 8) {
            Circle()
                .fill(statusColor)
                .frame(width: 8, height: 8)
                .shadow(color: statusColor, radius: 5)
            Text(model.connectionState.label)
                .font(.caption)
                .foregroundStyle(.secondary)
            Spacer()
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("Server status: \(model.connectionState.label)")
    }

    private var statusColor: Color {
        switch model.connectionState {
        case .online: SentinelTheme.green
        case .checking, .authenticationRequired: SentinelTheme.amber
        case .offline: SentinelTheme.red
        case .notConfigured: .secondary
        }
    }
}
