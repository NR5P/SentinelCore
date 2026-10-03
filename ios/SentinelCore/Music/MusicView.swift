import SwiftUI

// The Music section (core/docs/MUSIC_AND_PODCASTS.md §2-4): Home's rows, the whole library by
// artist/album/song/genre/playlist/folder, and search. Playing anything hands it to the shared
// AudioPlayer, whose mini player sits under every tab.
struct MusicView: View {
    enum Pane: String, CaseIterable, Identifiable {
        case home, library, search
        var id: Self { self }
        var title: String {
            switch self {
            case .home: "Home"
            case .library: "Library"
            case .search: "Search"
            }
        }
    }

    let isActive: Bool
    @EnvironmentObject private var model: AppModel
    @AppStorage("music.pane") private var pane: Pane = .home
    @State private var showingStats = false
    @State private var showingAdd = false
    @State private var canDownload = false
    @State private var downloadsReason: String?
    private var router: AudioRouter { AudioRouter.shared }

    var body: some View {
        Group {
            switch pane {
            case .home: MusicHomeView(isActive: isActive)
            case .library: MusicLibraryView()
            case .search: MusicSearchView()
            }
        }
        .safeAreaInset(edge: .top, spacing: 0) {
            VStack(spacing: 0) {
                Picker("Show", selection: $pane) {
                    ForEach(Pane.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(maxWidth: 420)
                .padding(.horizontal)
                .padding(.vertical, 8)
                .frame(maxWidth: .infinity)
                ContinueListeningBanner(kind: "music")
            }
            .background(.bar)
        }
        .navigationTitle("Music")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItemGroup(placement: .primaryAction) {
                Button {
                    MusicActions.shuffleAll()
                } label: {
                    Label("Shuffle All", systemImage: "shuffle")
                }
                .help("Shuffle the whole library")
                Menu {
                    Button("Listening Stats & Wrapped", systemImage: "chart.bar.xaxis") { showingStats = true }
                    NavigationLink {
                        AudioDownloadsView()
                    } label: {
                        Label("Downloaded on This Device", systemImage: "arrow.down.circle")
                    }
                    NavigationLink {
                        WakeAlarmsView()
                    } label: {
                        Label("Wake-Up Alarms", systemImage: "alarm")
                    }
                    if canDownload {
                        NavigationLink {
                            MusicDownloadsListView()
                        } label: {
                            Label("Album Downloads", systemImage: "tray.and.arrow.down")
                        }
                    }
                } label: {
                    Label("More", systemImage: "ellipsis.circle")
                }
                if canDownload {
                    Button {
                        showingAdd = true
                    } label: {
                        Label("Add Album", systemImage: "plus")
                    }
                    .help("Find an album and download it into the music library")
                }
            }
        }
        .navigationDestination(item: Binding(get: { router.musicRoute }, set: { router.musicRoute = $0 })) { route in
            MusicRouteView(route: route)
        }
        .sheet(isPresented: $showingStats) {
            NavigationStack { AudioStatsView() }
                #if os(macOS)
                .frame(minWidth: 720, minHeight: 640)
                #endif
        }
        .sheet(isPresented: $showingAdd) {
            MusicAddAlbumView()
        }
        .task(id: isActive) {
            guard isActive else { return }
            do {
                let status = try await model.client.musicDownloadsStatus()
                canDownload = status.canDownload == true
            } catch {
                canDownload = false
            }
        }
    }
}

struct MusicRouteView: View {
    let route: MusicRoute

    var body: some View {
        switch route {
        case .album(let id): MusicAlbumView(albumId: id)
        case .artist(let id): MusicArtistView(artistId: id)
        case .playlist(let id): MusicPlaylistView(playlistId: id)
        case .genre(let name): MusicGenreView(genre: name)
        case .decade(let decade, let label): MusicDecadeView(decade: decade, label: label)
        case .mix(let mix): MusicMixView(mix: mix)
        }
    }
}

// MARK: - Home

private struct MusicHomeView: View {
    let isActive: Bool
    @EnvironmentObject private var model: AppModel
    @State private var home: MusicHome?
    @State private var loading = false
    @State private var errorMessage: String?
    @State private var refreshToken = 0

    var body: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 28) {
                if let home {
                    header(home)
                    if let pinned = home.pinned, !pinned.isEmpty {
                        AudioShelf(title: "Pinned") {
                            ForEach(pinned) { PlaylistTile(playlist: $0) }
                        }
                    }
                    if let recent = home.recentlyPlayed, !recent.isEmpty {
                        AudioShelf(title: "Recently Played") {
                            ForEach(recent) { item in recentTile(item) }
                        }
                    }
                    if let mixes = home.mixes, !mixes.isEmpty {
                        AudioShelf(title: "Made for You", subtitle: "Mixes from what you play") {
                            ForEach(mixes) { MixTile(mix: $0) }
                        }
                    }
                    albumShelf("Recently Added", home.recentlyAdded, sort: "added")
                    albumShelf("Heavy Rotation", home.heavyRotation, subtitle: "Your most played lately", sort: "plays")
                    if let day = home.onThisDay, let tracks = day.tracks, !tracks.isEmpty {
                        TrackGridShelf(title: "On This Day", subtitle: day.label.map { "You were playing these on \($0)" },
                                       tracks: tracks, context: AudioPlayContext(type: "queue", id: "on-this-day", name: "On This Day"))
                    }
                    if let tracks = home.forgottenFavorites, !tracks.isEmpty {
                        TrackGridShelf(title: "Forgotten Favorites", subtitle: "Songs you loved and haven't heard in a while",
                                       tracks: tracks, context: AudioPlayContext(type: "queue", id: "forgotten", name: "Forgotten Favorites"))
                    }
                    albumShelf("Discover", home.discover, subtitle: "Albums you've never played")
                    if let tracks = home.lovedTracks, !tracks.isEmpty {
                        TrackGridShelf(title: "Loved", tracks: tracks, context: AudioPlayContext(type: "queue", id: "loved", name: "Loved Songs"))
                    }
                    if let decades = home.decades, !decades.isEmpty {
                        decadesRow(decades)
                    }
                }
            }
            .padding(.vertical)
        }
        .overlay {
            if home == nil {
                AudioLoadState(loading: loading, error: errorMessage, retry: { refreshToken += 1 })
            } else if let home, home.isEmpty {
                ContentUnavailableView(
                    "No music yet", systemImage: "music.note.house",
                    description: Text("An admin adds the folder your music is in from Settings → Music Libraries. The library fills in as the server scans it."))
            }
        }
        .refreshable { await load() }
        .task(id: Key(active: isActive, server: model.activeServerURL, token: refreshToken)) {
            guard isActive, !model.activeServerURL.isEmpty else { return }
            await load()
        }
        .onReceive(NotificationCenter.default.publisher(for: .audioLibraryChanged)) { _ in refreshToken += 1 }
    }

    private struct Key: Hashable {
        var active: Bool
        var server: String
        var token: Int
    }

    private func header(_ home: MusicHome) -> some View {
        HStack(spacing: 12) {
            Button {
                MusicActions.shuffleAll()
            } label: {
                Label("Shuffle All", systemImage: "shuffle")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent).foregroundStyle(Color.onSentinelGreen)
            NavigationLink {
                MusicSongsView(lovedOnly: true)
            } label: {
                Label("Loved", systemImage: "heart.fill").frame(maxWidth: .infinity)
            }
            .buttonStyle(.bordered)
        }
        .controlSize(.large)
        .tint(SentinelTheme.green)
        .frame(maxWidth: 520)
        .padding(.horizontal)
        .overlay(alignment: .bottomLeading) {
            if let tracks = home.trackCount, tracks > 0 {
                Text("\(tracks.formatted()) songs · \((home.albumCount ?? 0).formatted()) albums · \((home.artistCount ?? 0).formatted()) artists")
                    .font(.caption).foregroundStyle(.secondary)
                    .padding(.horizontal)
                    .offset(y: 22)
            }
        }
        .padding(.bottom, 14)
    }

    @ViewBuilder
    private func albumShelf(_ title: String, _ albums: [MusicAlbum]?, subtitle: String? = nil, sort: String? = nil) -> some View {
        if let albums, !albums.isEmpty {
            if let sort {
                AudioShelf(title: title, subtitle: subtitle) {
                    MusicAlbumsView(initialSort: sort)
                } content: {
                    ForEach(albums) { AlbumTile(album: $0) }
                }
            } else {
                AudioShelf(title: title, subtitle: subtitle) {
                    ForEach(albums) { AlbumTile(album: $0) }
                }
            }
        }
    }

    @ViewBuilder
    private func recentTile(_ item: MusicRecentItem) -> some View {
        if let album = item.album {
            AlbumTile(album: album)
        } else if let playlist = item.playlist {
            PlaylistTile(playlist: playlist)
        } else if let artist = item.artist {
            ArtistTile(artist: artist, width: 150)
        } else if let mix = item.mix {
            MixTile(mix: mix, width: 150)
        }
    }

    private func decadesRow(_ decades: [MusicDecade]) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Decades").font(.title3.bold()).padding(.horizontal)
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 10) {
                    ForEach(decades) { decade in
                        NavigationLink {
                            MusicDecadeView(decade: decade.decade, label: decade.title)
                        } label: {
                            VStack(spacing: 2) {
                                Text(decade.title).font(.title2.bold())
                                if let count = decade.trackCount { Text("\(count) songs").font(.caption2).opacity(0.8) }
                            }
                            .foregroundStyle(.white)
                            .frame(width: 110, height: 76)
                            .background(ArtworkPlaceholder(seed: decade.title, symbol: "").clipShape(RoundedRectangle(cornerRadius: 12)))
                        }
                        .buttonStyle(.plain)
                        .contextMenu {
                            Button("Play \(decade.title) Mix", systemImage: "play.fill") {
                                MusicActions.play(mix: MusicMix(id: "decade-\(decade.decade)", name: "\(decade.title) Mix"))
                            }
                        }
                    }
                }
                .padding(.horizontal)
            }
        }
    }

    private func load() async {
        loading = home == nil
        defer { loading = false }
        do {
            home = try await model.client.musicHome()
            errorMessage = nil
        } catch {
            if error.isCancellation { return }
            errorMessage = error.localizedDescription
        }
    }
}

// MARK: - Search

private struct MusicSearchView: View {
    @EnvironmentObject private var model: AppModel
    @State private var query = ""
    @State private var results: MusicSearchResults?
    @State private var loading = false
    @State private var errorMessage: String?

    var body: some View {
        List {
            if let results {
                if let artists = results.artists, !artists.isEmpty {
                    Section("Artists") {
                        ForEach(artists) { artist in
                            NavigationLink {
                                MusicArtistView(artistId: artist.id, initial: artist)
                            } label: {
                                HStack {
                                    ArtworkImage(path: artist.artUrl, size: 44, placeholder: "music.mic", circle: true)
                                    Text(artist.name)
                                }
                            }
                        }
                    }
                }
                if let albums = results.albums, !albums.isEmpty {
                    Section("Albums") {
                        ScrollView(.horizontal, showsIndicators: false) {
                            LazyHStack(alignment: .top, spacing: 14) {
                                ForEach(albums) { AlbumTile(album: $0, width: 130) }
                            }
                            .padding(.vertical, 4)
                        }
                        .listRowInsets(EdgeInsets(top: 4, leading: 16, bottom: 4, trailing: 16))
                    }
                }
                if let tracks = results.tracks, !tracks.isEmpty {
                    Section("Songs") {
                        ForEach(Array(tracks.enumerated()), id: \.element.id) { offset, track in
                            TrackRow(track: track) {
                                AudioPlayer.shared.play(tracks: tracks, startAt: offset, context: AudioPlayContext(type: "search", id: query, name: "\u{201C}\(query)\u{201D}"))
                            }
                        }
                    }
                }
                if let playlists = results.playlists, !playlists.isEmpty {
                    Section("Playlists") {
                        ForEach(playlists) { playlist in
                            NavigationLink {
                                MusicPlaylistView(playlistId: playlist.id, initial: playlist)
                            } label: {
                                HStack {
                                    MosaicArtwork(paths: playlist.artUrls ?? [], size: 44, corner: 5)
                                    Text(playlist.name)
                                }
                            }
                        }
                    }
                }
                if let genres = results.genres, !genres.isEmpty {
                    Section("Genres") {
                        ForEach(genres, id: \.self) { genre in
                            NavigationLink(genre) { MusicGenreView(genre: genre) }
                        }
                    }
                }
                if results.isEmpty {
                    ContentUnavailableView.search(text: query)
                }
            } else if query.isEmpty {
                Section {
                    Text("Search artists, albums, songs, playlists and genres.").foregroundStyle(.secondary)
                }
            }
            if let errorMessage { Text(errorMessage).foregroundStyle(SentinelTheme.red) }
        }
        .searchable(text: $query, placement: searchPlacement, prompt: "Artists, albums, songs")
        .overlay { if loading && results == nil { ProgressView() } }
        .task(id: query) {
            let trimmed = query.trimmingCharacters(in: .whitespaces)
            guard !trimmed.isEmpty else { results = nil; return }
            try? await Task.sleep(for: .milliseconds(250))
            guard !Task.isCancelled else { return }
            loading = true
            defer { loading = false }
            do {
                results = try await model.client.musicSearch(trimmed)
                errorMessage = nil
            } catch {
                if !error.isCancellation { errorMessage = error.localizedDescription }
            }
        }
    }

    private var searchPlacement: SearchFieldPlacement {
        #if os(iOS)
        .navigationBarDrawer(displayMode: .always)
        #else
        .automatic
        #endif
    }
}
