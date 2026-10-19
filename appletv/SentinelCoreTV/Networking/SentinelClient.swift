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
        let retired = [session, organizerSession]
        session = Self.makeSession()
        organizerSession = Self.makeOrganizerSession()
        // Cancel what's in flight now, but invalidate later. URLSession.data(for:) leaves the
        // main actor before it creates its task, so a poll that picked up the old session just
        // before this ran creates its task a moment afterwards - and creating a task in an
        // invalidated session raises an Objective-C exception, which kills the app instead of
        // throwing (seen: the weather-warnings poll during an offline reconnect).
        for old in retired {
            old.getAllTasks { tasks in tasks.forEach { $0.cancel() } }
        }
        Task {
            try? await Task.sleep(for: .seconds(30))
            for old in retired { old.invalidateAndCancel() }
        }
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

    // MARK: - Weather (see WeatherTVView)

    // Always 200 with a `state` on a server that has weather; a 404 means an older server.
    func weather() async throws -> WeatherReport {
        try await get("/api/weather")
    }

    // The NWS alerts the server's weather-warning settings ask for, worst first - the app-wide
    // flashing banner (WeatherWarningsModel). A 404 means a server from before the banner.
    func weatherWarnings() async throws -> WeatherWarningsResponse {
        try await get("/api/weather/warnings")
    }

    // A GIF from radar.weather.gov, proxied by the server. `kind` is "loop" or "still". The
    // fallback when the server is too old for the radar map.
    func weatherRadar(scope: String, kind: String) async throws -> Data {
        let query = [URLQueryItem(name: "scope", value: scope), URLQueryItem(name: "kind", value: kind)]
        guard let url = url(path: "/api/weather/radar", query: query) else { throw ClientError.invalidServerAddress }
        return try await performData(URLRequest(url: url))
    }

    // Precipitation totals, hour-by-hour feels-like and gusts, the barometer's trend, the
    // forecasters' discussion and the SPC severe outlook.
    func weatherDetails() async throws -> WeatherDetails {
        try await get("/api/weather/details")
    }

    // The radar map's description: home, every radar site, the layers and their frame times.
    // `station` picks whose per-radar layers to list (nil: the one nearest home).
    func weatherMap(station: String?) async throws -> WeatherMapInfo {
        try await get("/api/weather/map", query: station.map { [URLQueryItem(name: "station", value: $0)] } ?? [])
    }

    // GOES-19 lightning flashes inside a box over the loop window. `epoch` and `after` from the
    // previous answer ask for only what arrived since. A 404 means a server from before lightning.
    func weatherLightning(west: Double, south: Double, east: Double, north: Double,
                          epoch: String?, after: Int?) async throws -> WeatherLightningAnswer {
        let format = { (value: Double) in String(format: "%.3f", value) }
        var query = [
            URLQueryItem(name: "west", value: format(west)),
            URLQueryItem(name: "south", value: format(south)),
            URLQueryItem(name: "east", value: format(east)),
            URLQueryItem(name: "north", value: format(north)),
        ]
        if let epoch, let after {
            query.append(URLQueryItem(name: "epoch", value: epoch))
            query.append(URLQueryItem(name: "after", value: String(after)))
        }
        return try await get("/api/weather/lightning", query: query)
    }

    // Storm cells, warning outlines and today's SPC outlook inside a box of the map.
    func weatherStorms(west: Double, south: Double, east: Double, north: Double) async throws -> WeatherStorms {
        let format = { (value: Double) in String(format: "%.3f", value) }
        return try await get("/api/weather/storms", query: [
            URLQueryItem(name: "west", value: format(west)),
            URLQueryItem(name: "south", value: format(south)),
            URLQueryItem(name: "east", value: format(east)),
            URLQueryItem(name: "north", value: format(north)),
        ])
    }

    // A radar layer's colour key, a 500x30 PNG strip.
    func weatherLegend(layer: String, station: String?) async throws -> Data {
        guard let url = url(path: "/api/weather/map/legend/\(layer)",
                            query: station.map { [URLQueryItem(name: "station", value: $0)] } ?? []) else {
            throw ClientError.invalidServerAddress
        }
        return try await performData(URLRequest(url: url))
    }

    // MARK: - Family Files (read-only on the TV - see FilesTVView)

    // Always answers on a server that has the feature, even when it is off in Settings; a 404
    // means an older server. AppModel reads either as "no Files tab".
    func familyFilesStatus() async throws -> FamilyFilesStatus {
        try await get("/api/family-files/status")
    }

    func familyFolder(path: String) async throws -> FamilyFolderListing {
        try await get("/api/family-files/list", query: [URLQueryItem(name: "path", value: path)])
    }

    // A JPEG up to 400px, image files only - 404 for anything else.
    func familyThumbnail(path: String) async throws -> Data {
        guard let url = url(path: "/api/family-files/thumbnail/\(path)") else { throw ClientError.invalidServerAddress }
        return try await performData(URLRequest(url: url))
    }

    // The file itself, for showing a picture full screen. A photo can be tens of megabytes, so
    // this goes through the long-timeout session rather than the 15-second one.
    func familyFileData(path: String) async throws -> Data {
        guard let url = url(path: "/api/family-files/files/\(path)") else { throw ClientError.invalidServerAddress }
        var request = URLRequest(url: url)
        request.timeoutInterval = 120
        model.log("GET \(url.absoluteString) (file)")
        let (data, response) = try await organizerSession.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw ClientError.invalidResponse }
        guard (200..<300).contains(http.statusCode) else {
            let message = (try? JSONDecoder.sentinel.decode(APIError.self, from: data))
                .flatMap { $0.error ?? $0.errors?.joined(separator: "\n") }
            if http.statusCode == 401 { throw ClientError.authenticationRequired }
            throw ClientError.server(status: http.statusCode, message: message)
        }
        return data
    }

    func sendDeviceCommand(id: UUID, action: String? = nil, brightness: Int? = nil) async throws -> DeviceCommandResult {
        try await send(
            "/api/devices/\(id.uuidString)/command",
            method: "POST",
            body: DeviceCommandInput(action: action, brightness: brightness)
        )
    }

    // MARK: - Movies & TV (see core/docs/MOVIES.md)

    // Whether the Movies tab should exist at all. Its own endpoint, readable by a Viewer, and it
    // answers even while the section is switched off; a 404 is an older server and means off too.
    func mediaEnabled() async throws -> MediaEnabled {
        try await get("/api/media/enabled")
    }

    func mediaStatus() async throws -> MediaStatus {
        try await get("/api/media/status")
    }

    // MARK: Library management (admin)

    func mediaLibraries() async throws -> [MediaLibrary] {
        try await get("/api/media/libraries")
    }

    func saveMediaLibrary(id: String?, _ input: MediaLibraryInput) async throws -> MediaLibrary {
        if let id {
            return try await send("/api/media/libraries/\(id)", method: "PUT", body: input)
        }
        return try await send("/api/media/libraries", method: "POST", body: input)
    }

    func deleteMediaLibrary(id: String) async throws {
        let _: EmptyResponse = try await send("/api/media/libraries/\(id)", method: "DELETE", body: Optional<String>.none)
    }

    // nil scans every library. A 409 means a scan is already running.
    func scanMediaLibrary(id: String?) async throws {
        let path = id.map { "/api/media/libraries/\($0)/scan" } ?? "/api/media/scan"
        let _: EmptyResponse = try await send(path, method: "POST", body: Optional<String>.none)
    }

    // "" is the picker's first page; a local path or smb://host/Share/… anything below it.
    func mediaBrowse(path: String) async throws -> MediaBrowseListing {
        try await get("/api/media/browse", query: path.isEmpty ? [] : [URLQueryItem(name: "path", value: path)])
    }

    func mediaMakeFolder(parent: String, name: String) async throws -> MediaBrowseListing {
        try await send("/api/media/browse/folder", method: "POST", body: MediaNewFolderInput(parent: parent, name: name))
    }

    func mediaNetworkComputers() async throws -> [MediaNetworkComputer] {
        try await get("/api/media/network")
    }

    // Signs in (the server tests it before saving) and answers with the computer's shares.
    func saveMediaNetworkComputer(id: String?, _ input: MediaNetworkComputerInput) async throws -> MediaNetworkSignIn {
        if let id {
            return try await send("/api/media/network/\(id)", method: "PUT", body: input)
        }
        return try await send("/api/media/network", method: "POST", body: input)
    }

    func deleteMediaNetworkComputer(id: String) async throws {
        let _: EmptyResponse = try await send("/api/media/network/\(id)", method: "DELETE", body: Optional<String>.none)
    }

    // Takes about three seconds: the server listens for file-sharing computers over mDNS.
    func discoverMediaComputers() async throws -> [MediaDiscoveredComputer] {
        try await get("/api/media/network/discover")
    }

    func mediaHome() async throws -> MediaHome {
        try await get("/api/media/home")
    }

    // kind nil returns movies and shows together (never episodes). Sort and order in the server's
    // vocabulary; it picks a sensible default order for each sort when none is given. inList keeps
    // only the signed-in account's My List.
    func mediaItems(
        kind: String?, query: String? = nil, genre: String? = nil, sort: MediaSort? = nil, order: String? = nil,
        unwatched: Bool = false, inList: Bool = false, offset: Int = 0, limit: Int = 60
    ) async throws -> MediaItemsPage {
        var items: [URLQueryItem] = []
        if let kind { items.append(.init(name: "kind", value: kind)) }
        if let query, !query.isEmpty { items.append(.init(name: "q", value: query)) }
        if let genre, !genre.isEmpty { items.append(.init(name: "genre", value: genre)) }
        if let sort { items.append(.init(name: "sort", value: sort.rawValue)) }
        if let order { items.append(.init(name: "order", value: order)) }
        if unwatched { items.append(.init(name: "unwatched", value: "true")) }
        if inList { items.append(.init(name: "list", value: "true")) }
        items.append(.init(name: "offset", value: String(offset)))
        items.append(.init(name: "limit", value: String(limit)))
        return try await get("/api/media/items", query: items)
    }

    func mediaItem(id: String) async throws -> MediaItemDetail {
        try await get("/api/media/items/\(id)")
    }

    // "Surprise me": one movie or show at random, unwatched ones first. 404 with the server's own
    // "nothing matches" message when the library is empty.
    func mediaRandom() async throws -> MediaItemSummary {
        try await get("/api/media/random")
    }

    // start nil resumes from the saved progress, 0 starts over. The codec lists are what every
    // Apple device decodes in hardware; the server remuxes or transcodes anything else.
    //
    // Over the long-timeout session: for anything that isn't direct play the server starts ffmpeg
    // and only answers once the first segments exist, which can take over a minute for a
    // transcode - the main session gives up after 15 seconds.
    func mediaPlayback(id: String, start: Double?) async throws -> MediaPlayback {
        var items: [URLQueryItem] = [
            .init(name: "video", value: "h264,hevc"),
            .init(name: "audio", value: "aac,ac3,eac3,mp3"),
        ]
        if let start { items.append(.init(name: "start", value: String(Int(start.rounded(.down))))) }
        guard let url = url(path: "/api/media/items/\(id)/playback", query: items) else {
            throw ClientError.invalidServerAddress
        }
        var request = URLRequest(url: url)
        request.timeoutInterval = 100
        model.log("GET \(url.absoluteString)")
        let (data, response) = try await organizerSession.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw ClientError.invalidResponse }
        model.log("GET \(http.statusCode) (media playback)")
        guard (200..<300).contains(http.statusCode) else {
            if http.statusCode == 401 { throw ClientError.authenticationRequired }
            let message = (try? JSONDecoder.sentinel.decode(APIError.self, from: data))
                .flatMap { $0.error ?? $0.errors?.joined(separator: "\n") }
            throw ClientError.server(status: http.statusCode, message: message)
        }
        return try JSONDecoder.sentinel.decode(MediaPlayback.self, from: data)
    }

    // Fire-and-forget on purpose: the reply is the new progress object, but nothing here needs
    // it, and an older or oddly-configured server answering with an empty body must not read as
    // a failure ten seconds into every film.
    func reportMediaProgress(id: String, position: Double, duration: Double?) async throws {
        let _: EmptyResponse = try await send(
            "/api/media/items/\(id)/progress",
            method: "POST",
            body: MediaProgressBody(positionSeconds: position, durationSeconds: duration)
        )
    }

    func setMediaWatched(id: String, watched: Bool) async throws -> MediaProgress {
        try await send("/api/media/items/\(id)/watched", method: "POST", body: MediaWatchedBody(watched: watched))
    }

    // Adds to (or with false removes from) the signed-in account's My List.
    func setMediaInList(id: String, inList: Bool) async throws -> MediaListResult {
        try await send("/api/media/items/\(id)/list", method: "POST", body: MediaListBody(inList: inList))
    }

    func setMediaSeasonWatched(showId: String, season: Int, watched: Bool) async throws {
        let _: EmptyResponse = try await send(
            "/api/media/items/\(showId)/seasons/\(season)/watched",
            method: "POST",
            body: MediaWatchedBody(watched: watched)
        )
    }

    // The wire gives artwork and stream paths relative to the server ("/api/media/items/x/poster").
    // Always resolved against the address in use *now*, so a poster fetched after the plain-HTTP
    // fallback kicked in goes to the address that works. An absolute URL passes through untouched.
    func mediaURL(_ path: String?) -> URL? {
        guard let path, !path.isEmpty else { return nil }
        if path.hasPrefix("http://") || path.hasPrefix("https://") { return URL(string: path) }
        guard let question = path.firstIndex(of: "?") else { return url(path: path) }
        let query = URLComponents(string: "?" + path[path.index(after: question)...])?.queryItems ?? []
        return url(path: String(path[..<question]), query: query)
    }

    func mediaPosterURL(id: String) -> URL? { url(path: "/api/media/items/\(id)/poster") }
    func mediaBackdropURL(id: String) -> URL? { url(path: "/api/media/items/\(id)/backdrop") }
    func mediaThumbURL(id: String) -> URL? { url(path: "/api/media/items/\(id)/thumb") }
    func mediaSeasonPosterURL(showId: String, season: Int) -> URL? {
        url(path: "/api/media/items/\(showId)/seasons/\(season)/poster")
    }

    // MARK: - Movie downloads (see core/docs/MOVIE_DOWNLOADS.md)
    // The iPhone app's calls, less the site admin - the TV leaves managing sites to the others.

    // Answers while the feature is off, so the Movies tab can decide whether to show Downloads.
    // An older server answers 404, which the caller reads as off.
    func mediaDownloadsStatus() async throws -> MediaDownloadsStatus {
        try await get("/api/media/downloads/status")
    }

    // kind is "movie" or "show".
    func mediaDownloadLookup(query: String, kind: String) async throws -> MediaDownloadLookup {
        try await get("/api/media/downloads/lookup", query: [
            URLQueryItem(name: "q", value: query),
            URLQueryItem(name: "kind", value: kind),
        ])
    }

    // One request to TMDB per season, so the long-timeout session.
    func mediaDownloadShow(tmdbId: Int) async throws -> MediaDownloadShow {
        try await mediaDownloadsRequest("/api/media/downloads/show/\(tmdbId)", method: "GET")
    }

    // Answers 202 straight away; the search itself runs on the server. Poll mediaDownloadSearch.
    func startMediaDownloadSearch(_ input: MediaDownloadSearchInput) async throws -> MediaDownloadSearch {
        try await send("/api/media/downloads/search", method: "POST", body: input)
    }

    func mediaDownloadSearch(id: String) async throws -> MediaDownloadSearch {
        try await get("/api/media/downloads/search/\(id)")
    }

    func mediaDownloads() async throws -> MediaDownloadList {
        try await get("/api/media/downloads")
    }

    // The server fetches the torrent through the VPN before it answers, so the long timeout.
    // Throws MediaAlreadyInLibrary when the movie is already there and `replace` is nil.
    func startMediaDownload(searchId: String, releaseId: String, libraryId: String? = nil, replace: Bool? = nil) async throws -> MediaDownload {
        try await mediaDownloadsRequest(
            "/api/media/downloads", method: "POST",
            body: MediaDownloadGrabInput(searchId: searchId, releaseId: releaseId, libraryId: libraryId, replace: replace))
    }

    // Deletes a movie's files from the server's disk. Cannot be undone.
    func deleteMediaItem(id: String) async throws {
        let _: EmptyResponse = try await send("/api/media/items/\(id)", method: "DELETE", body: Optional<String>.none)
    }

    // Cancels an active download (its files are deleted) or removes a finished one from the list.
    func deleteMediaDownload(id: String) async throws {
        let _: EmptyResponse = try await send("/api/media/downloads/\(id)", method: "DELETE", body: Optional<String>.none)
    }

    func retryMediaDownload(id: String) async throws -> MediaDownload {
        try await send("/api/media/downloads/\(id)/retry", method: "POST", body: Optional<String>.none)
    }

    // Connects the VPN if it is idle and proves the sites see its address, not the home one. Can
    // take up to about a minute, so the long-timeout session.
    func testMediaDownloadsVpn() async throws -> MediaVpnTestResult {
        try await mediaDownloadsRequest("/api/media/downloads/vpn/test", method: "POST", timeout: 90)
    }

    // MARK: Imported lists (MOVIE_DOWNLOADS.md "Import a list")
    // The same routes under /api/music/downloads and /api/media/downloads - `kind` picks which.
    // Lists are imported from the iPhone, Mac or dashboard; the TV follows them and can pause,
    // resume and search the missing ones again.

    func mediaImports(kind: MediaImportKind) async throws -> [MediaImportJob] {
        let list: MediaImportJobList = try await get("\(kind.base)/imports")
        return list.imports ?? []
    }

    func pauseMediaImport(kind: MediaImportKind, id: String, paused: Bool) async throws -> MediaImportJob {
        try await send("\(kind.base)/imports/\(id)/pause", method: "POST", body: MediaImportPauseInput(paused: paused))
    }

    // Everything not found or failed is searched for again.
    func retryMediaImport(kind: MediaImportKind, id: String) async throws -> MediaImportJob {
        try await send("\(kind.base)/imports/\(id)/retry", method: "POST", body: Optional<String>.none)
    }

    // Over the long-timeout session. A 503 with `starting: true` (the VPN still coming up) is
    // thrown as MediaDownloadsStartingError so the screen can say so.
    private func mediaDownloadsRequest<T: Decodable>(
        _ path: String, method: String, body: (any Encodable)? = nil, timeout: TimeInterval = 60
    ) async throws -> T {
        guard let url = url(path: path) else { throw ClientError.invalidServerAddress }
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.timeoutInterval = timeout
        if let body {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try JSONEncoder().encode(body)
        }
        let started = ContinuousClock.now
        model.log("\(method) \(url.absoluteString)")
        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await organizerSession.data(for: request)
        } catch {
            model.log("\(method) failed after \(elapsed(from: started)): \(describe(error))", level: .error)
            throw error
        }
        guard let http = response as? HTTPURLResponse else { throw ClientError.invalidResponse }
        model.log("\(method) \(http.statusCode) in \(elapsed(from: started))")
        guard (200..<300).contains(http.statusCode) else {
            if http.statusCode == 503,
               let busy = try? JSONDecoder.sentinel.decode(MediaDownloadsBusy.self, from: data),
               busy.starting == true {
                throw MediaDownloadsStartingError(message: busy.error ?? "Connecting to the VPN... try again in a moment.")
            }
            if http.statusCode == 409,
               let duplicate = try? JSONDecoder.sentinel.decode(MediaAlreadyInLibrary.self, from: data),
               duplicate.code == "alreadyInLibrary" {
                throw duplicate
            }
            if http.statusCode == 401 { throw ClientError.authenticationRequired }
            let message = (try? JSONDecoder.sentinel.decode(APIError.self, from: data))
                .flatMap { $0.error ?? $0.errors?.joined(separator: "\n") }
            throw ClientError.server(status: http.statusCode, message: message)
        }
        do {
            return try JSONDecoder.sentinel.decode(T.self, from: data)
        } catch {
            model.log("Could not decode \(url.absoluteString): \(error)", level: .error)
            throw error
        }
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

// The VPN behind movie downloads is still coming up (503 { starting: true }).
struct MediaDownloadsStartingError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
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
