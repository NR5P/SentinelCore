import SwiftUI

// Rows, cards and menus shared by every Podcasts screen.

@MainActor
enum PodcastActions {
    static var client: SentinelClient { AppModel.shared.client }

    // Plays an episode; what follows it is the rest of the list it was picked from.
    static func play(_ episode: PodcastEpisode, in list: [PodcastEpisode] = [], context: AudioPlayContext? = nil) {
        let after = list.firstIndex(where: { $0.id == episode.id }).map { Array(list[($0 + 1)...]) } ?? []
        AudioPlayer.shared.play(episode: episode, queue: after.filter { !$0.isPlayed }, context: context)
    }

    static func addToUpNext(_ episode: PodcastEpisode, position: String) {
        Task {
            do {
                _ = try await client.addToPodcastQueue(episodeId: episode.id, position: position)
                AudioToast.shared.show(position == "next" ? "Playing next in Up Next" : "Added to Up Next")
                NotificationCenter.default.post(name: .podcastsChanged, object: nil)
            } catch {
                AudioToast.shared.show("Couldn't add to Up Next")
            }
        }
    }

    static func removeFromUpNext(_ episode: PodcastEpisode) {
        Task {
            _ = try? await client.removeFromPodcastQueue(episodeId: episode.id)
            NotificationCenter.default.post(name: .podcastsChanged, object: nil)
        }
    }

    static func setPlayed(_ episode: PodcastEpisode, _ played: Bool) {
        Task {
            try? await client.setEpisodePlayed(id: episode.id, played: played)
            AudioToast.shared.show(played ? "Marked as played" : "Marked as unplayed")
            NotificationCenter.default.post(name: .podcastsChanged, object: nil)
        }
    }

    static func downloadOnServer(_ episode: PodcastEpisode) {
        Task {
            do {
                _ = try await client.startEpisodeDownload(id: episode.id)
                AudioToast.shared.show("The server is downloading it")
                NotificationCenter.default.post(name: .podcastsChanged, object: nil)
            } catch {
                AudioToast.shared.show(error.localizedDescription)
            }
        }
    }
}

extension Notification.Name {
    static let podcastsChanged = Notification.Name("podcastsChanged")
}

// A list row: date and length, title, summary, with a play button that shows how far in it is.
struct EpisodeRow: View {
    let episode: PodcastEpisode
    var showShow = false
    var list: [PodcastEpisode] = []
    var inUpNext = false

    var body: some View {
        let player = AudioPlayer.shared
        let isCurrent = player.current?.episode?.id == episode.id
        HStack(alignment: .top, spacing: 12) {
            if showShow {
                ArtworkImage(path: episode.artUrl, size: 56, corner: 8, placeholder: "mic")
            }
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 6) {
                    if let date = AudioFormat.relative(episode.publishedAt) { Text(date.uppercased()) }
                    if let number = episode.numberLabel { Text("· \(number)") }
                    if showShow, let show = episode.podcastTitle { Text("· \(show)").lineLimit(1) }
                }
                .font(.caption2.weight(.semibold))
                .foregroundStyle(.secondary)
                NavigationLink {
                    PodcastEpisodeView(episodeId: episode.id, initial: episode)
                } label: {
                    Text(episode.title)
                        .font(.headline)
                        .foregroundStyle(episode.isPlayed ? .secondary : (isCurrent ? SentinelTheme.green : .primary))
                        .lineLimit(2)
                        .multilineTextAlignment(.leading)
                }
                .buttonStyle(.plain)
                if let summary = episode.summary, !summary.isEmpty {
                    Text(summary).font(.subheadline).foregroundStyle(.secondary).lineLimit(2)
                }
                HStack(spacing: 10) {
                    EpisodePlayButton(episode: episode, list: list)
                    if episode.download?.isActive == true {
                        Label("Server downloading", systemImage: "icloud.and.arrow.down").labelStyle(.iconOnly).foregroundStyle(.secondary)
                    }
                    OfflineBadge(kind: "episode", id: episode.id)
                    if episode.inQueue == true || inUpNext {
                        Image(systemName: "text.line.first.and.arrowtriangle.forward").font(.caption).foregroundStyle(.secondary)
                            .accessibilityLabel("In Up Next")
                    }
                    if (episode.bookmarkCount ?? 0) > 0 {
                        Image(systemName: "bookmark.fill").font(.caption).foregroundStyle(.secondary).accessibilityLabel("Has bookmarks")
                    }
                    if episode.hasTranscript == true {
                        Image(systemName: "text.quote").font(.caption).foregroundStyle(.secondary).accessibilityLabel("Has a transcript")
                    }
                    if episode.download?.enhanced == true {
                        Image(systemName: "waveform.badge.magnifyingglass").font(.caption).foregroundStyle(.secondary).accessibilityLabel("Enhanced")
                    }
                    Spacer()
                    Menu {
                        EpisodeMenuItems(episode: episode, inUpNext: inUpNext)
                    } label: {
                        Image(systemName: "ellipsis").frame(width: 28, height: 24).contentShape(Rectangle())
                    }
                    .menuStyle(.button)
                    .buttonStyle(.plain)
                    .menuIndicator(.hidden)
                    .foregroundStyle(.secondary)
                    .accessibilityLabel("More for \(episode.title)")
                }
            }
        }
        .padding(.vertical, 4)
        .contextMenu { EpisodeMenuItems(episode: episode, inUpNext: inUpNext) }
    }
}

// The pill with a progress ring: "▶ 28 min left".
struct EpisodePlayButton: View {
    let episode: PodcastEpisode
    var list: [PodcastEpisode] = []

    var body: some View {
        let player = AudioPlayer.shared
        let isCurrent = player.current?.episode?.id == episode.id
        let progress = isCurrent ? (player.duration > 0 ? player.position / player.duration : 0)
            : (episode.duration > 0 ? (episode.progress?.position ?? 0) / episode.duration : 0)
        Button {
            if isCurrent { player.togglePlayPause() } else { PodcastActions.play(episode, in: list) }
        } label: {
            HStack(spacing: 6) {
                ZStack {
                    Circle().stroke(.secondary.opacity(0.3), lineWidth: 2)
                    Circle().trim(from: 0, to: episode.isPlayed && !isCurrent ? 1 : progress)
                        .stroke(SentinelTheme.green, style: StrokeStyle(lineWidth: 2, lineCap: .round))
                        .rotationEffect(.degrees(-90))
                    Image(systemName: isCurrent && player.isPlaying ? "pause.fill" : "play.fill")
                        .font(.system(size: 8, weight: .bold))
                }
                .frame(width: 18, height: 18)
                Text(isCurrent ? AudioFormat.duration(max(0, player.duration - player.position), style: .short) + " left" : episode.remainingLabel)
                    .font(.caption.weight(.semibold).monospacedDigit())
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 5)
            .background(SentinelTheme.green.opacity(0.14), in: Capsule())
            .foregroundStyle(SentinelTheme.green)
        }
        .buttonStyle(.plain)
        .accessibilityLabel(isCurrent && player.isPlaying ? "Pause" : "Play \(episode.title)")
        .accessibilityValue(episode.remainingLabel)
    }
}

struct EpisodeMenuItems: View {
    let episode: PodcastEpisode
    var inUpNext = false

    var body: some View {
        Button("Play", systemImage: "play.fill") { PodcastActions.play(episode) }
        Button("Play Next", systemImage: "text.line.first.and.arrowtriangle.forward") {
            AudioPlayer.shared.playNext([AudioItem(episode: episode)])
        }
        Divider()
        if inUpNext || episode.inQueue == true {
            Button("Remove from Up Next", systemImage: "minus.circle") { PodcastActions.removeFromUpNext(episode) }
        } else {
            Button("Add to Top of Up Next", systemImage: "text.insert") { PodcastActions.addToUpNext(episode, position: "next") }
            Button("Add to End of Up Next", systemImage: "text.append") { PodcastActions.addToUpNext(episode, position: "last") }
        }
        Button(episode.isPlayed ? "Mark as Unplayed" : "Mark as Played", systemImage: episode.isPlayed ? "circle" : "checkmark.circle") {
            PodcastActions.setPlayed(episode, !episode.isPlayed)
        }
        Divider()
        if episode.download?.isDone != true {
            Button("Download on Server", systemImage: "icloud.and.arrow.down") { PodcastActions.downloadOnServer(episode) }
        }
        if AudioDownloads.shared.isSaved(episode: episode) {
            Button("Remove from This Device", systemImage: "trash") { AudioDownloads.shared.remove("episode", episode.id) }
        } else {
            Button("Download to This Device", systemImage: "arrow.down.circle") { AudioDownloads.shared.save(episode: episode) }
        }
        if let podcastId = episode.podcastId {
            Button("Go to Show", systemImage: "square.stack") { AudioRouter.shared.open(.show(podcastId)) }
        }
        if let link = episode.link, let url = URL(string: link) {
            ShareLink(item: url) { Label("Share Episode", systemImage: "square.and.arrow.up") }
        }
    }
}

// A card for the Listen Now rows: big art, the show, the title, and the play pill.
struct EpisodeCard: View {
    let episode: PodcastEpisode
    var list: [PodcastEpisode] = []
    var width: CGFloat = 220
    var inUpNext = false

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            NavigationLink {
                PodcastEpisodeView(episodeId: episode.id, initial: episode)
            } label: {
                VStack(alignment: .leading, spacing: 6) {
                    ArtworkImage(path: episode.artUrl, size: width, corner: 12, placeholder: "mic")
                        .shadow(color: .black.opacity(0.15), radius: 4, y: 2)
                    Text(episode.podcastTitle?.uppercased() ?? "").font(.caption2.weight(.semibold)).foregroundStyle(.secondary).lineLimit(1)
                    Text(episode.title).font(.subheadline.weight(.semibold)).lineLimit(2).multilineTextAlignment(.leading)
                        .frame(height: 40, alignment: .top)
                }
            }
            .buttonStyle(.plain)
            EpisodePlayButton(episode: episode, list: list)
        }
        .frame(width: width)
        .contextMenu { EpisodeMenuItems(episode: episode, inUpNext: inUpNext) }
    }
}

struct PodcastTile: View {
    let podcast: Podcast
    var width: CGFloat? = 150

    var body: some View {
        NavigationLink {
            PodcastShowView(podcastId: podcast.id, initial: podcast)
        } label: {
            VStack(alignment: .leading, spacing: 5) {
                ArtworkImage(path: podcast.artUrl, size: width, corner: 10, placeholder: "mic")
                    .shadow(color: .black.opacity(0.15), radius: 4, y: 2)
                    .overlay(alignment: .topTrailing) {
                        if let unplayed = podcast.unplayedCount, unplayed > 0 {
                            Text("\(unplayed)")
                                .font(.caption2.weight(.bold))
                                .padding(.horizontal, 6).padding(.vertical, 2)
                                .background(SentinelTheme.green, in: Capsule())
                                .foregroundStyle(.white)
                                .padding(6)
                                .accessibilityLabel("\(unplayed) unplayed")
                        }
                    }
                Text(podcast.title).font(.subheadline.weight(.medium)).lineLimit(1)
                if let author = podcast.author { Text(author).font(.caption).foregroundStyle(.secondary).lineLimit(1) }
            }
            .frame(width: width)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

// A show from the Apple directory (search, charts, recommended).
struct DirectoryShowRow: View {
    let show: DirectoryShow
    var rank: Int? = nil

    var body: some View {
        NavigationLink {
            if let podcastId = show.podcastId, show.subscribed == true {
                PodcastShowView(podcastId: podcastId)
            } else {
                PodcastPreviewView(show: show)
            }
        } label: {
            HStack(spacing: 12) {
                if let rank { Text("\(rank)").font(.headline.monospacedDigit()).foregroundStyle(.secondary).frame(width: 28) }
                ArtworkImage(path: show.artUrl, size: 60, corner: 8, placeholder: "mic")
                VStack(alignment: .leading, spacing: 2) {
                    Text(show.title).lineLimit(2)
                    Text([show.author, show.genre].compactMap { $0 }.joined(separator: " · ")).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
                Spacer()
                if show.subscribed == true {
                    Image(systemName: "checkmark.circle.fill").foregroundStyle(SentinelTheme.green).accessibilityLabel("Following")
                }
            }
        }
    }
}

// MARK: - Bookmarks and clips (also used from Now Playing)

struct BookmarkDraft: Identifiable {
    let id = UUID()
    let episode: PodcastEpisode
    let position: Double
}

struct BookmarkNoteSheet: View {
    let draft: BookmarkDraft
    @Environment(\.dismiss) private var dismiss
    @State private var note = ""
    @State private var saving = false
    @State private var errorMessage: String?

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    LabeledContent("Episode", value: draft.episode.title)
                    LabeledContent("At", value: AudioFormat.duration(draft.position))
                }
                Section("Note") {
                    TextField("", text: $note, prompt: Text("What was good about this bit? (optional)"), axis: .vertical)
                        .labelsHidden()
                        .lineLimit(3...8)
                        .multilineTextAlignment(.leading)
                        .accessibilityLabel("Note")
                }
                if let errorMessage { Section { Text(errorMessage).foregroundStyle(SentinelTheme.red) } }
            }
            .formStyle(.grouped)
            .navigationTitle("Bookmark")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button(saving ? "Saving…" : "Save") { Task { await save() } }.disabled(saving)
                }
            }
        }
        #if os(macOS)
        .frame(minWidth: 460, minHeight: 360)
        #endif
    }

    private func save() async {
        saving = true
        defer { saving = false }
        do {
            _ = try await AppModel.shared.client.addBookmark(episodeId: draft.episode.id, position: draft.position, note: note)
            AudioToast.shared.show("Bookmarked at \(AudioFormat.duration(draft.position))")
            NotificationCenter.default.post(name: .podcastsChanged, object: nil)
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

struct ClipRequest: Identifiable {
    let id = UUID()
    let episode: PodcastEpisode
    let start: Double
}

// Clip maker: pick a start and a length (5-180 s), the server cuts it with ffmpeg, and the m4a is
// handed to the share sheet.
struct ClipMakerView: View {
    let request: ClipRequest
    @Environment(\.dismiss) private var dismiss
    @State private var start: Double = 0
    @State private var length: Double = 30
    @State private var title = ""
    @State private var making = false
    @State private var file: URL?
    @State private var errorMessage: String?
    @State private var seeded = false

    private var maxStart: Double { max(0, request.episode.duration - 5) }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    LabeledContent("Episode", value: request.episode.title)
                    VStack(alignment: .leading) {
                        Text("Starts at \(AudioFormat.duration(start))")
                        Slider(value: $start, in: 0...max(maxStart, 1), step: 1)
                        HStack {
                            Button("−5 s") { start = max(0, start - 5) }
                            Button("+5 s") { start = min(maxStart, start + 5) }
                            Spacer()
                            Button("Use Current Position") { start = AudioPlayer.shared.position }
                                .disabled(AudioPlayer.shared.current?.episode?.id != request.episode.id)
                        }
                        .buttonStyle(.borderless)
                    }
                    VStack(alignment: .leading) {
                        Text("Length \(Int(length)) seconds")
                        Slider(value: $length, in: 5...180, step: 5)
                    }
                    Button {
                        AudioPlayer.shared.play(episode: request.episode, at: start)
                    } label: {
                        Label("Preview from Start", systemImage: "play.circle")
                    }
                }
                Section("Title") {
                    TextField("", text: $title, prompt: Text("Optional"))
                        .labelsHidden()
                        .multilineTextAlignment(.leading)
                        .accessibilityLabel("Clip title")
                }
                Section {
                    if let file {
                        ShareLink(item: file) {
                            Label("Share Clip", systemImage: "square.and.arrow.up")
                        }
                        Text("Clip ready: \(Int(length)) seconds.").font(.footnote).foregroundStyle(.secondary)
                    } else {
                        Button {
                            Task { await make() }
                        } label: {
                            HStack {
                                Label("Make Clip", systemImage: "scissors")
                                if making { Spacer(); ProgressView() }
                            }
                        }
                        .disabled(making)
                    }
                    if let errorMessage { Text(errorMessage).foregroundStyle(SentinelTheme.red) }
                } footer: {
                    Text("Your server cuts the clip, so the episode needs to be downloaded on it or reachable.")
                }
            }
            .formStyle(.grouped)
            .navigationTitle("Make a Clip")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } } }
            .onAppear {
                guard !seeded else { return }
                seeded = true
                start = min(request.start, maxStart)
            }
        }
        #if os(macOS)
        .frame(minWidth: 520, minHeight: 520)
        #endif
    }

    private func make() async {
        making = true
        defer { making = false }
        errorMessage = nil
        let client = AppModel.shared.client
        do {
            let clip = try await client.makeClip(episodeId: request.episode.id, start: start, duration: length, title: title)
            guard let url = client.audioURL(clip.url ?? "/api/podcasts/clips/\(clip.id).m4a") else { throw ClientError.invalidServerAddress }
            let (data, response) = try await URLSession.shared.data(from: url)
            _ = try client.validated(data, response)
            let name = (title.isEmpty ? "\(request.episode.podcastTitle ?? "Clip") - \(request.episode.title)" : title)
                .replacingOccurrences(of: "/", with: "-").prefix(80)
            let local = FileManager.default.temporaryDirectory.appendingPathComponent("\(name).m4a")
            try data.write(to: local, options: .atomic)
            file = local
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}
