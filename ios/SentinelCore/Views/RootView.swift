import SwiftUI

struct RootView: View {
    @EnvironmentObject private var model: AppModel
    @State private var selection: AppSection? = .feed

    var body: some View {
        Group {
            if !model.isConfigured {
                ServerSetupView()
            } else if model.requiresLogin {
                LoginView()
            } else {
                appNavigation
            }
        }
        .fontDesign(.monospaced)
        .tint(SentinelTheme.green)
        .background(SentinelTheme.background.ignoresSafeArea())
        // Reconnect whenever setup/settings changes the address. Keeping this task on the
        // persistent root prevents SwiftUI from cancelling it when ServerSetupView disappears.
        .task(id: model.serverURL) {
            await model.connect()
        }
    }

    private var appNavigation: some View {
        NavigationSplitView {
            VStack(spacing: 0) {
                BrandHeader()
                List(AppSection.allCases, selection: $selection) { section in
                    Label(section.title, systemImage: section.icon)
                        .tag(section)
                        .foregroundStyle(selection == section ? SentinelTheme.green : SentinelTheme.text)
                }
                .scrollContentBackground(.hidden)

                ConnectionBadge()
                    .padding()
            }
            .background(SentinelTheme.panel)
            .navigationSplitViewColumnWidth(min: 190, ideal: 220)
        } detail: {
            NavigationStack {
                sectionView
            }
            .background(SentinelTheme.background)
        }
    }

    @ViewBuilder private var sectionView: some View {
        switch selection ?? .feed {
        case .feed: EventFeedView()
        case .live: LiveCamerasView()
        case .triggers: TriggerFeedView()
        case .settings: SettingsView()
        }
    }
}

enum AppSection: String, CaseIterable, Identifiable {
    case feed, live, triggers, settings
    var id: Self { self }
    var title: String { rawValue.uppercased() }
    var icon: String {
        switch self {
        case .feed: "shield.lefthalf.filled"
        case .live: "video.fill"
        case .triggers: "wave.3.right.circle.fill"
        case .settings: "gearshape.fill"
        }
    }
}

struct BrandHeader: View {
    var body: some View {
        HStack(spacing: 8) {
            Text(">_").foregroundStyle(SentinelTheme.green)
            Text("SENTINEL_CORE").foregroundStyle(SentinelTheme.green).bold()
            Spacer()
        }
        .font(.headline)
        .shadow(color: SentinelTheme.green.opacity(0.5), radius: 6)
        .padding()
    }
}

struct ConnectionBadge: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        HStack(spacing: 8) {
            Circle()
                .fill(model.connectionState == .online ? SentinelTheme.green : SentinelTheme.red)
                .frame(width: 8, height: 8)
                .shadow(color: model.connectionState == .online ? SentinelTheme.green : SentinelTheme.red, radius: 5)
            Text(model.connectionState.label)
                .font(.caption)
                .foregroundStyle(SentinelTheme.textDim)
            Spacer()
        }
    }
}
