import OrganizerKit
import SwiftUI

struct RootView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.scenePhase) private var scenePhase
    @State private var selection: AppSection = .live

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
                    TabView(selection: $selection) {
                        ForEach(AppSection.visibleCases(
                            deviceHubEnabled: model.deviceHubEnabled, organizerEnabled: organizerEnabled,
                            moviesEnabled: model.moviesEnabled, alarmEnabled: model.alarmEnabled,
                            filesEnabled: model.familyFilesEnabled)) { section in
                            NavigationStack {
                                sectionView(section)
                                    .navigationDestination(item: $model.selectedEvent) { event in
                                        AlertDetailView(event: event)
                                    }
                            }
                            .tabItem { Label(section.title, systemImage: section.icon) }
                            .tag(section)
                        }
                    }
                    #if DEBUG
                    // Simulator testing without a remote: `-debugSection movies` as a launch argument.
                    .onAppear {
                        if let name = UserDefaults.standard.string(forKey: "debugSection"),
                           let section = AppSection(rawValue: name) { selection = section }
                    }
                    #endif
                }
            }
        }
        .tint(SentinelTheme.green)
        .task(id: model.serverURL) {
            // Connection setup may replace a certificate-rejected HTTPS URL with HTTP. Keep it
            // alive when that state change invalidates this view task; connect owns its own
            // request cancellation and the replacement task will reuse the in-flight work.
            Task { await model.connect(resetNetworkSession: true) }
        }
        // The AI-verified alert interrupt: set whenever a push-worthy event arrives while this
        // app is foregrounded, or is opened from the system banner. Covers the whole screen
        // regardless of which tab is active - see AppModel.alertTakeover and
        // PushNotificationCoordinator's willPresent handling for how it gets set.
        .fullScreenCover(item: $model.alertTakeover) { event in
            AlertTakeoverView(event: event)
        }
        .environment(model.organizer)
        .onChange(of: model.organizer.status?.enabled) { _, enabled in
            if let enabled { model.organizerLastKnownEnabled = enabled }
        }
        .onChange(of: scenePhase) { _, phase in model.setOrganizerActive(phase == .active) }
        // Same question the phone app asks when the server has been replaced. A TV only holds a
        // light copy, so using the server's is the likely answer here - but it's the person's call.
        .alert("This server doesn't have your Organizer data",
               isPresented: Binding(get: { model.organizer.resetPrompt != nil }, set: { _ in }),
               presenting: model.organizer.resetPrompt) { _ in
            Button("Use the Server's Copy") { Task { await model.organizer.resolveReset(upload: false) } }
            Button("Upload This TV's Copy") { Task { await model.organizer.resolveReset(upload: true) } }
        } message: { prompt in
            Text("This TV has \(prompt.localCount) Organizer items from a server that isn't the one answering now.")
        }
    }

    @ViewBuilder private func sectionView(_ section: AppSection) -> some View {
        switch section {
        case .live: LiveCamerasView(isActive: selection == .live)
        case .movies: MoviesTVView(isActive: selection == .movies)
        case .events: AlertFeedView()
        case .devices: DeviceHubView(isActive: selection == .devices)
        case .alarm: AlarmView(isActive: selection == .alarm)
        case .organizer: OrganizerTVView()
        case .files: FilesTVView()
        case .settings: SettingsView()
        }
    }
}

// "Alerts" and "Triggers" used to be separate tabs - one for AI-analysed alerts, one for raw
// motion - which put a camera that forwards to AI analysis on both, once with the description and
// once with the clip. They are now one Events tab over the server's merged list.
enum AppSection: String, CaseIterable, Identifiable {
    case live, movies, events, alarm, organizer, files, devices, settings
    var id: Self { self }
    var title: String {
        switch self {
        case .live: "Live"
        case .movies: "Movies"
        case .events: "Events"
        case .alarm: "Alarm"
        case .organizer: "Organizer"
        case .files: "Files"
        case .devices: "Device Hub"
        case .settings: "Settings"
        }
    }
    var icon: String {
        switch self {
        case .live: "video.fill"
        case .movies: "film.stack"
        case .events: "shield.lefthalf.filled"
        case .alarm: "lock.shield.fill"
        case .organizer: "calendar"
        case .files: "folder"
        case .devices: "lightbulb.fill"
        case .settings: "gearshape.fill"
        }
    }

    // The Device Hub, the Organizer, Movies & TV, the Alarm and Files can each be switched off
    // server-side; Movies, the Alarm and Files are off by default and only appear once switched on.
    // tvOS has no five-tab limit like the iPhone tab bar, so this is a plain filter.
    static func visibleCases(deviceHubEnabled: Bool, organizerEnabled: Bool, moviesEnabled: Bool = false,
                             alarmEnabled: Bool = false, filesEnabled: Bool = false) -> [AppSection] {
        allCases.filter {
            ($0 != .devices || deviceHubEnabled) && ($0 != .organizer || organizerEnabled)
                && ($0 != .movies || moviesEnabled)
                && ($0 != .alarm || alarmEnabled) && ($0 != .files || filesEnabled)
        }
    }
}

// The ">_" wordmark - same terminal-style monospace accent as the iOS app's BrandHeader.
// .fixedSize() keeps it on one line - tvOS's larger type sizes made "SENTINEL_CORE" wrap and
// hyphenate mid-word inside the narrower frame(maxWidth:) callers use to center it.
struct BrandHeader: View {
    var body: some View {
        HStack(spacing: 10) {
            Text(">_").foregroundStyle(SentinelTheme.green)
            Text("SENTINEL_CORE").foregroundStyle(SentinelTheme.green).bold()
        }
        .font(.title3)
        .fontDesign(.monospaced)
        .shadow(color: SentinelTheme.green.opacity(0.5), radius: 6)
        .fixedSize()
    }
}

// Advisory, never blocking - the app works normally behind it. Amber rather than red for that
// reason: this is "you'll want to update the server soon", not "something is broken". Collapses to
// nothing whenever the server is current, or when its version simply couldn't be read.
//
// Not focusable, and carries no button: an Apple TV is generally watched rather than operated, and
// there is nothing to do about this from the couch anyway - the fix is on the server.
struct ServerVersionBanner: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        if let advisory = model.serverCompatibility.advisory {
            HStack(alignment: .top, spacing: 12) {
                Image(systemName: "exclamationmark.triangle.fill")
                    .foregroundStyle(SentinelTheme.amber)
                Text(advisory)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 50)
            .padding(.vertical, 14)
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
                .frame(width: 10, height: 10)
                .shadow(color: statusColor, radius: 5)
            Text(model.connectionState.label)
                .font(.caption)
                .foregroundStyle(.secondary)
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
