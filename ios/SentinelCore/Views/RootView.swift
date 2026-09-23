import SwiftUI

struct RootView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
    @State private var selection: AppSection? = .events

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
        // persistent root normally prevents SwiftUI from cancelling it when a child disappears.
        // macOS can still recreate this root modifier while switching split-view sections,
        // though, which used to cancel an in-flight refresh after several successful requests
        // and leave connectionState stuck offline/checking. The unstructured task deliberately
        // outlives that transient view update; AppModel's isConnecting guard coalesces any
        // duplicate trigger produced by the replacement view.
        .task(id: model.serverURL) {
            Task { await model.connect(resetNetworkSession: true) }
        }
        .onChange(of: model.selectedEvent?.id) { _, eventId in
            if eventId != nil { selection = .events }
        }
        .background { DeviceCameraPresenter() }
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
                sectionView(selection ?? .events)
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
            get: { selection ?? .events },
            set: { selection = $0 }
        )
    }

    @ViewBuilder private func sectionView(_ section: AppSection) -> some View {
        switch section {
        case .events: EventFeedView()
        case .timeline: RecordingTimelineView()
        case .live: LiveCamerasView(isActive: selection == .live)
        case .devices: DeviceHubView(isActive: selection == .devices)
        case .settings: SettingsView()
        }
    }
}

// "Feed" and "Triggers" used to be separate sections - one for AI-analysed alerts, one for raw
// motion - which put a camera that forwards to AI analysis on both, once with the description and
// once with the clip. They are now one Events section over the server's merged list.
enum AppSection: String, CaseIterable, Identifiable {
    case events, timeline, live, devices, settings
    var id: Self { self }
    var title: String {
        switch self {
        case .events: "Events"
        case .timeline: "Timeline"
        case .live: "Live"
        case .devices: "Device Hub"
        case .settings: "Settings"
        }
    }
    var icon: String {
        switch self {
        case .events: "shield.lefthalf.filled"
        case .timeline: "clock.arrow.circlepath"
        case .live: "video.fill"
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
    // the section renders blank.
    //
    // Merging Feed and Triggers into one Events section brought the maximum back to exactly five
    // (Events, Timeline, Live, Device Hub, Settings), so Settings no longer has to be displaced
    // when the Device Hub is switched on - it keeps its tab in every configuration.
    static func compactTabCases(deviceHubEnabled: Bool) -> [AppSection] {
        visibleCases(deviceHubEnabled: deviceHubEnabled)
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

// Presents the "this device as a camera" screen and resumes a stream that was running when the
// app last quit.
//
// On the Mac the screen is its own window (see SentinelCoreApp), not a sheet. A `.sheet` bound to
// this flag - anywhere in the hierarchy, even unpresented - made the Mac app crash inside
// SwiftUI's own hit-testing and body evaluation (EXC_BAD_ACCESS in the Swift executor check) on
// macOS 26; bisected on 2026-09-23, and gone with the sheet gone. A window also suits a camera
// surface that is left open for days. iOS keeps the sheet, where it has been fine.
private struct DeviceCameraPresenter: View {
    @EnvironmentObject private var model: AppModel
    #if os(macOS)
    @Environment(\.openWindow) private var openWindow
    #endif

    var body: some View {
        Color.clear
            .frame(width: 0, height: 0)
            #if os(iOS)
            .sheet(isPresented: $model.showDeviceCamera) {
                DeviceCameraSheet().environmentObject(model)
            }
            #else
            .onChange(of: model.showDeviceCamera) { _, wanted in
                guard wanted else { return }
                openWindow(id: DeviceCameraSheet.windowId)
                model.showDeviceCamera = false
            }
            #endif
            // If this device was streaming as a camera when the app last ran, it goes back to
            // being one as soon as the server is reachable - and shows the screen, so it is
            // obvious that it did. See DevicePublisher.resumeIfIntended.
            .onChange(of: model.connectionState) { _, state in
                guard state == .online, model.isAdmin else { return }
                if DevicePublisher.shared.resumeIfIntended(client: model.client) != nil {
                    model.showDeviceCamera = true
                }
            }
    }
}
