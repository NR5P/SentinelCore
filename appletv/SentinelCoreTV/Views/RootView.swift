import SwiftUI

struct RootView: View {
    @EnvironmentObject private var model: AppModel
    @State private var selection: AppSection = .live

    var body: some View {
        Group {
            if !model.isConfigured {
                ServerSetupView()
            } else if model.requiresLogin {
                LoginView()
            } else {
                TabView(selection: $selection) {
                    ForEach(AppSection.allCases) { section in
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
        .tint(SentinelTheme.green)
        .task(id: model.serverURL) {
            await model.connect(resetNetworkSession: true)
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
        case .settings: SettingsView()
        }
    }
}

enum AppSection: String, CaseIterable, Identifiable {
    case live, alerts, triggers, settings
    var id: Self { self }
    var title: String {
        switch self {
        case .live: "Live"
        case .alerts: "Alerts"
        case .triggers: "Triggers"
        case .settings: "Settings"
        }
    }
    var icon: String {
        switch self {
        case .live: "video.fill"
        case .alerts: "shield.lefthalf.filled"
        case .triggers: "wave.3.right.circle.fill"
        case .settings: "gearshape.fill"
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
