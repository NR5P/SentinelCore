import SwiftUI
#if os(iOS)
import UIKit
#endif

@main
struct SentinelCoreApp: App {
    #if os(iOS)
    @UIApplicationDelegateAdaptor(SentinelApplicationDelegate.self) private var applicationDelegate
    #elseif os(macOS)
    @NSApplicationDelegateAdaptor(SentinelApplicationDelegate.self) private var applicationDelegate
    #endif

    @StateObject private var appModel = AppModel()

    init() {
        #if os(iOS)
        // Only large titles get this treatment (not the .inline titles on detail screens) -
        // largeTitleTextAttributes is a distinct attribute set from titleTextAttributes, so this
        // scopes itself automatically to exactly the top-level tabs (Feed, Timeline, Live,
        // Triggers, Settings) without touching anything pushed on top of them. Echoes
        // BrandHeader's green-glow monospace treatment of the ">_ SENTINEL_CORE" wordmark.
        let appearance = UINavigationBarAppearance()
        appearance.configureWithDefaultBackground()
        let shadow = NSShadow()
        shadow.shadowColor = UIColor(SentinelTheme.green).withAlphaComponent(0.55)
        shadow.shadowBlurRadius = 8
        appearance.largeTitleTextAttributes = [
            .foregroundColor: UIColor(SentinelTheme.green),
            .font: UIFont.monospacedSystemFont(ofSize: 32, weight: .bold),
            .shadow: shadow
        ]
        UINavigationBar.appearance().standardAppearance = appearance
        UINavigationBar.appearance().scrollEdgeAppearance = appearance
        UINavigationBar.appearance().compactAppearance = appearance
        #endif
    }

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(appModel)
                // Published into the environment as a plain value so the views that draw the
                // watermark - SentinelRemoteImage down in Design, the live tiles - can read it
                // without each one reaching for the whole AppModel. See UnlicensedWatermark.
                .environment(\.isLicensed, appModel.isLicensed)
        }
        #if os(macOS)
        .defaultSize(width: 1180, height: 760)
        #endif

        #if os(macOS)
        // "This Mac as a camera" in its own window - opened from Settings, the Live toolbar, or
        // on its own when a stream resumes after a relaunch (DeviceCameraPresenter in RootView).
        // A window, not a sheet: see that presenter for the crash a sheet caused here.
        Window("This Mac as a Camera", id: DeviceCameraSheet.windowId) {
            DeviceCameraSheet()
                .environmentObject(appModel)
                .environment(\.isLicensed, appModel.isLicensed)
        }
        .defaultSize(width: 600, height: 760)

        // Cmd+, and the app-menu "Settings…" item. The same screen is also a sidebar row in the
        // main window (see AppSection.sidebarCases) - this is the Mac convention on top of it,
        // not instead of it.
        //
        // The NavigationStack is load-bearing, not decoration: SettingsView's Manage Cameras,
        // Manage Accounts, Manage Known Faces and Server Settings rows are NavigationLinks, and a
        // Settings scene provides no navigation container of its own. Without this they were dead
        // rows - clicking them did nothing, which made it look as though the Mac app simply
        // couldn't reach those screens.
        Settings {
            NavigationStack {
                SettingsView()
            }
            .environmentObject(appModel)
            .environment(\.isLicensed, appModel.isLicensed)
            .frame(minWidth: 480, minHeight: 520)
        }
        #endif
    }
}
