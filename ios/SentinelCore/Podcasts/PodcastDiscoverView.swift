import SwiftUI

// MARK: - Discover

struct PodcastDiscoverView: View {
    @EnvironmentObject private var model: AppModel
    @State private var query = ""
    @State private var results: [DirectoryShow]?
    @State private var searching = false
    @State private var recommended: [DirectoryShow] = []
    @State private var genres: [DirectoryGenre] = []
    @AppStorage("podcasts.topGenre") private var genre = 0
    @State private var top: [DirectoryShow] = []
    @State private var loadingTop = false
    @State private var errorMessage: String?
    @State private var addingURL = false

    var body: some View {
        List {
            if let results {
                Section(results.isEmpty ? "Nothing found" : "Results") {
                    ForEach(results) { DirectoryShowRow(show: $0) }
                }
            } else {
                Section {
                    Button {
                        addingURL = true
                    } label: {
                        Label("Add a Show by Its Feed Address…", systemImage: "link.badge.plus")
                    }
                }
                if !recommended.isEmpty {
                    Section {
                        ScrollView(.horizontal, showsIndicators: false) {
                            LazyHStack(alignment: .top, spacing: 14) {
                                ForEach(recommended) { show in
                                    NavigationLink {
                                        PodcastPreviewView(show: show)
                                    } label: {
                                        VStack(alignment: .leading, spacing: 4) {
                                            ArtworkImage(path: show.artUrl, size: 130, corner: 10, placeholder: "mic")
                                            Text(show.title).font(.caption.weight(.semibold)).lineLimit(2).frame(width: 130, alignment: .leading)
                                        }
                                    }
                                    .buttonStyle(.plain)
                                }
                            }
                            .padding(.vertical, 4)
                        }
                    } header: {
                        Text("Recommended for the Household")
                    }
                }
                Section {
                    if !genres.isEmpty {
                        Picker("Category", selection: $genre) {
                            Text("All Categories").tag(0)
                            ForEach(genres) { Text($0.name).tag($0.id) }
                        }
                    }
                    if loadingTop && top.isEmpty { ProgressView().frame(maxWidth: .infinity) }
                    ForEach(Array(top.enumerated()), id: \.element.id) { offset, show in
                        DirectoryShowRow(show: show, rank: offset + 1)
                    }
                } header: {
                    Text("Top Shows")
                }
            }
            if let errorMessage { Text(errorMessage).foregroundStyle(SentinelTheme.red) }
        }
        .searchable(text: $query, prompt: "Search all podcasts")
        .onSubmit(of: .search) { Task { await search() } }
        .onChange(of: query) { _, value in if value.isEmpty { results = nil } }
        .overlay { if searching { ProgressView() } }
        .sheet(isPresented: $addingURL) { AddPodcastByURLView { _ in } }
        .task {
            async let g = model.client.podcastGenres()
            async let r = model.client.podcastRecommended()
            genres = (try? await g) ?? []
            recommended = (try? await r) ?? []
        }
        .task(id: genre) {
            loadingTop = true
            defer { loadingTop = false }
            do {
                top = try await model.client.podcastTop(genre: genre == 0 ? nil : genre)
                errorMessage = nil
            } catch {
                if !error.isCancellation { errorMessage = error.localizedDescription }
            }
        }
        .task(id: query) {
            let q = query.trimmingCharacters(in: .whitespaces)
            guard q.count >= 3 else { return }
            try? await Task.sleep(for: .milliseconds(450))
            guard !Task.isCancelled else { return }
            await search()
        }
    }

    private func search() async {
        let q = query.trimmingCharacters(in: .whitespaces)
        guard !q.isEmpty else { return }
        // A pasted feed address is a feed, not a search.
        if q.hasPrefix("http://") || q.hasPrefix("https://") {
            results = [DirectoryShow(itunesId: nil, title: q, author: "Feed address", artUrl: nil, feedUrl: q, genre: nil,
                                     episodeCount: nil, podcastId: nil, subscribed: nil)]
            return
        }
        searching = true
        defer { searching = false }
        do {
            results = try await model.client.podcastDirectorySearch(q)
            errorMessage = nil
        } catch {
            if !error.isCancellation { errorMessage = error.localizedDescription }
        }
    }
}

// "Look before you follow": the feed read without subscribing.
struct PodcastPreviewView: View {
    let show: DirectoryShow
    @EnvironmentObject private var model: AppModel
    @State private var preview: PodcastPreview?
    @State private var errorMessage: String?
    @State private var following = false
    @State private var followed: Podcast?

    var body: some View {
        List {
            HStack(alignment: .top, spacing: 16) {
                ArtworkImage(path: preview?.podcast.artUrl ?? show.artUrl, size: 130, corner: 12, placeholder: "mic")
                VStack(alignment: .leading, spacing: 6) {
                    Text(preview?.podcast.title ?? show.title).font(.title2.bold())
                    if let author = preview?.podcast.author ?? show.author { Text(author).foregroundStyle(.secondary) }
                    if let genre = show.genre { Text(genre).font(.caption).foregroundStyle(.secondary) }
                    if let followed {
                        NavigationLink {
                            PodcastShowView(podcastId: followed.id, initial: followed)
                        } label: {
                            Label("Following · Open Show", systemImage: "checkmark")
                        }
                        .buttonStyle(.bordered)
                    } else {
                        Button {
                            Task { await follow() }
                        } label: {
                            HStack {
                                Label("Follow", systemImage: "plus")
                                if following { ProgressView().controlSize(.small) }
                            }
                        }
                        .buttonStyle(.borderedProminent).foregroundStyle(Color.onSentinelGreen)
                        .tint(SentinelTheme.green)
                        .disabled(following)
                    }
                }
            }
            .listRowSeparator(.hidden)
            if let description = preview?.podcast.description {
                Text(description).font(.subheadline).foregroundStyle(.secondary).lineLimit(8)
            }
            if let errorMessage { Text(errorMessage).foregroundStyle(SentinelTheme.red) }
            if let episodes = preview?.episodes {
                Section("Latest Episodes") {
                    ForEach(episodes.prefix(30)) { episode in
                        VStack(alignment: .leading, spacing: 3) {
                            Text([AudioFormat.relative(episode.publishedAt), episode.duration > 0 ? AudioFormat.duration(episode.duration, style: .short) : nil]
                                .compactMap { $0 }.joined(separator: " · ").uppercased())
                                .font(.caption2.weight(.semibold)).foregroundStyle(.secondary)
                            Text(episode.title).font(.headline)
                            if let summary = episode.summary { Text(summary).font(.subheadline).foregroundStyle(.secondary).lineLimit(2) }
                        }
                    }
                }
            } else if errorMessage == nil {
                ProgressView().frame(maxWidth: .infinity)
            }
        }
        .listStyle(.plain)
        .navigationTitle(show.title)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .task {
            do {
                preview = try await model.client.podcastPreview(feedUrl: show.feedUrl, itunesId: show.feedUrl == nil ? show.itunesId : nil)
            } catch {
                if !error.isCancellation { errorMessage = error.localizedDescription }
            }
        }
    }

    private func follow() async {
        following = true
        defer { following = false }
        do {
            followed = try await model.client.subscribe(feedUrl: show.feedUrl, itunesId: show.feedUrl == nil ? show.itunesId : nil)
            NotificationCenter.default.post(name: .podcastsChanged, object: nil)
            AudioToast.shared.show("Following \(followed?.title ?? show.title)")
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

struct AddPodcastByURLView: View {
    let onAdded: (Podcast) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var url = ""
    @State private var adding = false
    @State private var errorMessage: String?

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("", text: $url, prompt: Text("https://example.com/feed.xml"))
                        .labelsHidden()
                        .multilineTextAlignment(.leading)
                        .autocorrectionDisabled()
                        #if os(iOS)
                        .textInputAutocapitalization(.never)
                        .keyboardType(.URL)
                        #endif
                        .accessibilityLabel("Feed address")
                } header: {
                    Text("Feed Address")
                } footer: {
                    Text("The show's RSS feed - private and paid feeds work too. The server reads it right away.")
                }
                if let errorMessage { Section { Text(errorMessage).foregroundStyle(SentinelTheme.red) } }
            }
            .formStyle(.grouped)
            .navigationTitle("Add a Show")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button(adding ? "Adding…" : "Follow") { Task { await add() } }
                        .disabled(url.trimmingCharacters(in: .whitespaces).isEmpty || adding)
                }
            }
        }
        #if os(macOS)
        .frame(minWidth: 460, minHeight: 300)
        #endif
    }

    private func add() async {
        adding = true
        defer { adding = false }
        var feed = url.trimmingCharacters(in: .whitespacesAndNewlines)
        if !feed.contains("://") { feed = "https://" + feed }
        do {
            let podcast = try await AppModel.shared.client.subscribe(feedUrl: feed, itunesId: nil)
            NotificationCenter.default.post(name: .podcastsChanged, object: nil)
            AudioToast.shared.show("Following \(podcast.title)")
            onAdded(podcast)
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

// MARK: - Up Next

struct PodcastUpNextView: View {
    @State private var queue: [PodcastEpisode] = []
    @State private var loading = true
    @State private var errorMessage: String?
    #if os(iOS)
    @State private var editMode: EditMode = .inactive
    #endif

    var body: some View {
        List {
            if !queue.isEmpty {
                Button {
                    AudioPlayer.shared.play(episode: queue[0], queue: Array(queue.dropFirst()),
                                            context: AudioPlayContext(type: "queue", id: "up-next", name: "Up Next"))
                } label: {
                    Label("Play All · \(AudioFormat.duration(queue.reduce(0) { $0 + max(0, $1.duration - ($1.progress?.position ?? 0)) }, style: .short))",
                          systemImage: "play.fill")
                }
            }
            ForEach(queue) { EpisodeRow(episode: $0, showShow: true, list: queue, inUpNext: true) }
                .onMove { from, to in
                    queue.move(fromOffsets: from, toOffset: to)
                    save()
                }
                .onDelete { offsets in
                    let removed = offsets.map { queue[$0] }
                    queue.remove(atOffsets: offsets)
                    Task { for episode in removed { _ = try? await AppModel.shared.client.removeFromPodcastQueue(episodeId: episode.id) } }
                }
            if let errorMessage { Text(errorMessage).foregroundStyle(SentinelTheme.red) }
        }
        .listStyle(.plain)
        #if os(iOS)
        .environment(\.editMode, $editMode)
        #endif
        .overlay {
            if queue.isEmpty {
                AudioLoadState(loading: loading, error: nil,
                               empty: loading ? nil : ("Up Next is empty", "text.line.first.and.arrowtriangle.forward",
                                                       "Add episodes with Add to Up Next, or turn on \u{201C}Add new episodes to Up Next\u{201D} in a show's settings."))
            }
        }
        .navigationTitle("Up Next")
        .toolbar {
            #if os(iOS)
            ToolbarItem(placement: .primaryAction) {
                if !queue.isEmpty {
                    Button(editMode.isEditing ? "Done" : "Reorder") { withAnimation { editMode = editMode.isEditing ? .inactive : .active } }
                }
            }
            #endif
        }
        .task { await load() }
        .refreshable { await load() }
        .onReceive(NotificationCenter.default.publisher(for: .podcastsChanged)) { _ in Task { await load() } }
    }

    private func load() async {
        do {
            queue = try await AppModel.shared.client.podcastQueue()
            errorMessage = nil
        } catch {
            if !error.isCancellation { errorMessage = error.localizedDescription }
        }
        loading = false
    }

    private func save() {
        let ids = queue.map(\.id)
        Task {
            do { queue = try await AppModel.shared.client.setPodcastQueue(ids) }
            catch { errorMessage = error.localizedDescription }
        }
    }
}

// MARK: - Bookmarks, clips, transcript search

struct PodcastBookmarksView: View {
    @State private var bookmarks: [PodcastBookmark] = []
    @State private var loading = true

    var body: some View {
        List {
            ForEach(bookmarks) { BookmarkRow(bookmark: $0) { Task { await load() } } }
        }
        .listStyle(.plain)
        .overlay {
            if bookmarks.isEmpty {
                AudioLoadState(loading: loading, error: nil,
                               empty: loading ? nil : ("No bookmarks", "bookmark", "Tap the bookmark in Now Playing to keep a moment - the transcript around it is saved too."))
            }
        }
        .navigationTitle("Bookmarks")
        .task { await load() }
    }

    private func load() async {
        bookmarks = (try? await AppModel.shared.client.bookmarks()) ?? []
        loading = false
    }
}

struct PodcastClipsView: View {
    @State private var clips: [PodcastClip] = []
    @State private var loading = true
    @State private var sharing: AudioSharedFile?

    var body: some View {
        List {
            ForEach(clips) { clip in
                HStack {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(clip.title?.isEmpty == false ? clip.title! : (clip.episodeTitle ?? "Clip")).lineLimit(2)
                        Text([clip.podcastTitle, clip.durationSeconds.map { "\(Int($0)) s" }, AudioFormat.relative(clip.createdAt)]
                            .compactMap { $0 }.joined(separator: " · "))
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button {
                        Task { await share(clip) }
                    } label: {
                        Image(systemName: "square.and.arrow.up")
                    }
                    .buttonStyle(.borderless)
                    .accessibilityLabel("Share clip")
                }
            }
            .onDelete { offsets in
                let removed = offsets.map { clips[$0] }
                clips.remove(atOffsets: offsets)
                Task { for clip in removed { try? await AppModel.shared.client.deleteClip(id: clip.id) } }
            }
        }
        .overlay {
            if clips.isEmpty {
                AudioLoadState(loading: loading, error: nil,
                               empty: loading ? nil : ("No clips", "scissors", "Make a Clip from an episode's page or Now Playing to share a moment."))
            }
        }
        .navigationTitle("Clips")
        .sheet(item: $sharing) { OPMLExportSheetForClip(file: $0.url) }
        .task {
            clips = (try? await AppModel.shared.client.clips()) ?? []
            loading = false
        }
    }

    private func share(_ clip: PodcastClip) async {
        let client = AppModel.shared.client
        guard let url = client.audioURL(clip.url ?? "/api/podcasts/clips/\(clip.id).m4a"),
              let (data, _) = try? await URLSession.shared.data(from: url) else { return }
        let local = FileManager.default.temporaryDirectory.appendingPathComponent("\(clip.title?.isEmpty == false ? clip.title! : "Clip \(clip.id.prefix(6))").m4a")
        try? data.write(to: local, options: .atomic)
        sharing = AudioSharedFile(url: local)
    }
}

private struct OPMLExportSheetForClip: View {
    let file: URL
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            VStack(spacing: 16) {
                Image(systemName: "waveform").font(.system(size: 50)).foregroundStyle(SentinelTheme.green)
                Text(file.deletingPathExtension().lastPathComponent).font(.headline).multilineTextAlignment(.center)
                ShareLink(item: file) { Label("Share Clip…", systemImage: "square.and.arrow.up") }
                    .buttonStyle(.borderedProminent).foregroundStyle(Color.onSentinelGreen).tint(SentinelTheme.green)
            }
            .padding(30)
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } } }
        }
        #if os(macOS)
        .frame(minWidth: 400, minHeight: 280)
        #endif
    }
}

struct TranscriptSearchView: View {
    @State private var query = ""
    @State private var hits: [TranscriptHit] = []
    @State private var searching = false
    @State private var searched = false

    var body: some View {
        List {
            ForEach(hits) { hit in
                Button {
                    Task {
                        guard let detail = try? await AppModel.shared.client.episode(id: hit.episodeId) else { return }
                        AudioPlayer.shared.play(episode: detail.episode, at: hit.startSeconds)
                    }
                } label: {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("\(hit.podcastTitle ?? "") · \(AudioFormat.duration(hit.startSeconds))")
                            .font(.caption2.weight(.semibold)).foregroundStyle(.secondary)
                        Text(hit.episodeTitle ?? "").font(.subheadline.weight(.semibold)).foregroundStyle(.primary)
                        if let snippet = hit.snippet { Text("\u{2026}\(snippet)\u{2026}").font(.subheadline).foregroundStyle(.secondary) }
                    }
                }
                .buttonStyle(.plain)
            }
        }
        .listStyle(.plain)
        .overlay {
            if searching { ProgressView() }
            else if hits.isEmpty {
                ContentUnavailableView(searched ? "No matches" : "Search what was said", systemImage: "text.magnifyingglass",
                                       description: Text(searched ? "Nothing in your transcripts mentions that." : "Finds words across every transcript. Tap a result to play from that moment."))
            }
        }
        .searchable(text: $query, prompt: "Words from any episode")
        .onSubmit(of: .search) { Task { await search() } }
        .task(id: query) {
            guard query.trimmingCharacters(in: .whitespaces).count >= 3 else { return }
            try? await Task.sleep(for: .milliseconds(400))
            guard !Task.isCancelled else { return }
            await search()
        }
        .navigationTitle("Search Transcripts")
    }

    private func search() async {
        let q = query.trimmingCharacters(in: .whitespaces)
        guard !q.isEmpty else { return }
        searching = true
        defer { searching = false }
        hits = (try? await AppModel.shared.client.searchTranscripts(q)) ?? []
        searched = true
    }
}
