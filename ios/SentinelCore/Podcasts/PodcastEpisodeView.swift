import SwiftUI

// One episode: art, play, the show notes (the server's sanitized HTML, rendered as rich text with
// working links), chapters, the transcript, bookmarks, download and transcribe.
struct PodcastEpisodeView: View {
    let episodeId: String
    var initial: PodcastEpisode? = nil
    @EnvironmentObject private var model: AppModel
    @State private var detail: PodcastEpisodeDetail?
    @State private var notes: AttributedString?
    @State private var chapters: [PodcastChapter] = []
    @State private var bookmarks: [PodcastBookmark] = []
    @State private var errorMessage: String?
    @State private var notice: String?
    @State private var showingTranscript = false
    @State private var clipRequest: ClipRequest?
    @State private var bookmarkDraft: BookmarkDraft?

    private var episode: PodcastEpisode? { detail?.episode ?? initial }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                if let episode {
                    header(episode)
                    actions(episode)
                    if let notice { Text(notice).font(.footnote).foregroundStyle(.secondary) }
                    if let notes {
                        Text(notes)
                            .font(.body)
                            .textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    } else if let text = detail?.descriptionText ?? episode.summary {
                        Text(text).textSelection(.enabled)
                    }
                    if !chapters.isEmpty { chaptersSection(episode) }
                    if !bookmarks.isEmpty { bookmarksSection }
                }
                if let errorMessage { Text(errorMessage).foregroundStyle(SentinelTheme.red) }
            }
            .padding()
            .frame(maxWidth: 760, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        .navigationTitle(episode?.podcastTitle ?? "Episode")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                if let episode {
                    Menu {
                        EpisodeMenuItems(episode: episode)
                    } label: {
                        Label("More", systemImage: "ellipsis.circle")
                    }
                }
            }
        }
        .sheet(isPresented: $showingTranscript) {
            if let episode {
                NavigationStack {
                    TranscriptPanel(episode: episode)
                        .navigationTitle("Transcript")
                        #if os(iOS)
                        .navigationBarTitleDisplayMode(.inline)
                        #endif
                        .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { showingTranscript = false } } }
                }
                #if os(macOS)
                .frame(minWidth: 560, minHeight: 620)
                #endif
            }
        }
        .sheet(item: $clipRequest) { ClipMakerView(request: $0) }
        .sheet(item: $bookmarkDraft, onDismiss: { Task { await loadBookmarks() } }) { BookmarkNoteSheet(draft: $0) }
        .task { await load() }
        .onReceive(NotificationCenter.default.publisher(for: .podcastsChanged)) { _ in Task { await load() } }
    }

    private func header(_ episode: PodcastEpisode) -> some View {
        HStack(alignment: .top, spacing: 16) {
            ArtworkImage(path: episode.artUrl, size: 120, corner: 12, placeholder: "mic")
                .shadow(color: .black.opacity(0.2), radius: 8, y: 4)
            VStack(alignment: .leading, spacing: 6) {
                if let podcastId = episode.podcastId {
                    NavigationLink {
                        PodcastShowView(podcastId: podcastId)
                    } label: {
                        Text(episode.podcastTitle ?? "Show").font(.subheadline.weight(.semibold)).foregroundStyle(SentinelTheme.green)
                    }
                    .buttonStyle(.plain)
                }
                Text(episode.title).font(.title2.bold())
                Text([AudioFormat.relative(episode.publishedAt), episode.numberLabel,
                      episode.duration > 0 ? AudioFormat.duration(episode.duration, style: .short) : nil,
                      episode.explicit == true ? "Explicit" : nil, episode.archived == true ? "No longer in the feed" : nil]
                    .compactMap { $0 }.joined(separator: " · "))
                    .font(.caption).foregroundStyle(.secondary)
                if let saved = episode.download?.timeSavedSeconds, saved >= 30 {
                    Label("Trimmed \(AudioFormat.duration(saved, style: .short)) of silence", systemImage: "scissors")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
        }
    }

    private func actions(_ episode: PodcastEpisode) -> some View {
        let player = AudioPlayer.shared
        let isCurrent = player.current?.episode?.id == episode.id
        return VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 10) {
                Button {
                    if isCurrent { player.togglePlayPause() } else { PodcastActions.play(episode) }
                } label: {
                    // remainingLabel is "Played" for a finished episode, which made "Play Played".
                    Label(isCurrent && player.isPlaying ? "Pause" : (episode.isInProgress ? "Resume · \(episode.remainingLabel)" : episode.isPlayed ? "Play Again" : "Play \(episode.remainingLabel)"),
                          systemImage: isCurrent && player.isPlaying ? "pause.fill" : "play.fill")
                        .lineLimit(1)
                        .minimumScaleFactor(0.8)
                        .frame(minWidth: 160)
                }
                .buttonStyle(.borderedProminent).foregroundStyle(Color.onSentinelGreen)
                .tint(SentinelTheme.green)
                Button {
                    PodcastActions.addToUpNext(episode, position: "last")
                } label: {
                    Label(episode.inQueue == true ? "In Up Next" : "Up Next", systemImage: episode.inQueue == true ? "checkmark" : "text.append")
                }
                .buttonStyle(.bordered)
                .disabled(episode.inQueue == true)
            }
            .controlSize(.large)
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 8) {
                    chip(episode.isPlayed ? "Mark Unplayed" : "Mark Played", episode.isPlayed ? "circle" : "checkmark.circle") {
                        PodcastActions.setPlayed(episode, !episode.isPlayed)
                    }
                    if AudioDownloads.shared.isSaved(episode: episode) {
                        chip("On This Device", "checkmark.icloud") { AudioDownloads.shared.remove("episode", episode.id) }
                    } else if let progress = AudioDownloads.shared.progress("episode", episode.id) {
                        chip(progress < 0 ? "Waiting…" : "Downloading \(Int(progress * 100))%", "arrow.down.circle") {
                            AudioDownloads.shared.cancel("episode", episode.id)
                        }
                    } else {
                        chip("Download", "arrow.down.circle") { AudioDownloads.shared.save(episode: episode) }
                    }
                    serverDownloadChip(episode)
                    if episode.hasTranscript == true {
                        chip("Transcript", "text.quote") { showingTranscript = true }
                    } else {
                        chip("Transcribe", "waveform") { Task { await transcribe(episode) } }
                    }
                    chip("Bookmark", "bookmark") {
                        bookmarkDraft = BookmarkDraft(episode: episode, position: isCurrent ? player.position : (episode.progress?.position ?? 0))
                    }
                    chip("Make a Clip", "scissors") {
                        clipRequest = ClipRequest(episode: episode, start: isCurrent ? max(0, player.position - 30) : (episode.progress?.position ?? 0))
                    }
                    if let link = episode.link, let url = URL(string: link) {
                        ShareLink(item: url) {
                            Label("Share", systemImage: "square.and.arrow.up").font(.caption.weight(.semibold))
                                .padding(.horizontal, 10).padding(.vertical, 6)
                                .background(.quaternary, in: Capsule())
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
        }
    }

    @ViewBuilder
    private func serverDownloadChip(_ episode: PodcastEpisode) -> some View {
        switch episode.download?.state {
        case "done":
            Label(episode.download?.enhanced == true ? "On Server · Enhanced" : "On Server", systemImage: "externaldrive.fill.badge.checkmark")
                .font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                .padding(.horizontal, 10).padding(.vertical, 6)
        case "queued", "downloading":
            Label("Server downloading \(Int((episode.download?.progress ?? 0) * 100))%", systemImage: "icloud.and.arrow.down")
                .font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                .padding(.horizontal, 10).padding(.vertical, 6)
        default:
            chip(episode.download?.state == "failed" ? "Retry Server Download" : "Download on Server", "icloud.and.arrow.down") {
                PodcastActions.downloadOnServer(episode)
            }
        }
    }

    private func chip(_ title: String, _ icon: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Label(title, systemImage: icon)
                .font(.caption.weight(.semibold))
                .padding(.horizontal, 10).padding(.vertical, 6)
                .background(.quaternary, in: Capsule())
        }
        .buttonStyle(.plain)
    }

    private func chaptersSection(_ episode: PodcastEpisode) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Chapters").font(.title3.bold())
            ForEach(chapters) { chapter in
                Button {
                    playFrom(episode, chapter.startSeconds)
                } label: {
                    HStack(spacing: 10) {
                        Text(AudioFormat.duration(chapter.startSeconds)).font(.caption.monospacedDigit()).foregroundStyle(.secondary).frame(width: 56, alignment: .leading)
                        Text(chapter.title).foregroundStyle(.primary)
                        Spacer()
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                Divider()
            }
        }
    }

    private var bookmarksSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Bookmarks").font(.title3.bold())
            ForEach(bookmarks) { bookmark in
                BookmarkRow(bookmark: bookmark, showEpisode: false) { Task { await loadBookmarks() } }
                Divider()
            }
        }
    }

    private func playFrom(_ episode: PodcastEpisode, _ seconds: Double) {
        let player = AudioPlayer.shared
        if player.current?.episode?.id == episode.id {
            player.seek(to: seconds)
            if !player.isPlaying { player.resume() }
        } else {
            player.play(episode: episode, at: seconds)
        }
    }

    private func transcribe(_ episode: PodcastEpisode) async {
        do {
            _ = try await model.client.transcribeEpisode(id: episode.id)
            notice = "Your server is writing a transcript. It appears here when it's done - longer episodes take a while."
        } catch {
            notice = error.localizedDescription
        }
    }

    private func load() async {
        do {
            let loaded = try await model.client.episode(id: episodeId)
            detail = loaded
            if let html = loaded.description, !html.isEmpty { notes = EpisodeNotes.render(html) }
            errorMessage = nil
            if loaded.episode.hasChapters == true {
                chapters = (try? await model.client.episodeChapters(id: episodeId)) ?? []
            }
            await loadBookmarks()
        } catch {
            if !error.isCancellation { errorMessage = error.localizedDescription }
        }
    }

    private func loadBookmarks() async {
        bookmarks = (try? await model.client.bookmarks(episodeId: episodeId)) ?? []
    }
}

// The show notes are a small sanitized subset of HTML (§5: p, br, a, b, i, em, strong, ul, ol,
// li, blockquote). Foundation's HTML importer turns that into attributed text; the fonts and
// colours it picks are dropped so the text follows the app's own type and dark mode.
@MainActor
enum EpisodeNotes {
    // The importer keeps list structure as NSTextList attributes, which SwiftUI's Text drops -
    // bullets and numbers vanished. So lists become plain "•" / "1." lines first, and paragraphs
    // get a blank line between them.
    static func flatten(_ html: String) -> String {
        var output = ""
        var lists: [(ordered: Bool, count: Int)] = []
        var rest = Substring(html)
        while let open = rest.firstIndex(of: "<") {
            output += rest[..<open]
            guard let close = rest[open...].firstIndex(of: ">") else { output += rest[open...]; rest = ""; break }
            let tag = rest[rest.index(after: open)..<close].lowercased()
            let name = tag.split(whereSeparator: { $0 == " " || $0 == "/" }).first.map(String.init) ?? ""
            let closing = tag.hasPrefix("/")
            switch (name, closing) {
            case ("ul", false): lists.append((false, 0))
            case ("ol", false): lists.append((true, 0))
            case ("ul", true), ("ol", true):
                if !lists.isEmpty { lists.removeLast() }
                output += lists.isEmpty ? "<br><br>" : ""
            case ("li", false):
                var marker = "•"
                if var last = lists.popLast() {
                    last.count += 1
                    if last.ordered { marker = "\(last.count)." }
                    lists.append(last)
                }
                output += (output.hasSuffix("<br><br>") || output.isEmpty ? "" : "<br>") + String(repeating: "&nbsp;&nbsp;&nbsp;", count: max(0, lists.count - 1)) + marker + "&nbsp;"
            case ("li", true):
                break
            case ("p", true):
                output += "<br><br>"
            case ("p", false):
                break
            default:
                output += rest[open...close]
            }
            rest = rest[rest.index(after: close)...]
        }
        output += rest
        return output
    }

    static func render(_ html: String) -> AttributedString? {
        let wrapped = "<meta charset=\"utf-8\"><div style=\"font-family: -apple-system; font-size: 15px\">\(flatten(html))</div>"
        guard let data = wrapped.data(using: .utf8),
              let ns = try? NSAttributedString(data: data, options: [
                .documentType: NSAttributedString.DocumentType.html,
                .characterEncoding: String.Encoding.utf8.rawValue,
              ], documentAttributes: nil) else { return nil }
        // Rebuilt run by run as SwiftUI attributes: SwiftUI's Text ignores UIKit/AppKit fonts, so
        // bold, italic and links are carried across by hand and everything else is left to Text.
        var result = AttributedString()
        let text = ns.string as NSString
        ns.enumerateAttributes(in: NSRange(location: 0, length: ns.length)) { attributes, range, _ in
            var piece = AttributedString(text.substring(with: range))
            var bold = false, italic = false
            #if os(iOS)
            if let font = attributes[.font] as? UIFont {
                bold = font.fontDescriptor.symbolicTraits.contains(.traitBold)
                italic = font.fontDescriptor.symbolicTraits.contains(.traitItalic)
            }
            #else
            if let font = attributes[.font] as? NSFont {
                bold = font.fontDescriptor.symbolicTraits.contains(.bold)
                italic = font.fontDescriptor.symbolicTraits.contains(.italic)
            }
            #endif
            var font = Font.body
            if bold { font = font.bold() }
            if italic { font = font.italic() }
            piece.font = font
            if let link = attributes[.link] as? URL {
                piece.link = link
            } else if let link = attributes[.link] as? String, let url = URL(string: link) {
                piece.link = url
            }
            result += piece
        }
        // The importer ends with a newline or two.
        while let last = result.characters.last, last.isNewline {
            result.characters.removeLast()
        }
        return result
    }
}

// MARK: - Bookmarks

struct BookmarkRow: View {
    let bookmark: PodcastBookmark
    var showEpisode = true
    var onChange: (() -> Void)? = nil
    @State private var editing = false
    @State private var note = ""

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            if showEpisode { ArtworkImage(path: bookmark.artUrl, size: 48, corner: 6, placeholder: "bookmark") }
            VStack(alignment: .leading, spacing: 4) {
                if showEpisode {
                    Text(bookmark.podcastTitle ?? "").font(.caption2.weight(.semibold)).foregroundStyle(.secondary)
                    Text(bookmark.episodeTitle ?? "").font(.subheadline.weight(.semibold)).lineLimit(2)
                }
                Button {
                    play()
                } label: {
                    Label(AudioFormat.duration(bookmark.positionSeconds), systemImage: "play.circle.fill")
                        .font(.caption.weight(.semibold).monospacedDigit())
                        .foregroundStyle(SentinelTheme.green)
                }
                .buttonStyle(.plain)
                if let note = bookmark.note, !note.isEmpty { Text(note).font(.subheadline) }
                if let snippet = bookmark.transcriptSnippet, !snippet.isEmpty {
                    Text("\u{201C}\(snippet)\u{201D}").font(.caption).italic().foregroundStyle(.secondary).lineLimit(4)
                }
            }
            Spacer()
            Menu {
                Button("Play from Here", systemImage: "play.fill", action: play)
                Button("Edit Note", systemImage: "pencil") {
                    note = bookmark.note ?? ""
                    editing = true
                }
                Button("Save to Organizer Note", systemImage: "note.text.badge.plus") {
                    Task {
                        do {
                            _ = try await AppModel.shared.client.bookmarkToNote(id: bookmark.id)
                            AudioToast.shared.show("Saved to your notes")
                        } catch {
                            AudioToast.shared.show(error.localizedDescription)
                        }
                    }
                }
                Button("Delete", systemImage: "trash", role: .destructive) {
                    Task {
                        try? await AppModel.shared.client.deleteBookmark(id: bookmark.id)
                        onChange?()
                        NotificationCenter.default.post(name: .podcastsChanged, object: nil)
                    }
                }
            } label: {
                Image(systemName: "ellipsis").frame(width: 28, height: 24).contentShape(Rectangle())
            }
            .menuStyle(.button)
            .buttonStyle(.plain)
            .menuIndicator(.hidden)
            .foregroundStyle(.secondary)
            .accessibilityLabel("Bookmark options")
        }
        .alert("Bookmark Note", isPresented: $editing) {
            TextField("Note", text: $note)
            Button("Save") {
                Task {
                    _ = try? await AppModel.shared.client.updateBookmark(id: bookmark.id, note: note)
                    onChange?()
                    NotificationCenter.default.post(name: .podcastsChanged, object: nil)
                }
            }
            Button("Cancel", role: .cancel) {}
        }
    }

    private func play() {
        Task {
            guard let detail = try? await AppModel.shared.client.episode(id: bookmark.episodeId) else { return }
            let player = AudioPlayer.shared
            if player.current?.episode?.id == bookmark.episodeId {
                player.seek(to: bookmark.positionSeconds)
                if !player.isPlaying { player.resume() }
            } else {
                player.play(episode: detail.episode, at: bookmark.positionSeconds)
            }
        }
    }
}
