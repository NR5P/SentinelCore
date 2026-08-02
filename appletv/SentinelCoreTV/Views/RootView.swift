import SwiftUI

struct RootView: View {
    @EnvironmentObject private var model: AppModel
    @State private var selection: AppSection = .live

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
                        ForEach(AppSection.visibleCases(deviceHubEnabled: model.deviceHubEnabled)) { section in
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
    }

    @ViewBuilder private func sectionView(_ section: AppSection) -> some View {
        switch section {
        case .live: LiveCamerasView(isActive: selection == .live)
        case .alerts: AlertFeedView()
        case .triggers: TriggerFeedView()
        case .devices: DeviceHubView(isActive: selection == .devices)
        case .settings: SettingsView()
        }
    }
}

enum AppSection: String, CaseIterable, Identifiable {
    case live, alerts, triggers, devices, settings
    var id: Self { self }
    var title: String {
        switch self {
        case .live: "Live"
        case .alerts: "Alerts"
        case .triggers: "Triggers"
        case .devices: "Device Hub"
        case .settings: "Settings"
        }
    }
    var icon: String {
        switch self {
        case .live: "video.fill"
        case .alerts: "shield.lefthalf.filled"
        case .triggers: "wave.3.right.circle.fill"
        case .devices: "lightbulb.fill"
        case .settings: "gearshape.fill"
        }
    }

    // The Device Hub is off by default and only appears once it's switched on server-side.
    // tvOS has no five-tab limit like the iPhone tab bar, so this is a plain filter.
    static func visibleCases(deviceHubEnabled: Bool) -> [AppSection] {
        allCases.filter { $0 != .devices || deviceHubEnabled }
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
