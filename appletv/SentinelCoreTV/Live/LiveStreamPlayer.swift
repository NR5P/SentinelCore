import AVFoundation
import Combine
import Foundation

// Everything a tile needs to know about where its video comes from. Both URLs are resolved by the
// view (only it knows the camera and the rendition it wants), and both are part of the value so
// that a change of server address - the HTTPS-to-HTTP fallback does exactly that mid-session -
// restarts the stream instead of leaving it attached to an address that no longer answers.
struct LiveStreamSource: Hashable {
    let cameraId: UUID
    // nil on a server too old to have the endpoint, which sends the tile straight to HLS.
    let mse: URL?
    let hls: URL
    // Whether an audio track is worth asking for: false for a grid tile, which is muted and stays
    // muted, true for the full-screen view where there is a mute button to turn off.
    let wantsAudio: Bool
    // A camera whose video is pushed to the server by a device (a phone or Mac running this app,
    // or a browser's publish page). Its MSE stream is a transcode the server starts on demand,
    // which changes what a first-connect failure means - see mseFailed.
    var isDevicePublished: Bool = false
}

// The live ladder for one camera tile: fMP4 over the server's WebSocket first, HLS if that can't
// be made to work.
//
// Twin of ios/SentinelCore/Live/LiveStreamPlayer.swift - keep the two in sync.
//
// This is the same shape the dashboard has had since the live grid was built - try the transport
// that sits a few hundred milliseconds behind the camera, fall back to the one that sits seconds
// behind - and it is here because the Apple apps never had the first rung. They had no way to
// decode anything but HLS, so a phone was always on the slow tier while a browser on the same
// network was not. That is the whole of the "the app is laggy but the website isn't" gap, and it
// showed up worst on the PTZ camera, where you steer by a picture that is already stale.
//
// WebRTC, the dashboard's own first rung, is deliberately not here. It needs its own UDP media
// port reachable from the viewer, which is true on the LAN and usually false from a phone away
// from the house - and MSE is within a couple hundred milliseconds of it while riding the same
// authenticated HTTPS connection as every other request this app makes.
@MainActor
final class LiveStreamPlayer: ObservableObject {
    enum Transport {
        case mse(MseLiveStream)
        case hls
    }

    // How long a tier that failed before delivering any media is written off for, process-wide. A
    // server without go2rtc answers every one of these sockets with a 503, and a grid of six tiles
    // shouldn't discover that six times per visit - nor pay the connect timeout each time.
    private static let tierUnavailableTTL: TimeInterval = 60
    private static var mseUnavailableUntil = Date.distantPast

    // How many times a stream that *was* working may drop and be reconnected before the tile gives
    // up on the tier. Dropping to HLS on the first mid-stream failure was wrong for the most
    // ordinary interruption there is: a phone that has been in a pocket. iOS suspends the socket
    // while the app is backgrounded, so coming back always looked like "media stopped arriving",
    // and the tile spent the rest of its life on the transport this whole change exists to avoid.
    private static let maxMseReconnects = 3

    @Published private(set) var isVideoReady = false
    @Published private(set) var transport: Transport = .hls

    @Published var isMuted: Bool {
        didSet {
            hls.isMuted = isMuted
            mse?.isMuted = isMuted
        }
    }

    // The HLS rung's player, which LiveVideoView draws when that is the active transport.
    var player: AVPlayer { hls.player }

    private let hls: HlsLiveStream
    private var mse: MseLiveStream?
    private var source: LiveStreamSource?
    private var client: SentinelClient?
    private var hlsReadiness: AnyCancellable?
    private var reconnectTask: Task<Void, Never>?
    private var mseReconnects = 0
    private var startedAt = Date()

    init(muted: Bool) {
        isMuted = muted
        hls = HlsLiveStream(muted: muted)
        // The HLS rung owns its own readiness (first advancing frame, cleared on reconnect), so
        // while it is the active transport it is the authority on whether there is a picture.
        hlsReadiness = hls.$isVideoReady.sink { [weak self] ready in
            guard let self, case .hls = self.transport else { return }
            self.isVideoReady = ready
        }
    }

    func start(_ source: LiveStreamSource, client: SentinelClient?) {
        self.client = client

        if self.source != source {
            teardownTransports()
            self.source = source
            mseReconnects = 0
        }

        startedAt = Date()

        if let url = source.mse, Date() >= Self.mseUnavailableUntil {
            startMse(url: url, source: source)
        } else {
            startHls(source)
        }
    }

    // Keeps a tile's connection warm enough that flipping to another tab and back is cheap,
    // without leaving it consuming bandwidth for the caller's whole grace window.
    //
    // The two rungs want opposite things here. A paused AVPlayer stops fetching segments and can
    // be resumed in place, so HLS keeps its item. An MSE socket has no paused state - go2rtc keeps
    // pushing frames whether anything is drawing them or not - so it is closed, and reopened on
    // the way back. That costs a keyframe's wait, which on these cameras is under a second.
    func pause() {
        switch transport {
        case .mse:
            teardownMse()
            isVideoReady = false
        case .hls:
            hls.pause()
        }
    }

    func stop() {
        teardownTransports()
        source = nil
        isVideoReady = false
    }

    // MARK: - The ladder

    private func startMse(url: URL, source: LiveStreamSource) {
        // Only ever one rung at a time. This matters on the way *back* up: a tile that fell to HLS
        // and was then paused and resumed retries MSE, and without this the HLS player would go on
        // pulling segments underneath it for as long as the tile was open.
        hls.stop()

        if let mse {
            mse.start() // no-op when it is already running
            isVideoReady = mse.isReady
            return
        }

        let stream = MseLiveStream(
            url: url,
            wantsAudio: source.wantsAudio,
            muted: isMuted,
            onReady: { [weak self] in
                guard let self else { return }
                self.isVideoReady = true
                self.mseReconnects = 0
                self.report(tier: "mse", outcome: "playing", reason: nil, elapsedMs: self.elapsedMs())
            },
            onFailure: { [weak self] reason, elapsedMs in
                self?.mseFailed(reason: reason, elapsedMs: elapsedMs, source: source)
            })

        mse = stream
        transport = .mse(stream)
        isVideoReady = false
        stream.start()
    }

    private func mseFailed(reason: String, elapsedMs: Int, source: LiveStreamSource) {
        let deliveredMedia = isVideoReady
        teardownMse()
        isVideoReady = false

        // A stream that was working and stopped is a dropped connection - the app was backgrounded,
        // the Wi-Fi blipped, the server restarted - and the answer to that is to dial again, not to
        // spend the rest of the session seconds behind the camera.
        if deliveredMedia, mseReconnects < Self.maxMseReconnects {
            mseReconnects += 1
            report(tier: "mse", outcome: "degraded", reason: reason, elapsedMs: elapsedMs)
            scheduleMseReconnect(source)
            return
        }

        // A device-published camera's MSE stream is a transcode the server starts when the first
        // viewer asks for it, and a cold encode has nothing to emit for its first ten or twenty
        // seconds - so "no media within 10s" on the first connect is the expected warm-up, not a
        // verdict on the tier. Dial again, a few times; and never let it latch MSE off for the
        // other tiles, which have nothing to do with it. A device that isn't streaming at all still
        // ends up on HLS once the cap is spent. (Measured 2026-09-23: a phone's tile sat on
        // CONNECTING for minutes because its first attempt latched every tile onto HLS.)
        if !deliveredMedia, source.isDevicePublished, mseReconnects < Self.maxMseReconnects {
            mseReconnects += 1
            report(tier: "mse", outcome: "degraded", reason: reason, elapsedMs: elapsedMs)
            scheduleMseReconnect(source)
            return
        }

        // Failing without ever delivering a frame is otherwise the signature of a server that has
        // no go2rtc at all, so it latches the tier off for every other tile too rather than having
        // each of them discover it separately.
        if !deliveredMedia, !source.isDevicePublished {
            Self.mseUnavailableUntil = Date().addingTimeInterval(Self.tierUnavailableTTL)
        }
        report(tier: "mse", outcome: "failed", reason: reason, elapsedMs: elapsedMs)
        startHls(source)
    }

    // Backs off so a server that is down (rather than blipping) isn't dialled by every tile on the
    // grid at once. The cap above is what stops this being an endless loop in front of HLS.
    private func scheduleMseReconnect(_ source: LiveStreamSource) {
        reconnectTask?.cancel()
        let delay = min(1 << (mseReconnects - 1), 8)
        // Nothing is drawn while this runs - isVideoReady is false, so the tile is showing its
        // poster frame - which is why there is no transport to hold on to in between.
        reconnectTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard !Task.isCancelled, let self, let current = self.source, current == source else { return }
            self.reconnectTask = nil
            guard let url = current.mse else { return }
            self.startMse(url: url, source: current)
        }
    }

    private func startHls(_ source: LiveStreamSource) {
        transport = .hls
        isVideoReady = hls.isVideoReady
        hls.isMuted = isMuted
        hls.start(url: source.hls)
    }

    private func teardownTransports() {
        reconnectTask?.cancel()
        reconnectTask = nil
        teardownMse()
        hls.stop()
    }

    private func teardownMse() {
        mse?.stop()
        mse = nil
        transport = .hls
    }

    private func elapsedMs() -> Int { Int(Date().timeIntervalSince(startedAt) * 1000) }

    // Fire-and-forget: this is a log line on the server, and a tile that can't report which tier
    // it landed on still has to show video.
    private func report(tier: String, outcome: String, reason: String?, elapsedMs: Int) {
        guard let client, let cameraId = source?.cameraId else { return }
        Task { try? await client.reportLivePath(
            cameraId: cameraId, tier: tier, outcome: outcome, reason: reason, elapsedMs: elapsedMs) }
    }
}
