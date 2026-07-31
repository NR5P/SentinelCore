import AVFoundation
import Combine
import Foundation

// Owns the AVPlayer for one camera's live HLS feed, along with the observers that drive the
// poster-frame handoff and automatic reconnection.
//
// Why this is a class instead of the per-view @State and inline closures it replaces: the
// periodic time observer needs a definite owner. The previous version captured its own token
// inside the very closure meant to remove it, so a tile torn down before its first frame ever
// arrived leaked the observer - and through it the AVPlayer - keeping the server's ffmpeg
// session alive long after the view was gone. On a grid of camera tiles that added up.
//
// A live feed also has no natural end. When the stream drops - server restart, camera
// unplugged, Wi-Fi blip - AVPlayer stalls and simply sits there, leaving a frozen frame under
// a "LIVE" badge that is now lying. Watching for the stall/failure notifications and
// rebuilding the item is what lets a tile recover on its own, which matters far more on an
// Apple TV left running for hours than it does on a phone.
@MainActor
final class LiveStreamPlayer: ObservableObject {
    // Hidden until playback actually advances past zero - `readyToPlay` alone still leaves a
    // black frame on screen briefly, which is what the poster frame is there to cover.
    @Published private(set) var isVideoReady = false

    let player = AVPlayer()

    // How long the clock may sit still while the player still claims to be playing before we
    // treat it as frozen and rebuild the item. Generous enough not to fight a slow network.
    private static let frozenPlaybackTimeout: TimeInterval = 6

    // Where we aim to sit relative to the live edge, and how far behind we'll let the playhead
    // drift before hauling it back. The floor on both is the camera's keyframe interval - the
    // server can only cut an HLS segment on a keyframe while copying video, so a camera with a
    // 4s GOP produces 4s segments and no amount of client-side seeking gets closer than that.
    private static let targetOffsetFromLive: TimeInterval = 3
    private static let maxDriftFromLive: TimeInterval = 6

    private var url: URL?
    private var timeObserver: Any?
    private var itemObservers: [NSObjectProtocol] = []
    private var reconnectTask: Task<Void, Never>?
    private var reconnectAttempts = 0
    private var watchdogTask: Task<Void, Never>?
    private var lastObservedTime: Double = -1
    private var lastAdvanceAt = Date()
    private var isSeekingToLive = false

    // The stream URL isn't known at init - it depends on the AppModel the view only reaches
    // through the environment - so it arrives with the first start(url:) instead.
    init(muted: Bool) {
        player.isMuted = muted
        // Deliberately NOT setting automaticallyWaitsToMinimizeStalling = false here. It reads
        // like the right knob for "show the newest frame, don't build a cushion", and it is for
        // progressive files - but on a live HLS stream it makes AVPlayer decode exactly one
        // frame and stop, while still reporting timeControlStatus == .playing and
        // isPlaybackLikelyToKeepUp == true. The result is a frozen picture under a "LIVE" badge,
        // which is indistinguishable from a still image.
        //
        // Measured against a real HLS feed from this server, with a renderer attached:
        //   waits=false, buffer=1  -> clock advanced 0.00s / 6s, 1 frame decoded
        //   waits=false            -> clock advanced 0.00s / 6s, 1 frame decoded
        //   buffer=1               -> clock advanced 5.50s / 6s, 12 frames decoded
        //   defaults               -> clock advanced 5.50s / 6s, 12 frames decoded
        //
        // preferredForwardBufferDuration in loadItem() is the part that actually helps start
        // sooner, and it's safe on its own.
    }

    func start(url: URL) {
        if self.url != url {
            self.url = url
            loadItem()
        } else if player.currentItem == nil {
            loadItem()
        } else {
            // Resuming an item pause() kept warm. It is parked exactly where it stopped, which is
            // by now however long the tile was away behind the live edge - up to the caller's full
            // teardown grace window. A live stream has no catch-up gear; left alone the tile plays
            // that stale video forward at 1x forever, looking perfectly healthy while showing the
            // driveway as it was a minute ago.
            seekToLiveEdge()
        }
        player.play()
    }

    // Keeps the item and its connection warm so returning within the caller's grace window
    // resumes instead of paying for a full reconnect.
    func pause() {
        player.pause()
    }

    func stop() {
        reconnectTask?.cancel()
        reconnectTask = nil
        watchdogTask?.cancel()
        watchdogTask = nil
        removeObservers()
        player.pause()
        player.replaceCurrentItem(with: nil)
        isVideoReady = false
        isSeekingToLive = false
    }

    private func loadItem() {
        guard let url else { return }
        removeObservers()
        isVideoReady = false
        let item = AVPlayerItem(url: url)
        item.preferredForwardBufferDuration = 1
        // Left to itself AVFoundation holds a plain (non-low-latency) live playlist back by three
        // target durations before it will start playing, and after a stall it simply resumes from
        // wherever the stall left the playhead. Ask for a tighter offset and for the player to
        // re-establish it on its own; checkDriftFromLive below is the backstop for when it can't.
        item.automaticallyPreservesTimeOffsetFromLive = true
        item.configuredTimeOffsetFromLive = CMTime(seconds: Self.targetOffsetFromLive, preferredTimescale: 600)
        player.replaceCurrentItem(with: item)
        observe(item)
    }

    private func observe(_ item: AVPlayerItem) {
        timeObserver = player.addPeriodicTimeObserver(
            forInterval: CMTime(seconds: 0.1, preferredTimescale: 60),
            queue: .main
        ) { [weak self] time in
            // addPeriodicTimeObserver(queue: .main) documents delivery on that queue, so this
            // is already MainActor - assumeIsolated rather than hopping and dropping frames.
            MainActor.assumeIsolated {
                guard let self, !self.isVideoReady, time.seconds > 0 else { return }
                self.isVideoReady = true
                self.reconnectAttempts = 0
                self.removeTimeObserver()
            }
        }

        startFrozenPlaybackWatchdog()

        // didPlayToEndTime counts as a failure here: a live stream reaching "the end" means the
        // server stopped producing segments, not that there is nothing left to watch.
        let failureNotifications: [Notification.Name] = [
            AVPlayerItem.failedToPlayToEndTimeNotification,
            AVPlayerItem.playbackStalledNotification,
            AVPlayerItem.didPlayToEndTimeNotification
        ]
        for name in failureNotifications {
            itemObservers.append(
                NotificationCenter.default.addObserver(forName: name, object: item, queue: .main) { [weak self] _ in
                    MainActor.assumeIsolated { self?.scheduleReconnect() }
                }
            )
        }
    }

    // Catches playback that has silently stopped advancing while the player still reports
    // .playing. None of the failure notifications fire in that state, and it can't be done from
    // the periodic time observer either - that only fires *as playback time advances*, so a
    // frozen clock silences the very thing that would notice it. Hence a wall-clock timer.
    private func startFrozenPlaybackWatchdog() {
        watchdogTask?.cancel()
        lastObservedTime = -1
        lastAdvanceAt = Date()
        watchdogTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(2))
                guard !Task.isCancelled, let self else { return }
                self.checkForFrozenPlayback()
                self.checkDriftFromLive()
            }
        }
    }

    // Playback that is advancing smoothly can still be minutes stale, and nothing above notices:
    // the frozen-playback check only fires when the clock stops, and the failure notifications
    // only fire when playback breaks outright. Anything that halts the clock for a while and then
    // lets it run again - a resumed tile, a stall the player recovered from by itself, the phone's
    // radio dropping out in a pocket - leaves the playhead that far behind the live edge for good.
    // "Smooth but a minute old" is the worst failure a security feed has, because it is the one
    // that still looks right, so the watchdog treats drift as seriously as it treats a freeze.
    private func checkDriftFromLive() {
        guard player.timeControlStatus == .playing,
              secondsBehindLive() > Self.maxDriftFromLive else { return }
        seekToLiveEdge()
    }

    private func secondsBehindLive() -> TimeInterval {
        guard let live = liveEdge() else { return 0 }
        let behind = live - player.currentTime().seconds
        return behind.isFinite ? behind : 0
    }

    private func liveEdge() -> TimeInterval? {
        guard let end = player.currentItem?.seekableTimeRanges.last?.timeRangeValue.end,
              end.isValid, end.isNumeric else { return nil }
        return end.seconds
    }

    // Aims a beat short of the live edge rather than at it. The edge is the newest sample the
    // playlist mentions, which the player has not necessarily downloaded yet - landing exactly
    // there stalls immediately, and the frozen-playback watchdog then tears the item down and
    // rebuilds it, which is a far more expensive way to arrive at the same place.
    private func seekToLiveEdge() {
        guard !isSeekingToLive, let live = liveEdge() else { return }
        isSeekingToLive = true
        let target = CMTime(seconds: max(live - Self.targetOffsetFromLive, 0), preferredTimescale: 600)
        // Tolerate landing early rather than late: everything before the target is already
        // buffered, everything after it may not be.
        player.seek(to: target, toleranceBefore: CMTime(seconds: 1, preferredTimescale: 600), toleranceAfter: .zero) { [weak self] _ in
            // Documented as an arbitrary queue, unlike the periodic time observer above - hop
            // rather than assume.
            Task { @MainActor in self?.isSeekingToLive = false }
        }
    }

    private func checkForFrozenPlayback() {
        // Only meaningful while the player is actually trying to play; a deliberately paused
        // tile (see pause()) is not frozen.
        guard player.timeControlStatus == .playing else {
            lastAdvanceAt = Date()
            return
        }

        let seconds = player.currentTime().seconds
        guard seconds.isFinite else { return }

        if seconds != lastObservedTime {
            lastObservedTime = seconds
            lastAdvanceAt = Date()
        } else if Date().timeIntervalSince(lastAdvanceAt) > Self.frozenPlaybackTimeout {
            scheduleReconnect()
        }
    }

    // Backs off so a server that is down (rather than blipping) isn't hammered by every tile on
    // the grid at once, but still recovers within a few seconds in the common transient case.
    private func scheduleReconnect() {
        guard reconnectTask == nil else { return }
        isVideoReady = false
        let delaySeconds = min(1 << min(reconnectAttempts, 4), 20)
        reconnectAttempts += 1
        reconnectTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(delaySeconds))
            guard !Task.isCancelled, let self else { return }
            self.reconnectTask = nil
            self.loadItem()
            self.player.play()
        }
    }

    private func removeTimeObserver() {
        if let timeObserver { player.removeTimeObserver(timeObserver) }
        timeObserver = nil
    }

    private func removeObservers() {
        removeTimeObserver()
        for observer in itemObservers { NotificationCenter.default.removeObserver(observer) }
        itemObservers.removeAll()
    }
}
