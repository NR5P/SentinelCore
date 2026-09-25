import Foundation

@MainActor
final class SentinelClient {
    private unowned let model: AppModel
    private var session: URLSession

    init(model: AppModel) {
        self.model = model
        SessionCookieStore.restore()
        session = Self.makeSession()
    }

    // Separate from `session` only for its timeouts: the main session gives up after 15 seconds,
    // which would cut off the Organizer's 25-second long poll every time. Same shared cookie
    // storage, so it is signed in exactly when the rest of the app is.
    private lazy var organizerSession: URLSession = Self.makeOrganizerSession()

    private static func makeOrganizerSession() -> URLSession {
        let configuration = URLSessionConfiguration.default
        configuration.httpCookieStorage = .shared
        configuration.httpShouldSetCookies = true
        configuration.timeoutIntervalForRequest = 60
        configuration.timeoutIntervalForResource = 600
        configuration.waitsForConnectivity = false
        return URLSession(configuration: configuration)
    }

    // The Organizer's way to the server - the same session cookie and chosen address as every
    // other request. Hands back any status rather than throwing, since the sync engine gives 401
    // and 404 their own meanings. See the iOS app's copy for the longer story.
    func organizerRequest(
        method: String, path: String, query: [URLQueryItem], body: Data?, contentType: String?, timeout: TimeInterval?
    ) async throws -> (status: Int, data: Data) {
        guard let url = url(path: path, query: query) else { throw ClientError.invalidServerAddress }
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.httpBody = body
        if let contentType { request.setValue(contentType, forHTTPHeaderField: "Content-Type") }
        if let timeout { request.timeoutInterval = timeout }
        let (data, response) = try await organizerSession.data(for: request)
        return ((response as? HTTPURLResponse)?.statusCode ?? 0, data)
    }

    func resetSession() {
        session.invalidateAndCancel()
        session = Self.makeSession()
        organizerSession.invalidateAndCancel()
        organizerSession = Self.makeOrganizerSession()
        model.log("Created a fresh network session")
    }

    private static func makeSession() -> URLSession {
        let configuration = URLSessionConfiguration.default
        configuration.httpCookieStorage = .shared
        configuration.httpShouldSetCookies = true
        configuration.timeoutIntervalForRequest = 10
        configuration.timeoutIntervalForResource = 15
        // This client only targets a server on the current LAN. Waiting for a network path
        // can leave the UI in CONNECTING for the full resource timeout when that host is
        // powered off or the Mac/device is on a different network. Fail promptly so the
        // user gets LINK LOST and can retry as soon as the server is available.
        configuration.waitsForConnectivity = false
        return URLSession(configuration: configuration)
    }

    func authStatus() async throws -> AuthStatus {
        try await get("/api/auth/status")
    }

    // Unauthenticated on the server side, so this is readable at the login screen too - which is
    // where knowing the server is too old is arguably most useful.
    func serverHealth() async throws -> ServerHealth {
        try await get("/api/health")
    }

    func event(id: UUID) async throws -> SecurityEvent {
        try await get("/api/events/\(id.uuidString)")
    }

    // The merged Events list - motion and the AI's read of it, joined server-side into one row
    // per moment, and the only list this app fetches now. It replaced separate calls to
    // /api/events and /api/triggers, which returned two rows for one moment whenever a camera
    // forwarded to AI analysis. Those endpoints still exist for older installed builds of this
    // app; nothing here calls them.
    func activity(notifiedOnly: Bool = false, includeUnconfirmed: Bool = false) async throws -> [ActivityItem] {
        var items = [URLQueryItem(name: "limit", value: "500")]
        if notifiedOnly { items.append(.init(name: "notifiedOnly", value: "true")) }
        if includeUnconfirmed { items.append(.init(name: "includeUnconfirmed", value: "true")) }
        return try await get("/api/activity", query: items)
    }

    func cameras() async throws -> [Camera] {
        try await get("/api/cameras")
    }

    // Read-only here, unlike the phone apps and the dashboard: an Apple TV shows the arrangement
    // its viewer made elsewhere, but there's no sensible way to drag a tile with a remote, so
    // nothing on this side ever writes it back.
    func liveOrder() async throws -> [UUID] {
        let response: LiveOrder = try await get("/api/live-order")
        return response.cameraIds
    }

    func createCamera(_ input: CameraInput) async throws -> Camera {
        try await send("/api/cameras", method: "POST", body: input)
    }

    // MARK: - Alarm (control only - see the note on AlarmStatus)

    func alarmStatus() async throws -> AlarmStatus {
        try await get("/api/alarm/status")
    }

    func armAlarm(mode: String, force: Bool, pin: String?) async throws -> AlarmCommandReply {
        try await send("/api/alarm/arm", method: "POST", body: AlarmArmInput(mode: mode, force: force, pin: pin))
    }

    func disarmAlarm(pin: String?) async throws -> AlarmCommandReply {
        try await send("/api/alarm/disarm", method: "POST", body: AlarmDisarmInput(pin: pin))
    }

    // MARK: - Device Hub (control only - see the note on HubDevice)

    func deviceHubEnabled() async throws -> DeviceHubEnabled {
        try await get("/api/device-hub/enabled")
    }

    // Readable by any signed-in account, not just an Admin, precisely so this app can ask - see
    // the note on the endpoint in LicenseEndpoints.cs.
    func licenseState() async throws -> LicenseState {
        try await get("/api/license/status")
    }

    func devices() async throws -> [HubDevice] {
        try await get("/api/devices")
    }

    func deviceStates() async throws -> HubDeviceStateSnapshot {
        try await get("/api/devices/state")
    }

    func sendDeviceCommand(id: UUID, action: String? = nil, brightness: Int? = nil) async throws -> DeviceCommandResult {
        try await send(
            "/api/devices/\(id.uuidString)/command",
            method: "POST",
            body: DeviceCommandInput(action: action, brightness: brightness)
        )
    }

    func updateCamera(id: UUID, _ input: CameraInput) async throws {
        let _: EmptyResponse = try await send("/api/cameras/\(id.uuidString)", method: "PUT", body: input)
    }

    func deleteCamera(id: UUID) async throws {
        let _: EmptyResponse = try await send(
            "/api/cameras/\(id.uuidString)",
            method: "DELETE",
            body: Optional<String>.none
        )
    }

    func probeCamera(_ request: CameraProbeRequest) async throws -> CameraProbeResult {
        try await send("/api/cameras/discovery/probe", method: "POST", body: request)
    }

    // Continuous-move semantics: the camera keeps moving at this velocity until ptzStop is
    // called - there's no "move by this much and stop automatically" in this mode, so the
    // joystick UI is expected to call ptzStop on release, not just ptzMove once.
    // Which live transport a tile actually landed on, and why. Logged by the server, not stored -
    // the question it answers ("was the TV on MSE or HLS at 9pm, and what pushed it down") is asked
    // of the log alongside the go2rtc and ffmpeg lines that explain it.
    func reportLivePath(
        cameraId: UUID, tier: String, outcome: String, reason: String?, elapsedMs: Int
    ) async throws {
        let _: EmptyResponse = try await send(
            "/api/cameras/\(cameraId.uuidString)/live-path",
            method: "POST",
            body: LivePathReportBody(tier: tier, outcome: outcome, reason: reason, elapsedMs: elapsedMs)
        )
    }

    func ptzMove(cameraId: UUID, pan: Double, tilt: Double, zoom: Double) async throws {
        let _: EmptyResponse = try await send(
            "/api/cameras/\(cameraId.uuidString)/ptz/move",
            method: "POST",
            body: PtzMoveBody(pan: pan, tilt: tilt, zoom: zoom)
        )
    }

    func ptzStop(cameraId: UUID) async throws {
        let _: EmptyResponse = try await send(
            "/api/cameras/\(cameraId.uuidString)/ptz/stop", method: "POST", body: Optional<String>.none
        )
    }

    func ptzHome(cameraId: UUID) async throws {
        let _: EmptyResponse = try await send(
            "/api/cameras/\(cameraId.uuidString)/ptz/home", method: "POST", body: Optional<String>.none
        )
    }

    func fetchScanInterfaces() async throws -> [ScanInterface] {
        try await get("/api/cameras/discovery/interfaces")
    }

    func startScan(_ request: ScanRequest) async throws -> ScanStartResult {
        try await send("/api/cameras/discovery/scan", method: "POST", body: request)
    }

    func fetchScanStatus(scanId: UUID) async throws -> ScanJobSnapshot {
        try await get("/api/cameras/discovery/scan/\(scanId.uuidString)")
    }

    func fetchTriggerConfig(cameraId: UUID) async throws -> TriggerConfig {
        try await get("/api/cameras/\(cameraId.uuidString)/trigger-config")
    }

    func saveTriggerConfig(cameraId: UUID, _ config: TriggerConfig) async throws -> TriggerConfig {
        try await send("/api/cameras/\(cameraId.uuidString)/trigger-config", method: "PUT", body: config)
    }

    // Live ffmpeg snapshot, never cached - fetched fresh every time the zone editor opens.
    func fetchReferenceFrame(cameraId: UUID) async throws -> Data {
        guard let url = url(path: "/api/cameras/\(cameraId.uuidString)/reference-frame") else {
            throw ClientError.invalidServerAddress
        }
        return try await performData(URLRequest(url: url))
    }

    func fetchPromptProfileNames() async throws -> [PromptProfileName] {
        try await get("/api/prompt-profile-names")
    }

    func timeline(camera: String, date: String) async throws -> RecordingTimelineDay {
        try await get(
            "/api/timeline/day",
            query: [
                .init(name: "camera", value: camera),
                .init(name: "date", value: date)
            ]
        )
    }

    func timelineCameras() async throws -> [String] {
        try await get("/api/timeline/cameras")
    }

    func setReviewed(eventId: UUID, reviewed: Bool) async throws {
        let _: EmptyResponse = try await send(
            "/api/events/\(eventId.uuidString)/reviewed",
            method: "PUT",
            body: ReviewedBody(reviewed: reviewed)
        )
    }

    func accounts() async throws -> [Account] {
        try await get("/api/accounts")
    }

    func createAccount(username: String, password: String, role: String, displayName: String?) async throws -> Account {
        try await send(
            "/api/accounts",
            method: "POST",
            body: CreateAccountBody(username: username, password: password, role: role, displayName: displayName)
        )
    }

    func updateAccountRole(id: UUID, role: String) async throws {
        let _: EmptyResponse = try await send(
            "/api/accounts/\(id.uuidString)/role",
            method: "PUT",
            body: UpdateRoleBody(role: role)
        )
    }

    func resetAccountPassword(id: UUID, newPassword: String) async throws {
        let _: EmptyResponse = try await send(
            "/api/accounts/\(id.uuidString)/reset-password",
            method: "POST",
            body: ResetPasswordBody(newPassword: newPassword)
        )
    }

    func deleteAccount(id: UUID) async throws {
        let _: EmptyResponse = try await send(
            "/api/accounts/\(id.uuidString)",
            method: "DELETE",
            body: Optional<String>.none
        )
    }

    // The server deliberately leaves the current session alone (doesn't invalidate it) - no
    // separate re-login needed here, the existing cookie just keeps working.
    func changeMyPassword(currentPassword: String, newPassword: String) async throws {
        let _: EmptyResponse = try await send(
            "/api/accounts/change-password",
            method: "POST",
            body: ChangePasswordBody(currentPassword: currentPassword, newPassword: newPassword)
        )
    }

    func login(username: String, password: String) async throws {
        let _: EmptyResponse = try await send(
            "/api/auth/login",
            method: "POST",
            body: LoginBody(username: username, password: password)
        )
    }

    func logout() async throws {
        defer { SessionCookieStore.clear() }
        let _: EmptyResponse = try await send("/api/auth/logout", method: "POST", body: Optional<String>.none)
    }

    func pushStatus() async throws -> PushStatus {
        try await get("/api/push/status")
    }

    func registerPushDevice(token: String, deviceName: String) async throws {
        let _: EmptyResponse = try await send(
            "/api/push/devices",
            method: "POST",
            body: PushDeviceBody(platform: "apple", token: token, deviceName: deviceName, appVariant: "tvos")
        )
    }

    func unregisterPushDevice(token: String) async throws {
        let _: EmptyResponse = try await send(
            "/api/push/devices/unregister",
            method: "POST",
            body: PushDeviceBody(platform: "apple", token: token, deviceName: nil, appVariant: "tvos")
        )
    }

    func testPushNotification() async throws -> PushDeliveryReport {
        try await send("/api/push/test", method: "POST", body: Optional<String>.none)
    }

    // The ws:// or wss:// form of url(path:), for the MSE live socket. Same host, port and path
    // rules - and the same cookie, since URLSession carries the shared storage onto a WebSocket
    // task exactly as it does onto a data task.
    func webSocketURL(path: String) -> URL? {
        guard var components = URLComponents(string: model.activeServerURL.trimmingCharacters(in: .whitespacesAndNewlines)),
              let scheme = components.scheme else { return nil }
        components.scheme = scheme == "https" ? "wss" : "ws"
        components.path = path
        return components.url
    }

    func url(path: String, query: [URLQueryItem] = []) -> URL? {
        guard var components = URLComponents(string: model.activeServerURL.trimmingCharacters(in: .whitespacesAndNewlines)) else {
            return nil
        }
        components.path = path
        components.queryItems = query.isEmpty ? nil : query
        return components.url
    }

    private func get<T: Decodable>(_ path: String, query: [URLQueryItem] = []) async throws -> T {
        guard var components = URLComponents(url: try baseURL(), resolvingAgainstBaseURL: false) else {
            throw ClientError.invalidServerAddress
        }
        components.path = path
        components.queryItems = query.isEmpty ? nil : query
        guard let url = components.url else { throw ClientError.invalidServerAddress }
        return try await perform(URLRequest(url: url))
    }

    private func send<T: Decodable, Body: Encodable>(
        _ path: String,
        method: String,
        body: Body?
    ) async throws -> T {
        guard let url = url(path: path) else { throw ClientError.invalidServerAddress }
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        if let body { request.httpBody = try JSONEncoder().encode(body) }
        return try await perform(request)
    }

    // Shared by `perform` (JSON) and `performData` (raw bytes, e.g. the reference-frame JPEG) -
    // runs the request, logs it, and turns a non-2xx response into a ClientError with the
    // server's message extracted, without assuming the success body is JSON.
    private func executeAndValidate(_ request: URLRequest) async throws -> Data {
        let started = ContinuousClock.now
        let method = request.httpMethod ?? "GET"
        let displayURL = request.url?.absoluteString ?? "(invalid URL)"
        model.log("\(method) \(displayURL)")

        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await session.data(for: request)
        } catch {
            model.log("\(method) failed after \(elapsed(from: started)): \(describe(error))", level: .error)
            throw error
        }

        guard let http = response as? HTTPURLResponse else { throw ClientError.invalidResponse }
        model.log("\(method) \(http.statusCode) in \(elapsed(from: started))")
        // Mirror the session cookie into UserDefaults whenever the server issues one - both at
        // login and on each sliding-expiration renewal, so what's persisted never goes stale.
        if http.value(forHTTPHeaderField: "Set-Cookie") != nil, let url = request.url {
            SessionCookieStore.save(for: url)
        }
        guard (200..<300).contains(http.statusCode) else {
            let message = (try? JSONDecoder.sentinel.decode(APIError.self, from: data))
                .flatMap { $0.error ?? $0.errors?.joined(separator: "\n") }
            if http.statusCode == 401 { throw ClientError.authenticationRequired }
            throw ClientError.server(status: http.statusCode, message: message)
        }
        return data
    }

    private func performData(_ request: URLRequest) async throws -> Data {
        try await executeAndValidate(request)
    }

    private func perform<T: Decodable>(_ request: URLRequest) async throws -> T {
        if T.self == EmptyResponse.self {
            _ = try await executeAndValidate(request)
            return EmptyResponse() as! T
        }
        let data = try await executeAndValidate(request)
        do {
            return try JSONDecoder.sentinel.decode(T.self, from: data)
        } catch {
            let displayURL = request.url?.absoluteString ?? "(invalid URL)"
            model.log("Could not decode \(displayURL): \(error)", level: .error)
            throw error
        }
    }

    private func baseURL() throws -> URL {
        guard let url = URL(string: model.activeServerURL.trimmingCharacters(in: .whitespacesAndNewlines)),
              url.scheme == "http" || url.scheme == "https",
              url.host != nil else {
            throw ClientError.invalidServerAddress
        }
        return url
    }

    private func elapsed(from start: ContinuousClock.Instant) -> String {
        let duration = start.duration(to: .now)
        return duration.formatted(.units(allowed: [.seconds, .milliseconds], width: .abbreviated))
    }

    private func describe(_ error: Error) -> String {
        if let urlError = error as? URLError {
            return "URLError \(urlError.code.rawValue) (\(urlError.code)): \(urlError.localizedDescription)"
        }
        return "\(type(of: error)): \(error.localizedDescription)"
    }
}

// Keeps a signed-in session alive across app restarts on an Apple TV.
//
// URLSession's cookie jar lives in the app container (Library/Cookies), which tvOS treats as
// reclaimable - unlike iOS, the only storage tvOS guarantees to keep is UserDefaults (500KB).
// When the system reclaims it the session cookie vanishes and the app drops back to the login
// screen even though the server session is still perfectly valid. That never reproduces in the
// Simulator, which reclaims nothing, so this mirrors the cookie somewhere tvOS won't take away.
enum SessionCookieStore {
    private static let defaultsKey = "sessionCookies"

    private struct StoredCookie: Codable {
        let name: String
        let value: String
        let domain: String
        let path: String
        let expiresAt: Date?
    }

    static func restore() {
        guard let data = UserDefaults.standard.data(forKey: defaultsKey),
              let stored = try? JSONDecoder().decode([StoredCookie].self, from: data) else { return }
        for cookie in stored {
            var properties: [HTTPCookiePropertyKey: Any] = [
                .name: cookie.name,
                .value: cookie.value,
                .domain: cookie.domain,
                .path: cookie.path
            ]
            properties[.expires] = cookie.expiresAt
            if let restored = HTTPCookie(properties: properties) {
                HTTPCookieStorage.shared.setCookie(restored)
            }
        }
    }

    static func save(for url: URL) {
        guard let cookies = HTTPCookieStorage.shared.cookies(for: url), !cookies.isEmpty else { return }
        let stored = cookies.map {
            StoredCookie(name: $0.name, value: $0.value, domain: $0.domain, path: $0.path, expiresAt: $0.expiresDate)
        }
        guard let data = try? JSONEncoder().encode(stored) else { return }
        UserDefaults.standard.set(data, forKey: defaultsKey)
    }

    static func clear() {
        UserDefaults.standard.removeObject(forKey: defaultsKey)
    }
}

struct EmptyResponse: Decodable {}

enum ClientError: LocalizedError {
    case invalidServerAddress
    case invalidResponse
    case authenticationRequired
    case server(status: Int, message: String?)

    var errorDescription: String? {
        switch self {
        case .invalidServerAddress: "Enter a valid server address, such as http://192.168.1.50:3320."
        case .invalidResponse: "The server returned an invalid response."
        case .authenticationRequired: "This server requires you to sign in."
        case let .server(status, message): message ?? "Server request failed (HTTP \(status))."
        }
    }
}
