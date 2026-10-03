import SwiftUI

// A show's page (episodes and "my settings"), an episode's page (description, chapters,
// transcript), Discover, and the look-before-you-follow preview.

private let speedChoices: [Double] = [0.8, 1.0, 1.1, 1.2, 1.25, 1.3, 1.5, 1.75, 2.0, 2.5, 3.0]
private let skipChoices: [Int] = [0, 10, 15, 20, 30, 45, 60, 90, 120]

// MARK: - Show

struct PodcastShowTVView: View {
    let podcastId: String
    @EnvironmentObject private var audio: AudioCenter
    @State private var podcast: Podcast?
    @State private var episodes: [Episode] = []
    @State private var total = 0
    @State private var seasons: [Int] = []
    @State private var season: Int?
    @State private var filter = "all"
    @State private var sort = "newest"
    @State private var errorMessage: String?
    @State private var busy = false
    @State private var expanded = false

    private static let filters: [(String, String)] = [("all", "All"), ("unplayed", "Unplayed"), ("inProgress", "In Progress"), ("downloaded", "Downloaded"), ("bookmarked", "Bookmarked")]

    var body: some View {
        List {
            if let podcast {
                Section {
                    HStack(alignment: .top, spacing: 40) {
                        AudioArt(path: podcast.artUrl ?? "/api/podcasts/\(podcast.id)/art", title: podcast.title, size: 600)
                            .frame(width: 300, height: 300)
                            .clipShape(RoundedRectangle(cornerRadius: 14))
                        VStack(alignment: .leading, spacing: 10) {
                            Text(podcast.title).font(.title2.bold())
                            Text([podcast.author, podcast.categories?.first, podcast.episodeCount.map { "\($0) episodes" }]
                                .compactMap { $0 }.joined(separator: " · "))
                                .font(.caption).foregroundStyle(.secondary)
                            if let description = podcast.description {
                                Text(description).font(.caption).foregroundStyle(.secondary).lineLimit(expanded ? nil : 4)
                            }
                            if let error = podcast.refreshError {
                                Label(error, systemImage: "exclamationmark.triangle").font(.caption2).foregroundStyle(SentinelTheme.amber)
                            }
                        }
                    }
                    .focusable()
                    .onTapGesture { expanded.toggle() }
                    TVButtonRow {
                        AudioButton("Play Latest", systemImage: "play.fill") { playLatest() }
                        if podcast.subscribed == true {
                            AudioButton("Unfollow", systemImage: "minus.circle") { Task { await setFollowing(false) } }
                        } else {
                            AudioButton("Follow", systemImage: "plus.circle") { Task { await setFollowing(true) } }
                        }
                        AudioButton("Mark All Played", systemImage: "checkmark.circle") {
                            Task {
                                try? await audio.api?.markAllPlayed(podcastId: podcastId)
                                await loadEpisodes()
                            }
                        }
                        AudioButton("Refresh", systemImage: "arrow.clockwise") {
                            Task {
                                busy = true
                                if let fresh = try? await audio.api?.refreshPodcast(id: podcastId) { self.podcast = fresh }
                                await loadEpisodes()
                                busy = false
                            }
                        }
                        if busy { ProgressView() }
                    }
                }
                if podcast.subscribed == true {
                    mySettingsSection(podcast)
                }
                Section {
                    TVButtonRow(spacing: 30) {
                        Menu {
                            Picker("Show", selection: $filter) { ForEach(Self.filters, id: \.0) { Text($0.1).tag($0.0) } }
                        } label: {
                            Label(Self.filters.first { $0.0 == filter }?.1 ?? "All", systemImage: "line.3.horizontal.decrease").audioFocusLabel()
                        }
                        Menu {
                            Picker("Order", selection: $sort) {
                                Text("Newest First").tag("newest")
                                Text("Oldest First").tag("oldest")
                            }
                        } label: {
                            Label(sort == "oldest" ? "Oldest First" : "Newest First", systemImage: "arrow.up.arrow.down").audioFocusLabel()
                        }
                        if seasons.count > 1 {
                            Menu {
                                Picker("Season", selection: $season) {
                                    Text("All Seasons").tag(Int?.none)
                                    ForEach(seasons, id: \.self) { Text("Season \($0)").tag(Optional($0)) }
                                }
                            } label: {
                                Label(season.map { "Season \($0)" } ?? "All Seasons", systemImage: "square.stack").audioFocusLabel()
                            }
                        }
                        Text("\(total) episodes").font(.caption).foregroundStyle(.secondary).padding(.leading, 20)
                    }
                    ForEach(Array(episodes.enumerated()), id: \.element.id) { offset, episode in
                        EpisodeRow(episode: episode) { play(from: offset) }
                            .onAppear { if episode.id == episodes.last?.id { Task { await loadMore() } } }
                    }
                } header: {
                    Text("Episodes")
                }
            }
        }
        .overlay { AudioStateOverlay(loading: podcast == nil && errorMessage == nil, error: errorMessage, isEmpty: podcast == nil) }
        .task(id: podcastId) {
            do {
                let show = try await audio.api?.podcast(id: podcastId)
                podcast = show
                if let show { audio.player.noteShowSettings(show); sort = show.mySettings?.sort ?? "newest" }
            } catch { errorMessage = error.localizedDescription }
        }
        .task(id: "\(filter)|\(sort)|\(season.map(String.init) ?? "")") { await loadEpisodes() }
    }

    private func mySettingsSection(_ podcast: Podcast) -> some View {
        let mine = podcast.mySettings ?? PodcastMySettings()
        return Section {
            Menu {
                Button("App Default (\(AudioFormat.speed(AudioPrefs.podcastSpeed)))") { update { $0.playbackSpeed = nil } }
                ForEach(speedChoices, id: \.self) { speed in
                    Button(AudioFormat.speed(speed)) { update { $0.playbackSpeed = speed } }
                }
            } label: {
                LabeledContent("Playback Speed", value: mine.playbackSpeed.map(AudioFormat.speed) ?? "Default").focusContrastForeground()
            }
            Menu {
                ForEach(skipChoices, id: \.self) { seconds in
                    Button(seconds == 0 ? "Off" : "\(seconds) seconds") { update { $0.skipIntroSeconds = seconds } }
                }
            } label: {
                LabeledContent("Skip Intro", value: (mine.skipIntroSeconds ?? 0) == 0 ? "Off" : "\(mine.skipIntroSeconds!) s").focusContrastForeground()
            }
            Menu {
                ForEach(skipChoices, id: \.self) { seconds in
                    Button(seconds == 0 ? "Off" : "\(seconds) seconds") { update { $0.skipOutroSeconds = seconds } }
                }
            } label: {
                LabeledContent("Skip Outro", value: (mine.skipOutroSeconds ?? 0) == 0 ? "Off" : "\(mine.skipOutroSeconds!) s").focusContrastForeground()
            }
            Toggle("In Morning Briefing", isOn: Binding(get: { mine.inBriefing == true }, set: { on in update { $0.inBriefing = on } }))
            Toggle("Add New Episodes to Up Next", isOn: Binding(get: { mine.autoQueue == true }, set: { on in update { $0.autoQueue = on } }))
            Toggle("Notify About New Episodes", isOn: Binding(get: { mine.notify != false }, set: { on in update { $0.notify = on } }))
        } header: {
            Text("My Settings")
        } footer: {
            Text("Just for you - everyone in the household has their own.")
        }
    }

    private func update(_ change: (inout PodcastMySettings) -> Void) {
        guard var podcast else { return }
        var settings = podcast.mySettings ?? PodcastMySettings()
        change(&settings)
        podcast.mySettings = settings
        self.podcast = podcast
        audio.player.noteShowSettings(podcast)
        Task {
            if let saved = try? await audio.api?.saveMySettings(podcastId: podcast.id, settings) {
                self.podcast = saved
                audio.player.noteShowSettings(saved)
            }
        }
    }

    private func loadEpisodes() async {
        guard let api = audio.api else { return }
        do {
            let page = try await api.episodes(podcastId: podcastId, sort: sort, filter: filter, season: season)
            episodes = page.items
            total = page.total ?? page.items.count
            if let found = page.seasons { seasons = found }
        } catch {
            if !AudioAPI.isCancellation(error) { errorMessage = error.localizedDescription }
        }
    }

    private func loadMore() async {
        guard let api = audio.api, episodes.count < total,
              let page = try? await api.episodes(podcastId: podcastId, sort: sort, filter: filter, season: season, offset: episodes.count) else { return }
        let known = Set(episodes.map(\.id))
        episodes += page.items.filter { !known.contains($0.id) }
    }

    // From the chosen episode on, in the order on screen (oldest-first plays a serial in order).
    private func play(from index: Int) {
        guard let podcast else { return }
        let list = sort == "oldest" ? Array(episodes[index...]) : [episodes[index]]
        audio.play(episodes: list, context: PlayContext(type: "podcast", id: podcast.id, name: podcast.title))
    }

    private func playLatest() {
        guard let podcast else { return }
        let latest = sort == "oldest" ? episodes.last : episodes.first
        if let latest {
            audio.play(episodes: [latest], context: PlayContext(type: "podcast", id: podcast.id, name: podcast.title))
        }
    }

    private func setFollowing(_ follow: Bool) async {
        guard let api = audio.api, let podcast else { return }
        do {
            if follow {
                self.podcast = try await api.subscribe(feedUrl: podcast.feedUrl, itunesId: nil)
            } else {
                try await api.unsubscribe(podcastId: podcast.id)
                self.podcast?.subscribed = false
            }
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

// MARK: - Episode

struct EpisodeTVView: View {
    let episodeId: String
    @EnvironmentObject private var audio: AudioCenter
    @State private var episode: Episode?
    @State private var chapters: [Chapter] = []
    @State private var transcript: Transcript?
    @State private var errorMessage: String?
    @State private var showTranscript = false

    var body: some View {
        List {
            if let episode {
                Section {
                    HStack(alignment: .top, spacing: 40) {
                        AudioArt(path: episode.artUrl, title: episode.podcastTitle ?? episode.title, size: 600)
                            .frame(width: 300, height: 300)
                            .clipShape(RoundedRectangle(cornerRadius: 14))
                        VStack(alignment: .leading, spacing: 10) {
                            Text(episode.podcastTitle ?? "").font(.caption).foregroundStyle(.secondary)
                            Text(episode.title).font(.title2.bold())
                            Text([episode.season.map { "Season \($0)" }, episode.episode.map { "Episode \($0)" }, EpisodeText.meta(episode),
                                  episode.archived == true ? "No longer in the feed" : nil, episode.download?.enhanced == true ? "Enhanced" : nil]
                                .compactMap { $0 }.joined(separator: " · "))
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    TVButtonRow {
                        AudioButton(episode.resumePosition > 0 ? "Resume" : "Play", systemImage: "play.fill") {
                            audio.play(episodes: [episode], context: PlayContext(type: "podcast", id: episode.podcastId, name: episode.podcastTitle))
                        }
                        if episode.resumePosition > 0 {
                            AudioButton("From the Start", systemImage: "backward.end.fill") {
                                audio.play(episodes: [episode], position: 0, context: PlayContext(type: "podcast", id: episode.podcastId, name: episode.podcastTitle))
                            }
                        }
                        AudioButton("Up Next", systemImage: "text.append") {
                            Task { _ = try? await audio.api?.enqueue(episodeId: episode.id, next: false) }
                        }
                        AudioButton(episode.isPlayed ? "Mark Unplayed" : "Mark Played", systemImage: "checkmark.circle") {
                            Task {
                                try? await audio.api?.setPlayed(episodeId: episode.id, played: !episode.isPlayed)
                                self.episode = try? await audio.api?.episode(id: episode.id)
                            }
                        }
                        if !episode.isDownloaded {
                            AudioButton("Download", systemImage: "arrow.down.circle") {
                                Task { if let updated = try? await audio.api?.downloadEpisode(id: episode.id) { self.episode = updated } }
                            }
                        }
                    }
                }
                if let text = episode.descriptionText ?? episode.summary, !text.isEmpty {
                    Section("About This Episode") {
                        Text(text).font(.callout).focusable()
                    }
                }
                if !chapters.isEmpty {
                    Section("Chapters") {
                        ForEach(chapters) { chapter in
                            Button {
                                audio.play(episodes: [episode], position: chapter.startSeconds,
                                           context: PlayContext(type: "podcast", id: episode.podcastId, name: episode.podcastTitle))
                            } label: {
                                HStack {
                                    Text(chapter.title)
                                    Spacer()
                                    Text(AudioFormat.clock(chapter.startSeconds)).font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                                }
                            }
                        }
                    }
                }
                if let segments = transcript?.segments, !segments.isEmpty {
                    Section {
                        Button(showTranscript ? "Hide Transcript" : "Show Transcript (\(segments.count) lines)") { showTranscript.toggle() }
                        if showTranscript {
                            ForEach(segments) { segment in
                                Button {
                                    audio.play(episodes: [episode], position: segment.start,
                                               context: PlayContext(type: "podcast", id: episode.podcastId, name: episode.podcastTitle))
                                } label: {
                                    HStack(alignment: .top, spacing: 20) {
                                        Text(AudioFormat.clock(segment.start)).font(.caption.monospacedDigit()).foregroundStyle(.secondary).frame(width: 110, alignment: .leading)
                                        VStack(alignment: .leading) {
                                            if let speaker = segment.speaker { Text(speaker).font(.caption2.bold()) }
                                            Text(segment.text).font(.callout)
                                        }
                                    }
                                }
                            }
                        }
                    } header: {
                        Text("Transcript")
                    } footer: {
                        Text(transcript?.source == "whisper" ? "Written on your server." : "From the show's feed.")
                    }
                }
            }
        }
        .overlay { AudioStateOverlay(loading: episode == nil && errorMessage == nil, error: errorMessage, isEmpty: episode == nil) }
        .task(id: episodeId) {
            guard let api = audio.api else { return }
            do {
                let loaded = try await api.episode(id: episodeId)
                episode = loaded
                if loaded.hasChapters != false { chapters = (try? await api.chapters(episodeId: episodeId)) ?? [] }
                if loaded.hasTranscript == true { transcript = try? await api.transcript(episodeId: episodeId) }
            } catch {
                errorMessage = error.localizedDescription
            }
        }
    }
}

// MARK: - Discover

struct DiscoverTVView: View {
    @EnvironmentObject private var audio: AudioCenter
    @State private var query = ""
    @State private var results: [DirectoryShow]?
    @State private var top: [DirectoryShow] = []
    @State private var recommended: [DirectoryShow] = []
    @State private var genres: [DirectoryGenre] = []
    @State private var genre: DirectoryGenre?
    @State private var searching = false

    var body: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 40) {
                if let results {
                    if results.isEmpty {
                        Text(Self.feedURL(query.trimmingCharacters(in: .whitespaces)) != nil
                             ? "Couldn't read a podcast feed at “\(query)”."
                             : "No shows found for “\(query)”.").foregroundStyle(.secondary).padding(.horizontal, 60)
                    } else {
                        showGrid("Results", results)
                    }
                } else {
                    if !recommended.isEmpty {
                        MediaShelfTV(title: "Recommended for Your Household") {
                            ForEach(recommended) { DirectoryTile(show: $0) }
                        }
                    }
                    HStack(spacing: 30) {
                        Text(genre.map { "Top \($0.name)" } ?? "Top Shows").font(.title3.bold())
                        Menu {
                            Button("All Categories") { genre = nil }
                            ForEach(genres) { g in Button(g.name) { genre = g } }
                        } label: {
                            Label(genre?.name ?? "All Categories", systemImage: "line.3.horizontal.decrease").audioFocusLabel()
                        }
                    }
                    .padding(.horizontal, 60)
                    showGrid(nil, top)
                }
            }
            .padding(.vertical, 20)
        }
        .scrollClipDisabled()
        .searchable(text: $query, prompt: "Search Apple Podcasts, or type a feed address")
        .overlay { if searching, results == nil { ProgressView() } }
        .task {
            guard let api = audio.api else { return }
            async let g = api.directoryGenres()
            async let r = api.recommended()
            genres = (try? await g) ?? []
            recommended = (try? await r) ?? []
        }
        .task(id: genre?.id) {
            top = (try? await audio.api?.directoryTop(genre: genre?.id)) ?? []
        }
        .task(id: query) {
            let trimmed = query.trimmingCharacters(in: .whitespaces)
            guard !trimmed.isEmpty else { results = nil; return }
            try? await Task.sleep(for: .milliseconds(450))
            guard !Task.isCancelled, let api = audio.api else { return }
            searching = true
            defer { searching = false }
            // A feed address typed (or dictated from the iPhone keyboard) instead of a name: read
            // that feed and offer it as the one result, so a show that isn't in the Apple
            // directory - a private or self-hosted feed - can be followed from the TV too.
            if let feed = Self.feedURL(trimmed) {
                if let preview = try? await api.preview(feedUrl: feed, itunesId: nil) {
                    let p = preview.podcast
                    results = [DirectoryShow(itunesId: nil, title: p.title, author: p.author, artUrl: p.artUrl,
                                             feedUrl: p.feedUrl ?? feed, genre: p.categories?.first,
                                             episodeCount: p.episodeCount ?? preview.episodes?.count,
                                             podcastId: p.id.isEmpty ? nil : p.id, subscribed: p.subscribed)]
                } else {
                    results = []
                }
                return
            }
            if let found = try? await api.directorySearch(trimmed) { results = found }
        }
    }

    // "http(s)://…", "feeds.example.com/show.xml" or "example.com/rss" - something with a dot and
    // no spaces that isn't plain words. Returns the address to read, with a scheme.
    static func feedURL(_ text: String) -> String? {
        let lower = text.lowercased()
        if lower.hasPrefix("http://") || lower.hasPrefix("https://") { return text }
        guard !text.contains(" "), text.contains("."), text.contains("/") else { return nil }
        return "https://" + text
    }

    private func showGrid(_ title: String?, _ shows: [DirectoryShow]) -> some View {
        VStack(alignment: .leading, spacing: 20) {
            if let title { Text(title).font(.title3.bold()) }
            LazyVGrid(columns: [GridItem(.adaptive(minimum: AudioTileSize.small, maximum: AudioTileSize.small + 30), spacing: 40)],
                      alignment: .leading, spacing: 46) {
                ForEach(shows) { DirectoryTile(show: $0) }
            }
        }
        .padding(.horizontal, 60)
    }
}

private struct DirectoryTile: View {
    let show: DirectoryShow

    var body: some View {
        AudioSquareTile(title: show.title, subtitle: show.subscribed == true ? "Following" : show.author,
                        route: show.podcastId != nil && show.subscribed == true ? .podcast(show.podcastId!) : .directoryShow(show),
                        size: AudioTileSize.small) {
            AudioArt(path: show.artUrl, title: show.title, size: 400)
        }
    }
}

// Look before you follow: the feed read without subscribing; play an episode as a preview.
struct DirectoryPreviewTVView: View {
    let show: DirectoryShow
    @EnvironmentObject private var audio: AudioCenter
    @State private var preview: PodcastPreview?
    @State private var errorMessage: String?
    @State private var following: Podcast?
    @State private var working = false

    var body: some View {
        List {
            Section {
                HStack(alignment: .top, spacing: 40) {
                    AudioArt(path: show.artUrl, title: show.title, size: 600)
                        .frame(width: 300, height: 300)
                        .clipShape(RoundedRectangle(cornerRadius: 14))
                    VStack(alignment: .leading, spacing: 10) {
                        Text(show.title).font(.title2.bold())
                        Text([show.author, show.genre, show.episodeCount.map { "\($0) episodes" }].compactMap { $0 }.joined(separator: " · "))
                            .font(.caption).foregroundStyle(.secondary)
                        if let description = preview?.podcast.description {
                            Text(description).font(.caption).foregroundStyle(.secondary).lineLimit(6)
                        }
                    }
                }
                if let following {
                    NavigationLink(value: AudioRoute.podcast(following.id)) {
                        Label("Following - Open Show", systemImage: "checkmark.circle.fill")
                    }
                } else {
                    Button {
                        Task { await follow() }
                    } label: {
                        HStack { Label("Follow", systemImage: "plus.circle"); if working { Spacer(); ProgressView() } }
                    }
                    .disabled(working)
                }
                if let errorMessage { Text(errorMessage).foregroundStyle(SentinelTheme.red) }
            }
            if let episodes = preview?.episodes, !episodes.isEmpty {
                Section("Recent Episodes - press to preview") {
                    ForEach(episodes.prefix(30)) { episode in
                        EpisodeRow(episode: episode) {
                            audio.play(episodes: [episode], position: 0, context: PlayContext(type: "podcast", id: nil, name: show.title))
                        }
                    }
                }
            } else if preview == nil, errorMessage == nil {
                Section { ProgressView() }
            }
        }
        .task {
            do { preview = try await audio.api?.preview(feedUrl: show.feedUrl, itunesId: show.itunesId) }
            catch { errorMessage = error.localizedDescription }
        }
    }

    private func follow() async {
        guard let api = audio.api else { return }
        working = true
        defer { working = false }
        do {
            following = try await api.subscribe(feedUrl: show.feedUrl, itunesId: show.feedUrl == nil ? show.itunesId : nil)
            errorMessage = nil
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}
