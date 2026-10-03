import AVFoundation
import Combine
import MediaPlayer
import UIKit

// One thing in the play queue: a music track or a podcast episode.
struct AudioEntry: Identifiable, Hashable {
    let uid = UUID()
    var track: Track?
    var episode: Episode?

    var id: UUID { uid }
    var isEpisode: Bool { episode != nil }
    var itemId: String { track?.id ?? episode?.id ?? "" }
    var kind: String { isEpisode ? "podcast" : "music" }
    var ref: QueueRef { QueueRef(type: isEpisode ? "episode" : "track", id: itemId) }
    var title: String { track?.title ?? episode?.title ?? "" }
    var subtitle: String {
        if let track { return [track.artistLine, track.album].compactMap { $0 }.joined(separator: " — ") }
        return episode?.podcastTitle ?? ""
    }
    var artist: String { track?.artistLine ?? episode?.podcastTitle ?? "" }
    var artPath: String? { track?.artUrl ?? track.map { "/api/music/tracks/\($0.id)/art" } ?? episode?.artUrl }
    var knownDuration: Double { track?.durationSeconds ?? episode?.durationSeconds ?? 0 }

    init(track: Track) { self.track = track }
    init(episode: Episode) { self.episode = episode }
}

enum RepeatMode: String, CaseIterable {
    case off, all, one
    var icon: String { self == .one ? "repeat.1" : "repeat" }
    var label: String {
        switch self {
        case .off: "Repeat Off"
        case .all: "Repeat All"
        case .one: "Repeat One"
        }
    }
}

enum SleepTimer: Equatable {
    case at(Date)
    case endOfItem
}

// The one player behind both tabs (core/docs/MUSIC_AND_PODCASTS.md §11). An AVQueuePlayer holding
// the current item and the next one, so a track change is gapless and the next file is already
// buffering. Owns the queue, shuffle/repeat, speed, Sound Check, the sleep timer, the lock-screen /
// Control Center / Siri Remote controls, and reports `played`, `progress` and `now-playing`.
@MainActor
final class AudioPlayer: ObservableObject {
    @Published private(set) var queue: [AudioEntry] = []
    @Published private(set) var index = 0
    @Published private(set) var isPlaying = false
    @Published private(set) var isBuffering = false
    @Published private(set) var position: Double = 0
    @Published private(set) var duration: Double = 0
    @Published private(set) var shuffle = false
    @Published private(set) var repeatMode: RepeatMode = .off
    // Repeat is a music setting: a podcast episode never loops (repeat-one would replay it
    // forever, repeat-all would restart a finished show), whatever was last chosen in Music.
    private var loopMode: RepeatMode { current?.episode != nil ? .off : repeatMode }
    @Published private(set) var speed: Double = 1
    @Published private(set) var context: PlayContext?
    @Published private(set) var sleepTimer: SleepTimer?
    @Published private(set) var volume: Double = AudioPrefs.volume
    @Published private(set) var ducked = false
    @Published var errorMessage: String?
    // Extras for whatever is playing now.
    @Published private(set) var lyrics: Lyrics?
    @Published private(set) var chapters: [Chapter] = []
    @Published private(set) var transcript: Transcript?
    // A line the system voice is reading out (an announcement, the Morning Briefing).
    @Published private(set) var spokenLine: String?
    @Published private(set) var showSettings: [String: PodcastMySettings] = [:]

    var current: AudioEntry? { queue.indices.contains(index) ? queue[index] : nil }
    var hasQueue: Bool { !queue.isEmpty }
    var upNext: ArraySlice<AudioEntry> { queue.indices.contains(index + 1) ? queue[(index + 1)...] : [] }
    var currentChapter: Chapter? { chapters.last { $0.startSeconds <= position + 0.25 } }

    // Called whenever anything a speaker reports has changed (see AudioCenter).
    var onStateChange: (() -> Void)?

    private weak var center: AudioCenter?
    private var api: AudioAPI? { center?.api }
    private let player = AVQueuePlayer()
    private let voice = SpeechVoice()
    private var itemEntries: [ObjectIdentifier: UUID] = [:]
    private var originalOrder: [AudioEntry] = []
    private var timeObserver: Any?
    private var currentItemObservation: NSKeyValueObservation?
    private var timeControlObservation: NSKeyValueObservation?
    private var statusObservation: NSKeyValueObservation?
    private var endObserver: NSObjectProtocol?
    private var pendingSeek: Double?
    private var autoplayPending = false
    private var extrasTask: Task<Void, Never>?
    private var fadeTask: Task<Void, Never>?
    private var duckTask: Task<Void, Never>?
    private var radioTask: Task<Void, Never>?
    // Reporting bookkeeping for the entry on screen.
    private var listened: Double = 0
    private var lastTick: Double?
    private var lastProgressReport = Date.distantPast
    private var lastNowPlayingReport = Date.distantPast
    private var reportedStart = false
    private var fadeFactor: Double = 1
    private var artworkFor: UUID?
    private var speedFromShow = false
    // The entry whose played/progress report has gone, so an auto-advance never reports twice.
    private var finishedUid: UUID?

    init() {
        player.actionAtItemEnd = .advance
        player.automaticallyWaitsToMinimizeStalling = true
        timeObserver = player.addPeriodicTimeObserver(forInterval: CMTime(seconds: 0.5, preferredTimescale: 600), queue: .main) { @Sendable [weak self] time in
            MainActor.assumeIsolated { self?.tick(time.seconds) }
        }
        // KVO, never publisher(for:).values - that drops changes (see the Movies player).
        currentItemObservation = player.observe(\.currentItem, options: [.new]) { @Sendable [weak self] player, _ in
            let item = player.currentItem
            Task { @MainActor [weak self] in self?.currentItemChanged(item) }
        }
        timeControlObservation = player.observe(\.timeControlStatus, options: [.initial, .new]) { @Sendable [weak self] player, _ in
            let status = player.timeControlStatus
            Task { @MainActor [weak self] in self?.timeControlChanged(status) }
        }
        endObserver = NotificationCenter.default.addObserver(
            forName: AVPlayerItem.didPlayToEndTimeNotification, object: nil, queue: .main
        ) { [weak self] note in
            let item = note.object as? AVPlayerItem
            MainActor.assumeIsolated { self?.itemDidEnd(item) }
        }
        configureRemoteCommands()
    }

    func attach(_ center: AudioCenter) {
        self.center = center
    }

    // MARK: - Starting things

    func play(tracks: [Track], startAt: Int = 0, shuffle: Bool = false, context: PlayContext?) {
        load(tracks.map(AudioEntry.init(track:)), startAt: startAt, position: 0, shuffle: shuffle, context: context, autoplay: true)
    }

    func play(episodes: [Episode], startAt: Int = 0, position: Double? = nil, context: PlayContext?) {
        let entries = episodes.map(AudioEntry.init(episode:))
        let start = entries.indices.contains(startAt) ? startAt : 0
        let resume = position ?? episodes[safe: start]?.resumePosition ?? 0
        load(entries, startAt: start, position: resume, shuffle: false, context: context, autoplay: true)
    }

    // A whole queue at a spot (a speaker `play`, "Continue from…").
    func load(_ entries: [AudioEntry], startAt: Int, position: Double, shuffle: Bool, context: PlayContext?,
              autoplay: Bool, repeatMode: RepeatMode? = nil, speed: Double? = nil) {
        guard !entries.isEmpty else { return }
        finishCurrent(completed: false)
        voice.stop()
        spokenLine = nil
        radioTask?.cancel()
        let start = entries.indices.contains(startAt) ? startAt : 0
        originalOrder = entries
        var ordered = entries
        var startIndex = start
        if shuffle {
            let first = ordered.remove(at: start)
            ordered.shuffle()
            ordered.insert(first, at: 0)
            startIndex = 0
        }
        self.shuffle = shuffle
        if let repeatMode { self.repeatMode = repeatMode }
        self.context = context
        queue = ordered
        index = startIndex
        if let speed { self.speed = speed }
        errorMessage = nil
        configureSession()
        startCurrent(at: position, autoplay: autoplay)
    }

    func addNext(_ entries: [AudioEntry]) {
        guard !queue.isEmpty else {
            load(entries, startAt: 0, position: 0, shuffle: false, context: PlayContext(type: "queue", id: nil, name: "Queue"), autoplay: true)
            return
        }
        queue.insert(contentsOf: entries, at: index + 1)
        originalOrder.append(contentsOf: entries)
        refreshPreload()
    }

    func addLast(_ entries: [AudioEntry]) {
        guard !queue.isEmpty else { addNext(entries); return }
        queue.append(contentsOf: entries)
        originalOrder.append(contentsOf: entries)
        refreshPreload()
    }

    func remove(_ entry: AudioEntry) {
        guard let at = queue.firstIndex(where: { $0.uid == entry.uid }), at != index else { return }
        queue.remove(at: at)
        originalOrder.removeAll { $0.uid == entry.uid }
        if at < index { index -= 1 }
        refreshPreload()
    }

    func jump(to entry: AudioEntry) {
        guard let at = queue.firstIndex(where: { $0.uid == entry.uid }) else { return }
        finishCurrent(completed: false)
        index = at
        startCurrent(at: 0, autoplay: true)
    }

    // MARK: - Transport

    func togglePlayPause() { isPlaying ? pause() : resume() }

    func resume() {
        guard current != nil else { return }
        if player.currentItem == nil {
            startCurrent(at: position >= duration - 1 ? 0 : position, autoplay: true)
            return
        }
        configureSession()
        player.defaultRate = Float(speed)
        player.playImmediately(atRate: Float(speed))
    }

    func pause() {
        player.pause()
        reportProgress(force: true)
        reportNowPlaying(force: true)
    }

    func stop() {
        finishCurrent(completed: false)
        player.pause()
        player.removeAllItems()
        itemEntries = [:]
        queue = []
        originalOrder = []
        index = 0
        position = 0
        duration = 0
        context = nil
        lyrics = nil
        chapters = []
        transcript = nil
        sleepTimer = nil
        fadeFactor = 1
        applyVolume()
        MPNowPlayingInfoCenter.default().nowPlayingInfo = nil
        MPNowPlayingInfoCenter.default().playbackState = .stopped
        onStateChange?()
    }

    func next() {
        guard !queue.isEmpty else { return }
        finishCurrent(completed: false)
        if index + 1 < queue.count {
            index += 1
        } else if loopMode != .off {
            index = 0
        } else {
            player.pause()
            seek(to: duration)
            return
        }
        startCurrent(at: 0, autoplay: true)
    }

    // Back to the start of this one, or to the one before when it has only just begun.
    func previous() {
        guard current != nil else { return }
        if position > 5 || index == 0 {
            seek(to: 0)
            return
        }
        finishCurrent(completed: false)
        index -= 1
        startCurrent(at: 0, autoplay: true)
    }

    func seek(to seconds: Double) {
        let target = max(0, duration > 0 ? min(seconds, duration - 0.5) : seconds)
        position = target
        lastTick = target
        player.seek(to: CMTime(seconds: target, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero) { @Sendable [weak self] _ in
            Task { @MainActor in
                self?.updateNowPlayingInfo()
                self?.reportNowPlaying(force: true)
                self?.onStateChange?()
            }
        }
    }

    func skip(by seconds: Double) { seek(to: position + seconds) }

    func setShuffle(_ on: Bool) {
        guard on != shuffle, let current else { shuffle = on; return }
        shuffle = on
        if on {
            var rest = queue.filter { $0.uid != current.uid }
            rest.shuffle()
            queue = [current] + rest
            index = 0
        } else {
            queue = originalOrder
            index = queue.firstIndex { $0.uid == current.uid } ?? 0
        }
        refreshPreload()
        applyVolume()
        reportNowPlaying(force: true)
    }

    func cycleRepeat() {
        let all = RepeatMode.allCases
        setRepeat(all[(all.firstIndex(of: repeatMode)! + 1) % all.count])
    }

    func setRepeat(_ mode: RepeatMode) {
        repeatMode = mode
        refreshPreload()
        reportNowPlaying(force: true)
    }

    func setSpeed(_ value: Double) {
        speed = min(3, max(0.5, value))
        player.defaultRate = Float(speed)
        if isPlaying { player.rate = Float(speed) }
        if let episode = current?.episode, let podcastId = episode.podcastId {
            Task { await rememberShowSpeed(podcastId: podcastId) }
        }
        reportNowPlaying(force: true)
    }

    func setVolume(_ value: Double, ramp: Double = 0) {
        let target = min(1, max(0, value))
        fadeTask?.cancel()
        AudioPrefs.volume = target
        guard ramp > 0 else {
            volume = target
            applyVolume()
            onStateChange?()
            return
        }
        volume = 0
        applyVolume()
        fadeTask = Task { [weak self] in
            let steps = max(1, Int(ramp * 4))
            for step in 1...steps {
                try? await Task.sleep(for: .milliseconds(250))
                guard let self, !Task.isCancelled else { return }
                self.volume = target * Double(step) / Double(steps)
                self.applyVolume()
            }
            self?.onStateChange?()
        }
    }

    func duck(_ on: Bool, autoRelease: Bool = true) {
        duckTask?.cancel()
        ducked = on
        applyVolume()
        if on, autoRelease {
            duckTask = Task { [weak self] in
                try? await Task.sleep(for: .seconds(30))
                guard !Task.isCancelled else { return }
                self?.duck(false)
            }
        }
    }

    // Duck, read it out, unduck. Speaks even when nothing is playing (at `volume` when given).
    // Returns only once the voice has finished, so the speaker's next command waits for it (§8).
    func announce(_ text: String, volume: Double? = nil) async {
        duck(true, autoRelease: false)
        spokenLine = text
        configureSession()
        await voice.speak(text, volume: isPlaying ? nil : volume)
        spokenLine = nil
        duck(false)
    }

    func setSleepTimer(_ timer: SleepTimer?) {
        sleepTimer = timer
        refreshPreload()
        if timer == nil, fadeFactor < 1 {
            fadeFactor = 1
            applyVolume()
        }
    }

    // MARK: - Morning Briefing (§9): speak each line, then play the episodes.

    func playBriefing(_ briefing: Briefing) async {
        finishCurrent(completed: false)
        player.pause()
        configureSession()
        let lines = [briefing.greeting].compactMap { $0 } + (briefing.speech ?? [])
        for line in lines {
            guard !Task.isCancelled else { break }
            spokenLine = line
            await voice.speak(line)
        }
        spokenLine = nil
        let episodes = briefing.episodes ?? []
        guard !episodes.isEmpty, !Task.isCancelled else { return }
        play(episodes: episodes, context: PlayContext(type: "queue", id: "briefing", name: "Morning Briefing"))
    }

    // MARK: - Items

    private func startCurrent(at start: Double, autoplay: Bool) {
        guard let entry = current else { return }
        player.removeAllItems()
        itemEntries = [:]
        finishedUid = nil
        listened = 0
        lastTick = nil
        reportedStart = false
        fadeFactor = 1
        lyrics = nil
        chapters = []
        transcript = nil
        duration = entry.knownDuration
        var begin = start
        if let episode = entry.episode, begin < 1 {
            let intro = Double(showSettings[episode.podcastId ?? ""]?.skipIntroSeconds ?? 0)
            if intro > 0 { begin = intro }
        }
        position = begin
        pendingSeek = begin > 0.5 ? begin : nil
        autoplayPending = autoplay
        guard let item = makeItem(entry) else {
            errorMessage = "This can't be played - the server address isn't set."
            return
        }
        player.insert(item, after: nil)
        observeStatus(of: item)
        refreshPreload()
        applySpeedForCurrent()
        applyVolume()
        loadExtras(for: entry)
        updateNowPlayingInfo()
        reportNowPlaying(force: true)
        onStateChange?()
        extendRadioIfNeeded()
    }

    private func makeItem(_ entry: AudioEntry) -> AVPlayerItem? {
        guard let api else { return nil }
        let url: URL?
        if let track = entry.track { url = api.streamURL(track) } else if let episode = entry.episode { url = api.audioURL(episode) } else { url = nil }
        guard let url else { return nil }
        var options: [String: Any] = [:]
        if let cookies = HTTPCookieStorage.shared.cookies(for: url), !cookies.isEmpty {
            options[AVURLAssetHTTPCookiesKey] = cookies
        }
        let item = AVPlayerItem(asset: AVURLAsset(url: url, options: options))
        item.audioTimePitchAlgorithm = .timeDomain
        item.preferredForwardBufferDuration = entry.isEpisode ? 60 : 20
        itemEntries[ObjectIdentifier(item)] = entry.uid
        return item
    }

    // Keeps exactly one item queued after the current one: the next entry (or the first, on
    // repeat all), nothing on repeat one - that one restarts the current item at its end.
    private func refreshPreload() {
        guard let currentItem = player.currentItem else { return }
        for item in player.items() where item !== currentItem {
            player.remove(item)
            itemEntries.removeValue(forKey: ObjectIdentifier(item))
        }
        guard loopMode != .one, sleepTimer != .endOfItem else { return }
        let nextIndex: Int? = index + 1 < queue.count ? index + 1 : (loopMode == .all && !queue.isEmpty ? 0 : nil)
        guard let nextIndex, let item = makeItem(queue[nextIndex]) else { return }
        if player.canInsert(item, after: currentItem) { player.insert(item, after: currentItem) }
    }

    private func observeStatus(of item: AVPlayerItem) {
        statusObservation = item.observe(\.status, options: [.initial, .new]) { @Sendable [weak self] item, _ in
            let status = item.status
            let message = item.error?.localizedDescription
            Task { @MainActor [weak self] in self?.statusChanged(item, status: status, error: message) }
        }
    }

    private func statusChanged(_ item: AVPlayerItem, status: AVPlayerItem.Status, error: String?) {
        guard player.currentItem === item else { return }
        switch status {
        case .readyToPlay:
            let seconds = item.duration.seconds
            if seconds.isFinite, seconds > 0 { duration = seconds }
            if let target = pendingSeek {
                pendingSeek = nil
                player.seek(to: CMTime(seconds: target, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
            }
            if autoplayPending {
                autoplayPending = false
                player.defaultRate = Float(speed)
                player.playImmediately(atRate: Float(speed))
            }
            updateNowPlayingInfo()
        case .failed:
            errorMessage = error ?? "This couldn't be played."
            autoplayPending = false
        default:
            break
        }
    }

    // AVQueuePlayer moved on by itself: the preloaded next item is now playing.
    private func currentItemChanged(_ item: AVPlayerItem?) {
        guard let item, let uid = itemEntries[ObjectIdentifier(item)],
              let at = queue.firstIndex(where: { $0.uid == uid }), at != index else { return }
        // Moving on by itself means the last one played to its end (a no-op if itemDidEnd ran first).
        finishCurrent(completed: true)
        finishedUid = nil
        index = at
        listened = 0
        lastTick = nil
        reportedStart = false
        lyrics = nil
        chapters = []
        transcript = nil
        duration = queue[at].knownDuration
        position = 0
        observeStatus(of: item)
        let seconds = item.duration.seconds
        if seconds.isFinite, seconds > 0 { duration = seconds }
        applySpeedForCurrent()
        applyVolume()
        refreshPreload()
        loadExtras(for: queue[at])
        updateNowPlayingInfo()
        reportNowPlaying(force: true)
        onStateChange?()
        extendRadioIfNeeded()
    }

    private func itemDidEnd(_ item: AVPlayerItem?) {
        guard let item, let uid = itemEntries[ObjectIdentifier(item)], current?.uid == uid else { return }
        finishCurrent(completed: true)
        if sleepTimer == .endOfItem {
            sleepTimer = nil
            player.pause()
            if index + 1 < queue.count {
                index += 1
                startCurrent(at: 0, autoplay: false)
            }
            return
        }
        if loopMode == .one {
            startCurrent(at: 0, autoplay: true)
        } else if index + 1 >= queue.count, loopMode == .off {
            // The end of the queue: stay on the last item, stopped. Pause the player too: with
            // its last item gone AVQueuePlayer keeps rate 1 and sits in "waiting to play", which
            // read back as playing - a spinning play button and a speaker reporting "playing"
            // forever. resume() restarts the item when there's none.
            player.pause()
            isPlaying = false
            isBuffering = false
            position = duration
            updateNowPlayingInfo()
            reportNowPlaying(force: true)
            onStateChange?()
        } else if player.items().count <= 1 {
            // Nothing was preloaded (the queue changed at the last moment) - start the next one.
            index = index + 1 < queue.count ? index + 1 : 0
            startCurrent(at: 0, autoplay: true)
        }
        // Otherwise AVQueuePlayer has already moved on; currentItemChanged picks it up.
    }

    private func timeControlChanged(_ status: AVPlayer.TimeControlStatus) {
        let playing = status != .paused
        isBuffering = status == .waitingToPlayAtSpecifiedRate
        guard playing != isPlaying else { return }
        isPlaying = playing
        updateNowPlayingInfo()
        if !playing { reportProgress(force: true) }
        reportNowPlaying(force: true)
        onStateChange?()
    }

    // MARK: - The half-second tick

    private func tick(_ seconds: Double) {
        guard seconds.isFinite, current != nil, player.currentItem != nil, pendingSeek == nil else { return }
        if isPlaying, let lastTick {
            let delta = seconds - lastTick
            if delta > 0, delta < 3 { listened += delta / max(speed, 0.5) }
        }
        lastTick = seconds
        position = seconds
        if isPlaying, !reportedStart, seconds > 1 {
            reportedStart = true
            if let track = current?.track, let api {
                Task { try? await api.nowPlayingTrack(track.id) }
            }
        }
        // Skip the outro: treat the show's last N seconds as the end.
        if let episode = current?.episode, duration > 0,
           let outro = showSettings[episode.podcastId ?? ""]?.skipOutroSeconds, outro > 0,
           seconds >= duration - Double(outro), seconds > 10 {
            finishCurrent(completed: true)
            if index + 1 < queue.count {
                index += 1
                startCurrent(at: 0, autoplay: sleepTimer != .endOfItem)
            } else {
                player.pause()
            }
            if sleepTimer == .endOfItem { sleepTimer = nil; player.pause() }
            return
        }
        sleepTick()
        if isPlaying {
            if Date().timeIntervalSince(lastProgressReport) >= 15 { reportProgress() }
            if Date().timeIntervalSince(lastNowPlayingReport) >= 30 {
                reportNowPlaying()
                onStateChange?()
            }
        }
    }

    // A 10-second fade before the timer runs out, then pause.
    private func sleepTick() {
        guard let sleepTimer else { return }
        let remaining: Double
        switch sleepTimer {
        case .at(let date): remaining = date.timeIntervalSinceNow
        case .endOfItem: remaining = duration > 0 ? duration - position : .infinity
        }
        if remaining <= 0, case .at = sleepTimer {
            self.sleepTimer = nil
            player.pause()
            fadeFactor = 1
            applyVolume()
            return
        }
        let newFade = remaining < 10 ? max(0, remaining / 10) : 1
        if abs(newFade - fadeFactor) > 0.01 {
            fadeFactor = newFade
            applyVolume()
        }
    }

    // MARK: - Volume, Sound Check, speed

    // Sound Check: volume = 10^(gain/20), clamped to 1.0; album gain when an album plays in order.
    var replayGainFactor: Double {
        guard AudioPrefs.soundCheck, let gain = current?.track?.replayGain else { return 1 }
        let albumInOrder = context?.type == "album" && !shuffle
        let db = albumInOrder ? (gain.albumGain ?? gain.trackGain) : (gain.trackGain ?? gain.albumGain)
        guard let db else { return 1 }
        return min(1, pow(10, db / 20))
    }

    func applyVolume() {
        player.volume = Float(volume * replayGainFactor * (ducked ? 0.2 : 1) * fadeFactor)
    }

    // Podcasts play at the show's remembered speed (or the app default); music at 1x unless
    // someone changed it while music was playing.
    private func applySpeedForCurrent() {
        if let episode = current?.episode {
            speed = showSettings[episode.podcastId ?? ""]?.playbackSpeed ?? AudioPrefs.podcastSpeed
            speedFromShow = true
        } else if speedFromShow {
            speed = 1
            speedFromShow = false
        }
        player.defaultRate = Float(speed)
        if isPlaying { player.rate = Float(speed) }
    }

    private func rememberShowSpeed(podcastId: String) async {
        guard let api else { return }
        var settings = showSettings[podcastId] ?? PodcastMySettings()
        settings.playbackSpeed = speed
        showSettings[podcastId] = settings
        _ = try? await api.saveMySettings(podcastId: podcastId, settings)
    }

    func noteShowSettings(_ podcast: Podcast) {
        if let mine = podcast.mySettings, !podcast.id.isEmpty { showSettings[podcast.id] = mine }
    }

    // MARK: - Extras: lyrics, chapters, transcript, the show's settings

    private func loadExtras(for entry: AudioEntry) {
        extrasTask?.cancel()
        guard let api else { return }
        let uid = entry.uid
        extrasTask = Task { [weak self] in
            if let track = entry.track {
                let lyrics = try? await api.lyrics(trackId: track.id)
                guard let self, !Task.isCancelled, self.current?.uid == uid else { return }
                self.lyrics = lyrics
            } else if let episode = entry.episode {
                if let podcastId = episode.podcastId, self?.showSettings[podcastId] == nil,
                   let podcast = try? await api.podcast(id: podcastId) {
                    self?.noteShowSettings(podcast)
                    if self?.current?.uid == uid { self?.applySpeedForCurrent() }
                }
                async let chapters = episode.hasChapters == false ? [] : ((try? await api.chapters(episodeId: episode.id)) ?? [])
                async let transcript = episode.hasTranscript == true ? (try? await api.transcript(episodeId: episode.id)) : nil
                let (c, t) = await (chapters, transcript)
                guard let self, !Task.isCancelled, self.current?.uid == uid else { return }
                self.chapters = c.sorted { $0.startSeconds < $1.startSeconds }
                self.transcript = t
            }
        }
    }

    // An endless radio: a few tracks from the end, ask for more like it.
    private func extendRadioIfNeeded() {
        guard context?.type == "radio", let seed = context?.id, queue.count - index <= 3, radioTask == nil, let api else { return }
        let seedType = seed.split(separator: ":").first.map(String.init) ?? "track"
        let seedId = seed.split(separator: ":").dropFirst().joined(separator: ":")
        let exclude = queue.suffix(100).map(\.itemId)
        radioTask = Task { [weak self] in
            defer { self?.radioTask = nil }
            guard let more = try? await api.radio(seedType: seedType, seedId: seedId.isEmpty ? seed : seedId, excludeIds: exclude),
                  let tracks = more.tracks, !tracks.isEmpty, !Task.isCancelled else { return }
            self?.addLast(tracks.map(AudioEntry.init(track:)))
        }
    }

    // MARK: - Reporting (§4 played, §6 progress, §7 now-playing)

    // The entry on screen is being left: say how it went.
    private func finishCurrent(completed: Bool) {
        guard let entry = current, let api, finishedUid != entry.uid else { return }
        finishedUid = entry.uid
        let deviceName = AudioPrefs.speakerName
        if let track = entry.track {
            let seconds = listened
            listened = 0
            guard seconds >= 1 else { return }
            let body = PlayedBody(secondsListened: seconds.rounded(), completed: completed,
                                  skipped: !completed, playedAt: AudioDate.iso(Date()), deviceName: deviceName)
            Task { try? await api.reportPlayed(trackId: track.id, body) }
        } else if let episode = entry.episode, !episode.isPreview {
            let end = duration > 0 ? duration : position
            let at = completed ? end : position
            guard at >= 10 else { return }
            let body = ProgressBody(positionSeconds: at, durationSeconds: duration > 0 ? duration : nil, speed: speed, deviceName: deviceName)
            Task { try? await api.reportProgress(episodeId: episode.id, body) }
        }
    }

    func reportProgress(force: Bool = false) {
        guard let episode = current?.episode, !episode.isPreview, let api, position >= 10 else { return }
        if !force, Date().timeIntervalSince(lastProgressReport) < 15 { return }
        lastProgressReport = Date()
        let body = ProgressBody(positionSeconds: position, durationSeconds: duration > 0 ? duration : nil,
                                speed: speed, deviceName: AudioPrefs.speakerName)
        Task { try? await api.reportProgress(episodeId: episode.id, body) }
    }

    func nowPlayingSnapshot() -> NowPlaying? {
        guard let entry = current else { return nil }
        // The contract cuts queues past 500 around the index; do the same here.
        let lower = max(0, index - 250)
        let upper = min(queue.count, lower + 500)
        return NowPlaying(
            deviceId: AudioPrefs.deviceId, deviceName: AudioPrefs.speakerName, kind: entry.kind,
            queue: queue[lower..<upper].map(\.ref), index: index - lower, positionSeconds: position,
            playing: isPlaying, shuffle: shuffle, repeat: repeatMode.rawValue, speed: speed,
            context: context, updatedAt: nil)
    }

    func reportNowPlaying(force: Bool = false) {
        guard let api, let snapshot = nowPlayingSnapshot() else { return }
        if !force, Date().timeIntervalSince(lastNowPlayingReport) < 30 { return }
        lastNowPlayingReport = Date()
        Task { try? await api.putNowPlaying(snapshot) }
    }

    func speakerState() -> SpeakerState {
        let entry = current
        return SpeakerState(
            playing: isPlaying, title: entry?.title, subtitle: entry?.artist, artUrl: entry?.artPath,
            positionSeconds: position, durationSeconds: duration > 0 ? duration : nil, volume: volume,
            kind: entry?.kind, itemId: entry?.itemId)
    }

    // MARK: - Audio session, lock screen, Siri Remote / iPhone remote / Control Center

    private func configureSession() {
        let session = AVAudioSession.sharedInstance()
        try? session.setCategory(.playback, mode: .default)
        try? session.setActive(true)
    }

    private func configureRemoteCommands() {
        let commands = MPRemoteCommandCenter.shared()
        commands.playCommand.addTarget { @Sendable [weak self] _ in
            Task { @MainActor in self?.resume() }
            return .success
        }
        commands.pauseCommand.addTarget { @Sendable [weak self] _ in
            Task { @MainActor in self?.pause() }
            return .success
        }
        commands.togglePlayPauseCommand.addTarget { @Sendable [weak self] _ in
            Task { @MainActor in self?.togglePlayPause() }
            return .success
        }
        commands.stopCommand.addTarget { @Sendable [weak self] _ in
            Task { @MainActor in self?.pause() }
            return .success
        }
        commands.nextTrackCommand.addTarget { @Sendable [weak self] _ in
            Task { @MainActor in self?.next() }
            return .success
        }
        commands.previousTrackCommand.addTarget { @Sendable [weak self] _ in
            Task { @MainActor in self?.previous() }
            return .success
        }
        commands.skipForwardCommand.preferredIntervals = [30]
        commands.skipForwardCommand.addTarget { @Sendable [weak self] _ in
            Task { @MainActor in self?.skip(by: 30) }
            return .success
        }
        commands.skipBackwardCommand.preferredIntervals = [15]
        commands.skipBackwardCommand.addTarget { @Sendable [weak self] _ in
            Task { @MainActor in self?.skip(by: -15) }
            return .success
        }
        commands.changePlaybackPositionCommand.addTarget { @Sendable [weak self] event in
            guard let event = event as? MPChangePlaybackPositionCommandEvent else { return .commandFailed }
            let target = event.positionTime
            Task { @MainActor in self?.seek(to: target) }
            return .success
        }
        commands.changePlaybackRateCommand.supportedPlaybackRates = [0.75, 1, 1.25, 1.5, 2]
        commands.changePlaybackRateCommand.addTarget { @Sendable [weak self] event in
            guard let event = event as? MPChangePlaybackRateCommandEvent else { return .commandFailed }
            let rate = Double(event.playbackRate)
            Task { @MainActor in self?.setSpeed(rate) }
            return .success
        }
        updateRemoteCommandAvailability()
    }

    private func updateRemoteCommandAvailability() {
        let commands = MPRemoteCommandCenter.shared()
        let podcast = current?.isEpisode == true
        // Podcasts get skip back 15 / forward 30; music gets previous / next.
        commands.skipForwardCommand.isEnabled = podcast
        commands.skipBackwardCommand.isEnabled = podcast
        commands.changePlaybackRateCommand.isEnabled = podcast
        commands.nextTrackCommand.isEnabled = !podcast || index + 1 < queue.count
        commands.previousTrackCommand.isEnabled = !podcast
    }

    private func updateNowPlayingInfo() {
        updateRemoteCommandAvailability()
        guard let entry = current else {
            MPNowPlayingInfoCenter.default().nowPlayingInfo = nil
            return
        }
        let center = MPNowPlayingInfoCenter.default()
        var info = center.nowPlayingInfo ?? [:]
        if artworkFor != entry.uid { info[MPMediaItemPropertyArtwork] = nil }
        info[MPMediaItemPropertyTitle] = entry.title
        info[MPMediaItemPropertyArtist] = entry.artist
        info[MPMediaItemPropertyAlbumTitle] = entry.track?.album ?? entry.episode?.podcastTitle
        info[MPMediaItemPropertyPlaybackDuration] = duration
        info[MPNowPlayingInfoPropertyElapsedPlaybackTime] = position
        info[MPNowPlayingInfoPropertyPlaybackRate] = isPlaying ? speed : 0
        info[MPNowPlayingInfoPropertyDefaultPlaybackRate] = speed
        info[MPNowPlayingInfoPropertyMediaType] = MPNowPlayingInfoMediaType.audio.rawValue
        info[MPNowPlayingInfoPropertyPlaybackQueueIndex] = index
        info[MPNowPlayingInfoPropertyPlaybackQueueCount] = queue.count
        center.nowPlayingInfo = info
        center.playbackState = isPlaying ? .playing : .paused
        guard artworkFor != entry.uid, let url = api?.art(entry.artPath, size: 600) else { return }
        artworkFor = entry.uid
        let uid = entry.uid
        Task { [weak self] in
            guard let (data, response) = try? await URLSession.shared.data(from: url),
                  (response as? HTTPURLResponse)?.statusCode == 200,
                  let image = UIImage(data: data) else { return }
            guard let self, self.current?.uid == uid else { return }
            let artwork = Self.artwork(image)
            var info = MPNowPlayingInfoCenter.default().nowPlayingInfo ?? [:]
            info[MPMediaItemPropertyArtwork] = artwork
            MPNowPlayingInfoCenter.default().nowPlayingInfo = info
        }
    }
}

extension AudioPlayer {
    // Nonisolated on purpose: MediaPlayer calls the request handler on its own queue, and a
    // closure written inside a @MainActor method inherits main-actor isolation and traps there.
    nonisolated static func artwork(_ image: UIImage) -> MPMediaItemArtwork {
        MPMediaItemArtwork(boundsSize: image.size) { _ in image }
    }
}

extension Array {
    subscript(safe index: Int) -> Element? { indices.contains(index) ? self[index] : nil }
}
