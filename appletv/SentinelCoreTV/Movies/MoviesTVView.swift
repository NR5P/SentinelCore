import SwiftUI

// Movies & TV on the television - the family media library, laid out like the Apple TV app:
// a hero, shelves to scroll sideways, poster grids, and a detail page behind every tile.
// See core/docs/MOVIES.md for the API.
struct MoviesTVView: View {
    enum Pane: String, CaseIterable, Identifiable {
        case home, movies, shows, search, downloads
        var id: Self { self }
        var title: String {
            switch self {
            case .home: "Home"
            case .movies: "Movies"
            case .shows: "TV Shows"
            case .search: "Search"
            case .downloads: "Downloads"
            }
        }
    }

    let isActive: Bool
    @EnvironmentObject private var model: AppModel
    @State private var pane: Pane = .home
    @State private var playing: MoviePlayRequest?
    @State private var detailRoute: MediaRoute?
    // Movie downloads: the Downloads pane is only there when the server says this account may
    // use them (see MediaDownloadsTVView).
    @StateObject private var downloads = MediaDownloadsTracker()
    #if DEBUG
    private struct DebugURL: Identifiable { let url: URL; var id: String { url.absoluteString } }
    @State private var debugURL: DebugURL?
    #endif
    // Bumped whenever the player closes, so whatever is on screen re-reads progress.
    @State private var refreshToken = 0

    var body: some View {
        VStack(alignment: .leading, spacing: 24) {
            Picker("Show", selection: $pane) {
                ForEach(Pane.allCases.filter { $0 != .downloads || downloads.canDownload }) { Text($0.title).tag($0) }
            }
            .pickerStyle(.segmented)
            .frame(maxWidth: 1000)
            .padding(.horizontal, 60)

            switch pane {
            case .home:
                MediaHomeTV(
                    isActive: isActive, refreshToken: refreshToken, downloads: downloads,
                    play: { playing = $0 }, open: { detailRoute = $0 }, openDownloads: { pane = .downloads })
            case .movies:
                MediaGridTV(kind: "movie", refreshToken: refreshToken)
            case .shows:
                MediaGridTV(kind: "show", refreshToken: refreshToken)
            case .search:
                MediaSearchTV(downloads: downloads) { pane = .downloads }
            case .downloads:
                MediaDownloadsTVView(downloads: downloads, open: { detailRoute = $0 })
            }
        }
        // Reads whether downloads are on, then follows the queue - always while the Downloads
        // pane shows, otherwise only while something is downloading (the banner on Home).
        .task(id: DownloadsKey(active: isActive, server: model.activeServerURL, following: pane == .downloads)) {
            guard isActive, !model.activeServerURL.isEmpty else { return }
            await downloads.run(client: model.client, following: pane == .downloads)
        }
        .onChange(of: model.activeServerURL) { _, _ in downloads.reset() }
        .onChange(of: downloads.landed) { _, _ in refreshToken += 1 }
        .onChange(of: downloads.canDownload) { _, can in
            if pane == .downloads, !can { pane = .home }
        }
        .navigationDestination(for: MediaRoute.self) { route in
            MovieDetailTVView(route: route, onDeleted: { refreshToken += 1 })
        }
        .navigationDestination(item: $detailRoute) { route in
            MovieDetailTVView(route: route, onDeleted: { refreshToken += 1 })
        }
        .fullScreenCover(item: $playing, onDismiss: { refreshToken += 1 }) { request in
            MoviePlayerTVView(request: request, client: model.client)
        }
        #if DEBUG
        // Simulator testing without a remote: `-debugMediaItem <id>` opens a detail page,
        // `-debugPlayItem <id>` starts playing it from the top, and `-debugMoviesPane downloads`
        // picks a pane.
        .task(id: model.activeServerURL) {
            guard !model.activeServerURL.isEmpty else { return }
            let defaults = UserDefaults.standard
            if let id = defaults.string(forKey: "debugMediaItem") { detailRoute = MediaRoute(id: id, title: "") }
            if let id = defaults.string(forKey: "debugPlayItem") { playing = MoviePlayRequest(itemId: id, start: 0) }
            if let raw = defaults.string(forKey: "debugPlayURL"), let url = URL(string: raw) { debugURL = DebugURL(url: url) }
            if let raw = defaults.string(forKey: "debugMoviesPane"), let picked = Pane(rawValue: raw) { pane = picked }
        }
        .fullScreenCover(item: $debugURL) { DebugRawPlayerView(url: $0.url) }
        #endif
    }

    private struct DownloadsKey: Hashable {
        var active: Bool
        var server: String
        var following: Bool
    }
}

// MARK: - Home

private struct MediaHomeTV: View {
    let isActive: Bool
    let refreshToken: Int
    @ObservedObject var downloads: MediaDownloadsTracker
    let play: (MoviePlayRequest) -> Void
    let open: (MediaRoute) -> Void
    let openDownloads: () -> Void
    @EnvironmentObject private var model: AppModel
    @State private var home: MediaHome?
    @State private var errorMessage: String?
    @State private var loading = false
    // Bumped each time Home comes back into view after a detail page, which may have changed My
    // List or what is watched.
    @State private var returns = 0
    @State private var hasAppeared = false
    @State private var surprising = false
    // A Surprise Me that found nothing (or failed), in the server's words.
    @State private var surpriseError: String?

    var body: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 50) {
                if let featured = home?.featured {
                    MediaHeroTV(item: featured)
                }
                let hasHome = home.map { !$0.isEmpty } ?? false
                if hasHome || !downloads.active.isEmpty {
                    HStack(spacing: 40) {
                        if hasHome {
                            Button {
                                Task { await surpriseMe() }
                            } label: {
                                Label("Surprise Me", systemImage: "shuffle")
                            }
                            .disabled(surprising)
                            // White like the detail page's buttons: left alone, a tinted tvOS button
                            // draws its title in the tint and it vanishes into the green.
                            .foregroundStyle(.white)
                        }
                        // "Downloading Bluey S02E05 · 42%" while anything is in the queue.
                        MediaDownloadsBannerTV(downloads: downloads, open: openDownloads)
                    }
                    .padding(.horizontal, 60)
                }
                if let items = home?.continueWatching, !items.isEmpty {
                    MediaShelfTV(title: "Continue Watching") {
                        ForEach(items) { item in
                            MediaLandscapeTileTV(
                                item: item, title: item.headline, subtitle: item.subheadline ?? item.remainingLabel,
                                action: { play(.resume(item)) })
                            .contextMenu {
                                Button("Resume", systemImage: "play.fill") { play(.resume(item)) }
                                Button("Start Over", systemImage: "backward.end.fill") { play(.startOver(item)) }
                                Button(item.isEpisode ? "Go to Show" : "Details", systemImage: "info.circle") { open(item.route) }
                            }
                        }
                    }
                }
                posterShelf("My List", home?.myList)
                posterShelf("Recently Added Movies", home?.recentMovies)
                posterShelf("Recently Added Shows", home?.recentShows)
                if let items = home?.recentEpisodes, !items.isEmpty {
                    MediaShelfTV(title: "New Episodes") {
                        ForEach(items) { item in
                            MediaLandscapeTileTV(
                                item: item, title: item.headline, subtitle: item.subheadline,
                                action: { open(item.route) })
                            .contextMenu {
                                Button("Play", systemImage: "play.fill") { play(.resume(item)) }
                                Button("Go to Show", systemImage: "info.circle") { open(item.route) }
                            }
                        }
                    }
                }
                posterShelf("Most Played", home?.mostPlayed)
                posterShelf("Top Rated", home?.topRated)
                ForEach(home?.genreShelves ?? []) { shelf in
                    posterShelf(shelf.genre, shelf.items)
                }
                posterShelf("Watch Again", home?.recentlyWatched)
            }
            .padding(.vertical, 20)
        }
        .scrollClipDisabled()
        .alert("Surprise Me", isPresented: Binding(get: { surpriseError != nil }, set: { if !$0 { surpriseError = nil } })) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(surpriseError ?? "")
        }
        .onAppear {
            if hasAppeared { returns += 1 }
            hasAppeared = true
        }
        .overlay {
            if home == nil, loading {
                ProgressView("Loading…")
            } else if home == nil, let errorMessage {
                ContentUnavailableView("Movies & TV isn't available", systemImage: "film.stack", description: Text(errorMessage))
            } else if let home, home.isEmpty {
                ContentUnavailableView(
                    "Nothing here yet", systemImage: "film.stack",
                    description: Text("An admin adds the Movies or TV Shows folder in Settings → Movies & TV Libraries (or in the dashboard's Movies tab). The library fills in as the server scans it."))
            }
        }
        .task(id: TaskKey(active: isActive, token: refreshToken, returns: returns, server: model.activeServerURL)) {
            guard isActive, !model.activeServerURL.isEmpty else { return }
            await load()
        }
    }

    private struct TaskKey: Hashable {
        var active: Bool
        var token: Int
        var returns: Int
        // The tab can be on screen before the app has connected; loading waits for an address.
        var server: String
    }

    // A row of posters, or nothing when the server sent none.
    @ViewBuilder
    private func posterShelf(_ title: String, _ items: [MediaItemSummary]?) -> some View {
        if let items, !items.isEmpty {
            MediaShelfTV(title: title) {
                ForEach(items) { MediaPosterTileTV(item: $0) }
            }
        }
    }

    // Asks the server for a title at random and opens it the way a context menu's Details does.
    private func surpriseMe() async {
        surprising = true
        defer { surprising = false }
        do {
            open(try await model.client.mediaRandom().route)
        } catch {
            if error is CancellationError || (error as? URLError)?.code == .cancelled { return }
            surpriseError = error.localizedDescription
        }
    }

    private func load() async {
        loading = true
        defer { loading = false }
        do {
            home = try await model.client.mediaHome()
            errorMessage = nil
        } catch {
            if error is CancellationError || (error as? URLError)?.code == .cancelled { return }
            errorMessage = error.localizedDescription
        }
    }
}

// The featured title across the top: its backdrop, big title, and the line under it.
private struct MediaHeroTV: View {
    let item: MediaItemSummary
    @EnvironmentObject private var model: AppModel

    var body: some View {
        NavigationLink(value: item.route) {
            ZStack(alignment: .bottomLeading) {
                PosterImage(url: model.client.mediaURL(item.backdropUrl), title: item.title, maxPixel: 1920)
                LinearGradient(colors: [.black.opacity(0.85), .black.opacity(0.2), .clear], startPoint: .bottom, endPoint: .top)
                VStack(alignment: .leading, spacing: 10) {
                    Text(item.title)
                        .font(.system(size: 64, weight: .bold))
                        .lineLimit(2)
                    HStack(spacing: 16) {
                        Text(item.metaLine)
                        if let certification = item.certification, !certification.isEmpty {
                            CertificationBadge(text: certification)
                        }
                    }
                    .font(.callout)
                    .foregroundStyle(.white.opacity(0.8))
                    if let genres = item.genres, !genres.isEmpty {
                        Text(genres.prefix(3).joined(separator: " · "))
                            .font(.caption)
                            .foregroundStyle(.white.opacity(0.7))
                    }
                    if let overview = item.overview, !overview.isEmpty {
                        Text(overview)
                            .font(.callout)
                            .foregroundStyle(.white.opacity(0.85))
                            .lineLimit(3)
                            .frame(maxWidth: 900, alignment: .leading)
                            .padding(.top, 6)
                    }
                }
                .foregroundStyle(.white)
                .padding(50)
            }
            // The screen's 1920 less the 80-point safe margins and this row's own 60-point
            // padding on each side; wider and the card runs off the right edge.
            .frame(width: 1640, height: 620)
        }
        .buttonStyle(.card)
        .padding(.horizontal, 60)
        .environment(\.colorScheme, .dark)
    }
}

// A titled row that scrolls sideways.
struct MediaShelfTV<Content: View>: View {
    let title: String
    @ViewBuilder let content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            Text(title)
                .font(.title3.bold())
                .padding(.horizontal, 60)
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: 40) {
                    content
                }
                .padding(.horizontal, 60)
                .padding(.vertical, 20)
            }
            .scrollClipDisabled()
        }
    }
}

// MARK: - Tiles

enum MediaTileSize {
    static let posterWidth: CGFloat = 240
    static let posterHeight: CGFloat = 360
    static let landscapeWidth: CGFloat = 480
    static let landscapeHeight: CGFloat = 270
}

// A 2:3 poster that opens its detail page, with the title and year under it.
struct MediaPosterTileTV: View {
    let item: MediaItemSummary
    @EnvironmentObject private var model: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            NavigationLink(value: item.route) {
                PosterImage(url: model.client.mediaURL(item.posterUrl), title: item.title, subtitle: item.yearLabel, maxPixel: 540)
                    .frame(width: MediaTileSize.posterWidth, height: MediaTileSize.posterHeight)
                    .overlay(alignment: .topTrailing) { MediaTileBadge(item: item) }
                    .overlay(alignment: .bottom) { MediaProgressBar(fraction: item.progressFraction) }
            }
            .buttonStyle(.card)
            VStack(alignment: .leading, spacing: 2) {
                Text(item.title).font(.caption).lineLimit(1)
                if let year = item.yearLabel {
                    Text(year).font(.caption2).foregroundStyle(.secondary)
                }
            }
            .frame(width: MediaTileSize.posterWidth, alignment: .leading)
        }
        .accessibilityElement(children: .combine)
    }
}

// A 16:9 tile - an episode's still or a film's backdrop - with the progress bar along the bottom.
struct MediaLandscapeTileTV: View {
    let item: MediaItemSummary
    let title: String
    let subtitle: String?
    var width: CGFloat = MediaTileSize.landscapeWidth
    let action: () -> Void
    @EnvironmentObject private var model: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Button(action: action) {
                PosterImage(url: model.client.mediaURL(item.landscapeArtPath), title: title, subtitle: subtitle, maxPixel: 960)
                    .frame(width: width, height: width * 9 / 16)
                    .overlay(alignment: .topTrailing) { MediaTileBadge(item: item) }
                    .overlay(alignment: .bottom) { MediaProgressBar(fraction: item.progressFraction) }
            }
            .buttonStyle(.card)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.caption).lineLimit(1)
                if let subtitle {
                    Text(subtitle).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                }
            }
            .frame(width: width, alignment: .leading)
        }
        .accessibilityElement(children: .combine)
    }
}

// Top-right corner of a tile: a check once watched, or how many episodes of a show are left.
struct MediaTileBadge: View {
    let item: MediaItemSummary

    var body: some View {
        if item.isWatched {
            Image(systemName: "checkmark.circle.fill")
                .font(.title3)
                .foregroundStyle(.white, SentinelTheme.green)
                .shadow(radius: 4)
                .padding(10)
                .accessibilityLabel("Watched")
        } else if let remaining = item.unwatchedEpisodeCount {
            Text("\(remaining)")
                .font(.caption2.bold())
                .foregroundStyle(.black)
                .padding(.horizontal, 12)
                .padding(.vertical, 4)
                .background(SentinelTheme.green, in: Capsule())
                .padding(10)
                .accessibilityLabel("\(remaining) unwatched episodes")
        }
    }
}

struct MediaProgressBar: View {
    let fraction: Double?

    var body: some View {
        if let fraction, fraction > 0 {
            GeometryReader { geometry in
                ZStack(alignment: .leading) {
                    Capsule().fill(.black.opacity(0.55))
                    Capsule().fill(SentinelTheme.green).frame(width: max(8, geometry.size.width * fraction))
                }
            }
            .frame(height: 8)
            .padding(.horizontal, 14)
            .padding(.bottom, 12)
            .accessibilityLabel("\(Int(fraction * 100)) percent watched")
        }
    }
}

struct CertificationBadge: View {
    let text: String

    var body: some View {
        Text(text)
            .font(.caption2.bold())
            .padding(.horizontal, 10)
            .padding(.vertical, 3)
            .overlay { RoundedRectangle(cornerRadius: 6).stroke(.secondary, lineWidth: 2) }
    }
}

// MARK: - Grids

private struct MediaGridTV: View {
    let kind: String
    let refreshToken: Int
    @EnvironmentObject private var model: AppModel
    @State private var items: [MediaItemSummary] = []
    @State private var total = 0
    @State private var genres: [String] = []
    @State private var sort: MediaSort = .title
    @State private var genre: String?
    @State private var unwatchedOnly = false
    @State private var myListOnly = false
    @State private var loading = false
    @State private var loadingMore = false
    @State private var errorMessage: String?

    private static let pageSize = 60
    private let columns = [GridItem(.adaptive(minimum: MediaTileSize.posterWidth, maximum: MediaTileSize.posterWidth + 20), spacing: 40)]

    private struct FilterKey: Hashable {
        var sort: MediaSort
        var genre: String?
        var unwatched: Bool
        var myList: Bool
        var token: Int
        var server: String
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 30) {
                filterBar
                LazyVGrid(columns: columns, alignment: .leading, spacing: 50) {
                    ForEach(items) { item in
                        MediaPosterTileTV(item: item)
                            .onAppear {
                                if item.id == items.last?.id { Task { await loadMore() } }
                            }
                    }
                }
                if loadingMore {
                    ProgressView().frame(maxWidth: .infinity)
                }
            }
            .padding(.horizontal, 60)
            .padding(.vertical, 20)
        }
        .scrollClipDisabled()
        .overlay {
            if items.isEmpty, loading {
                ProgressView("Loading…")
            } else if items.isEmpty, let errorMessage {
                ContentUnavailableView("Couldn't load the library", systemImage: "exclamationmark.triangle", description: Text(errorMessage))
            } else if items.isEmpty, !loading {
                ContentUnavailableView(
                    unwatchedOnly || myListOnly || genre != nil ? "Nothing matches" : (kind == "show" ? "No TV shows yet" : "No movies yet"),
                    systemImage: kind == "show" ? "tv" : "film")
            }
        }
        .task(id: FilterKey(sort: sort, genre: genre, unwatched: unwatchedOnly, myList: myListOnly, token: refreshToken, server: model.activeServerURL)) {
            guard !model.activeServerURL.isEmpty else { return }
            await reload()
        }
    }

    private var filterBar: some View {
        HStack(spacing: 30) {
            Menu {
                Picker("Sort", selection: $sort) {
                    ForEach(MediaSort.allCases) { Text($0.title).tag($0) }
                }
            } label: {
                Label(sort.title, systemImage: "arrow.up.arrow.down")
            }
            Menu {
                Picker("Genre", selection: $genre) {
                    Text("All Genres").tag(String?.none)
                    ForEach(genres, id: \.self) { Text($0).tag(Optional($0)) }
                }
            } label: {
                Label(genre ?? "All Genres", systemImage: "line.3.horizontal.decrease")
            }
            Button {
                unwatchedOnly.toggle()
            } label: {
                Label(unwatchedOnly ? "Unwatched Only" : "Everything", systemImage: unwatchedOnly ? "eye.slash" : "eye")
            }
            Button {
                myListOnly.toggle()
            } label: {
                Label(myListOnly ? "My List Only" : "Whole Library", systemImage: myListOnly ? "checkmark" : "plus")
            }
            Spacer()
            if total > 0 {
                Text(kind == "show" ? "\(total) shows" : "\(total) movies")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
    }

    private func reload() async {
        loading = true
        defer { loading = false }
        do {
            let page = try await model.client.mediaItems(
                kind: kind, genre: genre, sort: sort, unwatched: unwatchedOnly, inList: myListOnly, offset: 0, limit: Self.pageSize)
            items = page.items
            total = page.total ?? page.items.count
            if let pageGenres = page.genres { genres = pageGenres }
            errorMessage = nil
        } catch {
            if error is CancellationError || (error as? URLError)?.code == .cancelled { return }
            errorMessage = error.localizedDescription
        }
    }

    private func loadMore() async {
        guard !loading, !loadingMore, items.count < total else { return }
        loadingMore = true
        defer { loadingMore = false }
        do {
            let page = try await model.client.mediaItems(
                kind: kind, genre: genre, sort: sort, unwatched: unwatchedOnly, inList: myListOnly, offset: items.count, limit: Self.pageSize)
            let known = Set(items.map(\.id))
            items.append(contentsOf: page.items.filter { !known.contains($0.id) })
            total = page.total ?? total
        } catch {
            // The next tile to appear tries again.
        }
    }
}

// MARK: - Search

private struct MediaSearchTV: View {
    @ObservedObject var downloads: MediaDownloadsTracker
    let onStarted: () -> Void
    @EnvironmentObject private var model: AppModel
    @State private var query = ""
    @State private var results: [MediaItemSummary] = []
    // The text `results` answer, so the download offer never shows for text still being typed.
    @State private var resultsQuery = ""
    @State private var searching = false
    @State private var addStart: MediaAddStart?
    private let columns = [GridItem(.adaptive(minimum: MediaTileSize.posterWidth, maximum: MediaTileSize.posterWidth + 20), spacing: 40)]

    private var trimmed: String { query.trimmingCharacters(in: .whitespaces) }

    // Nothing in the library for what was typed: offer to download it instead.
    private var offersDownload: Bool {
        downloads.canDownload && !searching && results.isEmpty && trimmed.count >= 2 && resultsQuery == trimmed
    }

    var body: some View {
        ScrollView {
            if offersDownload {
                MediaSearchOfferTV(query: trimmed, tmdbConfigured: downloads.status?.tmdbConfigured != false) { addStart = $0 }
                    .padding(.horizontal, 60)
            }
            LazyVGrid(columns: columns, alignment: .leading, spacing: 50) {
                ForEach(results) { MediaPosterTileTV(item: $0) }
            }
            .padding(.horizontal, 60)
            .padding(.vertical, 20)
        }
        .scrollClipDisabled()
        .searchable(text: $query, prompt: "Movies and TV shows")
        .overlay {
            if searching, results.isEmpty {
                ProgressView()
            } else if !trimmed.isEmpty, results.isEmpty, !offersDownload {
                ContentUnavailableView.search(text: trimmed)
            }
        }
        // Full screen like Downloads' Add: a tvOS sheet is a narrow card. Menu closes it.
        .fullScreenCover(item: $addStart) { start in
            MediaAddTVView(status: downloads.status, vpnDisabled: downloads.vpnDisabled, start: start) { download in
                downloads.noteStarted(download)
                addStart = nil
                onStarted()
            }
            .environmentObject(model)
            // A tvOS full-screen cover is see-through until given a background.
            .background { Color.black.ignoresSafeArea() }
        }
        #if DEBUG
        // Simulator testing without a remote: `-debugMediaSearch <text>` types it into the search.
        .task {
            if let typed = UserDefaults.standard.string(forKey: "debugMediaSearch"), query.isEmpty { query = typed }
        }
        #endif
        .task(id: query) {
            let text = trimmed
            guard !text.isEmpty else {
                results = []
                resultsQuery = ""
                return
            }
            // Let the typing settle before asking.
            try? await Task.sleep(for: .milliseconds(350))
            guard !Task.isCancelled else { return }
            searching = true
            defer { searching = false }
            if let page = try? await model.client.mediaItems(kind: nil, query: text, limit: 60) {
                results = page.items
                resultsQuery = text
            }
        }
    }
}
