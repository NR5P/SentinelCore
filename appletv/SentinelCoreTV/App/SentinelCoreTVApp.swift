import SwiftUI

@main
struct SentinelCoreTVApp: App {
    @UIApplicationDelegateAdaptor(SentinelApplicationDelegate.self) private var applicationDelegate
    @StateObject private var appModel = AppModel()

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(appModel)
                // Published into the environment as a plain value so the views that draw the
                // watermark - SentinelRemoteImage down in Design, the live tiles - can read it
                // without each one reaching for the whole AppModel. See UnlicensedWatermark.
                .environment(\.isLicensed, appModel.isLicensed)
        }
    }
}
