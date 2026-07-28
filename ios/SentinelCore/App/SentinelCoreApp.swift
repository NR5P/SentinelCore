import SwiftUI

@main
struct SentinelCoreApp: App {
    #if os(iOS)
    @UIApplicationDelegateAdaptor(SentinelApplicationDelegate.self) private var applicationDelegate
    #elseif os(macOS)
    @NSApplicationDelegateAdaptor(SentinelApplicationDelegate.self) private var applicationDelegate
    #endif

    @StateObject private var appModel = AppModel()

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(appModel)
        }
        #if os(macOS)
        .defaultSize(width: 1180, height: 760)
        #endif

        #if os(macOS)
        // Cmd+, and the app-menu "Settings…" item, replacing the sidebar's Settings row
        // (AppSection.sidebarCases filters it out on macOS) with the standard Mac convention.
        Settings {
            SettingsView()
                .environmentObject(appModel)
                .frame(minWidth: 480, minHeight: 520)
        }
        #endif
    }
}
