import Foundation

// Every Music & Podcasts route the TV uses (core/docs/MUSIC_AND_PODCASTS.md), over the app's
// long-timeout session - the same signed-in cookie and chosen address as everything else, and a
// timeout long enough for the speaker long poll and a music release search. Kept apart from
// SentinelClient so this feature's ~80 routes don't bury the rest of the app's networking.
@MainActor
final class AudioAPI {
    private unowned let client: SentinelClient

    init(client: SentinelClient) {
        self.client = client
    }

    // MARK: Plumbing

    struct Failure: LocalizedError {
        let status: Int
        let message: String?
        var errorDescription: String? { message ?? "Server request failed (HTTP \(status))." }
    }

    static func isNotFound(_ error: Error) -> Bool { (error as? Failure)?.status == 404 }

    static func isCancellation(_ error: Error) -> Bool {
        error is CancellationError || (error as? URLError)?.code == .cancelled
    }

    private struct APIErrorBody: Decodable {
        var error: String?
        var errors: [String]?
    }

    private func raw(_ method: String, _ path: String, query: [URLQueryItem] = [], body: Data? = nil,
                     contentType: String? = nil, timeout: TimeInterval? = 20) async throws -> Data {
        // A screen can appear (remembered tabs, launch) before the app has settled on an address;
        // give the connection a moment rather than failing with "unsupported URL".
        var waited = 0
        while client.url(path: "/") == nil || client.url(path: "/")?.host == nil, waited < 60 {
            try await Task.sleep(for: .milliseconds(250))
            waited += 1
        }
        let (status, data) = try await client.organizerRequest(
            method: method, path: path, query: query, body: body,
            contentType: body == nil ? contentType : (contentType ?? "application/json"), timeout: timeout)
        guard (200..<300).contains(status) else {
            if status == 401 { throw ClientError.authenticationRequired }
            let decoded = try? JSONDecoder.sentinel.decode(APIErrorBody.self, from: data)
            throw Failure(status: status, message: decoded?.error ?? decoded?.errors?.joined(separator: "\n"))
        }
        return data
    }

    private func get<T: Decodable>(_ path: String, _ query: [URLQueryItem] = [], timeout: TimeInterval? = 20) async throws -> T {
        let data = try await raw("GET", path, query: query, timeout: timeout)
        do {
            return try JSONDecoder.sentinel.decode(T.self, from: data)
        } catch {
            print("[SentinelCoreTV] [AUDIO] Could not decode \(path): \(error)")
            throw error
        }
    }

    private func send<T: Decodable>(_ method: String, _ path: String, _ body: (any Encodable)? = nil,
                                    query: [URLQueryItem] = [], timeout: TimeInterval? = 20) async throws -> T {
        let encoded = try body.map { try JSONEncoder().encode($0) }
        let data = try await raw(method, path, query: query, body: encoded, timeout: timeout)
        if T.self == EmptyResponse.self { return EmptyResponse() as! T }
        return try JSONDecoder.sentinel.decode(T.self, from: data)
    }

    private func fire(_ method: String, _ path: String, _ body: (any Encodable)? = nil, timeout: TimeInterval? = 20) async throws {
        let _: EmptyResponse = try await send(method, path, body, timeout: timeout)
    }

    private static func q(_ pairs: [(String, String?)]) -> [URLQueryItem] {
        pairs.compactMap { name, value in
            guard let value, !value.isEmpty else { return nil }
            return URLQueryItem(name: name, value: value)
        }
    }

    // A server path ("/api/music/albums/x/art") as a URL against the address in use now.
    func url(_ path: String?, extra: [URLQueryItem] = []) -> URL? {
        guard let base = client.mediaURL(path) else { return nil }
        guard !extra.isEmpty, var components = URLComponents(url: base, resolvingAgainstBaseURL: false) else { return base }
        var items = components.queryItems ?? []
        for item in extra where !items.contains(where: { $0.name == item.name }) { items.append(item) }
        components.queryItems = items
        return components.url
    }

    // Album/artist art at the size it is drawn.
    func art(_ path: String?, size: Int = 600) -> URL? {
        guard let path, path.hasPrefix("/api/") else { return url(path) }
        return url(path, extra: [URLQueryItem(name: "size", value: String(size))])
    }

    static let appleCodecs = "mp3,aac,alac,flac,wav,aiff"

    func streamURL(_ track: Track) -> URL? {
        url(track.streamUrl ?? "/api/music/tracks/\(track.id)/stream",
            extra: [URLQueryItem(name: "codecs", value: Self.appleCodecs)])
    }

    func audioURL(_ episode: Episode) -> URL? {
        url(episode.audioUrl ?? "/api/podcasts/episodes/\(episode.id)/audio")
    }

    // MARK: Flags (§1)

    func enabled() async throws -> AudioEnabled { try await get("/api/audio/enabled") }

    // MARK: Music libraries (§2)

    func musicStatus() async throws -> MusicStatus { try await get("/api/music/status") }
    func musicLibraries() async throws -> [MusicLibrary] { try await get("/api/music/libraries") }

    func saveMusicLibrary(id: String?, _ input: MusicLibraryInput) async throws -> MusicLibrary {
        if let id { return try await send("PUT", "/api/music/libraries/\(id)", input) }
        return try await send("POST", "/api/music/libraries", input)
    }

    func deleteMusicLibrary(id: String) async throws { try await fire("DELETE", "/api/music/libraries/\(id)") }

    func scanMusic(libraryId: String?) async throws {
        try await fire("POST", libraryId.map { "/api/music/libraries/\($0)/scan" } ?? "/api/music/scan")
    }

    func audioBrowse(path: String) async throws -> MediaBrowseListing {
        try await get("/api/audio/browse", Self.q([("path", path)]))
    }

    func audioMakeFolder(parent: String, name: String) async throws -> MediaBrowseListing {
        try await send("POST", "/api/audio/browse/folder", MediaNewFolderInput(parent: parent, name: name))
    }

    // MARK: Music (§4)

    func musicHome() async throws -> MusicHome { try await get("/api/music/home") }

    func artists(query: String? = nil, sort: String? = nil, offset: Int = 0, limit: Int = 200) async throws -> Page<Artist> {
        try await get("/api/music/artists", Self.q([("q", query), ("sort", sort), ("albumArtistsOnly", "true"),
                                                    ("offset", String(offset)), ("limit", String(limit))]))
    }

    func albums(query: String? = nil, artistId: String? = nil, genre: String? = nil, decade: Int? = nil,
                sort: String? = nil, order: String? = nil, offset: Int = 0, limit: Int = 200) async throws -> Page<Album> {
        try await get("/api/music/albums", Self.q([
            ("q", query), ("artistId", artistId), ("genre", genre), ("decade", decade.map(String.init)),
            ("sort", sort), ("order", order), ("offset", String(offset)), ("limit", String(limit)),
        ]))
    }

    func tracks(query: String? = nil, genre: String? = nil, decade: Int? = nil, loved: Bool = false,
                sort: String? = nil, order: String? = nil, offset: Int = 0, limit: Int = 200) async throws -> Page<Track> {
        try await get("/api/music/tracks", Self.q([
            ("q", query), ("genre", genre), ("decade", decade.map(String.init)), ("loved", loved ? "true" : nil),
            ("sort", sort), ("order", order), ("offset", String(offset)), ("limit", String(limit)),
        ]))
    }

    func genres() async throws -> [MusicGenre] { try await get("/api/music/genres") }
    func artist(id: String) async throws -> ArtistDetail { try await get("/api/music/artists/\(id)") }
    func album(id: String) async throws -> AlbumDetail { try await get("/api/music/albums/\(id)") }
    func search(_ query: String) async throws -> MusicSearchResult { try await get("/api/music/search", Self.q([("q", query)])) }

    func lyrics(trackId: String) async throws -> Lyrics { try await get("/api/music/tracks/\(trackId)/lyrics") }

    func reportPlayed(trackId: String, _ body: PlayedBody) async throws {
        try await fire("POST", "/api/music/tracks/\(trackId)/played", body)
    }

    func nowPlayingTrack(_ trackId: String) async throws {
        try await fire("POST", "/api/music/now-playing-track", TrackIdBody(trackId: trackId))
    }

    // kind: "tracks" | "albums" | "artists".
    func love(kind: String, id: String, loved: Bool) async throws -> Bool {
        let result: LovedResult = try await send("POST", "/api/music/\(kind)/\(id)/love", LovedBody(loved: loved))
        return result.loved
    }

    func mix(id: String) async throws -> MixResult { try await get("/api/music/mix", Self.q([("mix", id)])) }

    func radio(seedType: String, seedId: String, excludeIds: [String] = [], limit: Int = 50) async throws -> MixResult {
        try await get("/api/music/mix", Self.q([
            ("seedType", seedType), ("seedId", seedId), ("limit", String(limit)),
            ("excludeIds", excludeIds.isEmpty ? nil : excludeIds.joined(separator: ",")),
        ]))
    }

    func shuffleAll(limit: Int = 200) async throws -> [Track] {
        // Shuffle answers like a mix; accept a bare list too.
        let data = try await raw("GET", "/api/music/shuffle", query: Self.q([("limit", String(limit))]))
        if let mix = try? JSONDecoder.sentinel.decode(MixResult.self, from: data), let tracks = mix.tracks { return tracks }
        return try JSONDecoder.sentinel.decode([Track].self, from: data)
    }

    func playlists() async throws -> [Playlist] { try await get("/api/music/playlists") }
    func playlist(id: String) async throws -> PlaylistDetail { try await get("/api/music/playlists/\(id)") }

    func createPlaylist(_ input: PlaylistInput) async throws -> Playlist {
        try await send("POST", "/api/music/playlists", input)
    }

    func addToPlaylist(id: String, trackIds: [String]) async throws -> Playlist {
        try await send("POST", "/api/music/playlists/\(id)/tracks", TrackIdsBody(trackIds: trackIds, position: nil))
    }

    func pinPlaylist(id: String, pinned: Bool) async throws {
        let _: EmptyResponse = try await send("POST", "/api/music/playlists/\(id)/pin", PinBody(pinned: pinned))
    }

    func deletePlaylist(id: String) async throws { try await fire("DELETE", "/api/music/playlists/\(id)") }

    // MARK: Getting music

    func musicDownloadsStatus() async throws -> MusicDownloadsStatus { try await get("/api/music/downloads/status") }

    func musicLookup(_ query: String) async throws -> [MusicLookupResult] {
        try await get("/api/music/downloads/lookup", Self.q([("q", query)]), timeout: 45)
    }

    func startMusicSearch(_ input: MusicDownloadSearchInput) async throws -> MusicDownloadSearch {
        try await send("POST", "/api/music/downloads/search", input, timeout: 60)
    }

    func musicSearch(id: String) async throws -> MusicDownloadSearch {
        try await get("/api/music/downloads/search/\(id)")
    }

    func musicGrab(_ input: MusicGrabInput) async throws -> MediaDownload {
        try await send("POST", "/api/music/downloads", input, timeout: 90)
    }

    func musicDownloads() async throws -> MusicDownloadList { try await get("/api/music/downloads") }
    func deleteMusicDownload(id: String) async throws { try await fire("DELETE", "/api/music/downloads/\(id)") }
    func retryMusicDownload(id: String) async throws -> MediaDownload {
        try await send("POST", "/api/music/downloads/\(id)/retry")
    }

    // MARK: Podcasts (§6)

    func podcastsStatus() async throws -> PodcastsStatus { try await get("/api/podcasts/status") }
    func podcastHome() async throws -> PodcastHome { try await get("/api/podcasts/home") }
    func subscriptions() async throws -> [Podcast] { try await get("/api/podcasts") }
    func podcast(id: String) async throws -> Podcast { try await get("/api/podcasts/\(id)") }

    func episodes(podcastId: String, sort: String = "newest", filter: String = "all", season: Int? = nil,
                  offset: Int = 0, limit: Int = 100) async throws -> EpisodesPage {
        try await get("/api/podcasts/\(podcastId)/episodes", Self.q([
            ("sort", sort), ("filter", filter), ("season", season.map(String.init)),
            ("offset", String(offset)), ("limit", String(limit)),
        ]))
    }

    func episode(id: String) async throws -> Episode { try await get("/api/podcasts/episodes/\(id)") }

    func subscribe(feedUrl: String?, itunesId: Int?) async throws -> Podcast {
        try await send("POST", "/api/podcasts/subscriptions", SubscribeBody(feedUrl: feedUrl, itunesId: itunesId), timeout: 60)
    }

    func unsubscribe(podcastId: String) async throws { try await fire("DELETE", "/api/podcasts/\(podcastId)/subscription") }

    func saveMySettings(podcastId: String, _ settings: PodcastMySettings) async throws -> Podcast {
        try await send("PUT", "/api/podcasts/\(podcastId)/my-settings", settings)
    }

    func refreshPodcast(id: String) async throws -> Podcast {
        try await send("POST", "/api/podcasts/\(id)/refresh", timeout: 60)
    }

    func markAllPlayed(podcastId: String) async throws {
        try await fire("POST", "/api/podcasts/\(podcastId)/played", PlayedFlagBody(played: true))
    }

    func directorySearch(_ query: String) async throws -> [DirectoryShow] {
        try await get("/api/podcasts/directory/search", Self.q([("q", query)]), timeout: 30)
    }

    func directoryTop(genre: Int?) async throws -> [DirectoryShow] {
        try await get("/api/podcasts/directory/top", Self.q([("genre", genre.map(String.init))]), timeout: 30)
    }

    func directoryGenres() async throws -> [DirectoryGenre] { try await get("/api/podcasts/directory/genres") }
    func recommended() async throws -> [DirectoryShow] { try await get("/api/podcasts/recommended", timeout: 30) }

    func preview(feedUrl: String?, itunesId: Int?) async throws -> PodcastPreview {
        try await get("/api/podcasts/preview", Self.q([("feedUrl", feedUrl), ("itunesId", itunesId.map(String.init))]), timeout: 45)
    }

    func queue() async throws -> [Episode] { try await get("/api/podcasts/queue") }

    func enqueue(episodeId: String, next: Bool) async throws -> [Episode] {
        try await send("POST", "/api/podcasts/queue", QueueAddBody(episodeId: episodeId, position: next ? "next" : "last"))
    }

    func reorderQueue(_ episodeIds: [String]) async throws -> [Episode] {
        try await send("PUT", "/api/podcasts/queue", QueueOrderBody(episodeIds: episodeIds))
    }

    func dequeue(episodeId: String) async throws -> [Episode] {
        try await send("DELETE", "/api/podcasts/queue/\(episodeId)")
    }

    func reportProgress(episodeId: String, _ body: ProgressBody) async throws {
        try await fire("POST", "/api/podcasts/episodes/\(episodeId)/progress", body)
    }

    func setPlayed(episodeId: String, played: Bool) async throws {
        try await fire("POST", "/api/podcasts/episodes/\(episodeId)/played", PlayedFlagBody(played: played))
    }

    func downloadEpisode(id: String) async throws -> Episode {
        try await send("POST", "/api/podcasts/episodes/\(id)/download")
    }

    func chapters(episodeId: String) async throws -> [Chapter] { try await get("/api/podcasts/episodes/\(episodeId)/chapters") }
    func transcript(episodeId: String) async throws -> Transcript { try await get("/api/podcasts/episodes/\(episodeId)/transcript") }

    func bookmarks(episodeId: String? = nil) async throws -> [Bookmark] {
        try await get("/api/podcasts/bookmarks", Self.q([("episodeId", episodeId)]))
    }

    func addBookmark(episodeId: String, position: Double, note: String = "") async throws -> Bookmark {
        try await send("POST", "/api/podcasts/episodes/\(episodeId)/bookmarks", BookmarkBody(positionSeconds: position, note: note))
    }

    func deleteBookmark(id: String) async throws { try await fire("DELETE", "/api/podcasts/bookmarks/\(id)") }

    // MARK: Shared (§7-9)

    func putNowPlaying(_ body: NowPlaying) async throws { try await fire("PUT", "/api/audio/now-playing", body) }
    func nowPlayingDevices() async throws -> [NowPlaying] { try await get("/api/audio/now-playing") }
    func resolveNowPlaying(deviceId: String) async throws -> NowPlayingResolved {
        try await get("/api/audio/now-playing/\(deviceId)/resolve")
    }

    func heartbeat(deviceId: String, _ body: SpeakerHeartbeat) async throws {
        try await fire("PUT", "/api/audio/speakers/\(deviceId)", body)
    }

    func removeSpeaker(deviceId: String) async throws { try await fire("DELETE", "/api/audio/speakers/\(deviceId)") }
    func speakers() async throws -> [Speaker] { try await get("/api/audio/speakers") }

    func putSpeakerState(deviceId: String, _ state: SpeakerState) async throws {
        try await fire("PUT", "/api/audio/speakers/\(deviceId)/state", state)
    }

    func sendCommand(deviceId: String, _ command: SpeakerCommand) async throws {
        let _: SeqReply = try await send("POST", "/api/audio/speakers/\(deviceId)/commands", command)
    }

    // The long poll: answers as soon as anything newer than `after` arrives, or empty after `wait`.
    func commands(deviceId: String, after: Int, wait: Int = 25) async throws -> SpeakerCommandsReply {
        try await get("/api/audio/speakers/\(deviceId)/commands",
                      [URLQueryItem(name: "after", value: String(after)), URLQueryItem(name: "wait", value: String(wait))],
                      timeout: TimeInterval(wait + 15))
    }

    func alarms() async throws -> [WakeAlarm] { try await get("/api/audio/alarms") }

    func saveAlarm(id: String?, _ input: WakeAlarmInput) async throws -> WakeAlarm {
        if let id { return try await send("PUT", "/api/audio/alarms/\(id)", input) }
        return try await send("POST", "/api/audio/alarms", input)
    }

    func deleteAlarm(id: String) async throws { try await fire("DELETE", "/api/audio/alarms/\(id)") }
    func testAlarm(id: String) async throws { try await fire("POST", "/api/audio/alarms/\(id)/test") }

    func briefing() async throws -> Briefing { try await get("/api/audio/briefing", timeout: 30) }

    func stats(period: String, year: Int?) async throws -> AudioStats {
        try await get("/api/audio/stats", Self.q([("period", period), ("year", year.map(String.init))]), timeout: 30)
    }

    // MARK: Settings sections (schema-driven; admin)

    func settingsSchema() async throws -> [AudioSettingsSection] { try await get("/api/settings/schema") }
    func settingsSnapshot() async throws -> [String: AudioSettingsValues] { try await get("/api/settings") }

    func saveSettings(endpoint: String, values: [String: AudioSettingValue]) async throws {
        try await fire("PUT", endpoint, values)
    }
}

// Just enough of the settings schema to draw the MUSIC and PODCASTS sections on the TV - the
// full renderer lives on the iPhone/Mac (ServerSettingsView); the TV has never had one.
struct AudioSettingsField: Decodable, Hashable, Identifiable {
    var name: String
    var label: String
    var type: String
    var tip: String?
    var min: Double?
    var max: Double?
    var options: [AudioSettingsOption]?
    var secret: Bool?
    var id: String { name }
}

struct AudioSettingsOption: Decodable, Hashable {
    var value: String
    var label: String
}

struct AudioSettingsSection: Decodable, Hashable {
    var key: String
    var title: String
    var endpoint: String
    var description: String?
    var fields: [AudioSettingsField]
}

// One section's values out of the /api/settings snapshot; anything that isn't an object is empty.
struct AudioSettingsValues: Decodable {
    var values: [String: AudioSettingValue]

    init(from decoder: Decoder) throws {
        values = (try? decoder.singleValueContainer().decode([String: AudioSettingValue].self)) ?? [:]
    }
}

enum AudioSettingValue: Codable, Hashable {
    case string(String), number(Double), bool(Bool), other

    init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        if c.decodeNil() { self = .other }
        else if let v = try? c.decode(Bool.self) { self = .bool(v) }
        else if let v = try? c.decode(Double.self) { self = .number(v) }
        else if let v = try? c.decode(String.self) { self = .string(v) }
        else if (try? c.decode([String: AudioSettingValue].self)) != nil { self = .other }
        else if (try? c.decode([AudioSettingValue].self)) != nil { self = .other }
        else { self = .other }
    }

    func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        switch self {
        case .string(let v): try c.encode(v)
        case .number(let v): try c.encode(v)
        case .bool(let v): try c.encode(v)
        case .other: try c.encodeNil()
        }
    }

    var text: String {
        switch self {
        case .string(let v): v
        case .number(let v): v == v.rounded() ? String(Int64(v)) : String(v)
        case .bool(let v): v ? "true" : "false"
        case .other: ""
        }
    }

    var bool: Bool { if case .bool(let v) = self { v } else { false } }
}
