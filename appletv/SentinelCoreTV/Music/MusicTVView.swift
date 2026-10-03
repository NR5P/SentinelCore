import SwiftUI

// Music on the television - the household's own music files (core/docs/MUSIC_AND_PODCASTS.md §2-4):
// Home's rows, the library by artist/album/song/genre/playlist, search, and "+ Add" for albums when
// the server allows downloads. Everything plays through the shared AudioPlayer.
struct MusicTVView: View {
    enum Pane: String, CaseIterable, Identifiable {
        case home, library, search, add
        var id: Self { self }
        var title: String {
            switch self {
            case .home: "Home"
            case .library: "Library"
            case .search: "Search"
            case .add: "+ Add"
            }
        }
    }

    let isActive: Bool
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var audio: AudioCenter
    @State private var pane: Pane = .home
    @State private var canDownload = false
    @State private var refreshToken = 0

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            HStack(spacing: 40) {
                Picker("Show", selection: $pane) {
                    ForEach(Pane.allCases.filter { $0 != .add || canDownload }) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                .frame(maxWidth: 900)
                Spacer()
            }
            .padding(.horizontal, 60)

            AudioTopBar()

            switch pane {
            case .home: MusicHomeTV(isActive: isActive, refreshToken: refreshToken)
            case .library: MusicLibraryTV()
            case .search: MusicSearchTV()
            case .add: MusicAddTVView()
            }
        }
        .modifier(AudioTabRoot(isActive: isActive))
        .task(id: "\(isActive)|\(model.activeServerURL)") {
            guard isActive, let api = audio.api else { return }
            canDownload = (try? await api.musicDownloadsStatus())?.canDownload == true
            await audio.refreshContinueOffer()
        }
        #if DEBUG
        // Simulator testing without a remote: `-debugMusicPane library|search|add`,
        // `-debugMusicAlbum <id>` / `-debugMusicArtist <id>` / `-debugMusicRoute stats|alarms|speakers`
        // open a page, `-debugMusicPlayAlbum <id>` plays an album and opens Now Playing.
        .task(id: model.activeServerURL) {
            guard !model.activeServerURL.isEmpty else { return }
            while audio.api == nil { try? await Task.sleep(for: .milliseconds(300)) }
            let defaults = UserDefaults.standard
            if let raw = defaults.string(forKey: "debugMusicPane"), let picked = Pane(rawValue: raw) {
                if picked == .add { canDownload = true }
                pane = picked
            }
            try? await Task.sleep(for: .milliseconds(600))
            if let id = defaults.string(forKey: "debugMusicAlbum") { AudioNavigator.shared.open(.album(id)) }
            if let id = defaults.string(forKey: "debugMusicArtist") { AudioNavigator.shared.open(.artist(id)) }
            switch defaults.string(forKey: "debugMusicRoute") {
            case "stats": AudioNavigator.shared.open(.stats)
            case "alarms": AudioNavigator.shared.open(.alarms)
            case "speakers": AudioNavigator.shared.open(.speakers)
            default: break
            }
            if let id = defaults.string(forKey: "debugMusicPlayAlbum"), let detail = try? await audio.api?.album(id: id) {
                audio.play(tracks: detail.tracks, context: PlayContext(type: "album", id: id, name: detail.album.title))
            }
        }
        #endif
    }
}

// MARK: - Home

private struct MusicHomeTV: View {
    let isActive: Bool
    let refreshToken: Int
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var audio: AudioCenter
    @State private var home: MusicHome?
    @State private var loading = false
    @State private var errorMessage: String?
    @State private var actionError: String?

    var body: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 46) {
                actions
                if let home {
                    if let pinned = home.pinned, !pinned.isEmpty {
                        MediaShelfTV(title: "Pinned") { ForEach(pinned) { PlaylistTile(playlist: $0) } }
                    }
                    if let recent = home.recentlyPlayed, !recent.isEmpty {
                        MediaShelfTV(title: "Recently Played") {
                            ForEach(Array(recent.enumerated()), id: \.offset) { _, entry in recentTile(entry) }
                        }
                    }
                    if let mixes = home.mixes, !mixes.isEmpty {
                        MediaShelfTV(title: "Made for You") {
                            ForEach(mixes) { mix in MixTile(mix: mix) { playMix(mix) } }
                        }
                    }
                    albumShelf("Recently Added", home.recentlyAdded)
                    albumShelf("Heavy Rotation", home.heavyRotation)
                    if let day = home.onThisDay, let tracks = day.tracks, !tracks.isEmpty {
                        trackShelf("On This Day · \(day.label ?? "")", tracks, context: PlayContext(type: "queue", id: nil, name: "On This Day"))
                    }
                    trackShelf("Forgotten Favorites", home.forgottenFavorites, context: PlayContext(type: "queue", id: nil, name: "Forgotten Favorites"))
                    albumShelf("Discover", home.discover)
                    if let loved = home.lovedTracks, !loved.isEmpty {
                        trackShelf("Loved", loved, context: PlayContext(type: "queue", id: "loved", name: "Loved"), more: .lovedTracks)
                    }
                    if let decades = home.decades, !decades.isEmpty {
                        MediaShelfTV(title: "Decades") {
                            ForEach(decades) { decade in
                                AudioSquareTile(title: decade.title, subtitle: decade.trackCount.map { "\($0) songs" },
                                                route: .decade(decade.decade, decade.title), size: AudioTileSize.small) {
                                    DecadeArt(label: decade.title, decade: decade.decade)
                                }
                            }
                        }
                    }
                    if let tracks = home.trackCount, tracks > 0 {
                        Text("\(tracks) songs · \(home.albumCount ?? 0) albums · \(home.artistCount ?? 0) artists")
                            .font(.caption).foregroundStyle(.secondary).padding(.horizontal, 60)
                    }
                }
            }
            .padding(.vertical, 20)
        }
        .scrollClipDisabled()
        .overlay {
            AudioStateOverlay(loading: loading, error: errorMessage, isEmpty: home == nil || home?.isEmpty == true,
                              emptyTitle: "No music yet", emptyIcon: "music.note.house",
                              emptyMessage: "An admin adds the music folder in Settings → Music Libraries. The library fills in as the server scans it.")
        }
        .alert("Music", isPresented: Binding(get: { actionError != nil }, set: { if !$0 { actionError = nil } })) {
            Button("OK", role: .cancel) {}
        } message: { Text(actionError ?? "") }
        .task(id: "\(isActive)|\(refreshToken)|\(model.activeServerURL)") {
            guard isActive else { return }
            await load()
        }
    }

    private var actions: some View {
        HStack(spacing: 30) {
            AudioButton("Shuffle All", systemImage: "shuffle") { run { try await audio.shuffleAll() } }
            AudioButton("Play On…", systemImage: "hifispeaker.2") { AudioNavigator.shared.open(.speakers) }
            AudioButton("Your Stats", systemImage: "chart.bar.xaxis") { AudioNavigator.shared.open(.stats) }
            AudioButton("Wake-up Alarms", systemImage: "alarm") { AudioNavigator.shared.open(.alarms) }
        }
        .padding(.horizontal, 60)
    }

    @ViewBuilder
    private func recentTile(_ entry: RecentlyPlayedEntry) -> some View {
        if let album = entry.album {
            AlbumTile(album: album)
        } else if let playlist = entry.playlist {
            PlaylistTile(playlist: playlist)
        } else if let artist = entry.artist {
            ArtistTile(artist: artist, size: AudioTileSize.square)
        } else if let mix = entry.mix {
            MixTile(mix: mix) { playMix(mix) }
        }
    }

    @ViewBuilder
    private func albumShelf(_ title: String, _ albums: [Album]?) -> some View {
        if let albums, !albums.isEmpty {
            MediaShelfTV(title: title) { ForEach(albums) { AlbumTile(album: $0) } }
        }
    }

    @ViewBuilder
    private func trackShelf(_ title: String, _ tracks: [Track]?, context: PlayContext, more: AudioRoute? = nil) -> some View {
        if let tracks, !tracks.isEmpty {
            MediaShelfTV(title: title) {
                ForEach(Array(tracks.enumerated()), id: \.element.id) { offset, track in
                    AudioSquareTile(title: track.title, subtitle: track.artistLine, size: AudioTileSize.small,
                                    action: { audio.play(tracks: tracks, startAt: offset, context: context) }) {
                        AudioArt(path: track.artUrl, title: track.album ?? track.title, size: 400)
                    }
                    .contextMenu { TrackMenu(track: track, loved: .constant(nil)) }
                }
                if let more {
                    AudioSquareTile(title: "See All", size: AudioTileSize.small, action: { AudioNavigator.shared.open(more) }) {
                        ZStack { Color.gray.opacity(0.25); Image(systemName: "ellipsis").font(.largeTitle) }
                    }
                }
            }
        }
    }

    private func playMix(_ mix: Mix) {
        run { try await audio.playMix(id: mix.id, name: mix.name) }
    }

    private func run(_ work: @escaping () async throws -> Void) {
        Task {
            do { try await work() } catch {
                if !AudioAPI.isCancellation(error) { actionError = error.localizedDescription }
            }
        }
    }

    private func load() async {
        guard let api = audio.api else { return }
        loading = true
        defer { loading = false }
        do {
            home = try await api.musicHome()
            errorMessage = nil
        } catch {
            if AudioAPI.isCancellation(error) { return }
            errorMessage = error.localizedDescription
        }
    }
}

// A decade tile with no artwork of its own: the label on a colour picked from the decade.
struct DecadeArt: View {
    let label: String
    let decade: Int

    var body: some View {
        let hue = Double((decade / 10) % 12) / 12
        ZStack {
            LinearGradient(colors: [Color(hue: hue, saturation: 0.7, brightness: 0.75), Color(hue: hue + 0.08, saturation: 0.8, brightness: 0.35)],
                           startPoint: .topLeading, endPoint: .bottomTrailing)
            Text(label).font(.system(size: 64, weight: .heavy)).foregroundStyle(.white)
        }
    }
}

// MARK: - Library

private struct MusicLibraryTV: View {
    enum Kind: String, CaseIterable, Identifiable {
        case artists, albums, songs, genres, playlists
        var id: Self { self }
        var title: String { rawValue.capitalized }
    }

    @State private var kind: Kind = .albums

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Picker("Browse", selection: $kind) {
                ForEach(Kind.allCases) { Text($0.title).tag($0) }
            }
            .pickerStyle(.segmented)
            .frame(maxWidth: 1100)
            .padding(.horizontal, 60)
            switch kind {
            case .artists: ArtistGridTV()
            case .albums: AlbumGridTVView(title: nil, genre: nil, decade: nil)
            case .songs: SongListTV()
            case .genres: GenreGridTV()
            case .playlists: PlaylistGridTV()
            }
        }
    }
}

private struct ArtistGridTV: View {
    @EnvironmentObject private var audio: AudioCenter
    @State private var artists: [Artist] = []
    @State private var total = 0
    @State private var sort = "name"
    @State private var loading = false
    @State private var errorMessage: String?
    private let columns = [GridItem(.adaptive(minimum: AudioTileSize.small, maximum: AudioTileSize.small + 30), spacing: 40)]

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                HStack {
                    Menu {
                        Picker("Sort", selection: $sort) {
                            Text("Name").tag("name")
                            Text("Most Played").tag("plays")
                            Text("Recently Added").tag("added")
                        }
                    } label: {
                        Label(sort == "plays" ? "Most Played" : (sort == "added" ? "Recently Added" : "Name"), systemImage: "arrow.up.arrow.down")
                            .audioFocusLabel()
                    }
                    Spacer()
                    if total > 0 { Text("\(total) artists").font(.caption).foregroundStyle(.secondary) }
                }
                LazyVGrid(columns: columns, alignment: .leading, spacing: 46) {
                    ForEach(artists) { artist in
                        ArtistTile(artist: artist)
                            .onAppear { if artist.id == artists.last?.id { Task { await more() } } }
                    }
                }
            }
            .padding(.horizontal, 60)
            .padding(.vertical, 20)
        }
        .scrollClipDisabled()
        .overlay { AudioStateOverlay(loading: loading, error: errorMessage, isEmpty: artists.isEmpty, emptyTitle: "No artists yet") }
        .task(id: sort) { await reload() }
    }

    private func reload() async {
        guard let api = audio.api else { return }
        loading = true
        defer { loading = false }
        do {
            let page = try await api.artists(sort: sort, limit: 200)
            artists = page.items
            total = page.total ?? page.items.count
            errorMessage = nil
        } catch {
            if !AudioAPI.isCancellation(error) { errorMessage = error.localizedDescription }
        }
    }

    private func more() async {
        guard let api = audio.api, !loading, artists.count < total else { return }
        loading = true
        defer { loading = false }
        if let page = try? await api.artists(sort: sort, offset: artists.count, limit: 200) {
            let known = Set(artists.map(\.id))
            artists += page.items.filter { !known.contains($0.id) }
        }
    }
}

// Albums: the whole library, or one genre or decade.
struct AlbumGridTVView: View {
    let title: String?
    let genre: String?
    let decade: Int?
    @EnvironmentObject private var audio: AudioCenter
    @State private var albums: [Album] = []
    @State private var total = 0
    @State private var sort = "title"
    @State private var loading = false
    @State private var errorMessage: String?
    private let columns = [GridItem(.adaptive(minimum: AudioTileSize.square, maximum: AudioTileSize.square + 30), spacing: 40)]
    private static let sorts: [(String, String)] = [("title", "Title"), ("artist", "Artist"), ("year", "Year"), ("added", "Recently Added"), ("plays", "Most Played"), ("rating", "Rating")]

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                if let title {
                    Text(title).font(.largeTitle.bold())
                }
                HStack(spacing: 30) {
                    Menu {
                        Picker("Sort", selection: $sort) {
                            ForEach(Self.sorts, id: \.0) { Text($0.1).tag($0.0) }
                        }
                    } label: {
                        Label(Self.sorts.first { $0.0 == sort }?.1 ?? "Title", systemImage: "arrow.up.arrow.down").audioFocusLabel()
                    }
                    if genre != nil || decade != nil {
                        AudioButton("Play", systemImage: "play.fill") { Task { await playAll(shuffle: false) } }
                        AudioButton("Shuffle", systemImage: "shuffle") { Task { await playAll(shuffle: true) } }
                        if let genre {
                            AudioButton("\(genre) Radio", systemImage: "dot.radiowaves.left.and.right") {
                                Task { try? await audio.startRadio(seedType: "genre", seedId: genre, name: genre) }
                            }
                        }
                        if let decade {
                            AudioButton("Decade Mix", systemImage: "dot.radiowaves.left.and.right") {
                                Task { try? await audio.playMix(id: "decade-\(decade)", name: title ?? "Decade") }
                            }
                        }
                    }
                    Spacer()
                    if total > 0 { Text("\(total) albums").font(.caption).foregroundStyle(.secondary) }
                }
                LazyVGrid(columns: columns, alignment: .leading, spacing: 46) {
                    ForEach(albums) { album in
                        AlbumTile(album: album)
                            .onAppear { if album.id == albums.last?.id { Task { await more() } } }
                    }
                }
            }
            .padding(.horizontal, 60)
            .padding(.vertical, 20)
        }
        .scrollClipDisabled()
        .overlay { AudioStateOverlay(loading: loading, error: errorMessage, isEmpty: albums.isEmpty, emptyTitle: "No albums", emptyIcon: "square.stack") }
        .task(id: sort) { await reload() }
    }

    private func reload() async {
        guard let api = audio.api else { return }
        loading = true
        defer { loading = false }
        do {
            let page = try await api.albums(genre: genre, decade: decade, sort: sort, order: sort == "added" || sort == "plays" || sort == "rating" ? "desc" : nil)
            albums = page.items
            total = page.total ?? page.items.count
            errorMessage = nil
        } catch {
            if !AudioAPI.isCancellation(error) { errorMessage = error.localizedDescription }
        }
    }

    private func more() async {
        guard let api = audio.api, !loading, albums.count < total else { return }
        loading = true
        defer { loading = false }
        if let page = try? await api.albums(genre: genre, decade: decade, sort: sort, offset: albums.count) {
            let known = Set(albums.map(\.id))
            albums += page.items.filter { !known.contains($0.id) }
        }
    }

    private func playAll(shuffle: Bool) async {
        guard let api = audio.api, let page = try? await api.tracks(genre: genre, decade: decade, sort: "album", limit: 500) else { return }
        audio.play(tracks: page.items, shuffle: shuffle, context: PlayContext(type: "queue", id: genre ?? decade.map(String.init), name: title))
    }
}

private struct SongListTV: View {
    @EnvironmentObject private var audio: AudioCenter
    @State private var tracks: [Track] = []
    @State private var total = 0
    @State private var sort = "title"
    @State private var loading = false
    @State private var errorMessage: String?
    private static let sorts: [(String, String)] = [("title", "Title"), ("artist", "Artist"), ("album", "Album"), ("added", "Recently Added"), ("plays", "Most Played"), ("lastPlayed", "Recently Played"), ("rating", "Rating")]

    var body: some View {
        List {
            Section {
                HStack(spacing: 30) {
                    Menu {
                        Picker("Sort", selection: $sort) { ForEach(Self.sorts, id: \.0) { Text($0.1).tag($0.0) } }
                    } label: {
                        Label(Self.sorts.first { $0.0 == sort }?.1 ?? "Title", systemImage: "arrow.up.arrow.down")
                    }
                    Spacer()
                    if total > 0 { Text("\(total) songs").font(.caption).foregroundStyle(.secondary) }
                }
            }
            Section {
                ForEach(Array(tracks.enumerated()), id: \.element.id) { offset, track in
                    TrackRow(track: track, showAlbum: true) {
                        audio.play(tracks: tracks, startAt: offset, context: PlayContext(type: "queue", id: nil, name: "Songs"))
                    }
                    .onAppear { if track.id == tracks.last?.id { Task { await more() } } }
                }
            }
        }
        .overlay { AudioStateOverlay(loading: loading, error: errorMessage, isEmpty: tracks.isEmpty, emptyTitle: "No songs") }
        .task(id: sort) { await reload() }
    }

    private var order: String? { ["added", "plays", "lastPlayed", "rating"].contains(sort) ? "desc" : nil }

    private func reload() async {
        guard let api = audio.api else { return }
        loading = true
        defer { loading = false }
        do {
            let page = try await api.tracks(sort: sort, order: order)
            tracks = page.items
            total = page.total ?? page.items.count
            errorMessage = nil
        } catch {
            if !AudioAPI.isCancellation(error) { errorMessage = error.localizedDescription }
        }
    }

    private func more() async {
        guard let api = audio.api, !loading, tracks.count < total else { return }
        loading = true
        defer { loading = false }
        if let page = try? await api.tracks(sort: sort, order: order, offset: tracks.count) {
            let known = Set(tracks.map(\.id))
            tracks += page.items.filter { !known.contains($0.id) }
        }
    }
}

private struct GenreGridTV: View {
    @EnvironmentObject private var audio: AudioCenter
    @State private var genres: [MusicGenre] = []
    @State private var loading = false
    @State private var errorMessage: String?
    private let columns = [GridItem(.adaptive(minimum: AudioTileSize.square, maximum: AudioTileSize.square + 30), spacing: 40)]

    var body: some View {
        ScrollView {
            LazyVGrid(columns: columns, alignment: .leading, spacing: 46) {
                ForEach(genres) { genre in
                    AudioSquareTile(title: genre.name, subtitle: genre.albumCount.map { "\($0) albums" }, route: .genre(genre.name)) {
                        ZStack(alignment: .bottomLeading) {
                            ArtMosaic(paths: genre.artUrls ?? [], title: genre.name)
                            LinearGradient(colors: [.black.opacity(0.8), .clear], startPoint: .bottom, endPoint: .center)
                            Text(genre.name).font(.title3.bold()).foregroundStyle(.white).padding(14)
                        }
                    }
                }
            }
            .padding(.horizontal, 60)
            .padding(.vertical, 20)
        }
        .scrollClipDisabled()
        .overlay { AudioStateOverlay(loading: loading, error: errorMessage, isEmpty: genres.isEmpty, emptyTitle: "No genres") }
        .task {
            guard let api = audio.api else { return }
            loading = true
            defer { loading = false }
            do { genres = try await api.genres() } catch { errorMessage = error.localizedDescription }
        }
    }
}

private struct PlaylistGridTV: View {
    @EnvironmentObject private var audio: AudioCenter
    @ObservedObject private var prompt = NewPlaylistPrompt.shared
    @State private var playlists: [Playlist] = []
    @State private var loading = false
    @State private var errorMessage: String?
    private let columns = [GridItem(.adaptive(minimum: AudioTileSize.square, maximum: AudioTileSize.square + 30), spacing: 40)]

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                HStack {
                    AudioButton("New Playlist", systemImage: "plus") { prompt.ask(trackIds: []) }
                    Spacer()
                }
                LazyVGrid(columns: columns, alignment: .leading, spacing: 46) {
                    AudioSquareTile(title: "Loved Songs", subtitle: "Everything you've loved", route: .lovedTracks) {
                        ZStack {
                            LinearGradient(colors: [SentinelTheme.red, .purple], startPoint: .topLeading, endPoint: .bottomTrailing)
                            Image(systemName: "heart.fill").font(.system(size: 90)).foregroundStyle(.white)
                        }
                    }
                    ForEach(playlists) { PlaylistTile(playlist: $0) }
                }
            }
            .padding(.horizontal, 60)
            .padding(.vertical, 20)
        }
        .scrollClipDisabled()
        .task(id: prompt.lastCreated) {
            guard let api = audio.api else { return }
            loading = true
            defer { loading = false }
            do { playlists = try await api.playlists() } catch { errorMessage = error.localizedDescription }
        }
    }
}

// MARK: - Search

private struct MusicSearchTV: View {
    @EnvironmentObject private var audio: AudioCenter
    @State private var query = ""
    @State private var result: MusicSearchResult?
    @State private var searching = false

    var body: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 40) {
                if let artists = result?.artists, !artists.isEmpty {
                    MediaShelfTV(title: "Artists") { ForEach(artists) { ArtistTile(artist: $0) } }
                }
                if let albums = result?.albums, !albums.isEmpty {
                    MediaShelfTV(title: "Albums") { ForEach(albums) { AlbumTile(album: $0) } }
                }
                if let playlists = result?.playlists, !playlists.isEmpty {
                    MediaShelfTV(title: "Playlists") { ForEach(playlists) { PlaylistTile(playlist: $0) } }
                }
                if let genres = result?.genres, !genres.isEmpty {
                    MediaShelfTV(title: "Genres") {
                        ForEach(genres, id: \.self) { genre in
                            NavigationLink(value: AudioRoute.genre(genre)) { Text(genre).audioFocusLabel() }
                        }
                    }
                }
                if let tracks = result?.tracks, !tracks.isEmpty {
                    AudioSectionHeader(title: "Songs")
                    VStack(spacing: 8) {
                        ForEach(Array(tracks.enumerated()), id: \.element.id) { offset, track in
                            TrackRow(track: track, showAlbum: true) {
                                audio.play(tracks: tracks, startAt: offset, context: PlayContext(type: "search", id: query, name: "Search"))
                            }
                        }
                    }
                    .padding(.horizontal, 60)
                }
            }
            .padding(.vertical, 20)
        }
        .scrollClipDisabled()
        .searchable(text: $query, prompt: "Artists, albums, songs")
        .overlay {
            let trimmed = query.trimmingCharacters(in: .whitespaces)
            if searching, result == nil {
                ProgressView()
            } else if !trimmed.isEmpty, result?.isEmpty == true {
                ContentUnavailableView.search(text: trimmed)
            }
        }
        .task(id: query) {
            let trimmed = query.trimmingCharacters(in: .whitespaces)
            guard !trimmed.isEmpty else { result = nil; return }
            try? await Task.sleep(for: .milliseconds(350))
            guard !Task.isCancelled, let api = audio.api else { return }
            searching = true
            defer { searching = false }
            if let found = try? await api.search(trimmed) { result = found }
        }
    }
}
