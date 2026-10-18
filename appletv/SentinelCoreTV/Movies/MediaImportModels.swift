import Foundation

// "Import a list" on Music and Movies - the wire shapes of {base}/import/preview and
// {base}/imports, as core/docs/MOVIE_DOWNLOADS.md "Import a list (Music and Movies)" describes
// them (server: core/SentinelCore.Web/MediaImportEndpoints.cs). The server reads the files
// (Exportify, Spotify's own JSON, TuneMyMusic, Letterboxd, IMDb, any CSV); the app only sends their
// text. Every field the server could leave out or send as null is optional, so a server that sends
// less never fails a decode. The wording matches the dashboard's wwwroot/media-import.js.
//
// Twin of ios/SentinelCore/Movies/MediaImportModels.swift - keep the two identical.

enum MediaImportKind: String, Hashable, Identifiable, Sendable {
    case music, movies

    var id: String { rawValue }
    var base: String { self == .music ? "/api/music/downloads" : "/api/media/downloads" }
    var thing: String { self == .music ? "album" : "movie" }
    var things: String { self == .music ? "albums" : "movies" }
    var tab: String { self == .music ? "Music" : "Movies" }

    // "1 album", "12 movies"
    func count(_ n: Int) -> String { mediaImportPlural(n, thing, things) }

    // The line shown under Imported Lists while there are none.
    var emptyLine: String {
        self == .movies
            ? "Got a Letterboxd or IMDb list? Import a List downloads everything on it."
            : "Got a Spotify list? Import a List downloads everything on it."
    }
}

func mediaImportPlural(_ n: Int, _ word: String, _ words: String? = nil) -> String {
    "\(n) \(n == 1 ? word : (words ?? word + "s"))"
}

// MARK: - Preview

// POST {base}/import/preview
struct MediaImportFile: Encodable, Hashable {
    var name: String
    var text: String
}

struct MediaImportPreviewInput: Encodable {
    var files: [MediaImportFile]
}

struct MediaImportPreview: Decodable {
    var kind: String?
    var sources: [String]?
    var tracks: Int?
    var noAlbum: Int?
    var shows: Int?
    var unreadable: Int?
    var problems: [String]?
    var albums: [MediaImportAlbum]?
    var movies: [MediaImportMovie]?

    // The albums (music) or films (movies) as one list to tick.
    func candidates(_ kind: MediaImportKind) -> [MediaImportCandidate] {
        var seen = Set<String>()
        let list: [MediaImportCandidate]
        switch kind {
        case .music:
            list = (albums ?? []).map {
                MediaImportCandidate(
                    key: "\($0.artist ?? "")\u{1}\($0.title ?? "")", artist: $0.artist, title: $0.title ?? "",
                    year: $0.year, trackCount: $0.trackCount, single: $0.single == true, inLibrary: $0.inLibrary == true)
            }
        case .movies:
            list = (movies ?? []).map {
                MediaImportCandidate(
                    key: "\($0.title ?? "")\u{1}\($0.year.map(String.init) ?? "")", artist: nil, title: $0.title ?? "",
                    year: $0.year, trackCount: nil, single: false, inLibrary: $0.inLibrary == true)
            }
        }
        // A key twice would confuse the checkboxes; the server drops duplicates anyway.
        return list.filter { !$0.title.isEmpty && seen.insert($0.key).inserted }
    }

    // "From Exportify · 312 songs → 40 albums · 3 already in the library"
    func facts(_ kind: MediaImportKind) -> [String] {
        let items = candidates(kind)
        var facts: [String] = []
        let names = (sources ?? []).filter { !$0.isEmpty }
        if !names.isEmpty { facts.append("From " + names.joined(separator: " + ")) }
        if kind == .music {
            facts.append("\(mediaImportPlural(tracks ?? 0, "song")) → \(mediaImportPlural(items.count, "album"))")
        } else {
            facts.append(mediaImportPlural(items.count, "movie"))
        }
        let have = items.filter(\.inLibrary).count
        if have > 0 { facts.append("\(have) already in the library") }
        return facts
    }

    // What was left out and why, then the server's own problems, then a word about singles.
    func notes(_ kind: MediaImportKind) -> [String] {
        var notes: [String] = []
        if let n = noAlbum, n > 0 {
            notes.append("\(mediaImportPlural(n, "song")) had no album, so \(n == 1 ? "it was" : "they were") left out.")
        }
        if let n = shows, n > 0 {
            notes.append("\(mediaImportPlural(n, "TV show")) left out: add \(n == 1 ? "it" : "them") with + Add to pick seasons.")
        }
        if let n = unreadable, n > 0 {
            notes.append("\(n) row\(n == 1 ? "" : "s") could not be read (podcast episodes, local files, blank titles).")
        }
        notes.append(contentsOf: (problems ?? []).filter { !$0.isEmpty })
        let singles = candidates(kind).filter { $0.single && !$0.inLibrary }.count
        if singles > 0 {
            notes.append("\(mediaImportPlural(singles, "single")): one-song releases are rarely shared on their own, so expect some of these to come back not found.")
        }
        return notes
    }
}

struct MediaImportAlbum: Decodable, Hashable {
    var artist: String?
    var title: String?
    var year: Int?
    var trackCount: Int?
    var tracks: [String]?
    var single: Bool?
    var inLibrary: Bool?
}

struct MediaImportMovie: Decodable, Hashable {
    var title: String?
    var year: Int?
    var imdbId: String?
    var inLibrary: Bool?
}

// One album or film on the preview's list.
struct MediaImportCandidate: Hashable, Identifiable {
    var key: String
    var artist: String?
    var title: String
    var year: Int?
    var trackCount: Int?
    var single: Bool
    var inLibrary: Bool

    var id: String { key }

    func matches(_ filter: String) -> Bool {
        let q = filter.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if q.isEmpty { return true }
        return "\(artist ?? "") \(title) \(year.map(String.init) ?? "")".lowercased().contains(q)
    }

    var startItem: MediaImportStartItem {
        MediaImportStartItem(artist: artist, title: title, year: year, trackCount: trackCount ?? 0)
    }
}

// What the list is called before anyone renames it: the file's name, or "Music import".
func mediaImportDefaultName(_ kind: MediaImportKind, fileNames: [String]) -> String {
    guard fileNames.count == 1, let name = fileNames.first else { return "\(kind.tab) import" }
    var base = name
    for ext in [".csv", ".json", ".txt", ".tsv"] where base.lowercased().hasSuffix(ext) {
        base = String(base.dropLast(ext.count))
        break
    }
    return base.replacingOccurrences(of: "_", with: " ")
}

// MARK: - Starting

// POST {base}/imports. nil fields are left out of the JSON.
struct MediaImportStartInput: Encodable {
    var name: String
    var libraryId: String?
    var items: [MediaImportStartItem]
}

struct MediaImportStartItem: Encodable, Hashable {
    var artist: String?
    var title: String
    var year: Int?
    var trackCount: Int
}

// POST {base}/imports/{id}/pause
struct MediaImportPauseInput: Encodable {
    var paused: Bool
}

// MARK: - Imported lists

// GET {base}/imports - newest first, without items.
struct MediaImportJobList: Decodable {
    var imports: [MediaImportJob]?
}

struct MediaImportJob: Decodable, Hashable, Identifiable {
    var id: String?
    var kind: String?
    var name: String?
    var requestedBy: String?
    var createdAt: String?
    var paused: Bool?
    // Why nothing is moving: "Waiting to try again: …". Not shown while paused.
    var message: String?
    var finished: Bool?
    var counts: MediaImportCounts?
    // Only on GET {base}/imports/{id} and the answers to changes; null in the list.
    var items: [MediaImportItem]?

    var displayName: String { (name ?? "").isEmpty ? "Imported list" : name ?? "" }
    var isPaused: Bool { paused == true }
    var isFinished: Bool { finished == true }
    var total: Int { counts?.total ?? items?.count ?? 0 }
    var missing: Int { (counts?.notFound ?? 0) + (counts?.failed ?? 0) }
    // Pause shows until the list is finished; Resume while it is paused.
    var canPause: Bool { !isFinished || isPaused }
    var canRetry: Bool { missing > 0 }
    // Still moving on its own: worth asking again soon.
    var isBusy: Bool { !isFinished && !isPaused }

    // "paused" | "finished" | "working"
    var statusWord: String { isPaused ? "paused" : isFinished ? "finished" : "working" }

    // How far through the list the server is: everything settled one way or another.
    var settledFraction: Double {
        guard let c = counts, let total = c.total, total > 0 else { return 1 }
        let settled = (c.done ?? 0) + (c.inLibrary ?? 0) + (c.notFound ?? 0) + (c.failed ?? 0) + (c.canceled ?? 0)
        return min(1, max(0, Double(settled) / Double(total)))
    }

    // "12 downloaded · 3 downloading · 25 to search · 2 not found"
    var summaryBits: [String] {
        guard let c = counts else { return [] }
        var bits: [String] = []
        if let n = c.done, n > 0 { bits.append("\(n) downloaded") }
        if let n = c.downloading, n > 0 { bits.append("\(n) downloading") }
        if let n = c.waiting, n > 0 { bits.append("\(n) to search") }
        if let n = c.inLibrary, n > 0 { bits.append("\(n) already had") }
        if let n = c.notFound, n > 0 { bits.append("\(n) not found") }
        if let n = c.failed, n > 0 { bits.append("\(n) failed") }
        return bits
    }

    var shownMessage: String? {
        guard !isPaused, let message, !message.isEmpty else { return nil }
        return message
    }
}

struct MediaImportCounts: Decodable, Hashable {
    var total: Int?
    var waiting: Int?
    var downloading: Int?
    var done: Int?
    var inLibrary: Int?
    var notFound: Int?
    var failed: Int?
    var canceled: Int?
}

struct MediaImportItem: Decodable, Hashable, Identifiable {
    var id: String?
    var artist: String?
    var title: String?
    var year: Int?
    var trackCount: Int?
    // waiting | searching | queued | downloading | seeding | importing | done | inLibrary |
    // notFound | failed | canceled
    var state: String?
    var message: String?
    var progress: Double?
    var downloadId: String?
    var releaseTitle: String?

    var displayTitle: String { (title ?? "").isEmpty ? "Untitled" : title ?? "" }
    var isFailed: Bool { state == "failed" || state == "notFound" }
    var isDone: Bool { state == "done" || state == "inLibrary" }
    // ✕ "leave this one out": only while nothing has been queued for it.
    var canSkip: Bool { ["waiting", "notFound", "failed"].contains(state ?? "") }

    // "42%" while downloading, else the state in words.
    var stateWords: String {
        if state == "downloading", let progress {
            return "\(Int((min(1, max(0, progress)) * 100).rounded()))%"
        }
        return mediaImportStateWords(state)
    }
}

func mediaImportStateWords(_ state: String?) -> String {
    switch state {
    case "waiting": "waiting"
    case "searching": "searching…"
    case "queued": "queued"
    case "downloading": "downloading"
    case "seeding": "sharing back"
    case "importing": "adding to library"
    case "done": "in the library"
    case "inLibrary": "already had it"
    case "notFound": "not found"
    case "failed": "failed"
    case "canceled": "left out"
    default: state ?? ""
    }
}

// The filter buttons over one list's items.
enum MediaImportItemFilter: String, CaseIterable, Identifiable, Hashable {
    case all, active, done, missing

    var id: String { rawValue }

    var title: String {
        switch self {
        case .all: "All"
        case .active: "In Progress"
        case .done: "Done"
        case .missing: "Not Found"
        }
    }

    func matches(_ state: String?) -> Bool {
        let s = state ?? ""
        switch self {
        case .all: return true
        case .active: return ["waiting", "searching", "queued", "downloading", "seeding", "importing"].contains(s)
        case .done: return s == "done" || s == "inLibrary"
        case .missing: return s == "notFound" || s == "failed"
        }
    }
}
