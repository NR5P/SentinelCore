import Combine
import Foundation
import OrganizerKit
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
    // Whether the server this app is talking to is older than it expects. Advisory: nothing in the
    // app behaves differently, it just says so - see ServerCompatibility.
    @Published private(set) var serverCompatibility: ServerCompatibility = .unknown
    @Published var connectionState: ConnectionState = .checking {
        didSet { updateOrganizerConnection() }
    }
    @Published var authStatus: AuthStatus?
    // The single Events list: motion and the AI's read of it, already joined server-side into one
    // row per moment. This replaced two separately-fetched lists (alerts and triggers), which had
    // the app showing the same moment twice whenever a camera forwarded to AI analysis.
    @Published var activity: [ActivityItem] = []
    // "Show hidden" - motion the AI rejected as nothing, plus anything still awaiting a verdict.
    // Not persisted: the confirmed-only list is the one to be looking at day to day.
    @Published var showHiddenActivity = false
    @Published var cameras: [Camera] = []
    // How the signed-in account has arranged the Live grid, set on a phone or in the dashboard.
    // Read-only on this device - see liveCameras.
    @Published private(set) var liveCameraOrder: [UUID] = []
    // Whether the server has the Device Hub switched on - drives whether the tab exists at all.
    @Published private(set) var deviceHubEnabled = false
    // Whether the server has Movies & TV switched on - drives whether the Movies tab exists.
    // Remembered, so the tab is there from the first frame of the next launch rather than
    // popping in once the server has answered.
    @Published private(set) var moviesEnabled: Bool {
        didSet { UserDefaults.standard.set(moviesEnabled, forKey: "moviesEnabled") }
    }

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

    // Notes, calendar, reminders and boards - see OrganizerKit. The TV keeps a small light copy
    // in its caches (tvOS may clear those, which only means the next launch re-reads it).
    let organizer: OrganizerEngine
    @Published var organizerLastKnownEnabled: Bool {
        didSet { UserDefaults.standard.set(organizerLastKnownEnabled, forKey: "organizerEnabled") }
    }
    private var organizerActive = true
    private var localNetworkBrowser: NWBrowser?
    private var isConnecting = false
    private var pendingPushEventId: UUID?
    private var observers: [NSObjectProtocol] = []

    init() {
        organizerLastKnownEnabled = UserDefaults.standard.object(forKey: "organizerEnabled") as? Bool ?? false
        moviesEnabled = UserDefaults.standard.bool(forKey: "moviesEnabled")
        do {
            organizer = try OrganizerEngine(directory: OrganizerDevice.directory, deviceId: OrganizerDevice.id, policy: .televisionDefault)
        } catch {
            organizer = try! OrganizerEngine(directory: nil, deviceId: OrganizerDevice.id, policy: .televisionDefault)
        }
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

    // The engine follows the connection: running while the app is online and in the foreground,
    // stopped otherwise.
    private func updateOrganizerConnection() {
        if connectionState == .online, organizerActive {
            if !organizer.isConnected { organizer.connect(AppOrganizerTransport(client: client)) }
        } else if organizer.isConnected {
            organizer.disconnect()
        }
    }

    func setOrganizerActive(_ active: Bool) {
        organizerActive = active
        if active, organizer.isConnected { Task { await organizer.sync() } }
        updateOrganizerConnection()
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
            try await establishSession()
        } catch {
            if Self.isCancellation(error) {
                log("Connection task cancelled by the app lifecycle", level: .notice)
                return
            }
            // The server generates its own certificate authority, which no Apple device trusts
            // out of the box - so an https:// address (the default that server discovery
            // advertises) would simply never connect on a fresh install. The server keeps its
            // plain-HTTP listener open precisely for app clients in this position, so fall back
            // to it rather than stranding the user. Mirrors the iOS/macOS app, including only
            // falling back to a server that proves it is this one (see ServerIdentity) - the
            // plain-HTTP requests carry the session cookie.
            if Self.isUntrustedCertificate(error),
               let fallback = Self.httpFallbackAddress(for: activeServerURL) {
                let httpsAddress = activeServerURL
                let identity = await ServerIdentity.check(httpsAddress)
                switch identity {
                case .mismatch:
                    log("The server at \(httpsAddress) presented a certificate its own authority didn't sign - not falling back to plain HTTP", level: .error)
                    connectionState = .offline
                    lastError = "Something at this address isn't your Sentinel Core server - its certificate wasn't issued by your server. Nothing was sent to it. If you reinstalled the server, save its address again in Settings."
                    return
                case .unavailable:
                    log("Couldn't check the certificate at \(httpsAddress) before falling back to plain HTTP", level: .notice)
                    failConnection(with: error)
                    return
                case .verified, .unknown:
                    break
                }
                log("This device does not trust the server's HTTPS certificate; trying the plain-HTTP port at \(fallback)", level: .notice)
                activeServerURL = fallback
                do {
                    try await establishSession()
                    if case .unknown(let leaf) = identity {
                        if let authority = await ServerIdentity.fetchAuthority(from: fallback),
                           ServerIdentity.remember(authority: authority, signing: leaf, for: httpsAddress) {
                            log("Remembered this server's certificate authority; plain-HTTP fallback now only goes to this server")
                        } else {
                            log("Couldn't remember the server's certificate authority; the next fallback is treated as a first one", level: .notice)
                        }
                    }
                    log("Connected over plain HTTP. For an encrypted connection instead, trust the server's certificate authority (ca.crt) on this device.", level: .notice)
                    return
                } catch {
                    if Self.isCancellation(error) {
                        log("Connection task cancelled by the app lifecycle", level: .notice)
                        return
                    }
                    activeServerURL = serverURL
                    failConnection(with: error)
                    return
                }
            }
            failConnection(with: error)
        }
    }

    // The post-address half of connect(), split out so the certificate-trust fallback above can
    // run it a second time against the plain-HTTP address.
    private func establishSession() async throws {
        authStatus = try await client.authStatus()
        // Before the requiresLogin return below, so the advisory is already known at the login
        // screen. Never throws - see checkServerCompatibility.
        await checkServerCompatibility()
        if requiresLogin {
            log("Server requires dashboard authentication")
            connectionState = .authenticationRequired
            return
        }
        try await refreshAll()
        connectionState = .online
        log("Connection established; loaded \(activity.count) events")
        await refreshPushStatus()
        await syncPushRegistration()
        selectPendingPushEvent()
    }

    // Deliberately swallows every failure. This is a diagnostic nicety layered on top of a
    // connection that has already succeeded - failing the connect() that called it, over a server
    // that merely declined to describe itself, would turn a cosmetic feature into an outage.
    private func checkServerCompatibility() async {
        do {
            serverCompatibility = ServerCompatibility.evaluate(try await client.serverHealth().build)
        } catch {
            serverCompatibility = .unknown
            log("Could not read the server's version: \(error.localizedDescription)", level: .info)
            return
        }
        if let advisory = serverCompatibility.advisory {
            log(advisory, level: .notice)
        }
    }

    private func failConnection(with error: Error) {
        connectionState = .offline
        lastError = Self.userFacingMessage(for: error)
        log("Connection failed: \(error.localizedDescription)", level: .error)
    }

    // URLError's wording is written for apps that talk to the public internet, so for the two
    // failures this app actually hits on a LAN it names the wrong problem. A distrusted
    // self-signed certificate ("...may be pretending to be...") reads as an active attack and
    // names no way out. A denied Local Network permission reports "The Internet connection
    // appears to be offline", which sends people to their router and their Wi-Fi when the fix is
    // a privacy toggle. Both get replaced with the real cause and a way out; everything else
    // keeps the system wording, which is accurate for the remaining cases.
    static func userFacingMessage(for error: Error) -> String {
        if isCertificateTrustFailure(error) {
            return "This device doesn't trust the certificate the server generated for itself. Use the http:// address, or install the server's ca.crt on this device to keep HTTPS."
        }
        if isLocalNetworkFailure(error) {
            return "Can't reach anything on your local network. Check this Apple TV is on the same network as the server - and if Sentinel Core was denied Local Network access, turn it back on under Settings → General → Privacy & Security → Local Network."
        }
        return error.localizedDescription
    }

    // -1009 has two causes here and the error carries nothing that separates them: the network
    // really is down, or the OS is refusing this app's LAN traffic specifically. A denied grant is
    // the far more likely one for a server on the same subnet - it is also the one users cannot
    // guess - so the message has to raise it without asserting it over the mundane case.
    static func isLocalNetworkFailure(_ error: Error) -> Bool {
        (error as? URLError)?.code == .notConnectedToInternet
    }

    // Everything URLSession reports when a TLS handshake completed enough to judge the server's
    // certificate and the judgement was "no". Distinct from the host being unreachable: these
    // mean the server is up and answering, just not trusted by this particular device.
    static func isCertificateTrustFailure(_ error: Error) -> Bool {
        guard let code = (error as? URLError)?.code else { return false }
        switch code {
        case .serverCertificateUntrusted, .serverCertificateHasUnknownRoot,
             .serverCertificateHasBadDate, .serverCertificateNotYetValid, .secureConnectionFailed:
            return true
        default:
            return false
        }
    }

    // https://host[:port] -> http://host:3320, the dashboard's shipped plain-HTTP port. A custom
    // HTTP port can't be derived from the HTTPS address, so the default is the only sensible guess.
    // What may start the plain-HTTP fallback: the certificate was judged and not trusted. Narrower
    // than isCertificateTrustFailure, which also counts a bare handshake failure (-1200) that
    // anything can produce without presenting a certificate. Mirrors the iOS/macOS app.
    static func isUntrustedCertificate(_ error: Error) -> Bool {
        guard let code = (error as? URLError)?.code else { return false }
        switch code {
        case .serverCertificateUntrusted, .serverCertificateHasUnknownRoot,
             .serverCertificateHasBadDate, .serverCertificateNotYetValid:
            return true
        default:
            return false
        }
    }

    static func httpFallbackAddress(for address: String) -> String? {
        guard var components = URLComponents(string: address.trimmingCharacters(in: .whitespacesAndNewlines)),
              components.scheme == "https" else {
            return nil
        }
        components.scheme = "http"
        components.port = 3320
        return components.string
    }

    func refreshAll() async throws {
        isLoading = true
        defer { isLoading = false }

        var firstError: Error?
        do {
            activity = try await client.activity(includeUnconfirmed: showHiddenActivity)
        } catch {
            if Self.isCancellation(error) { throw error }
            firstError = error
        }
        do {
            cameras = try await client.cameras()
        } catch {
            if Self.isCancellation(error) { throw error }
            firstError = firstError ?? error
        }
        // Kept out of firstError for the same reason as the two below: a server older than this
        // feature has no such endpoint, and the grid falling back to the camera list's own order
        // is a fine outcome, not a failed connection.
        do {
            liveCameraOrder = try await client.liveOrder()
        } catch {
            if Self.isCancellation(error) { throw error }
        }
        // Kept out of firstError: an older server has no such endpoint, and a 404 there means
        // "no Device Hub", not a failed connection.
        do {
            deviceHubEnabled = try await client.deviceHubEnabled().enabled
        } catch {
            if Self.isCancellation(error) { throw error }
            deviceHubEnabled = false
        }
        if deviceHubEnabled {
            do {
                devices = try await client.devices()
                let snapshot = try await client.deviceStates()
                for state in snapshot.devices {
                    guard let index = devices.firstIndex(where: { $0.id == state.id }) else { continue }
                    devices[index].power = state.power
                    devices[index].brightness = state.brightness
                    devices[index].available = state.available
                    devices[index].measurements = state.measurements
                }
            } catch {
                if Self.isCancellation(error) { throw error }
                log("Could not prime Device Hub state during startup: \(error.localizedDescription)", level: .notice)
            }
        } else {
            devices = []
        }
        // Same as the Device Hub: its own endpoint that answers even while the section is off,
        // and a 404 from a server older than Movies & TV means "no Movies tab".
        do {
            moviesEnabled = try await client.mediaEnabled().enabled
        } catch {
            if Self.isCancellation(error) { throw error }
            moviesEnabled = false
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
            if activity.isEmpty { throw firstError }
        } else {
            lastError = nil
        }
        lastUpdated = .now
        selectPendingPushEvent()
    }

    // The cameras in the order the signed-in account arranged them on a phone or in the dashboard:
    // saved placements first (any pointing at a camera that no longer exists dropped), then
    // anything that order has never seen - a camera added since - at the end. Same rule the server
    // and the other apps apply.
    var liveCameras: [Camera] {
        var remaining = cameras
        var ordered: [Camera] = []
        for id in liveCameraOrder {
            guard let index = remaining.firstIndex(where: { $0.id == id }) else { continue }
            ordered.append(remaining.remove(at: index))
        }
        return ordered + remaining
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
    // A push notification names an alert, and alerts are the analyzed half of the Events list -
    // derived rather than fetched separately, so there's still exactly one request behind the
    // list. Motion rows have no alert id and correctly never match a notification.
    var events: [SecurityEvent] { activity.compactMap(\.asSecurityEvent) }

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
