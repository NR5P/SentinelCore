import SwiftUI

// Album, artist and playlist pages: big art on the left with the actions, the songs on the right.

private struct DetailHeader<Art: View, Buttons: View>: View {
    let title: String
    let lines: [String]
    @ViewBuilder let art: Art
    @ViewBuilder let buttons: Buttons

    var body: some View {
        HStack(alignment: .bottom, spacing: 50) {
            art
                .frame(width: 420, height: 420)
                .clipShape(RoundedRectangle(cornerRadius: 16))
                .shadow(color: .black.opacity(0.4), radius: 24, y: 12)
            VStack(alignment: .leading, spacing: 12) {
                Text(title).font(.system(size: 56, weight: .bold)).lineLimit(2).minimumScaleFactor(0.6)
                ForEach(lines.filter { !$0.isEmpty }, id: \.self) { line in
                    Text(line).font(.callout).foregroundStyle(.secondary).lineLimit(2)
                }
                HStack(spacing: 24) { buttons }
                    .padding(.top, 20)
            }
            Spacer(minLength: 0)
        }
    }
}

// MARK: - Album

struct AlbumTVView: View {
    let albumId: String
    @EnvironmentObject private var audio: AudioCenter
    @State private var detail: AlbumDetail?
    @State private var loved: Bool?
    @State private var errorMessage: String?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 40) {
                if let detail {
                    let album = detail.album
                    DetailHeader(title: album.title, lines: [
                        album.artist ?? "",
                        [album.year.map(String.init), album.genres?.first, "\(detail.tracks.count) songs", AudioFormat.minutes(album.durationSeconds)]
                            .compactMap { $0 }.joined(separator: " · "),
                        detail.formats.joined(separator: ", "),
                    ]) {
                        AudioArt(path: album.artUrl ?? "/api/music/albums/\(album.id)/art", title: album.title, size: 840)
                    } buttons: {
                        AudioButton("Play", systemImage: "play.fill") { play(at: 0, shuffle: false) }
                        AudioButton("Shuffle", systemImage: "shuffle") { play(at: 0, shuffle: true) }
                        AudioButton("Radio", systemImage: "dot.radiowaves.left.and.right") {
                            Task { try? await audio.startRadio(seedType: "album", seedId: album.id, name: album.title) }
                        }
                        let isLoved = loved ?? (album.loved == true)
                        Button {
                            Task { loved = try? await audio.api?.love(kind: "albums", id: album.id, loved: !isLoved) }
                        } label: {
                            Image(systemName: isLoved ? "heart.fill" : "heart").audioFocusLabel(isLoved ? SentinelTheme.red : .primary)
                        }
                        .accessibilityLabel(isLoved ? "Unlove" : "Love")
                        if let artistId = album.artistId {
                            NavigationLink(value: AudioRoute.artist(artistId)) {
                                Label("Artist", systemImage: "music.mic").audioFocusLabel()
                            }
                        }
                    }
                    VStack(spacing: 6) {
                        let discs = Set(detail.tracks.compactMap(\.discNumber)).count > 1
                        ForEach(Array(detail.tracks.enumerated()), id: \.element.id) { offset, track in
                            if discs, track.discNumber != detail.tracks[safe: offset - 1]?.discNumber {
                                Text("Disc \(track.discNumber ?? 1)").font(.headline).frame(maxWidth: .infinity, alignment: .leading).padding(.top, 16)
                            }
                            TrackRow(track: track, number: track.trackNumber ?? offset + 1, showArt: false) { play(at: offset, shuffle: false) }
                        }
                    }
                    if !detail.moreByArtist.isEmpty {
                        VStack(alignment: .leading, spacing: 10) {
                            Text("More by \(album.artist ?? "this artist")").font(.title3.bold())
                            ScrollView(.horizontal) {
                                LazyHStack(spacing: 40) { ForEach(detail.moreByArtist) { AlbumTile(album: $0, size: AudioTileSize.small) } }
                                    .padding(.vertical, 20)
                            }
                            .scrollClipDisabled()
                        }
                    }
                }
            }
            .padding(.horizontal, 60)
            .padding(.vertical, 30)
        }
        .scrollClipDisabled()
        .overlay { AudioStateOverlay(loading: detail == nil && errorMessage == nil, error: errorMessage, isEmpty: detail == nil) }
        .task(id: albumId) {
            do { detail = try await audio.api?.album(id: albumId) } catch { errorMessage = error.localizedDescription }
        }
    }

    private func play(at index: Int, shuffle: Bool) {
        guard let detail else { return }
        audio.play(tracks: detail.tracks, startAt: index, shuffle: shuffle,
                   context: PlayContext(type: "album", id: detail.album.id, name: detail.album.title))
    }
}

// MARK: - Artist

struct ArtistTVView: View {
    let artistId: String
    @EnvironmentObject private var audio: AudioCenter
    @State private var detail: ArtistDetail?
    @State private var loved: Bool?
    @State private var errorMessage: String?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 40) {
                if let detail {
                    let artist = detail.artist
                    DetailHeader(title: artist.name, lines: [
                        [artist.albumCount.map { "\($0) albums" }, artist.trackCount.map { "\($0) songs" }].compactMap { $0 }.joined(separator: " · "),
                        (artist.genres ?? []).prefix(3).joined(separator: ", "),
                    ]) {
                        AudioArt(path: artist.artUrl ?? "/api/music/artists/\(artist.id)/art", title: artist.name, size: 840)
                            .clipShape(Circle())
                    } buttons: {
                        AudioButton("Play", systemImage: "play.fill") { Task { await playAll(shuffle: false) } }
                        AudioButton("Shuffle", systemImage: "shuffle") { Task { await playAll(shuffle: true) } }
                        AudioButton("Radio", systemImage: "dot.radiowaves.left.and.right") {
                            Task { try? await audio.startRadio(seedType: "artist", seedId: artist.id, name: artist.name) }
                        }
                        let isLoved = loved ?? (artist.loved == true)
                        Button {
                            Task { loved = try? await audio.api?.love(kind: "artists", id: artist.id, loved: !isLoved) }
                        } label: {
                            Image(systemName: isLoved ? "heart.fill" : "heart").audioFocusLabel(isLoved ? SentinelTheme.red : .primary)
                        }
                        .accessibilityLabel(isLoved ? "Unlove" : "Love")
                    }
                    if !detail.topTracks.isEmpty {
                        Text("Top Songs").font(.title3.bold())
                        VStack(spacing: 6) {
                            ForEach(Array(detail.topTracks.enumerated()), id: \.element.id) { offset, track in
                                TrackRow(track: track, number: offset + 1, showAlbum: true) {
                                    audio.play(tracks: detail.topTracks, startAt: offset, context: PlayContext(type: "artist", id: artist.id, name: artist.name))
                                }
                            }
                        }
                    }
                    shelf("Albums", detail.albums)
                    shelf("Appears On", detail.appearsOn)
                    if !detail.similar.isEmpty {
                        Text("Similar Artists").font(.title3.bold())
                        ScrollView(.horizontal) {
                            LazyHStack(spacing: 40) { ForEach(detail.similar) { ArtistTile(artist: $0) } }.padding(.vertical, 20)
                        }
                        .scrollClipDisabled()
                    }
                }
            }
            .padding(.horizontal, 60)
            .padding(.vertical, 30)
        }
        .scrollClipDisabled()
        .overlay { AudioStateOverlay(loading: detail == nil && errorMessage == nil, error: errorMessage, isEmpty: detail == nil) }
        .task(id: artistId) {
            do { detail = try await audio.api?.artist(id: artistId) } catch { errorMessage = error.localizedDescription }
        }
    }

    @ViewBuilder
    private func shelf(_ title: String, _ albums: [Album]) -> some View {
        if !albums.isEmpty {
            Text(title).font(.title3.bold())
            ScrollView(.horizontal) {
                LazyHStack(spacing: 40) { ForEach(albums) { AlbumTile(album: $0) } }.padding(.vertical, 20)
            }
            .scrollClipDisabled()
        }
    }

    // Every album in order (newest first, as the server lists them).
    private func playAll(shuffle: Bool) async {
        guard let detail, let api = audio.api else { return }
        var tracks: [Track] = []
        for album in detail.albums.reversed() {
            if let full = try? await api.album(id: album.id) { tracks += full.tracks }
        }
        if tracks.isEmpty { tracks = detail.topTracks }
        audio.play(tracks: tracks, shuffle: shuffle, context: PlayContext(type: "artist", id: detail.artist.id, name: detail.artist.name))
    }
}

// MARK: - Playlist

struct PlaylistTVView: View {
    let playlistId: String
    @EnvironmentObject private var audio: AudioCenter
    @Environment(\.dismiss) private var dismiss
    @State private var detail: PlaylistDetail?
    @State private var pinned: Bool?
    @State private var errorMessage: String?
    @State private var confirmingDelete = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 40) {
                if let detail {
                    let playlist = detail.playlist
                    DetailHeader(title: playlist.name, lines: [
                        playlist.description ?? "",
                        [playlist.smart == true ? "Smart playlist" : nil, playlist.mine == false ? "By \(playlist.ownerName ?? "someone")" : nil,
                         playlist.shared == true ? "Shared" : nil, "\(detail.tracks.count) songs", AudioFormat.minutes(playlist.durationSeconds)]
                            .compactMap { $0 }.joined(separator: " · "),
                    ]) {
                        ArtMosaic(paths: playlist.artUrls ?? [], title: playlist.name, size: 840)
                    } buttons: {
                        AudioButton("Play", systemImage: "play.fill") { play(at: 0, shuffle: false) }
                        AudioButton("Shuffle", systemImage: "shuffle") { play(at: 0, shuffle: true) }
                        let isPinned = pinned ?? (playlist.pinned == true)
                        AudioButton(isPinned ? "Unpin" : "Pin to Home", systemImage: isPinned ? "pin.slash" : "pin") {
                            Task {
                                try? await audio.api?.pinPlaylist(id: playlist.id, pinned: !isPinned)
                                pinned = !isPinned
                            }
                        }
                        if playlist.mine == true {
                            AudioButton("Delete", systemImage: "trash", role: .destructive) { confirmingDelete = true }
                        }
                    }
                    if detail.tracks.isEmpty {
                        Text("No songs yet. Long-press any song and choose Add to Playlist.").foregroundStyle(.secondary)
                    }
                    VStack(spacing: 6) {
                        ForEach(Array(detail.tracks.enumerated()), id: \.offset) { offset, track in
                            TrackRow(track: track, number: offset + 1, showAlbum: true) { play(at: offset, shuffle: false) }
                        }
                    }
                }
            }
            .padding(.horizontal, 60)
            .padding(.vertical, 30)
        }
        .scrollClipDisabled()
        .overlay { AudioStateOverlay(loading: detail == nil && errorMessage == nil, error: errorMessage, isEmpty: detail == nil) }
        .confirmationDialog("Delete this playlist?", isPresented: $confirmingDelete, titleVisibility: .visible) {
            Button("Delete Playlist", role: .destructive) {
                Task {
                    try? await audio.api?.deletePlaylist(id: playlistId)
                    dismiss()
                }
            }
        }
        .task(id: playlistId) {
            do { detail = try await audio.api?.playlist(id: playlistId) } catch { errorMessage = error.localizedDescription }
        }
    }

    private func play(at index: Int, shuffle: Bool) {
        guard let detail else { return }
        audio.play(tracks: detail.tracks, startAt: index, shuffle: shuffle,
                   context: PlayContext(type: "playlist", id: detail.playlist.id, name: detail.playlist.name))
    }
}

struct LovedTracksTVView: View {
    @EnvironmentObject private var audio: AudioCenter
    @State private var tracks: [Track] = []
    @State private var loading = true
    @State private var errorMessage: String?

    var body: some View {
        List {
            Section {
                HStack(spacing: 24) {
                    AudioButton("Play", systemImage: "play.fill") { play(0, shuffle: false) }
                    AudioButton("Shuffle", systemImage: "shuffle") { play(0, shuffle: true) }
                    AudioButton("Loved Mix", systemImage: "dot.radiowaves.left.and.right") {
                        Task { try? await audio.playMix(id: "loved", name: "Loved") }
                    }
                }
            }
            Section("\(tracks.count) loved songs") {
                ForEach(Array(tracks.enumerated()), id: \.element.id) { offset, track in
                    TrackRow(track: track, showAlbum: true) { play(offset, shuffle: false) }
                }
            }
        }
        .navigationTitle("Loved Songs")
        .overlay { AudioStateOverlay(loading: loading, error: errorMessage, isEmpty: tracks.isEmpty, emptyTitle: "Nothing loved yet", emptyIcon: "heart") }
        .task {
            defer { loading = false }
            do { tracks = try await audio.api?.tracks(loved: true, sort: "added", order: "desc", limit: 1000).items ?? [] }
            catch { errorMessage = error.localizedDescription }
        }
    }

    private func play(_ index: Int, shuffle: Bool) {
        audio.play(tracks: tracks, startAt: index, shuffle: shuffle, context: PlayContext(type: "playlist", id: "loved", name: "Loved Songs"))
    }
}
