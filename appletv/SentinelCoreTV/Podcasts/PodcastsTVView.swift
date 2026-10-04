import SwiftUI

// Podcasts on the television (core/docs/MUSIC_AND_PODCASTS.md §5-6): Listen Now, Up Next, the
// shows you follow, Discover (search the directory, top charts, look before you follow), and
// bookmarks - plus the Morning Briefing, read out and then played.
struct PodcastsTVView: View {
    enum Pane: String, CaseIterable, Identifiable {
        case listenNow, upNext, library, discover, bookmarks
        var id: Self { self }
        var title: String {
            switch self {
            case .listenNow: "Listen Now"
            case .upNext: "Up Next"
            case .library: "Library"
            case .discover: "Discover"
            case .bookmarks: "Bookmarks"
            }
        }
    }

    let isActive: Bool
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var audio: AudioCenter
    @State private var pane: Pane = .listenNow

    var body: some View {
        Group {
            switch pane {
            case .listenNow: ListenNowTV(isActive: isActive)
            case .upNext: UpNextTV()
            case .library: PodcastLibraryTV()
            case .discover: DiscoverTVView()
            case .bookmarks: BookmarksTVView()
            }
        }
        .tabHeader(spacing: 20) {
            VStack(alignment: .leading, spacing: 20) {
                Picker("Show", selection: $pane) {
                    ForEach(Pane.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                .frame(maxWidth: 1200)
                .padding(.horizontal, 60)

                AudioTopBar()
            }
        }
        .modifier(AudioTabRoot(isActive: isActive))
        .task(id: "\(isActive)|\(model.activeServerURL)") {
            guard isActive else { return }
            await audio.refreshContinueOffer()
        }
        #if DEBUG
        // `-debugPodcastsPane upNext|library|discover|bookmarks`, `-debugPodcast <id>`,
        // `-debugEpisode <id>`, `-debugPlayEpisode <id>` (plays it, opens Now Playing),
        // `-debugBriefing YES`.
        .task(id: model.activeServerURL) {
            guard !model.activeServerURL.isEmpty else { return }
            while audio.api == nil { try? await Task.sleep(for: .milliseconds(300)) }
            let defaults = UserDefaults.standard
            if let raw = defaults.string(forKey: "debugPodcastsPane"), let picked = Pane(rawValue: raw) { pane = picked }
            try? await Task.sleep(for: .milliseconds(600))
            if let id = defaults.string(forKey: "debugPodcast") { AudioNavigator.shared.open(.podcast(id)) }
            if let id = defaults.string(forKey: "debugEpisode") { AudioNavigator.shared.open(.episode(id)) }
            if let id = defaults.string(forKey: "debugPlayEpisode"), let episode = try? await audio.api?.episode(id: id) {
                audio.play(episodes: [episode], context: PlayContext(type: "podcast", id: episode.podcastId, name: episode.podcastTitle))
            }
            if defaults.bool(forKey: "debugBriefing") { try? await audio.playBriefing() }
        }
        #endif
    }
}

// MARK: - Listen Now

private struct ListenNowTV: View {
    let isActive: Bool
    @EnvironmentObject private var audio: AudioCenter
    @State private var home: PodcastHome?
    @State private var loading = false
    @State private var errorMessage: String?
    @State private var actionError: String?

    var body: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 46) {
                HStack(spacing: 30) {
                    AudioButton("Morning Briefing", systemImage: "sun.horizon.fill") { run { try await audio.playBriefing() } }
                    AudioButton("Play On…", systemImage: "hifispeaker.2") { AudioNavigator.shared.open(.speakers) }
                    AudioButton("Your Stats", systemImage: "chart.bar.xaxis") { AudioNavigator.shared.open(.stats) }
                    AudioButton("Wake-up Alarms", systemImage: "alarm") { AudioNavigator.shared.open(.alarms) }
                    Spacer()
                    if let saved = home?.timeSavedSeconds, saved > 60 {
                        Label("\(AudioFormat.minutes(saved) ?? "") saved", systemImage: "hare").font(.caption).foregroundStyle(.secondary)
                    }
                }
                .padding(.horizontal, 60)
                if let home {
                    episodeShelf("Up Next", home.upNext, context: PlayContext(type: "queue", id: "upNext", name: "Up Next"))
                    episodeShelf("Continue Listening", home.continueListening)
                    episodeShelf("New Episodes", home.newEpisodes)
                    episodeShelf("Short on Time", home.shortOnes)
                    episodeShelf("Downloaded", home.downloaded)
                    if let shows = home.subscriptions, !shows.isEmpty {
                        MediaShelfTV(title: "Your Shows") { ForEach(shows) { PodcastTile(podcast: $0) } }
                    }
                    if let bookmarks = home.bookmarks, !bookmarks.isEmpty {
                        MediaShelfTV(title: "Bookmarks") {
                            ForEach(bookmarks) { bookmark in BookmarkCard(bookmark: bookmark) }
                        }
                    }
                }
            }
            .padding(.vertical, 20)
        }
        .scrollClipDisabled()
        .overlay {
            AudioStateOverlay(loading: loading, error: errorMessage, isEmpty: home == nil || home?.isEmpty == true,
                              emptyTitle: "No shows yet", emptyIcon: "antenna.radiowaves.left.and.right",
                              emptyMessage: "Find shows to follow under Discover.")
        }
        .alert("Podcasts", isPresented: Binding(get: { actionError != nil }, set: { if !$0 { actionError = nil } })) {
            Button("OK", role: .cancel) {}
        } message: { Text(actionError ?? "") }
        .task(id: isActive) {
            guard isActive, let api = audio.api else { return }
            loading = true
            defer { loading = false }
            do { home = try await api.podcastHome(); errorMessage = nil }
            catch { if !AudioAPI.isCancellation(error) { errorMessage = error.localizedDescription } }
        }
    }

    @ViewBuilder
    private func episodeShelf(_ title: String, _ episodes: [Episode]?, context: PlayContext? = nil) -> some View {
        if let episodes, !episodes.isEmpty {
            MediaShelfTV(title: title) {
                ForEach(Array(episodes.enumerated()), id: \.element.id) { offset, episode in
                    EpisodeCard(episode: episode) {
                        if let context {
                            audio.play(episodes: episodes, startAt: offset, context: context)
                        } else {
                            audio.play(episodes: [episode], context: PlayContext(type: "podcast", id: episode.podcastId, name: episode.podcastTitle))
                        }
                    }
                    .contextMenu { EpisodeMenu(episode: episode) }
                }
            }
        }
    }

    private func run(_ work: @escaping () async throws -> Void) {
        Task {
            do { try await work() } catch { if !AudioAPI.isCancellation(error) { actionError = error.localizedDescription } }
        }
    }
}

struct BookmarkCard: View {
    let bookmark: Bookmark
    @EnvironmentObject private var audio: AudioCenter

    var body: some View {
        Button {
            Task { await play() }
        } label: {
            HStack(alignment: .top, spacing: 16) {
                AudioArt(path: bookmark.artUrl, title: bookmark.podcastTitle ?? "", size: 200)
                    .frame(width: 110, height: 110)
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                VStack(alignment: .leading, spacing: 4) {
                    Text("\(bookmark.podcastTitle ?? "") · \(AudioFormat.clock(bookmark.positionSeconds))").font(.caption2).foregroundStyle(.secondary)
                    Text(bookmark.episodeTitle ?? "").font(.caption.weight(.semibold)).lineLimit(2)
                    Text(bookmark.note?.isEmpty == false ? bookmark.note! : (bookmark.transcriptSnippet ?? ""))
                        .font(.caption2).foregroundStyle(.secondary).lineLimit(2)
                }
            }
            .padding(16)
            .frame(width: 520, height: 150, alignment: .topLeading)
        }
        .buttonStyle(.card)
    }

    private func play() async {
        guard let episode = try? await audio.api?.episode(id: bookmark.episodeId) else { return }
        audio.play(episodes: [episode], position: bookmark.positionSeconds,
                   context: PlayContext(type: "podcast", id: episode.podcastId, name: episode.podcastTitle))
    }
}

// MARK: - Up Next

private struct UpNextTV: View {
    @EnvironmentObject private var audio: AudioCenter
    @State private var queue: [Episode] = []
    @State private var loading = true
    @State private var errorMessage: String?

    var body: some View {
        List {
            if !queue.isEmpty {
                Section {
                    AudioButton("Play Up Next", systemImage: "play.fill") { play(0) }
                }
            }
            Section {
                ForEach(Array(queue.enumerated()), id: \.element.id) { offset, episode in
                    EpisodeRow(episode: episode, play: { play(offset) }, showPodcast: true)
                        .contextMenu {
                            if offset > 0 {
                                Button("Move to Top", systemImage: "arrow.up.to.line") { Task { await move(episode, to: 0) } }
                            }
                            if offset < queue.count - 1 {
                                Button("Move Down", systemImage: "arrow.down") { Task { await move(episode, to: offset + 1) } }
                            }
                            Button("Remove from Up Next", systemImage: "minus.circle", role: .destructive) {
                                Task { if let updated = try? await audio.api?.dequeue(episodeId: episode.id) { queue = updated } }
                            }
                            EpisodeMenu(episode: episode)
                        }
                }
            } footer: {
                if !queue.isEmpty { Text("Long-press an episode to reorder or remove it. Finished episodes leave Up Next by themselves.") }
            }
        }
        .overlay {
            AudioStateOverlay(loading: loading, error: errorMessage, isEmpty: queue.isEmpty, emptyTitle: "Up Next is empty",
                              emptyIcon: "text.line.first.and.arrowtriangle.forward",
                              emptyMessage: "Long-press any episode and choose Add to Up Next.")
        }
        .task {
            defer { loading = false }
            do { queue = try await audio.api?.queue() ?? [] } catch { errorMessage = error.localizedDescription }
        }
    }

    private func play(_ index: Int) {
        audio.play(episodes: queue, startAt: index, context: PlayContext(type: "queue", id: "upNext", name: "Up Next"))
    }

    private func move(_ episode: Episode, to index: Int) async {
        var ids = queue.map(\.id)
        ids.removeAll { $0 == episode.id }
        ids.insert(episode.id, at: min(index, ids.count))
        if let updated = try? await audio.api?.reorderQueue(ids) { queue = updated }
    }
}

// MARK: - Library of shows

private struct PodcastLibraryTV: View {
    @EnvironmentObject private var audio: AudioCenter
    @State private var shows: [Podcast] = []
    @State private var loading = true
    @State private var errorMessage: String?
    private let columns = [GridItem(.adaptive(minimum: AudioTileSize.square, maximum: AudioTileSize.square + 30), spacing: 40)]

    var body: some View {
        ScrollView {
            LazyVGrid(columns: columns, alignment: .leading, spacing: 46) {
                ForEach(shows) { PodcastTile(podcast: $0) }
            }
            .padding(.horizontal, 60)
            .padding(.vertical, 20)
        }
        .scrollClipDisabled()
        .overlay {
            AudioStateOverlay(loading: loading, error: errorMessage, isEmpty: shows.isEmpty, emptyTitle: "No shows yet",
                              emptyIcon: "antenna.radiowaves.left.and.right", emptyMessage: "Find shows to follow under Discover.")
        }
        .task {
            defer { loading = false }
            do {
                shows = try await audio.api?.subscriptions() ?? []
                shows.forEach { audio.player.noteShowSettings($0) }
            } catch { errorMessage = error.localizedDescription }
        }
    }
}

// MARK: - Bookmarks

struct BookmarksTVView: View {
    @EnvironmentObject private var audio: AudioCenter
    @State private var bookmarks: [Bookmark] = []
    @State private var loading = true
    @State private var errorMessage: String?

    var body: some View {
        List {
            ForEach(bookmarks) { bookmark in
                Button {
                    Task {
                        guard let episode = try? await audio.api?.episode(id: bookmark.episodeId) else { return }
                        audio.play(episodes: [episode], position: bookmark.positionSeconds,
                                   context: PlayContext(type: "podcast", id: episode.podcastId, name: episode.podcastTitle))
                    }
                } label: {
                    HStack(alignment: .top, spacing: 20) {
                        AudioArt(path: bookmark.artUrl, title: bookmark.podcastTitle ?? "", size: 200)
                            .frame(width: 90, height: 90).clipShape(RoundedRectangle(cornerRadius: 8))
                        VStack(alignment: .leading, spacing: 4) {
                            Text("\(bookmark.podcastTitle ?? "") · \(AudioFormat.clock(bookmark.positionSeconds))").font(.caption).foregroundStyle(.secondary)
                            Text(bookmark.episodeTitle ?? "").lineLimit(1)
                            if let note = bookmark.note, !note.isEmpty { Text(note).font(.callout) }
                            if let snippet = bookmark.transcriptSnippet, !snippet.isEmpty {
                                Text("“\(snippet)”").font(.caption).italic().foregroundStyle(.secondary).lineLimit(3)
                            }
                        }
                    }
                }
                .contextMenu {
                    Button("Delete Bookmark", systemImage: "trash", role: .destructive) {
                        Task {
                            try? await audio.api?.deleteBookmark(id: bookmark.id)
                            bookmarks.removeAll { $0.id == bookmark.id }
                        }
                    }
                }
            }
        }
        .overlay {
            AudioStateOverlay(loading: loading, error: errorMessage, isEmpty: bookmarks.isEmpty, emptyTitle: "No bookmarks",
                              emptyIcon: "bookmark", emptyMessage: "Bookmark a moment from Now Playing while an episode plays.")
        }
        .task {
            defer { loading = false }
            do { bookmarks = try await audio.api?.bookmarks() ?? [] } catch { errorMessage = error.localizedDescription }
        }
    }
}
