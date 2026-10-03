import Foundation

// The Music & Podcasts wire shapes - core/docs/MUSIC_AND_PODCASTS.md. Nearly every field is
// optional on purpose: the server is newer than this file in some places and older in others, and
// one missing key must never cost a whole screen its decode. Dates stay strings (ISO-8601 UTC) and
// are parsed where they are shown - see AudioDate.

// MARK: - Flags and status

struct AudioEnabled: Codable, Hashable {
    var music: Bool?
    var podcasts: Bool?
}

struct MusicLibrary: Codable, Identifiable, Hashable {
    let id: String
    var name: String
    var path: String?
    var network: Bool?
    var trackCount: Int?
    var lastScanFinishedAt: String?
    var lastScanError: String?
    var createdAt: String?
}

struct MusicLibraryInput: Encodable {
    var name: String
    var path: String
}

struct MusicScan: Codable, Hashable {
    var phase: String?
    var libraryName: String?
    var processed: Int?
    var total: Int?
    var startedAt: String?
}

struct MusicStatus: Codable {
    var enabled: Bool?
    var ffmpegAvailable: Bool?
    var scanning: Bool?
    var scan: MusicScan?
    var libraries: [MusicLibrary]?
    var trackCount: Int?
    var albumCount: Int?
    var artistCount: Int?
    var totalDurationSeconds: Double?
    var totalBytes: Int64?
    var lastScanFinishedAt: String?
    var lastScanError: String?
}

struct PodcastTranscriptsStatus: Codable, Hashable {
    var enabled: Bool?
    var available: Bool?
    var error: String?
    var queued: Int?
}

struct PodcastsStatus: Codable {
    var enabled: Bool?
    var ffmpegAvailable: Bool?
    var refreshing: Bool?
    var lastRefreshAt: String?
    var downloadFolder: String?
    var storageBytes: Int64?
    var maxStorageBytes: Int64?
    var downloadsActive: Int?
    var downloadsQueued: Int?
    var transcripts: PodcastTranscriptsStatus?
    var podcastCount: Int?
    var episodeCount: Int?
}

// MARK: - Music objects (§3)

struct ReplayGain: Codable, Hashable {
    var trackGain: Double?
    var trackPeak: Double?
    var albumGain: Double?
    var albumPeak: Double?
}

struct Track: Codable, Identifiable, Hashable {
    let id: String
    var title: String
    var artist: String?
    var artists: [String]?
    var artistId: String?
    var albumArtist: String?
    var album: String?
    var albumId: String?
    var trackNumber: Int?
    var discNumber: Int?
    var year: Int?
    var genres: [String]?
    var durationSeconds: Double?
    var codec: String?
    var bitrate: Int?
    var sampleRate: Int?
    var bitDepth: Int?
    var channels: Int?
    var sizeBytes: Int64?
    var replayGain: ReplayGain?
    var hasLyrics: Bool?
    var artUrl: String?
    var streamUrl: String?
    var addedAt: String?
    var loved: Bool?
    var rating: Int?
    var playCount: Int?
    var lastPlayedAt: String?
    // Playlist tracks only: tells duplicates apart.
    var entryId: String?

    var artistLine: String { artist ?? albumArtist ?? "Unknown Artist" }
    var isLoved: Bool { loved == true }
    var duration: Double { durationSeconds ?? 0 }
}

struct Album: Codable, Identifiable, Hashable {
    let id: String
    var title: String
    var artist: String?
    var artistId: String?
    var year: Int?
    var genres: [String]?
    var trackCount: Int?
    var discCount: Int?
    var durationSeconds: Double?
    var artUrl: String?
    var addedAt: String?
    var compilation: Bool?
    var loved: Bool?
    var rating: Int?
    var playCount: Int?
    var lastPlayedAt: String?
}

struct AlbumDetail: Decodable {
    var album: Album
    var tracks: [Track]
    var moreByArtist: [Album]
    var totalBytes: Int64?
    var formats: [String]

    private enum Keys: String, CodingKey { case tracks, moreByArtist, totalBytes, formats }

    init(from decoder: Decoder) throws {
        album = try Album(from: decoder)
        let c = try decoder.container(keyedBy: Keys.self)
        tracks = try c.decodeIfPresent([Track].self, forKey: .tracks) ?? []
        moreByArtist = try c.decodeIfPresent([Album].self, forKey: .moreByArtist) ?? []
        totalBytes = try c.decodeIfPresent(Int64.self, forKey: .totalBytes)
        formats = try c.decodeIfPresent([String].self, forKey: .formats) ?? []
    }
}

struct Artist: Codable, Identifiable, Hashable {
    let id: String
    var name: String
    var albumCount: Int?
    var trackCount: Int?
    var genres: [String]?
    var artUrl: String?
    var loved: Bool?
    var playCount: Int?
}

struct ArtistDetail: Decodable {
    var artist: Artist
    var albums: [Album]
    var appearsOn: [Album]
    var topTracks: [Track]
    var similar: [Artist]

    private enum Keys: String, CodingKey { case albums, appearsOn, topTracks, similar }

    init(from decoder: Decoder) throws {
        artist = try Artist(from: decoder)
        let c = try decoder.container(keyedBy: Keys.self)
        albums = try c.decodeIfPresent([Album].self, forKey: .albums) ?? []
        appearsOn = try c.decodeIfPresent([Album].self, forKey: .appearsOn) ?? []
        topTracks = try c.decodeIfPresent([Track].self, forKey: .topTracks) ?? []
        similar = try c.decodeIfPresent([Artist].self, forKey: .similar) ?? []
    }
}

struct Playlist: Codable, Identifiable, Hashable {
    let id: String
    var name: String
    var description: String?
    var ownerName: String?
    var mine: Bool?
    var shared: Bool?
    var collaborative: Bool?
    var smart: Bool?
    var trackCount: Int?
    var durationSeconds: Double?
    var artUrls: [String]?
    var createdAt: String?
    var updatedAt: String?
    var pinned: Bool?
}

struct PlaylistDetail: Decodable {
    var playlist: Playlist
    var tracks: [Track]

    private enum Keys: String, CodingKey { case tracks }

    init(from decoder: Decoder) throws {
        playlist = try Playlist(from: decoder)
        tracks = try decoder.container(keyedBy: Keys.self).decodeIfPresent([Track].self, forKey: .tracks) ?? []
    }
}

struct PlaylistInput: Encodable {
    var name: String
    var description: String?
    var shared: Bool?
    var collaborative: Bool?
    var trackIds: [String]?
}

struct Mix: Codable, Identifiable, Hashable {
    let id: String
    var name: String
    var description: String?
    var artUrls: [String]?
    var seedType: String?
    var seedId: String?
}

struct MixResult: Decodable {
    var name: String?
    var tracks: [Track]?
}

struct RecentlyPlayedEntry: Codable, Hashable {
    var kind: String
    var album: Album?
    var playlist: Playlist?
    var artist: Artist?
    var mix: Mix?
}

struct OnThisDay: Codable, Hashable {
    var label: String?
    var tracks: [Track]?
}

struct DecadeEntry: Codable, Hashable, Identifiable {
    var decade: Int
    var label: String?
    var trackCount: Int?
    var id: Int { decade }
    var title: String { label ?? "\(decade)s" }
}

struct MusicHome: Decodable {
    var pinned: [Playlist]?
    var recentlyPlayed: [RecentlyPlayedEntry]?
    var recentlyAdded: [Album]?
    var heavyRotation: [Album]?
    var mixes: [Mix]?
    var forgottenFavorites: [Track]?
    var onThisDay: OnThisDay?
    var discover: [Album]?
    var lovedTracks: [Track]?
    var decades: [DecadeEntry]?
    var trackCount: Int?
    var albumCount: Int?
    var artistCount: Int?

    var isEmpty: Bool { (trackCount ?? 0) == 0 && (recentlyAdded ?? []).isEmpty && (mixes ?? []).isEmpty }
}

struct Page<T: Decodable>: Decodable {
    var items: [T]
    var total: Int?
}

struct MusicGenre: Codable, Hashable, Identifiable {
    var name: String
    var trackCount: Int?
    var albumCount: Int?
    var artUrls: [String]?
    var id: String { name }
}

struct MusicSearchResult: Decodable {
    var artists: [Artist]?
    var albums: [Album]?
    var tracks: [Track]?
    var playlists: [Playlist]?
    var genres: [String]?

    var isEmpty: Bool {
        (artists ?? []).isEmpty && (albums ?? []).isEmpty && (tracks ?? []).isEmpty && (playlists ?? []).isEmpty
    }
}

struct LyricsLine: Codable, Hashable {
    var time: Double?
    var text: String
}

struct Lyrics: Decodable {
    var synced: Bool?
    var lines: [LyricsLine]?
    var plain: String?
    var source: String?

    var isSynced: Bool { synced == true && (lines ?? []).contains { $0.time != nil } }
    var displayLines: [LyricsLine] {
        if let lines, !lines.isEmpty { return lines }
        return (plain ?? "").components(separatedBy: "\n").map { LyricsLine(time: nil, text: $0) }
    }
}

struct LovedResult: Codable { var loved: Bool }
struct LovedBody: Encodable { var loved: Bool }

struct PlayedBody: Encodable {
    var secondsListened: Double
    var completed: Bool
    var skipped: Bool
    var playedAt: String?
    var deviceName: String
}

struct TrackIdBody: Encodable { var trackId: String }
struct TrackIdsBody: Encodable {
    var trackIds: [String]
    var position: Int?
}
struct PinBody: Encodable { var pinned: Bool }

// MARK: Getting music ("+ Add")

struct MusicDownloadsStatus: Decodable {
    var enabled: Bool?
    var canDownload: Bool?
    var musicLibrary: Bool?
}

struct MusicLookupResult: Decodable, Identifiable, Hashable {
    var mbid: String?
    var title: String
    var artist: String?
    var year: Int?
    var type: String?
    var trackCount: Int?
    var artUrl: String?
    var inLibrary: Bool?
    var id: String { mbid ?? "\(artist ?? "")|\(title)|\(year ?? 0)" }
}

struct MusicDownloadSearchInput: Encodable, Hashable {
    var artist: String
    var album: String
    var year: Int?
    var mbid: String?
    var artUrl: String?
}

struct MusicRelease: Decodable, Identifiable, Hashable {
    let id: String
    var title: String?
    var site: String?
    var sizeBytes: Int64?
    var seeders: Int?
    var leechers: Int?
    var publishedAt: String?
    var format: String?
    var lossless: Bool?
    var matches: Bool?
    var warning: String?
}

// A background search (202, then poll every 2 s until done or failed).
struct MusicDownloadSearch: Decodable {
    var id: String
    var phase: String?
    var message: String?
    var error: String?
    var artist: String?
    var album: String?
    var year: Int?
    var results: [MusicRelease]?
    var others: [MusicRelease]?

    var isFinished: Bool { phase == "done" || phase == "failed" }
}

struct MusicDownloadList: Decodable {
    var active: [MediaDownload]?
    var recent: [MediaDownload]?
}

struct MusicGrabInput: Encodable {
    var searchId: String
    var releaseId: String
    var libraryId: String?
}

// MARK: - Podcast objects (§5)

struct PodcastFunding: Codable, Hashable {
    var url: String?
    var title: String?
}

struct PodcastSettings: Codable, Hashable {
    var autoDownload: Bool?
    var keepEpisodes: Int?
    var voiceBoost: Bool?
    var trimSilence: Bool?
    var transcribe: Bool?
}

struct PodcastMySettings: Codable, Hashable {
    var playbackSpeed: Double?
    var skipIntroSeconds: Int?
    var skipOutroSeconds: Int?
    var notify: Bool?
    var autoQueue: Bool?
    var inBriefing: Bool?
    var sort: String?

    // Encoded in full, nulls included: the contract's "null = use the app's default" speed.
    func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(playbackSpeed, forKey: .playbackSpeed)
        try c.encode(skipIntroSeconds ?? 0, forKey: .skipIntroSeconds)
        try c.encode(skipOutroSeconds ?? 0, forKey: .skipOutroSeconds)
        try c.encode(notify ?? true, forKey: .notify)
        try c.encode(autoQueue ?? false, forKey: .autoQueue)
        try c.encode(inBriefing ?? false, forKey: .inBriefing)
        try c.encode(sort ?? "newest", forKey: .sort)
    }
}

struct Podcast: Codable, Identifiable, Hashable {
    var id: String
    var feedUrl: String?
    var title: String
    var author: String?
    var description: String?
    var artUrl: String?
    var link: String?
    var categories: [String]?
    var language: String?
    var explicit: Bool?
    var episodeCount: Int?
    var unplayedCount: Int?
    var newestEpisodeAt: String?
    var lastRefreshedAt: String?
    var refreshError: String?
    var subscribed: Bool?
    var subscriberCount: Int?
    var funding: [PodcastFunding]?
    var settings: PodcastSettings?
    var mySettings: PodcastMySettings?

    // A preview's podcast has no id fields at all.
    private enum CodingKeys: String, CodingKey {
        case id, feedUrl, title, author, description, artUrl, link, categories, language, explicit
        case episodeCount, unplayedCount, newestEpisodeAt, lastRefreshedAt, refreshError, subscribed
        case subscriberCount, funding, settings, mySettings
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decodeIfPresent(String.self, forKey: .id) ?? ""
        feedUrl = try c.decodeIfPresent(String.self, forKey: .feedUrl)
        title = try c.decodeIfPresent(String.self, forKey: .title) ?? "Untitled show"
        author = try c.decodeIfPresent(String.self, forKey: .author)
        description = try c.decodeIfPresent(String.self, forKey: .description)
        artUrl = try c.decodeIfPresent(String.self, forKey: .artUrl)
        link = try c.decodeIfPresent(String.self, forKey: .link)
        categories = try c.decodeIfPresent([String].self, forKey: .categories)
        language = try c.decodeIfPresent(String.self, forKey: .language)
        explicit = try c.decodeIfPresent(Bool.self, forKey: .explicit)
        episodeCount = try c.decodeIfPresent(Int.self, forKey: .episodeCount)
        unplayedCount = try c.decodeIfPresent(Int.self, forKey: .unplayedCount)
        newestEpisodeAt = try c.decodeIfPresent(String.self, forKey: .newestEpisodeAt)
        lastRefreshedAt = try c.decodeIfPresent(String.self, forKey: .lastRefreshedAt)
        refreshError = try c.decodeIfPresent(String.self, forKey: .refreshError)
        subscribed = try c.decodeIfPresent(Bool.self, forKey: .subscribed)
        subscriberCount = try c.decodeIfPresent(Int.self, forKey: .subscriberCount)
        funding = try c.decodeIfPresent([PodcastFunding].self, forKey: .funding)
        settings = try c.decodeIfPresent(PodcastSettings.self, forKey: .settings)
        mySettings = try c.decodeIfPresent(PodcastMySettings.self, forKey: .mySettings)
    }
}

struct EpisodeDownload: Codable, Hashable {
    var state: String?
    var progress: Double?
    var error: String?
    var enhanced: Bool?
    var timeSavedSeconds: Double?
}

struct EpisodeProgress: Codable, Hashable {
    var positionSeconds: Double?
    var durationSeconds: Double?
    var percent: Double?
    var played: Bool?
    var updatedAt: String?
}

struct Episode: Codable, Identifiable, Hashable {
    var id: String
    var podcastId: String?
    var podcastTitle: String?
    var title: String
    var summary: String?
    var publishedAt: String?
    var durationSeconds: Double?
    var season: Int?
    var episode: Int?
    var episodeType: String?
    var explicit: Bool?
    var link: String?
    var artUrl: String?
    var audioUrl: String?
    var mimeType: String?
    var sizeBytes: Int64?
    var download: EpisodeDownload?
    var archived: Bool?
    var hasChapters: Bool?
    var hasTranscript: Bool?
    var transcriptSource: String?
    var progress: EpisodeProgress?
    var inQueue: Bool?
    var bookmarkCount: Int?
    // GET /api/podcasts/episodes/{id} only.
    var description: String?
    var descriptionText: String?

    private enum CodingKeys: String, CodingKey {
        case id, podcastId, podcastTitle, title, summary, publishedAt, durationSeconds, season, episode, episodeType
        case explicit, link, artUrl, audioUrl, mimeType, sizeBytes, download, archived, hasChapters, hasTranscript
        case transcriptSource, progress, inQueue, bookmarkCount, description, descriptionText
    }

    // A preview's "Episode-lite" may carry no id; fall back to its audio URL so lists still work.
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        audioUrl = try c.decodeIfPresent(String.self, forKey: .audioUrl)
        title = try c.decodeIfPresent(String.self, forKey: .title) ?? "Untitled episode"
        id = try c.decodeIfPresent(String.self, forKey: .id) ?? audioUrl ?? title
        podcastId = try c.decodeIfPresent(String.self, forKey: .podcastId)
        podcastTitle = try c.decodeIfPresent(String.self, forKey: .podcastTitle)
        summary = try c.decodeIfPresent(String.self, forKey: .summary)
        publishedAt = try c.decodeIfPresent(String.self, forKey: .publishedAt)
        durationSeconds = try c.decodeIfPresent(Double.self, forKey: .durationSeconds)
        season = try c.decodeIfPresent(Int.self, forKey: .season)
        episode = try c.decodeIfPresent(Int.self, forKey: .episode)
        episodeType = try c.decodeIfPresent(String.self, forKey: .episodeType)
        explicit = try c.decodeIfPresent(Bool.self, forKey: .explicit)
        link = try c.decodeIfPresent(String.self, forKey: .link)
        artUrl = try c.decodeIfPresent(String.self, forKey: .artUrl)
        mimeType = try c.decodeIfPresent(String.self, forKey: .mimeType)
        sizeBytes = try c.decodeIfPresent(Int64.self, forKey: .sizeBytes)
        download = try c.decodeIfPresent(EpisodeDownload.self, forKey: .download)
        archived = try c.decodeIfPresent(Bool.self, forKey: .archived)
        hasChapters = try c.decodeIfPresent(Bool.self, forKey: .hasChapters)
        hasTranscript = try c.decodeIfPresent(Bool.self, forKey: .hasTranscript)
        transcriptSource = try c.decodeIfPresent(String.self, forKey: .transcriptSource)
        progress = try c.decodeIfPresent(EpisodeProgress.self, forKey: .progress)
        inQueue = try c.decodeIfPresent(Bool.self, forKey: .inQueue)
        bookmarkCount = try c.decodeIfPresent(Int.self, forKey: .bookmarkCount)
        description = try c.decodeIfPresent(String.self, forKey: .description)
        descriptionText = try c.decodeIfPresent(String.self, forKey: .descriptionText)
    }

    var isPlayed: Bool { progress?.played == true }
    var resumePosition: Double {
        guard let progress, progress.played != true else { return 0 }
        return progress.positionSeconds ?? 0
    }
    var progressFraction: Double? {
        guard let progress, progress.played != true, let pos = progress.positionSeconds, pos > 0 else { return nil }
        let total = progress.durationSeconds ?? durationSeconds ?? 0
        return total > 0 ? min(1, pos / total) : nil
    }
    var isDownloaded: Bool { download?.state == "done" }
    // A preview of a show nobody follows: nothing to report progress against.
    var isPreview: Bool { id.contains("/") }
}

struct EpisodesPage: Decodable {
    var items: [Episode]
    var total: Int?
    var seasons: [Int]?
}

struct PodcastPreview: Decodable {
    var podcast: Podcast
    var episodes: [Episode]?
}

struct DirectoryShow: Codable, Hashable, Identifiable {
    var itunesId: Int?
    var title: String
    var author: String?
    var artUrl: String?
    var feedUrl: String?
    var genre: String?
    var episodeCount: Int?
    var podcastId: String?
    var subscribed: Bool?
    var id: String { itunesId.map(String.init) ?? feedUrl ?? title }
}

struct DirectoryGenre: Codable, Hashable, Identifiable {
    var id: Int
    var name: String
}

struct Bookmark: Codable, Hashable, Identifiable {
    var id: String
    var episodeId: String
    var episodeTitle: String?
    var podcastId: String?
    var podcastTitle: String?
    var positionSeconds: Double
    var note: String?
    var transcriptSnippet: String?
    var createdAt: String?
    var artUrl: String?
}

struct PodcastHome: Decodable {
    var upNext: [Episode]?
    var continueListening: [Episode]?
    var newEpisodes: [Episode]?
    var shortOnes: [Episode]?
    var downloaded: [Episode]?
    var subscriptions: [Podcast]?
    var bookmarks: [Bookmark]?
    var timeSavedSeconds: Double?

    var isEmpty: Bool {
        (subscriptions ?? []).isEmpty && (upNext ?? []).isEmpty && (newEpisodes ?? []).isEmpty
    }
}

struct Chapter: Codable, Hashable, Identifiable {
    var startSeconds: Double
    var endSeconds: Double?
    var title: String
    var imageUrl: String?
    var url: String?
    var id: Double { startSeconds }
}

struct TranscriptSegment: Codable, Hashable, Identifiable {
    var start: Double
    var end: Double?
    var speaker: String?
    var text: String
    var id: Double { start }
}

struct Transcript: Decodable {
    var source: String?
    var language: String?
    var segments: [TranscriptSegment]?
}

struct SubscribeBody: Encodable {
    var feedUrl: String?
    var itunesId: Int?
}

struct QueueAddBody: Encodable {
    var episodeId: String
    var position: String
}

struct QueueOrderBody: Encodable { var episodeIds: [String] }

struct ProgressBody: Encodable {
    var positionSeconds: Double
    var durationSeconds: Double?
    var speed: Double
    var deviceName: String
}

struct PlayedFlagBody: Encodable { var played: Bool }

struct BookmarkBody: Encodable {
    var positionSeconds: Double
    var note: String
}

// MARK: - Shared: now playing, speakers, alarms, briefing, stats (§7-9)

struct QueueRef: Codable, Hashable {
    var type: String
    var id: String
}

struct PlayContext: Codable, Hashable {
    var type: String
    var id: String?
    var name: String?
}

struct NowPlaying: Codable, Hashable {
    var deviceId: String
    var deviceName: String?
    var kind: String?
    var queue: [QueueRef]?
    var index: Int?
    var positionSeconds: Double?
    var playing: Bool?
    var shuffle: Bool?
    var `repeat`: String?
    var speed: Double?
    var context: PlayContext?
    var updatedAt: String?
}

struct NowPlayingResolved: Decodable {
    var nowPlaying: NowPlaying
    var tracks: [Track]?
    var episodes: [Episode]?
}

struct SpeakerHeartbeat: Encodable {
    var name: String
    var kind: String
    var allowAnnouncements: Bool
    var volume: Double
}

struct SpeakerState: Codable, Hashable {
    var playing: Bool?
    var title: String?
    var subtitle: String?
    var artUrl: String?
    var positionSeconds: Double?
    var durationSeconds: Double?
    var volume: Double?
    var kind: String?
    var itemId: String?
}

struct Speaker: Codable, Hashable, Identifiable {
    var deviceId: String
    var name: String
    var kind: String?
    var online: Bool?
    var allowAnnouncements: Bool?
    var volume: Double?
    var lastSeenAt: String?
    var state: SpeakerState?
    var id: String { deviceId }

    var icon: String {
        switch kind {
        case "tv": "appletv.fill"
        case "kiosk": "display"
        case "phone": "iphone"
        case "tablet": "ipad"
        case "mac": "laptopcomputer"
        case "windows": "pc"
        default: "hifispeaker.fill"
        }
    }
}

struct SpeakerCommand: Codable, Hashable {
    var seq: Int?
    var at: String?
    var from: String?
    var action: String
    var queue: [QueueRef]?
    var index: Int?
    var positionSeconds: Double?
    var shuffle: Bool?
    var volume: Double?
    var rampSeconds: Double?
    var text: String?
    var context: PlayContext?
}

struct SpeakerCommandsReply: Decodable {
    var commands: [SpeakerCommand]?
    var latestSeq: Int?
}

struct SeqReply: Decodable { var seq: Int? }

struct AlarmSource: Codable, Hashable {
    var type: String
    var id: String?
    var name: String?
}

struct WakeAlarm: Codable, Hashable, Identifiable {
    var id: String
    var name: String?
    var time: String
    var days: [String]?
    var speakerId: String?
    var source: AlarmSource?
    var volume: Double?
    var rampSeconds: Double?
    var enabled: Bool?
    var nextFireAt: String?
    var lastFiredAt: String?
}

struct WakeAlarmInput: Encodable {
    var name: String
    var time: String
    var days: [String]
    var speakerId: String?
    var source: AlarmSource
    var volume: Double
    var rampSeconds: Double
    var enabled: Bool
}

struct Briefing: Decodable {
    var greeting: String?
    var speech: [String]?
    var episodes: [Episode]?
}

struct StatArtist: Decodable, Hashable { var artist: Artist; var plays: Int?; var minutes: Double? }
struct StatAlbum: Decodable, Hashable { var album: Album; var plays: Int? }
struct StatTrack: Decodable, Hashable { var track: Track; var plays: Int? }
struct StatGenre: Decodable, Hashable { var genre: String; var minutes: Double? }
struct StatFirstPlay: Decodable, Hashable { var track: Track; var playedAt: String? }
struct StatShow: Decodable, Hashable { var podcast: Podcast; var minutes: Double?; var episodes: Int? }

struct MusicStats: Decodable {
    var minutes: Double?
    var plays: Int?
    var skips: Int?
    var distinctTracks: Int?
    var distinctArtists: Int?
    var newArtists: Int?
    var topArtists: [StatArtist]?
    var topAlbums: [StatAlbum]?
    var topTracks: [StatTrack]?
    var topGenres: [StatGenre]?
    var byHour: [Double]?
    var byWeekday: [Double]?
    var longestStreakDays: Int?
    var firstPlay: StatFirstPlay?
}

struct PodcastStats: Decodable {
    var minutes: Double?
    var episodesFinished: Int?
    var timeSavedMinutes: Double?
    var topShows: [StatShow]?
    var byHour: [Double]?
    var byWeekday: [Double]?
}

struct AudioStats: Decodable {
    var period: String?
    var from: String?
    var to: String?
    var music: MusicStats?
    var podcasts: PodcastStats?
}

// MARK: - Helpers

enum AudioDate {
    static func parse(_ value: String?) -> Date? {
        guard let value, !value.isEmpty else { return nil }
        if let date = try? Date.ISO8601FormatStyle(includingFractionalSeconds: true).parse(value) { return date }
        return try? Date.ISO8601FormatStyle(includingFractionalSeconds: false).parse(value)
    }

    static func iso(_ date: Date) -> String {
        date.formatted(Date.ISO8601FormatStyle(includingFractionalSeconds: true))
    }

    static func relative(_ value: String?) -> String? {
        guard let date = parse(value) else { return nil }
        return date.formatted(.relative(presentation: .named))
    }

    static func short(_ value: String?) -> String? {
        guard let date = parse(value) else { return nil }
        return date.formatted(date: .abbreviated, time: .omitted)
    }
}

enum AudioFormat {
    // 3:07, or 1:02:07 past an hour.
    static func clock(_ seconds: Double?) -> String {
        guard let seconds, seconds.isFinite, seconds >= 0 else { return "--:--" }
        let total = Int(seconds.rounded(.down))
        let h = total / 3600, m = (total % 3600) / 60, s = total % 60
        return h > 0 ? String(format: "%d:%02d:%02d", h, m, s) : String(format: "%d:%02d", m, s)
    }

    // "42 min", "1 hr 5 min".
    static func minutes(_ seconds: Double?) -> String? {
        guard let seconds, seconds > 0 else { return nil }
        let minutes = Int((seconds / 60).rounded())
        if minutes < 60 { return "\(max(1, minutes)) min" }
        let h = minutes / 60, m = minutes % 60
        return m == 0 ? "\(h) hr" : "\(h) hr \(m) min"
    }

    static func bytes(_ value: Int64?) -> String? {
        guard let value, value > 0 else { return nil }
        return ByteCountFormatter.string(fromByteCount: value, countStyle: .file)
    }

    static func speed(_ value: Double) -> String {
        value == value.rounded() ? String(format: "%.0f×", value) : String(format: "%g×", value)
    }
}
