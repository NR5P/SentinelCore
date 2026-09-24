import Foundation
@preconcurrency import UserNotifications

#if os(iOS)
import UIKit
#elseif os(macOS)
import AppKit
#endif

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
        let center = UNUserNotificationCenter.current()
        center.delegate = PushAppDelegate.shared
        let viewAction = UNNotificationAction(
            identifier: "VIEW_EVENT",
            title: "View event",
            options: [.foreground]
        )
        center.setNotificationCategories([
            UNNotificationCategory(
                identifier: "SECURITY_EVENT",
                actions: [viewAction],
                intentIdentifiers: [],
                options: []
            )
        ])
    }

    func registerWithAPNs() {
        #if os(iOS)
        UIApplication.shared.registerForRemoteNotifications()
        #elseif os(macOS)
        NSApplication.shared.registerForRemoteNotifications()
        #endif
    }

    func requestAuthorization() async throws -> Bool {
        let granted = try await UNUserNotificationCenter.current()
            .requestAuthorization(options: [.alert, .badge, .sound])
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

    var deviceName: String {
        #if os(iOS)
        UIDevice.current.name
        #elseif os(macOS)
        Host.current().localizedName ?? "Mac"
        #endif
    }
}

final class PushAppDelegate: NSObject, UNUserNotificationCenterDelegate, @unchecked Sendable {
    static let shared = PushAppDelegate()

    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        willPresent notification: UNNotification
    ) async -> UNNotificationPresentationOptions {
        [.banner, .list, .sound]
    }

    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        didReceive response: UNNotificationResponse
    ) async {
        // A reminder or event alert - see OrganizerNotifications.
        if let itemId = response.notification.request.content.userInfo["organizerItemId"] as? String {
            await MainActor.run {
                NotificationCenter.default.post(name: .sentinelOrganizerOpened, object: itemId)
            }
            return
        }
        guard let eventId = response.notification.request.content.userInfo["eventId"] as? String else {
            return
        }
        await MainActor.run {
            PushNotificationCoordinator.shared.openedNotification(eventId: eventId)
        }
    }
}

#if os(iOS)
@MainActor
final class SentinelApplicationDelegate: NSObject, UIApplicationDelegate {
    func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil
    ) -> Bool {
        PushNotificationCoordinator.shared.configure()
        PushNotificationCoordinator.shared.registerWithAPNs()
        OrganizerBackgroundRefresh.register()
        OrganizerBackgroundRefresh.schedule()
        return true
    }

    // A silent push - the server saying the Organizer changed. See OrganizerBackgroundRefresh.
    func application(
        _ application: UIApplication,
        didReceiveRemoteNotification userInfo: [AnyHashable: Any],
        fetchCompletionHandler completionHandler: @escaping (UIBackgroundFetchResult) -> Void
    ) {
        let eventType = userInfo["eventType"] as? String
        Task { @MainActor in
            completionHandler(await OrganizerBackgroundRefresh.handleRemoteNotification(eventType: eventType))
        }
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
}
#elseif os(macOS)
@MainActor
final class SentinelApplicationDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        PushNotificationCoordinator.shared.configure()
        PushNotificationCoordinator.shared.registerWithAPNs()
        SharedNotesPickup.registerServices()
    }

    func application(
        _ application: NSApplication,
        didRegisterForRemoteNotificationsWithDeviceToken deviceToken: Data
    ) {
        PushNotificationCoordinator.shared.receivedDeviceToken(deviceToken)
    }

    func application(
        _ application: NSApplication,
        didFailToRegisterForRemoteNotificationsWithError error: Error
    ) {
        PushNotificationCoordinator.shared.registrationFailed(error)
    }
}
#endif
