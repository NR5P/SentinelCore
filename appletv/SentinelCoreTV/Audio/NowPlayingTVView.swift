import SwiftUI
import UIKit

// The living-room Now Playing: the art blurred across the whole screen, the cover big on the left,
// lyrics / up next / chapters / transcript on the right, the transport along the bottom. Left
// alone while playing it drifts into an ambient mode (art and karaoke lyrics, slowly moving so
// nothing burns in) and keeps the screen saver away; any press brings the controls back.
struct NowPlayingTVView: View {
    enum Panel: String, CaseIterable, Identifiable {
        case lyrics, queue, chapters, transcript
        var id: Self { self }
        var title: String {
            switch self {
            case .lyrics: "Lyrics"
            case .queue: "Up Next"
            case .chapters: "Chapters"
            case .transcript: "Transcript"
            }
        }
        var icon: String {
            switch self {
            case .lyrics: "quote.bubble"
            case .queue: "list.bullet"
            case .chapters: "list.number"
            case .transcript: "text.quote"
            }
        }
    }

    enum Focus: Hashable { case play, scrubber, panel, wake }

    @EnvironmentObject private var audio: AudioCenter
    @ObservedObject private var player = AudioCenter.shared.player
    @Environment(\.dismiss) private var dismiss
    @State private var panel: Panel = .queue
    @State private var karaoke = false
    @State private var idle = false
    @State private var lastActivity = Date()
    @State private var showingSpeakers = false
    @State private var loved: [String: Bool] = [:]
    @State private var toast: String?
    @FocusState private var focus: Focus?

    private var entry: AudioEntry? { player.current }
    private var isEpisode: Bool { entry?.isEpisode == true }
    private var availablePanels: [Panel] {
        if isEpisode {
            return [.queue] + (player.chapters.isEmpty ? [] : [.chapters]) + ((player.transcript?.segments ?? []).isEmpty ? [] : [.transcript])
        }
        return (player.lyrics == nil ? [] : [.lyrics]) + [.queue]
    }

    var body: some View {
        ZStack {
            if let entry {
                if idle {
                    AmbientNowPlaying(entry: entry, focus: $focus) { wake() }
                } else if karaoke, let lyrics = player.lyrics {
                    VStack(spacing: 30) {
                        LyricsPanel(lyrics: lyrics, position: player.position, large: true) { player.seek(to: $0) }
                        transport
                    }
                    .padding(80)
                } else {
                    VStack(spacing: 34) {
                        HStack(alignment: .top, spacing: 70) {
                            artColumn(entry)
                            panelColumn
                        }
                        .frame(maxHeight: .infinity, alignment: .top)
                        transport
                    }
                    .padding(.horizontal, 90)
                    .padding(.vertical, 50)
                }
            } else {
                ContentUnavailableView("Nothing playing", systemImage: "music.note", description: Text("Pick something in Music or Podcasts."))
            }
            if let line = player.spokenLine {
                SpokenLineCard(text: line)
            }
            if let toast {
                Text(toast)
                    .font(.headline)
                    .padding(.horizontal, 30).padding(.vertical, 16)
                    .background(.regularMaterial, in: Capsule())
                    .frame(maxHeight: .infinity, alignment: .top)
                    .padding(.top, 40)
                    .transition(.opacity)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        // In the background, so the blown-up blurred art can't size the screen's layout.
        .background { NowPlayingBackdrop(path: entry?.artPath, title: entry?.title ?? "") }
        .environment(\.colorScheme, .dark)
        .onPlayPauseCommand { player.togglePlayPause(); poke() }
        .onChange(of: focus) { _, _ in poke() }
        .onChange(of: player.current?.uid) { _, _ in
            if !availablePanels.contains(panel) { panel = availablePanels.first ?? .queue }
        }
        .onChange(of: player.lyrics == nil) { _, missing in
            if !missing, !isEpisode, panel == .queue { panel = .lyrics }
        }
        .task {
            panel = availablePanels.contains(.lyrics) ? .lyrics : (availablePanels.first ?? .queue)
            focus = .play
            // Ambient mode after 40 s untouched while playing; no screen saver while this is up.
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(2))
                UIApplication.shared.isIdleTimerDisabled = player.isPlaying
                if !idle, player.isPlaying, Date().timeIntervalSince(lastActivity) > 40 {
                    withAnimation(.easeInOut(duration: 1.2)) { idle = true }
                    focus = .wake
                }
            }
        }
        .onDisappear { UIApplication.shared.isIdleTimerDisabled = false }
        .sheet(isPresented: $showingSpeakers) {
            NavigationStack { SpeakersTVView() }
                .environmentObject(audio)
        }
        #if DEBUG
        // `-debugNowPlayingPanel lyrics|queue|chapters|transcript`, `-debugKaraoke YES`, `-debugAmbient YES`.
        .task {
            let defaults = UserDefaults.standard
            try? await Task.sleep(for: .seconds(2))
            if let raw = defaults.string(forKey: "debugNowPlayingPanel"), let picked = Panel(rawValue: raw) { panel = picked }
            if defaults.bool(forKey: "debugKaraoke") { karaoke = true }
            if defaults.bool(forKey: "debugAmbient") { idle = true; focus = .wake }
        }
        #endif
    }

    private func poke() {
        lastActivity = Date()
    }

    private func wake() {
        lastActivity = Date()
        withAnimation(.easeInOut(duration: 0.5)) { idle = false }
        focus = .play
    }

    private func flash(_ text: String) {
        withAnimation { toast = text }
        Task {
            try? await Task.sleep(for: .seconds(2.5))
            withAnimation { toast = nil }
        }
    }

    // MARK: Left: the cover and the words under it

    private func artColumn(_ entry: AudioEntry) -> some View {
        VStack(alignment: .leading, spacing: 22) {
            AudioArt(path: entry.artPath, title: entry.title, size: 1000)
                .frame(width: 520, height: 520)
                .clipShape(RoundedRectangle(cornerRadius: 20))
                .shadow(color: .black.opacity(0.5), radius: 40, y: 20)
                .scaleEffect(player.isPlaying ? 1 : 0.92)
                .animation(.spring(duration: 0.6), value: player.isPlaying)
            VStack(alignment: .leading, spacing: 6) {
                if let context = player.context?.name, !context.isEmpty {
                    Text(context.uppercased()).font(.caption2.weight(.semibold)).foregroundStyle(.white.opacity(0.6)).lineLimit(1)
                }
                Text(entry.title).font(.title3.bold()).lineLimit(2).minimumScaleFactor(0.7)
                Text(entry.subtitle).font(.callout).foregroundStyle(.white.opacity(0.75)).lineLimit(1)
                if let chapter = player.currentChapter {
                    Label(chapter.title, systemImage: "bookmark").font(.caption).foregroundStyle(SentinelTheme.green).lineLimit(1)
                }
                if let error = player.errorMessage {
                    Text(error).font(.caption).foregroundStyle(SentinelTheme.red).lineLimit(2)
                }
            }
            .frame(width: 520, alignment: .leading)
            .foregroundStyle(.white)
        }
    }

    // MARK: Right: lyrics / queue / chapters / transcript

    private var panelColumn: some View {
        VStack(alignment: .leading, spacing: 20) {
            HStack(spacing: 20) {
                ForEach(availablePanels) { item in
                    Button {
                        panel = item
                    } label: {
                        Label(item.title, systemImage: item.icon)
                            .font(.caption)
                            .audioFocusLabel(panel == item ? SentinelTheme.green : .white.opacity(0.7))
                    }
                }
                if panel == .lyrics, player.lyrics?.isSynced == true {
                    Button {
                        karaoke = true
                    } label: {
                        Label("Karaoke", systemImage: "music.mic").font(.caption).audioFocusLabel(.white.opacity(0.7))
                    }
                }
                Spacer()
            }
            Group {
                switch panel {
                case .lyrics:
                    if let lyrics = player.lyrics {
                        LyricsPanel(lyrics: lyrics, position: player.position, large: false) { player.seek(to: $0) }
                    } else {
                        Text("No lyrics for this song.").foregroundStyle(.white.opacity(0.6))
                    }
                case .queue:
                    QueuePanel()
                case .chapters:
                    ChaptersPanel()
                case .transcript:
                    TranscriptPanel()
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            .focused($focus, equals: .panel)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
    }

    // MARK: Bottom: progress and buttons

    private var transport: some View {
        VStack(spacing: 22) {
            Scrubber(position: player.position, duration: player.duration, isEpisode: isEpisode) { player.seek(to: $0) }
            HStack(spacing: 34) {
                if !isEpisode {
                    roundButton(player.shuffle ? "shuffle.circle.fill" : "shuffle", label: "Shuffle", active: player.shuffle) {
                        player.setShuffle(!player.shuffle)
                    }
                }
                if isEpisode {
                    roundButton("gobackward.15", label: "Back 15 seconds") { player.skip(by: -15) }
                } else {
                    roundButton("backward.fill", label: "Previous") { player.previous() }
                }
                Button {
                    player.togglePlayPause()
                } label: {
                    Image(systemName: player.isBuffering ? "ellipsis" : (player.isPlaying ? "pause.fill" : "play.fill"))
                        .font(.system(size: 44))
                        .frame(width: 90, height: 70)
                        .audioFocusLabel(.white)
                }
                .focused($focus, equals: .play)
                .accessibilityLabel(player.isPlaying ? "Pause" : "Play")
                if isEpisode {
                    roundButton("goforward.30", label: "Forward 30 seconds") { player.skip(by: 30) }
                } else {
                    roundButton("forward.fill", label: "Next") { player.next() }
                }
                if !isEpisode {
                    roundButton(player.repeatMode.icon, label: player.repeatMode.label, active: player.repeatMode != .off) { player.cycleRepeat() }
                }
                Spacer().frame(width: 30)
                speedMenu
                sleepMenu
                if let track = entry?.track {
                    let isLoved = loved[track.id] ?? track.isLoved
                    roundButton(isLoved ? "heart.fill" : "heart", label: isLoved ? "Unlove" : "Love", active: isLoved) {
                        Task {
                            if let result = try? await audio.api?.love(kind: "tracks", id: track.id, loved: !isLoved) {
                                loved[track.id] = result
                                flash(result ? "Loved" : "Unloved")
                            }
                        }
                    }
                    Menu {
                        Button("Start Radio", systemImage: "dot.radiowaves.left.and.right") {
                            Task { try? await audio.startRadio(seedType: "track", seedId: track.id, name: track.title) }
                        }
                        if let albumId = track.albumId {
                            Button("Go to Album", systemImage: "square.stack") { AudioNavigator.shared.open(.album(albumId)) }
                        }
                        if let artistId = track.artistId {
                            Button("Go to Artist", systemImage: "music.mic") { AudioNavigator.shared.open(.artist(artistId)) }
                        }
                        Button("Stop and Clear Queue", systemImage: "stop.fill", role: .destructive) { player.stop(); dismiss() }
                    } label: {
                        Image(systemName: "ellipsis").font(.title3).frame(width: 56, height: 56).audioFocusLabel(.white)
                    }
                } else if let episode = entry?.episode {
                    roundButton("bookmark", label: "Bookmark") {
                        let at = player.position
                        Task {
                            if (try? await audio.api?.addBookmark(episodeId: episode.id, position: at)) != nil {
                                flash("Bookmarked at \(AudioFormat.clock(at))")
                            }
                        }
                    }
                    Menu {
                        if let podcastId = episode.podcastId {
                            Button("Go to Show", systemImage: "square.stack") { AudioNavigator.shared.open(.podcast(podcastId)) }
                        }
                        Button("Episode Details", systemImage: "info.circle") { AudioNavigator.shared.open(.episode(episode.id)) }
                        Button("Mark as Played", systemImage: "checkmark.circle") {
                            Task { try? await audio.api?.setPlayed(episodeId: episode.id, played: true) }
                            player.next()
                        }
                        Button("Stop and Clear Queue", systemImage: "stop.fill", role: .destructive) { player.stop(); dismiss() }
                    } label: {
                        Image(systemName: "ellipsis").font(.title3).frame(width: 56, height: 56).audioFocusLabel(.white)
                    }
                }
                roundButton("hifispeaker.2", label: "Play On…") { showingSpeakers = true }
            }
        }
    }

    private var speedMenu: some View {
        Menu {
            ForEach([0.5, 0.75, 1.0, 1.1, 1.2, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0], id: \.self) { value in
                Button {
                    player.setSpeed(value)
                } label: {
                    if abs(value - player.speed) < 0.01 { Label(AudioFormat.speed(value), systemImage: "checkmark") } else { Text(AudioFormat.speed(value)) }
                }
            }
        } label: {
            Text(AudioFormat.speed(player.speed))
                .font(.callout.monospacedDigit().weight(.semibold))
                .frame(minWidth: 70, minHeight: 56)
                .audioFocusLabel(abs(player.speed - 1) > 0.01 ? SentinelTheme.green : .white)
        }
        .accessibilityLabel("Playback speed")
    }

    private var sleepMenu: some View {
        Menu {
            Button("Off") { player.setSleepTimer(nil) }
            ForEach([5, 10, 15, 30, 45, 60, 90], id: \.self) { minutes in
                Button("\(minutes) minutes") { player.setSleepTimer(.at(Date().addingTimeInterval(Double(minutes) * 60))) }
            }
            Button(isEpisode ? "End of Episode" : "End of Song") { player.setSleepTimer(.endOfItem) }
        } label: {
            HStack(spacing: 8) {
                Image(systemName: player.sleepTimer == nil ? "moon.zzz" : "moon.zzz.fill")
                if let timer = player.sleepTimer {
                    switch timer {
                    case .at(let date): Text(timerInterval: Date()...max(Date(), date), countsDown: true).font(.caption.monospacedDigit())
                    case .endOfItem: Text("End").font(.caption)
                    }
                }
            }
            .font(.title3)
            .frame(minWidth: 56, minHeight: 56)
            .audioFocusLabel(player.sleepTimer == nil ? .white : SentinelTheme.green)
        }
        .accessibilityLabel("Sleep timer")
    }

    private func roundButton(_ icon: String, label: String, active: Bool = false, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: icon)
                .font(.title3)
                .frame(width: 56, height: 56)
                .audioFocusLabel(active ? SentinelTheme.green : .white)
        }
        .accessibilityLabel(label)
    }
}

// MARK: - Backdrop

struct NowPlayingBackdrop: View {
    let path: String?
    let title: String

    var body: some View {
        ZStack {
            Color.black
            AudioArt(path: path, title: "", size: 400)
                .blur(radius: 80)
                .saturation(1.4)
                .opacity(0.75)
                .scaleEffect(1.3)
            LinearGradient(colors: [.black.opacity(0.25), .black.opacity(0.75)], startPoint: .top, endPoint: .bottom)
        }
        .ignoresSafeArea()
        .animation(.easeInOut(duration: 1), value: path)
    }
}

// MARK: - Scrubber: focus it and press left/right to seek

private struct Scrubber: View {
    let position: Double
    let duration: Double
    let isEpisode: Bool
    let seek: (Double) -> Void
    @FocusState private var isFocused: Bool

    var body: some View {
        VStack(spacing: 8) {
            GeometryReader { geo in
                ZStack(alignment: .leading) {
                    Capsule().fill(.white.opacity(0.25))
                    Capsule().fill(isFocused ? SentinelTheme.green : .white)
                        .frame(width: duration > 0 ? max(8, geo.size.width * min(1, position / duration)) : 0)
                }
            }
            .frame(height: isFocused ? 14 : 8)
            HStack {
                Text(AudioFormat.clock(position))
                Spacer()
                if isFocused { Text("◀ ▶ to skip \(isEpisode ? "15/30" : "10") s").foregroundStyle(.white.opacity(0.6)) }
                Spacer()
                Text("-" + AudioFormat.clock(max(0, duration - position)))
            }
            .font(.caption.monospacedDigit())
            .foregroundStyle(.white.opacity(0.8))
        }
        .padding(.vertical, 6)
        .focusable()
        .focused($isFocused)
        .onMoveCommand { direction in
            switch direction {
            case .left: seek(position - (isEpisode ? 15 : 10))
            case .right: seek(position + (isEpisode ? 30 : 10))
            default: break
            }
        }
        .animation(.easeOut(duration: 0.2), value: isFocused)
        .accessibilityElement()
        .accessibilityLabel("Position")
        .accessibilityValue("\(AudioFormat.clock(position)) of \(AudioFormat.clock(duration))")
    }
}

// MARK: - Lyrics (synced lines highlight and scroll; pressing a line seeks)

struct LyricsPanel: View {
    let lyrics: Lyrics
    let position: Double
    let large: Bool
    let seek: (Double) -> Void

    private var lines: [LyricsLine] { lyrics.displayLines }
    private var currentIndex: Int? {
        guard lyrics.isSynced else { return nil }
        return lines.lastIndex { ($0.time ?? .infinity) <= position + 0.2 }
    }

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: large ? .center : .leading, spacing: large ? 28 : 16) {
                    ForEach(Array(lines.enumerated()), id: \.offset) { offset, line in
                        lineView(line, offset: offset).id(offset)
                    }
                }
                .padding(.vertical, large ? 300 : 40)
                .frame(maxWidth: .infinity, alignment: large ? .center : .leading)
            }
            .scrollClipDisabled(false)
            .onChange(of: currentIndex) { _, index in
                guard let index else { return }
                withAnimation(.easeInOut(duration: 0.5)) { proxy.scrollTo(index, anchor: .center) }
            }
        }
        .mask(LinearGradient(stops: [.init(color: .clear, location: 0), .init(color: .black, location: 0.12),
                                     .init(color: .black, location: 0.88), .init(color: .clear, location: 1)],
                             startPoint: .top, endPoint: .bottom))
    }

    @ViewBuilder
    private func lineView(_ line: LyricsLine, offset: Int) -> some View {
        let isCurrent = offset == currentIndex
        let isPast = currentIndex.map { offset < $0 } ?? false
        let text = Text(line.text.isEmpty ? "♪" : line.text)
            .font(large ? .system(size: isCurrent ? 64 : 46, weight: .heavy) : .system(size: isCurrent ? 40 : 32, weight: .bold))
            .multilineTextAlignment(large ? .center : .leading)
            .foregroundStyle(isCurrent ? Color.white : .white.opacity(isPast ? 0.35 : (lyrics.isSynced ? 0.5 : 0.85)))
            .scaleEffect(isCurrent && !large ? 1.02 : 1, anchor: .leading)
            .shadow(color: isCurrent ? SentinelTheme.green.opacity(0.6) : .clear, radius: 14)
            .animation(.easeOut(duration: 0.3), value: isCurrent)
        if let time = line.time, !large {
            Button { seek(time) } label: { text }
                .buttonStyle(LyricLineButtonStyle())
        } else {
            text
        }
    }
}

// A lyric line you can focus and press to seek, without a platter behind it.
private struct LyricLineButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        LyricLine(configuration: configuration)
    }

    private struct LyricLine: View {
        let configuration: ButtonStyle.Configuration
        @Environment(\.isFocused) private var isFocused

        var body: some View {
            configuration.label
                .padding(.horizontal, 16).padding(.vertical, 6)
                .background(isFocused ? Color.white.opacity(0.15) : .clear, in: RoundedRectangle(cornerRadius: 12))
                .scaleEffect(configuration.isPressed ? 0.97 : 1)
        }
    }
}

// MARK: - Queue

private struct QueuePanel: View {
    @ObservedObject private var player = AudioCenter.shared.player

    var body: some View {
        if player.upNext.isEmpty {
            Text(player.repeatMode == .all ? "Repeating the queue from the top." : "Nothing after this.")
                .foregroundStyle(.white.opacity(0.6))
        } else {
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 10) {
                    ForEach(Array(player.upNext.prefix(100))) { entry in
                        Button {
                            player.jump(to: entry)
                        } label: {
                            HStack(spacing: 18) {
                                AudioArt(path: entry.artPath, title: entry.title, size: 120)
                                    .frame(width: 64, height: 64)
                                    .clipShape(RoundedRectangle(cornerRadius: 6))
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(entry.title).lineLimit(1)
                                    Text(entry.artist).font(.caption).opacity(0.7).lineLimit(1)
                                }
                                Spacer()
                                Text(AudioFormat.clock(entry.knownDuration)).font(.caption.monospacedDigit()).opacity(0.7)
                            }
                            .audioFocusLabel(.white)
                        }
                        .buttonStyle(LyricLineButtonStyle())
                        .contextMenu {
                            Button("Play Now", systemImage: "play.fill") { player.jump(to: entry) }
                            Button("Remove from Queue", systemImage: "minus.circle", role: .destructive) { player.remove(entry) }
                        }
                    }
                }
                .padding(.vertical, 10)
            }
        }
    }
}

// MARK: - Chapters

private struct ChaptersPanel: View {
    @ObservedObject private var player = AudioCenter.shared.player

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 8) {
                    ForEach(player.chapters) { chapter in
                        let current = player.currentChapter == chapter
                        Button {
                            player.seek(to: chapter.startSeconds)
                        } label: {
                            HStack(spacing: 18) {
                                Image(systemName: current ? "speaker.wave.2.fill" : "circle.fill")
                                    .font(.caption2)
                                    .foregroundStyle(current ? SentinelTheme.green : .white.opacity(0.3))
                                Text(chapter.title).lineLimit(2).font(current ? .body.weight(.bold) : .body)
                                Spacer()
                                Text(AudioFormat.clock(chapter.startSeconds)).font(.caption.monospacedDigit()).opacity(0.7)
                            }
                            .foregroundStyle(.white)
                        }
                        .buttonStyle(LyricLineButtonStyle())
                        .id(chapter.id)
                    }
                }
            }
            .onChange(of: player.currentChapter?.id) { _, id in
                if let id { withAnimation { proxy.scrollTo(id, anchor: .center) } }
            }
        }
    }
}

// MARK: - Transcript (follows playback; press a line to seek; search inside it)

private struct TranscriptPanel: View {
    @ObservedObject private var player = AudioCenter.shared.player
    @State private var search = ""

    private var segments: [TranscriptSegment] { player.transcript?.segments ?? [] }
    private var shown: [TranscriptSegment] {
        let q = search.trimmingCharacters(in: .whitespaces)
        return q.isEmpty ? segments : segments.filter { $0.text.localizedCaseInsensitiveContains(q) }
    }
    private var currentId: Double? { segments.last { $0.start <= player.position + 0.2 }?.id }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            TextField("Search the transcript", text: $search)
                .frame(maxWidth: 600)
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 8) {
                        ForEach(shown) { segment in
                            let current = segment.id == currentId
                            Button {
                                player.seek(to: segment.start)
                            } label: {
                                VStack(alignment: .leading, spacing: 2) {
                                    if let speaker = segment.speaker {
                                        Text(speaker.uppercased()).font(.caption2.bold()).foregroundStyle(SentinelTheme.green)
                                    }
                                    Text(segment.text)
                                        .font(.system(size: current ? 32 : 28, weight: current ? .bold : .regular))
                                        .foregroundStyle(.white.opacity(current ? 1 : 0.55))
                                }
                            }
                            .buttonStyle(LyricLineButtonStyle())
                            .id(segment.id)
                        }
                    }
                }
                .onChange(of: currentId) { _, id in
                    guard search.isEmpty, let id else { return }
                    withAnimation(.easeInOut(duration: 0.5)) { proxy.scrollTo(id, anchor: .center) }
                }
            }
        }
    }
}

// MARK: - Ambient mode

private struct AmbientNowPlaying: View {
    let entry: AudioEntry
    var focus: FocusState<NowPlayingTVView.Focus?>.Binding
    let wake: () -> Void
    @ObservedObject private var player = AudioCenter.shared.player

    var body: some View {
        ZStack {
            // One big invisible button: any press, or a swipe on the remote, wakes the controls.
            Button(action: wake) { Color.clear.frame(maxWidth: .infinity, maxHeight: .infinity) }
                .buttonStyle(AmbientWakeStyle())
                .focused(focus, equals: .wake)
                .onMoveCommand { _ in wake() }
            if let lyrics = player.lyrics, lyrics.isSynced {
                HStack(spacing: 80) {
                    AudioArt(path: entry.artPath, title: entry.title, size: 800)
                        .frame(width: 420, height: 420)
                        .clipShape(RoundedRectangle(cornerRadius: 18))
                        .shadow(radius: 30)
                    LyricsPanel(lyrics: lyrics, position: player.position, large: true) { _ in }
                }
                .padding(100)
                .allowsHitTesting(false)
            } else {
                // Drifts slowly around the screen so nothing stays lit in one place. Driven by a
                // clock rather than a repeating animation: a forever-animation also caught the
                // artwork's placeholder-to-image swap and left a ghost of the placeholder behind.
                TimelineView(.animation(minimumInterval: 1.0 / 15)) { timeline in
                    let t = timeline.date.timeIntervalSinceReferenceDate
                    ambientCard
                        .offset(x: 260 * sin(t / 17), y: 90 * cos(t / 23))
                }
                .allowsHitTesting(false)
            }
            VStack {
                Spacer()
                HStack {
                    Text(Date(), style: .time).font(.title3.monospacedDigit())
                    Spacer()
                    if player.duration > 0 {
                        Text("\(AudioFormat.clock(player.position)) / \(AudioFormat.clock(player.duration))").font(.caption.monospacedDigit())
                    }
                }
                .foregroundStyle(.white.opacity(0.5))
                .padding(60)
            }
            .allowsHitTesting(false)
        }
    }

    private var ambientCard: some View {
        VStack(spacing: 30) {
            AudioArt(path: entry.artPath, title: entry.title, size: 1000)
                .frame(width: 560, height: 560)
                .clipShape(RoundedRectangle(cornerRadius: 22))
                .shadow(color: .black.opacity(0.6), radius: 50, y: 24)
            VStack(spacing: 8) {
                Text(entry.title).font(.title2.bold())
                Text(entry.subtitle).font(.callout).opacity(0.7)
                if let chapter = player.currentChapter { Text(chapter.title).font(.caption).foregroundStyle(SentinelTheme.green) }
            }
            .foregroundStyle(.white)
            .multilineTextAlignment(.center)
        }
    }
}

private struct AmbientWakeStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View { configuration.label }
}

// The same card over the rest of the app (RootView), for an announcement that arrives while Now
// Playing isn't open - "someone is at the front door" said with nothing on screen to show it.
struct SpokenLineOverlay: View {
    @ObservedObject private var player = AudioCenter.shared.player
    let hidden: Bool

    var body: some View {
        ZStack {
            if !hidden, let line = player.spokenLine {
                SpokenLineCard(text: line)
            }
        }
        .animation(.easeInOut(duration: 0.3), value: player.spokenLine)
        .allowsHitTesting(false)
    }
}

// An announcement or the Morning Briefing being read out.
private struct SpokenLineCard: View {
    let text: String

    var body: some View {
        VStack(spacing: 20) {
            Image(systemName: "waveform").font(.system(size: 54)).foregroundStyle(SentinelTheme.green)
                .symbolEffect(.variableColor.iterative)
            Text(text).font(.title2.weight(.semibold)).multilineTextAlignment(.center).foregroundStyle(.white)
        }
        .padding(50)
        .frame(maxWidth: 1200)
        .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 30))
        .transition(.scale.combined(with: .opacity))
    }
}
