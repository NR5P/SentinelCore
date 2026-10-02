import Foundation

// Movie downloads ("+ Add" on the Movies tab) - the wire shapes of /api/media/downloads, as
// core/docs/MOVIE_DOWNLOADS.md describes them. Every field is optional, identities included, so a
// server that sends less than this never fails a decode; the screens fall back to something
// sensible instead. Dates stay ISO-8601 strings and are read through `mediaDownloadDate` when
// they are shown - a date in a shape the app does not expect costs one "3 h ago", not the list.
//
// Twin of ios/SentinelCore/Movies/MediaDownloadModels.swift - keep the two identical (the site
// admin shapes are unused on the TV, which leaves managing sites to the other apps).

// GET /api/media/downloads/status - whether to show "+ Add" at all. An older server answers 404,
// which reads as "off".
struct MediaDownloadsStatus: Decodable {
    var enabled: Bool?
    var canDownload: Bool?
    var canManageSites: Bool?
    var tmdbConfigured: Bool?
    var movieLibrary: Bool?
    var showLibrary: Bool?
    var status: MediaDownloadServiceStatus?
}

// The VPN and the torrent services behind it. `services: stopped` with `vpn.state: off` is the
// normal idle state: they start by themselves when someone searches or downloads.
struct MediaDownloadServiceStatus: Decodable, Hashable {
    var enabled: Bool?
    var services: String?
    var idleMinutes: Int?
    var vpn: MediaDownloadVpn?
}

// `state` is connected | connecting | down | off | unknown | disabled. `disabled` means Use VPN is
// off in Settings: downloads use the home connection and ip/location/country/provider/error are
// null. `verified`: the address the sites see was compared with the home address and differs -
// missing (an older server) counts as not verified.
struct MediaDownloadVpn: Decodable, Hashable {
    var state: String?
    var required: Bool?
    var verified: Bool?
    var verifiedAt: String?
    var ip: String?
    var location: String?
    var country: String?
    var provider: String?
    var checkedAt: String?
    var error: String?
    var account: MediaVpnAccount?
}

// The VPN account's paid time. Only sent for Mullvad; null or missing (an
// older server, another provider) shows nothing. `daysLeft` is whole days rounded down;
// `runningOut` is 7 days or less; `expired` is time used up.
struct MediaVpnAccount: Decodable, Hashable {
    var provider: String?
    var paidUntil: String?
    var daysLeft: Int?
    var runningOut: Bool?
    var expired: Bool?
    var checkedAt: String?
}

// POST /api/media/downloads/vpn/test - connects if needed and proves the sites see the VPN's
// address. `message` is the result to show either way; `status` redraws the light.
struct MediaVpnTestResult: Decodable {
    var ok: Bool?
    var message: String?
    var status: MediaDownloadServiceStatus?
}

// MARK: - The VPN light

// How the VPN looks everywhere it is shown: the light next to "+ Add" (`short`) and the line on
// the Downloads screen (`text`). One function so the two can never disagree - the dashboard's
// vpnLook in wwwroot/movies.js, and the table "The VPN light" in MOVIE_DOWNLOADS.md.
struct MediaVpnLook: Equatable {
    enum Tone: Equatable {
        case on      // green: connected and proven to hide the home address
        case busy    // cyan, pulsing: coming up, or connected but not yet checked
        case idle    // grey: switched off until needed (the normal resting state)
        case off     // amber: Use VPN is turned off in Settings
        case bad     // red: not installed, failed, or not getting through
    }

    var tone: Tone
    var short: String
    var text: String

    init(tone: Tone, short: String, text: String) {
        self.tone = tone
        self.short = short
        self.text = text
    }

    init(_ status: MediaDownloadServiceStatus?, now: Date = Date(), calendar: Calendar = .current) {
        let vpn = status?.vpn
        let services = status?.services
        if vpn?.state == "disabled" {
            self.init(tone: .off, short: "VPN off",
                      text: "Turned off in Settings · searching and downloading use your home connection, so the sites see your home address.")
        } else if services == "notInstalled" {
            self.init(tone: .bad, short: "VPN not set up",
                      text: "The VPN and torrent services are not installed on the server yet. Run core/downloads/install-downloads.sh on it once.")
        } else if vpn?.account?.expired == true {
            let day = mediaVpnAccountDay(vpn?.account?.paidUntil, now: now, calendar: calendar)
            self.init(tone: .bad, short: "VPN out of time",
                      text: "Your Mullvad account ran out of time\(day.map { " on \($0)" } ?? ""). Add time at mullvad.net and downloads work again.")
        } else if vpn?.state == "connected" {
            let place = [vpn?.location, vpn?.ip].compactMap { $0 }.filter { !$0.isEmpty }
            let whereText = place.isEmpty ? "" : " · " + place.joined(separator: " · ")
            if vpn?.verified == true {
                let country = vpn?.country ?? ""
                self.init(tone: .on, short: country.isEmpty ? "VPN on" : "VPN · \(country)",
                          text: "Connected and verified\(whereText) · your home address is hidden")
            } else {
                self.init(tone: .busy, short: "VPN checking",
                          text: "Connected\(whereText) · checking it hides your home address…")
            }
        } else if services == "starting" || vpn?.state == "connecting" {
            self.init(tone: .busy, short: "VPN connecting", text: "Connecting…")
        } else if services == "failed" || vpn?.state == "down" {
            self.init(tone: .bad, short: "VPN problem",
                      text: vpn?.error ?? "The VPN or torrent services failed to start. Check the server's log (journalctl -u sentinelcore-vpn).")
        } else if services == "external" {
            self.init(tone: .idle, short: "VPN idle",
                      text: "qBittorrent and Prowlarr are run outside this server; the VPN is checked when they are used.")
        } else {
            self.init(tone: .idle, short: "VPN idle",
                      text: "Off until someone searches or downloads, then it connects by itself. Press Test to check it now.")
        }
    }
}

// The Downloads screen's line under the light about the account's paid time - the table "The
// account line" in MOVIE_DOWNLOADS.md. nil when the server sent no account.
struct MediaVpnAccountLine: Equatable {
    enum Tone: Equatable {
        case plain    // secondary text: plenty of time left
        case warning  // amber: 7 days or less
        case problem  // red: out of time
    }

    var tone: Tone
    var text: String

    init(tone: Tone, text: String) {
        self.tone = tone
        self.text = text
    }

    init?(_ account: MediaVpnAccount?, now: Date = Date(), calendar: Calendar = .current) {
        guard let account else { return nil }
        let day = mediaVpnAccountDay(account.paidUntil, now: now, calendar: calendar)
        let days = max(account.daysLeft ?? 0, 0)
        if account.expired == true {
            self.init(tone: .problem,
                      text: "Mullvad ran out\(day.map { " on \($0)" } ?? "") · add time at mullvad.net")
        } else if account.runningOut == true {
            let when = days == 0 ? "today" : days == 1 ? "tomorrow" : "in \(days) days"
            self.init(tone: .warning,
                      text: "Mullvad runs out \(when)\(day.map { " (\($0))" } ?? "") · add time at mullvad.net")
        } else {
            let left = days == 0 ? "less than a day left" : days == 1 ? "1 day left" : "\(days) days left"
            self.init(tone: .plain,
                      text: day.map { "Mullvad paid until \($0) · \(left)" } ?? "Mullvad · \(left)")
        }
    }
}

// "Oct 27" in the device's time zone, or "Oct 27, 2027" when it is not this year.
func mediaVpnAccountDay(_ value: String?, now: Date, calendar: Calendar) -> String? {
    guard let date = mediaDownloadDate(value) else { return nil }
    let formatter = DateFormatter()
    formatter.locale = Locale(identifier: "en_US_POSIX")
    formatter.calendar = calendar
    formatter.timeZone = calendar.timeZone
    let sameYear = calendar.component(.year, from: date) == calendar.component(.year, from: now)
    formatter.dateFormat = sameYear ? "MMM d" : "MMM d, yyyy"
    return formatter.string(from: date)
}

// MARK: - 1. Finding the title (TMDB)

struct MediaDownloadLookup: Decodable {
    var tmdbConfigured: Bool?
    var results: [MediaDownloadLookupResult]?
}

struct MediaDownloadLookupResult: Decodable, Hashable, Identifiable {
    var kind: String?
    var tmdbId: Int?
    var title: String?
    var year: Int?
    var overview: String?
    var posterPath: String?
    var posterUrl: String?
    var inLibrary: Bool?
    var itemId: String?

    var id: String { "\(kind ?? "movie")-\(tmdbId.map(String.init) ?? title ?? "")" }
    var isShow: Bool { kind == "show" }
    var displayTitle: String { title ?? "Untitled" }
}

// GET /api/media/downloads/show/{tmdbId} - a show's seasons for picking. Season 0 (specials) last.
struct MediaDownloadShow: Decodable {
    var tmdbId: Int?
    var title: String?
    var year: Int?
    var overview: String?
    var posterPath: String?
    var posterUrl: String?
    var itemId: String?
    var seasons: [MediaDownloadShowSeason]?

    // Where the season picker starts: the first real season with an aired episode that is not
    // in the library yet, else the first season there is.
    var firstSeasonWithGap: Int? {
        let list = seasons ?? []
        let gap = list.first { season in
            (season.number ?? 0) > 0
                && (season.episodes ?? []).contains { $0.aired != false && $0.inLibrary != true }
        }
        return (gap ?? list.first)?.number
    }
}

struct MediaDownloadShowSeason: Decodable, Hashable, Identifiable {
    var number: Int?
    var name: String?
    var episodeCount: Int?
    var ownedCount: Int?
    var episodes: [MediaDownloadShowEpisode]?

    var id: Int { number ?? -1 }
    var title: String { name ?? (number == 0 ? "Specials" : "Season \(number ?? 0)") }
    var shortTitle: String { number == 0 ? "Specials" : "S\(number ?? 0)" }
    var isComplete: Bool { (episodeCount ?? 0) > 0 && ownedCount == episodeCount }
}

struct MediaDownloadShowEpisode: Decodable, Hashable, Identifiable {
    var number: Int?
    var name: String?
    var airDate: String?
    var aired: Bool?
    var inLibrary: Bool?

    var id: Int { number ?? -1 }
    // Out yet? A missing `aired` counts as out, like the server's own rule for a missing date.
    var isOut: Bool { aired != false }
}

// MARK: - 2. Searching the sites

// POST /api/media/downloads/search. `season` alone asks for a whole season (season packs);
// `season` + `episode` for one episode. nil fields are left out of the JSON.
struct MediaDownloadSearchInput: Encodable, Hashable {
    var kind: String
    var title: String
    var year: Int?
    var tmdbId: Int?
    var posterPath: String?
    var season: Int?
    var episode: Int?

    // "Dune: Part Two (2024)", "Bluey (2018) · S02E05", "Bluey · season 2".
    var headline: String {
        var text = title
        if let year { text += " (\(year))" }
        if let season {
            if let episode {
                text += " · " + mediaEpisodeCode(season: season, episode: episode)
            } else {
                text += " · season \(season)"
            }
        }
        return text
    }
}

// A search in progress. Poll GET /api/media/downloads/search/{id} every second until `phase` is
// done or failed; `message` is what goes under the spinner (or the failure).
struct MediaDownloadSearch: Decodable {
    var id: String?
    var phase: String?
    var message: String?
    var error: String?
    var kind: String?
    var title: String?
    var year: Int?
    var season: Int?
    var episode: Int?
    // What was asked for with people sharing it, best first.
    var results: [MediaRelease]?
    // The rest: a different title, year or episode, or nobody sharing. Shown collapsed.
    var others: [MediaRelease]?

    var isFinished: Bool { phase == "done" || phase == "failed" }
}

struct MediaRelease: Decodable, Hashable, Identifiable {
    var id: String?
    var title: String?
    var site: String?
    var sizeBytes: Int64?
    var seeders: Int?
    var leechers: Int?
    var publishedAt: String?
    var quality: String?
    var codec: String?
    var source: String?
    var hdr: Bool?
    var season: Int?
    var episode: Int?
    var seasonPack: Bool?
    var matches: Bool?
    var warning: String?

    // The little labels over the release name: "1080p", "HEVC", "WEB-DL", "HDR", "whole season".
    var chips: [String] {
        var list = [quality, codec, source].compactMap { $0 }.filter { !$0.isEmpty }
        if hdr == true { list.append("HDR") }
        if seasonPack == true { list.append("whole season") }
        return list
    }

    // "6.4 GB · 812 sharing · TheSite · 3 days ago"
    var factsLine: String {
        var parts: [String] = []
        if let size = mediaDownloadSizeLabel(sizeBytes) { parts.append(size) }
        if let seeders { parts.append("\(seeders) sharing") }
        if let site, !site.isEmpty { parts.append(site) }
        if let age = mediaDownloadAgo(publishedAt) { parts.append(age) }
        return parts.joined(separator: " · ")
    }
}

// MARK: - 3. Downloading

// POST /api/media/downloads. `libraryId` nil lets the server pick the first local library of the
// right kind.
struct MediaDownloadGrabInput: Encodable {
    var searchId: String
    var releaseId: String
    var libraryId: String?
}

// GET /api/media/downloads - the queue. `recent` is the last 30 finished.
struct MediaDownloadList: Decodable {
    var status: MediaDownloadServiceStatus?
    var active: [MediaDownload]?
    var recent: [MediaDownload]?
}

struct MediaDownload: Decodable, Hashable, Identifiable {
    var id: String?
    var kind: String?
    var title: String?
    var year: Int?
    var tmdbId: Int?
    var season: Int?
    var episode: Int?
    // "S02E05", "Season 2", or nil for a film.
    var subtitle: String?
    var posterUrl: String?
    var releaseTitle: String?
    var site: String?
    var sizeBytes: Int64?
    var state: String?
    var progress: Double?
    var downloadedBytes: Int64?
    var speedBytesPerSecond: Double?
    var etaSeconds: Double?
    var peers: Int?
    // On an active download a note ("Waiting for the VPN to connect."), not a failure.
    var error: String?
    var libraryId: String?
    // On done: the new movie, or the show an episode was filed under.
    var itemId: String?
    var requestedBy: String?
    var createdAt: String?
    var completedAt: String?
    var finishedAt: String?

    var displayTitle: String { title ?? "Untitled" }
    var isActive: Bool { ["queued", "downloading", "seeding", "importing"].contains(state ?? "") }
    var isDone: Bool { state == "done" }
    var isFailed: Bool { state == "failed" }
    var isDownloading: Bool { state == "downloading" }
    var fraction: Double { min(1, max(0, progress ?? 0)) }

    // "Bluey S02E05" - for banners and notices.
    var headline: String {
        if let subtitle, !subtitle.isEmpty { return "\(displayTitle) \(subtitle)" }
        return displayTitle
    }

    // The progress bar: where a download is, full while sharing back or adding to the library.
    var barFraction: Double {
        switch state {
        case "downloading": fraction
        case "queued": 0
        default: 1
        }
    }

    var stateWords: String {
        switch state {
        case "queued": "waiting"
        case "downloading": "downloading"
        case "seeding": "sharing back"
        case "importing": "adding to library"
        case "done": "ready"
        case "failed": "failed"
        case "canceled": "canceled"
        default: state ?? ""
        }
    }

    // "42% · 3.1 MB/s · 2 min left · 400 MB · 14 peers" while active,
    // "ready · 400 MB · by sam · 3 h ago" once finished.
    var factsLine: String {
        var parts: [String] = []
        if isActive {
            if isDownloading {
                let percent = (fraction * 1000).rounded() / 10
                parts.append(percent.formatted(.number.precision(.fractionLength(0...1))) + "%")
                if let speed = mediaDownloadSpeedLabel(speedBytesPerSecond) { parts.append(speed) }
                if let eta = mediaDownloadEtaLabel(etaSeconds) { parts.append(eta) }
            } else {
                parts.append(stateWords)
            }
            if let size = mediaDownloadSizeLabel(sizeBytes) { parts.append(size) }
            if isDownloading, let peers, peers > 0 { parts.append(peers == 1 ? "1 peer" : "\(peers) peers") }
        } else {
            parts.append(stateWords)
            if let size = mediaDownloadSizeLabel(sizeBytes) { parts.append(size) }
            if let requestedBy, !requestedBy.isEmpty { parts.append("by \(requestedBy)") }
            if let age = mediaDownloadAgo(finishedAt ?? createdAt) { parts.append(age) }
        }
        return parts.filter { !$0.isEmpty }.joined(separator: " · ")
    }
}

// MARK: - Sites (admin)

// An error body from the sites routes. 503 with `starting: true` means the VPN is still coming
// up: say so and ask again.
struct MediaDownloadsBusy: Decodable {
    var error: String?
    var starting: Bool?
}

struct MediaDownloadSite: Decodable, Hashable, Identifiable {
    var id: Int?
    var name: String?
    var enabled: Bool?
    var privacy: String?
    var language: String?
    var description: String?
    var definition: String?

    var displayName: String { name ?? definition ?? "Site" }
}

// One entry of GET /api/media/downloads/sites/catalog. `needsSetup: false` sites are added with
// one tap; the others show `fields` as a form first.
struct MediaDownloadSiteDefinition: Decodable, Hashable, Identifiable {
    var definition: String?
    var name: String?
    var description: String?
    var privacy: String?
    var language: String?
    var needsSetup: Bool?
    var fields: [MediaDownloadSiteField]?

    var id: String { definition ?? name ?? "" }
    var displayName: String { name ?? definition ?? "Site" }
}

struct MediaDownloadSiteField: Decodable, Hashable, Identifiable {
    var name: String?
    var label: String?
    // text | password | checkbox | select | number
    var type: String?
    var help: String?
    var value: MediaDownloadFieldValue?
    var options: [MediaDownloadSiteOption]?

    var id: String { name ?? label ?? "" }
    var displayLabel: String { label ?? name ?? "" }
}

struct MediaDownloadSiteOption: Decodable, Hashable, Identifiable {
    var value: String
    var label: String

    var id: String { value }

    private enum CodingKeys: String, CodingKey { case value, label }

    // The value is a string on the wire; a number is taken as one too.
    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        if let text = try? container.decode(String.self, forKey: .value) {
            value = text
        } else if let number = try? container.decode(Int.self, forKey: .value) {
            value = String(number)
        } else if let number = try? container.decode(Double.self, forKey: .value) {
            value = String(number)
        } else {
            value = ""
        }
        label = (try? container.decodeIfPresent(String.self, forKey: .label)) ?? value
    }
}

// A site field's value: whatever JSON Prowlarr keeps there. Null is a missing value (nil).
enum MediaDownloadFieldValue: Codable, Hashable {
    case bool(Bool)
    case int(Int)
    case double(Double)
    case string(String)
    case null

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() {
            self = .null
        } else if let value = try? container.decode(Bool.self) {
            self = .bool(value)
        } else if let value = try? container.decode(Int.self) {
            self = .int(value)
        } else if let value = try? container.decode(Double.self) {
            self = .double(value)
        } else if let value = try? container.decode(String.self) {
            self = .string(value)
        } else {
            // An object or array: nothing a form here can edit.
            self = .null
        }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .bool(let value): try container.encode(value)
        case .int(let value): try container.encode(value)
        case .double(let value): try container.encode(value)
        case .string(let value): try container.encode(value)
        case .null: try container.encodeNil()
        }
    }

    var boolValue: Bool {
        switch self {
        case .bool(let value): value
        case .string(let value): value == "true"
        case .int(let value): value != 0
        default: false
        }
    }

    var textValue: String {
        switch self {
        case .string(let value): value
        case .int(let value): String(value)
        case .double(let value): value.formatted(.number.grouping(.never))
        case .bool(let value): value ? "true" : "false"
        case .null: ""
        }
    }
}

// POST /api/media/downloads/sites
struct MediaDownloadSiteInput: Encodable {
    var definition: String
    var name: String?
    var fields: [String: MediaDownloadFieldValue]
}

// PUT /api/media/downloads/sites/{id}
struct MediaDownloadSiteUpdate: Encodable {
    var enabled: Bool
}

// MARK: - Wording

// "S02E05"
func mediaEpisodeCode(season: Int, episode: Int) -> String {
    let s = season < 10 ? "0\(season)" : String(season)
    let e = episode < 10 ? "0\(episode)" : String(episode)
    return "S\(s)E\(e)"
}

func mediaDownloadSizeLabel(_ bytes: Int64?) -> String? {
    guard let bytes, bytes > 0 else { return nil }
    return ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
}

func mediaDownloadSpeedLabel(_ bytesPerSecond: Double?) -> String? {
    guard let bytesPerSecond, bytesPerSecond > 0 else { return nil }
    if bytesPerSecond >= 1_000_000 {
        return (bytesPerSecond / 1_000_000).formatted(.number.precision(.fractionLength(1))) + " MB/s"
    }
    return "\(max(1, Int((bytesPerSecond / 1000).rounded()))) KB/s"
}

func mediaDownloadEtaLabel(_ seconds: Double?) -> String? {
    guard let seconds, seconds.isFinite, seconds > 0 else { return nil }
    if seconds < 90 { return "about a minute left" }
    if seconds < 3600 { return "\(Int((seconds / 60).rounded())) min left" }
    let hours = Int(seconds / 3600)
    let minutes = Int((seconds.truncatingRemainder(dividingBy: 3600) / 60).rounded())
    return "\(hours) h \(minutes) min left"
}

func mediaDownloadDate(_ value: String?) -> Date? {
    guard let value, !value.isEmpty else { return nil }
    if let date = try? Date.ISO8601FormatStyle(includingFractionalSeconds: true).parse(value) { return date }
    return try? Date.ISO8601FormatStyle(includingFractionalSeconds: false).parse(value)
}

// "3 hours ago"
func mediaDownloadAgo(_ value: String?) -> String? {
    guard let date = mediaDownloadDate(value) else { return nil }
    return date.formatted(.relative(presentation: .named))
}
