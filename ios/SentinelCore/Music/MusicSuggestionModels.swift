import Foundation

// "Suggested for you" on Music - the wire shapes of GET /api/music/downloads/suggestions and
// POST …/suggestions/dismiss, as core/docs/MUSIC_AND_PODCASTS.md "Suggestions" describes them
// (server: core/SentinelCore.Web/MusicSuggestions.cs). Same `canDownload` gate as "+ Add".
// Downloading one is an imported list (MediaImportModels.swift). Every field the server could
// leave out or send as null is optional.
//
// Twin of appletv/SentinelCoreTV/Music/MusicSuggestionModels.swift - keep the two identical.

struct MusicSuggestions: Decodable {
    // ready | working | error. While working, show what `albums` holds and read again.
    var state: String?
    var builtAt: String?
    var message: String?
    var seeds: [MusicSuggestionSeed]?
    var albums: [MusicSuggestedAlbum]?

    var isWorking: Bool { state == "working" }
    var isError: Bool { state == "error" }

    // "Based on: Fleetwood Mac, Eagles, Steely Dan and 4 more"
    var basedOnLine: String? {
        let names = (seeds ?? []).compactMap(\.artist).filter { !$0.isEmpty }
        guard !names.isEmpty else { return nil }
        let shown = names.prefix(5)
        let more = names.count - shown.count
        return "Based on: " + shown.joined(separator: ", ") + (more > 0 ? " and \(more) more" : "")
    }
}

struct MusicSuggestionSeed: Decodable, Hashable {
    var artist: String?
    // loved | rated | played | playlists | imported | library
    var reason: String?
}

struct MusicSuggestedAlbum: Decodable, Hashable, Identifiable {
    var key: String?
    var artist: String?
    var title: String?
    var year: Int?
    var mbid: String?
    var artUrl: String?
    var because: [String]?
    var listens: Int64?
    // Already on one of this person's imported lists, or downloading.
    var queued: Bool?

    var id: String { key ?? "\((artist ?? "").lowercased())|\((title ?? "").lowercased())" }
    var displayTitle: String { (title ?? "").isEmpty ? "Untitled" : title ?? "" }
    var displayArtist: String { artist ?? "" }

    // "Because you like Fleetwood Mac", "… Fleetwood Mac and Eagles", "… A, B and C"
    var becauseLine: String? {
        let names = (because ?? []).filter { !$0.isEmpty }
        guard let last = names.last else { return nil }
        if names.count == 1 { return "Because you like \(last)" }
        return "Because you like \(names.dropLast().joined(separator: ", ")) and \(last)"
    }

    var importItem: MediaImportStartItem {
        MediaImportStartItem(artist: artist, title: title ?? "", year: year, trackCount: 0)
    }
}

// POST /api/music/downloads/suggestions/dismiss. `title` nil = the whole artist; `dismissed`
// false takes it back.
struct MusicSuggestionDismissInput: Encodable {
    var artist: String
    var title: String?
    var dismissed: Bool = true
}

// What "Download" sends: an imported list called "Suggested for you".
func musicSuggestionImport(_ albums: [MusicSuggestedAlbum]) -> MediaImportStartInput {
    MediaImportStartInput(name: "Suggested for you", libraryId: nil,
                          items: albums.filter { !($0.title ?? "").isEmpty }.map(\.importItem))
}
