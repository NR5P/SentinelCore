import Foundation

// Every Music & Podcasts route (core/docs/MUSIC_AND_PODCASTS.md), on the same session, cookie and
// chosen local/remote address as the rest of SentinelClient. Over the long-timeout session: art
// lookups, feed fetches, OPML imports and the speaker long poll all outlast the main session's 15 s.
//
// Only failures are written to the diagnostics log. A playing app reports progress every 15 s and
// a speaker holds a long poll open all day; logging every one of those would bury everything else.

// Apple decodes these natively (§4 Streaming); anything else is converted to AAC by the server.
let appleAudioCodecs = "mp3,aac,alac,flac,wav,aiff"

struct AudioCodableBody<T: Encodable & Sendable>: Encodable, Sendable {
    let value: T
    func encode(to encoder: Encoder) throws { try value.encode(to: encoder) }
}

private struct LovedBody: Codable, Sendable { var loved: Bool }
private struct RatingBody: Codable, Sendable { var rating: Int }
private struct PinnedBody: Encodable, Sendable { var pinned: Bool }
private struct PlayedFlagBody: Encodable, Sendable { var played: Bool; var before: String? }
private struct TrackIdsBody: Encodable, Sendable { var trackIds: [String]; var position: Int? }
private struct EntryIdsBody: Encodable, Sendable { var entryIds: [String] }
private struct TrackIdBody: Encodable, Sendable { var trackId: String }
private struct FeedBody: Encodable, Sendable { var feedUrl: String?; var itunesId: Int? }
private struct QueueAddBody: Encodable, Sendable { var episodeId: String; var position: String }
private struct QueueSetBody: Encodable, Sendable { var episodeIds: [String] }
private struct ProgressBody: Encodable, Sendable {
    var positionSeconds: Double
    var durationSeconds: Double?
    var speed: Double
    var deviceName: String
}
private struct BookmarkBody: Encodable, Sendable { var positionSeconds: Double; var note: String }
private struct NoteBody: Encodable, Sendable { var note: String }
private struct ClipBody: Encodable, Sendable { var startSeconds: Double; var durationSeconds: Double; var title: String }
private struct TokenBody: Encodable, Sendable { var token: String }
private struct DownloadSearchBody: Encodable, Sendable { var artist: String; var album: String; var year: Int?; var mbid: String?; var artUrl: String? }
private struct GrabBody: Encodable, Sendable { var searchId: String; var releaseId: String; var libraryId: String? }
struct BookmarkNoteResult: Decodable, Sendable { var noteId: String? }
struct TranscribeResult: Decodable, Sendable { var state: String? }

extension SentinelClient {
    // MARK: - Plumbing

    func audioData(
        _ method: String, _ path: String, query: [URLQueryItem] = [], body: Data? = nil,
        contentType: String? = "application/json", timeout: TimeInterval? = nil
    ) async throws -> Data {
        guard let url = url(path: path, query: query) else { throw ClientError.invalidServerAddress }
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.httpBody = body
        if body != nil, let contentType { request.setValue(contentType, forHTTPHeaderField: "Content-Type") }
        if let timeout { request.timeoutInterval = timeout }
        do {
            let (data, response) = try await organizerSession.data(for: request)
            do {
                return try validated(data, response)
            } catch {
                if (response as? HTTPURLResponse)?.statusCode != 404 {
                    log("\(method) \(path) failed: \(error.localizedDescription)", level: .notice)
                }
                throw error
            }
        } catch {
            if (error as? URLError)?.code != .cancelled, !(error is ClientError), !(error is CancellationError) {
                log("\(method) \(path) failed: \(error.localizedDescription)", level: .notice)
            }
            throw error
        }
    }

    func audioGet<T: Decodable>(_ path: String, _ query: [URLQueryItem] = [], timeout: TimeInterval? = nil) async throws -> T {
        let data = try await audioData("GET", path, query: query, timeout: timeout)
        return try audioDecode(T.self, data, path: path)
    }

    func audioSend<T: Decodable>(_ method: String, _ path: String, _ body: (any Encodable)? = nil, query: [URLQueryItem] = []) async throws -> T {
        let encoded = try body.map { try JSONEncoder().encode($0) }
        let data = try await audioData(method, path, query: query, body: encoded ?? (method == "GET" ? nil : Data("{}".utf8)))
        return try audioDecode(T.self, data, path: path)
    }

    func audioSend(_ method: String, _ path: String, _ body: (any Encodable)? = nil) async throws {
        let encoded = try body.map { try JSONEncoder().encode($0) }
        _ = try await audioData(method, path, body: encoded ?? Data("{}".utf8))
    }

    private func audioDecode<T: Decodable>(_ type: T.Type, _ data: Data, path: String) throws -> T {
        if T.self == EmptyResponse.self { return EmptyResponse() as! T }
        do {
            return try JSONDecoder.sentinel.decode(T.self, from: data)
        } catch {
            log("Could not decode \(path): \(error)", level: .error)
            throw error
        }
    }

    // Art, stream and download paths on the wire are relative to the server ("/api/music/…").
    func audioURL(_ path: String?, query: [URLQueryItem] = []) -> URL? {
        guard let path, !path.isEmpty else { return nil }
        if path.hasPrefix("http://") || path.hasPrefix("https://") { return URL(string: path) }
        var components = URLComponents(string: path)
        let existing = components?.queryItems ?? []
        components?.queryItems = nil
        return url(path: components?.path ?? path, query: existing + query)
    }

    func audioArtURL(_ path: String?, size: Int = 600) -> URL? {
        guard let path, !path.isEmpty else { return nil }
        if path.hasPrefix("http") { return URL(string: path) }
        return audioURL(path, query: [URLQueryItem(name: "size", value: String(size))])
    }

    func trackStreamURL(_ track: MusicTrack, maxBitrate: Int? = nil) -> URL? {
        var query = [URLQueryItem(name: "codecs", value: appleAudioCodecs)]
        if let maxBitrate { query.append(URLQueryItem(name: "maxBitrate", value: String(maxBitrate))) }
        return audioURL(track.streamUrl ?? "/api/music/tracks/\(track.id)/stream", query: query)
    }

    func trackDownloadURL(_ id: String, maxBitrate: Int? = nil) -> URL? {
        var query = [URLQueryItem(name: "codecs", value: appleAudioCodecs)]
        if let maxBitrate { query.append(URLQueryItem(name: "maxBitrate", value: String(maxBitrate))) }
        return url(path: "/api/music/tracks/\(id)/download", query: query)
    }

    func episodeAudioURL(_ episode: PodcastEpisode, download: Bool = false) -> URL? {
        audioURL(episode.audioUrl ?? "/api/podcasts/episodes/\(episode.id)/audio",
                 query: download ? [URLQueryItem(name: "download", value: "1")] : [])
    }

    // MARK: - Flags and folders (§1)

    func audioEnabled() async throws -> AudioEnabled { try await audioGet("/api/audio/enabled") }

    func audioBrowse(path: String) async throws -> MediaBrowseListing {
        try await audioGet("/api/audio/browse", path.isEmpty ? [] : [URLQueryItem(name: "path", value: path)])
    }

    // MARK: - Music libraries (§2)

    func musicStatus() async throws -> MusicStatus { try await audioGet("/api/music/status") }
    func musicLibraries() async throws -> [MusicLibrary] { try await audioGet("/api/music/libraries") }

    func saveMusicLibrary(id: String?, _ input: MusicLibraryInput) async throws -> MusicLibrary {
        if let id { return try await audioSend("PUT", "/api/music/libraries/\(id)", input) }
        return try await audioSend("POST", "/api/music/libraries", input)
    }

    func deleteMusicLibrary(id: String) async throws { try await audioSend("DELETE", "/api/music/libraries/\(id)") }

    func scanMusic(libraryId: String?) async throws {
        try await audioSend("POST", libraryId.map { "/api/music/libraries/\($0)/scan" } ?? "/api/music/scan")
    }

    // MARK: - Music browse (§4)

    func musicHome() async throws -> MusicHome { try await audioGet("/api/music/home") }

    private func pageQuery(_ pairs: [(String, String?)], offset: Int, limit: Int) -> [URLQueryItem] {
        var items = pairs.compactMap { name, value in value.flatMap { $0.isEmpty ? nil : URLQueryItem(name: name, value: $0) } }
        items.append(URLQueryItem(name: "offset", value: String(offset)))
        items.append(URLQueryItem(name: "limit", value: String(limit)))
        return items
    }

    func musicArtists(q: String? = nil, genre: String? = nil, sort: String? = nil, order: String? = nil,
                      albumArtistsOnly: Bool = false, offset: Int = 0, limit: Int = 200) async throws -> AudioPage<MusicArtist> {
        try await audioGet("/api/music/artists", pageQuery([
            ("q", q), ("genre", genre), ("sort", sort), ("order", order),
            ("albumArtistsOnly", albumArtistsOnly ? "true" : nil),
        ], offset: offset, limit: limit))
    }

    func musicAlbums(q: String? = nil, artistId: String? = nil, genre: String? = nil, year: Int? = nil, decade: Int? = nil,
                     sort: String? = nil, order: String? = nil, offset: Int = 0, limit: Int = 200) async throws -> AudioPage<MusicAlbum> {
        try await audioGet("/api/music/albums", pageQuery([
            ("q", q), ("artistId", artistId), ("genre", genre), ("year", year.map(String.init)),
            ("decade", decade.map(String.init)), ("sort", sort), ("order", order),
        ], offset: offset, limit: limit))
    }

    func musicTracks(q: String? = nil, artistId: String? = nil, albumId: String? = nil, genre: String? = nil, decade: Int? = nil,
                     loved: Bool = false, minRating: Int? = nil, sort: String? = nil, order: String? = nil,
                     offset: Int = 0, limit: Int = 200) async throws -> AudioPage<MusicTrack> {
        try await audioGet("/api/music/tracks", pageQuery([
            ("q", q), ("artistId", artistId), ("albumId", albumId), ("genre", genre), ("decade", decade.map(String.init)),
            ("loved", loved ? "true" : nil), ("minRating", minRating.map(String.init)), ("sort", sort), ("order", order),
        ], offset: offset, limit: limit))
    }

    func musicGenres() async throws -> [MusicGenre] { try await audioGet("/api/music/genres") }
    func musicArtist(id: String) async throws -> MusicArtistDetail { try await audioGet("/api/music/artists/\(id)") }
    func musicAlbum(id: String) async throws -> MusicAlbumDetail { try await audioGet("/api/music/albums/\(id)") }

    func musicSearch(_ q: String) async throws -> MusicSearchResults {
        try await audioGet("/api/music/search", [URLQueryItem(name: "q", value: q)])
    }

    func musicFolders(libraryId: String?, path: String?) async throws -> MusicFolderListing {
        try await audioGet("/api/music/folders", pageQuery([("libraryId", libraryId), ("path", path)], offset: 0, limit: 1000).filter { $0.name != "offset" && $0.name != "limit" })
    }

    func musicLyrics(trackId: String) async throws -> MusicLyrics {
        try await audioGet("/api/music/tracks/\(trackId)/lyrics", timeout: 20)
    }

    // MARK: Playing, loving, rating

    func reportTrackPlayed(id: String, _ body: MusicPlayedBody) async throws {
        try await audioSend("POST", "/api/music/tracks/\(id)/played", body)
    }

    func reportNowPlayingTrack(id: String) async throws {
        try await audioSend("POST", "/api/music/now-playing-track", TrackIdBody(trackId: id))
    }

    // kind: tracks | albums | artists
    func setLoved(_ kind: String, id: String, loved: Bool) async throws -> Bool {
        let reply: LovedBody = try await audioSend("POST", "/api/music/\(kind)/\(id)/love", LovedBody(loved: loved))
        return reply.loved
    }

    // kind: tracks | albums
    func setRating(_ kind: String, id: String, rating: Int) async throws -> Int {
        let reply: RatingBody = try await audioSend("POST", "/api/music/\(kind)/\(id)/rating", RatingBody(rating: rating))
        return reply.rating
    }

    // MARK: Mixes and radio

    func musicMix(id: String) async throws -> MusicMixResult {
        try await audioGet("/api/music/mix", [URLQueryItem(name: "mix", value: id)])
    }

    func musicRadio(seedType: String, seedId: String, limit: Int = 50, excludeIds: [String] = []) async throws -> MusicMixResult {
        var query = [URLQueryItem(name: "seedType", value: seedType), URLQueryItem(name: "seedId", value: seedId),
                     URLQueryItem(name: "limit", value: String(limit))]
        if !excludeIds.isEmpty { query.append(URLQueryItem(name: "excludeIds", value: excludeIds.suffix(300).joined(separator: ","))) }
        return try await audioGet("/api/music/mix", query)
    }

    func musicShuffle(limit: Int = 200) async throws -> [MusicTrack] {
        let result: MusicShuffleResult = try await audioGet("/api/music/shuffle", [URLQueryItem(name: "limit", value: String(limit))])
        return result.tracks
    }

    // MARK: Playlists

    func musicPlaylists() async throws -> [MusicPlaylist] { try await audioGet("/api/music/playlists") }
    func musicPlaylist(id: String) async throws -> MusicPlaylistDetail { try await audioGet("/api/music/playlists/\(id)") }

    func createPlaylist(_ input: MusicPlaylistInput) async throws -> MusicPlaylist {
        try await audioSend("POST", "/api/music/playlists", input)
    }

    func updatePlaylist(id: String, _ input: MusicPlaylistInput) async throws -> MusicPlaylist {
        var body = input
        body.trackIds = nil
        return try await audioSend("PUT", "/api/music/playlists/\(id)", body)
    }

    func deletePlaylist(id: String) async throws { try await audioSend("DELETE", "/api/music/playlists/\(id)") }

    @discardableResult
    func addToPlaylist(id: String, trackIds: [String], position: Int? = nil) async throws -> MusicPlaylist {
        try await audioSend("POST", "/api/music/playlists/\(id)/tracks", TrackIdsBody(trackIds: trackIds, position: position))
    }

    func removeFromPlaylist(id: String, entryId: String) async throws {
        try await audioSend("DELETE", "/api/music/playlists/\(id)/tracks/\(entryId)")
    }

    func reorderPlaylist(id: String, entryIds: [String]) async throws {
        try await audioSend("PUT", "/api/music/playlists/\(id)/order", EntryIdsBody(entryIds: entryIds))
    }

    func pinPlaylist(id: String, pinned: Bool) async throws {
        try await audioSend("POST", "/api/music/playlists/\(id)/pin", PinnedBody(pinned: pinned))
    }

    func duplicatePlaylist(id: String) async throws -> MusicPlaylist {
        try await audioSend("POST", "/api/music/playlists/\(id)/duplicate")
    }

    // MARK: "+ Add" album downloads

    func musicDownloadsStatus() async throws -> MusicDownloadsStatus { try await audioGet("/api/music/downloads/status") }

    func musicDownloadLookup(_ q: String) async throws -> [MusicLookupResult] {
        try await audioGet("/api/music/downloads/lookup", [URLQueryItem(name: "q", value: q)], timeout: 40)
    }

    // 202 straight away; poll musicDownloadSearch(id:) every 2 s until done or failed.
    func startMusicDownloadSearch(artist: String, album: String, year: Int?, mbid: String?, artUrl: String?) async throws -> MusicReleaseSearch {
        let data = try await audioData("POST", "/api/music/downloads/search",
                                       body: try JSONEncoder().encode(DownloadSearchBody(artist: artist, album: album, year: year, mbid: mbid, artUrl: artUrl)),
                                       timeout: 60)
        return try JSONDecoder.sentinel.decode(MusicReleaseSearch.self, from: data)
    }

    func musicDownloadSearch(id: String) async throws -> MusicReleaseSearch {
        try await audioGet("/api/music/downloads/search/\(id)")
    }

    // The server fetches the torrent through the VPN before it answers, so the long timeout.
    func startMusicDownload(searchId: String, releaseId: String, libraryId: String?) async throws -> MediaDownload {
        let data = try await audioData("POST", "/api/music/downloads",
                                       body: try JSONEncoder().encode(GrabBody(searchId: searchId, releaseId: releaseId, libraryId: libraryId)),
                                       timeout: 90)
        return try JSONDecoder.sentinel.decode(MediaDownload.self, from: data)
    }

    func musicDownloads() async throws -> MusicDownloadList { try await audioGet("/api/music/downloads") }
    func deleteMusicDownload(id: String) async throws { try await audioSend("DELETE", "/api/music/downloads/\(id)") }

    func retryMusicDownload(id: String) async throws -> MediaDownload {
        try await audioSend("POST", "/api/music/downloads/\(id)/retry")
    }

    // MARK: - Podcasts (§6)

    func podcastDirectorySearch(_ q: String) async throws -> [DirectoryShow] {
        try await audioGet("/api/podcasts/directory/search", [URLQueryItem(name: "q", value: q)], timeout: 30)
    }

    func podcastTop(genre: Int?) async throws -> [DirectoryShow] {
        try await audioGet("/api/podcasts/directory/top", genre.map { [URLQueryItem(name: "genre", value: String($0))] } ?? [], timeout: 30)
    }

    func podcastGenres() async throws -> [DirectoryGenre] { try await audioGet("/api/podcasts/directory/genres") }
    func podcastRecommended() async throws -> [DirectoryShow] { try await audioGet("/api/podcasts/recommended", timeout: 30) }

    func podcastPreview(feedUrl: String?, itunesId: Int?) async throws -> PodcastPreview {
        var query: [URLQueryItem] = []
        if let feedUrl { query.append(URLQueryItem(name: "feedUrl", value: feedUrl)) }
        if let itunesId { query.append(URLQueryItem(name: "itunesId", value: String(itunesId))) }
        return try await audioGet("/api/podcasts/preview", query, timeout: 45)
    }

    func podcasts(all: Bool = false) async throws -> [Podcast] {
        try await audioGet("/api/podcasts", all ? [URLQueryItem(name: "all", value: "true")] : [])
    }

    func subscribe(feedUrl: String?, itunesId: Int?) async throws -> Podcast {
        let data = try await audioData("POST", "/api/podcasts/subscriptions",
                                       body: try JSONEncoder().encode(FeedBody(feedUrl: feedUrl, itunesId: itunesId)), timeout: 60)
        return try JSONDecoder.sentinel.decode(Podcast.self, from: data)
    }

    func unsubscribe(podcastId: String) async throws { try await audioSend("DELETE", "/api/podcasts/\(podcastId)/subscription") }
    func deletePodcast(id: String) async throws { try await audioSend("DELETE", "/api/podcasts/\(id)") }
    func podcast(id: String) async throws -> Podcast { try await audioGet("/api/podcasts/\(id)") }

    func savePodcastSettings(id: String, _ settings: PodcastSettings) async throws -> Podcast {
        try await audioSend("PUT", "/api/podcasts/\(id)/settings", settings)
    }

    func savePodcastMySettings(id: String, _ settings: PodcastMySettings) async throws -> Podcast {
        try await audioSend("PUT", "/api/podcasts/\(id)/my-settings", settings)
    }

    func refreshPodcast(id: String) async throws -> Podcast {
        let data = try await audioData("POST", "/api/podcasts/\(id)/refresh", body: Data("{}".utf8), timeout: 60)
        return try JSONDecoder.sentinel.decode(Podcast.self, from: data)
    }

    func refreshAllPodcasts() async throws { try await audioSend("POST", "/api/podcasts/refresh") }

    func podcastEpisodes(id: String, sort: String?, filter: String?, q: String?, season: Int?,
                         offset: Int = 0, limit: Int = 100) async throws -> PodcastEpisodesPage {
        try await audioGet("/api/podcasts/\(id)/episodes", pageQuery([
            ("sort", sort), ("filter", filter), ("q", q), ("season", season.map(String.init)),
        ], offset: offset, limit: limit))
    }

    func markPodcastPlayed(id: String, played: Bool, before: String? = nil) async throws {
        try await audioSend("POST", "/api/podcasts/\(id)/played", PlayedFlagBody(played: played, before: before))
    }

    func exportOPMLURL() -> URL? { url(path: "/api/podcasts/opml") }

    func importOPML(_ xml: Data) async throws -> OPMLImportResult {
        let data = try await audioData("POST", "/api/podcasts/opml", body: xml, contentType: "text/xml", timeout: 60)
        return try JSONDecoder.sentinel.decode(OPMLImportResult.self, from: data)
    }

    func exportOPML() async throws -> Data { try await audioData("GET", "/api/podcasts/opml") }

    func podcastHome() async throws -> PodcastHome { try await audioGet("/api/podcasts/home") }

    // MARK: Queue

    func podcastQueue() async throws -> [PodcastEpisode] { try await audioGet("/api/podcasts/queue") }

    func addToPodcastQueue(episodeId: String, position: String) async throws -> [PodcastEpisode] {
        try await audioSend("POST", "/api/podcasts/queue", QueueAddBody(episodeId: episodeId, position: position))
    }

    func setPodcastQueue(_ ids: [String]) async throws -> [PodcastEpisode] {
        try await audioSend("PUT", "/api/podcasts/queue", QueueSetBody(episodeIds: ids))
    }

    func removeFromPodcastQueue(episodeId: String) async throws -> [PodcastEpisode] {
        try await audioSend("DELETE", "/api/podcasts/queue/\(episodeId)")
    }

    // MARK: Listening

    func episode(id: String) async throws -> PodcastEpisodeDetail { try await audioGet("/api/podcasts/episodes/\(id)") }

    func reportEpisodeProgress(id: String, position: Double, duration: Double?, speed: Double, deviceName: String) async throws {
        try await audioSend("POST", "/api/podcasts/episodes/\(id)/progress",
                            ProgressBody(positionSeconds: position, durationSeconds: duration, speed: speed, deviceName: deviceName))
    }

    func setEpisodePlayed(id: String, played: Bool) async throws {
        try await audioSend("POST", "/api/podcasts/episodes/\(id)/played", PlayedFlagBody(played: played, before: nil))
    }

    func startEpisodeDownload(id: String) async throws -> PodcastEpisode {
        try await audioSend("POST", "/api/podcasts/episodes/\(id)/download")
    }

    func deleteEpisodeDownload(id: String) async throws -> PodcastEpisode {
        try await audioSend("DELETE", "/api/podcasts/episodes/\(id)/download")
    }

    func episodeChapters(id: String) async throws -> [PodcastChapter] { try await audioGet("/api/podcasts/episodes/\(id)/chapters") }

    func podcastFill(minutes: Int) async throws -> PodcastFill {
        try await audioGet("/api/podcasts/fill", [URLQueryItem(name: "minutes", value: String(minutes))])
    }

    // MARK: Transcripts

    func episodeTranscript(id: String) async throws -> PodcastTranscript {
        try await audioGet("/api/podcasts/episodes/\(id)/transcript", timeout: 30)
    }

    func transcribeEpisode(id: String) async throws -> TranscribeResult {
        try await audioSend("POST", "/api/podcasts/episodes/\(id)/transcribe")
    }

    func searchTranscripts(_ q: String) async throws -> [TranscriptHit] {
        try await audioGet("/api/podcasts/transcripts/search", [URLQueryItem(name: "q", value: q)])
    }

    // MARK: Bookmarks and clips

    func bookmarks(episodeId: String? = nil) async throws -> [PodcastBookmark] {
        try await audioGet("/api/podcasts/bookmarks", episodeId.map { [URLQueryItem(name: "episodeId", value: $0)] } ?? [])
    }

    func addBookmark(episodeId: String, position: Double, note: String) async throws -> PodcastBookmark {
        try await audioSend("POST", "/api/podcasts/episodes/\(episodeId)/bookmarks", BookmarkBody(positionSeconds: position, note: note))
    }

    func updateBookmark(id: String, note: String) async throws -> PodcastBookmark {
        try await audioSend("PUT", "/api/podcasts/bookmarks/\(id)", NoteBody(note: note))
    }

    func deleteBookmark(id: String) async throws { try await audioSend("DELETE", "/api/podcasts/bookmarks/\(id)") }

    func bookmarkToNote(id: String) async throws -> BookmarkNoteResult {
        try await audioSend("POST", "/api/podcasts/bookmarks/\(id)/to-note")
    }

    func makeClip(episodeId: String, start: Double, duration: Double, title: String) async throws -> PodcastClip {
        let data = try await audioData("POST", "/api/podcasts/episodes/\(episodeId)/clips",
                                       body: try JSONEncoder().encode(ClipBody(startSeconds: start, durationSeconds: duration, title: title)),
                                       timeout: 120)
        return try JSONDecoder.sentinel.decode(PodcastClip.self, from: data)
    }

    func clips() async throws -> [PodcastClip] { try await audioGet("/api/podcasts/clips") }
    func deleteClip(id: String) async throws { try await audioSend("DELETE", "/api/podcasts/clips/\(id)") }

    func podcastStatus() async throws -> PodcastStatus { try await audioGet("/api/podcasts/status") }

    // MARK: - Play where you left off (§7)

    func putNowPlaying(_ report: NowPlayingReport) async throws { try await audioSend("PUT", "/api/audio/now-playing", report) }
    func nowPlayingDevices() async throws -> [NowPlayingDevice] { try await audioGet("/api/audio/now-playing") }
    func deleteNowPlaying(deviceId: String) async throws { try await audioSend("DELETE", "/api/audio/now-playing/\(deviceId)") }

    func resolveNowPlaying(deviceId: String) async throws -> NowPlayingResolved {
        try await audioGet("/api/audio/now-playing/\(deviceId)/resolve", timeout: 30)
    }

    // MARK: - Speakers (§8)

    func speakerHeartbeat(deviceId: String, _ body: SpeakerHeartbeat) async throws {
        try await audioSend("PUT", "/api/audio/speakers/\(deviceId)", body)
    }

    func removeSpeaker(deviceId: String) async throws { try await audioSend("DELETE", "/api/audio/speakers/\(deviceId)") }
    func speakers() async throws -> [AudioSpeaker] { try await audioGet("/api/audio/speakers") }

    func reportSpeakerState(deviceId: String, _ state: SpeakerState) async throws {
        try await audioSend("PUT", "/api/audio/speakers/\(deviceId)/state", state)
    }

    @discardableResult
    func sendSpeakerCommand(deviceId: String, _ command: SpeakerCommand) async throws -> SpeakerCommandAck {
        try await audioSend("POST", "/api/audio/speakers/\(deviceId)/commands", command)
    }

    func waitForSpeakerCommands(deviceId: String, after: Int, wait: Int = 25) async throws -> SpeakerCommands {
        let data = try await audioData("GET", "/api/audio/speakers/\(deviceId)/commands",
                                       query: [URLQueryItem(name: "after", value: String(after)), URLQueryItem(name: "wait", value: String(wait))],
                                       timeout: TimeInterval(wait + 15))
        return try JSONDecoder.sentinel.decode(SpeakerCommands.self, from: data)
    }

    // MARK: - Alarms, briefing, stats, ListenBrainz (§9)

    func wakeAlarms() async throws -> [WakeAlarm] { try await audioGet("/api/audio/alarms") }

    func saveWakeAlarm(_ alarm: WakeAlarm) async throws -> WakeAlarm {
        if let id = alarm.id { return try await audioSend("PUT", "/api/audio/alarms/\(id)", alarm) }
        return try await audioSend("POST", "/api/audio/alarms", alarm)
    }

    func deleteWakeAlarm(id: String) async throws { try await audioSend("DELETE", "/api/audio/alarms/\(id)") }
    func testWakeAlarm(id: String) async throws { try await audioSend("POST", "/api/audio/alarms/\(id)/test") }

    func morningBriefing() async throws -> MorningBriefing { try await audioGet("/api/audio/briefing", timeout: 30) }

    func audioStats(period: String, year: Int?) async throws -> AudioStats {
        var query = [URLQueryItem(name: "period", value: period)]
        if let year { query.append(URLQueryItem(name: "year", value: String(year))) }
        return try await audioGet("/api/audio/stats", query, timeout: 30)
    }

    func listenBrainz() async throws -> ListenBrainzStatus { try await audioGet("/api/audio/me/listenbrainz") }

    func setListenBrainz(token: String) async throws -> ListenBrainzStatus {
        try await audioSend("PUT", "/api/audio/me/listenbrainz", TokenBody(token: token))
    }

    // MARK: - Network computers for the picker (§1)

    func audioNetworkComputers() async throws -> [MediaNetworkComputer] { try await audioGet("/api/audio/network") }

    func saveAudioNetworkComputer(id: String?, _ input: MediaNetworkComputerInput) async throws -> MediaNetworkSignIn {
        if let id { return try await audioSend("PUT", "/api/audio/network/\(id)", input) }
        return try await audioSend("POST", "/api/audio/network", input)
    }

    func deleteAudioNetworkComputer(id: String) async throws { try await audioSend("DELETE", "/api/audio/network/\(id)") }

    func discoverAudioComputers() async throws -> [MediaDiscoveredComputer] {
        try await audioGet("/api/audio/network/discover", timeout: 20)
    }
}

// "Shuffle all" comes back as a plain track list or wrapped like a mix; both are accepted.
struct MusicShuffleResult: Decodable, Sendable {
    var tracks: [MusicTrack]

    init(from decoder: Decoder) throws {
        if let list = try? [MusicTrack](from: decoder) {
            tracks = list
        } else if let mix = try? MusicMixResult(from: decoder) {
            tracks = mix.tracks
        } else {
            tracks = try AudioPage<MusicTrack>(from: decoder).items
        }
    }
}
