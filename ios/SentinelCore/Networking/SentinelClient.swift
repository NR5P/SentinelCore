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

    // Unauthenticated on the server side, so this is readable at the login screen too - which is
    // where knowing the server is too old is arguably most useful.
    func serverHealth() async throws -> ServerHealth {
        try await get("/api/health")
    }

    func event(id: UUID) async throws -> SecurityEvent {
        try await get("/api/events/\(id.uuidString)")
    }

    // The merged Events list, and the only list this app fetches now - it replaced separate calls
    // to /api/events and /api/triggers, which returned two rows for one moment whenever a camera
    // forwarded to AI analysis. Those endpoints still exist server-side for older installed
    // builds of this app; nothing here calls them.
    //
    // includeUnconfirmed is the "show hidden" toggle - motion the AI rejected as nothing, plus
    // anything still waiting on a verdict. Off by default, matching the web dashboard.
    func activity(search: String? = nil, notifiedOnly: Bool = false, includeUnconfirmed: Bool = false) async throws -> [ActivityItem] {
        // This list is the day-level audit trail now that it carries motion as well as analyses,
        // so a busy camera's whole day has to fit.
        var items = [URLQueryItem(name: "limit", value: "500")]
        if let search, !search.isEmpty { items.append(.init(name: "search", value: search)) }
        if notifiedOnly { items.append(.init(name: "notifiedOnly", value: "true")) }
        if includeUnconfirmed { items.append(.init(name: "includeUnconfirmed", value: "true")) }
        return try await get("/api/activity", query: items)
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

    // The one way to see a camera's real password. Deliberately per-camera and on demand: the
    // camera list carries only the mask, so nothing holds a live device credential in memory
    // until someone actually asks to look at it. Admin-only on the server.
    func cameraPassword(id: UUID) async throws -> String {
        let response: CameraPasswordResponse = try await get("/api/cameras/\(id.uuidString)/password")
        return response.password
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
    // "Record this now" from the live view. The server turns it into the same kind of trigger a
    // motion zone produces - same event, same Feed card - attaches a clip covering the seconds
    // either side of the press, and starts recording until stopRecording is called or the server's
    // own limit ends it.
    @discardableResult
    func recordNow(cameraId: UUID) async throws -> ManualRecordingState {
        let response: ManualRecordingResponse = try await send(
            "/api/cameras/\(cameraId.uuidString)/record", method: "POST", body: Optional<String>.none
        )
        return response.state
    }

    func stopRecording(cameraId: UUID) async throws -> ManualRecordingStopped {
        try await send(
            "/api/cameras/\(cameraId.uuidString)/record", method: "DELETE", body: Optional<String>.none
        )
    }

    // Asked when a camera opens, so a recording started on another device - or by this one before
    // it was force-quit - shows as running rather than as a button that has forgotten about it.
    func recordingState(cameraId: UUID) async throws -> ManualRecordingState {
        try await get("/api/cameras/\(cameraId.uuidString)/record")
    }

    // Which live transport a tile actually landed on, and why. Logged by the server, not stored -
    // the question it answers ("was the phone on MSE or HLS at 9pm, and what pushed it down") is
    // asked of the log alongside the go2rtc and ffmpeg lines that explain it.
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

    // Saves wherever the camera is pointing right now as its home position. Separate from
    // ptzHome for the same reason the server splits POST from PUT: one reads the stored position,
    // the other replaces it.
    func ptzSaveHome(cameraId: UUID) async throws {
        let _: EmptyResponse = try await send(
            "/api/cameras/\(cameraId.uuidString)/ptz/home", method: "PUT", body: Optional<String>.none
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

    // The raw contents of a .lic file, sent as text/plain the way the install endpoint expects -
    // it takes the file's bytes, not a path, because the server has no way to reach whatever
    // machine the file was emailed to. Verified server-side against the embedded public key before
    // anything is written, so a truncated pick can't clobber a working license.
    func installLicense(contents: String) async throws -> LicenseState {
        guard let url = url(path: "/api/license/install") else { throw ClientError.invalidServerAddress }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("text/plain", forHTTPHeaderField: "Content-Type")
        request.httpBody = Data(contents.utf8)
        return try await perform(request)
    }

    // Prompt profiles arrive and leave as raw JSON rather than a Swift struct, and that is load
    // bearing rather than laziness. The save endpoint replaces the whole PromptProfiles
    // dictionary, so anything this app failed to model would be erased by the act of saving - a
    // profile's crop region is a rectangle no phone form should be editing, but it must survive
    // being edited here. Keeping the original object and writing back only the handful of fields
    // this screen actually shows means the rest round-trips untouched.
    func promptProfiles() async throws -> [String: SettingsValue] {
        let snapshot: [String: SettingsValue] = try await get("/api/settings")
        guard case .object(let profiles)? = snapshot["promptProfiles"] else { return [:] }
        return profiles
    }

    func savePromptProfiles(_ profiles: [String: SettingsValue]) async throws {
        let _: EmptyResponse = try await send(
            "/api/settings/prompt-profiles", method: "PUT", body: profiles)
    }

    // The backup is deliberately untyped here: it is whatever the server considers its whole
    // configuration, and a client that decoded it into a fixed shape would quietly drop any
    // section added after this app was built - which is the one thing a backup must never do. It
    // travels through this app as raw bytes and goes back exactly as it came.
    func exportBackup() async throws -> Data {
        guard let url = url(path: "/api/backup/export") else { throw ClientError.invalidServerAddress }
        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        return try await performData(request)
    }

    func importBackup(_ contents: Data) async throws -> BackupRestoreResult {
        guard let url = url(path: "/api/backup/import") else { throw ClientError.invalidServerAddress }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = contents
        return try await perform(request)
    }

    func homeKitStatus() async throws -> HomeKitStatus {
        try await get("/api/homekit/status")
    }

    func homeKitSettings() async throws -> HomeKitSettings {
        let snapshot: [String: SettingsValue] = try await get("/api/settings")
        guard case .object(let fields)? = snapshot["homeKit"] else {
            return HomeKitSettings(
                enabled: false, sidecarBaseUrl: "http://127.0.0.1:3321",
                timeoutSeconds: 10, minimumConfidence: 0.6)
        }
        return HomeKitSettings(
            enabled: fields["enabled"]?.boolValue ?? false,
            sidecarBaseUrl: fields["sidecarBaseUrl"]?.displayString ?? "http://127.0.0.1:3321",
            timeoutSeconds: Int(Double(fields["timeoutSeconds"]?.displayString ?? "") ?? 10),
            minimumConfidence: Double(fields["minimumConfidence"]?.displayString ?? "") ?? 0.6)
    }

    func saveHomeKitSettings(_ settings: HomeKitSettings) async throws {
        let _: EmptyResponse = try await send("/api/settings/homekit", method: "PUT", body: settings)
    }

    // The literal "RESET" is what the endpoint requires - an explicit confirmation string rather
    // than a bare POST, since this drops the pairing and every Home app on the network has to
    // pair again.
    func resetHomeKitPairing() async throws {
        let _: EmptyResponse = try await send(
            "/api/homekit/reset", method: "POST", body: ["confirm": "RESET"])
    }

    func knownVehicles() async throws -> [KnownVehicle] {
        let snapshot: [String: SettingsValue] = try await get("/api/settings")
        guard case .array(let values)? = snapshot["knownVehicles"] else { return [] }
        return values.compactMap { value in
            guard case .object(let fields) = value else { return nil }
            return KnownVehicle(
                plate: fields["plate"]?.displayString ?? "",
                label: fields["label"]?.displayString ?? "")
        }
    }

    // The whole list every time, which is what the endpoint takes - it replaces the section rather
    // than patching it, so a partial send would delete the rest.
    func saveKnownVehicles(_ vehicles: [KnownVehicle]) async throws {
        let _: EmptyResponse = try await send(
            "/api/settings/known-vehicles",
            method: "PUT",
            body: vehicles)
    }

    // Turning the dashboard's login requirement on creates the first admin account in the same
    // step - there is deliberately no way to require a login with nobody able to satisfy it.
    func enableDashboardLogin(username: String, password: String) async throws {
        let _: EmptyResponse = try await send(
            "/api/auth/enable",
            method: "POST",
            body: ["username": username, "password": password])
    }

    func disableDashboardLogin() async throws {
        let _: EmptyResponse = try await send(
            "/api/auth/disable",
            method: "POST",
            body: Optional<String>.none)
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

    // MARK: - Schedules

    // Schedules live in the main app and are proxied by the web host, exactly like automation
    // rules - see ScheduleProxyEndpoints.cs. Reads need only a signed-in account; everything that
    // changes or fires one is AdminOnly server-side.
    func schedules() async throws -> [Schedule] {
        try await get("/api/schedules")
    }

    func scheduleMetadata() async throws -> ScheduleMetadata {
        try await get("/api/schedules/metadata")
    }

    func scheduleRuns(id: UUID, limit: Int = 25) async throws -> [ScheduleRun] {
        try await get("/api/schedules/\(id.uuidString)/runs?limit=\(limit)")
    }

    // Upcoming firing times for a schedule that hasn't been saved yet - the only practical way
    // to check that a darkness level or a cron expression says what was meant.
    func previewSchedule(_ input: ScheduleInput) async throws -> SchedulePreview {
        try await send("/api/schedules/preview", method: "POST", body: input)
    }

    func createSchedule(_ input: ScheduleInput) async throws -> Schedule {
        try await send("/api/schedules", method: "POST", body: input)
    }

    func updateSchedule(id: UUID, _ input: ScheduleInput) async throws -> Schedule {
        try await send("/api/schedules/\(id.uuidString)", method: "PUT", body: input)
    }

    func deleteSchedule(id: UUID) async throws {
        let _: EmptyResponse = try await send(
            "/api/schedules/\(id.uuidString)",
            method: "DELETE",
            body: Optional<String>.none
        )
    }

    func setScheduleEnabled(id: UUID, enabled: Bool) async throws -> Schedule {
        try await send(
            "/api/schedules/\(id.uuidString)/enabled",
            method: "POST",
            body: ScheduleEnabledInput(enabled: enabled)
        )
    }

    // Fires the action now, ignoring the timing. This really opens the gate or switches the
    // light, which is why the server puts it behind AdminOnly.
    func runSchedule(id: UUID) async throws -> ScheduleRunResult {
        try await send(
            "/api/schedules/\(id.uuidString)/run",
            method: "POST",
            body: Optional<String>.none
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

    // The whole arrangement in one PUT, like saveLiveOrder for camera tiles. The response is the
    // reordered device list, which callers can ignore - the local move already happened.
    func saveDeviceOrder(_ deviceIds: [UUID]) async throws {
        let _: [HubDevice] = try await send(
            "/api/devices/order",
            method: "PUT",
            body: DeviceOrderInput(deviceIds: deviceIds)
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

    // The values for a section that isn't in the bulk snapshot, fetched from the dataEndpoint the
    // schema gives it. Only INTERNET EXPOSED MODE needs this - it configures the dashboard
    // project's own Kestrel binding and so lives in a different settings file - but nothing here
    // knows that, which is the point: the section says where its values are and this fetches them.
    func settingsSectionData(endpoint: String) async throws -> [String: SettingsValue] {
        try await get(endpoint)
    }

    func saveSettingsSection(endpoint: String, values: [String: SettingsValue]) async throws -> SettingsSaveResult {
        try await send(endpoint, method: "PUT", body: values)
    }

    // Runs a settings field's lookup (SettingsLookupSchema) - the endpoint and the meaning of the
    // results both come from the schema, so this stays one method however many fields grow one.
    func settingsLookup(_ lookup: SettingsLookupSchema, query: String) async throws -> [SettingsLookupCandidate] {
        let response: SettingsLookupResponse = try await get(
            lookup.endpoint,
            query: [URLQueryItem(name: "query", value: query)]
        )
        return response.results
    }

    // Fetches the values a settings field can hold (SettingsOptionsSource). Same shape of method
    // as settingsLookup above and for the same reason: the endpoint comes from the schema, so one
    // method serves however many fields grow a list.
    func settingsOptions(_ source: SettingsOptionsSource) async throws -> [SettingsFieldOption] {
        let response: SettingsOptionsResponse = try await get(source.endpoint)
        return response.options
    }

    // Returns whatever the action had to say for itself, when it says anything. The test sends
    // report which address the mail actually went to; the setup reset has nothing to report and
    // answers with a body of a different shape entirely. So the detail is decoded leniently - an
    // action that succeeded but couldn't be read is still an action that succeeded, and a failure
    // has already been thrown by then with the server's own message.
    func runSettingsAction(_ action: SettingsActionSchema) async throws -> String? {
        let data = try await sendForData(action.endpoint, method: action.method)
        return (try? JSONDecoder.sentinel.decode(SettingsActionResult.self, from: data))?.detail
    }

    // service is "bridge" or "dashboard". The server answers 202 and restarts only when running
    // under a supervisor (systemd etc.); otherwise it refuses with an explanation, which
    // surfaces through ClientError.server like any other error message.
    func restartServer(service: String) async throws {
        let _: EmptyResponse = try await send("/api/system/restart", method: "POST", body: ["service": service])
    }

    // Rebooting the machine itself, as opposed to the two service restarts above. The probe is a
    // plain GET with no side effects - it is safe to call every time this screen appears, and is
    // the only trustworthy answer to "will the reboot button work", since the permission it
    // depends on lives in the operating system and can disappear without this app knowing.
    func hostRebootProbe() async throws -> HostRebootProbe {
        try await get("/api/system/host-reboot")
    }

    func rebootHost() async throws -> HostRebootOutcome {
        try await send("/api/system/host-reboot", method: "POST", body: [String: String]())
    }

    func cancelHostReboot() async throws -> HostRebootOutcome {
        try await send("/api/system/host-reboot/cancel", method: "POST", body: [String: String]())
    }

    func deviceScanStatus(scanId: UUID) async throws -> DeviceScanSnapshot {
        try await get("/api/devices/discover/\(scanId.uuidString)")
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

    // Like send, but hands back the raw body rather than decoding it - for a caller that has to
    // cope with an answer of any shape, including none. See runSettingsAction.
    private func sendForData(_ path: String, method: String) async throws -> Data {
        guard let url = url(path: path) else { throw ClientError.invalidServerAddress }
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        return try await performData(request)
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
                .flatMap { $0.error ?? $0.errors?.joined(separator: "\n") ?? $0.detail }
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

// Whether a camera is recording by hand right now, and when the server will stop it if nobody
// does. stopsAtUtc is the press-again deadline, not a guess: the server writes it when the
// recording starts and honours it with ffmpeg's own -t.
struct ManualRecordingState: Decodable, Equatable {
    let isRecording: Bool
    let recordingId: UUID?
    let startedUtc: Date?
    let stopsAtUtc: Date?
    let maxMinutes: Int

    static let idle = ManualRecordingState(
        isRecording: false, recordingId: nil, startedUtc: nil, stopsAtUtc: nil, maxMinutes: 0
    )
}

struct ManualRecordingResponse: Decodable {
    let success: Bool
    let state: ManualRecordingState
}

struct ManualRecordingStopped: Decodable {
    let success: Bool
    let state: ManualRecordingState

    // How the finished recording was filed - "complete", "partial", "failed" - and how many seconds
    // of video it holds. Both are nil when the stop found nothing running, which is not an error:
    // two devices racing to stop the same recording is an ordinary thing to do.
    let status: String?
    let detail: String
    let seconds: Int?
}

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
