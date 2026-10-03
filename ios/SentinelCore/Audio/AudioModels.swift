import Foundation

// The wire shapes of Music & Podcasts - core/docs/MUSIC_AND_PODCASTS.md. Nearly everything is
// optional on purpose: the server and the six apps are built against the same document at the
// same time, and one field arriving as null (or not at all) must cost that field, never the whole
// screen. Dates stay strings for the same reason and are read with AudioDate.parse, which takes
// every ISO-8601 shape ASP.NET produces, with or without a zone or fractional seconds.

// MARK: - Shared

struct AudioEnabled: Decodable, Sendable {
    var music: Bool?
    var podcasts: Bool?
}

struct AudioPage<T: Decodable & Sendable>: Decodable, Sendable {
    var items: [T]
    var total: Int?
}

enum AudioDate {
    static func parse(_ value: String?) -> Date? {
        guard let value, !value.isEmpty else { return nil }
        if let date = try? Date.ISO8601FormatStyle(includingFractionalSeconds: true).parse(value) { return date }
        if let date = try? Date.ISO8601FormatStyle(includingFractionalSeconds: false).parse(value) { return date }
        // No zone (a DateTime with Kind Unspecified): the contract says UTC.
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(identifier: "UTC")
        for format in ["yyyy-MM-dd'T'HH:mm:ss.SSSSSSS", "yyyy-MM-dd'T'HH:mm:ss.SSS", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd"] {
            formatter.dateFormat = format
            if let date = formatter.date(from: value) { return date }
        }
        return nil
    }

    static func iso(_ date: Date) -> String {
        date.formatted(.iso8601)
    }
}

// A value in a smart-playlist rule: text, a number or a yes/no, depending on the field.
enum AudioJSONValue: Codable, Hashable, Sendable {
    case string(String)
    case number(Double)
    case bool(Bool)

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if let bool = try? container.decode(Bool.self) { self = .bool(bool) }
        else if let number = try? container.decode(Double.self) { self = .number(number) }
        else { self = .string((try? container.decode(String.self)) ?? "") }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .string(let value): try container.encode(value)
        case .number(let value):
            if value.rounded() == value, abs(value) < 1e15 { try container.encode(Int(value)) } else { try container.encode(value) }
        case .bool(let value): try container.encode(value)
        }
    }

    var text: String {
        switch self {
        case .string(let value): value
        case .number(let value): value.rounded() == value ? String(Int(value)) : String(value)
        case .bool(let value): value ? "true" : "false"
        }
    }
}

// MARK: - Music objects (§3)

struct ReplayGain: Codable, Hashable, Sendable {
    var trackGain: Double?
    var trackPeak: Double?
    var albumGain: Double?
    var albumPeak: Double?
}

struct MusicTrack: Codable, Identifiable, Hashable, Sendable {
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
    var composer: String?
    var bpm: Double?
    var replayGain: ReplayGain?
    var hasLyrics: Bool?
    var artUrl: String?
    var streamUrl: String?
    var addedAt: String?
    var loved: Bool?
    var rating: Int?
    var playCount: Int?
    var lastPlayedAt: String?
    var path: String?
    // Only on a playlist's tracks: tells two copies of one track apart.
    var entryId: String?

    var isLoved: Bool { loved == true }
    var rowKey: String { entryId ?? id }
    var artistLine: String { artist ?? albumArtist ?? "Unknown Artist" }
    var duration: Double { durationSeconds ?? 0 }

    // "FLAC 16/44.1" - what Get Info and the Now Playing badge show.
    var formatLine: String? {
        guard let codec else { return nil }
        var text = codec.uppercased()
        if let bitDepth, let sampleRate, bitDepth > 0 {
            let khz = Double(sampleRate) / 1000
            text += " \(bitDepth)/\(khz.rounded() == khz ? String(Int(khz)) : String(format: "%.1f", khz))"
        } else if let bitrate, bitrate > 0 {
            text += " \(bitrate / 1000) kbps"
        }
        return text
    }
    var isLossless: Bool { ["flac", "alac", "wav", "aiff", "ape", "wv", "dsf", "dff"].contains(codec?.lowercased() ?? "") }
}

struct MusicAlbum: Codable, Identifiable, Hashable, Sendable {
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

    var subtitle: String {
        [artist, year.map(String.init)].compactMap { $0 }.joined(separator: " · ")
    }
}

struct MusicArtist: Codable, Identifiable, Hashable, Sendable {
    let id: String
    var name: String
    var albumCount: Int?
    var trackCount: Int?
    var genres: [String]?
    var artUrl: String?
    var loved: Bool?
    var playCount: Int?
}

struct SmartRule: Codable, Hashable, Sendable, Identifiable {
    var field: String
    var op: String
    var value: AudioJSONValue
    // Local only, so a list of rules can be edited row by row.
    var id = UUID()

    enum CodingKeys: String, CodingKey { case field, op, value }

    init(field: String, op: String, value: AudioJSONValue) {
        self.field = field
        self.op = op
        self.value = value
    }
}

struct SmartRules: Codable, Hashable, Sendable {
    var match: String = "all"
    var rules: [SmartRule] = []
    var sort: String? = "random"
    var order: String? = "desc"
    var limit: Int? = 100
}

struct MusicPlaylist: Codable, Identifiable, Hashable, Sendable {
    let id: String
    var name: String
    var description: String?
    var ownerName: String?
    var mine: Bool?
    var shared: Bool?
    var collaborative: Bool?
    var smart: Bool?
    var rules: SmartRules?
    var trackCount: Int?
    var durationSeconds: Double?
    var artUrls: [String]?
    var createdAt: String?
    var updatedAt: String?
    var pinned: Bool?

    var isMine: Bool { mine ?? true }
    var isSmart: Bool { smart == true }
    var canEditTracks: Bool { !isSmart && (isMine || collaborative == true) }
}

struct MusicMix: Codable, Identifiable, Hashable, Sendable {
    let id: String
    var name: String
    var description: String?
    var artUrls: [String]?
    var seedType: String?
    var seedId: String?
}

struct MusicRecentItem: Decodable, Hashable, Sendable, Identifiable {
    var kind: String
    var album: MusicAlbum?
    var playlist: MusicPlaylist?
    var artist: MusicArtist?
    var mix: MusicMix?

    var id: String {
        "\(kind)-\(album?.id ?? playlist?.id ?? artist?.id ?? mix?.id ?? UUID().uuidString)"
    }
}

struct MusicOnThisDay: Decodable, Hashable, Sendable {
    var label: String?
    var tracks: [MusicTrack]?
}

struct MusicDecade: Decodable, Hashable, Sendable, Identifiable {
    var decade: Int
    var label: String?
    var trackCount: Int?
    var id: Int { decade }
    var title: String { label ?? "\(decade)s" }
}

struct MusicHome: Decodable, Sendable {
    var pinned: [MusicPlaylist]?
    var recentlyPlayed: [MusicRecentItem]?
    var recentlyAdded: [MusicAlbum]?
    var heavyRotation: [MusicAlbum]?
    var mixes: [MusicMix]?
    var forgottenFavorites: [MusicTrack]?
    var onThisDay: MusicOnThisDay?
    var discover: [MusicAlbum]?
    var lovedTracks: [MusicTrack]?
    var decades: [MusicDecade]?
    var trackCount: Int?
    var albumCount: Int?
    var artistCount: Int?

    var isEmpty: Bool { (trackCount ?? 0) == 0 && (recentlyAdded ?? []).isEmpty && (discover ?? []).isEmpty }
}

struct MusicGenre: Decodable, Hashable, Sendable, Identifiable {
    var name: String
    var trackCount: Int?
    var albumCount: Int?
    var artUrls: [String]?
    var id: String { name }
}

struct MusicArtistDetail: Decodable, Sendable {
    var artist: MusicArtist
    var albums: [MusicAlbum]?
    var appearsOn: [MusicAlbum]?
    var topTracks: [MusicTrack]?
    var similar: [MusicArtist]?

    enum CodingKeys: String, CodingKey { case albums, appearsOn, topTracks, similar }

    init(from decoder: Decoder) throws {
        artist = try MusicArtist(from: decoder)
        let container = try decoder.container(keyedBy: CodingKeys.self)
        albums = try container.decodeIfPresent([MusicAlbum].self, forKey: .albums)
        appearsOn = try container.decodeIfPresent([MusicAlbum].self, forKey: .appearsOn)
        topTracks = try container.decodeIfPresent([MusicTrack].self, forKey: .topTracks)
        similar = try container.decodeIfPresent([MusicArtist].self, forKey: .similar)
    }
}

struct MusicAlbumDetail: Decodable, Sendable {
    var album: MusicAlbum
    var tracks: [MusicTrack]
    var moreByArtist: [MusicAlbum]?
    var totalBytes: Int64?
    var formats: [String]?

    enum CodingKeys: String, CodingKey { case tracks, moreByArtist, totalBytes, formats }

    init(from decoder: Decoder) throws {
        album = try MusicAlbum(from: decoder)
        let container = try decoder.container(keyedBy: CodingKeys.self)
        tracks = try container.decodeIfPresent([MusicTrack].self, forKey: .tracks) ?? []
        moreByArtist = try container.decodeIfPresent([MusicAlbum].self, forKey: .moreByArtist)
        totalBytes = try container.decodeIfPresent(Int64.self, forKey: .totalBytes)
        formats = try container.decodeIfPresent([String].self, forKey: .formats)
    }
}

struct MusicPlaylistDetail: Decodable, Sendable {
    var playlist: MusicPlaylist
    var tracks: [MusicTrack]

    enum CodingKeys: String, CodingKey { case tracks }

    init(from decoder: Decoder) throws {
        playlist = try MusicPlaylist(from: decoder)
        let container = try decoder.container(keyedBy: CodingKeys.self)
        tracks = try container.decodeIfPresent([MusicTrack].self, forKey: .tracks) ?? []
    }
}

struct MusicSearchResults: Decodable, Sendable {
    var artists: [MusicArtist]?
    var albums: [MusicAlbum]?
    var tracks: [MusicTrack]?
    var playlists: [MusicPlaylist]?
    var genres: [String]?

    var isEmpty: Bool {
        (artists ?? []).isEmpty && (albums ?? []).isEmpty && (tracks ?? []).isEmpty
            && (playlists ?? []).isEmpty && (genres ?? []).isEmpty
    }
}

struct MusicFolderEntry: Decodable, Hashable, Sendable, Identifiable {
    var name: String
    var path: String
    var trackCount: Int?
    // Only on the libraries-as-folders first page.
    var libraryId: String?
    var id: String { (libraryId ?? "") + "/" + path }
}

struct MusicFolderListing: Decodable, Sendable {
    var libraryId: String?
    var path: String?
    var parent: String?
    var folders: [MusicFolderEntry]?
    var tracks: [MusicTrack]?
}

struct MusicLyricLine: Decodable, Hashable, Sendable {
    var time: Double?
    var text: String
}

struct MusicLyrics: Decodable, Sendable {
    var synced: Bool?
    var lines: [MusicLyricLine]?
    var plain: String?
    var source: String?
}

struct MusicMixResult: Decodable, Sendable {
    var name: String?
    var tracks: [MusicTrack]
}

struct MusicLibrary: Codable, Identifiable, Hashable, Sendable {
    let id: String
    var name: String
    var path: String?
    var network: Bool?
    var trackCount: Int?
    var lastScanFinishedAt: String?
    var lastScanError: String?
    var createdAt: String?
}

struct MusicScan: Decodable, Hashable, Sendable {
    var phase: String?
    var libraryName: String?
    var processed: Int?
    var total: Int?
    var startedAt: String?
}

struct MusicStatus: Decodable, Sendable {
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

struct MusicLibraryInput: Encodable, Sendable {
    var name: String
    var path: String
}

struct MusicPlayedBody: Codable, Sendable {
    var secondsListened: Double
    var completed: Bool
    var skipped: Bool
    var playedAt: String?
    var deviceName: String
}

struct MusicPlaylistInput: Encodable, Sendable {
    var name: String
    var description: String?
    var shared: Bool?
    var collaborative: Bool?
    var rules: SmartRules?
    var trackIds: [String]?
}

struct MusicDownloadsStatus: Decodable, Sendable {
    var enabled: Bool?
    var canDownload: Bool?
    var musicLibrary: Bool?
    var status: MediaDownloadServiceStatus?
}

struct MusicLookupResult: Decodable, Hashable, Sendable, Identifiable {
    var mbid: String?
    var title: String
    var artist: String?
    var year: Int?
    var type: String?
    var trackCount: Int?
    var artUrl: String?
    var inLibrary: Bool?
    var id: String { mbid ?? "\(artist ?? "")-\(title)-\(year ?? 0)" }
}

struct MusicRelease: Decodable, Hashable, Sendable, Identifiable {
    var id: String
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

// A search runs in the background (the VPN can take a minute to come up); poll it every 2 s.
struct MusicReleaseSearch: Decodable, Sendable {
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

struct MusicDownloadList: Decodable, Sendable {
    var status: MediaDownloadServiceStatus?
    var active: [MediaDownload]?
    var recent: [MediaDownload]?
}

// MARK: - Podcast objects (§5)

struct PodcastFunding: Codable, Hashable, Sendable {
    var url: String
    var title: String?
}

struct PodcastSettings: Codable, Hashable, Sendable {
    var autoDownload: Bool?
    var keepEpisodes: Int?
    var voiceBoost: Bool?
    var trimSilence: Bool?
    var transcribe: Bool?
}

struct PodcastMySettings: Codable, Hashable, Sendable {
    var playbackSpeed: Double?
    var skipIntroSeconds: Int?
    var skipOutroSeconds: Int?
    var notify: Bool?
    var autoQueue: Bool?
    var inBriefing: Bool?
    var sort: String?
}

struct Podcast: Codable, Identifiable, Hashable, Sendable {
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

    enum CodingKeys: String, CodingKey {
        case id, feedUrl, title, author, description, artUrl, link, categories, language, explicit
        case episodeCount, unplayedCount, newestEpisodeAt, lastRefreshedAt, refreshError, subscribed
        case subscriberCount, funding, settings, mySettings
    }

    // A preview ("Podcast-without-id-fields") has no id; it gets the feed URL instead.
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        feedUrl = try c.decodeIfPresent(String.self, forKey: .feedUrl)
        id = try c.decodeIfPresent(String.self, forKey: .id) ?? feedUrl ?? UUID().uuidString
        title = try c.decodeIfPresent(String.self, forKey: .title) ?? "Untitled"
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

    var isSubscribed: Bool { subscribed == true }
}

struct EpisodeDownload: Codable, Hashable, Sendable {
    var state: String?
    var progress: Double?
    var error: String?
    var enhanced: Bool?
    var timeSavedSeconds: Double?

    var isDone: Bool { state == "done" }
    var isActive: Bool { state == "queued" || state == "downloading" }
}

struct EpisodeProgress: Codable, Hashable, Sendable {
    var positionSeconds: Double?
    var durationSeconds: Double?
    var percent: Double?
    var played: Bool?
    var updatedAt: String?

    var isPlayed: Bool { played == true }
    var position: Double { positionSeconds ?? 0 }
}

struct PodcastEpisode: Codable, Identifiable, Hashable, Sendable {
    let id: String
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

    var isPlayed: Bool { progress?.isPlayed == true }
    var duration: Double { durationSeconds ?? progress?.durationSeconds ?? 0 }
    var isInProgress: Bool { !isPlayed && (progress?.position ?? 0) > 0 }
    var publishedDate: Date? { AudioDate.parse(publishedAt) }

    // "S2 E14" / "E512" / "Trailer"
    var numberLabel: String? {
        if episodeType == "trailer" { return "Trailer" }
        if episodeType == "bonus" { return "Bonus" }
        switch (season, episode) {
        case let (s?, e?): return "S\(s) E\(e)"
        case let (nil, e?): return "E\(e)"
        default: return nil
        }
    }

    // "28 min left", "1 hr 4 min", "Played"
    var remainingLabel: String {
        if isPlayed { return "Played" }
        let total = duration
        let position = progress?.position ?? 0
        if position > 0, total > 0 {
            return AudioFormat.duration(max(0, total - position), style: .short) + " left"
        }
        return total > 0 ? AudioFormat.duration(total, style: .short) : ""
    }
}

struct PodcastEpisodeDetail: Decodable, Sendable {
    var episode: PodcastEpisode
    var description: String?
    var descriptionText: String?

    enum CodingKeys: String, CodingKey { case description, descriptionText }

    init(from decoder: Decoder) throws {
        episode = try PodcastEpisode(from: decoder)
        let container = try decoder.container(keyedBy: CodingKeys.self)
        description = try container.decodeIfPresent(String.self, forKey: .description)
        descriptionText = try container.decodeIfPresent(String.self, forKey: .descriptionText)
    }
}

struct DirectoryShow: Decodable, Hashable, Sendable, Identifiable {
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

struct DirectoryGenre: Decodable, Hashable, Sendable, Identifiable {
    var id: Int
    var name: String
}

struct PodcastPreview: Decodable, Sendable {
    var podcast: Podcast
    var episodes: [PodcastEpisode]?
}

struct PodcastEpisodesPage: Decodable, Sendable {
    var items: [PodcastEpisode]
    var total: Int?
    var seasons: [Int]?
}

struct OPMLImportResult: Decodable, Sendable {
    struct Failure: Decodable, Sendable, Hashable {
        var feedUrl: String?
        var error: String?
    }
    var added: Int?
    var existing: Int?
    var failed: [Failure]?
}

struct PodcastBookmark: Decodable, Identifiable, Hashable, Sendable {
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

struct PodcastHome: Decodable, Sendable {
    var upNext: [PodcastEpisode]?
    var continueListening: [PodcastEpisode]?
    var newEpisodes: [PodcastEpisode]?
    var shortOnes: [PodcastEpisode]?
    var downloaded: [PodcastEpisode]?
    var subscriptions: [Podcast]?
    var bookmarks: [PodcastBookmark]?
    var timeSavedSeconds: Double?
}

struct PodcastChapter: Decodable, Hashable, Sendable, Identifiable {
    var startSeconds: Double
    var endSeconds: Double?
    var title: String
    var imageUrl: String?
    var url: String?
    var id: Double { startSeconds }
}

struct TranscriptSegment: Decodable, Hashable, Sendable {
    var start: Double
    var end: Double?
    var speaker: String?
    var text: String
}

struct PodcastTranscript: Decodable, Sendable {
    var source: String?
    var language: String?
    var segments: [TranscriptSegment]
}

struct TranscriptHit: Decodable, Hashable, Sendable, Identifiable {
    var episodeId: String
    var episodeTitle: String?
    var podcastId: String?
    var podcastTitle: String?
    var startSeconds: Double
    var snippet: String?
    var id: String { "\(episodeId)@\(startSeconds)" }
}

struct PodcastClip: Decodable, Hashable, Sendable, Identifiable {
    var id: String
    var url: String?
    var durationSeconds: Double?
    var title: String?
    var episodeId: String?
    var episodeTitle: String?
    var podcastTitle: String?
    var startSeconds: Double?
    var createdAt: String?
}

struct PodcastTranscriptsStatus: Decodable, Sendable {
    var enabled: Bool?
    var available: Bool?
    var error: String?
    var queued: Int?
}

struct PodcastStatus: Decodable, Sendable {
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

struct PodcastFill: Decodable, Sendable {
    var episodes: [PodcastEpisode]
    var totalSeconds: Double?
}

// MARK: - Play where you left off (§7)

struct AudioQueueRef: Codable, Hashable, Sendable {
    var type: String   // track | episode
    var id: String
}

struct AudioPlayContext: Codable, Hashable, Sendable {
    var type: String   // album|playlist|artist|mix|podcast|queue|search|radio
    var id: String?
    var name: String?
}

struct NowPlayingReport: Codable, Sendable {
    var deviceId: String
    var deviceName: String
    var kind: String
    var queue: [AudioQueueRef]
    var index: Int
    var positionSeconds: Double
    var playing: Bool
    var shuffle: Bool
    var `repeat`: String
    var speed: Double
    var context: AudioPlayContext?
}

struct NowPlayingDevice: Decodable, Sendable, Identifiable {
    var deviceId: String
    var deviceName: String?
    var kind: String?
    var queue: [AudioQueueRef]?
    var index: Int?
    var positionSeconds: Double?
    var playing: Bool?
    var shuffle: Bool?
    var `repeat`: String?
    var speed: Double?
    var context: AudioPlayContext?
    var updatedAt: String?
    var id: String { deviceId }
}

struct NowPlayingResolved: Decodable, Sendable {
    var nowPlaying: NowPlayingDevice?
    var tracks: [MusicTrack]?
    var episodes: [PodcastEpisode]?
}

// MARK: - Speakers (§8)

struct SpeakerHeartbeat: Encodable, Sendable {
    var name: String
    var kind: String
    var allowAnnouncements: Bool
    var volume: Double
}

struct SpeakerState: Codable, Hashable, Sendable {
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

struct AudioSpeaker: Decodable, Identifiable, Hashable, Sendable {
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
        case "tv": "appletv"
        case "kiosk": "display"
        case "phone": "iphone"
        case "tablet": "ipad"
        case "mac": "desktopcomputer"
        case "windows": "pc"
        case "web": "globe"
        default: "hifispeaker"
        }
    }
}

struct SpeakerCommand: Codable, Sendable {
    var action: String
    var queue: [AudioQueueRef]?
    var index: Int?
    var positionSeconds: Double?
    var shuffle: Bool?
    var volume: Double?
    var rampSeconds: Double?
    var text: String?
    var context: AudioPlayContext?
    // Only on what the long poll hands back.
    var seq: Int?
    var at: String?
    var from: String?
}

struct SpeakerCommandAck: Decodable, Sendable {
    var seq: Int?
}

struct SpeakerCommands: Decodable, Sendable {
    var commands: [SpeakerCommand]
    var latestSeq: Int?
}

// MARK: - Alarms, briefing, stats, ListenBrainz (§9)

struct WakeAlarmSource: Codable, Hashable, Sendable {
    var type: String
    var id: String?
    var name: String?
}

struct WakeAlarm: Codable, Identifiable, Hashable, Sendable {
    var id: String?
    var name: String
    var time: String
    var days: [String]
    var speakerId: String
    var source: WakeAlarmSource
    var volume: Double
    var rampSeconds: Int
    var enabled: Bool
    var nextFireAt: String?
    var lastFiredAt: String?
}

struct MorningBriefing: Decodable, Sendable {
    var greeting: String?
    var speech: [String]?
    var episodes: [PodcastEpisode]?
}

struct StatsArtistEntry: Decodable, Hashable, Sendable {
    var artist: MusicArtist
    var plays: Int?
    var minutes: Double?
}

struct StatsAlbumEntry: Decodable, Hashable, Sendable {
    var album: MusicAlbum
    var plays: Int?
}

struct StatsTrackEntry: Decodable, Hashable, Sendable {
    var track: MusicTrack
    var plays: Int?
}

struct StatsGenreEntry: Decodable, Hashable, Sendable {
    var genre: String
    var minutes: Double?
}

struct StatsFirstPlay: Decodable, Hashable, Sendable {
    var track: MusicTrack
    var playedAt: String?
}

struct MusicStats: Decodable, Sendable {
    var minutes: Double?
    var plays: Int?
    var skips: Int?
    var distinctTracks: Int?
    var distinctArtists: Int?
    var newArtists: Int?
    var topArtists: [StatsArtistEntry]?
    var topAlbums: [StatsAlbumEntry]?
    var topTracks: [StatsTrackEntry]?
    var topGenres: [StatsGenreEntry]?
    var byHour: [Double]?
    var byWeekday: [Double]?
    var longestStreakDays: Int?
    var firstPlay: StatsFirstPlay?
}

struct StatsShowEntry: Decodable, Hashable, Sendable {
    var podcast: Podcast
    var minutes: Double?
    var episodes: Int?
}

struct PodcastStats: Decodable, Sendable {
    var minutes: Double?
    var episodesFinished: Int?
    var timeSavedMinutes: Double?
    var topShows: [StatsShowEntry]?
    var byHour: [Double]?
    var byWeekday: [Double]?
}

struct AudioStats: Decodable, Sendable {
    var period: String?
    var from: String?
    var to: String?
    var music: MusicStats?
    var podcasts: PodcastStats?
}

struct ListenBrainzStatus: Decodable, Sendable {
    var connected: Bool?
    var username: String?
}

// MARK: - Formatting

enum AudioFormat {
    enum Style { case clock, short, long }

    static func duration(_ seconds: Double, style: Style = .clock) -> String {
        guard seconds.isFinite, seconds >= 0 else { return style == .clock ? "--:--" : "" }
        let total = Int(seconds.rounded(.down))
        let h = total / 3600, m = (total % 3600) / 60, s = total % 60
        switch style {
        case .clock:
            return h > 0 ? String(format: "%d:%02d:%02d", h, m, s) : String(format: "%d:%02d", m, s)
        case .short:
            if h > 0 { return m > 0 ? "\(h) hr \(m) min" : "\(h) hr" }
            return m > 0 ? "\(m) min" : "\(max(s, 1)) sec"
        case .long:
            if h > 0 { return "\(h) hour\(h == 1 ? "" : "s"), \(m) minute\(m == 1 ? "" : "s")" }
            return "\(m) minute\(m == 1 ? "" : "s")"
        }
    }

    static func bytes(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }

    static func relative(_ iso: String?) -> String? {
        guard let date = AudioDate.parse(iso) else { return nil }
        if Calendar.current.isDateInToday(date) { return "Today" }
        if Calendar.current.isDateInYesterday(date) { return "Yesterday" }
        if date > Date().addingTimeInterval(-6 * 86400) { return date.formatted(.dateTime.weekday(.wide)) }
        if Calendar.current.isDate(date, equalTo: Date(), toGranularity: .year) {
            return date.formatted(.dateTime.month(.abbreviated).day())
        }
        return date.formatted(.dateTime.month(.abbreviated).day().year())
    }
}
