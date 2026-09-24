import Combine
import Foundation
import Network
import OrganizerKit
import OSLog
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
    static let shared = AppModel()

    @Published var serverURL: String {
        didSet { UserDefaults.standard.set(serverURL, forKey: "serverURL") }
    }
    // Optional Cloudflare Tunnel (or any other externally-reachable) address, used only when
    // the local address can't be reached - see connect() for the resolution order.
    @Published var remoteServerURL: String {
        didSet { UserDefaults.standard.set(remoteServerURL, forKey: "remoteServerURL") }
    }
    @Published private(set) var activeServerURL: String = ""
    // Whether the "this device as a camera" sheet is up. A flag on the model rather than a link
    // in one screen because three things open it: the Live toolbar, Settings, and streaming that
    // resumed on its own after a relaunch. See DeviceCameraView.
    @Published var showDeviceCamera = false
    @Published private(set) var isUsingRemote = false
    // True while this session is talking plain HTTP to an HTTPS-configured server because this
    // device doesn't trust the server's self-generated certificate - see connect(). Surfaced in
    // SettingsView so the downgrade is never silent.
    @Published private(set) var usingHttpFallback = false
    // Whether the server this app is talking to is older than it expects. Advisory: nothing in the
    // app behaves differently, it just says so - see ServerCompatibility.
    @Published private(set) var serverCompatibility: ServerCompatibility = .unknown
    @Published var connectionState: ConnectionState = .checking {
        didSet { updateOrganizerConnection() }
    }
    @Published var authStatus: AuthStatus?
    // The single Events list: motion and the AI's read of it, already joined server-side into one
    // row per moment. This replaced two separately-fetched lists (events and triggers), which had
    // the app showing the same moment twice whenever a camera forwarded to AI analysis.
    @Published var activity: [ActivityItem] = []
    // "Show hidden" - motion the AI rejected as nothing, plus anything still awaiting a verdict.
    // Not persisted: the confirmed-only list is the one to be looking at day to day.
    @Published var showHiddenActivity = false
    @Published var cameras: [Camera] = []
    // How this account has arranged the Live grid, newest server answer first. Kept apart from
    // `cameras` on purpose: that list stays the canonical, order-agnostic one every other screen
    // reads, and only the Live grid cares where the tiles sit. See liveCameras.
    @Published private(set) var liveCameraOrder: [UUID] = []
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

    // Notes, calendar, reminders and boards - see OrganizerKit. Created up front and independent
    // of the connection: this device's copy is usable offline, before the server has ever been
    // reached this launch.
    let organizer: OrganizerEngine
    // Whether the server last said the Organizer is switched on, remembered so the section is
    // there on an offline launch too.
    @Published var organizerLastKnownEnabled: Bool {
        didSet {
            UserDefaults.standard.set(organizerLastKnownEnabled, forKey: "organizerEnabled")
            #if os(iOS)
            organizerWidgets?.setEnabled(organizerLastKnownEnabled)
            #endif
        }
    }
    // The same, but nil when the server has never answered on this device.
    var organizerEnabledIfKnown: Bool? { UserDefaults.standard.object(forKey: "organizerEnabled") as? Bool }
    private var organizerActive = true
    #if os(iOS)
    // Keeps the Home Screen widgets' copy current - see OrganizerWidgets.swift.
    private var organizerWidgets: OrganizerWidgetBridge?
    #endif
    private var localNetworkBrowser: NWBrowser?
    private var isConnecting = false
    private var pendingPushEventId: UUID?
    private var observers: [NSObjectProtocol] = []

    init() {
        serverURL = UserDefaults.standard.string(forKey: "serverURL") ?? ""
        remoteServerURL = UserDefaults.standard.string(forKey: "remoteServerURL") ?? ""
        pushRegistrationEnabled = UserDefaults.standard.bool(forKey: "pushRegistrationEnabled")
        organizerLastKnownEnabled = UserDefaults.standard.object(forKey: "organizerEnabled") as? Bool ?? false
        do {
            organizer = try OrganizerEngine(directory: OrganizerDevice.directory, deviceId: OrganizerDevice.id, policy: OrganizerDevice.savedPolicy)
        } catch {
            // A copy that can't be opened (a corrupt file, a full disk) must not take the rest of
            // the app down with it; run from memory and say so in the diagnostics.
            organizer = try! OrganizerEngine(directory: nil, deviceId: OrganizerDevice.id, policy: OrganizerDevice.savedPolicy)
        }
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
        organizer.onAlertsChanged = { alerts in OrganizerNotifications.schedule(alerts) }
        #if os(iOS)
        organizerWidgets = OrganizerWidgetBridge(engine: organizer, enabled: organizerLastKnownEnabled)
        #endif
    }

    // The engine follows the connection: running while the app is online and in the foreground,
    // stopped otherwise. Its own copy and queue carry on either way.
    private func updateOrganizerConnection() {
        if connectionState == .online, organizerActive {
            if !organizer.isConnected { organizer.connect(AppOrganizerTransport(client: client)) }
        } else if organizer.isConnected {
            organizer.disconnect()
        }
    }

    #if os(iOS)
    // Woken in the background - by the server's silent "Organizer changed" push, or by a
    // background refresh - to bring this device's copy and the Home Screen widgets up to date.
    // No window and about 30 seconds, so one sync and no long poll (OrganizerEngine.syncOnce).
    // The address last used may be the wrong one now (the phone left home), so a failed sync
    // re-picks between the local and remote address once, as connect() would.
    func refreshOrganizerInBackground() async {
        guard isConfigured, organizerLastKnownEnabled else { return }
        if activeServerURL.isEmpty { await resolveActiveServerURL() }
        let tried = activeServerURL
        var online = await organizer.syncOnce(AppOrganizerTransport(client: client))
        if !online {
            await resolveActiveServerURL()
            if activeServerURL != tried {
                online = await organizer.syncOnce(AppOrganizerTransport(client: client))
            }
        }
        // An untrusted HTTPS certificate on the local address: the plain-HTTP port, as connect() does.
        if !online, !isUsingRemote, let fallback = Self.httpFallbackAddress(for: activeServerURL),
           await client.probeReachable(fallback) {
            activeServerURL = fallback
            online = await organizer.syncOnce(AppOrganizerTransport(client: client))
        }
        log("Background Organizer refresh \(online ? "synced" : "could not reach the server")", level: online ? .info : .notice)
        organizerWidgets?.writeNow()
    }
    #endif

    // Called from the scene phase: a background app shouldn't hold a long poll open.
    func setOrganizerActive(_ active: Bool) {
        organizerActive = active
        #if os(iOS)
        if active { organizerWidgets?.appBecameActive() } else { OrganizerBackgroundRefresh.schedule() }
        #endif
        if active, organizer.isConnected {
            Task { await organizer.sync() }
        }
        updateOrganizerConnection()
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
        // The configured address is logged rather than assumed. A field typed into but never
        // saved leaves this pointing somewhere else entirely, and "which address did it actually
        // use" turned out to be the single hardest thing to establish from outside the app.
        log("Connecting to \(serverURL)\(hasRemoteServerURL ? " (remote fallback \(remoteServerURL))" : "")")
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
        usingHttpFallback = false
        await resolveActiveServerURL()
        do {
            try await establishSession()
        } catch {
            if Self.isCancellation(error) {
                log("Connection task cancelled by the app lifecycle", level: .notice)
                return
            }
            // The server generates its own certificate authority, which no Apple device trusts
            // out of the box - so a fresh install pointed at the https:// address (the default
            // that server discovery advertises) would simply never connect. The server keeps its
            // plain-HTTP listener open precisely for app clients in this position (the Android
            // app uses nothing else), so fall back to it rather than stranding the user. Local
            // address only: a remote address crosses the open Internet, where quietly dropping
            // encryption is not an acceptable trade.
            if !isUsingRemote, Self.isCertificateTrustFailure(error),
               let fallback = Self.httpFallbackAddress(for: activeServerURL) {
                log("This device does not trust the server's HTTPS certificate; trying the plain-HTTP port at \(fallback)", level: .notice)
                if await client.probeReachable(fallback) {
                    activeServerURL = fallback
                    usingHttpFallback = true
                    do {
                        try await establishSession()
                        log("Connected over plain HTTP. For an encrypted connection instead, trust the server's certificate authority (ca.crt) on this device - see DEPLOYMENT.md section 14a.", level: .notice)
                    } catch {
                        usingHttpFallback = false
                        if Self.isCancellation(error) {
                            log("Connection task cancelled by the app lifecycle", level: .notice)
                            return
                        }
                        failConnection(with: error)
                    }
                    return
                }
                log("The plain-HTTP port at \(fallback) did not answer either", level: .notice)
            }
            failConnection(with: error)
        }
    }

    // The post-address-resolution half of connect(): everything from the first real request to
    // fully online. Split out so the certificate-trust fallback in connect() can run it a second
    // time against the plain-HTTP address.
    private func establishSession() async throws {
        authStatus = try await client.authStatus()
        if requiresLogin, let credential = CredentialStore.load(for: serverURL) {
            log("The saved server session expired; renewing it with the Keychain login", level: .notice)
            do {
                try await client.login(username: credential.username, password: credential.password)
                authStatus = try await client.authStatus()
            } catch ClientError.authenticationRequired {
                // A password reset or deleted account must not cause a failed automatic login on
                // every launch. Forget the stale secret and show the normal sign-in screen.
                CredentialStore.delete(for: serverURL)
                authStatus = try await client.authStatus()
            }
        }
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
            log("Could not read the server's version: \(Self.describe(error))", level: .info)
            return
        }
        if let advisory = serverCompatibility.advisory {
            log(advisory, level: .notice)
        }
    }

    private func failConnection(with error: Error) {
        connectionState = .offline
        lastError = Self.userFacingMessage(for: error)
        log("Connection failed against \(activeServerURL): \(Self.describe(error))", level: .error)
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
            return "Can't reach anything on your local network. Check this device is on the same network as the server - and if Sentinel Core was denied Local Network access, turn it back on under \(localNetworkSettingsPath)."
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

    private static var localNetworkSettingsPath: String {
        #if os(macOS)
        "System Settings → Privacy & Security → Local Network"
        #else
        "Settings → Privacy & Security → Local Network"
        #endif
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
    // HTTP port can't be derived from the HTTPS address, so the default is the only sensible
    // guess - and connect() probes the result before actually switching to it.
    static func httpFallbackAddress(for address: String) -> String? {
        guard var components = URLComponents(string: address.trimmingCharacters(in: .whitespacesAndNewlines)),
              components.scheme == "https" else {
            return nil
        }
        components.scheme = "http"
        components.port = 3320
        return components.string
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
            log("Local address \(serverURL) answered; using it")
            activeServerURL = serverURL
            isUsingRemote = false
        } else {
            log("Local address \(serverURL) unreachable; using remote address \(remoteServerURL)", level: .notice)
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
        // Not folded into firstError, same reasoning as the Device Hub below: a server older than
        // this feature has no such endpoint, and the Live grid falling back to the camera list's
        // own order is a fine outcome - not a failed connection.
        do {
            liveCameraOrder = try await client.liveOrder()
        } catch {
            if Self.isCancellation(error) { throw error }
        }
        // Not folded into firstError: an older server has no such endpoint, and a 404 there
        // must not read as a failed connection - it just means no Device Hub.
        do {
            deviceHubEnabled = try await client.deviceHubEnabled().enabled
        } catch {
            if Self.isCancellation(error) { throw error }
            deviceHubEnabled = false
        }
        // Prime the Device Hub at app startup rather than waiting until its tab is first opened.
        // The server's state endpoint first makes sure its MQTT subscriptions are ready, so a
        // retained device report can tell us whether each light is currently on or off.
        if deviceHubEnabled {
            do {
                devices = try await client.devices()
                let snapshot = try await client.deviceStates()
                for state in snapshot.devices {
                    guard let index = devices.firstIndex(where: { $0.id == state.id }) else { continue }
                    devices[index].power = state.power
                    devices[index].brightness = state.brightness
                    devices[index].available = state.available
                }
            } catch {
                if Self.isCancellation(error) { throw error }
                log("Could not prime Device Hub state during startup: \(Self.describe(error))", level: .notice)
            }
        } else {
            devices = []
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
            // Logged even when it isn't rethrown: a partial refresh still leaves the app looking
            // connected while quietly missing a resource, and without this that left no trace.
            log("Refresh finished with an error: \(Self.describe(firstError))", level: .error)
            if activity.isEmpty { throw firstError }
        } else {
            lastError = nil
        }
        lastUpdated = .now
        selectPendingPushEvent()
    }

    // MARK: - Live grid order

    // The cameras as this account has arranged them: saved placements first (any pointing at a
    // camera that no longer exists dropped), then anything the saved order has never seen - a
    // camera added since - at the end. Same rule the server applies, repeated here because the
    // local camera list can be ahead of the last order fetched from it.
    var liveCameras: [Camera] {
        var remaining = cameras
        var ordered: [Camera] = []
        for id in liveCameraOrder {
            guard let index = remaining.firstIndex(where: { $0.id == id }) else { continue }
            ordered.append(remaining.remove(at: index))
        }
        return ordered + remaining
    }

    // Drop a dragged tile onto another one: the dragged camera takes that tile's place and the
    // rest close up behind it.
    func moveLiveCamera(_ movingId: UUID, toPositionOf targetId: UUID) {
        var ids = liveCameras.map(\.id)
        guard let from = ids.firstIndex(of: movingId), let to = ids.firstIndex(of: targetId), from != to else { return }
        ids.remove(at: from)
        ids.insert(movingId, at: to)
        applyLiveCameraOrder(ids)
    }

    private func applyLiveCameraOrder(_ ids: [UUID]) {
        // Shown immediately and saved in the background: the arrangement is what the person just
        // asked for, and a round trip to the server is no reason to make them watch a tile snap
        // back before it moves.
        liveCameraOrder = ids
        Task {
            do {
                try await client.saveLiveOrder(ids)
            } catch {
                log("Could not save the live camera order: \(error.localizedDescription)", level: .error)
            }
        }
    }

    private static func isCancellation(_ error: Error) -> Bool {
        if error is CancellationError { return true }
        return (error as? URLError)?.code == .cancelled
    }

    // Mirrored into the unified log as well as the in-app Diagnostics screen. print() only reaches
    // a terminal that launched the app, which an app launched from the Finder or /Applications
    // never has - so when a connection problem had to be diagnosed, the only copy of the evidence
    // was on a screen someone had to read out loud. With this, the same lines are available to
    //   log show --predicate 'subsystem == "com.sentinelcore.client"' --last 10m
    // from any terminal, on this Mac or over SSH.
    func log(_ message: String, level: DiagnosticLevel = .info) {
        diagnostics.insert(DiagnosticEntry(timestamp: .now, level: level, message: message), at: 0)
        if diagnostics.count > 100 {
            diagnostics.removeLast(diagnostics.count - 100)
        }
        // .public because none of this is personal data - it's server addresses and error codes,
        // and redacted logs would defeat the entire point of having them.
        Self.logger.log(level: level.osLogType, "\(message, privacy: .public)")
    }

    private static let logger = Logger(subsystem: "com.sentinelcore.client", category: "connection")

    // localizedDescription alone is what produced "The Internet connection appears to be offline",
    // a message that covers a genuinely dead network, a sandboxed app denied local-network access,
    // and a server that isn't listening - three completely different problems with three
    // completely different fixes. The domain and code tell those apart, so they get logged too.
    static func describe(_ error: Error) -> String {
        let nsError = error as NSError
        var parts = ["\(error.localizedDescription) [\(nsError.domain) \(nsError.code)"]
        if let urlError = error as? URLError {
            parts.append(" \(Self.name(for: urlError.code))")
            if let failing = urlError.failingURL {
                parts.append(" url=\(failing.absoluteString)")
            }
        }
        parts.append("]")
        if let underlying = nsError.userInfo[NSUnderlyingErrorKey] as? NSError {
            parts.append(" underlying=(\(underlying.domain) \(underlying.code))")
        }
        return parts.joined()
    }

    // The handful of URLError codes this app actually runs into, named rather than numbered. Every
    // one of these has been mistaken for another at some point while debugging a connection.
    private static func name(for code: URLError.Code) -> String {
        switch code {
        case .notConnectedToInternet: "notConnectedToInternet - no usable network path; on a sandboxed Mac app this is also what a denied Local Network permission looks like"
        case .cannotConnectToHost: "cannotConnectToHost - reached the host, nothing listening on that port"
        case .cannotFindHost: "cannotFindHost - name did not resolve"
        case .timedOut: "timedOut - no response; typically a firewall dropping packets"
        case .secureConnectionFailed: "secureConnectionFailed - TLS handshake rejected"
        case .serverCertificateUntrusted: "serverCertificateUntrusted - the server's CA is not trusted on this device"
        case .serverCertificateHasBadDate: "serverCertificateHasBadDate"
        case .serverCertificateNotYetValid: "serverCertificateNotYetValid"
        case .appTransportSecurityRequiresSecureConnection: "appTransportSecurityRequiresSecureConnection - plain HTTP blocked by ATS"
        default: "URLError \(code.rawValue)"
        }
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
        // Save only after the server accepted the credentials. A Keychain write failure should
        // not undo a valid login; it only means this session cannot renew itself automatically.
        do {
            try CredentialStore.save(StoredCredential(username: username, password: password), for: serverURL)
        } catch {
            log("Signed in, but could not save the login in Keychain: \(error.localizedDescription)", level: .error)
        }
        authStatus = try await client.authStatus()
        try await refreshAll()
        connectionState = .online
        await refreshPushStatus()
        await syncPushRegistration()
    }

    // Re-reads whether the server requires a login. Called after the Dashboard Login screen turns
    // that requirement on or off, so the rest of the app (the Account section, the sign-in gate)
    // stops describing the state it was in a moment ago.
    func refreshAuthStatus() async {
        authStatus = try? await client.authStatus()
        connectionState = requiresLogin ? .authenticationRequired : .online
    }

    // Set by the License screen after a successful install, so the unlicensed watermark clears
    // straight away rather than at the next full refresh. isLicensed is otherwise private(set)
    // precisely so nothing else can decide this on its own.
    func applyLicenseState(_ state: LicenseState) {
        isLicensed = state.valid
    }

    func signOut() async {
        CredentialStore.delete(for: serverURL)
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

    // A push notification names an alert, and alerts are the analyzed half of the Events list -
    // derived rather than fetched separately, so there's still exactly one request behind the
    // list. Motion rows have no alert id and correctly never match a notification.
    var events: [SecurityEvent] { activity.compactMap(\.asSecurityEvent) }

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

    var osLogType: OSLogType {
        switch self {
        case .info: .info
        case .notice: .default
        case .error: .error
        }
    }
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
