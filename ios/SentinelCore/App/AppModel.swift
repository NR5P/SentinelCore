import Combine
import Foundation
import Network
import UserNotifications

// A SentinelCore server found on the local network via Bonjour/mDNS - see
// AppModel.startServerDiscovery(). Purely a convenience for prefilling ServerSetupView's manual
// address field; nothing here is persisted directly.
struct DiscoveredServer: Identifiable, Equatable {
    let id: String
    let name: String
    let host: String
    let port: Int
    let scheme: String

    var addressString: String { "\(scheme)://\(host):\(port)" }
}

@MainActor
final class AppModel: ObservableObject {
    @Published var serverURL: String {
        didSet { UserDefaults.standard.set(serverURL, forKey: "serverURL") }
    }
    // Optional Cloudflare Tunnel (or any other externally-reachable) address, used only when
    // the local address can't be reached - see connect() for the resolution order.
    @Published var remoteServerURL: String {
        didSet { UserDefaults.standard.set(remoteServerURL, forKey: "remoteServerURL") }
    }
    @Published private(set) var activeServerURL: String = ""
    @Published private(set) var isUsingRemote = false
    @Published var connectionState: ConnectionState = .checking
    @Published var authStatus: AuthStatus?
    @Published var events: [SecurityEvent] = []
    @Published var triggers: [TriggerEvent] = []
    @Published var cameras: [Camera] = []
    @Published var accounts: [Account] = []
    @Published var knownFaces: [KnownFace] = []
    // Whether the server has the Device Hub switched on. Drives whether the section exists at
    // all - see AppSection.visibleCases in RootView.
    @Published private(set) var deviceHubEnabled = false

    // Drives the UNLICENSED watermark over camera images and live video. Starts out - and stays -
    // true whenever the check can't be made: an older server has no such endpoint, and a request
    // that simply failed says nothing about the licence. Accusing a paying customer over a dropped
    // request would be a worse bug than missing the nag.
    @Published private(set) var isLicensed = true
    @Published var devices: [HubDevice] = []
    @Published var isLoading = false
    @Published var lastError: String?
    @Published var diagnostics: [DiagnosticEntry] = []
    @Published var lastUpdated: Date?
    @Published var selectedEvent: SecurityEvent?
    @Published var pushStatus: PushStatus?
    @Published var systemStatus: SystemStatus?
    @Published var pushAuthorization = "NOT REQUESTED"
    @Published var pushRegistrationEnabled: Bool
    @Published var pushMessage: String?
    @Published private(set) var discoveredServers: [DiscoveredServer] = []

    private(set) lazy var client = SentinelClient(model: self)
    private var localNetworkBrowser: NWBrowser?
    private var isConnecting = false
    private var pendingPushEventId: UUID?
    private var observers: [NSObjectProtocol] = []

    init() {
        serverURL = UserDefaults.standard.string(forKey: "serverURL") ?? ""
        remoteServerURL = UserDefaults.standard.string(forKey: "remoteServerURL") ?? ""
        pushRegistrationEnabled = UserDefaults.standard.bool(forKey: "pushRegistrationEnabled")
        observers.append(NotificationCenter.default.addObserver(
            forName: .sentinelPushTokenChanged,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in await self?.syncPushRegistration() }
        })
        observers.append(NotificationCenter.default.addObserver(
            forName: .sentinelPushOpened,
            object: nil,
            queue: .main
        ) { [weak self] notification in
            guard let value = notification.object as? String, let id = UUID(uuidString: value) else { return }
            Task { @MainActor in await self?.openPushEvent(id) }
        })
        observers.append(NotificationCenter.default.addObserver(
            forName: .sentinelPushRegistrationFailed,
            object: nil,
            queue: .main
        ) { [weak self] notification in
            guard let message = notification.object as? String else { return }
            Task { @MainActor in
                self?.pushMessage = "APNs registration failed: \(message)"
                self?.log("APNs registration failed: \(message)", level: .error)
            }
        })
        if let value = PushNotificationCoordinator.shared.takePendingEventId(),
           let id = UUID(uuidString: value) {
            Task { await openPushEvent(id) }
        }
        Task { await refreshPushAuthorization() }
    }

    var isConfigured: Bool { URL(string: serverURL)?.host != nil }
    var hasRemoteServerURL: Bool { URL(string: remoteServerURL)?.host != nil }
    var requiresLogin: Bool { authStatus?.enabled == true && authStatus?.authenticated == false }
    // Absent role (older server, or login not required) reads as Admin - matches the server's
    // own AuthGateMiddleware treatment of "no login required" as full access for everyone.
    var isAdmin: Bool { (authStatus?.role ?? "Admin") == "Admin" }

    func connect(resetNetworkSession: Bool = false) async {
        guard isConfigured else {
            connectionState = .notConfigured
            return
        }
        guard !isConnecting else {
            log("Connection request skipped because one is already active", level: .notice)
            return
        }

        isConnecting = true
        defer { isConnecting = false }
        log("Connecting to \(serverURL)")
        let wasOffline = connectionState == .offline
        if connectionState != .online {
            connectionState = .checking
        }
        // A URLSession created while local-network access was denied can retain the old
        // unsatisfied network path. Recreate it on each explicit connection attempt so a
        // newly-granted privacy decision takes effect immediately.
        if resetNetworkSession || wasOffline {
            client.resetSession()
        }
        startServerDiscovery()
        // Give macOS a moment to present/register the local-network permission before the
        // first HTTP request. The browser stays alive for the app's lifetime.
        try? await Task.sleep(for: .milliseconds(500))
        await resolveActiveServerURL()
        do {
            authStatus = try await client.authStatus()
            if requiresLogin {
                log("Server requires dashboard authentication")
                connectionState = .authenticationRequired
                return
            }
            try await refreshAll()
            connectionState = .online
            log("Connection established; loaded \(events.count) events")
            await refreshPushStatus()
            await syncPushRegistration()
            selectPendingPushEvent()
        } catch {
            if Self.isCancellation(error) {
                log("Connection task cancelled by the app lifecycle", level: .notice)
                return
            }
            connectionState = .offline
            lastError = error.localizedDescription
            log("Connection failed: \(error.localizedDescription)", level: .error)
        }
    }

    // Prefers the local address whenever it's reachable at all (any HTTP response counts,
    // not just 2xx) and only falls back to the remote address on an actual network failure -
    // this runs at the top of every connect(), so moving between home and away self-heals on
    // the next reconnect without any WiFi/SSID detection.
    private func resolveActiveServerURL() async {
        guard hasRemoteServerURL else {
            activeServerURL = serverURL
            isUsingRemote = false
            return
        }
        if await client.probeReachable(serverURL) {
            activeServerURL = serverURL
            isUsingRemote = false
        } else {
            log("Local address unreachable; using remote address")
            activeServerURL = remoteServerURL
            isUsingRemote = true
        }
    }

    func refreshAll() async throws {
        isLoading = true
        defer { isLoading = false }

        // The alert feed is the primary screen. Load each resource independently so an
        // unavailable camera stream or an older server without trigger endpoints cannot hide
        // events that were returned successfully.
        var firstError: Error?
        do {
            events = try await client.events()
        } catch {
            if Self.isCancellation(error) { throw error }
            firstError = error
        }
        do {
            triggers = try await client.triggers()
        } catch {
            if Self.isCancellation(error) { throw error }
            firstError = firstError ?? error
        }
        do {
            cameras = try await client.cameras()
        } catch {
            if Self.isCancellation(error) { throw error }
            firstError = firstError ?? error
        }
        // Not folded into firstError: an older server has no such endpoint, and a 404 there
        // must not read as a failed connection - it just means no Device Hub.
        do {
            deviceHubEnabled = try await client.deviceHubEnabled().enabled
        } catch {
            if Self.isCancellation(error) { throw error }
            deviceHubEnabled = false
        }
        // Same treatment, opposite default - see isLicensed.
        do {
            isLicensed = try await client.licenseState().valid
        } catch {
            if Self.isCancellation(error) { throw error }
            isLicensed = true
        }

        if let firstError {
            lastError = firstError.localizedDescription
            if events.isEmpty { throw firstError }
        } else {
            lastError = nil
        }
        lastUpdated = .now
        selectPendingPushEvent()
    }

    private static func isCancellation(_ error: Error) -> Bool {
        if error is CancellationError { return true }
        return (error as? URLError)?.code == .cancelled
    }

    func log(_ message: String, level: DiagnosticLevel = .info) {
        diagnostics.insert(DiagnosticEntry(timestamp: .now, level: level, message: message), at: 0)
        if diagnostics.count > 100 {
            diagnostics.removeLast(diagnostics.count - 100)
        }
        print("[SentinelCore] [\(level.rawValue)] \(message)")
    }

    func clearDiagnostics() {
        diagnostics.removeAll()
    }

    // Browses for other SentinelCore servers on the LAN (used to populate ServerSetupView's
    // discovered-server list) and, as a side effect, is also what makes iOS/macOS prompt for
    // local-network permission in the first place - so this is started from ServerSetupView.onAppear
    // as well as from connect(), rather than gated behind isConfigured like the rest of connect() is.
    //
    // "_sentinelcore._tcp" rather than "_http._tcp": a dedicated service type means every result
    // this browser ever sees is actually a SentinelCore server, with no further filtering needed.
    func startServerDiscovery() {
        guard localNetworkBrowser == nil else { return }

        let browser = NWBrowser(
            for: .bonjour(type: "_sentinelcore._tcp", domain: nil),
            using: .tcp
        )
        browser.stateUpdateHandler = { [weak self] state in
            Task { @MainActor in
                guard let self else { return }
                switch state {
                case .ready:
                    self.log("Local-network discovery is ready")
                    if self.connectionState != .online {
                        await self.connect(resetNetworkSession: true)
                    }
                case let .waiting(error):
                    self.log("Waiting for local-network permission: \(error)", level: .notice)
                case let .failed(error):
                    self.log("Local-network discovery failed: \(error)", level: .error)
                case .cancelled:
                    self.log("Local-network discovery was cancelled", level: .notice)
                case .setup:
                    self.log("Requesting local-network access")
                @unknown default:
                    self.log("Local-network discovery entered an unknown state", level: .notice)
                }
            }
        }
        browser.browseResultsChangedHandler = { [weak self] results, _ in
            guard let self else { return }
            let currentIds = Set(results.compactMap(Self.serviceName(for:)))
            Task { @MainActor in
                self.discoveredServers.removeAll { !currentIds.contains($0.id) }
            }
            for result in results {
                guard let name = Self.serviceName(for: result),
                      case let .bonjour(txtRecord) = result.metadata else { continue }
                Task { @MainActor in
                    guard !self.discoveredServers.contains(where: { $0.id == name }) else { return }
                    self.resolveDiscoveredServer(endpoint: result.endpoint, name: name, txtRecord: txtRecord)
                }
            }
        }
        localNetworkBrowser = browser
        browser.start(queue: DispatchQueue(label: "com.sentinelcore.local-network-discovery"))
    }

    // nonisolated: called from NWBrowser's browseResultsChangedHandler, which runs on the
    // browser's own dispatch queue rather than the MainActor.
    private nonisolated static func serviceName(for result: NWBrowser.Result) -> String? {
        guard case let .service(name, _, _, _) = result.endpoint else { return nil }
        return name
    }

    // A Bonjour NWBrowser.Result's endpoint identifies a service by name, not by host/port -
    // briefly opening a connection to it and reading back currentPath.remoteEndpoint is the
    // standard way to resolve the actual address, per Apple's Network framework guidance.
    private func resolveDiscoveredServer(endpoint: NWEndpoint, name: String, txtRecord: NWTXTRecord) {
        let scheme: String
        if case let .string(value) = txtRecord.getEntry(for: "scheme") {
            scheme = value
        } else {
            scheme = "http"
        }

        let connection = NWConnection(to: endpoint, using: .tcp)
        connection.stateUpdateHandler = { [weak self] state in
            switch state {
            case .ready:
                defer { connection.cancel() }
                guard let self,
                      case let .hostPort(host, port) = connection.currentPath?.remoteEndpoint else { return }

                let hostString: String
                switch host {
                case .ipv4(let address): hostString = "\(address)"
                case .ipv6(let address): hostString = "\(address)"
                case .name(let value, _): hostString = value
                @unknown default: return
                }

                Task { @MainActor in
                    guard !self.discoveredServers.contains(where: { $0.id == name }) else { return }
                    self.discoveredServers.append(DiscoveredServer(
                        id: name, name: name, host: hostString, port: Int(port.rawValue), scheme: scheme))
                }
            case .failed:
                connection.cancel()
            default:
                break
            }
        }
        connection.start(queue: DispatchQueue(label: "com.sentinelcore.local-network-discovery.resolve"))
    }

    func signIn(username: String, password: String) async throws {
        try await client.login(username: username, password: password)
        authStatus = try await client.authStatus()
        try await refreshAll()
        connectionState = .online
        await refreshPushStatus()
        await syncPushRegistration()
    }

    func signOut() async {
        try? await client.logout()
        authStatus = try? await client.authStatus()
        connectionState = requiresLogin ? .authenticationRequired : .online
    }

    func enablePushNotifications() async {
        do {
            let granted = try await PushNotificationCoordinator.shared.requestAuthorization()
            await refreshPushAuthorization()
            if granted {
                pushRegistrationEnabled = true
                UserDefaults.standard.set(true, forKey: "pushRegistrationEnabled")
                pushMessage = "Notification permission granted. Waiting for an Apple device token…"
                await syncPushRegistration()
            } else {
                pushMessage = "Notifications were not allowed. You can change this in System Settings."
            }
        } catch {
            pushMessage = error.localizedDescription
            log("Notification permission request failed: \(error.localizedDescription)", level: .error)
        }
    }

    func disablePushNotifications() async {
        if connectionState == .online,
           !requiresLogin,
           let token = PushNotificationCoordinator.shared.currentDeviceToken {
            do {
                try await client.unregisterPushDevice(token: token)
            } catch {
                log("Could not unregister this device from Core: \(error.localizedDescription)", level: .notice)
            }
        }
        pushRegistrationEnabled = false
        UserDefaults.standard.set(false, forKey: "pushRegistrationEnabled")
        pushMessage = "Push notifications are disabled for this installation."
        await refreshPushStatus()
    }

    func sendTestPushNotification() async {
        do {
            let report = try await client.testPushNotification()
            pushMessage = report.delivered > 0
                ? "Test delivered to \(report.delivered) device(s)."
                : "Core attempted \(report.attempted) delivery(s), but none succeeded."
        } catch {
            pushMessage = error.localizedDescription
            log("Test push failed: \(error.localizedDescription)", level: .error)
        }
    }

    func refreshPushStatus() async {
        pushStatus = try? await client.pushStatus()
    }

    func refreshSystemStatus() async {
        systemStatus = try? await client.systemStatus()
    }

    private func refreshPushAuthorization() async {
        let status = await PushNotificationCoordinator.shared.authorizationStatus()
        switch status {
        case .authorized: pushAuthorization = "ENABLED"
        case .provisional: pushAuthorization = "PROVISIONAL"
        case .denied: pushAuthorization = "DENIED"
        case .notDetermined: pushAuthorization = "NOT REQUESTED"
        case .ephemeral: pushAuthorization = "EPHEMERAL"
        @unknown default: pushAuthorization = "UNKNOWN"
        }
    }

    private func syncPushRegistration() async {
        guard connectionState == .online,
              !requiresLogin,
              pushRegistrationEnabled,
              let token = PushNotificationCoordinator.shared.currentDeviceToken else {
            return
        }
        let authorization = await PushNotificationCoordinator.shared.authorizationStatus()
        guard authorization == .authorized || authorization == .provisional else { return }

        do {
            try await client.registerPushDevice(
                token: token,
                deviceName: PushNotificationCoordinator.shared.deviceName
            )
            await refreshPushStatus()
            pushMessage = "This device is registered with Sentinel Core."
            log("Registered this Apple device for push notifications")
        } catch {
            pushMessage = error.localizedDescription
            log("Push token registration failed: \(error.localizedDescription)", level: .error)
        }
    }

    private func openPushEvent(_ id: UUID) async {
        pendingPushEventId = id
        if events.first(where: { $0.id == id }) == nil, isConfigured, !requiresLogin {
            if let event = try? await client.event(id: id) {
                selectedEvent = event
                pendingPushEventId = nil
                return
            }
            try? await refreshAll()
        }
        selectPendingPushEvent()
    }

    private func selectPendingPushEvent() {
        guard let id = pendingPushEventId,
              let event = events.first(where: { $0.id == id }) else { return }
        selectedEvent = event
        pendingPushEventId = nil
    }
}

struct DiagnosticEntry: Identifiable {
    let id = UUID()
    let timestamp: Date
    let level: DiagnosticLevel
    let message: String
}

enum DiagnosticLevel: String {
    case info = "INFO"
    case notice = "NOTICE"
    case error = "ERROR"
}

enum ConnectionState: Equatable {
    case notConfigured, checking, online, authenticationRequired, offline

    var label: String {
        switch self {
        case .notConfigured: "Set server"
        case .checking: "Connecting…"
        case .online: "Connected"
        case .authenticationRequired: "Sign in required"
        case .offline: "Offline"
        }
    }
}
