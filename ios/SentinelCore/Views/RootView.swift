import SwiftUI

struct RootView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
    @State private var selection: AppSection? = .feed

    var body: some View {
        VStack(spacing: 0) {
            // Above the login screen as well as the main UI: "this server is too old" explains a
            // login that behaves oddly just as much as it explains an empty screen afterwards. It
            // renders nothing at all unless there's something to say.
            if model.isConfigured { ServerVersionBanner() }

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
                List(AppSection.sidebarCases(deviceHubEnabled: model.deviceHubEnabled), selection: $selection) { section in
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
            ForEach(AppSection.compactTabCases(deviceHubEnabled: model.deviceHubEnabled)) { section in
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
        case .devices: DeviceHubView(isActive: selection == .devices)
        case .settings: SettingsView()
        }
    }
}

enum AppSection: String, CaseIterable, Identifiable {
    case feed, timeline, live, triggers, devices, settings
    var id: Self { self }
    var title: String {
        switch self {
        case .feed: "Feed"
        case .timeline: "Timeline"
        case .live: "Live"
        case .triggers: "Triggers"
        case .devices: "Device Hub"
        case .settings: "Settings"
        }
    }
    var icon: String {
        switch self {
        case .feed: "shield.lefthalf.filled"
        case .timeline: "clock.arrow.circlepath"
        case .live: "video.fill"
        case .triggers: "wave.3.right.circle.fill"
        case .devices: "lightbulb.fill"
        case .settings: "gearshape.fill"
        }
    }

    // The Device Hub is off by default and only appears once it's switched on server-side, so
    // every list of sections is filtered rather than using allCases directly.
    static func visibleCases(deviceHubEnabled: Bool) -> [AppSection] {
        allCases.filter { $0 != .devices || deviceHubEnabled }
    }

    // An iPhone tab bar holds five items; a sixth makes UIKit collapse the overflow into its own
    // "More" navigation controller, which cannot host these sections - a NavigationStack inside
    // it crashes outright ("attempt to nest wrapped navigation controllers"), and without one
    // the section renders blank. So the compact bar is capped at five, and enabling the Device
    // Hub displaces Settings, which EventFeedView then offers as a toolbar button instead.
    // With the Device Hub off - the default - this is the original five tabs, unchanged.
    static func compactTabCases(deviceHubEnabled: Bool) -> [AppSection] {
        visibleCases(deviceHubEnabled: deviceHubEnabled)
            .filter { !(deviceHubEnabled && $0 == .settings) }
    }

    // Every section, on every platform with a sidebar. macOS also has a real Settings scene on
    // Cmd+, (see SentinelCoreApp), and for a while that was the *only* way in - the row was
    // filtered out here as "the Mac convention". In practice that made Settings hard to find, and
    // worse, the Settings scene has no NavigationStack of its own, so the rows that push a screen
    // (Manage Cameras, Manage Accounts, Manage Known Faces, Server Settings) did nothing at all
    // when clicked. The sidebar row renders into the split view's detail column, which does have
    // one, so those screens work from here. Both routes now lead to the same place.
    static func sidebarCases(deviceHubEnabled: Bool) -> [AppSection] {
        visibleCases(deviceHubEnabled: deviceHubEnabled)
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

// Advisory, never blocking - the app works normally behind it. Amber rather than red for that
// reason: this is "you'll want to update the server soon", not "something is broken". Collapses to
// nothing whenever the server is current, or when its version simply couldn't be read.
struct ServerVersionBanner: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        if let advisory = model.serverCompatibility.advisory {
            HStack(alignment: .top, spacing: 8) {
                Image(systemName: "exclamationmark.triangle.fill")
                    .foregroundStyle(SentinelTheme.amber)
                Text(advisory)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 0)
            }
            .padding(.horizontal)
            .padding(.vertical, 10)
            .background(SentinelTheme.amber.opacity(0.12))
            .accessibilityElement(children: .combine)
            .accessibilityLabel("Server version warning: \(advisory)")
        }
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
