import Foundation

// Movies & TV - the wire shapes of /api/media, as core/docs/MOVIES.md describes them. Everything
// an older server might not send is optional; only the handful of fields the screens cannot draw
// without (an item's id, kind and title; a playback's mode and url) are required. Twin of
// ios/SentinelCore/Movies/MediaModels.swift - keep the two identical.
//
// Dates: `addedAt`, `updatedAt` and the scan timestamps are full ISO-8601 instants and decode
// through JSONDecoder.sentinel. `airDate` and `releaseDate` are bare calendar dates ("2009-04-10"),
// which that decoder's date strategy rejects, so they stay strings.

struct MediaEnabled: Codable {
    let enabled: Bool
}

struct MediaLibrary: Codable, Identifiable, Hashable {
    let id: String
    var name: String
    var kind: String?
    var path: String?
    // A shared folder on another computer (smb://…), read over the network by the server.
    var network: Bool?
    var itemCount: Int?
    var lastScanStartedAt: Date?
    var lastScanFinishedAt: Date?
    var lastScanError: String?
}

// MARK: - Library management (admin) - MOVIES.md "Libraries", "Folder picker" and "Folders on other computers"

struct MediaLibraryInput: Encodable {
    var name: String
    var kind: String
    var path: String
}

// One page of the folder picker. `kind` is roots | local | computer | network. A folder that
// could not be opened still comes back as a page, with `error` (and `needsLogin` when signing in
// to that computer would fix it).
struct MediaBrowseListing: Decodable {
    var path: String
    var parent: String?
    var kind: String?
    var host: String?
    var folders: [MediaBrowseFolder]
    var computers: [MediaNetworkComputer]?
    var videoCount: Int?
    var hint: String?
    var error: String?
    var needsLogin: Bool?

    var isRoots: Bool { kind == "roots" || path.isEmpty }
    // A computer is not a folder; one of its shared folders, or anything inside one, is.
    var isPickable: Bool { !path.isEmpty && kind != "computer" && error == nil }
}

struct MediaBrowseFolder: Decodable, Identifiable, Hashable {
    var name: String
    var path: String
    var videoCount: Int?

    var id: String { path }
}

// A computer on the network the server signs in to for its shared folders. Never carries the
// password; `hasPassword` says whether one is saved.
struct MediaNetworkComputer: Decodable, Identifiable, Hashable {
    let id: String
    var name: String
    var host: String
    var username: String?
    var domain: String?
    var hasPassword: Bool?
    var path: String
}

struct MediaNetworkComputerInput: Encodable {
    var host: String
    var name: String?
    var username: String
    // Blank on an edit keeps the saved password.
    var password: String
    var domain: String?
}

struct MediaNetworkShare: Decodable, Hashable {
    var name: String
    var path: String
}

struct MediaNetworkSignIn: Decodable {
    var computer: MediaNetworkComputer
    var shares: [MediaNetworkShare]?
}

// A computer that answered "who shares files?" on the local network (mDNS). Windows PCs don't.
struct MediaDiscoveredComputer: Decodable, Identifiable, Hashable {
    var name: String
    var host: String
    var address: String?
    var path: String
    var savedId: String?

    var id: String { path }
}

struct MediaScan: Codable, Hashable {
    var phase: String?
    var libraryName: String?
    var processed: Int?
    var total: Int?
    var startedAt: Date?
}

struct MediaStatus: Codable {
    var enabled: Bool
    var ffmpegAvailable: Bool?
    var tmdbConfigured: Bool?
    var scanning: Bool?
    var scan: MediaScan?
    var libraries: [MediaLibrary]?
    var movieCount: Int?
    var showCount: Int?
    var episodeCount: Int?
    var lastScanFinishedAt: Date?
}

// The signed-in account's place in an item. Null on the wire when never played.
struct MediaProgress: Codable, Hashable {
    var positionSeconds: Double?
    var durationSeconds: Double?
    var percent: Double?
    var watched: Bool?
    var updatedAt: Date?

    var isWatched: Bool { watched == true }
    var position: Double { positionSeconds ?? 0 }
    // 0...1 of the way through, for the thin bar under a tile - nil once watched or never started.
    var fraction: Double? {
        guard !isWatched else { return nil }
        if let percent, percent > 0 { return min(1, max(0, percent / 100)) }
        if let durationSeconds, durationSeconds > 0, position > 0 { return min(1, position / durationSeconds) }
        return nil
    }
}

struct MediaItemSummary: Codable, Identifiable, Hashable {
    let id: String
    var kind: String
    var libraryId: String?
    var title: String
    var sortTitle: String?
    var year: Int?
    var genres: [String]?
    var rating: Double?
    var certification: String?
    var runtimeMinutes: Int?
    var durationSeconds: Double?
    var posterUrl: String?
    var backdropUrl: String?
    var thumbUrl: String?
    var addedAt: Date?
    var progress: MediaProgress?

    // Episodes only.
    var showId: String?
    var showTitle: String?
    var seasonNumber: Int?
    var episodeNumber: Int?
    var airDate: String?

    // Shows only.
    var seasonCount: Int?
    var episodeCount: Int?
    var watchedEpisodeCount: Int?
    var nextEpisodeId: String?

    // Sent on summaries from 2026-09-26 so shelves and episode lists can show it without a
    // detail request; optional because an earlier server does not.
    var overview: String?

    // Sent from 2026-09-29 (Most played, My List). Optional, not defaulted: a `var x = 0` would
    // make the synthesized decoder require the key, and an older server leaves both out. Read
    // them through `plays` and `isInList`.
    var playCount: Int?
    var inList: Bool?
}

extension MediaItemSummary {
    var isMovie: Bool { kind == "movie" }
    var isShow: Bool { kind == "show" }
    var isEpisode: Bool { kind == "episode" }
    var isWatched: Bool { progress?.isWatched == true }
    var isInList: Bool { inList == true }
    // Everyone's presses of Play, a show adding up its episodes.
    var plays: Int { playCount ?? 0 }
    // A place to pick up from: started, not finished.
    var canResume: Bool { !isWatched && (progress?.position ?? 0) > 0 }
    var progressFraction: Double? { progress?.fraction }

    // "S1 · E3" - the episode's coordinates, the way every app and the dashboard write them.
    var episodeCode: String? {
        guard isEpisode, let seasonNumber, let episodeNumber else { return nil }
        return "S\(seasonNumber) · E\(episodeNumber)"
    }

    // "E3 · Title" for an episode row under its season.
    var episodeRowTitle: String {
        if let episodeNumber { return "E\(episodeNumber) · \(title)" }
        return title
    }

    // What to put on the big line of a tile: the show for an episode, else the title itself.
    var headline: String { isEpisode ? (showTitle ?? title) : title }

    // The second line under a Continue Watching tile: "S1 · E3 · Title" for an episode, the year
    // for anything else.
    var subheadline: String? {
        if isEpisode { return [episodeCode, title].compactMap { $0 }.joined(separator: " · ") }
        return year.map(String.init)
    }

    var yearLabel: String? { year.map(String.init) }

    // "1h 42m" from TMDB's runtime, else from the probed file.
    var runtimeLabel: String? {
        let minutes: Int
        if let runtimeMinutes, runtimeMinutes > 0 {
            minutes = runtimeMinutes
        } else if let durationSeconds, durationSeconds > 0 {
            minutes = Int((durationSeconds / 60).rounded())
        } else {
            return nil
        }
        return minutes >= 60 ? "\(minutes / 60)h \(minutes % 60)m" : "\(minutes)m"
    }

    var ratingLabel: String? {
        guard let rating, rating > 0 else { return nil }
        return "★ " + rating.formatted(.number.precision(.fractionLength(1)))
    }

    // "2009 · 1h 42m · ★ 6.2" - the line under a title. Certification is drawn as its own badge.
    var metaLine: String {
        var parts: [String] = []
        if let yearLabel { parts.append(yearLabel) }
        if isShow, let seasonCount, seasonCount > 0 {
            parts.append(seasonCount == 1 ? "1 season" : "\(seasonCount) seasons")
        } else if let runtimeLabel {
            parts.append(runtimeLabel)
        }
        if let ratingLabel { parts.append(ratingLabel) }
        return parts.joined(separator: " · ")
    }

    // "Played 3 times" - nil until someone has pressed Play.
    var playCountLabel: String? {
        guard plays > 0 else { return nil }
        return plays == 1 ? "Played once" : "Played \(plays) times"
    }

    // The meta line on a detail page: metaLine plus how often it has been played.
    var detailMetaLine: String {
        [metaLine, playCountLabel].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: " · ")
    }

    // Episodes still to watch on a show - the badge on its poster. nil when nothing is left or
    // the server never said.
    var unwatchedEpisodeCount: Int? {
        guard isShow, let episodeCount else { return nil }
        let remaining = episodeCount - (watchedEpisodeCount ?? 0)
        return remaining > 0 ? remaining : nil
    }

    // Artwork for a 16:9 tile: an episode's still, else the backdrop, else nothing (a text card).
    var landscapeArtPath: String? { thumbUrl ?? backdropUrl }

    // Where selecting this tile goes: an episode opens its show, everything else itself.
    var route: MediaRoute {
        if isEpisode, let showId { return MediaRoute(id: showId, title: showTitle ?? title) }
        return MediaRoute(id: id, title: title)
    }

    // Progress-bar time remaining: "42 min left".
    var remainingLabel: String? {
        guard canResume, let progress else { return nil }
        let total = progress.durationSeconds ?? durationSeconds ?? runtimeMinutes.map { Double($0) * 60 }
        guard let total, total > progress.position else { return nil }
        let minutes = Int(((total - progress.position) / 60).rounded(.up))
        return minutes >= 60 ? "\(minutes / 60)h \(minutes % 60)m left" : "\(minutes) min left"
    }
}

struct MediaCastMember: Codable, Hashable {
    var name: String
    var character: String?

    var line: String {
        if let character, !character.isEmpty { return "\(name) — \(character)" }
        return name
    }
}

struct MediaFileInfo: Codable, Hashable {
    var container: String?
    var videoCodec: String?
    var videoProfile: String?
    var width: Int?
    var height: Int?
    var audioCodec: String?
    var audioChannels: Int?
    var sizeBytes: Int64?
    var bitrate: Int64?
    var path: String?

    // "1080p · HEVC · EAC3 5.1"
    var summaryLine: String? {
        var parts: [String] = []
        // By width as well as height: a 2.39:1 film encoded at 1920x800 is a 1080p file.
        if let height, height > 0 {
            let width = self.width ?? 0
            if height >= 2000 || width >= 3200 { parts.append("4K") }
            else if height >= 1000 || width >= 1800 { parts.append("1080p") }
            else if height >= 700 || width >= 1200 { parts.append("720p") }
            else { parts.append("\(height)p") }
        }
        if let videoCodec, !videoCodec.isEmpty {
            switch videoCodec.lowercased() {
            case "h264", "avc": parts.append("H.264")
            case "hevc", "h265": parts.append("HEVC")
            default: parts.append(videoCodec.uppercased())
            }
        }
        if let audioCodec, !audioCodec.isEmpty {
            var audio = audioCodec.uppercased()
            switch audioChannels {
            case 1: audio += " Mono"
            case 2: audio += " 2.0"
            case 6: audio += " 5.1"
            case 8: audio += " 7.1"
            case let channels? where channels > 0: audio += " \(channels)ch"
            default: break
            }
            parts.append(audio)
        }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }
}

struct MediaAudioTrack: Codable, Hashable {
    var index: Int
    var codec: String?
    var channels: Int?
    var language: String?
    var label: String?
    var isDefault: Bool?
}

struct MediaSubtitle: Codable, Hashable {
    var index: Int
    var language: String?
    var label: String?
    var url: String?
    var forced: Bool?
    var external: Bool?
}

struct MediaSeason: Codable, Identifiable, Hashable {
    var number: Int
    var name: String?
    var episodeCount: Int?
    var watchedCount: Int?
    var posterUrl: String?
    var episodes: [MediaItemSummary]?

    var id: Int { number }
    var title: String { name ?? (number == 0 ? "Specials" : "Season \(number)") }
    var episodeList: [MediaItemSummary] { episodes ?? [] }
}

// An ItemSummary plus the long-form fields. The summary half decodes as a MediaItemSummary from
// the same object, so every helper above works on a detail too - and so the two never drift.
struct MediaItemDetail: Decodable, Identifiable {
    var summary: MediaItemSummary
    var overview: String?
    var tagline: String?
    var releaseDate: String?
    var cast: [MediaCastMember]?
    var tmdbId: Int?
    var tmdbUrl: String?
    var file: MediaFileInfo?
    var audioTracks: [MediaAudioTrack]?
    var subtitles: [MediaSubtitle]?
    var seasons: [MediaSeason]?
    // This account may delete the movie from the server's disk (admins, movies on its own disks).
    var canDelete: Bool?

    var id: String { summary.id }

    private enum CodingKeys: String, CodingKey {
        case overview, tagline, releaseDate, cast, tmdbId, tmdbUrl, file, audioTracks, subtitles, seasons, canDelete
    }

    init(from decoder: Decoder) throws {
        summary = try MediaItemSummary(from: decoder)
        let container = try decoder.container(keyedBy: CodingKeys.self)
        overview = try container.decodeIfPresent(String.self, forKey: .overview)
        tagline = try container.decodeIfPresent(String.self, forKey: .tagline)
        releaseDate = try container.decodeIfPresent(String.self, forKey: .releaseDate)
        cast = try container.decodeIfPresent([MediaCastMember].self, forKey: .cast)
        tmdbId = try container.decodeIfPresent(Int.self, forKey: .tmdbId)
        tmdbUrl = try container.decodeIfPresent(String.self, forKey: .tmdbUrl)
        file = try container.decodeIfPresent(MediaFileInfo.self, forKey: .file)
        audioTracks = try container.decodeIfPresent([MediaAudioTrack].self, forKey: .audioTracks)
        subtitles = try container.decodeIfPresent([MediaSubtitle].self, forKey: .subtitles)
        seasons = try container.decodeIfPresent([MediaSeason].self, forKey: .seasons)
        canDelete = try container.decodeIfPresent(Bool.self, forKey: .canDelete)
    }

    var seasonList: [MediaSeason] { (seasons ?? []).sorted { $0.number < $1.number } }
    var allEpisodes: [MediaItemSummary] { seasonList.flatMap(\.episodeList) }

    // The episode a show's Play button starts: the server's pick, else the first one there is.
    var nextEpisode: MediaItemSummary? {
        if let id = summary.nextEpisodeId, let episode = allEpisodes.first(where: { $0.id == id }) { return episode }
        return allEpisodes.first
    }
}

struct MediaHome: Decodable {
    var continueWatching: [MediaItemSummary]?
    var recentMovies: [MediaItemSummary]?
    var recentShows: [MediaItemSummary]?
    var recentEpisodes: [MediaItemSummary]?
    var featured: MediaItemSummary?
    // From 2026-09-29; all five are missing from an older server, which reads as empty rows.
    var myList: [MediaItemSummary]?
    var mostPlayed: [MediaItemSummary]?
    var recentlyWatched: [MediaItemSummary]?
    var topRated: [MediaItemSummary]?
    var genreShelves: [MediaGenreShelf]?
    var movieCount: Int?
    var showCount: Int?
    var libraries: [MediaLibrary]?

    var isEmpty: Bool {
        (continueWatching ?? []).isEmpty && (recentMovies ?? []).isEmpty
            && (recentShows ?? []).isEmpty && (recentEpisodes ?? []).isEmpty && featured == nil
            && (myList ?? []).isEmpty && (mostPlayed ?? []).isEmpty && (recentlyWatched ?? []).isEmpty
            && (topRated ?? []).isEmpty && (genreShelves ?? []).allSatisfy(\.items.isEmpty)
    }
}

// One of Home's genre rows: "Comedy" and its titles.
struct MediaGenreShelf: Decodable, Identifiable {
    var genre: String
    var items: [MediaItemSummary]

    var id: String { genre }
}

struct MediaItemsPage: Decodable {
    var items: [MediaItemSummary]
    var total: Int?
    var genres: [String]?
}

struct MediaPlayback: Decodable {
    var itemId: String?
    var title: String?
    var subtitle: String?
    var durationSeconds: Double?
    var startSeconds: Double?
    var resumeSeconds: Double?
    var mode: String
    var url: String
    var mimeType: String?
    var video: String?
    var audio: String?
    var subtitles: [MediaSubtitle]?
    var nextEpisodeId: String?
    var nextEpisodeTitle: String?
    var backdropUrl: String?
    var posterUrl: String?

    // HLS sessions start their clock at startSeconds: the player's time 0 is the movie at
    // startSeconds, so every position the player reports gets this added before it is shown or
    // sent back. Direct play is the whole file and the player is simply seeked there instead.
    var isHLS: Bool { mode == "hls" }
    var clockOffset: Double { isHLS ? (startSeconds ?? 0) : 0 }
}

// POST /api/media/items/{id}/progress
struct MediaProgressBody: Encodable {
    var positionSeconds: Double
    var durationSeconds: Double?
}

// POST /api/media/items/{id}/watched and .../seasons/{n}/watched
struct MediaWatchedBody: Encodable {
    var watched: Bool
}

// POST /api/media/items/{id}/list
struct MediaListBody: Encodable {
    var inList: Bool
}

// The answer to it. `itemId` is the show when an episode's id was posted.
struct MediaListResult: Decodable {
    var itemId: String?
    var inList: Bool
}

// The sort menu over a Movies or TV Shows grid, in the server's vocabulary.
enum MediaSort: String, CaseIterable, Identifiable {
    case title, added, year, rating, plays
    var id: Self { self }
    var title: String {
        switch self {
        case .title: "Title"
        case .added: "Recently Added"
        case .year: "Year"
        case .rating: "Rating"
        case .plays: "Most Played"
        }
    }
}

// A detail screen to push: the id to load plus a title to show while it loads.
struct MediaRoute: Hashable {
    var id: String
    var title: String
}

// What to play and where from. nil start resumes from the saved progress; 0 starts over.
struct MoviePlayRequest: Identifiable, Hashable, Codable {
    var itemId: String
    var start: Double?
    // Shown in the player's info panel while the detail is at hand; the server's playback answer
    // carries the title itself.
    var overview: String?

    var id: String { "\(itemId)@\(start.map { String(Int($0)) } ?? "resume")" }

    static func resume(_ item: MediaItemSummary) -> MoviePlayRequest { MoviePlayRequest(itemId: item.id, start: nil) }
    static func startOver(_ item: MediaItemSummary) -> MoviePlayRequest { MoviePlayRequest(itemId: item.id, start: 0) }
}
