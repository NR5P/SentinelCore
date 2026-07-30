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

    private var url: URL?
    private var timeObserver: Any?
    private var itemObservers: [NSObjectProtocol] = []
    private var reconnectTask: Task<Void, Never>?
    private var reconnectAttempts = 0

    // The stream URL isn't known at init - it depends on the AppModel the view only reaches
    // through the environment - so it arrives with the first start(url:) instead.
    init(muted: Bool) {
        player.isMuted = muted
        // Live video should show the newest frame it can rather than spend time building a
        // comfortable cushion first - the right trade for a security feed.
        player.automaticallyWaitsToMinimizeStalling = false
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
