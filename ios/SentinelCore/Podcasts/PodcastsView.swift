import SwiftUI
import UniformTypeIdentifiers

// The Podcasts section (§5-6): Listen Now (Up Next, continue, new, short ones, downloaded,
// bookmarks), the Library of followed shows, and Discover (the Apple directory, top charts by
// genre, recommendations, add by URL, OPML).
struct PodcastsView: View {
    enum Pane: String, CaseIterable, Identifiable {
        case listenNow, library, discover
        var id: Self { self }
        var title: String {
            switch self {
            case .listenNow: "Listen Now"
            case .library: "Library"
            case .discover: "Discover"
            }
        }
    }

    let isActive: Bool
    @AppStorage("podcasts.pane") private var pane: Pane = .listenNow
    @State private var showingStats = false
    @State private var showingFill = false
    private var router: AudioRouter { AudioRouter.shared }

    var body: some View {
        Group {
            switch pane {
            case .listenNow: PodcastListenNowView(isActive: isActive, showFill: { showingFill = true })
            case .library: PodcastLibraryView()
            case .discover: PodcastDiscoverView()
            }
        }
        .safeAreaInset(edge: .top, spacing: 0) {
            VStack(spacing: 0) {
                Picker("Show", selection: $pane) {
                    ForEach(Pane.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(maxWidth: 460)
                .padding(.horizontal)
                .padding(.vertical, 8)
                .frame(maxWidth: .infinity)
                ContinueListeningBanner(kind: "podcast")
            }
            .background(.bar)
        }
        .navigationTitle("Podcasts")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItemGroup(placement: .primaryAction) {
                Button {
                    AudioPlayer.shared.playBriefing()
                } label: {
                    Label("Morning Briefing", systemImage: "sun.horizon")
                }
                .help("Hear today's weather, calendar and reminders, then the news episodes")
                Menu {
                    Button("Fill My Drive…", systemImage: "car") { showingFill = true }
                    NavigationLink { PodcastUpNextView() } label: { Label("Up Next", systemImage: "text.line.first.and.arrowtriangle.forward") }
                    NavigationLink { PodcastBookmarksView() } label: { Label("Bookmarks", systemImage: "bookmark") }
                    NavigationLink { PodcastClipsView() } label: { Label("Clips", systemImage: "scissors") }
                    NavigationLink { TranscriptSearchView() } label: { Label("Search Transcripts", systemImage: "text.magnifyingglass") }
                    Divider()
                    Button("Listening Stats", systemImage: "chart.bar.xaxis") { showingStats = true }
                    NavigationLink { AudioDownloadsView() } label: { Label("Downloaded on This Device", systemImage: "arrow.down.circle") }
                    NavigationLink { WakeAlarmsView() } label: { Label("Wake-Up Alarms", systemImage: "alarm") }
                } label: {
                    Label("More", systemImage: "ellipsis.circle")
                }
            }
        }
        .navigationDestination(item: Binding(get: { router.podcastRoute }, set: { router.podcastRoute = $0 })) { route in
            switch route {
            case .show(let id): PodcastShowView(podcastId: id)
            case .episode(let id): PodcastEpisodeView(episodeId: id)
            }
        }
        .sheet(isPresented: $showingStats) {
            NavigationStack { AudioStatsView() }
                #if os(macOS)
                .frame(minWidth: 720, minHeight: 640)
                #endif
        }
        .sheet(isPresented: $showingFill) { FillMyDriveView() }
    }
}

// MARK: - Listen Now

private struct PodcastListenNowView: View {
    let isActive: Bool
    let showFill: () -> Void
    @EnvironmentObject private var model: AppModel
    @State private var home: PodcastHome?
    @State private var loading = false
    @State private var errorMessage: String?
    @State private var token = 0

    var body: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 28) {
                if let home {
                    quickActions(home)
                    episodeShelf("Up Next", home.upNext, inUpNext: true) { PodcastUpNextView() }
                    episodeShelf("Continue Listening", home.continueListening)
                    if let new = home.newEpisodes, !new.isEmpty {
                        VStack(alignment: .leading, spacing: 6) {
                            HStack {
                                Text("New Episodes").font(.title3.bold())
                                Spacer()
                                NavigationLink("See All") {
                                    EpisodeListScreen(title: "New Episodes", episodes: new)
                                }
                            }
                            .padding(.horizontal)
                            ForEach(new.prefix(6)) { episode in
                                EpisodeRow(episode: episode, showShow: true, list: new)
                                    .padding(.horizontal)
                                Divider().padding(.leading)
                            }
                        }
                    }
                    episodeShelf("Short Ones", home.shortOnes, subtitle: "Under 20 minutes")
                    episodeShelf("Downloaded", home.downloaded, subtitle: "Ready on the server")
                    if let shows = home.subscriptions, !shows.isEmpty {
                        AudioShelf(title: "Your Shows") {
                            PodcastLibraryView()
                        } content: {
                            ForEach(shows) { PodcastTile(podcast: $0, width: 130) }
                        }
                    }
                    if let bookmarks = home.bookmarks, !bookmarks.isEmpty {
                        VStack(alignment: .leading, spacing: 6) {
                            HStack {
                                Text("Bookmarks").font(.title3.bold())
                                Spacer()
                                NavigationLink("See All") { PodcastBookmarksView() }
                            }
                            .padding(.horizontal)
                            ForEach(bookmarks.prefix(4)) { bookmark in
                                BookmarkRow(bookmark: bookmark).padding(.horizontal)
                                Divider().padding(.leading)
                            }
                        }
                    }
                }
            }
            .padding(.vertical)
        }
        .overlay {
            if home == nil {
                AudioLoadState(loading: loading, error: errorMessage, retry: { token += 1 })
            } else if let home, (home.subscriptions ?? []).isEmpty, (home.upNext ?? []).isEmpty {
                ContentUnavailableView("Follow a show to get started", systemImage: "mic",
                                       description: Text("Find shows in Discover, add one by its feed address, or import an OPML file from another podcast app."))
            }
        }
        .refreshable { await load() }
        .task(id: "\(isActive)|\(model.activeServerURL)|\(token)") {
            guard isActive, !model.activeServerURL.isEmpty else { return }
            await load()
        }
        .onReceive(NotificationCenter.default.publisher(for: .podcastsChanged)) { _ in token += 1 }
    }

    private func quickActions(_ home: PodcastHome) -> some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 10) {
                actionChip("Morning Briefing", "sun.horizon.fill", .orange) { AudioPlayer.shared.playBriefing() }
                actionChip("Fill My Drive", "car.fill", .blue, action: showFill)
                if let upNext = home.upNext, !upNext.isEmpty {
                    actionChip("Play Up Next", "play.fill", SentinelTheme.green) {
                        AudioPlayer.shared.play(episode: upNext[0], queue: Array(upNext.dropFirst()),
                                                context: AudioPlayContext(type: "queue", id: "up-next", name: "Up Next"))
                    }
                }
                if let saved = home.timeSavedSeconds, saved >= 60 {
                    Label("\(AudioFormat.duration(saved, style: .short)) saved", systemImage: "hare.fill")
                        .font(.subheadline.weight(.semibold))
                        .padding(.horizontal, 12).padding(.vertical, 8)
                        .background(.quaternary, in: Capsule())
                        .help("What faster playback, trimmed silences and skipped intros have saved you")
                }
            }
            .padding(.horizontal)
        }
    }

    private func actionChip(_ title: String, _ icon: String, _ color: Color, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Label(title, systemImage: icon)
                .font(.subheadline.weight(.semibold))
                .padding(.horizontal, 14).padding(.vertical, 8)
                .background(color.opacity(0.18), in: Capsule())
                .foregroundStyle(color)
        }
        .buttonStyle(.plain)
    }

    @ViewBuilder
    private func episodeShelf(_ title: String, _ episodes: [PodcastEpisode]?, subtitle: String? = nil, inUpNext: Bool = false) -> some View {
        if let episodes, !episodes.isEmpty {
            AudioShelf(title: title, subtitle: subtitle) {
                EpisodeListScreen(title: title, episodes: episodes)
            } content: {
                ForEach(episodes) { EpisodeCard(episode: $0, list: episodes, width: 200, inUpNext: inUpNext) }
            }
        }
    }

    private func episodeShelf<D: View>(_ title: String, _ episodes: [PodcastEpisode]?, inUpNext: Bool, @ViewBuilder destination: @escaping () -> D) -> some View {
        Group {
            if let episodes, !episodes.isEmpty {
                AudioShelf(title: title, destination: destination) {
                    ForEach(episodes) { EpisodeCard(episode: $0, list: episodes, width: 200, inUpNext: inUpNext) }
                }
            }
        }
    }

    private func load() async {
        loading = home == nil
        defer { loading = false }
        do {
            home = try await model.client.podcastHome()
            errorMessage = nil
        } catch {
            if !error.isCancellation { errorMessage = error.localizedDescription }
        }
    }
}

struct EpisodeListScreen: View {
    let title: String
    let episodes: [PodcastEpisode]

    var body: some View {
        List {
            ForEach(episodes) { EpisodeRow(episode: $0, showShow: true, list: episodes) }
        }
        .listStyle(.plain)
        .navigationTitle(title)
    }
}

// MARK: - Library

struct PodcastLibraryView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
    @State private var podcasts: [Podcast] = []
    @State private var loading = true
    @State private var errorMessage: String?
    @AppStorage("podcasts.showAll") private var showAll = false
    @AppStorage("podcasts.librarySort") private var sort = "updated"
    @State private var addingURL = false
    @State private var importing = false
    @State private var exportFile: URL?
    @State private var notice: String?

    private var sorted: [Podcast] {
        switch sort {
        case "title": podcasts.sorted { $0.title.localizedCaseInsensitiveCompare($1.title) == .orderedAscending }
        case "unplayed": podcasts.sorted { ($0.unplayedCount ?? 0) > ($1.unplayedCount ?? 0) }
        default: podcasts
        }
    }

    var body: some View {
        ScrollView {
            if let notice {
                Text(notice).font(.footnote).foregroundStyle(.secondary).padding(.horizontal).frame(maxWidth: .infinity, alignment: .leading)
            }
            LazyVGrid(columns: [GridItem(.adaptive(minimum: horizontalSizeClass == .compact ? 105 : 150), spacing: 16, alignment: .top)], spacing: 20) {
                ForEach(sorted) { podcast in
                    PodcastTile(podcast: podcast, width: nil)
                        .contextMenu {
                            Button("Refresh", systemImage: "arrow.clockwise") {
                                Task { _ = try? await model.client.refreshPodcast(id: podcast.id); await load() }
                            }
                            Button("Mark All Played", systemImage: "checkmark.circle") {
                                Task { try? await model.client.markPodcastPlayed(id: podcast.id, played: true); await load() }
                            }
                            if podcast.isSubscribed {
                                Button("Unfollow", systemImage: "minus.circle", role: .destructive) {
                                    Task { try? await model.client.unsubscribe(podcastId: podcast.id); await load() }
                                }
                            }
                        }
                }
            }
            .padding()
        }
        .overlay {
            if podcasts.isEmpty {
                AudioLoadState(loading: loading, error: errorMessage,
                               empty: loading ? nil : ("No shows yet", "mic", "Find shows in Discover, add one by its feed address, or import OPML."))
            }
        }
        .refreshable {
            try? await model.client.refreshAllPodcasts()
            await load()
        }
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Menu {
                    Picker("Sort", selection: $sort) {
                        Text("Recently Updated").tag("updated")
                        Text("Title").tag("title")
                        Text("Most Unplayed").tag("unplayed")
                    }
                    Toggle("Every Show in the Household", isOn: $showAll)
                    Divider()
                    Button("Add a Show by URL…", systemImage: "link") { addingURL = true }
                    Button("Import OPML…", systemImage: "square.and.arrow.down") { importing = true }
                    Button("Export OPML", systemImage: "square.and.arrow.up") { Task { await exportOPML() } }
                    Button("Refresh All Shows", systemImage: "arrow.clockwise") {
                        Task {
                            try? await model.client.refreshAllPodcasts()
                            notice = "Checking every show for new episodes."
                        }
                    }
                } label: {
                    Label("Library Options", systemImage: "line.3.horizontal.decrease.circle")
                }
            }
        }
        .sheet(isPresented: $addingURL) { AddPodcastByURLView { _ in Task { await load() } } }
        .sheet(item: Binding(get: { exportFile.map(AudioSharedFile.init) }, set: { if $0 == nil { exportFile = nil } })) { file in
            OPMLExportSheet(file: file.url)
        }
        .fileImporter(isPresented: $importing, allowedContentTypes: OPMLTypes.importable) { result in
            Task { await importOPML(result) }
        }
        .task(id: showAll) { await load() }
        .onReceive(NotificationCenter.default.publisher(for: .podcastsChanged)) { _ in Task { await load() } }
    }

    private func load() async {
        do {
            podcasts = try await model.client.podcasts(all: showAll)
            errorMessage = nil
        } catch {
            if !error.isCancellation { errorMessage = error.localizedDescription }
        }
        loading = false
    }

    private func importOPML(_ result: Result<URL, Error>) async {
        do {
            let url = try result.get()
            let access = url.startAccessingSecurityScopedResource()
            defer { if access { url.stopAccessingSecurityScopedResource() } }
            let data = try Data(contentsOf: url)
            let reply = try await model.client.importOPML(data)
            var text = "Added \(reply.added ?? 0) show\(reply.added == 1 ? "" : "s")"
            if let existing = reply.existing, existing > 0 { text += ", \(existing) you already had" }
            if let failed = reply.failed, !failed.isEmpty { text += ", \(failed.count) couldn't be read" }
            notice = text + ". New shows appear as their feeds are fetched."
            try? await Task.sleep(for: .seconds(2))
            await load()
        } catch {
            notice = error.localizedDescription
        }
    }

    private func exportOPML() async {
        do {
            let data = try await model.client.exportOPML()
            let file = FileManager.default.temporaryDirectory.appendingPathComponent("Sentinel Core Podcasts.opml")
            try data.write(to: file, options: .atomic)
            exportFile = file
        } catch {
            notice = error.localizedDescription
        }
    }
}

struct AudioSharedFile: Identifiable {
    let url: URL
    var id: String { url.path }
}

enum OPMLTypes {
    static var importable: [UTType] {
        [UTType(filenameExtension: "opml"), .xml, .plainText].compactMap { $0 }
    }
}

struct OPMLExportSheet: View {
    let file: URL
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            VStack(spacing: 18) {
                Image(systemName: "doc.text").font(.system(size: 54)).foregroundStyle(SentinelTheme.green)
                Text("Your shows as OPML").font(.headline)
                Text("Open this file in another podcast app to bring your subscriptions across.")
                    .multilineTextAlignment(.center).foregroundStyle(.secondary)
                ShareLink(item: file) { Label("Share or Save…", systemImage: "square.and.arrow.up") }
                    .buttonStyle(.borderedProminent).foregroundStyle(Color.onSentinelGreen)
                    .tint(SentinelTheme.green)
            }
            .padding(30)
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } } }
        }
        #if os(macOS)
        .frame(minWidth: 420, minHeight: 320)
        #endif
    }
}

// MARK: - Fill my drive

struct FillMyDriveView: View {
    @Environment(\.dismiss) private var dismiss
    @AppStorage("podcasts.fillMinutes") private var minutes = 30.0
    @State private var fill: PodcastFill?
    @State private var loading = false
    @State private var errorMessage: String?

    var body: some View {
        NavigationStack {
            List {
                Section {
                    VStack(alignment: .leading) {
                        Text("I have \(AudioFormat.duration(minutes * 60, style: .short))").font(.title3.bold())
                        Slider(value: $minutes, in: 10...240, step: 5) { editing in
                            if !editing { Task { await load() } }
                        }
                    }
                } footer: {
                    Text("Unplayed and in-progress episodes from your shows - Up Next first, then the newest - that fit in the time, counting only what's left of each.")
                }
                if loading { ProgressView().frame(maxWidth: .infinity) }
                if let errorMessage { Text(errorMessage).foregroundStyle(SentinelTheme.red) }
                if let fill {
                    Section {
                        ForEach(fill.episodes) { EpisodeRow(episode: $0, showShow: true, list: fill.episodes) }
                    } header: {
                        Text("\(fill.episodes.count) episode\(fill.episodes.count == 1 ? "" : "s") · \(AudioFormat.duration(fill.totalSeconds ?? 0, style: .short))")
                    }
                }
            }
            .navigationTitle("Fill My Drive")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Close") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Play All") {
                        guard let episodes = fill?.episodes, let first = episodes.first else { return }
                        AudioPlayer.shared.play(episode: first, queue: Array(episodes.dropFirst()),
                                                context: AudioPlayContext(type: "queue", id: "fill", name: "Fill My Drive"))
                        dismiss()
                    }
                    .disabled(fill?.episodes.isEmpty ?? true)
                }
            }
            .task { await load() }
        }
        #if os(macOS)
        .frame(minWidth: 520, minHeight: 560)
        #endif
    }

    private func load() async {
        loading = true
        defer { loading = false }
        do {
            fill = try await AppModel.shared.client.podcastFill(minutes: Int(minutes))
            errorMessage = nil
        } catch {
            if !error.isCancellation { errorMessage = error.localizedDescription }
        }
    }
}
