import Combine
import Foundation
import Network
import UserNotifications

// A SentinelCore server found on the local network via Bonjour/mDNS - see
// AppModel.startServerDiscovery(). Purely a convenience for prefilling ServerSetupView's manual
// address field; nothing here is persisted directly. Mirrors ios/SentinelCore/App/AppModel.swift.
struct DiscoveredServer: Identifiable, Equatable {
    let id: String
    let name: String
    let host: String
    let port: Int
    let scheme: String

    var addressString: String { "\(scheme)://\(host):\(port)" }
}

// Trimmed port of ios/SentinelCore/App/AppModel.swift for a single-platform (tvOS) target - the
// connection/push machinery is identical, only the macOS-specific branches are gone. The one
// addition is `alertTakeover`, which drives the full-screen AI-verified-alert interrupt.
@MainActor
final class AppModel: ObservableObject {
    @Published var serverURL: String {
        didSet { UserDefaults.standard.set(serverURL, forKey: "serverURL") }
    }
    // No remote-address fallback here, unlike the iOS/macOS ports. That exists for a device that
    // leaves the house and has to reach the server the long way round; an Apple TV is bolted to a
    // TV on the same network as the server, so if the local address is unreachable the TV has no
    // working network either and a second address to try is just a reachability probe delaying
    // every connect.
    @Published private(set) var activeServerURL: String = ""
    @Published var connectionState: ConnectionState = .checking
    @Published var authStatus: AuthStatus?
    @Published var events: [SecurityEvent] = []
    @Published var triggers: [TriggerEvent] = []
    @Published var cameras: [Camera] = []
    // Whether the server has the Device Hub switched on - drives whether the tab exists at all.
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
    // Drives the full-screen "AI VERIFIED ALERT" takeover - set whenever a push-worthy event
    // arrives (foreground delivery) or is opened (banner tap while backgrounded/launching).
    @Published var alertTakeover: SecurityEvent?
    @Published var pushStatus: PushStatus?
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
        pushRegistrationEnabled = UserDefaults.standard.bool(forKey: "pushRegistrationEnabled")
        observers.append(NotificationCenter.default.addObserver(
            forName: .sentinelPushTokenChanged,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                // The failure side of APNs registration is logged below; log the success side
                // too, so the connection log answers "did this Apple TV ever get a token?"
                // rather than only ever showing silence when everything worked.
                self?.log("APNs device token received from Apple")
                await self?.syncPushRegistration()
            }
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
    var requiresLogin: Bool { authStatus?.enabled == true && authStatus?.authenticated == false }
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
        if resetNetworkSession || wasOffline {
            client.resetSession()
        }
        startServerDiscovery()
        try? await Task.sleep(for: .milliseconds(500))
        activeServerURL = serverURL
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

    func refreshAll() async throws {
        isLoading = true
        defer { isLoading = false }

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
        // Kept out of firstError: an older server has no such endpoint, and a 404 there means
        // "no Device Hub", not a failed connection.
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
        print("[SentinelCoreTV] [\(level.rawValue)] \(message)")
    }

    func clearDiagnostics() {
        diagnostics.removeAll()
    }

    // Browses for other SentinelCore servers on the LAN (used to populate ServerSetupView's
    // discovered-server list) and, as a side effect, is also what makes tvOS prompt for
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
                    // A browser that has failed never recovers on its own, and mDNS does hand
                    // back DefunctConnection (-65569) on a real Apple TV when the daemon's
                    // connection is torn out from under it. Without dropping the reference the
                    // guard at the top of startServerDiscovery() would refuse to ever build a
                    // new one, leaving the setup screen searching forever.
                    self.log("Local-network discovery failed: \(error); restarting", level: .error)
                    self.restartServerDiscovery()
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
        browser.start(queue: DispatchQueue(label: "com.sentinelcore.tv.local-network-discovery"))
    }

    // Tears the failed browser down and builds a fresh one after a short pause. The delay keeps
    // a persistently unavailable mDNS daemon from turning into a tight rebuild loop; the results
    // are cleared because anything this browser found can no longer be trusted to still be there.
    private func restartServerDiscovery() {
        localNetworkBrowser?.cancel()
        localNetworkBrowser = nil
        discoveredServers.removeAll()
        Task { [weak self] in
            try? await Task.sleep(for: .seconds(5))
            guard let self, !Task.isCancelled else { return }
            self.startServerDiscovery()
        }
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
        connection.start(queue: DispatchQueue(label: "com.sentinelcore.tv.local-network-discovery.resolve"))
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
                pushMessage = "Notifications were not allowed. You can change this in tvOS Settings."
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
            pushMessage = "This Apple TV is registered with Sentinel Core."
            log("Registered this Apple TV for push notifications")
        } catch {
            pushMessage = error.localizedDescription
            log("Push token registration failed: \(error.localizedDescription)", level: .error)
        }
    }

    // Fetches the alerted event (falling back to a full refresh if it isn't already loaded) and
    // drives the full-screen takeover - used both for a banner tap (backgrounded/launch) and for
    // an immediate foreground interrupt (see PushNotificationCoordinator's willPresent handling).
    private func openPushEvent(_ id: UUID) async {
        pendingPushEventId = id
        if let event = events.first(where: { $0.id == id }) {
            presentTakeover(event)
            return
        }
        if isConfigured, !requiresLogin {
            if let event = try? await client.event(id: id) {
                presentTakeover(event)
                return
            }
            try? await refreshAll()
        }
        selectPendingPushEvent()
    }

    private func presentTakeover(_ event: SecurityEvent) {
        selectedEvent = event
        alertTakeover = event
        pendingPushEventId = nil
    }

    private func selectPendingPushEvent() {
        guard let id = pendingPushEventId,
              let event = events.first(where: { $0.id == id }) else { return }
        presentTakeover(event)
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
