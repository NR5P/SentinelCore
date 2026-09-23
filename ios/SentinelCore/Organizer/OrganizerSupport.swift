import Foundation
import OrganizerKit
@preconcurrency import UserNotifications

// The app's side of OrganizerKit: how the engine reaches the server, where it keeps its copy,
// which offline policy this device uses, and turning planned alerts into local notifications.

struct AppOrganizerTransport: OrganizerTransport {
    let client: SentinelClient

    func send(method: String, path: String, query: [URLQueryItem], body: Data?, contentType: String?, timeout: TimeInterval?) async throws -> OrganizerHTTPResponse {
        do {
            let (status, data) = try await client.organizerRequest(
                method: method, path: path, query: query, body: body, contentType: contentType, timeout: timeout)
            return OrganizerHTTPResponse(status: status, data: data)
        } catch let error as URLError where error.code == .cancelled {
            throw CancellationError()
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            throw OrganizerError.offline
        }
    }
}

enum OrganizerDevice {
    // Stable for the life of this install; the tiebreak inside every clock this device writes.
    static var id: String {
        if let existing = UserDefaults.standard.string(forKey: "organizerDeviceId") { return existing }
        #if os(macOS)
        let prefix = "mac"
        #else
        let prefix = "ios"
        #endif
        let id = "\(prefix)-\(UUID().uuidString.prefix(8).lowercased())"
        UserDefaults.standard.set(id, forKey: "organizerDeviceId")
        return id
    }

    static var directory: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        let url = base.appendingPathComponent("Organizer", isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        // The copy is rebuilt from the server whenever it's missing; iCloud backups of it would
        // only double people's storage.
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        var mutable = url
        try? mutable.setResourceValues(values)
        return url
    }

    // Phones keep a light copy by default and computers everything - see OfflinePolicy.
    static var defaultPolicy: OfflinePolicy {
        #if os(macOS)
        .computerDefault
        #else
        .phoneDefault
        #endif
    }

    static var savedPolicy: OfflinePolicy {
        get {
            guard let data = UserDefaults.standard.data(forKey: "organizerOfflinePolicy"),
                  let policy = try? JSONDecoder().decode(OfflinePolicy.self, from: data) else { return defaultPolicy }
            return policy
        }
        set {
            UserDefaults.standard.set(try? JSONEncoder().encode(newValue), forKey: "organizerOfflinePolicy")
        }
    }
}

// Reminder and event alerts as local notifications - they fire with no signal and with the
// server down, which is why they're scheduled here rather than pushed.
@MainActor
enum OrganizerNotifications {
    static let prefix = "organizer."
    static let itemKey = "organizerItemId"

    static func schedule(_ alerts: [PlannedAlert]) {
        Task {
            let center = UNUserNotificationCenter.current()
            if !alerts.isEmpty, await center.notificationSettings().authorizationStatus == .notDetermined {
                _ = try? await center.requestAuthorization(options: [.alert, .sound, .badge])
            }
            let pending = await center.pendingNotificationRequests()
            let ours = pending.map(\.identifier).filter { $0.hasPrefix(prefix) }
            let wanted = Set(alerts.map(\.id))
            center.removePendingNotificationRequests(withIdentifiers: ours.filter { !wanted.contains($0) })
            let existing = Set(ours)
            for alert in alerts where !existing.contains(alert.id) {
                let content = UNMutableNotificationContent()
                content.title = alert.title
                content.body = alert.body
                content.sound = .default
                content.userInfo = [itemKey: alert.itemId]
                let components = Calendar.current.dateComponents([.year, .month, .day, .hour, .minute, .second], from: alert.fireDate)
                let trigger = UNCalendarNotificationTrigger(dateMatching: components, repeats: false)
                try? await center.add(UNNotificationRequest(identifier: alert.id, content: content, trigger: trigger))
            }
        }
    }
}

extension Notification.Name {
    static let sentinelOrganizerOpened = Notification.Name("sentinelOrganizerOpened")
}
