#if os(iOS)
import BackgroundTasks
import UIKit

// Keeps the Organizer (and so the Home Screen widgets) current while the app isn't open, two ways:
//
// - The server's silent push when anything in the Organizer changes (OrganizerPushNudger on the
//   server) - prompt, but iOS rations them and never delivers one to an app the person has
//   force-quit.
// - A background refresh, which iOS runs when it judges best (roughly every half hour to a few
//   hours, depending on how the phone is used) - the fallback, and what moves the widgets' dates
//   along on a quiet day.
//
// Both do the same thing: AppModel.refreshOrganizerInBackground.
@MainActor
enum OrganizerBackgroundRefresh {
    static let taskIdentifier = "com.sentinelcore.client.organizer-refresh"

    // Must run before launch finishes (application(_:didFinishLaunchingWithOptions:)).
    static func register() {
        BGTaskScheduler.shared.register(forTaskWithIdentifier: taskIdentifier, using: .main) { task in
            MainActor.assumeIsolated { run(task) }
        }
    }

    static func schedule() {
        let request = BGAppRefreshTaskRequest(identifier: taskIdentifier)
        request.earliestBeginDate = Date(timeIntervalSinceNow: 30 * 60)
        // Replaces any earlier request with the same identifier. Fails in the simulator and when
        // Background App Refresh is off - neither is worth reporting.
        try? BGTaskScheduler.shared.submit(request)
    }

    private static func run(_ task: BGTask) {
        schedule()
        let work = Task { @MainActor in
            await AppModel.shared.refreshOrganizerInBackground()
            task.setTaskCompleted(success: !Task.isCancelled)
        }
        task.expirationHandler = { work.cancel() }
    }

    // The silent push. Anything else arriving here (an ordinary alert) isn't ours to handle.
    static func handleRemoteNotification(eventType: String?) async -> UIBackgroundFetchResult {
        guard eventType == "organizer-changed" else { return .noData }
        AppModel.shared.log("Woken by the server: the Organizer changed")
        await AppModel.shared.refreshOrganizerInBackground()
        return .newData
    }
}
#endif
