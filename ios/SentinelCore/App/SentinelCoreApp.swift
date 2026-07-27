import SwiftUI

@main
struct SentinelCoreApp: App {
    @StateObject private var appModel = AppModel()

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(appModel)
                .preferredColorScheme(.dark)
        }
    }
}
