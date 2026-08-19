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

    // Mirrors the player's own mute state so a view can bind to it. AVPlayer.isMuted isn't
    // observable from SwiftUI, and on macOS this is the only way to silence a feed - the live
    // screens there draw the video themselves and have no AVKit volume control (see LiveVideoView).
    @Published var isMuted: Bool {
        didSet { player.isMuted = isMuted }
    }

    let player = AVPlayer()

    // How long the clock may sit still while the player still claims to be playing before we
    // treat it as frozen and rebuild the item. Generous enough not to fight a slow network.
    private static let frozenPlaybackTimeout: TimeInterval = 6

    // Fallback for how far behind the live edge to sit, used only until the stream tells us its
    // own recommendation - see targetOffsetFromLive(). One segment on a 2s-GOP camera, which is
    // the shortest cushion a segmented transport can actually hold.
    private static let fallbackOffsetFromLive: TimeInterval = 2.5

    // How much further behind than the target we tolerate before hauling the playhead back.
    // Seeking is itself a rebuffer, so this wants to be wide enough that only real staleness
    // trips it, not the ordinary breathing room a live player keeps. Sized against the offset
    // itself now rather than the old flat six seconds, which on a tightened offset would have
    // let a tile sit three times its target behind before anything noticed.
    private static let driftAllowance: TimeInterval = 2.5

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
        isMuted = muted
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
        // The buffer=1 row was once read as "safe, and starts sooner". It does start sooner, and
        // it is not safe: see loadItem(), where a one-second forward buffer turned out to be less
        // than one segment of these streams and left every tile rebuffering on a loop.
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
        // Deliberately left at 0 (automatic). This used to be 1, to start sooner, and that was the
        // single biggest cause of tiles rebuffering every few seconds: one second of forward
        // buffer is less than one segment of a stream whose segments are the camera's GOP - 2s,
        // often 4s - so the player was structurally unable to hold even one segment ahead and ran
        // dry on a loop. Automatic lets AVFoundation size the buffer against the target duration
        // it actually parsed out of the playlist.
        item.preferredForwardBufferDuration = 0
        // configuredTimeOffsetFromLive is not set here, because the value worth setting isn't
        // known yet - it is derived from the stream's own recommendation once the playlist has
        // been parsed. See tightenLiveOffset, which does it at the first advancing frame.
        //
        // It used to be pinned to a hardcoded 3s here, and that was wrong for a reason worth
        // keeping: 3s is closer to the live edge than a single segment on a camera with a 4s
        // keyframe interval, and no amount of buffering can sustain an offset smaller than the
        // quantum the stream arrives in. Anything that sets this has to be expressed relative to
        // the stream's own timing rather than as an absolute number of seconds.
        //
        // automaticallyPreservesTimeOffsetFromLive keeps whatever offset we end up at across a
        // stall rather than letting each one permanently push the tile further behind;
        // checkDriftFromLive below is the backstop for when the player can't re-establish it.
        item.automaticallyPreservesTimeOffsetFromLive = true
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
                self.tightenLiveOffset(on: item)
                self.removeTimeObserver()
            }
        }

        startFrozenPlaybackWatchdog()

        // didPlayToEndTime counts as a failure here: a live stream reaching "the end" means the
        // server stopped producing segments, not that there is nothing left to watch.
        let failureNotifications: [Notification.Name] = [
            AVPlayerItem.failedToPlayToEndTimeNotification,
            AVPlayerItem.didPlayToEndTimeNotification
        ]
        for name in failureNotifications {
            itemObservers.append(
                NotificationCenter.default.addObserver(forName: name, object: item, queue: .main) { [weak self] _ in
                    MainActor.assumeIsolated { self?.scheduleReconnect() }
                }
            )
        }

        // playbackStalled is pointedly NOT in that list any more. It fires on any buffer underrun,
        // including the routine one-second kind a live stream has on a busy network, and treating
        // each as a failure meant tearing down a working AVPlayerItem and paying a full HLS
        // reconnect - which is precisely the "buffers, then starts again" the tiles were doing. A
        // stall is a reason to make sure we are still trying, nothing more; if the clock really
        // has stopped, the watchdog rebuilds the item a few seconds later.
        itemObservers.append(
            NotificationCenter.default.addObserver(
                forName: AVPlayerItem.playbackStalledNotification, object: item, queue: .main
            ) { [weak self] _ in
                MainActor.assumeIsolated { self?.player.play() }
            }
        )
    }

    // How close to the live edge to ask to sit, as a fraction of what the stream itself
    // recommends. AVFoundation's recommendation for a plain (non-low-latency) playlist is three
    // target durations, which on a camera with a 2s keyframe interval is a six-second delay -
    // and that delay is the whole reason driving a PTZ camera from this app is unpleasant: you
    // nudge, wait, and overshoot because the picture you are steering by is six seconds old.
    //
    // How many segments of cushion to keep, expressed in segments because that is the quantum
    // this transport actually arrives in. AVFoundation's recommendation for a plain live playlist
    // is three target durations; on these 2s-GOP cameras that is a six-second-old picture, and
    // six seconds is very nearly the whole of the PTZ lag. Measured end to end on 2026-08-19
    // against the camera's own burned-in clock: the picture in the app was 7.4s behind reality,
    // of which only ~1.0s was the server (camera -> RTSP -> ffmpeg -> completed segment on disk).
    // Everything else was this offset.
    //
    // One segment is deliberately the floor of what is sane rather than a comfortable margin: it
    // is the point where any hiccup becomes a visible rebuffer, which is why the drift watchdog
    // and the reconnect path below matter more at this setting than they did at three.
    private static let liveOffsetSegments = 1.0

    // Never ask to sit closer to live than this regardless of what the segment maths works out
    // to. Below this the answer is not a smaller number here but a different transport (WebRTC),
    // which the dashboard uses and this player has no access to.
    private static let minimumLiveOffsetSeconds = 1.5

    // Asks the player to sit closer to the live edge than AVFoundation would by default. Done
    // here rather than in loadItem because recommendedTimeOffsetFromLive is only meaningful once
    // the playlist has been parsed, and the first advancing frame is proof that it has been.
    //
    // Everything is derived from the stream's own recommendation, so a camera with a long
    // keyframe interval is still given room proportional to how coarsely its video arrives -
    // this cannot reintroduce the hardcoded-3s failure described in loadItem.
    private func tightenLiveOffset(on item: AVPlayerItem) {
        let recommended = item.recommendedTimeOffsetFromLive
        // A stream that recommends nothing usable (not yet parsed, or an indefinite/invalid time)
        // gets left entirely alone - the default behaviour is the safe one.
        guard recommended.isNumeric, recommended.seconds > 0 else { return }

        let tightened = Self.desiredOffset(forRecommended: recommended.seconds)

        // Only ever move closer to live. On a stream whose recommendation is already at or inside
        // the floor - a low-latency playlist, or a camera with a very short GOP - this would
        // otherwise push the picture further behind than it needs to be.
        guard tightened < recommended.seconds else { return }
        item.configuredTimeOffsetFromLive = CMTime(seconds: tightened, preferredTimescale: 600)
    }

    // The one place the target offset is decided, so the value the player is asked to hold and
    // the value the drift watchdog measures against can never disagree. They used to: the offset
    // was tightened to two thirds of the recommendation while the watchdog went on comparing
    // against the full recommendation, so the watchdog's idea of "correct" was the very position
    // the tightening existed to move away from - and seekToLiveEdge would put it back there.
    //
    // The recommendation is three target durations, so dividing by three recovers the segment
    // length the stream is actually arriving in without having to parse the playlist.
    private static func desiredOffset(forRecommended recommended: TimeInterval) -> TimeInterval {
        let segment = recommended / 3.0
        return max(segment * liveOffsetSegments, minimumLiveOffsetSeconds)
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
              secondsBehindLive() > targetOffsetFromLive() + Self.driftAllowance else { return }
        seekToLiveEdge()
    }

    // Where this player intends to sit: derived from the stream's own recommendation, so it
    // tracks the camera's actual segment length instead of a number picked here.
    // Comparing drift against a hardcoded 3s target - as this used to - meant that on any camera
    // with a GOP of 2s or more the player's own healthy resting position already counted as
    // "drifted", so the watchdog seeked to the live edge every two seconds, forever. Each of those
    // seeks is a rebuffer, which made this watchdog a cause of the stuttering rather than a cure.
    private func targetOffsetFromLive() -> TimeInterval {
        guard let recommended = player.currentItem?.recommendedTimeOffsetFromLive,
              recommended.isValid, recommended.isNumeric, recommended.seconds > 0 else {
            return Self.fallbackOffsetFromLive
        }
        return Self.desiredOffset(forRecommended: recommended.seconds)
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
        let target = CMTime(seconds: max(live - targetOffsetFromLive(), 0), preferredTimescale: 600)
        // Tolerate landing early rather than late: everything before the target is already
        // buffered, everything after it may not be.
        player.seek(to: target, toleranceBefore: CMTime(seconds: 1, preferredTimescale: 600), toleranceAfter: .zero) { [weak self] _ in
            // Documented as an arbitrary queue, unlike the periodic time observer above - hop
            // rather than assume.
            Task { @MainActor in self?.isSeekingToLive = false }
        }
    }

    private func checkForFrozenPlayback() {
        // Only meaningful while the player is trying to play; a deliberately paused tile (see
        // pause()) is not frozen.
        //
        // waitingToPlayAtSpecifiedRate counts as trying. That is the state a player sits in while
        // it waits for data it is never going to get, and it is now the only thing watching for
        // that: a stall no longer rebuilds the item on sight (see observe(_:)), so if this checked
        // only for .playing, a stream that stalled and stayed stalled would wait forever with the
        // watchdog resetting its own timer every two seconds.
        switch player.timeControlStatus {
        case .playing, .waitingToPlayAtSpecifiedRate:
            break
        case .paused:
            lastAdvanceAt = Date()
            return
        @unknown default:
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
