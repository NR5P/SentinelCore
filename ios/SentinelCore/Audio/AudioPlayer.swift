import AVFoundation
import Foundation
import MediaPlayer
import Observation
#if os(iOS)
import UIKit
#else
import AppKit
#endif

// The one player behind both the Music and Podcasts tabs (core/docs/MUSIC_AND_PODCASTS.md §11).
//
// An AVQueuePlayer always holds the current item and the next one, so the next track is already
// buffered when this one ends - that is what makes albums play (nearly) gaplessly. The queue itself
// is ours: `queue` + `index`, with shuffle/repeat worked out here, and only ever the next entry
// handed to AVFoundation. Each AVPlayerItem carries its queue entry's uid, so whichever item
// AVFoundation moves on to tells us where in the queue we are.
//
// It also owns everything a system media player is expected to do: the lock screen / Control
// Center / CarPlay Now Playing card and its remote commands, background audio, interruptions and
// headphones being pulled out, Sound Check (ReplayGain), speed without chipmunks, a sleep timer that
// fades out, and the reports the server builds Home, stats and "continue on another device" from.
@MainActor
@Observable
final class AudioPlayer {
    static let shared = AudioPlayer()

    // MARK: Observable state

    private(set) var queue: [AudioItem] = []
    private(set) var index = 0
    private(set) var isPlaying = false
    private(set) var isBuffering = false
    private(set) var position: Double = 0
    private(set) var duration: Double = 0
    private(set) var shuffle = false
    private(set) var repeatMode: AudioRepeat = .off
    private(set) var speed: Double = 1
    private(set) var context: AudioPlayContext?
    private(set) var chapters: [PodcastChapter] = []
    // While the Morning Briefing is being read out: the sentence being spoken.
    private(set) var speaking: String?
    private(set) var sleepEndsAt: Date?
    private(set) var sleepAtEndOfItem = false
    private(set) var errorMessage: String?
    // The volume a speaker command or the Mac's own slider set (0...1), before Sound Check.
    private(set) var userVolume: Double = UserDefaults.standard.object(forKey: "audio.volume") as? Double ?? 1
    var showNowPlaying = false

    var current: AudioItem? { queue.indices.contains(index) ? queue[index] : nil }
    var hasItem: Bool { current != nil || speaking != nil }
    var upNext: [AudioItem] { queue.indices.contains(index + 1) ? Array(queue[(index + 1)...]) : [] }
    var isEpisode: Bool { current?.isEpisode == true }
    var currentChapter: PodcastChapter? {
        chapters.last { $0.startSeconds <= position + 0.25 }
    }
    var sleepTimerActive: Bool { sleepEndsAt != nil || sleepAtEndOfItem }

    // MARK: Internals

    @ObservationIgnored private let player = AVQueuePlayer()
    @ObservationIgnored private var originalOrder: [AudioItem]?
    @ObservationIgnored private var timeObserver: Any?
    @ObservationIgnored private var currentItemObservation: NSKeyValueObservation?
    @ObservationIgnored private var statusObservation: NSKeyValueObservation?
    @ObservationIgnored private var itemStatusObservation: NSKeyValueObservation?
    @ObservationIgnored private var observers: [NSObjectProtocol] = []
    @ObservationIgnored private var listened: Double = 0
    @ObservationIgnored private var lastTickPosition: Double?
    @ObservationIgnored private var lastProgressReport = Date.distantPast
    @ObservationIgnored private var lastNowPlayingReport = Date.distantPast
    @ObservationIgnored private var pendingSeek: Double?
    @ObservationIgnored private var fade: Double = 1
    @ObservationIgnored private var duck: Double = 1
    @ObservationIgnored private var duckTask: Task<Void, Never>?
    @ObservationIgnored private var rampTask: Task<Void, Never>?
    @ObservationIgnored private var briefingTask: Task<Void, Never>?
    @ObservationIgnored private var artwork: (uid: UUID, image: PlatformImage)?
    @ObservationIgnored private var showSpeeds: [String: Double] = [:]
    @ObservationIgnored private var speedSaveTask: Task<Void, Never>?
    @ObservationIgnored private var outroSkipped: UUID?
    @ObservationIgnored private var introSkipped: UUID?
    @ObservationIgnored private var restored = false

    // Settings (Settings → Music & Podcasts on This Device).
    var soundCheck: Bool {
        get { UserDefaults.standard.object(forKey: "audio.soundCheck") as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: "audio.soundCheck"); applyVolume() }
    }
    var defaultPodcastSpeed: Double {
        get { UserDefaults.standard.object(forKey: "audio.podcastSpeed") as? Double ?? 1 }
        set { UserDefaults.standard.set(newValue, forKey: "audio.podcastSpeed") }
    }
    var dataSaver: Bool {
        get { UserDefaults.standard.bool(forKey: "audio.dataSaver") }
        set { UserDefaults.standard.set(newValue, forKey: "audio.dataSaver") }
    }

    private var client: SentinelClient { AppModel.shared.client }

    init() {
        player.automaticallyWaitsToMinimizeStalling = true
        player.actionAtItemEnd = .advance
        #if os(iOS)
        player.allowsExternalPlayback = true
        #endif
        observePlayer()
        Self.installRemoteCommands()
        #if os(iOS)
        observeAudioSession()
        #endif
    }

    // MARK: - Starting playback

    /// Replaces the queue and starts playing. `startAt` is an index into `items`; with `shuffle`
    /// the whole list is shuffled (the chosen item, if any, first).
    func play(_ items: [AudioItem], startAt: Int = 0, position: Double? = nil, context: AudioPlayContext? = nil,
              shuffle: Bool = false, autoplay: Bool = true) {
        guard !items.isEmpty else { return }
        cancelBriefing()
        finishCurrent(skipped: false)
        errorMessage = nil
        var items = items.map { var copy = $0; copy.uid = UUID(); return copy }
        var start = min(max(startAt, 0), items.count - 1)
        originalOrder = nil
        if shuffle {
            originalOrder = items
            let first = items.remove(at: start)
            items = (startAt > 0 ? [first] : []) + (items + (startAt > 0 ? [] : [first])).shuffled()
            start = 0
        }
        self.shuffle = shuffle
        self.context = context
        queue = items
        index = start
        startCurrent(at: position ?? resumePosition(for: items[start]), autoplay: autoplay)
    }

    func play(tracks: [MusicTrack], startAt: Int = 0, context: AudioPlayContext? = nil, shuffle: Bool = false) {
        play(tracks.map(AudioItem.init(track:)), startAt: startAt, context: context, shuffle: shuffle)
    }

    func play(episode: PodcastEpisode, queue more: [PodcastEpisode] = [], at position: Double? = nil, context: AudioPlayContext? = nil) {
        let items = ([episode] + more.filter { $0.id != episode.id }).map(AudioItem.init(episode:))
        play(items, position: position,
             context: context ?? AudioPlayContext(type: "podcast", id: episode.podcastId, name: episode.podcastTitle))
    }

    func playNext(_ items: [AudioItem]) {
        guard !items.isEmpty else { return }
        if queue.isEmpty { play(items); return }
        let fresh = items.map { var copy = $0; copy.uid = UUID(); return copy }
        queue.insert(contentsOf: fresh, at: index + 1)
        originalOrder?.append(contentsOf: fresh)
        syncUpcoming()
        reportNowPlaying()
        AudioToast.shared.show(fresh.count == 1 ? "Playing next" : "\(fresh.count) items playing next")
    }

    func playLater(_ items: [AudioItem]) {
        guard !items.isEmpty else { return }
        if queue.isEmpty { play(items); return }
        let fresh = items.map { var copy = $0; copy.uid = UUID(); return copy }
        queue.append(contentsOf: fresh)
        originalOrder?.append(contentsOf: fresh)
        syncUpcoming()
        reportNowPlaying()
        AudioToast.shared.show(fresh.count == 1 ? "Added to queue" : "\(fresh.count) items added to queue")
    }

    // MARK: - Transport

    func togglePlayPause() { isPlaying ? pause() : resume() }

    func resume() {
        guard current != nil else { return }
        if player.currentItem == nil {
            startCurrent(at: position, autoplay: true)
            return
        }
        activateSession()
        player.playImmediately(atRate: Float(speed))
        reportNowPlaying(force: true)
    }

    func pause() {
        player.pause()
        reportEpisodeProgress(force: true)
        reportNowPlaying(force: true)
    }

    func stop() {
        cancelBriefing()
        finishCurrent(skipped: false)
        player.pause()
        player.removeAllItems()
        queue = []
        index = 0
        position = 0
        duration = 0
        chapters = []
        context = nil
        cancelSleepTimer()
        updateNowPlayingInfo()
        SpeakerHost.shared.playerChanged()
        Task { try? await client.deleteNowPlaying(deviceId: AudioDevice.id) }
    }

    func next() {
        guard !queue.isEmpty else { return }
        if let next = nextIndex(after: index, manual: true) {
            jump(to: next, skipped: true)
        } else {
            // End of the queue: stop on the last item, back at its start.
            finishCurrent(skipped: true)
            player.pause()
            seek(to: 0)
        }
    }

    func previous() {
        guard !queue.isEmpty else { return }
        if position > 3 || index == 0 && repeatMode != .all {
            seek(to: 0)
            return
        }
        let target = index > 0 ? index - 1 : queue.count - 1
        jump(to: target, skipped: true)
    }

    func jump(to newIndex: Int, skipped: Bool = true) {
        guard queue.indices.contains(newIndex) else { return }
        finishCurrent(skipped: skipped)
        index = newIndex
        startCurrent(at: resumePosition(for: queue[newIndex]), autoplay: true)
    }

    func seek(to seconds: Double) {
        let target = max(0, duration > 0 ? min(seconds, duration - 0.5) : seconds)
        position = target
        lastTickPosition = target
        guard let item = player.currentItem else {
            pendingSeek = target
            return
        }
        guard item.status == .readyToPlay else {
            pendingSeek = target
            return
        }
        Self.preciseSeek(player, to: target) { [weak self] in
            Task { @MainActor in
                self?.updateNowPlayingInfo()
                self?.reportNowPlaying(force: true)
                SpeakerHost.shared.playerChanged()
            }
        }
    }

    func skip(by seconds: Double) { seek(to: position + seconds) }

    func setSpeed(_ value: Double) {
        speed = min(max(value, 0.5), 3)
        player.defaultRate = Float(speed)
        if isPlaying { player.rate = Float(speed) }
        updateNowPlayingInfo()
        // Podcasts remember a speed per show (§11): saved to the show's my-settings.
        if let episode = current?.episode, let podcastId = episode.podcastId {
            showSpeeds[podcastId] = speed
            speedSaveTask?.cancel()
            let chosen = speed
            speedSaveTask = Task {
                try? await Task.sleep(for: .seconds(1.5))
                guard !Task.isCancelled else { return }
                await Self.saveShowSpeed(podcastId: podcastId, speed: chosen)
            }
        } else {
            UserDefaults.standard.set(speed, forKey: "audio.musicSpeed")
        }
    }

    private static func saveShowSpeed(podcastId: String, speed: Double) async {
        let client = AppModel.shared.client
        guard let podcast = try? await client.podcast(id: podcastId) else { return }
        var settings = podcast.mySettings ?? PodcastMySettings()
        settings.playbackSpeed = speed
        _ = try? await client.savePodcastMySettings(id: podcastId, settings)
    }

    func toggleShuffle() {
        setShuffle(!shuffle)
    }

    func setShuffle(_ on: Bool) {
        guard on != shuffle else { return }
        shuffle = on
        guard let current else { return }
        if on {
            originalOrder = queue
            var rest = queue
            rest.remove(at: index)
            queue = [current] + rest.shuffled()
            index = 0
        } else if let original = originalOrder {
            // Anything removed while shuffled stays removed.
            let kept = Set(queue.map(\.uid))
            queue = original.filter { kept.contains($0.uid) }
            index = queue.firstIndex { $0.uid == current.uid } ?? 0
            originalOrder = nil
        }
        syncUpcoming()
        applyVolume()
        updateNowPlayingInfo()
        reportNowPlaying(force: true)
    }

    func cycleRepeat() { setRepeat(repeatMode.next) }

    func setRepeat(_ mode: AudioRepeat) {
        repeatMode = mode
        syncUpcoming()
        updateNowPlayingInfo()
        reportNowPlaying(force: true)
    }

    // MARK: Queue editing (the queue view shows upNext, so offsets are relative to index + 1)

    func moveUpNext(from source: IndexSet, to destination: Int) {
        var upcoming = upNext
        upcoming.move(fromOffsets: source, toOffset: destination)
        queue = Array(queue[...index]) + upcoming
        syncUpcoming()
        reportNowPlaying()
    }

    func removeUpNext(at offsets: IndexSet) {
        let uids = Set(offsets.map { upNext[$0].uid })
        queue.removeAll { uids.contains($0.uid) && $0.uid != current?.uid }
        syncUpcoming()
        reportNowPlaying()
    }

    func clearUpNext() {
        guard current != nil else { return }
        queue = Array(queue[...index])
        syncUpcoming()
        reportNowPlaying()
    }

    func jump(toUpNext item: AudioItem) {
        guard let target = queue.firstIndex(where: { $0.uid == item.uid }) else { return }
        jump(to: target)
    }

    // Keep a track's love/rating current in the queue after it changes elsewhere.
    func updateTrack(_ track: MusicTrack) {
        for i in queue.indices where queue[i].track?.id == track.id {
            var updated = track
            updated.entryId = queue[i].track?.entryId
            queue[i].track = updated
        }
        updateNowPlayingInfo()
    }

    // MARK: - Volume: Sound Check, fades and ducking

    func setUserVolume(_ value: Double, ramp: Double = 0) {
        rampTask?.cancel()
        let target = min(max(value, 0), 1)
        guard ramp > 0 else {
            userVolume = target
            UserDefaults.standard.set(target, forKey: "audio.volume")
            applyVolume()
            return
        }
        // Alarms fade in from silence.
        userVolume = 0
        applyVolume()
        rampTask = Task {
            let steps = max(Int(ramp * 4), 1)
            for step in 1...steps {
                try? await Task.sleep(for: .milliseconds(250))
                if Task.isCancelled { return }
                userVolume = target * Double(step) / Double(steps)
                applyVolume()
            }
            UserDefaults.standard.set(target, forKey: "audio.volume")
        }
    }

    // `duck` drops to 20% until unduck, or 30 seconds (§8).
    func setDucked(_ ducked: Bool) {
        duckTask?.cancel()
        duck = ducked ? 0.2 : 1
        applyVolume()
        if ducked {
            duckTask = Task {
                try? await Task.sleep(for: .seconds(30))
                guard !Task.isCancelled else { return }
                duck = 1
                applyVolume()
            }
        }
    }

    // ReplayGain: volume = 10^(gain/20), clamped to 1.0, album gain when an album plays in order.
    private var gainMultiplier: Double {
        guard soundCheck, let gain = current?.track?.replayGain else { return 1 }
        let albumInOrder = context?.type == "album" && !shuffle
        guard let db = albumInOrder ? (gain.albumGain ?? gain.trackGain) : (gain.trackGain ?? gain.albumGain) else { return 1 }
        return min(1, pow(10, db / 20))
    }

    private func applyVolume() {
        player.volume = Float(gainMultiplier * fade * duck * userVolume)
    }

    // MARK: - Sleep timer

    func setSleepTimer(minutes: Int) {
        sleepAtEndOfItem = false
        sleepEndsAt = Date().addingTimeInterval(TimeInterval(minutes * 60))
        AudioToast.shared.show("Sleep timer: \(minutes) min")
    }

    func setSleepAtEndOfItem() {
        sleepEndsAt = nil
        sleepAtEndOfItem = true
        AudioToast.shared.show(isEpisode ? "Stopping at the end of this episode" : "Stopping at the end of this song")
    }

    func cancelSleepTimer() {
        sleepEndsAt = nil
        sleepAtEndOfItem = false
        if fade != 1 {
            fade = 1
            applyVolume()
        }
    }

    var sleepRemaining: TimeInterval? {
        if let sleepEndsAt { return max(0, sleepEndsAt.timeIntervalSinceNow) }
        if sleepAtEndOfItem { return max(0, duration - position) / max(speed, 0.1) }
        return nil
    }

    private func tickSleepTimer() {
        guard let remaining = sleepRemaining, isPlaying else { return }
        // A 10-second fade to silence, then pause.
        let level = min(1, remaining / 10)
        if abs(level - fade) > 0.01 {
            fade = level
            applyVolume()
        }
        if remaining <= 0.05, sleepEndsAt != nil {
            pause()
            cancelSleepTimer()
        }
    }

    // MARK: - Morning Briefing (§9)

    func playBriefing() {
        cancelBriefing()
        player.pause()
        briefingTask = Task {
            do {
                let briefing = try await client.morningBriefing()
                var lines = [briefing.greeting].compactMap { $0 } + (briefing.speech ?? [])
                if lines.isEmpty { lines = ["Good morning."] }
                activateSession(spoken: true)
                for line in lines {
                    if Task.isCancelled { return }
                    speaking = line
                    updateNowPlayingInfo()
                    await AudioSpeech.shared.speak(line)
                }
                speaking = nil
                if Task.isCancelled { return }
                if let episodes = briefing.episodes, !episodes.isEmpty {
                    play(episodes.map(AudioItem.init(episode:)), position: nil,
                         context: AudioPlayContext(type: "queue", id: "briefing", name: "Morning Briefing"))
                } else {
                    AudioToast.shared.show("That's your briefing")
                }
            } catch {
                speaking = nil
                errorMessage = error.localizedDescription
                AudioToast.shared.show("Couldn't load the briefing")
            }
        }
    }

    private func cancelBriefing() {
        guard briefingTask != nil || speaking != nil else { return }
        briefingTask?.cancel()
        briefingTask = nil
        AudioSpeech.shared.stop()
        speaking = nil
    }

    // Speaks over whatever is playing: duck, speak, unduck (a speaker's `announce`).
    // With nothing playing it just speaks, at `volume` when one is given (§8).
    func announce(_ text: String, volume: Double? = nil) async {
        let wasDucked = duck < 1
        if isPlaying { setDucked(true) }
        activateSession(spoken: true)
        await AudioSpeech.shared.speak(text, volume: isPlaying ? 1 : volume.map { Float($0) } ?? 1)
        if !wasDucked { setDucked(false) }
    }

    // MARK: - Building player items

    private func resumePosition(for item: AudioItem) -> Double {
        guard let episode = item.episode, let progress = episode.progress, !progress.isPlayed else { return 0 }
        // A few seconds of context before where they stopped.
        return max(0, progress.position - 2)
    }

    private func makePlayerItem(_ entry: AudioItem) -> AudioPlayerItem? {
        let url: URL?
        if let local = AudioDownloads.shared.localURL(kind: entry.kind, id: entry.itemId) {
            url = local
        } else if let track = entry.track {
            url = client.trackStreamURL(track, maxBitrate: dataSaver ? 128 : nil)
        } else if let episode = entry.episode {
            url = client.episodeAudioURL(episode)
        } else {
            url = nil
        }
        guard let url else { return nil }
        var options: [String: Any] = [:]
        if !url.isFileURL, let cookies = HTTPCookieStorage.shared.cookies(for: url), !cookies.isEmpty {
            options[AVURLAssetHTTPCookiesKey] = cookies
        }
        let item = AudioPlayerItem(asset: AVURLAsset(url: url, options: options), uid: entry.uid)
        // Speed without the chipmunk: time-domain stretching suits both voices and music.
        item.audioTimePitchAlgorithm = .timeDomain
        item.preferredForwardBufferDuration = entry.isEpisode ? 30 : 0
        return item
    }

    private func startCurrent(at start: Double, autoplay: Bool) {
        guard let entry = current, let item = makePlayerItem(entry) else {
            errorMessage = "This can't be played - the server address isn't known yet."
            return
        }
        player.removeAllItems()
        player.insert(item, after: nil)
        pendingSeek = start > 0.5 ? start : nil
        position = start
        duration = entry.duration
        listened = 0
        lastTickPosition = nil
        syncUpcoming()
        // currentItem's KVO fires for the new item and does the per-item setup.
        if autoplay {
            activateSession()
            player.playImmediately(atRate: Float(speed))
        }
    }

    // The item after the current one, already buffering. Called whenever what comes next changes.
    private func syncUpcoming() {
        guard let currentItem = player.currentItem else { return }
        for item in player.items() where item !== currentItem {
            player.remove(item)
        }
        guard let next = nextIndex(after: index, manual: false), let nextItem = makePlayerItem(queue[next]) else { return }
        if player.canInsert(nextItem, after: currentItem) {
            player.insert(nextItem, after: currentItem)
        }
    }

    private func nextIndex(after i: Int, manual: Bool) -> Int? {
        if repeatMode == .one && !manual { return i }
        if sleepAtEndOfItem && !manual { return nil }
        if i + 1 < queue.count { return i + 1 }
        if repeatMode != .off && !queue.isEmpty { return 0 }
        return nil
    }

    // MARK: - Watching the player

    private func observePlayer() {
        timeObserver = player.addPeriodicTimeObserver(forInterval: CMTime(seconds: 0.5, preferredTimescale: 600), queue: .main) { [weak self] time in
            let seconds = time.seconds
            MainActor.assumeIsolated { self?.tick(seconds) }
        }
        // KVO fires on whichever thread made the change, so these handlers are built outside the
        // main actor (see the AVKVO helpers below) and hop back to it themselves.
        currentItemObservation = Self.observeCurrentItem(player) { [weak self] uid, hasItem in
            Task { @MainActor in self?.currentItemChanged(uid: uid, hasItem: hasItem) }
        }
        statusObservation = Self.observeTimeControl(player) { [weak self] status in
            Task { @MainActor in self?.timeControlChanged(status) }
        }
        observers.append(NotificationCenter.default.addObserver(
            forName: AVPlayerItem.didPlayToEndTimeNotification, object: nil, queue: .main
        ) { [weak self] note in
            let uid = (note.object as? AudioPlayerItem)?.uid
            MainActor.assumeIsolated { self?.itemDidEnd(uid: uid) }
        })
        observers.append(NotificationCenter.default.addObserver(
            forName: AVPlayerItem.failedToPlayToEndTimeNotification, object: nil, queue: .main
        ) { [weak self] note in
            let message = (note.userInfo?[AVPlayerItemFailedToPlayToEndTimeErrorKey] as? Error)?.localizedDescription
            MainActor.assumeIsolated { self?.errorMessage = message ?? "Playback stopped." }
        })
    }

    private func tick(_ seconds: Double) {
        guard seconds.isFinite, pendingSeek == nil else { return }
        if let last = lastTickPosition, isPlaying {
            let delta = seconds - last
            if delta > 0, delta < 2 * max(speed, 1) { listened += delta / max(speed, 0.1) }
        }
        lastTickPosition = seconds
        position = seconds
        if let item = player.currentItem {
            let itemDuration = item.duration.seconds
            if itemDuration.isFinite, itemDuration > 0 { duration = itemDuration }
        }
        tickSleepTimer()
        skipOutroIfNeeded()
        if isPlaying {
            reportEpisodeProgress()
            reportNowPlaying()
            SpeakerHost.shared.playerTicked()
        }
    }

    private func currentItemChanged(uid: UUID?, hasItem: Bool) {
        guard hasItem, let uid else {
            // Played off the end of the queue.
            if !queue.isEmpty, player.items().isEmpty {
                position = 0
                isPlaying = false
                if sleepAtEndOfItem { cancelSleepTimer() }
                // Leave the last item loaded at its start, paused, so Play starts it again.
                if let entry = current, let item = makePlayerItem(entry) {
                    player.insert(item, after: nil)
                    syncUpcoming()
                }
                updateNowPlayingInfo()
                reportNowPlaying(force: true)
                SpeakerHost.shared.playerChanged()
            }
            return
        }
        if let newIndex = queue.firstIndex(where: { $0.uid == uid }), newIndex != index || current?.uid != uid {
            index = newIndex
        }
        if sleepAtEndOfItem, lastTickPosition != nil, pendingSeek == nil, position < 1, isPlaying {
            // The item the sleep timer was waiting for has ended.
        }
        listened = 0
        lastTickPosition = nil
        if let entry = current { duration = entry.duration }
        syncUpcoming()
        applyVolume()
        prepareForCurrentItem()
        watchItemStatus()
        updateNowPlayingInfo()
        reportNowPlaying(force: true)
        SpeakerHost.shared.playerChanged()
        if let track = current?.track {
            let client = client
            let id = track.id
            Task { try? await client.reportNowPlayingTrack(id: id) }
        }
        topUpRadioIfNeeded()
    }

    // A radio never runs dry: when a few songs are left, ask for more, excluding what it played.
    @ObservationIgnored private var toppingUp = false

    private func topUpRadioIfNeeded() {
        guard context?.type == "radio", let contextId = context?.id, upNext.count < 5, !toppingUp else { return }
        let parts = contextId.split(separator: ":", maxSplits: 1).map(String.init)
        guard parts.count == 2 else { return }
        toppingUp = true
        let played = queue.compactMap { $0.track?.id }
        let client = client
        Task {
            defer { toppingUp = false }
            guard let more = try? await client.musicRadio(seedType: parts[0], seedId: parts[1], excludeIds: played),
                  context?.id == contextId else { return }
            let known = Set(played)
            let fresh = more.tracks.filter { !known.contains($0.id) }.map(AudioItem.init(track:))
            guard !fresh.isEmpty else { return }
            queue.append(contentsOf: fresh)
            originalOrder?.append(contentsOf: fresh)
            syncUpcoming()
        }
    }

    private func watchItemStatus() {
        guard let item = player.currentItem else { itemStatusObservation = nil; return }
        itemStatusObservation = Self.observeStatus(item) { [weak self] status, message in
            Task { @MainActor in self?.itemStatusChanged(status, message: message) }
        }
    }

    // MARK: AVKVO helpers - nonisolated, so the closures carry no main-actor expectation. A
    // closure formed inside a @MainActor method inherits that isolation, and Swift 6 traps
    // ("Block was expected to execute on queue main-thread") when AVFoundation or MediaRemote
    // calls it from one of their own queues.

    private nonisolated static func observeCurrentItem(_ player: AVQueuePlayer, _ handler: @escaping @Sendable (UUID?, Bool) -> Void) -> NSKeyValueObservation {
        player.observe(\.currentItem, options: [.new]) { player, _ in
            handler((player.currentItem as? AudioPlayerItem)?.uid, player.currentItem != nil)
        }
    }

    private nonisolated static func observeTimeControl(_ player: AVQueuePlayer, _ handler: @escaping @Sendable (AVPlayer.TimeControlStatus) -> Void) -> NSKeyValueObservation {
        player.observe(\.timeControlStatus, options: [.new]) { player, _ in handler(player.timeControlStatus) }
    }

    private nonisolated static func observeStatus(_ item: AVPlayerItem, _ handler: @escaping @Sendable (AVPlayerItem.Status, String?) -> Void) -> NSKeyValueObservation {
        item.observe(\.status, options: [.initial, .new]) { item, _ in handler(item.status, item.error?.localizedDescription) }
    }

    private nonisolated static func preciseSeek(_ player: AVQueuePlayer, to seconds: Double, then done: @escaping @Sendable () -> Void) {
        player.seek(to: CMTime(seconds: seconds, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero) { _ in done() }
    }

    private nonisolated static func artwork(_ image: PlatformImage) -> MPMediaItemArtwork {
        MPMediaItemArtwork(boundsSize: image.size) { _ in image }
    }

    private func itemStatusChanged(_ status: AVPlayerItem.Status, message: String?) {
        switch status {
        case .readyToPlay:
            if let target = pendingSeek {
                pendingSeek = nil
                seek(to: target)
            }
            errorMessage = nil
        case .failed:
            errorMessage = message ?? "This couldn't be played."
            pendingSeek = nil
        default:
            break
        }
    }

    private func timeControlChanged(_ status: AVPlayer.TimeControlStatus) {
        let playing = status != .paused
        isBuffering = status == .waitingToPlayAtSpecifiedRate
        guard playing != isPlaying else { return }
        isPlaying = playing
        if !playing {
            reportEpisodeProgress(force: true)
        }
        updateNowPlayingInfo()
        reportNowPlaying(force: true)
        SpeakerHost.shared.playerChanged()
    }

    private func itemDidEnd(uid: UUID?) {
        guard let uid, let entry = queue.first(where: { $0.uid == uid }) else { return }
        report(entry: entry, completed: true, skipped: false, endPosition: entry.duration > 0 ? entry.duration : duration)
        listened = 0
        if sleepAtEndOfItem {
            // The timer said "end of this one": stop here instead of going on.
            player.pause()
            cancelSleepTimer()
        }
    }

    // Per-item setup: speed, chapters, skipping the intro.
    private func prepareForCurrentItem() {
        guard let entry = current else { return }
        if let episode = entry.episode {
            let showSpeed = episode.podcastId.flatMap { showSpeeds[$0] }
            let target = showSpeed ?? defaultPodcastSpeed
            if abs(target - speed) > 0.01 { applySpeed(target) }
            chapters = []
            loadShowSettings(for: episode, uid: entry.uid)
            if episode.hasChapters == true {
                let id = episode.id
                Task {
                    let loaded = (try? await client.episodeChapters(id: id)) ?? []
                    if current?.episode?.id == id { chapters = loaded.sorted { $0.startSeconds < $1.startSeconds } }
                }
            }
        } else {
            chapters = []
            let musicSpeed = UserDefaults.standard.object(forKey: "audio.musicSpeed") as? Double ?? 1
            if abs(musicSpeed - speed) > 0.01 { applySpeed(musicSpeed) }
        }
    }

    private func applySpeed(_ value: Double) {
        speed = value
        player.defaultRate = Float(value)
        if isPlaying { player.rate = Float(value) }
    }

    // The show's own speed and intro/outro skips live in its my-settings (§5).
    @ObservationIgnored private var showSettings: [String: PodcastMySettings] = [:]

    private func loadShowSettings(for episode: PodcastEpisode, uid: UUID) {
        guard let podcastId = episode.podcastId else { return }
        if let cached = showSettings[podcastId] {
            applyShowSettings(cached, podcastId: podcastId, uid: uid)
            return
        }
        Task {
            guard let podcast = try? await client.podcast(id: podcastId), let settings = podcast.mySettings else { return }
            showSettings[podcastId] = settings
            if current?.uid == uid { applyShowSettings(settings, podcastId: podcastId, uid: uid) }
        }
    }

    private func applyShowSettings(_ settings: PodcastMySettings, podcastId: String, uid: UUID) {
        if showSpeeds[podcastId] == nil, let saved = settings.playbackSpeed {
            showSpeeds[podcastId] = saved
            if abs(saved - speed) > 0.01 { applySpeed(saved) }
        }
        if let intro = settings.skipIntroSeconds, intro > 0, introSkipped != uid, position < Double(intro) {
            introSkipped = uid
            seek(to: Double(intro))
        }
    }

    private func skipOutroIfNeeded() {
        guard let entry = current, let episode = entry.episode, let podcastId = episode.podcastId,
              let outro = showSettings[podcastId]?.skipOutroSeconds, outro > 0, duration > Double(outro) + 30,
              outroSkipped != entry.uid, position >= duration - Double(outro) else { return }
        outroSkipped = entry.uid
        report(entry: entry, completed: true, skipped: false, endPosition: duration)
        if let next = nextIndex(after: index, manual: false), next != index {
            index = next
            startCurrent(at: resumePosition(for: queue[next]), autoplay: true)
        } else {
            pause()
        }
    }

    // MARK: - Reporting (§4 played, §6 progress, §7 now-playing)

    // The current item is being left (skipped, replaced, stopped) - say how much of it was heard.
    private func finishCurrent(skipped: Bool) {
        guard let entry = current else { return }
        report(entry: entry, completed: false, skipped: skipped, endPosition: position)
        listened = 0
    }

    private func report(entry: AudioItem, completed: Bool, skipped: Bool, endPosition: Double) {
        if let track = entry.track {
            let heard = completed ? max(listened, min(track.duration, listened + 1)) : listened
            guard heard >= 1 || completed else { return }
            AudioReportQueue.shared.played(trackId: track.id, body: MusicPlayedBody(
                secondsListened: (heard * 10).rounded() / 10, completed: completed, skipped: skipped && !completed,
                playedAt: nil, deviceName: AudioDevice.name))
            updatePlayCount(trackId: track.id, completed: completed)
        } else if let episode = entry.episode {
            let total = entry.duration > 0 ? entry.duration : duration
            let at = completed ? max(total, endPosition) : endPosition
            guard at >= 10 else { return }
            AudioReportQueue.shared.progress(episodeId: episode.id, position: at, duration: total > 0 ? total : nil, speed: speed)
            lastProgressReport = Date()
            updateEpisodeProgress(id: episode.id, position: at, duration: total, played: completed)
        }
    }

    private func updatePlayCount(trackId: String, completed: Bool) {
        guard completed else { return }
        // Read, then write: `queue[i].x = queue[i].x + 1` reads the observed array inside its own
        // modify access, which Swift's exclusivity checking traps on.
        for i in queue.indices where queue[i].track?.id == trackId {
            let count = (queue[i].track?.playCount ?? 0) + 1
            queue[i].track?.playCount = count
        }
    }

    private func updateEpisodeProgress(id: String, position: Double, duration: Double, played: Bool) {
        for i in queue.indices where queue[i].episode?.id == id {
            var progress = queue[i].episode?.progress ?? EpisodeProgress()
            progress.positionSeconds = position
            progress.durationSeconds = duration
            progress.played = played || (duration > 0 && (position >= duration - 30 || position / duration >= 0.95))
            queue[i].episode?.progress = progress
        }
    }

    // Every 15 s while playing, and on pause and close.
    func reportEpisodeProgress(force: Bool = false) {
        guard let entry = current, let episode = entry.episode, position >= 10 else { return }
        guard force || Date().timeIntervalSince(lastProgressReport) >= 15 else { return }
        lastProgressReport = Date()
        let total = duration > 0 ? duration : entry.duration
        AudioReportQueue.shared.progress(episodeId: episode.id, position: position, duration: total > 0 ? total : nil, speed: speed)
        updateEpisodeProgress(id: episode.id, position: position, duration: total, played: false)
    }

    // On track change, play/pause and seek, and every 30 s while playing.
    func reportNowPlaying(force: Bool = false) {
        guard let entry = current else { return }
        guard force || Date().timeIntervalSince(lastNowPlayingReport) >= 30 else { return }
        lastNowPlayingReport = Date()
        // At most 500 entries, centred on the current one (§7).
        let lower = max(0, min(index - 250, queue.count - 500))
        let window = Array(queue[lower..<min(queue.count, lower + 500)])
        let report = NowPlayingReport(
            deviceId: AudioDevice.id, deviceName: AudioDevice.name, kind: entry.isEpisode ? "podcast" : "music",
            queue: window.map(\.ref), index: index - lower, positionSeconds: (position * 10).rounded() / 10,
            playing: isPlaying, shuffle: shuffle, repeat: repeatMode.rawValue, speed: speed, context: context)
        let client = client
        Task { try? await client.putNowPlaying(report) }
    }

    // Leaving the app: say where we got to.
    func appWillResignActive() {
        reportEpisodeProgress(force: true)
        reportNowPlaying(force: true)
    }

    // MARK: - Continuing from another device (§7)

    func takeOver(_ device: NowPlayingDevice) async throws {
        let resolved = try await client.resolveNowPlaying(deviceId: device.deviceId)
        let refs = resolved.nowPlaying?.queue ?? device.queue ?? []
        let tracks = Dictionary((resolved.tracks ?? []).map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        let episodes = Dictionary((resolved.episodes ?? []).map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        var items: [AudioItem] = []
        var startIndex = 0
        let wanted = resolved.nowPlaying?.index ?? device.index ?? 0
        for (i, ref) in refs.enumerated() {
            if i == wanted { startIndex = items.count }
            if ref.type == "episode", let episode = episodes[ref.id] { items.append(AudioItem(episode: episode)) }
            else if let track = tracks[ref.id] { items.append(AudioItem(track: track)) }
        }
        guard !items.isEmpty else { throw ClientError.server(status: 404, message: "Nothing to continue.") }
        let state = resolved.nowPlaying ?? device
        play(items, startAt: startIndex, position: state.positionSeconds ?? 0, context: state.context, shuffle: false)
        shuffle = state.shuffle ?? false
        if let mode = AudioRepeat(rawValue: state.repeat ?? "off") { setRepeat(mode) }
        if let speed = state.speed, abs(speed - self.speed) > 0.01 { applySpeed(speed) }
    }

    // Speaker `play`: the queue comes as bare ids. Resolved through this device's own now-playing
    // entry - the one route that turns a queue of ids into tracks and episodes in a single request.
    func playRefs(_ refs: [AudioQueueRef], index start: Int, position: Double, shuffle: Bool, context: AudioPlayContext?) async {
        guard !refs.isEmpty else { return }
        let report = NowPlayingReport(
            deviceId: AudioDevice.id, deviceName: AudioDevice.name,
            kind: refs.first?.type == "episode" ? "podcast" : "music", queue: Array(refs.prefix(500)),
            index: min(start, refs.count - 1), positionSeconds: position, playing: false, shuffle: shuffle,
            repeat: repeatMode.rawValue, speed: speed, context: context)
        do {
            try await client.putNowPlaying(report)
            let device = NowPlayingDevice(deviceId: AudioDevice.id, deviceName: AudioDevice.name, kind: report.kind,
                                          queue: report.queue, index: report.index, positionSeconds: position,
                                          playing: true, shuffle: shuffle, repeat: repeatMode.rawValue, speed: speed,
                                          context: context, updatedAt: nil)
            try await takeOver(device)
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    // MARK: - Love and rating for the current track

    func toggleLoveCurrent() {
        guard var track = current?.track else { return }
        let loved = !track.isLoved
        track.loved = loved
        updateTrack(track)
        let client = client
        Task { _ = try? await client.setLoved("tracks", id: track.id, loved: loved) }
    }

    func rateCurrent(_ rating: Int) {
        guard var track = current?.track else { return }
        track.rating = rating
        updateTrack(track)
        let client = client
        Task { _ = try? await client.setRating("tracks", id: track.id, rating: rating) }
    }

    // MARK: - Audio session (iOS)

    private func activateSession(spoken: Bool = false) {
        #if os(iOS)
        let session = AVAudioSession.sharedInstance()
        let mode: AVAudioSession.Mode = (spoken || isEpisode) ? .spokenAudio : .default
        try? session.setCategory(.playback, mode: mode, policy: .longFormAudio)
        try? session.setActive(true)
        #endif
    }

    #if os(iOS)
    private func observeAudioSession() {
        let center = NotificationCenter.default
        observers.append(center.addObserver(forName: AVAudioSession.interruptionNotification, object: nil, queue: .main) { [weak self] note in
            let type = (note.userInfo?[AVAudioSessionInterruptionTypeKey] as? UInt).flatMap(AVAudioSession.InterruptionType.init(rawValue:))
            let options = (note.userInfo?[AVAudioSessionInterruptionOptionKey] as? UInt).map(AVAudioSession.InterruptionOptions.init(rawValue:))
            MainActor.assumeIsolated {
                guard let self else { return }
                if type == .began {
                    self.reportEpisodeProgress(force: true)
                } else if type == .ended, options?.contains(.shouldResume) == true, self.current != nil {
                    self.resume()
                }
            }
        })
        observers.append(center.addObserver(forName: AVAudioSession.routeChangeNotification, object: nil, queue: .main) { [weak self] note in
            let reason = (note.userInfo?[AVAudioSessionRouteChangeReasonKey] as? UInt).flatMap(AVAudioSession.RouteChangeReason.init(rawValue:))
            MainActor.assumeIsolated {
                // Headphones pulled out (or a Bluetooth speaker gone): pause rather than blare.
                if reason == .oldDeviceUnavailable { self?.pause() }
            }
        })
        observers.append(center.addObserver(forName: UIApplication.willResignActiveNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.appWillResignActive() }
        })
    }
    #endif

    // MARK: - Lock screen, Control Center, CarPlay

    private func updateNowPlayingInfo() {
        let center = MPNowPlayingInfoCenter.default()
        if let speaking {
            center.nowPlayingInfo = [
                MPMediaItemPropertyTitle: "Morning Briefing",
                MPMediaItemPropertyArtist: speaking,
                MPNowPlayingInfoPropertyPlaybackRate: 1.0,
            ]
            #if os(macOS)
            center.playbackState = .playing
            #endif
            return
        }
        guard let entry = current else {
            center.nowPlayingInfo = nil
            #if os(macOS)
            center.playbackState = .stopped
            #endif
            return
        }
        var info: [String: Any] = [
            MPMediaItemPropertyTitle: entry.title,
            MPMediaItemPropertyArtist: entry.subtitle,
            MPMediaItemPropertyPlaybackDuration: duration,
            MPNowPlayingInfoPropertyElapsedPlaybackTime: position,
            MPNowPlayingInfoPropertyPlaybackRate: isPlaying ? speed : 0.0,
            MPNowPlayingInfoPropertyDefaultPlaybackRate: speed,
            MPNowPlayingInfoPropertyPlaybackQueueIndex: index,
            MPNowPlayingInfoPropertyPlaybackQueueCount: queue.count,
            MPNowPlayingInfoPropertyMediaType: MPNowPlayingInfoMediaType.audio.rawValue,
        ]
        if let album = entry.album { info[MPMediaItemPropertyAlbumTitle] = album }
        if let track = entry.track {
            if let n = track.trackNumber { info[MPMediaItemPropertyAlbumTrackNumber] = n }
            info[MPMediaItemPropertyRating] = track.rating ?? 0
        }
        if let episode = entry.episode {
            info[MPMediaItemPropertyPodcastTitle] = episode.podcastTitle ?? ""
            if let chapter = currentChapter { info[MPNowPlayingInfoPropertyChapterNumber] = chapters.firstIndex(of: chapter) ?? 0 }
            info[MPNowPlayingInfoPropertyChapterCount] = chapters.count
        }
        if let artwork, artwork.uid == entry.uid {
            let image = artwork.image
            info[MPMediaItemPropertyArtwork] = Self.artwork(image)
        } else {
            loadArtwork(for: entry)
        }
        center.nowPlayingInfo = info
        #if os(macOS)
        center.playbackState = isPlaying ? .playing : .paused
        #endif
        updateRemoteCommands(episode: entry.isEpisode)
    }

    private func loadArtwork(for entry: AudioItem) {
        guard let url = client.audioArtURL(entry.artPath, size: 600) else { return }
        let uid = entry.uid
        Task {
            guard let image = await ArtworkLoader.platformImage(url) else { return }
            artwork = (uid, image)
            if current?.uid == uid { updateNowPlayingInfo() }
        }
    }

    @ObservationIgnored private var commandsForEpisode: Bool?

    private func updateRemoteCommands(episode: Bool) {
        let center = MPRemoteCommandCenter.shared()
        center.likeCommand.isActive = current?.track?.isLoved == true
        guard commandsForEpisode != episode else { return }
        commandsForEpisode = episode
        // Podcasts get the 15/30 skip buttons; music gets previous/next.
        center.skipForwardCommand.isEnabled = episode
        center.skipBackwardCommand.isEnabled = episode
        center.nextTrackCommand.isEnabled = !episode || queue.count > 1
        center.previousTrackCommand.isEnabled = !episode
        center.likeCommand.isEnabled = !episode
        center.ratingCommand.isEnabled = !episode
    }

    private nonisolated static func installRemoteCommands() {
        let center = MPRemoteCommandCenter.shared()
        center.playCommand.addTarget { _ in
            MainActor.assumeIsolated { AudioPlayer.shared.resume() }
            return .success
        }
        center.pauseCommand.addTarget { _ in
            MainActor.assumeIsolated { AudioPlayer.shared.pause() }
            return .success
        }
        center.togglePlayPauseCommand.addTarget { _ in
            MainActor.assumeIsolated { AudioPlayer.shared.togglePlayPause() }
            return .success
        }
        center.stopCommand.addTarget { _ in
            MainActor.assumeIsolated { AudioPlayer.shared.pause() }
            return .success
        }
        center.nextTrackCommand.addTarget { _ in
            MainActor.assumeIsolated { AudioPlayer.shared.next() }
            return .success
        }
        center.previousTrackCommand.addTarget { _ in
            MainActor.assumeIsolated { AudioPlayer.shared.previous() }
            return .success
        }
        center.skipForwardCommand.preferredIntervals = [30]
        center.skipForwardCommand.addTarget { _ in
            MainActor.assumeIsolated { AudioPlayer.shared.skip(by: 30) }
            return .success
        }
        center.skipBackwardCommand.preferredIntervals = [15]
        center.skipBackwardCommand.addTarget { _ in
            MainActor.assumeIsolated { AudioPlayer.shared.skip(by: -15) }
            return .success
        }
        center.changePlaybackPositionCommand.addTarget { event in
            guard let event = event as? MPChangePlaybackPositionCommandEvent else { return .commandFailed }
            let time = event.positionTime
            MainActor.assumeIsolated { AudioPlayer.shared.seek(to: time) }
            return .success
        }
        center.changePlaybackRateCommand.supportedPlaybackRates = [0.75, 1, 1.25, 1.5, 1.75, 2, 2.5, 3]
        center.changePlaybackRateCommand.addTarget { event in
            guard let event = event as? MPChangePlaybackRateCommandEvent else { return .commandFailed }
            let rate = Double(event.playbackRate)
            MainActor.assumeIsolated { AudioPlayer.shared.setSpeed(rate) }
            return .success
        }
        center.likeCommand.localizedTitle = "Love"
        center.likeCommand.localizedShortTitle = "Love"
        center.likeCommand.addTarget { _ in
            MainActor.assumeIsolated { AudioPlayer.shared.toggleLoveCurrent() }
            return .success
        }
        center.dislikeCommand.isEnabled = false
        center.bookmarkCommand.isEnabled = false
        center.ratingCommand.minimumRating = 0
        center.ratingCommand.maximumRating = 5
        center.ratingCommand.addTarget { event in
            guard let event = event as? MPRatingCommandEvent else { return .commandFailed }
            let rating = Int(event.rating.rounded())
            MainActor.assumeIsolated { AudioPlayer.shared.rateCurrent(rating) }
            return .success
        }
        center.changeRepeatModeCommand.addTarget { event in
            guard let event = event as? MPChangeRepeatModeCommandEvent else { return .commandFailed }
            let mode: AudioRepeat = event.repeatType == .one ? .one : event.repeatType == .all ? .all : .off
            MainActor.assumeIsolated { AudioPlayer.shared.setRepeat(mode) }
            return .success
        }
        center.changeShuffleModeCommand.addTarget { event in
            guard let event = event as? MPChangeShuffleModeCommandEvent else { return .commandFailed }
            let on = event.shuffleType != .off
            MainActor.assumeIsolated { AudioPlayer.shared.setShuffle(on) }
            return .success
        }
    }

    #if os(macOS)
    // The Mac's AirPlay picker needs the player it routes.
    var avPlayer: AVPlayer { player }
    #endif
}

// An AVPlayerItem that knows which queue entry it plays.
final class AudioPlayerItem: AVPlayerItem {
    let uid: UUID

    init(asset: AVAsset, uid: UUID) {
        self.uid = uid
        super.init(asset: asset, automaticallyLoadedAssetKeys: ["duration", "playable"])
    }
}

// PlatformImage (UIImage / NSImage) is declared in TriggerZoneEditorView.swift.

// MARK: - Speech (the Morning Briefing and speaker announcements)

@MainActor
final class AudioSpeech: NSObject, AVSpeechSynthesizerDelegate {
    static let shared = AudioSpeech()
    private let synthesizer = AVSpeechSynthesizer()
    private var continuation: CheckedContinuation<Void, Never>?

    override init() {
        super.init()
        synthesizer.delegate = self
    }

    func speak(_ text: String, volume: Float = 1) async {
        finish()
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            self.continuation = continuation
            let utterance = AVSpeechUtterance(string: text)
            utterance.volume = max(0.05, min(volume, 1))
            utterance.voice = AVSpeechSynthesisVoice(language: Locale.current.identifier.replacingOccurrences(of: "_", with: "-"))
                ?? AVSpeechSynthesisVoice(language: "en-US")
            utterance.rate = AVSpeechUtteranceDefaultSpeechRate
            utterance.postUtteranceDelay = 0.25
            synthesizer.speak(utterance)
        }
    }

    func stop() {
        synthesizer.stopSpeaking(at: .immediate)
        finish()
    }

    private func finish() {
        continuation?.resume()
        continuation = nil
    }

    nonisolated func speechSynthesizer(_ synthesizer: AVSpeechSynthesizer, didFinish utterance: AVSpeechUtterance) {
        Task { @MainActor in self.finish() }
    }

    nonisolated func speechSynthesizer(_ synthesizer: AVSpeechSynthesizer, didCancel utterance: AVSpeechUtterance) {
        Task { @MainActor in self.finish() }
    }
}
