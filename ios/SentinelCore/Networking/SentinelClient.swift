import Foundation

@MainActor
final class SentinelClient {
    private unowned let model: AppModel
    private var session: URLSession

    init(model: AppModel) {
        self.model = model
        session = Self.makeSession()
    }

    func resetSession() {
        session.invalidateAndCancel()
        session = Self.makeSession()
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

    func events(search: String? = nil, notifiedOnly: Bool = false) async throws -> [SecurityEvent] {
        var items = [URLQueryItem(name: "limit", value: "100")]
        if let search, !search.isEmpty { items.append(.init(name: "search", value: search)) }
        if notifiedOnly { items.append(.init(name: "notifiedOnly", value: "true")) }
        return try await get("/api/events", query: items)
    }

    func event(id: UUID) async throws -> SecurityEvent {
        try await get("/api/events/\(id.uuidString)")
    }

    func triggers() async throws -> [TriggerEvent] {
        try await get("/api/triggers", query: [.init(name: "limit", value: "100")])
    }

    func cameras() async throws -> [Camera] {
        try await get("/api/cameras")
    }

    func createCamera(_ input: CameraInput) async throws -> Camera {
        try await send("/api/cameras", method: "POST", body: input)
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

    // How this account has arranged the Live grid. The server answers with a complete order
    // covering every camera that currently exists - deleted ones dropped, ones it has never seen
    // appended last - so there is nothing to merge here.
    func liveOrder() async throws -> [UUID] {
        let response: LiveOrder = try await get("/api/live-order")
        return response.cameraIds
    }

    func saveLiveOrder(_ cameraIds: [UUID]) async throws {
        let _: LiveOrder = try await send("/api/live-order", method: "PUT", body: LiveOrder(cameraIds: cameraIds))
    }

    func knownFaces() async throws -> [KnownFace] {
        try await get("/api/known-faces")
    }

    func createKnownFace(label: String, imageData: Data, fileName: String = "photo.jpg", mimeType: String = "image/jpeg") async throws -> KnownFace {
        try await sendMultipart("/api/known-faces", fields: ["label": label], fileFieldName: "image", fileData: imageData, fileName: fileName, mimeType: mimeType)
    }

    func addKnownFaceEnrollment(faceId: String, imageData: Data, fileName: String = "photo.jpg", mimeType: String = "image/jpeg") async throws -> KnownFace {
        try await sendMultipart("/api/known-faces/\(faceId)/enrollments", fields: [:], fileFieldName: "image", fileData: imageData, fileName: fileName, mimeType: mimeType)
    }

    func deleteKnownFaceEnrollment(faceId: String, enrollmentId: String) async throws {
        let _: EmptyResponse = try await send(
            "/api/known-faces/\(faceId)/enrollments/\(enrollmentId)", method: "DELETE", body: Optional<String>.none
        )
    }

    func deleteKnownFace(id: String) async throws {
        let _: EmptyResponse = try await send("/api/known-faces/\(id)", method: "DELETE", body: Optional<String>.none)
    }

    func probeCamera(_ request: CameraProbeRequest) async throws -> CameraProbeResult {
        try await send("/api/cameras/discovery/probe", method: "POST", body: request)
    }

    // Continuous-move semantics: the camera keeps moving at this velocity until ptzStop is
    // called - there's no "move by this much and stop automatically" in this mode, so the
    // joystick UI is expected to call ptzStop on release, not just ptzMove once.
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
        let _: EmptyResponse = try await send("/api/auth/logout", method: "POST", body: Optional<String>.none)
    }

    func pushStatus() async throws -> PushStatus {
        try await get("/api/push/status")
    }

    func systemStatus() async throws -> SystemStatus {
        try await get("/api/system/status")
    }

    func registerPushDevice(token: String, deviceName: String) async throws {
        let _: EmptyResponse = try await send(
            "/api/push/devices",
            method: "POST",
            body: PushDeviceBody(platform: "apple", token: token, deviceName: deviceName)
        )
    }

    func unregisterPushDevice(token: String) async throws {
        let _: EmptyResponse = try await send(
            "/api/push/devices/unregister",
            method: "POST",
            body: PushDeviceBody(platform: "apple", token: token, deviceName: nil)
        )
    }

    func testPushNotification() async throws -> PushDeliveryReport {
        try await send("/api/push/test", method: "POST", body: Optional<String>.none)
    }

    // Readable by any signed-in account, not just an Admin, precisely so this app can ask - see
    // the note on the endpoint in LicenseEndpoints.cs.
    func licenseState() async throws -> LicenseState {
        try await get("/api/license/status")
    }

    // MARK: - Device Hub

    // Whether the DEVICE HUB section should exist at all. Deliberately its own endpoint rather
    // than read from /api/settings, which is Admin-only - a Viewer gets the tab too.
    func deviceHubEnabled() async throws -> DeviceHubEnabled {
        try await get("/api/device-hub/enabled")
    }

    func devices() async throws -> [HubDevice] {
        try await get("/api/devices")
    }

    func deviceMetadata() async throws -> DeviceHubMetadata {
        try await get("/api/devices/metadata")
    }

    // The light poll while the Device Hub is on screen - no topics or payload strings, just what
    // each device is currently doing.
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

    func createDevice(_ input: HubDeviceInput) async throws -> HubDevice {
        try await send("/api/devices", method: "POST", body: input)
    }

    func updateDevice(id: UUID, _ input: HubDeviceInput) async throws -> HubDevice {
        try await send("/api/devices/\(id.uuidString)", method: "PUT", body: input)
    }

    func deleteDevice(id: UUID) async throws {
        let _: EmptyResponse = try await send(
            "/api/devices/\(id.uuidString)",
            method: "DELETE",
            body: Optional<String>.none
        )
    }

    func startDeviceScan() async throws -> DeviceScanStartResult {
        try await send("/api/devices/discover", method: "POST", body: Optional<String>.none)
    }

    // MARK: Server settings (schema-driven, Admin-only server-side)

    func settingsSchema() async throws -> [SettingsSectionSchema] {
        try await get("/api/settings/schema")
    }

    // The full settings snapshot, untyped on purpose: its shape is described by the schema
    // above, and hard-coding it here is exactly what the schema exists to avoid. Secrets arrive
    // masked (••••••••) and are sent back unchanged to mean "keep the stored value".
    func settingsSnapshot() async throws -> [String: SettingsValue] {
        try await get("/api/settings")
    }

    func saveSettingsSection(endpoint: String, values: [String: SettingsValue]) async throws -> SettingsSaveResult {
        try await send(endpoint, method: "PUT", body: values)
    }

    func runSettingsAction(_ action: SettingsActionSchema) async throws {
        let _: EmptyResponse = try await send(action.endpoint, method: action.method, body: Optional<String>.none)
    }

    // service is "bridge" or "dashboard". The server answers 202 and restarts only when running
    // under a supervisor (systemd etc.); otherwise it refuses with an explanation, which
    // surfaces through ClientError.server like any other error message.
    func restartServer(service: String) async throws {
        let _: EmptyResponse = try await send("/api/system/restart", method: "POST", body: ["service": service])
    }

    func deviceScanStatus(scanId: UUID) async throws -> DeviceScanSnapshot {
        try await get("/api/devices/discover/\(scanId.uuidString)")
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

    // Only used for Known Faces photo enrollment - every other endpoint in this client sends
    // plain JSON. Builds a standard multipart/form-data body by hand since there's no other
    // binary-upload need in this app to justify a dependency for it.
    private func sendMultipart<T: Decodable>(
        _ path: String,
        fields: [String: String],
        fileFieldName: String,
        fileData: Data,
        fileName: String,
        mimeType: String
    ) async throws -> T {
        guard let url = url(path: path) else { throw ClientError.invalidServerAddress }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"

        let boundary = "Boundary-\(UUID().uuidString)"
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")

        var body = Data()
        for (name, value) in fields {
            body.append("--\(boundary)\r\n".data(using: .utf8)!)
            body.append("Content-Disposition: form-data; name=\"\(name)\"\r\n\r\n".data(using: .utf8)!)
            body.append("\(value)\r\n".data(using: .utf8)!)
        }
        body.append("--\(boundary)\r\n".data(using: .utf8)!)
        body.append("Content-Disposition: form-data; name=\"\(fileFieldName)\"; filename=\"\(fileName)\"\r\n".data(using: .utf8)!)
        body.append("Content-Type: \(mimeType)\r\n\r\n".data(using: .utf8)!)
        body.append(fileData)
        body.append("\r\n--\(boundary)--\r\n".data(using: .utf8)!)

        request.httpBody = body
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

    // Used to decide between the local and remote server addresses before a real connect -
    // any HTTP response counts as "reachable" (even a 401 or 500), since the point is just to
    // detect whether the host is reachable at all, not to validate the response itself. A
    // per-request timeout override (rather than a whole separate URLSessionConfiguration) keeps
    // this from disturbing the session's normal 10s/15s timeouts used for everything else.
    func probeReachable(_ urlString: String, timeout: TimeInterval = 2.5) async -> Bool {
        guard let url = URL(string: "\(urlString.trimmingCharacters(in: .whitespacesAndNewlines))/api/auth/status") else {
            return false
        }
        var request = URLRequest(url: url)
        request.timeoutInterval = timeout
        do {
            _ = try await session.data(for: request)
            return true
        } catch {
            // A rejected certificate still proves a server answered - it takes a completed-enough
            // TLS handshake to have a certificate to reject. Treating it as unreachable here would
            // wrongly send connect() to the remote address when the local server is fine.
            return AppModel.isCertificateTrustFailure(error)
        }
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
