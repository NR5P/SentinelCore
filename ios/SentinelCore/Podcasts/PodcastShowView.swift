import SwiftUI

// One show: header, follow, funding, my settings and (admin) the household settings, then its
// episodes with filters, sort, seasons and search.
struct PodcastShowView: View {
    let podcastId: String
    var initial: Podcast? = nil
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var podcast: Podcast?
    @State private var episodes: [PodcastEpisode] = []
    @State private var total: Int?
    @State private var seasons: [Int] = []
    @State private var filter = "all"
    @State private var sort: String?
    @State private var season: Int?
    @State private var query = ""
    @State private var loading = false
    @State private var errorMessage: String?
    @State private var expanded = false
    @State private var showingMySettings = false
    @State private var showingShowSettings = false
    @State private var confirmingDelete = false
    @State private var refreshing = false

    private var show: Podcast? { podcast ?? initial }
    private var effectiveSort: String { sort ?? show?.mySettings?.sort ?? "newest" }

    var body: some View {
        List {
            header
                .listRowSeparator(.hidden)
            Section {
                filterBar
                ForEach(episodes) { episode in
                    EpisodeRow(episode: episode, list: effectiveSort == "oldest" ? episodes : episodes.reversed())
                        .task { await loadMoreIfNeeded(episode) }
                }
                if loading { ProgressView().frame(maxWidth: .infinity) }
                if !loading && episodes.isEmpty {
                    Text(filter == "all" && query.isEmpty ? "No episodes yet." : "No episodes match.").foregroundStyle(.secondary)
                }
            } header: {
                if let total { Text("\(total) episode\(total == 1 ? "" : "s")") }
            }
            if let errorMessage { Text(errorMessage).foregroundStyle(SentinelTheme.red) }
        }
        .listStyle(.plain)
        .searchable(text: $query, prompt: "Search episodes")
        .navigationTitle(show?.title ?? "Show")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Menu {
                    Button("My Settings for This Show…", systemImage: "slider.horizontal.3") { showingMySettings = true }
                    if model.isAdmin {
                        Button("Household Settings…", systemImage: "gearshape") { showingShowSettings = true }
                    }
                    Button("Refresh Feed", systemImage: "arrow.clockwise") { Task { await refresh() } }
                    Button("Mark All Played", systemImage: "checkmark.circle") {
                        Task {
                            try? await model.client.markPodcastPlayed(id: podcastId, played: true)
                            await reload()
                        }
                    }
                    Button("Mark All Unplayed", systemImage: "circle") {
                        Task {
                            try? await model.client.markPodcastPlayed(id: podcastId, played: false)
                            await reload()
                        }
                    }
                    if let link = show?.link, let url = URL(string: link) {
                        Link(destination: url) { Label("Show Website", systemImage: "safari") }
                    }
                    if let feed = show?.feedUrl, let url = URL(string: feed) {
                        ShareLink(item: url) { Label("Share Feed", systemImage: "square.and.arrow.up") }
                    }
                    if model.isAdmin {
                        Divider()
                        Button("Remove Show from Server", systemImage: "trash", role: .destructive) { confirmingDelete = true }
                    }
                } label: {
                    Label("More", systemImage: "ellipsis.circle")
                }
            }
        }
        .sheet(isPresented: $showingMySettings) {
            if let show { PodcastMySettingsView(podcast: show) { podcast = $0 } }
        }
        .sheet(isPresented: $showingShowSettings) {
            if let show { PodcastHouseholdSettingsView(podcast: show) { podcast = $0 } }
        }
        .confirmationDialog("Remove \u{201C}\(show?.title ?? "")\u{201D} for everyone?", isPresented: $confirmingDelete, titleVisibility: .visible) {
            Button("Remove Show", role: .destructive) {
                Task {
                    do {
                        try await model.client.deletePodcast(id: podcastId)
                        NotificationCenter.default.post(name: .podcastsChanged, object: nil)
                        dismiss()
                    } catch { errorMessage = error.localizedDescription }
                }
            }
        } message: {
            Text("The show, its episodes, everyone's progress and its downloads are removed from the server.")
        }
        .task { podcast = try? await model.client.podcast(id: podcastId) }
        .task(id: "\(filter)|\(effectiveSort)|\(season ?? -1)|\(query)") {
            try? await Task.sleep(for: .milliseconds(query.isEmpty ? 0 : 250))
            await reload()
        }
        .onReceive(NotificationCenter.default.publisher(for: .podcastsChanged)) { _ in Task { await reload() } }
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .top, spacing: 16) {
                ArtworkImage(path: show?.artUrl, size: 140, corner: 14, placeholder: "mic")
                    .shadow(color: .black.opacity(0.2), radius: 10, y: 5)
                VStack(alignment: .leading, spacing: 5) {
                    Text(show?.title ?? "").font(.title2.bold())
                    if let author = show?.author { Text(author).foregroundStyle(.secondary) }
                    Text([show?.categories?.first, show?.explicit == true ? "Explicit" : nil,
                          show?.subscriberCount.map { $0 > 1 ? "\($0) in the household follow" : nil } ?? nil]
                        .compactMap { $0 }.joined(separator: " · "))
                        .font(.caption).foregroundStyle(.secondary)
                    HStack {
                        followButton
                        if refreshing { ProgressView().controlSize(.small) }
                    }
                }
            }
            if let description = show?.description, !description.isEmpty {
                Text(description)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .lineLimit(expanded ? nil : 3)
                    .onTapGesture { withAnimation { expanded.toggle() } }
                    .accessibilityHint(expanded ? "Shows less" : "Shows the whole description")
            }
            if let funding = show?.funding, !funding.isEmpty {
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack {
                        ForEach(funding, id: \.url) { link in
                            if let url = URL(string: link.url) {
                                Link(destination: url) {
                                    Label(link.title ?? "Support the Show", systemImage: "heart.circle")
                                        .font(.caption.weight(.semibold))
                                        .padding(.horizontal, 10).padding(.vertical, 6)
                                        .background(.pink.opacity(0.15), in: Capsule())
                                        .foregroundStyle(.pink)
                                }
                            }
                        }
                    }
                }
            }
            if let error = show?.refreshError {
                Label(error, systemImage: "exclamationmark.triangle").font(.caption).foregroundStyle(SentinelTheme.amber)
            }
            if let settings = show?.mySettings {
                mySettingsSummary(settings)
            }
        }
        .padding(.vertical, 6)
    }

    private func mySettingsSummary(_ settings: PodcastMySettings) -> some View {
        let parts = [
            settings.playbackSpeed.map { "Plays at \(speedLabel($0))" },
            (settings.skipIntroSeconds ?? 0) > 0 ? "skips \(settings.skipIntroSeconds!) s intro" : nil,
            (settings.skipOutroSeconds ?? 0) > 0 ? "\(settings.skipOutroSeconds!) s outro" : nil,
            settings.autoQueue == true ? "new episodes go to Up Next" : nil,
            settings.inBriefing == true ? "in the Morning Briefing" : nil,
        ].compactMap { $0 }
        return Group {
            if !parts.isEmpty {
                Button {
                    showingMySettings = true
                } label: {
                    Label(parts.joined(separator: " · "), systemImage: "slider.horizontal.3")
                        .font(.caption).foregroundStyle(.secondary)
                }
                .buttonStyle(.plain)
            }
        }
    }

    @ViewBuilder
    private var followButton: some View {
        if show?.isSubscribed == true {
            Button {
                Task {
                    try? await model.client.unsubscribe(podcastId: podcastId)
                    podcast?.subscribed = false
                    NotificationCenter.default.post(name: .podcastsChanged, object: nil)
                }
            } label: {
                Label("Following", systemImage: "checkmark")
            }
            .buttonStyle(.bordered)
        } else {
            Button {
                Task {
                    if let feed = show?.feedUrl, let added = try? await model.client.subscribe(feedUrl: feed, itunesId: nil) {
                        podcast = added
                        NotificationCenter.default.post(name: .podcastsChanged, object: nil)
                    }
                }
            } label: {
                Label("Follow", systemImage: "plus")
            }
            .buttonStyle(.borderedProminent).foregroundStyle(Color.onSentinelGreen)
            .tint(SentinelTheme.green)
        }
    }

    private var filterBar: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) {
                Picker("Show", selection: $filter) {
                    Text("All").tag("all")
                    Text("Unplayed").tag("unplayed")
                    Text("In Progress").tag("inProgress")
                    Text("Downloaded").tag("downloaded")
                    Text("Bookmarked").tag("bookmarked")
                }
                .pickerStyle(.menu)
                .fixedSize()
                Picker("Sort", selection: Binding(get: { effectiveSort }, set: { sort = $0 })) {
                    Text("Newest First").tag("newest")
                    Text("Oldest First").tag("oldest")
                }
                .pickerStyle(.menu)
                .fixedSize()
                if !seasons.isEmpty {
                    Picker("Season", selection: $season) {
                        Text("All Seasons").tag(Int?.none)
                        ForEach(seasons, id: \.self) { Text("Season \($0)").tag(Int?.some($0)) }
                    }
                    .pickerStyle(.menu)
                    .fixedSize()
                }
                if filter == "unplayed", let first = episodes.first {
                    Button {
                        PodcastActions.play(first, in: episodes, context: AudioPlayContext(type: "podcast", id: podcastId, name: show?.title))
                    } label: {
                        Label("Play All", systemImage: "play.fill")
                    }
                    .buttonStyle(.bordered)
                }
            }
        }
        .listRowSeparator(.hidden)
    }

    private func reload() async {
        loading = true
        defer { loading = false }
        do {
            let page = try await model.client.podcastEpisodes(id: podcastId, sort: effectiveSort, filter: filter, q: query, season: season)
            episodes = page.items
            total = page.total
            if let s = page.seasons { seasons = s }
            errorMessage = nil
        } catch {
            if !error.isCancellation { errorMessage = error.localizedDescription }
        }
    }

    private func loadMoreIfNeeded(_ episode: PodcastEpisode) async {
        guard episode.id == episodes.last?.id, let total, episodes.count < total, !loading else { return }
        loading = true
        defer { loading = false }
        if let page = try? await model.client.podcastEpisodes(id: podcastId, sort: effectiveSort, filter: filter, q: query,
                                                               season: season, offset: episodes.count) {
            episodes += page.items
        }
    }

    private func refresh() async {
        refreshing = true
        defer { refreshing = false }
        do {
            podcast = try await model.client.refreshPodcast(id: podcastId)
            await reload()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

// MARK: - Settings sheets

struct PodcastMySettingsView: View {
    let podcast: Podcast
    let onSaved: (Podcast) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var settings = PodcastMySettings()
    @State private var useDefaultSpeed = true
    @State private var saving = false
    @State private var errorMessage: String?
    @State private var seeded = false

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Toggle("Use My Usual Speed", isOn: $useDefaultSpeed)
                    if !useDefaultSpeed {
                        Picker("Speed", selection: Binding(get: { settings.playbackSpeed ?? 1 }, set: { settings.playbackSpeed = $0 })) {
                            ForEach([0.75, 0.9, 1.0, 1.1, 1.2, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0], id: \.self) { Text(speedLabel($0)).tag($0) }
                        }
                    }
                } header: {
                    Text("Speed")
                } footer: {
                    Text("Changing the speed while one of this show's episodes plays is remembered here too.")
                }
                Section("Skip") {
                    Stepper("Intro: \(settings.skipIntroSeconds ?? 0) s",
                            value: Binding(get: { settings.skipIntroSeconds ?? 0 }, set: { settings.skipIntroSeconds = $0 }), in: 0...300, step: 5)
                    Stepper("Outro: \(settings.skipOutroSeconds ?? 0) s",
                            value: Binding(get: { settings.skipOutroSeconds ?? 0 }, set: { settings.skipOutroSeconds = $0 }), in: 0...300, step: 5)
                }
                Section {
                    Toggle("Notify Me of New Episodes", isOn: Binding(get: { settings.notify ?? true }, set: { settings.notify = $0 }))
                    Toggle("Add New Episodes to Up Next", isOn: Binding(get: { settings.autoQueue ?? false }, set: { settings.autoQueue = $0 }))
                    Toggle("Include in Morning Briefing", isOn: Binding(get: { settings.inBriefing ?? false }, set: { settings.inBriefing = $0 }))
                    Picker("Episode Order", selection: Binding(get: { settings.sort ?? "newest" }, set: { settings.sort = $0 })) {
                        Text("Newest First").tag("newest")
                        Text("Oldest First").tag("oldest")
                    }
                } footer: {
                    Text("The Morning Briefing plays the newest unplayed episode of each show you include, after the weather and your day.")
                }
                if let errorMessage { Section { Text(errorMessage).foregroundStyle(SentinelTheme.red) } }
            }
            .formStyle(.grouped)
            .navigationTitle("My Settings")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) { Button(saving ? "Saving…" : "Save") { Task { await save() } }.disabled(saving) }
            }
            .onAppear {
                guard !seeded else { return }
                seeded = true
                settings = podcast.mySettings ?? PodcastMySettings()
                useDefaultSpeed = settings.playbackSpeed == nil
            }
        }
        #if os(macOS)
        .frame(minWidth: 480, minHeight: 560)
        #endif
    }

    private func save() async {
        saving = true
        defer { saving = false }
        var body = settings
        if useDefaultSpeed { body.playbackSpeed = nil }
        do {
            let saved = try await AppModel.shared.client.savePodcastMySettings(id: podcast.id, body)
            onSaved(saved)
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

struct PodcastHouseholdSettingsView: View {
    let podcast: Podcast
    let onSaved: (Podcast) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var settings = PodcastSettings()
    @State private var saving = false
    @State private var errorMessage: String?
    @State private var seeded = false

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Toggle("Download New Episodes", isOn: Binding(get: { settings.autoDownload ?? true }, set: { settings.autoDownload = $0 }))
                    Stepper((settings.keepEpisodes ?? 10) == 0 ? "Keep all downloaded episodes" : "Keep the newest \(settings.keepEpisodes ?? 10)",
                            value: Binding(get: { settings.keepEpisodes ?? 10 }, set: { settings.keepEpisodes = $0 }), in: 0...500)
                } header: {
                    Text("On the Server")
                }
                Section {
                    Toggle("Voice Boost", isOn: Binding(get: { settings.voiceBoost ?? false }, set: { settings.voiceBoost = $0 }))
                    Toggle("Trim Silences", isOn: Binding(get: { settings.trimSilence ?? false }, set: { settings.trimSilence = $0 }))
                    Toggle("Write Transcripts", isOn: Binding(get: { settings.transcribe ?? false }, set: { settings.transcribe = $0 }))
                } header: {
                    Text("Make It Better")
                } footer: {
                    Text("Voice Boost evens out loudness and clarity. Trim Silences cuts pauses over a second down to 0.3 s when an episode downloads. Transcripts need transcription turned on in Server Settings → Podcasts.")
                }
                if let errorMessage { Section { Text(errorMessage).foregroundStyle(SentinelTheme.red) } }
            }
            .formStyle(.grouped)
            .navigationTitle("Household Settings")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) { Button(saving ? "Saving…" : "Save") { Task { await save() } }.disabled(saving) }
            }
            .onAppear {
                guard !seeded else { return }
                seeded = true
                settings = podcast.settings ?? PodcastSettings()
            }
        }
        #if os(macOS)
        .frame(minWidth: 480, minHeight: 480)
        #endif
    }

    private func save() async {
        saving = true
        defer { saving = false }
        do {
            onSaved(try await AppModel.shared.client.savePodcastSettings(id: podcast.id, settings))
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}
