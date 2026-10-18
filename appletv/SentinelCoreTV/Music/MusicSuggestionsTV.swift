import SwiftUI

// "Suggested for You" on the TV's Music Home: albums by artists like the ones this person
// listens to, not in the library yet (core/docs/MUSIC_AND_PODCASTS.md "Suggestions"). Selecting a
// card offers Download or Not Interested - all a remote needs. Only shown when the server says
// canDownload. The iPhone's version is ios/SentinelCore/Music/MusicSuggestionsViews.swift.
struct MusicSuggestionsShelfTV: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var audio: AudioCenter
    @State private var suggestions: MusicSuggestions?
    // Downloaded or dismissed here since the last read said so.
    @State private var queuedHere: Set<String> = []
    @State private var hidden: Set<String> = []
    @State private var hiddenArtists: Set<String> = []
    @State private var acting: MusicSuggestedAlbum?
    @State private var notice: String?
    @State private var actionError: String?

    private var albums: [MusicSuggestedAlbum] {
        (suggestions?.albums ?? []).compactMap { album in
            if hidden.contains(album.id) || hiddenArtists.contains(album.displayArtist.lowercased()) { return nil }
            var shown = album
            if queuedHere.contains(album.id) { shown.queued = true }
            return shown
        }
    }

    var body: some View {
        let albums = albums
        VStack(alignment: .leading, spacing: 0) {
            // Never empty: an empty stack never appears, so its .task below would never load.
            Color.clear.frame(height: 0)
            if !albums.isEmpty || suggestions?.isWorking == true {
                MediaShelfTV(title: "Suggested for You") {
                    if albums.isEmpty {
                        ProgressView().frame(width: AudioTileSize.square, height: AudioTileSize.square)
                    }
                    ForEach(albums) { album in
                        AudioSquareTile(title: album.displayTitle,
                                        subtitle: album.queued == true ? "On its way" : album.displayArtist,
                                        action: { acting = album }) {
                            cover(album)
                        }
                    }
                }
                if let line = notice ?? suggestions?.basedOnLine {
                    Text(line)
                        .font(.caption)
                        .foregroundStyle(notice == nil ? AnyShapeStyle(.secondary) : AnyShapeStyle(SentinelTheme.green))
                        .padding(.horizontal, 60)
                }
            }
        }
        .task(id: model.activeServerURL) {
            suggestions = nil
            queuedHere = []
            hidden = []
            hiddenArtists = []
            await load()
            while suggestions?.isWorking == true, !Task.isCancelled {
                try? await Task.sleep(for: .seconds(3))
                await load()
            }
        }
        .confirmationDialog(
            acting.map { "\($0.displayTitle) · \($0.displayArtist)" } ?? "Suggestion",
            isPresented: Binding(get: { acting != nil }, set: { if !$0 { acting = nil } }),
            titleVisibility: .visible,
            presenting: acting
        ) { album in
            if album.queued != true {
                Button("Download") { Task { await download(album) } }
            }
            Button("Not Interested") { Task { await dismiss(album, wholeArtist: false) } }
            if !album.displayArtist.isEmpty {
                Button("Not Interested in \(album.displayArtist)") { Task { await dismiss(album, wholeArtist: true) } }
            }
            Button("Cancel", role: .cancel) {}
        } message: { album in
            Text(album.queued == true ? "On its way: it shows under + Add → Imported Lists."
                 : album.becauseLine ?? "An album you don't have yet.")
        }
        .alert("Suggestions", isPresented: Binding(get: { actionError != nil }, set: { if !$0 { actionError = nil } })) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(actionError ?? "")
        }
    }

    private func cover(_ album: MusicSuggestedAlbum) -> some View {
        AsyncImage(url: album.artUrl.flatMap(URL.init(string:))) { phase in
            if let image = phase.image {
                image.resizable().scaledToFill()
            } else {
                ZStack {
                    Rectangle().fill(.quaternary)
                    Image(systemName: "opticaldisc").font(.largeTitle).foregroundStyle(.secondary)
                }
            }
        }
        .frame(width: AudioTileSize.square, height: AudioTileSize.square)
        .clipped()
    }

    private func load() async {
        guard let api = audio.api else { return }
        do {
            let fresh = try await api.musicSuggestions()
            suggestions = fresh
            let ids = Set((fresh.albums ?? []).map(\.id))
            queuedHere = queuedHere.intersection(ids)
            hidden = hidden.intersection(ids)
        } catch {
            // A failed read leaves the row as it was; it is a suggestion, not something to report.
        }
    }

    private func download(_ album: MusicSuggestedAlbum) async {
        guard let api = audio.api else { return }
        do {
            _ = try await api.downloadMusicSuggestions([album])
            queuedHere.insert(album.id)
            notice = "Downloading \(album.displayTitle). It shows under + Add → Imported Lists."
        } catch {
            if !AudioAPI.isCancellation(error) { actionError = error.localizedDescription }
        }
    }

    private func dismiss(_ album: MusicSuggestedAlbum, wholeArtist: Bool) async {
        guard let api = audio.api, let artist = album.artist, !artist.isEmpty else { return }
        do {
            try await api.dismissMusicSuggestion(artist: artist, title: wholeArtist ? nil : album.title)
            if wholeArtist { hiddenArtists.insert(artist.lowercased()) } else { hidden.insert(album.id) }
        } catch {
            if !AudioAPI.isCancellation(error) { actionError = error.localizedDescription }
        }
    }
}
