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
                .preferredColorScheme(.dark)
        }
        .defaultSize(width: 1180, height: 760)
    }
}
