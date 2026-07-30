import AVFoundation
import Combine
import Foundation

// Owns the AVPlayer for one camera's live HLS feed, along with the observers that drive the
// poster-frame handoff and automatic reconnection.
//
// Twin of appletv/SentinelCoreTV/Views/LiveStreamPlayer.swift - keep the two in sync. Nothing
// here is platform-specific; both apps hit the same HLS endpoint with the same warm-up and
// reconnect needs.
//
// Why this is a class instead of the per-view @State and inline closures it replaces: the
// periodic time observer needs a definite owner. The previous version captured its own token
// inside the very closure meant to remove it, so a tile torn down before its first frame ever
// arrived leaked the observer - and through it the AVPlayer - keeping the server's ffmpeg
// session alive long after the view was gone. On a grid of camera tiles that added up.
//
// Owning the readiness flag here also fixes the frozen-poster bug the @State version had: the
// view cleared its own isVideoReady when the Live tab went inactive, but resuming within the
// teardown grace window took an early return that never re-armed the first-frame observer, so
// the flag stayed false and the poster JPEG sat on top of perfectly good video forever. pause()
// deliberately leaves isVideoReady alone - a paused player still has a frame on screen.
//
// A live feed also has no natural end. When the stream drops - server restart, camera
// unplugged, Wi-Fi blip - AVPlayer stalls and simply sits there, leaving a frozen frame under
// a "LIVE" badge that is now lying. Watching for the stall/failure notifications and
// rebuilding the item is what lets a tile recover on its own.
@MainActor
final class LiveStreamPlayer: ObservableObject {
    // Hidden until playback actually advances past zero - `readyToPlay` alone still leaves a
    // black frame on screen briefly, which is what the poster frame is there to cover.
    @Published private(set) var isVideoReady = false

    let player = AVPlayer()

    // How long the clock may sit still while the player still claims to be playing before we
    // treat it as frozen and rebuild the item. Generous enough not to fight a slow network.
    private static let frozenPlaybackTimeout: TimeInterval = 6

    private var url: URL?
    private var timeObserver: Any?
    private var itemObservers: [NSObjectProtocol] = []
    private var reconnectTask: Task<Void, Never>?
    private var reconnectAttempts = 0
    private var watchdogTask: Task<Void, Never>?
    private var lastObservedTime: Double = -1
    private var lastAdvanceAt = Date()

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
    }

    private func loadItem() {
        guard let url else { return }
        removeObservers()
        isVideoReady = false
        let item = AVPlayerItem(url: url)
        item.preferredForwardBufferDuration = 1
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
            }
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
