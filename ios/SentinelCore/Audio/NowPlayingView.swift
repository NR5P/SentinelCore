import MediaPlayer
import SwiftUI

// The full Now Playing screen: big art, the scrubber and transport, and beside or under it one of
// lyrics (synced ones follow along; tap a line to jump there), a podcast's transcript (follows
// playback, searchable, tap to jump), its chapters, or the queue. A sheet on iPhone and iPad, its
// own window on the Mac.
struct NowPlayingView: View {
    static let windowId = "now-playing"

    enum Panel: String { case none, lyrics, transcript, chapters, queue }

    @Environment(\.dismiss) private var dismiss
    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
    @AppStorage("nowPlaying.panel") private var panel: Panel = .none
    @State private var scrubbing: Double?
    @State private var showingPlayOn = false
    @State private var clipRequest: ClipRequest?
    @State private var bookmarkNote: BookmarkDraft?

    private var player: AudioPlayer { AudioPlayer.shared }
    private var wide: Bool {
        #if os(macOS)
        true
        #else
        horizontalSizeClass == .regular
        #endif
    }

    var body: some View {
        let entry = player.current
        ZStack {
            if entry == nil && player.speaking == nil {
                ContentUnavailableView("Nothing playing", systemImage: "music.note", description: Text("Play something from Music or Podcasts."))
            } else if wide {
                HStack(spacing: 32) {
                    mainColumn(entry, artSize: 340)
                        .frame(maxWidth: 420)
                    if panel != .none {
                        panelView(entry)
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                            .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 16))
                    }
                }
                .padding(28)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                VStack(spacing: 0) {
                    grabber
                    if panel == .none {
                        Spacer(minLength: 8)
                        mainColumn(entry, artSize: 320)
                            .padding(.horizontal, 24)
                        Spacer(minLength: 8)
                    } else {
                        compactHeader(entry)
                        panelView(entry)
                            .frame(maxHeight: .infinity)
                        controls(entry)
                            .padding(.horizontal, 24)
                    }
                    bottomRow(entry)
                        .padding(.horizontal, 24)
                        .padding(.bottom, 12)
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        // A background, not a ZStack layer: the blurred art is filled to the screen's height, and
        // as a layer it made the whole screen as wide as that square.
        .background { background(entry) }
        .foregroundStyle(.white)
        .tint(.white)
        .environment(\.colorScheme, .dark)
        .sheet(isPresented: $showingPlayOn) { SpeakerPickerView() }
        .sheet(item: $clipRequest) { ClipMakerView(request: $0) }
        .sheet(item: $bookmarkNote) { BookmarkNoteSheet(draft: $0) }
        #if os(macOS)
        .frame(minWidth: 760, minHeight: 620)
        #endif
    }

    // MARK: Pieces

    private func background(_ entry: AudioItem?) -> some View {
        Color.black
            .overlay {
                GeometryReader { proxy in
                    let side = max(proxy.size.width, proxy.size.height)
                    ArtworkImage(path: entry?.artPath, size: side, corner: 0, placeholder: entry?.isEpisode == true ? "mic" : "music.note")
                        .blur(radius: 60)
                        .opacity(0.7)
                        .position(x: proxy.size.width / 2, y: proxy.size.height / 2)
                }
            }
            .overlay {
                LinearGradient(colors: [.black.opacity(0.15), .black.opacity(0.6)], startPoint: .top, endPoint: .bottom)
            }
            .clipped()
            .ignoresSafeArea()
    }

    private var grabber: some View {
        Button {
            dismiss()
        } label: {
            Capsule().fill(.white.opacity(0.5)).frame(width: 40, height: 5).padding(10)
        }
        .buttonStyle(.plain)
        .accessibilityLabel("Close Now Playing")
    }

    private func mainColumn(_ entry: AudioItem?, artSize: CGFloat) -> some View {
        VStack(spacing: 22) {
            ArtworkImage(path: entry?.artPath, corner: 14, placeholder: entry?.isEpisode == true ? "mic" : "music.note")
                .frame(maxWidth: artSize, maxHeight: artSize)
                .scaleEffect(player.isPlaying ? 1 : 0.88)
                .shadow(color: .black.opacity(0.5), radius: 24, y: 12)
                .animation(.spring(response: 0.45, dampingFraction: 0.75), value: player.isPlaying)
            titleRow(entry)
            controls(entry)
            if !wide { EmptyView() } else { bottomRow(entry) }
        }
    }

    private func compactHeader(_ entry: AudioItem?) -> some View {
        HStack(spacing: 12) {
            ArtworkImage(path: entry?.artPath, size: 56, corner: 8)
            VStack(alignment: .leading, spacing: 2) {
                Text(entry?.title ?? "").font(.headline).lineLimit(1)
                Text(entry?.subtitle ?? "").font(.subheadline).foregroundStyle(.white.opacity(0.7)).lineLimit(1)
            }
            Spacer()
            loveOrBookmark(entry)
        }
        .padding(.horizontal, 24)
        .padding(.bottom, 8)
    }

    private func titleRow(_ entry: AudioItem?) -> some View {
        HStack(alignment: .center, spacing: 12) {
            VStack(alignment: .leading, spacing: 3) {
                if let speaking = player.speaking {
                    Text("Morning Briefing").font(.title3.bold())
                    Text(speaking).font(.body).foregroundStyle(.white.opacity(0.8)).lineLimit(3)
                } else {
                    Button {
                        goToItem(entry)
                    } label: {
                        Text(entry?.title ?? "").font(.title3.bold()).lineLimit(2).multilineTextAlignment(.leading)
                    }
                    .buttonStyle(.plain)
                    Button {
                        goToArtist(entry)
                    } label: {
                        Text(entry?.subtitle ?? "").font(.body).foregroundStyle(.white.opacity(0.75)).lineLimit(1)
                    }
                    .buttonStyle(.plain)
                    if let format = entry?.track?.formatLine {
                        Text(format)
                            .font(.caption2.weight(.semibold))
                            .padding(.horizontal, 6).padding(.vertical, 2)
                            .overlay(Capsule().strokeBorder(.white.opacity(0.4)))
                            .foregroundStyle(.white.opacity(0.7))
                    }
                }
            }
            Spacer()
            loveOrBookmark(entry)
            Menu {
                moreMenu(entry)
            } label: {
                Image(systemName: "ellipsis.circle.fill").font(.title2).symbolRenderingMode(.hierarchical)
            }
            .menuStyle(.button)
            .buttonStyle(.plain)
            .menuIndicator(.hidden)
            .accessibilityLabel("More")
        }
    }

    @ViewBuilder
    private func loveOrBookmark(_ entry: AudioItem?) -> some View {
        if let track = entry?.track {
            let loved = MusicState.shared.isLoved(track)
            Button {
                MusicState.shared.toggleLove(track)
            } label: {
                Image(systemName: loved ? "heart.fill" : "heart").font(.title2).foregroundStyle(loved ? .pink : .white)
            }
            .buttonStyle(.plain)
            .accessibilityLabel(loved ? "Unlove" : "Love")
        } else if let episode = entry?.episode {
            Button {
                bookmarkNote = BookmarkDraft(episode: episode, position: player.position)
            } label: {
                Image(systemName: "bookmark").font(.title2)
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Bookmark this moment")
        }
    }

    @ViewBuilder
    private func moreMenu(_ entry: AudioItem?) -> some View {
        if let track = entry?.track {
            TrackMenuItems(track: track)
        } else if let episode = entry?.episode {
            Button("Bookmark This Moment", systemImage: "bookmark") {
                bookmarkNote = BookmarkDraft(episode: episode, position: player.position)
            }
            Button("Make a Clip…", systemImage: "scissors") {
                clipRequest = ClipRequest(episode: episode, start: max(0, player.position - 30))
            }
            Button("Go to Episode", systemImage: "info.circle") { goToItem(entry); dismiss() }
            Button("Go to Show", systemImage: "square.stack") { goToArtist(entry); dismiss() }
            Button("Mark as Played", systemImage: "checkmark.circle") {
                Task { try? await AppModel.shared.client.setEpisodePlayed(id: episode.id, played: true) }
                player.next()
            }
            if AudioDownloads.shared.isSaved(episode: episode) {
                Button("Remove Download", systemImage: "trash") { AudioDownloads.shared.remove("episode", episode.id) }
            } else {
                Button("Download to This Device", systemImage: "arrow.down.circle") { AudioDownloads.shared.save(episode: episode) }
            }
        }
        Divider()
        Button("Stop", systemImage: "stop.fill", role: .destructive) { player.stop(); dismiss() }
    }

    private func controls(_ entry: AudioItem?) -> some View {
        VStack(spacing: 18) {
            scrubber(entry)
            HStack {
                if entry?.isEpisode == true {
                    Spacer()
                    transportButton("gobackward.15", "Back 15 seconds", size: 30) { player.skip(by: -15) }
                } else {
                    transportButton("shuffle", player.shuffle ? "Shuffle on" : "Shuffle off", size: 18, active: player.shuffle) { player.toggleShuffle() }
                    Spacer()
                    transportButton("backward.fill", "Previous", size: 30) { player.previous() }
                }
                Spacer()
                transportButton(player.isPlaying ? "pause.circle.fill" : "play.circle.fill", player.isPlaying ? "Pause" : "Play", size: 64) {
                    player.togglePlayPause()
                }
                Spacer()
                if entry?.isEpisode == true {
                    transportButton("goforward.30", "Forward 30 seconds", size: 30) { player.skip(by: 30) }
                    Spacer()
                } else {
                    transportButton("forward.fill", "Next", size: 30) { player.next() }
                    Spacer()
                    transportButton(player.repeatMode.icon, "Repeat \(player.repeatMode.rawValue)", size: 18, active: player.repeatMode != .off) { player.cycleRepeat() }
                }
            }
            volume
        }
    }

    private func transportButton(_ symbol: String, _ label: String, size: CGFloat, active: Bool = false, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: size, weight: .semibold))
                .foregroundStyle(active ? SentinelTheme.green : .white)
                .padding(size < 24 ? 8 : 0)
                .background(active ? Circle().fill(.white.opacity(0.15)) : nil)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(label)
    }

    private func scrubber(_ entry: AudioItem?) -> some View {
        let duration = max(player.duration, 1)
        let shown = scrubbing ?? player.position
        return VStack(spacing: 4) {
            Slider(value: Binding(get: { min(shown, duration) }, set: { scrubbing = $0 }), in: 0...duration) { editing in
                if !editing, let scrubbing {
                    player.seek(to: scrubbing)
                    self.scrubbing = nil
                }
            }
            .accessibilityLabel("Position")
            .accessibilityValue(AudioFormat.duration(shown))
            HStack {
                Text(AudioFormat.duration(shown))
                Spacer()
                if let chapter = player.currentChapter {
                    Text(chapter.title).lineLimit(1).foregroundStyle(.white.opacity(0.85))
                } else if player.speed != 1 {
                    Text("\(speedLabel(player.speed))").foregroundStyle(.white.opacity(0.85))
                }
                Spacer()
                Text("-" + AudioFormat.duration(max(0, (player.duration - shown) / max(player.speed, 0.1))))
            }
            .font(.caption.monospacedDigit())
            .foregroundStyle(.white.opacity(0.65))
        }
    }

    @ViewBuilder
    private var volume: some View {
        #if os(iOS)
        SystemVolumeSlider()
            .frame(height: 30)
        #else
        HStack {
            Image(systemName: "speaker.fill")
            Slider(value: Binding(get: { player.userVolume }, set: { player.setUserVolume($0) }), in: 0...1)
                .accessibilityLabel("Volume")
            Image(systemName: "speaker.wave.3.fill")
        }
        .foregroundStyle(.white.opacity(0.7))
        #endif
    }

    private func bottomRow(_ entry: AudioItem?) -> some View {
        HStack {
            Menu {
                Picker("Speed", selection: Binding(get: { player.speed }, set: { player.setSpeed($0) })) {
                    ForEach([0.5, 0.75, 0.9, 1.0, 1.1, 1.2, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0], id: \.self) { speed in
                        Text(speedLabel(speed)).tag(speed)
                    }
                }
            } label: {
                Text(speedLabel(player.speed))
                    .font(.subheadline.weight(.semibold).monospacedDigit())
                    .frame(minWidth: 44, minHeight: 32)
            }
            .menuStyle(.button)
            .buttonStyle(.plain)
            .menuIndicator(.hidden)
            .accessibilityLabel("Playback speed \(speedLabel(player.speed))")
            Spacer()
            SleepTimerMenu()
            Spacer()
            if entry?.isEpisode == true {
                panelButton(.transcript, "text.quote", "Transcript", enabled: entry?.episode?.hasTranscript == true)
                Spacer()
                panelButton(.chapters, "list.bullet.rectangle", "Chapters", enabled: !player.chapters.isEmpty)
            } else {
                panelButton(.lyrics, "quote.bubble", "Lyrics", enabled: entry?.track != nil)
            }
            Spacer()
            Button {
                showingPlayOn = true
            } label: {
                Image(systemName: SpeakerRemote.shared.target == nil ? "hifispeaker.2" : "hifispeaker.2.fill").font(.title3)
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Play on another speaker")
            Spacer()
            AirPlayButton().frame(width: 32, height: 32)
            Spacer()
            panelButton(.queue, "list.bullet", "Queue", enabled: true)
        }
        .font(.title3)
    }

    private func panelButton(_ target: Panel, _ symbol: String, _ label: String, enabled: Bool) -> some View {
        Button {
            withAnimation(.easeInOut(duration: 0.25)) { panel = panel == target ? .none : target }
        } label: {
            Image(systemName: symbol)
                .foregroundStyle(panel == target ? .black : .white)
                .padding(7)
                .background(panel == target ? Circle().fill(.white) : nil)
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
        .opacity(enabled ? 1 : 0.35)
        .accessibilityLabel(label)
        .accessibilityAddTraits(panel == target ? .isSelected : [])
    }

    @ViewBuilder
    private func panelView(_ entry: AudioItem?) -> some View {
        switch panel {
        case .lyrics:
            if let track = entry?.track { LyricsPanel(track: track) } else { EmptyView() }
        case .transcript:
            if let episode = entry?.episode { TranscriptPanel(episode: episode) } else { EmptyView() }
        case .chapters:
            ChaptersPanel()
        case .queue:
            QueuePanel()
        case .none:
            EmptyView()
        }
    }

    private func goToItem(_ entry: AudioItem?) {
        if let albumId = entry?.track?.albumId {
            AudioRouter.shared.open(.album(albumId))
            dismiss()
        } else if let episode = entry?.episode {
            AudioRouter.shared.open(.episode(episode.id))
            dismiss()
        }
    }

    private func goToArtist(_ entry: AudioItem?) {
        if let artistId = entry?.track?.artistId {
            AudioRouter.shared.open(.artist(artistId))
            dismiss()
        } else if let podcastId = entry?.episode?.podcastId {
            AudioRouter.shared.open(.show(podcastId))
            dismiss()
        }
    }
}

func speedLabel(_ speed: Double) -> String {
    let text = speed.rounded() == speed ? String(format: "%.0f", speed) : (speed * 10).rounded() == speed * 10 ? String(format: "%.1f", speed) : String(format: "%.2f", speed)
    return "\(text)×"
}

#if os(iOS)
// The system volume slider (it also follows the hardware buttons).
private struct SystemVolumeSlider: UIViewRepresentable {
    func makeUIView(context: Context) -> MPVolumeView {
        let view = MPVolumeView(frame: .zero)
        view.showsVolumeSlider = true
        view.tintColor = .white
        return view
    }
    func updateUIView(_ view: MPVolumeView, context: Context) {}
}
#endif

// MARK: - Sleep timer

struct SleepTimerMenu: View {
    var body: some View {
        let player = AudioPlayer.shared
        Menu {
            if player.sleepTimerActive {
                Button("Turn Off Sleep Timer", systemImage: "xmark") { player.cancelSleepTimer() }
                Divider()
            }
            ForEach([5, 10, 15, 30, 45, 60, 90], id: \.self) { minutes in
                Button("\(minutes) Minutes") { player.setSleepTimer(minutes: minutes) }
            }
            Button(player.isEpisode ? "End of Episode" : "End of Song") { player.setSleepAtEndOfItem() }
        } label: {
            TimelineView(.periodic(from: .now, by: 1)) { _ in
                HStack(spacing: 3) {
                    Image(systemName: player.sleepTimerActive ? "moon.zzz.fill" : "moon.zzz")
                    if let remaining = player.sleepRemaining, player.sleepEndsAt != nil {
                        Text(AudioFormat.duration(remaining)).font(.caption.monospacedDigit())
                    }
                }
                .foregroundStyle(player.sleepTimerActive ? SentinelTheme.green : .white)
            }
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        .accessibilityLabel(player.sleepTimerActive ? "Sleep timer on" : "Sleep timer")
    }
}

// MARK: - Lyrics

private struct LyricsPanel: View {
    let track: MusicTrack
    @State private var lyrics: MusicLyrics?
    @State private var missing = false
    @State private var loadedFor: String?

    var body: some View {
        let player = AudioPlayer.shared
        Group {
            if let lyrics, lyrics.synced == true, let lines = lyrics.lines, !lines.isEmpty {
                let currentIndex = lines.lastIndex { ($0.time ?? .infinity) <= player.position + 0.3 }
                ScrollViewReader { proxy in
                    ScrollView {
                        VStack(alignment: .leading, spacing: 18) {
                            ForEach(Array(lines.enumerated()), id: \.offset) { offset, line in
                                Text(line.text.isEmpty ? "♪" : line.text)
                                    .font(.title2.bold())
                                    .foregroundStyle(.white.opacity(offset == currentIndex ? 1 : 0.35))
                                    .scaleEffect(offset == currentIndex ? 1.03 : 1, anchor: .leading)
                                    .animation(.easeOut(duration: 0.25), value: currentIndex)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                    .contentShape(Rectangle())
                                    .onTapGesture { if let time = line.time { player.seek(to: time) } }
                                    .id(offset)
                                    .accessibilityAddTraits(.isButton)
                                    .accessibilityHint("Plays from this line")
                            }
                        }
                        .padding(24)
                        .padding(.vertical, 120)
                    }
                    .onChange(of: currentIndex) { _, index in
                        guard let index else { return }
                        withAnimation(.easeInOut(duration: 0.4)) { proxy.scrollTo(index, anchor: .center) }
                    }
                    .onAppear { if let currentIndex { proxy.scrollTo(currentIndex, anchor: .center) } }
                }
                .mask(LinearGradient(stops: [.init(color: .clear, location: 0), .init(color: .black, location: 0.12),
                                             .init(color: .black, location: 0.88), .init(color: .clear, location: 1)],
                                     startPoint: .top, endPoint: .bottom))
            } else if let lyrics {
                ScrollView {
                    Text(lyrics.plain ?? lyrics.lines?.map(\.text).joined(separator: "\n") ?? "")
                        .font(.title3.weight(.semibold))
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(24)
                        .textSelection(.enabled)
                }
            } else if missing {
                ContentUnavailableView("No lyrics", systemImage: "quote.bubble", description: Text("None were found for this song."))
            } else {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .overlay(alignment: .bottomTrailing) {
            if let source = lyrics?.source {
                Text(source == "lrclib" ? "Lyrics from LRCLIB" : "Lyrics from the file")
                    .font(.caption2).foregroundStyle(.white.opacity(0.5)).padding(10)
            }
        }
        .task(id: track.id) {
            guard loadedFor != track.id else { return }
            lyrics = nil
            missing = false
            do {
                lyrics = try await AppModel.shared.client.musicLyrics(trackId: track.id)
            } catch {
                if !error.isCancellation { missing = true }
            }
            loadedFor = track.id
        }
    }
}

// MARK: - Transcript

struct TranscriptPanel: View {
    let episode: PodcastEpisode
    var followsPlayer = true
    @State private var transcript: PodcastTranscript?
    @State private var missing = false
    @State private var query = ""
    @State private var following = true

    var body: some View {
        let player = AudioPlayer.shared
        let playingThis = player.current?.episode?.id == episode.id
        VStack(spacing: 0) {
            HStack {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                TextField("", text: $query, prompt: Text("Search transcript"))
                    .labelsHidden()
                    .textFieldStyle(.plain)
                    .autocorrectionDisabled()
                if !query.isEmpty {
                    Text("\(matches.count) found").font(.caption).foregroundStyle(.secondary)
                    Button { query = "" } label: { Image(systemName: "xmark.circle.fill") }.buttonStyle(.plain)
                }
            }
            .padding(10)
            .background(.white.opacity(0.1), in: RoundedRectangle(cornerRadius: 10))
            .padding([.horizontal, .top], 14)

            if let transcript {
                let segments = transcript.segments
                let currentIndex = playingThis ? segments.lastIndex { $0.start <= player.position + 0.2 } : nil
                ScrollViewReader { proxy in
                    ScrollView {
                        LazyVStack(alignment: .leading, spacing: 12) {
                            ForEach(Array(segments.enumerated()), id: \.offset) { offset, segment in
                                if query.isEmpty || matches.contains(offset) {
                                    segmentRow(segment, current: offset == currentIndex)
                                        .id(offset)
                                        .onTapGesture { play(from: segment.start) }
                                }
                            }
                        }
                        .padding(16)
                    }
                    .onChange(of: currentIndex) { _, index in
                        guard following, query.isEmpty, let index else { return }
                        withAnimation(.easeInOut(duration: 0.35)) { proxy.scrollTo(index, anchor: .center) }
                    }
                    .simultaneousGesture(DragGesture().onChanged { _ in following = false })
                    .overlay(alignment: .bottom) {
                        if !following, playingThis, query.isEmpty {
                            Button {
                                following = true
                                if let currentIndex { withAnimation { proxy.scrollTo(currentIndex, anchor: .center) } }
                            } label: {
                                Label("Follow Along", systemImage: "arrow.down.to.line")
                            }
                            .buttonStyle(.borderedProminent).foregroundStyle(Color.onSentinelGreen)
                            .padding()
                        }
                    }
                }
                if let source = transcript.source {
                    Text(source == "whisper" ? "Transcript made on your server" : "Transcript from the show")
                        .font(.caption2).foregroundStyle(.secondary).padding(.bottom, 6)
                }
            } else if missing {
                ContentUnavailableView("No transcript", systemImage: "text.quote",
                                       description: Text("This episode doesn't have one yet. Use Transcribe on the episode page to make one."))
            } else {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .task(id: episode.id) {
            transcript = nil
            missing = false
            do { transcript = try await AppModel.shared.client.episodeTranscript(id: episode.id) }
            catch { if !error.isCancellation { missing = true } }
        }
    }

    private var matches: Set<Int> {
        let q = query.trimmingCharacters(in: .whitespaces)
        guard !q.isEmpty, let segments = transcript?.segments else { return [] }
        return Set(segments.indices.filter { segments[$0].text.localizedCaseInsensitiveContains(q) })
    }

    private func segmentRow(_ segment: TranscriptSegment, current: Bool) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(spacing: 6) {
                Text(AudioFormat.duration(segment.start)).font(.caption2.monospacedDigit())
                if let speaker = segment.speaker { Text(speaker).font(.caption2.weight(.semibold)) }
            }
            .foregroundStyle(.secondary)
            Text(highlighted(segment.text))
                .font(current ? .body.weight(.semibold) : .body)
                .opacity(current || !query.isEmpty ? 1 : 0.7)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(8)
        .background(current ? RoundedRectangle(cornerRadius: 8).fill(.white.opacity(0.12)) : nil)
        .contentShape(Rectangle())
        .accessibilityAddTraits(.isButton)
        .accessibilityHint("Plays from here")
    }

    private func highlighted(_ text: String) -> AttributedString {
        var attributed = AttributedString(text)
        let q = query.trimmingCharacters(in: .whitespaces)
        guard !q.isEmpty else { return attributed }
        var searchRange = attributed.startIndex..<attributed.endIndex
        while let range = attributed[searchRange].range(of: q, options: .caseInsensitive) {
            attributed[range].backgroundColor = SentinelTheme.green.opacity(0.5)
            searchRange = range.upperBound..<attributed.endIndex
        }
        return attributed
    }

    private func play(from seconds: Double) {
        let player = AudioPlayer.shared
        if player.current?.episode?.id == episode.id {
            player.seek(to: seconds)
            if !player.isPlaying { player.resume() }
        } else {
            player.play(episode: episode, at: seconds)
        }
        following = true
    }
}

// MARK: - Chapters

private struct ChaptersPanel: View {
    var body: some View {
        let player = AudioPlayer.shared
        let current = player.currentChapter
        List {
            ForEach(player.chapters) { chapter in
                Button {
                    player.seek(to: chapter.startSeconds)
                } label: {
                    HStack(spacing: 12) {
                        if let image = chapter.imageUrl {
                            ArtworkImage(path: image, size: 40, corner: 5, placeholder: "list.bullet")
                        }
                        VStack(alignment: .leading, spacing: 2) {
                            Text(chapter.title).fontWeight(chapter == current ? .bold : .regular)
                            Text(AudioFormat.duration(chapter.startSeconds)).font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                        }
                        Spacer()
                        if chapter == current { NowPlayingBars(playing: player.isPlaying) }
                        if let link = chapter.url, let url = URL(string: link) {
                            Link(destination: url) { Image(systemName: "link") }.buttonStyle(.plain)
                        }
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .listRowBackground(Color.clear)
            }
        }
        .scrollContentBackground(.hidden)
        .listStyle(.plain)
    }
}

// MARK: - Queue

private struct QueuePanel: View {
    var body: some View {
        let player = AudioPlayer.shared
        List {
            if let current = player.current {
                Section {
                    queueRow(current, playing: true)
                } header: {
                    HStack {
                        Text("Now Playing")
                        Spacer()
                        if let name = player.context?.name { Text("From \(name)").textCase(nil) }
                    }
                }
            }
            Section {
                ForEach(player.upNext) { item in
                    queueRow(item, playing: false)
                        .contentShape(Rectangle())
                        .onTapGesture { player.jump(toUpNext: item) }
                }
                .onMove { player.moveUpNext(from: $0, to: $1) }
                .onDelete { player.removeUpNext(at: $0) }
            } header: {
                HStack {
                    Text("Up Next")
                    Spacer()
                    if !player.isEpisode {
                        Button { player.toggleShuffle() } label: {
                            Image(systemName: "shuffle").foregroundStyle(player.shuffle ? SentinelTheme.green : .secondary)
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel(player.shuffle ? "Shuffle on" : "Shuffle off")
                        Button { player.cycleRepeat() } label: {
                            Image(systemName: player.repeatMode.icon).foregroundStyle(player.repeatMode != .off ? SentinelTheme.green : .secondary)
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel("Repeat \(player.repeatMode.rawValue)")
                    }
                    if !player.upNext.isEmpty {
                        Button("Clear") { player.clearUpNext() }.buttonStyle(.plain).textCase(nil)
                    }
                }
            }
            if player.upNext.isEmpty {
                // A row, not a section footer: footers in a plain list draw an opaque band.
                Text("Nothing queued. Use Play Next or Play Later on any song or episode.")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
                    .listRowBackground(Color.clear)
                    .listRowSeparator(.hidden)
            }
        }
        .scrollContentBackground(.hidden)
        .listStyle(.plain)
    }

    private func queueRow(_ item: AudioItem, playing: Bool) -> some View {
        HStack(spacing: 12) {
            ArtworkImage(path: item.artPath, size: 40, corner: 5, placeholder: item.isEpisode ? "mic" : "music.note")
            VStack(alignment: .leading, spacing: 2) {
                Text(item.title).lineLimit(1).fontWeight(playing ? .semibold : .regular)
                Text(item.subtitle).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer()
            if playing { NowPlayingBars(playing: AudioPlayer.shared.isPlaying) }
            else { Text(AudioFormat.duration(item.duration)).font(.caption.monospacedDigit()).foregroundStyle(.secondary) }
        }
        .listRowBackground(Color.clear)
    }
}
