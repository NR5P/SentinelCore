import SwiftUI

// Library: the whole collection by artist, album, song, genre, playlist and folder (§4 Browse),
// each list sortable and filterable the ways the server can sort and filter it, loaded a page at a
// time.

struct MusicLibraryView: View {
    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
    private var downloads: AudioDownloads { AudioDownloads.shared }
    @State private var recent: [MusicAlbum] = []

    var body: some View {
        List {
            Section {
                link("Playlists", "music.note.list") { MusicPlaylistsView() }
                link("Artists", "music.mic") { MusicArtistsView() }
                link("Albums", "square.stack") { MusicAlbumsView() }
                link("Songs", "music.note") { MusicSongsView() }
                link("Genres", "guitars") { MusicGenresView() }
                link("Folders", "folder") { MusicFolderView(libraryId: nil, path: nil, title: "Folders") }
                link("Loved", "heart") { MusicSongsView(lovedOnly: true) }
                link("Downloaded", "arrow.down.circle") { AudioDownloadsView() }
            }
            if !recent.isEmpty {
                Section("Recently Added") {
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: horizontalSizeClass == .compact ? 140 : 170), spacing: 16, alignment: .top)], spacing: 18) {
                        ForEach(recent) { AlbumTile(album: $0, width: nil) }
                    }
                    .padding(.vertical, 6)
                }
            }
        }
        .task {
            recent = (try? await AppModel.shared.client.musicAlbums(sort: "added", order: "desc", limit: 24).items) ?? []
        }
    }

    private func link<Destination: View>(_ title: String, _ icon: String, @ViewBuilder _ destination: @escaping () -> Destination) -> some View {
        NavigationLink(destination: destination) {
            Label(title, systemImage: icon)
        }
    }
}

// A paged list loader: the next page is fetched when the last row appears.
@MainActor
@Observable
final class PagedList<T: Identifiable & Decodable & Sendable> {
    private(set) var items: [T] = []
    private(set) var total: Int?
    private(set) var loading = false
    private(set) var error: String?
    @ObservationIgnored private var generation = 0
    @ObservationIgnored var fetch: ((Int) async throws -> AudioPage<T>)?

    var hasMore: Bool { total.map { items.count < $0 } ?? false }

    func reload() async {
        generation += 1
        let mine = generation
        loading = true
        defer { if mine == generation { loading = false } }
        do {
            guard let fetch else { return }
            let page = try await fetch(0)
            guard mine == generation else { return }
            items = page.items
            total = page.total ?? page.items.count
            error = nil
        } catch {
            if !error.isCancellation, mine == generation { self.error = error.localizedDescription }
        }
    }

    func loadMoreIfNeeded(_ item: T) async {
        guard hasMore, !loading, item.id == items.last?.id, let fetch else { return }
        let mine = generation
        loading = true
        defer { loading = false }
        if let page = try? await fetch(items.count), mine == generation {
            items += page.items
            total = page.total ?? total
        }
    }
}

// MARK: - Artists

struct MusicArtistsView: View {
    @State private var list = PagedList<MusicArtist>()
    @State private var query = ""
    @AppStorage("music.artists.sort") private var sort = "name"
    @AppStorage("music.artists.albumArtistsOnly") private var albumArtistsOnly = true

    var body: some View {
        List {
            ForEach(list.items) { artist in
                NavigationLink {
                    MusicArtistView(artistId: artist.id, initial: artist)
                } label: {
                    HStack(spacing: 12) {
                        ArtworkImage(path: artist.artUrl, size: 48, placeholder: "music.mic", circle: true)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(artist.name)
                            Text("\(artist.albumCount ?? 0) album\(artist.albumCount == 1 ? "" : "s") · \(artist.trackCount ?? 0) songs")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        if MusicState.shared.isLoved(artist: artist) { Image(systemName: "heart.fill").font(.caption).foregroundStyle(.pink) }
                    }
                }
                .contextMenu {
                    Button("Start Radio", systemImage: "dot.radiowaves.left.and.right") {
                        MusicActions.startRadio(seedType: "artist", seedId: artist.id, name: artist.name)
                    }
                }
                .task { await list.loadMoreIfNeeded(artist) }
            }
        }
        .overlay { if list.items.isEmpty { AudioLoadState(loading: list.loading, error: list.error, empty: list.loading ? nil : ("No artists", "music.mic", "")) } }
        .searchable(text: $query, prompt: "Filter artists")
        .navigationTitle("Artists")
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Menu {
                    Picker("Sort", selection: $sort) {
                        Text("Name").tag("name")
                        Text("Most Played").tag("plays")
                        Text("Recently Added").tag("added")
                    }
                    Toggle("Album Artists Only", isOn: $albumArtistsOnly)
                } label: {
                    Label("Sort", systemImage: "arrow.up.arrow.down")
                }
            }
        }
        .task(id: "\(query)|\(sort)|\(albumArtistsOnly)") {
            try? await Task.sleep(for: .milliseconds(query.isEmpty ? 0 : 250))
            let q = query, sort = sort, only = albumArtistsOnly
            list.fetch = { offset in
                try await AppModel.shared.client.musicArtists(q: q, sort: sort, order: sort == "name" ? "asc" : "desc",
                                                              albumArtistsOnly: only, offset: offset)
            }
            await list.reload()
        }
    }
}

// MARK: - Albums

struct MusicAlbumsView: View {
    var initialSort: String? = nil
    var genre: String? = nil
    var decade: Int? = nil
    var title = "Albums"

    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
    @State private var list = PagedList<MusicAlbum>()
    @State private var query = ""
    @State private var sort = "title"
    @State private var started = false

    var body: some View {
        ScrollView {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: horizontalSizeClass == .compact ? 150 : 170), spacing: 16, alignment: .top)], spacing: 20) {
                ForEach(list.items) { album in
                    AlbumTile(album: album, width: nil)
                        .task { await list.loadMoreIfNeeded(album) }
                }
            }
            .padding()
        }
        .overlay { if list.items.isEmpty { AudioLoadState(loading: list.loading, error: list.error, empty: list.loading ? nil : ("No albums", "square.stack", "")) } }
        .searchable(text: $query, prompt: "Filter albums")
        .navigationTitle(title)
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Menu {
                    Picker("Sort", selection: $sort) {
                        Text("Title").tag("title")
                        Text("Artist").tag("artist")
                        Text("Year").tag("year")
                        Text("Recently Added").tag("added")
                        Text("Most Played").tag("plays")
                        Text("Rating").tag("rating")
                    }
                } label: {
                    Label("Sort", systemImage: "arrow.up.arrow.down")
                }
            }
        }
        .onAppear {
            if !started, let initialSort { sort = initialSort }
            started = true
        }
        .task(id: "\(query)|\(sort)") {
            try? await Task.sleep(for: .milliseconds(query.isEmpty ? 0 : 250))
            let q = query, sort = sort, genre = genre, decade = decade
            let order = ["title", "artist"].contains(sort) ? "asc" : "desc"
            list.fetch = { offset in
                try await AppModel.shared.client.musicAlbums(q: q, genre: genre, decade: decade, sort: sort, order: order, offset: offset)
            }
            await list.reload()
        }
    }
}

// MARK: - Songs

struct MusicSongsView: View {
    var lovedOnly = false
    @State private var list = PagedList<MusicTrack>()
    @State private var query = ""
    @AppStorage("music.songs.sort") private var sort = "title"
    @State private var minRating = 0
    @State private var loved = false

    private var context: AudioPlayContext {
        AudioPlayContext(type: "queue", id: lovedOnly || loved ? "loved" : "songs", name: lovedOnly || loved ? "Loved Songs" : "Songs")
    }

    var body: some View {
        List {
            if !list.items.isEmpty {
                PlayShuffleButtons(play: { playAll(shuffle: false) }, shuffle: { playAll(shuffle: true) })
            }
            ForEach(Array(list.items.enumerated()), id: \.element.id) { offset, track in
                TrackRow(track: track) {
                    AudioPlayer.shared.play(tracks: list.items, startAt: offset, context: context)
                }
                .task { await list.loadMoreIfNeeded(track) }
            }
            if let total = list.total, total > 0 {
                Text("\(total.formatted()) songs").font(.caption).foregroundStyle(.secondary)
            }
        }
        .listStyle(.plain)
        .overlay { if list.items.isEmpty { AudioLoadState(loading: list.loading, error: list.error, empty: list.loading ? nil : ("No songs", "music.note", lovedOnly ? "Love a song and it shows up here." : "")) } }
        .searchable(text: $query, prompt: "Filter songs")
        .navigationTitle(lovedOnly ? "Loved" : "Songs")
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Menu {
                    Picker("Sort", selection: $sort) {
                        Text("Title").tag("title")
                        Text("Artist").tag("artist")
                        Text("Album").tag("album")
                        Text("Year").tag("year")
                        Text("Recently Added").tag("added")
                        Text("Most Played").tag("plays")
                        Text("Recently Played").tag("lastPlayed")
                        Text("Rating").tag("rating")
                        Text("Length").tag("duration")
                    }
                    if !lovedOnly {
                        Toggle("Loved Only", isOn: $loved)
                    }
                    Picker("Rating", selection: $minRating) {
                        Text("Any Rating").tag(0)
                        ForEach(1...5, id: \.self) { Text("\(String(repeating: "★", count: $0)) or more").tag($0) }
                    }
                } label: {
                    Label("Sort and Filter", systemImage: "line.3.horizontal.decrease.circle")
                }
            }
        }
        .task(id: "\(query)|\(sort)|\(minRating)|\(loved)") {
            try? await Task.sleep(for: .milliseconds(query.isEmpty ? 0 : 250))
            let q = query, sort = sort, onlyLoved = lovedOnly || loved, rating = minRating > 0 ? minRating : nil
            let order = ["title", "artist", "album"].contains(sort) ? "asc" : "desc"
            list.fetch = { offset in
                try await AppModel.shared.client.musicTracks(q: q, loved: onlyLoved, minRating: rating, sort: sort, order: order, offset: offset)
            }
            await list.reload()
        }
    }

    private func playAll(shuffle: Bool) {
        // Everything that matches, not just the page on screen.
        let total = list.total ?? 0
        guard total > list.items.count, let fetch = list.fetch else {
            AudioPlayer.shared.play(tracks: list.items, context: context, shuffle: shuffle)
            return
        }
        Task {
            var all = list.items
            while all.count < min(total, 2000), let page = try? await fetch(all.count), !page.items.isEmpty {
                all += page.items
            }
            AudioPlayer.shared.play(tracks: all, context: context, shuffle: shuffle)
        }
    }
}

// MARK: - Genres

struct MusicGenresView: View {
    @State private var genres: [MusicGenre] = []
    @State private var loading = true
    @State private var error: String?

    var body: some View {
        ScrollView {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 160), spacing: 14)], spacing: 14) {
                ForEach(genres) { genre in
                    NavigationLink {
                        MusicGenreView(genre: genre.name)
                    } label: {
                        ZStack(alignment: .bottomLeading) {
                            MosaicArtwork(paths: genre.artUrls ?? [], corner: 12, placeholder: "guitars")
                                .overlay { LinearGradient(colors: [.black.opacity(0.75), .clear], startPoint: .bottom, endPoint: .center).clipShape(RoundedRectangle(cornerRadius: 12)) }
                            VStack(alignment: .leading, spacing: 1) {
                                Text(genre.name).font(.headline).foregroundStyle(.white)
                                Text("\(genre.albumCount ?? 0) albums · \(genre.trackCount ?? 0) songs").font(.caption2).foregroundStyle(.white.opacity(0.8))
                            }
                            .padding(10)
                        }
                    }
                    .buttonStyle(.plain)
                    .contextMenu {
                        Button("Start \(genre.name) Radio", systemImage: "dot.radiowaves.left.and.right") {
                            MusicActions.startRadio(seedType: "genre", seedId: genre.name, name: genre.name)
                        }
                    }
                }
            }
            .padding()
        }
        .overlay { if genres.isEmpty { AudioLoadState(loading: loading, error: error) } }
        .navigationTitle("Genres")
        .task {
            do { genres = try await AppModel.shared.client.musicGenres() } catch { self.error = error.localizedDescription }
            loading = false
        }
    }
}

struct MusicGenreView: View {
    let genre: String
    @State private var albums: [MusicAlbum] = []
    @State private var tracks: [MusicTrack] = []
    @State private var loading = true

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                HStack(spacing: 12) {
                    Button {
                        MusicActions.startRadio(seedType: "genre", seedId: genre, name: genre)
                    } label: {
                        Label("\(genre) Radio", systemImage: "dot.radiowaves.left.and.right").frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.borderedProminent).foregroundStyle(Color.onSentinelGreen)
                    Button {
                        AudioPlayer.shared.play(tracks: tracks, context: AudioPlayContext(type: "queue", id: "genre:\(genre)", name: genre), shuffle: true)
                    } label: {
                        Label("Shuffle", systemImage: "shuffle").frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.bordered)
                    .disabled(tracks.isEmpty)
                }
                .controlSize(.large)
                .tint(SentinelTheme.green)
                .frame(maxWidth: 520)
                .padding(.horizontal)
                if !tracks.isEmpty {
                    TrackGridShelf(title: "Most Played", tracks: Array(tracks.prefix(20)),
                                   context: AudioPlayContext(type: "queue", id: "genre:\(genre)", name: genre))
                }
                if !albums.isEmpty {
                    AudioShelf(title: "Albums") {
                        MusicAlbumsView(genre: genre, title: genre)
                    } content: {
                        ForEach(albums) { AlbumTile(album: $0) }
                    }
                }
            }
            .padding(.vertical)
        }
        .overlay { if loading { ProgressView() } }
        .navigationTitle(genre)
        .task {
            let client = AppModel.shared.client
            async let a = client.musicAlbums(genre: genre, sort: "plays", order: "desc", limit: 30)
            async let t = client.musicTracks(genre: genre, sort: "plays", order: "desc", limit: 200)
            albums = (try? await a.items) ?? []
            tracks = (try? await t.items) ?? []
            loading = false
        }
    }
}

struct MusicDecadeView: View {
    let decade: Int
    let label: String
    @State private var tracks: [MusicTrack] = []

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                Button {
                    MusicActions.play(mix: MusicMix(id: "decade-\(decade)", name: "\(label) Mix"))
                } label: {
                    Label("Play the \(label) Mix", systemImage: "play.fill").frame(maxWidth: 360)
                }
                .buttonStyle(.borderedProminent).foregroundStyle(Color.onSentinelGreen)
                .controlSize(.large)
                .tint(SentinelTheme.green)
                .padding(.horizontal)
                if !tracks.isEmpty {
                    TrackGridShelf(title: "Most Played", tracks: Array(tracks.prefix(24)),
                                   context: AudioPlayContext(type: "queue", id: "decade-\(decade)", name: label))
                }
                VStack(alignment: .leading) {
                    Text("Albums").font(.title3.bold()).padding(.horizontal)
                    MusicAlbumsGridInline(decade: decade)
                }
            }
            .padding(.vertical)
        }
        .navigationTitle(label)
        .task {
            tracks = (try? await AppModel.shared.client.musicTracks(decade: decade, sort: "plays", order: "desc", limit: 50).items) ?? []
        }
    }
}

private struct MusicAlbumsGridInline: View {
    let decade: Int
    @State private var albums: [MusicAlbum] = []

    var body: some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 150), spacing: 16, alignment: .top)], spacing: 20) {
            ForEach(albums) { AlbumTile(album: $0, width: nil) }
        }
        .padding(.horizontal)
        .task {
            albums = (try? await AppModel.shared.client.musicAlbums(decade: decade, sort: "year", order: "asc", limit: 200).items) ?? []
        }
    }
}

struct MusicMixView: View {
    let mix: MusicMix
    @State private var tracks: [MusicTrack] = []
    @State private var name: String?
    @State private var error: String?

    var body: some View {
        List {
            HStack(spacing: 16) {
                MosaicArtwork(paths: mix.artUrls ?? [], size: 120, corner: 10, placeholder: "sparkles")
                VStack(alignment: .leading, spacing: 4) {
                    Text(name ?? mix.name).font(.title2.bold())
                    if let description = mix.description { Text(description).foregroundStyle(.secondary) }
                }
            }
            .listRowSeparator(.hidden)
            if !tracks.isEmpty {
                PlayShuffleButtons(
                    play: { AudioPlayer.shared.play(tracks: tracks, context: AudioPlayContext(type: "mix", id: mix.id, name: name ?? mix.name)) },
                    shuffle: { AudioPlayer.shared.play(tracks: tracks, context: AudioPlayContext(type: "mix", id: mix.id, name: name ?? mix.name), shuffle: true) })
            }
            ForEach(Array(tracks.enumerated()), id: \.offset) { offset, track in
                TrackRow(track: track) {
                    AudioPlayer.shared.play(tracks: tracks, startAt: offset, context: AudioPlayContext(type: "mix", id: mix.id, name: name ?? mix.name))
                }
            }
            if let error { Text(error).foregroundStyle(SentinelTheme.red) }
        }
        .listStyle(.plain)
        .navigationTitle(mix.name)
        .task {
            do {
                let result = try await AppModel.shared.client.musicMix(id: mix.id)
                tracks = result.tracks
                name = result.name
            } catch { self.error = error.localizedDescription }
        }
    }
}

// MARK: - Playlists

struct MusicPlaylistsView: View {
    @State private var playlists: [MusicPlaylist] = []
    @State private var loading = true
    @State private var error: String?
    @State private var editing: PlaylistEditorRequest?

    var body: some View {
        List {
            Section {
                Button { editing = PlaylistEditorRequest(smart: false) } label: { Label("New Playlist", systemImage: "plus") }
                Button { editing = PlaylistEditorRequest(smart: true) } label: { Label("New Smart Playlist", systemImage: "gearshape.2") }
            }
            let mine = playlists.filter(\.isMine)
            let others = playlists.filter { !$0.isMine }
            if !mine.isEmpty {
                Section("My Playlists") { ForEach(mine) { row($0) } }
            }
            if !others.isEmpty {
                Section("Shared with Me") { ForEach(others) { row($0) } }
            }
        }
        .overlay { if playlists.isEmpty { AudioLoadState(loading: loading, error: error, empty: loading ? nil : ("No playlists yet", "music.note.list", "Make one with New Playlist, or a smart one that fills itself from rules.")) } }
        .navigationTitle("Playlists")
        .sheet(item: $editing) { request in
            PlaylistEditorView(existing: nil, startSmart: request.smart) { _ in Task { await load() } }
        }
        .task { await load() }
        .refreshable { await load() }
        .onReceive(NotificationCenter.default.publisher(for: .audioLibraryChanged)) { _ in Task { await load() } }
    }

    private func row(_ playlist: MusicPlaylist) -> some View {
        NavigationLink {
            MusicPlaylistView(playlistId: playlist.id, initial: playlist)
        } label: {
            HStack(spacing: 12) {
                MosaicArtwork(paths: playlist.artUrls ?? [], size: 52, corner: 6, placeholder: playlist.isSmart ? "gearshape.2" : "music.note.list")
                VStack(alignment: .leading, spacing: 2) {
                    HStack(spacing: 4) {
                        if playlist.pinned == true { Image(systemName: "pin.fill").font(.caption2).foregroundStyle(SentinelTheme.green) }
                        Text(playlist.name)
                    }
                    Text(playlistSubtitle(playlist)).font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        .contextMenu {
            Button("Play", systemImage: "play.fill") { MusicActions.play(playlist: playlist) }
            Button("Shuffle", systemImage: "shuffle") { MusicActions.play(playlist: playlist, shuffle: true) }
            Button(playlist.pinned == true ? "Unpin from Home" : "Pin to Home", systemImage: "pin") {
                Task {
                    try? await AppModel.shared.client.pinPlaylist(id: playlist.id, pinned: playlist.pinned != true)
                    await load()
                }
            }
        }
    }

    private func load() async {
        do {
            playlists = try await AppModel.shared.client.musicPlaylists()
            error = nil
        } catch {
            if !error.isCancellation { self.error = error.localizedDescription }
        }
        loading = false
    }
}

struct PlaylistEditorRequest: Identifiable {
    let id = UUID()
    var smart: Bool
}

// MARK: - Folders

struct MusicFolderView: View {
    let libraryId: String?
    let path: String?
    let title: String
    @State private var listing: MusicFolderListing?
    @State private var error: String?

    var body: some View {
        List {
            if let listing {
                let tracks = listing.tracks ?? []
                if !tracks.isEmpty {
                    PlayShuffleButtons(
                        play: { AudioPlayer.shared.play(tracks: tracks, context: context) },
                        shuffle: { AudioPlayer.shared.play(tracks: tracks, context: context, shuffle: true) })
                }
                ForEach(listing.folders ?? []) { folder in
                    // On the first page (no library yet) each "folder" is a library: its id is the
                    // libraryId when the server says so, and otherwise the path it gave.
                    let atRoot = libraryId == nil && listing.libraryId == nil
                    NavigationLink {
                        MusicFolderView(
                            libraryId: folder.libraryId ?? listing.libraryId ?? libraryId ?? folder.path,
                            path: atRoot && folder.libraryId == nil ? "" : folder.path, title: folder.name)
                    } label: {
                        HStack {
                            Label(folder.name, systemImage: libraryId == nil && listing.libraryId == nil ? "externaldrive" : "folder")
                            Spacer()
                            if let count = folder.trackCount { Text("\(count)").font(.caption).foregroundStyle(.secondary) }
                        }
                    }
                }
                ForEach(Array(tracks.enumerated()), id: \.element.id) { offset, track in
                    TrackRow(track: track, showArt: false, number: track.trackNumber) {
                        AudioPlayer.shared.play(tracks: tracks, startAt: offset, context: context)
                    }
                }
            }
        }
        .overlay { if listing == nil { AudioLoadState(loading: error == nil, error: error) } }
        .navigationTitle(title)
        .task {
            do { listing = try await AppModel.shared.client.musicFolders(libraryId: libraryId, path: path) }
            catch { self.error = error.localizedDescription }
        }
    }

    private var context: AudioPlayContext { AudioPlayContext(type: "queue", id: "folder:\(path ?? "")", name: title) }
}
