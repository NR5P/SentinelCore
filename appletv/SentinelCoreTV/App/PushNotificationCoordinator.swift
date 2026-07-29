import Foundation
import UIKit
@preconcurrency import UserNotifications

extension Notification.Name {
    static let sentinelPushTokenChanged = Notification.Name("sentinelPushTokenChanged")
    static let sentinelPushOpened = Notification.Name("sentinelPushOpened")
    static let sentinelPushRegistrationFailed = Notification.Name("sentinelPushRegistrationFailed")
}

@MainActor
final class PushNotificationCoordinator {
    static let shared = PushNotificationCoordinator()

    private(set) var currentDeviceToken: String?
    private var pendingEventId: String?

    private init() {}

    func configure() {
        UNUserNotificationCenter.current().delegate = PushAppDelegate.shared
    }

    func registerWithAPNs() {
        UIApplication.shared.registerForRemoteNotifications()
    }

    func requestAuthorization() async throws -> Bool {
        let granted = try await UNUserNotificationCenter.current()
            .requestAuthorization(options: [.alert, .sound])
        if granted {
            registerWithAPNs()
        }
        return granted
    }

    func authorizationStatus() async -> UNAuthorizationStatus {
        await UNUserNotificationCenter.current().notificationSettings().authorizationStatus
    }

    func receivedDeviceToken(_ data: Data) {
        let token = data.map { String(format: "%02x", $0) }.joined()
        currentDeviceToken = token
        NotificationCenter.default.post(name: .sentinelPushTokenChanged, object: token)
    }

    func registrationFailed(_ error: Error) {
        NotificationCenter.default.post(
            name: .sentinelPushRegistrationFailed,
            object: error.localizedDescription
        )
    }

    func openedNotification(eventId: String) {
        guard !eventId.isEmpty else { return }
        pendingEventId = eventId
        NotificationCenter.default.post(name: .sentinelPushOpened, object: eventId)
    }

    func takePendingEventId() -> String? {
        defer { pendingEventId = nil }
        return pendingEventId
    }

    var deviceName: String { UIDevice.current.name }
}

// tvOS's UserNotifications framework is noticeably stripped down: UNNotificationResponse (the
// interactive "user selected the banner" callback) is unavailable outright, and even
// UNNotificationContent.userInfo - the normal way to read a push's custom payload - is marked
// API_UNAVAILABLE(tvos). So willPresent can only control whether the banner/sound show for
// foreground delivery; it can't tell us which event the push was about. All eventId extraction
// happens in the legacy application(_:didReceiveRemoteNotification:) below instead, which still
// gets the raw APNs payload dictionary directly and fires for both foreground and background
// delivery - that's what actually drives the alert takeover.
final class PushAppDelegate: NSObject, UNUserNotificationCenterDelegate, @unchecked Sendable {
    static let shared = PushAppDelegate()

    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        willPresent notification: UNNotification
    ) async -> UNNotificationPresentationOptions {
        [.banner, .sound]
    }
}

@MainActor
final class SentinelApplicationDelegate: NSObject, UIApplicationDelegate {
    func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil
    ) -> Bool {
        PushNotificationCoordinator.shared.configure()
        PushNotificationCoordinator.shared.registerWithAPNs()
        return true
    }

    func application(
        _ application: UIApplication,
        didRegisterForRemoteNotificationsWithDeviceToken deviceToken: Data
    ) {
        PushNotificationCoordinator.shared.receivedDeviceToken(deviceToken)
    }

    func application(
        _ application: UIApplication,
        didFailToRegisterForRemoteNotificationsWithError error: Error
    ) {
        PushNotificationCoordinator.shared.registrationFailed(error)
    }

    // Covers background/launch delivery, standing in for the interactive open gesture tvOS
    // doesn't have (see PushAppDelegate). The system calls this whenever a push reaches the app
    // process outside the foreground willPresent path, including relaunching a killed app for a
    // high-priority alert push - background wake timing on tvOS isn't guaranteed, but this is
    // the best-effort equivalent of "opening the alert" on this platform.
    func application(
        _ application: UIApplication,
        didReceiveRemoteNotification userInfo: [AnyHashable: Any],
        fetchCompletionHandler completionHandler: @escaping (UIBackgroundFetchResult) -> Void
    ) {
        if let eventId = userInfo["eventId"] as? String {
            PushNotificationCoordinator.shared.openedNotification(eventId: eventId)
        }
        completionHandler(.newData)
    }
}
