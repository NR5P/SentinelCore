import SwiftUI

@main
struct SentinelCoreTVApp: App {
    @UIApplicationDelegateAdaptor(SentinelApplicationDelegate.self) private var applicationDelegate
    @StateObject private var appModel = AppModel()

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(appModel)
        }
    }
}
