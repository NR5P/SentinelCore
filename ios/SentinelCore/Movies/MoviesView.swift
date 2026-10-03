import SwiftUI

// Movies & TV - the family media library on an iPhone, iPad or Mac: a home page of shelves
// (Continue Watching, My List, recently added, most played, genres), poster grids of every movie and show, search, and a detail
// page behind every tile. See core/docs/MOVIES.md for the API.
struct MoviesView: View {
    enum Pane: String, CaseIterable, Identifiable {
        case home, movies, shows, search
        var id: Self { self }
        var title: String {
            switch self {
            case .home: "Home"
            case .movies: "Movies"
            case .shows: "TV Shows"
            case .search: "Search"
            }
        }
    }

    let isActive: Bool
    @EnvironmentObject private var model: AppModel
    @State private var pane: Pane = .home
    @State private var playing: MoviePlayRequest?
    // A detail page opened from a context menu. Tiles push theirs with destination links instead:
    // under the iPhone's More tab this screen lives in a NavigationStack whose path only holds
    // AppSection values, where a NavigationLink(value: MediaRoute) would go nowhere.
    @State private var detailRoute: MediaRoute?
    // Bumped whenever the player closes, so whatever is on screen re-reads progress - and when a
    // download reaches the library, so it shows up under Recently Added.
    @State private var refreshToken = 0
    // Movie downloads ("+ Add"): only there when the server says this account may use them.
    @StateObject private var downloads = MediaDownloadsTracker()
    @State private var addingDownload = false
    @State private var showingDownloads = false

    var body: some View {
        Group {
            switch pane {
            case .home:
                MediaHomeView(
                    isActive: isActive, refreshToken: refreshToken, downloads: downloads,
                    play: { playing = $0 }, open: { detailRoute = $0 }, openDownloads: { showingDownloads = true })
            case .movies:
                MediaGridView(kind: "movie", refreshToken: refreshToken)
            case .shows:
                MediaGridView(kind: "show", refreshToken: refreshToken)
            case .search:
                MediaSearchView(downloads: downloads) { _ in showingDownloads = true }
            }
        }
        .safeAreaInset(edge: .top, spacing: 0) {
            Picker("Show", selection: $pane) {
                ForEach(Pane.allCases) { Text($0.title).tag($0) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .frame(maxWidth: 520)
            .padding(.horizontal)
            .padding(.vertical, 8)
            .frame(maxWidth: .infinity)
            .background(.bar)
        }
        .navigationTitle("Movies & TV")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .navigationDestination(item: $detailRoute) { route in
            MovieDetailView(route: route)
        }
        .navigationDestination(isPresented: $showingDownloads) {
            MediaDownloadsView(downloads: downloads)
        }
        .toolbar {
            if downloads.canDownload {
                // The VPN light: green when it is proven to hide the home address, amber when
                // it is turned off in Settings. Opens Downloads, where Test is.
                if downloads.serviceStatus != nil {
                    ToolbarItem(placement: .primaryAction) {
                        MediaVpnLight(downloads: downloads) { showingDownloads = true }
                    }
                }
                ToolbarItem(placement: .primaryAction) {
                    Button {
                        showingDownloads = true
                    } label: {
                        Label("Downloads", systemImage: "arrow.down.circle")
                    }
                    .help("Downloads in progress and recent ones")
                }
                ToolbarItem(placement: .primaryAction) {
                    Button {
                        addingDownload = true
                    } label: {
                        Label("Add", systemImage: "plus")
                    }
                    .help("Find a movie or TV show and download it into the library")
                }
            }
        }
        .mediaAddSheet(isPresented: $addingDownload, downloads: downloads) { _ in
            showingDownloads = true
        }
        .task(id: DownloadsKey(active: isActive, server: model.activeServerURL)) {
            guard isActive, !model.activeServerURL.isEmpty else { return }
            await downloads.run(client: model.client)
        }
        .onChange(of: model.activeServerURL) { _, _ in downloads.reset() }
        .onChange(of: downloads.landed) { _, _ in refreshToken += 1 }
        .onReceive(NotificationCenter.default.publisher(for: .mediaItemDeleted)) { _ in refreshToken += 1 }
        .moviePlayer($playing, client: model.client) { refreshToken += 1 }
    }

    private struct DownloadsKey: Hashable {
        var active: Bool
        var server: String
    }
}

// Tile sizes by width class: an iPhone shows three posters across, an iPad or Mac more.
struct MediaLayout {
    let compact: Bool
    var posterWidth: CGFloat { compact ? 112 : 150 }
    var landscapeWidth: CGFloat { compact ? 250 : 320 }
    var spacing: CGFloat { compact ? 12 : 20 }
    var gridColumns: [GridItem] {
        compact
            ? Array(repeating: GridItem(.flexible(), spacing: 12, alignment: .top), count: 3)
            : [GridItem(.adaptive(minimum: 160, maximum: 200), spacing: 20, alignment: .top)]
    }
}

private struct MediaLayoutKey: EnvironmentKey {
    static let defaultValue = MediaLayout(compact: false)
}

extension EnvironmentValues {
    var mediaLayout: MediaLayout {
        get { self[MediaLayoutKey.self] }
        set { self[MediaLayoutKey.self] = newValue }
    }
}

// MARK: - Home

private struct MediaHomeView: View {
    let isActive: Bool
    let refreshToken: Int
    @ObservedObject var downloads: MediaDownloadsTracker
    let play: (MoviePlayRequest) -> Void
    let open: (MediaRoute) -> Void
    let openDownloads: () -> Void
    @EnvironmentObject private var model: AppModel
    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
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

    private var layout: MediaLayout { MediaLayout(compact: horizontalSizeClass == .compact) }

    var body: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 28) {
                MediaDownloadsBanner(downloads: downloads, open: openDownloads)
                if let featured = home?.featured {
                    MediaHeroView(item: featured)
                        .padding(.horizontal)
                }
                if let items = home?.continueWatching, !items.isEmpty {
                    MediaShelf(title: "Continue Watching") {
                        ForEach(items) { item in
                            MediaLandscapeTile(item: item, title: item.headline, subtitle: item.subheadline ?? item.remainingLabel) {
                                play(.resume(item))
                            }
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
                    MediaShelf(title: "New Episodes") {
                        ForEach(items) { item in
                            NavigationLink {
                                MovieDetailView(route: item.route)
                            } label: {
                                MediaLandscapeArt(item: item, title: item.headline, subtitle: item.subheadline)
                            }
                            .buttonStyle(.plain)
                            .contextMenu {
                                Button("Play", systemImage: "play.fill") { play(.resume(item)) }
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
            .padding(.vertical)
        }
        .environment(\.mediaLayout, layout)
        .refreshable { await load() }
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button {
                    Task { await surpriseMe() }
                } label: {
                    Label("Surprise Me", systemImage: "shuffle")
                }
                .disabled(surprising)
                .help("Open a movie or show picked at random")
            }
        }
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
            MediaShelf(title: title) {
                ForEach(items) { MediaPosterTile(item: $0).frame(width: layout.posterWidth) }
            }
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
}

// The featured title: its backdrop with the title over the bottom of it.
private struct MediaHeroView: View {
    let item: MediaItemSummary
    @EnvironmentObject private var model: AppModel

    var body: some View {
        NavigationLink {
            MovieDetailView(route: item.route)
        } label: {
            PosterImage(url: model.client.mediaURL(item.backdropUrl), title: item.title, maxPixel: 1600)
                .aspectRatio(16 / 9, contentMode: .fit)
                .frame(maxWidth: 900)
                .overlay {
                    LinearGradient(colors: [.black.opacity(0.85), .clear], startPoint: .bottom, endPoint: .center)
                }
                .overlay(alignment: .bottomLeading) {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(item.title)
                            .font(.title.bold())
                            .lineLimit(2)
                        HStack(spacing: 8) {
                            Text(item.metaLine)
                            if let certification = item.certification, !certification.isEmpty {
                                CertificationBadge(text: certification)
                            }
                        }
                        .font(.caption)
                        .foregroundStyle(.white.opacity(0.8))
                    }
                    .foregroundStyle(.white)
                    .padding()
                }
                .clipShape(RoundedRectangle(cornerRadius: 14))
        }
        .buttonStyle(.plain)
        .environment(\.colorScheme, .dark)
    }
}

// A titled row that scrolls sideways.
struct MediaShelf<Content: View>: View {
    let title: String
    @ViewBuilder let content: Content
    @Environment(\.mediaLayout) private var layout

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title)
                .font(.title3.bold())
                .padding(.horizontal)
            ScrollView(.horizontal, showsIndicators: false) {
                LazyHStack(alignment: .top, spacing: layout.spacing) {
                    content
                }
                .padding(.horizontal)
            }
        }
    }
}

// MARK: - Tiles

// A 2:3 poster that opens its detail page, with the title and year under it. Sized by its parent:
// a fixed width on a shelf, a grid column in a grid.
struct MediaPosterTile: View {
    let item: MediaItemSummary
    @EnvironmentObject private var model: AppModel

    var body: some View {
        NavigationLink {
            MovieDetailView(route: item.route)
        } label: {
            VStack(alignment: .leading, spacing: 6) {
                PosterImage(url: model.client.mediaURL(item.posterUrl), title: item.title, subtitle: item.yearLabel, maxPixel: 450)
                    .aspectRatio(2 / 3, contentMode: .fit)
                    .overlay(alignment: .topTrailing) { MediaTileBadge(item: item) }
                    .overlay(alignment: .bottom) { MediaProgressBar(fraction: item.progressFraction) }
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                VStack(alignment: .leading, spacing: 1) {
                    Text(item.title).font(.caption.weight(.medium)).lineLimit(1)
                    Text(item.yearLabel ?? " ").font(.caption2).foregroundStyle(.secondary)
                }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .combine)
    }
}

// A 16:9 tile that does something when tapped (plays, on Continue Watching).
struct MediaLandscapeTile: View {
    let item: MediaItemSummary
    let title: String
    let subtitle: String?
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            MediaLandscapeArt(item: item, title: title, subtitle: subtitle)
        }
        .buttonStyle(.plain)
    }
}

// The picture and captions of a 16:9 tile: an episode's still or a film's backdrop.
struct MediaLandscapeArt: View {
    let item: MediaItemSummary
    let title: String
    let subtitle: String?
    var width: CGFloat?
    @EnvironmentObject private var model: AppModel
    @Environment(\.mediaLayout) private var layout

    var body: some View {
        let width = width ?? layout.landscapeWidth
        VStack(alignment: .leading, spacing: 6) {
            PosterImage(url: model.client.mediaURL(item.landscapeArtPath), title: title, subtitle: subtitle, maxPixel: 720)
                .frame(width: width, height: width * 9 / 16)
                .overlay(alignment: .topTrailing) { MediaTileBadge(item: item) }
                .overlay(alignment: .bottom) { MediaProgressBar(fraction: item.progressFraction) }
                .clipShape(RoundedRectangle(cornerRadius: 8))
                .overlay(alignment: .center) {
                    Image(systemName: "play.circle.fill")
                        .font(.largeTitle)
                        .foregroundStyle(.white.opacity(0.85))
                        .shadow(radius: 6)
                        .opacity(item.canResume ? 1 : 0)
                }
            VStack(alignment: .leading, spacing: 1) {
                Text(title).font(.caption.weight(.medium)).lineLimit(1)
                if let subtitle {
                    Text(subtitle).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                }
            }
            .frame(width: width, alignment: .leading)
        }
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
    }
}

// Top-right corner of a tile: a check once watched, or how many episodes of a show are left.
struct MediaTileBadge: View {
    let item: MediaItemSummary

    var body: some View {
        if item.isWatched {
            Image(systemName: "checkmark.circle.fill")
                .font(.body)
                .foregroundStyle(.white, SentinelTheme.green)
                .shadow(radius: 3)
                .padding(5)
                .accessibilityLabel("Watched")
        } else if let remaining = item.unwatchedEpisodeCount {
            Text("\(remaining)")
                .font(.caption2.bold())
                .foregroundStyle(.black)
                .padding(.horizontal, 7)
                .padding(.vertical, 2)
                .background(SentinelTheme.green, in: Capsule())
                .padding(5)
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
                    Capsule().fill(SentinelTheme.green).frame(width: max(4, geometry.size.width * fraction))
                }
            }
            .frame(height: 4)
            .padding(.horizontal, 6)
            .padding(.bottom, 6)
            .accessibilityLabel("\(Int(fraction * 100)) percent watched")
        }
    }
}

struct CertificationBadge: View {
    let text: String

    var body: some View {
        Text(text)
            .font(.caption2.bold())
            .padding(.horizontal, 5)
            .padding(.vertical, 1)
            .overlay { RoundedRectangle(cornerRadius: 3).stroke(.secondary, lineWidth: 1) }
    }
}

// MARK: - Grids

private struct MediaGridView: View {
    let kind: String
    let refreshToken: Int
    @EnvironmentObject private var model: AppModel
    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
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
    private var layout: MediaLayout { MediaLayout(compact: horizontalSizeClass == .compact) }

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
            VStack(alignment: .leading, spacing: 12) {
                if total > 0 {
                    Text(summary)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                LazyVGrid(columns: layout.gridColumns, alignment: .leading, spacing: 18) {
                    ForEach(items) { item in
                        MediaPosterTile(item: item)
                            .onAppear {
                                if item.id == items.last?.id { Task { await loadMore() } }
                            }
                    }
                }
                if loadingMore {
                    ProgressView().frame(maxWidth: .infinity)
                }
            }
            .padding()
        }
        .environment(\.mediaLayout, layout)
        .refreshable { await reload() }
        .toolbar {
            ToolbarItem(placement: .primaryAction) { filterMenu }
        }
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

    private var summary: String {
        var parts = [kind == "show" ? "\(total) shows" : "\(total) movies"]
        if let genre { parts.append(genre) }
        if unwatchedOnly { parts.append("unwatched") }
        if myListOnly { parts.append("on My List") }
        parts.append("by \(sort.title.lowercased())")
        return parts.joined(separator: " · ")
    }

    private var filterMenu: some View {
        Menu {
            Picker("Sort By", selection: $sort) {
                ForEach(MediaSort.allCases) { Text($0.title).tag($0) }
            }
            Picker("Genre", selection: $genre) {
                Text("All Genres").tag(String?.none)
                ForEach(genres, id: \.self) { Text($0).tag(Optional($0)) }
            }
            .pickerStyle(.menu)
            Toggle("Unwatched Only", isOn: $unwatchedOnly)
            Toggle("My List Only", isOn: $myListOnly)
        } label: {
            Label("Sort and Filter", systemImage: "line.3.horizontal.decrease.circle")
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

private struct MediaSearchView: View {
    @ObservedObject var downloads: MediaDownloadsTracker
    let onStarted: (MediaDownload) -> Void
    @EnvironmentObject private var model: AppModel
    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
    @State private var query = ""
    @State private var results: [MediaItemSummary] = []
    // The text `results` answer, so the download offer never shows for text still being typed.
    @State private var resultsQuery = ""
    @State private var searching = false
    @State private var addStart: MediaAddStart?

    private var trimmed: String { query.trimmingCharacters(in: .whitespaces) }

    // Nothing in the library for what was typed: offer to download it instead.
    private var offersDownload: Bool {
        downloads.canDownload && !searching && results.isEmpty && trimmed.count >= 2 && resultsQuery == trimmed
    }

    var body: some View {
        let layout = MediaLayout(compact: horizontalSizeClass == .compact)
        ScrollView {
            if offersDownload {
                MediaSearchOffer(query: trimmed, tmdbConfigured: downloads.status?.tmdbConfigured != false) { addStart = $0 }
                    .padding()
            }
            LazyVGrid(columns: layout.gridColumns, alignment: .leading, spacing: 18) {
                ForEach(results) { MediaPosterTile(item: $0) }
            }
            .padding()
        }
        .environment(\.mediaLayout, layout)
        .searchable(text: $query, prompt: "Movies and TV shows")
        .overlay {
            if searching, results.isEmpty {
                ProgressView()
            } else if trimmed.isEmpty {
                ContentUnavailableView("Search the library", systemImage: "magnifyingglass", description: Text("Find a movie or show by its title."))
            } else if results.isEmpty, !offersDownload {
                ContentUnavailableView.search(text: trimmed)
            }
        }
        .mediaAddSheet(start: $addStart, downloads: downloads, onStarted: onStarted)
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
