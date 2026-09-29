import OrganizerKit
import SwiftUI

struct RootView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
    @Environment(\.scenePhase) private var scenePhase
    @State private var selection: AppSection? = .events
    @State private var morePath: [AppSection] = []
    @AppStorage(TabBarLayout.storageKey) private var tabBarSections = ""
    @AppStorage(TabBarLayout.weatherAddedKey) private var tabBarWeatherAdded = false
    @State private var editingTabBar = false
    // An iPad on a wall that was left in the Device Hub's wall display wants the app to open
    // straight back onto it, not onto Events. Once per launch, and only once the server has said
    // the hub is on - before that the section doesn't exist to select.
    @AppStorage(DeviceWall.storageKey) private var wallDisplayRemembered = false
    @State private var openedWallDisplay = false

    // Whether the server has the Organizer on - its own answer when it has given one this
    // launch, otherwise what it said last time, so the section is there offline too.
    private var organizerEnabled: Bool {
        model.organizer.status?.enabled ?? model.organizerLastKnownEnabled
    }

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
        .onChange(of: model.deviceHubEnabled) { _, enabled in
            #if os(iOS)
            if enabled && wallDisplayRemembered && !openedWallDisplay {
                openedWallDisplay = true
                selection = .devices
            }
            #endif
        }
        .background { DeviceCameraPresenter() }
        .environment(model.organizer)
        .onChange(of: model.organizer.status?.enabled) { _, enabled in
            if let enabled { model.organizerLastKnownEnabled = enabled }
        }
        .onChange(of: scenePhase) { _, phase in
            model.setOrganizerActive(phase == .active)
        }
        .onReceive(NotificationCenter.default.publisher(for: .sentinelOrganizerOpened)) { _ in
            selection = .organizer
        }
        // An alarm push was tapped: the person wants the arm/disarm buttons, not the feed.
        .onReceive(NotificationCenter.default.publisher(for: .sentinelAlarmOpened)) { _ in
            selection = .alarm
        }
        #if DEBUG
        // Simulator testing without taps: `-debugSection weather` as a launch argument, like the
        // Apple TV app's.
        .onAppear {
            if let name = UserDefaults.standard.string(forKey: "debugSection"),
               let section = AppSection(rawValue: name) { selection = section }
        }
        #endif
        // sentinelcore://organizer/<pane>, from tapping an Organizer widget on the Home Screen.
        .onOpenURL { url in
            // sentinelcore://alarm, from the Alarm widget.
            if url.scheme == "sentinelcore", url.host() == "alarm" {
                selection = .alarm
                return
            }
            // sentinelcore://weather, straight to the forecast and radar.
            if url.scheme == "sentinelcore", url.host() == "weather" {
                selection = .weather
                return
            }
            guard url.scheme == "sentinelcore", url.host() == "organizer" else { return }
            if let pane = url.pathComponents.dropFirst().first, ["calendar", "reminders", "notes", "boards"].contains(pane) {
                UserDefaults.standard.set(pane, forKey: "organizerPane")
            }
            selection = .organizer
        }
        // The server answering isn't the one this device last synced with. Asked wherever the
        // person happens to be, because nothing syncs until they answer. "Not Now" is remembered
        // for this server (the binding's setter can't do it: it also fires after the other
        // buttons), and Settings > Organizer on This Device brings the question back.
        .alert("This server doesn't have your Organizer data",
               isPresented: Binding(get: { model.organizer.resetPrompt != nil }, set: { _ in }),
               presenting: model.organizer.resetPrompt) { _ in
            Button("Upload This Device's Copy") { Task { await model.organizer.resolveReset(upload: true) } }
            Button("Use the Server's Copy", role: .destructive) { Task { await model.organizer.resolveReset(upload: false) } }
            Button("Not Now", role: .cancel) { model.organizer.declineReset() }
        } message: { prompt in
            Text("This device has \(prompt.localCount) Organizer items from a server that isn't the one answering now\(prompt.serverCount.map { " (it has \($0))" } ?? ""). If the old server was lost or reinstalled, upload this copy to fill the new one back up - anything already on the new server is kept, and newer edits win. Using the server's copy replaces what's on this device. Not Now keeps this device's copy and pauses Organizer sync until you decide in Settings.")
        }
    }

    private var splitNavigation: some View {
        NavigationSplitView {
            VStack(spacing: 0) {
                BrandHeader()
                List(AppSection.sidebarCases(
                    deviceHubEnabled: model.deviceHubEnabled, organizerEnabled: organizerEnabled,
                    moviesEnabled: model.moviesEnabled, alarmEnabled: model.alarmEnabled,
                    filesEnabled: model.familyFilesEnabled), selection: $selection) { section in
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

    private var compactLayout: TabBarLayout {
        TabBarLayout(pinned: TabBarLayout.decode(tabBarSections), visible: visibleSections)
    }

    private var visibleSections: [AppSection] {
        AppSection.visibleCases(
            deviceHubEnabled: model.deviceHubEnabled, organizerEnabled: organizerEnabled,
            moviesEnabled: model.moviesEnabled, alarmEnabled: model.alarmEnabled,
            filesEnabled: model.familyFilesEnabled)
    }

    private var compactNavigation: some View {
        let layout = compactLayout
        return TabView(selection: compactSelection) {
            ForEach(layout.bar) { section in
                NavigationStack {
                    sectionView(section)
                        .navigationDestination(item: $model.selectedEvent) { event in
                            EventDetailView(event: event)
                        }
                }
                .tabItem { Label(section.title, systemImage: section.icon) }
                .tag(CompactTab.section(section))
            }
            if layout.showsMore {
                NavigationStack(path: $morePath) {
                    MoreSectionsView(sections: layout.more) { editingTabBar = true }
                        .navigationDestination(for: AppSection.self) { section in
                            sectionView(section)
                        }
                        .navigationDestination(item: $model.selectedEvent) { event in
                            EventDetailView(event: event)
                        }
                }
                .tabItem { Label("More", systemImage: "ellipsis.circle") }
                .tag(CompactTab.more)
            }
        }
        // Changing which tabs exist in place leaves UIKit's tab controller showing a blank page
        // for the selected one, so a new bar is a new TabView.
        .id(layout.bar)
        .sheet(isPresented: $editingTabBar) {
            NavigationStack { TabBarEditor(visible: visibleSections, inSheet: true) }
        }
        // `selection` stays the one answer to "which section is on screen", on the tab bar and
        // in the sidebar alike. A section without a tab is on screen when it is pushed on More,
        // so whatever sends the app to one (a tapped Organizer alert, an event) lands there.
        .onChange(of: selection) { _, section in showUnderMoreIfNeeded(section) }
        .onChange(of: layout) { _, _ in showUnderMoreIfNeeded(selection) }
        .onAppear {
            if !tabBarWeatherAdded {
                tabBarSections = TabBarLayout.addingWeather(to: tabBarSections)
                tabBarWeatherAdded = true
            }
            showUnderMoreIfNeeded(selection)
        }
        .onChange(of: morePath) { _, path in
            // Only while More is the tab on screen - a section with a tab owns `selection` then.
            if selection.map({ !compactLayout.bar.contains($0) }) ?? true {
                selection = path.last
            }
        }
    }

    private func showUnderMoreIfNeeded(_ section: AppSection?) {
        let layout = compactLayout
        guard let section, !layout.bar.contains(section) else { return }
        if layout.more.contains(section) {
            if morePath.last != section { morePath = [section] }
        } else {
            // Switched off server-side while it was on screen.
            selection = layout.bar.first
        }
    }

    private var compactSelection: Binding<CompactTab> {
        Binding(
            get: {
                if let selection, compactLayout.bar.contains(selection) { return .section(selection) }
                return .more
            },
            set: { tab in
                switch tab {
                case .section(let section): selection = section
                case .more: selection = morePath.last
                }
            }
        )
    }

    @ViewBuilder private func sectionView(_ section: AppSection) -> some View {
        switch section {
        case .events: EventFeedView()
        case .timeline: RecordingTimelineView()
        case .live: LiveCamerasView(isActive: selection == .live)
        case .movies: MoviesView(isActive: selection == .movies)
        case .devices: DeviceHubView(isActive: selection == .devices)
        case .alarm: AlarmView(isActive: selection == .alarm)
        case .organizer: OrganizerView()
        case .weather: WeatherView(isActive: selection == .weather)
        case .files: FilesView()
        case .settings: SettingsView()
        }
    }
}

private enum CompactTab: Hashable {
    case section(AppSection)
    case more
}

// "Feed" and "Triggers" used to be separate sections - one for AI-analysed alerts, one for raw
// motion - which put a camera that forwards to AI analysis on both, once with the description and
// once with the clip. They are now one Events section over the server's merged list.
enum AppSection: String, CaseIterable, Identifiable {
    // Weather sits after the Organizer, as on the dashboard's tab row.
    case events, timeline, live, movies, alarm, organizer, weather, files, devices, settings
    var id: Self { self }
    var title: String {
        switch self {
        case .events: "Events"
        case .timeline: "Timeline"
        case .live: "Live"
        case .movies: "Movies"
        case .alarm: "Alarm"
        case .organizer: "Organizer"
        case .weather: "Weather"
        case .files: "Files"
        case .devices: "Device Hub"
        case .settings: "Settings"
        }
    }
    var icon: String {
        switch self {
        case .events: "shield.lefthalf.filled"
        case .timeline: "clock.arrow.circlepath"
        case .live: "video.fill"
        case .movies: "film.stack"
        case .alarm: "lock.shield.fill"
        case .organizer: "calendar"
        case .weather: "cloud.sun.fill"
        case .files: "folder"
        case .devices: "lightbulb.fill"
        case .settings: "gearshape.fill"
        }
    }

    // The Device Hub, the Organizer, Movies & TV, the Alarm and Files can each be switched off
    // server-side, so every list of sections is filtered rather than using allCases directly.
    static func visibleCases(deviceHubEnabled: Bool, organizerEnabled: Bool, moviesEnabled: Bool = false,
                             alarmEnabled: Bool = false, filesEnabled: Bool = false) -> [AppSection] {
        allCases.filter {
            ($0 != .devices || deviceHubEnabled) && ($0 != .organizer || organizerEnabled)
                && ($0 != .movies || moviesEnabled)
                && ($0 != .alarm || alarmEnabled) && ($0 != .files || filesEnabled)
        }
    }

    // Which of these get a tab on an iPhone is up to the person - see TabBarLayout.

    // Every section, on every platform with a sidebar. macOS also has a real Settings scene on
    // Cmd+, (see SentinelCoreApp), and for a while that was the *only* way in - the row was
    // filtered out here as "the Mac convention". In practice that made Settings hard to find, and
    // worse, the Settings scene has no NavigationStack of its own, so the rows that push a screen
    // (Manage Cameras, Manage Accounts, Manage Known Faces, Server Settings) did nothing at all
    // when clicked. The sidebar row renders into the split view's detail column, which does have
    // one, so those screens work from here. Both routes now lead to the same place.
    static func sidebarCases(deviceHubEnabled: Bool, organizerEnabled: Bool, moviesEnabled: Bool = false,
                             alarmEnabled: Bool = false, filesEnabled: Bool = false) -> [AppSection] {
        visibleCases(deviceHubEnabled: deviceHubEnabled, organizerEnabled: organizerEnabled,
                     moviesEnabled: moviesEnabled, alarmEnabled: alarmEnabled, filesEnabled: filesEnabled)
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
