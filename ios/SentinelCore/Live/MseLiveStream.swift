import AVFoundation
import Combine
import Foundation

// The fast rung of the live ladder on Apple platforms: go2rtc's fragmented MP4, streamed over the
// server's own authenticated WebSocket, decoded straight into an AVSampleBufferDisplayLayer.
//
// This is the same tier the dashboard calls "mse", and it exists here for the same reason it does
// there - it rides the ordinary HTTPS connection, so it works anywhere the app can talk to the
// server at all, including from outside the house where WebRTC's separate UDP media port usually
// isn't reachable. What it does *not* share with the browser is MediaSource, which iOS has never
// had; Fmp4Demuxer takes its place.
//
// Latency, measured end to end against the burned-in clock on these Reolinks: HLS sat 2.5s behind
// reality at its floor, because AVPlayer parks three TARGETDURATIONs back from the live edge and
// ffmpeg's HLS muxer has no LL-HLS parts to shorten that. Here a fragment is a single frame (go2rtc
// muxes per RTP packet, not per GOP), nothing waits for a segment to complete, and the only
// deliberate delay is `targetLatency` below.
@MainActor
final class MseLiveStream {
    // How far behind the arriving frames the render clock deliberately sits. This is the entire
    // jitter buffer: at zero, a frame that arrives one millisecond after its display time is
    // already late and gets dropped, so a feed on a busy Wi-Fi network judders continuously. A
    // sixth of a second is about four frames at 25fps - enough to absorb ordinary Wi-Fi jitter and
    // small enough that a PTZ camera still answers the joystick as if it were wired to it.
    private static let targetLatency = CMTime(value: 1, timescale: 6)

    // How far the render clock may drift from that target before it is re-anchored. Re-anchoring
    // is a visible jump, so this is wide enough that ordinary breathing doesn't trip it - but a
    // stream that has quietly fallen a second behind (a backgrounded app, a network stall the
    // player rode out) never recovers on its own, because nothing in this transport has a
    // catch-up gear. Freshness wins over smoothness on a security feed.
    private static let driftAllowance: Double = 0.5

    // No media at all within this long and the tier is written off, so the caller can fall back to
    // HLS rather than leaving a tile on "Starting stream..." forever. Generous, because the server
    // may be cold-starting go2rtc and then waiting on the camera's next keyframe.
    private static let firstFrameTimeout: TimeInterval = 10

    // Media flowed and then stopped. Shorter, because by now everything is known to work.
    private static let stallTimeout: TimeInterval = 6

    let displayLayer = AVSampleBufferDisplayLayer()

    private let synchronizer = AVSampleBufferRenderSynchronizer()
    private let url: URL
    private let wantsAudio: Bool
    private let onReady: () -> Void
    private let onFailure: (String, Int) -> Void

    private var audioRenderer: AVSampleBufferAudioRenderer?
    private var demuxer = Fmp4Demuxer()
    private var session: URLSession?
    private var socket: URLSessionWebSocketTask?
    private var receiveTask: Task<Void, Never>?
    private var watchdogTask: Task<Void, Never>?
    private var readyObserver: Any?

    private var startedAt = Date()
    private var lastSampleAt: Date?
    private var hasAnchoredClock = false
    private var isRunning = false
    private var hasFailed = false
    private(set) var isReady = false

    var isMuted: Bool {
        didSet { applyMute() }
    }

    init(
        url: URL,
        wantsAudio: Bool,
        muted: Bool,
        onReady: @escaping () -> Void,
        onFailure: @escaping (String, Int) -> Void
    ) {
        self.url = url
        self.wantsAudio = wantsAudio
        self.isMuted = muted
        self.onReady = onReady
        self.onFailure = onFailure

        displayLayer.videoGravity = .resizeAspect
        synchronizer.addRenderer(displayLayer.sampleBufferRenderer)
    }

    // MARK: - Lifecycle

    func start() {
        guard !isRunning else { return }
        isRunning = true
        hasFailed = false
        startedAt = Date()
        lastSampleAt = nil

        let configuration = URLSessionConfiguration.default
        // The server authenticates this WebSocket exactly like every other request, from the
        // session cookie the login already put in the shared storage.
        configuration.httpCookieStorage = .shared
        configuration.httpShouldSetCookies = true
        configuration.waitsForConnectivity = false
        // Deliberately longer than SentinelClient's 10s: this is the handshake budget for a socket
        // that then stays open for hours, not a request that should fail fast.
        configuration.timeoutIntervalForRequest = 20
        configuration.timeoutIntervalForResource = .infinity
        let session = URLSession(configuration: configuration)
        self.session = session

        let socket = session.webSocketTask(with: url)
        // A keyframe off a 2560x1920 camera is comfortably over the 1MB default, and exceeding it
        // fails the socket rather than truncating the message - which looks exactly like the
        // server refusing the connection.
        socket.maximumMessageSize = 16 * 1024 * 1024
        self.socket = socket
        socket.resume()

        sendHandshake()
        receiveTask = Task { [weak self] in await self?.receiveLoop(on: socket) }
        startWatchdog()
        applyMute()
    }

    func stop() {
        isRunning = false
        receiveTask?.cancel()
        receiveTask = nil
        watchdogTask?.cancel()
        watchdogTask = nil
        if let readyObserver {
            synchronizer.removeTimeObserver(readyObserver)
            self.readyObserver = nil
        }
        socket?.cancel(with: .goingAway, reason: nil)
        socket = nil
        session?.invalidateAndCancel()
        session = nil
        synchronizer.rate = 0
        detachAudioRenderer()
        displayLayer.sampleBufferRenderer.flush()
        demuxer = Fmp4Demuxer()
        hasAnchoredClock = false
        isReady = false
    }

    // The codecs this app can decode from fMP4, in the same shape the dashboard offers. go2rtc
    // picks from this list and answers with what it will actually send.
    //
    // Audio is offered only where it can be heard. A grid tile is muted and stays muted, and
    // asking for an audio track there would have go2rtc send one down a link whose bandwidth is
    // the entire reason the tile is on the low rendition in the first place.
    private func sendHandshake() {
        var codecs = [
            "avc1.640033", "avc1.64002A", "avc1.640029", "avc1.4D4029", "avc1.42E01F",
            "hvc1.1.6.L153.B0"
        ]
        if wantsAudio { codecs.append("mp4a.40.2") }
        let message = "{\"type\":\"mse\",\"value\":\"\(codecs.joined(separator: ","))\"}"

        socket?.send(.string(message)) { [weak self] error in
            guard let error else { return }
            Task { @MainActor in
                self?.fail("the MSE handshake could not be sent: \(error.localizedDescription)")
            }
        }
    }

    // MARK: - Receiving

    private func receiveLoop(on socket: URLSessionWebSocketTask) async {
        while !Task.isCancelled {
            do {
                let message = try await socket.receive()
                guard !Task.isCancelled else { return }
                switch message {
                case .data(let data):
                    handle(data)
                case .string:
                    // go2rtc's codec answer. Nothing to do with it: the init segment that follows
                    // describes the tracks precisely, and that is what the demuxer reads.
                    break
                @unknown default:
                    break
                }
            } catch {
                guard !Task.isCancelled else { return }
                fail(lastSampleAt == nil
                    ? "the MSE WebSocket closed before any media (\(error.localizedDescription))"
                    : "the MSE WebSocket closed mid-stream (\(error.localizedDescription))")
                return
            }
        }
    }

    private func handle(_ data: Data) {
        let samples: [Fmp4Sample]
        do {
            samples = try demuxer.append(data)
        } catch {
            fail((error as? LocalizedError)?.errorDescription ?? "the fMP4 stream could not be read")
            return
        }

        for sample in samples {
            switch sample.kind {
            case .video: enqueueVideo(sample.buffer)
            case .audio: audioRenderer?.enqueue(sample.buffer)
            }
        }
    }

    private func enqueueVideo(_ buffer: CMSampleBuffer) {
        lastSampleAt = Date()

        let renderer = displayLayer.sampleBufferRenderer
        // A decode failure leaves the renderer permanently wedged, so it is flushed and re-anchored
        // rather than left showing a frozen picture under a LIVE badge.
        if renderer.status == .failed {
            renderer.flush()
            hasAnchoredClock = false
        }

        let presentationTime = CMSampleBufferGetPresentationTimeStamp(buffer)
        renderer.enqueue(buffer)

        if hasAnchoredClock {
            correctDrift(against: presentationTime)
        } else {
            hasAnchoredClock = true
            anchorClock(to: presentationTime)
            watchForFirstFrame(at: presentationTime)
        }
    }

    // MARK: - Clock

    // The render clock is set behind the newest frame by exactly `targetLatency`, which is what
    // makes that constant the whole jitter buffer.
    private func anchorClock(to presentationTime: CMTime) {
        synchronizer.setRate(1, time: presentationTime - Self.targetLatency)
    }

    private func correctDrift(against presentationTime: CMTime) {
        let target = presentationTime - Self.targetLatency
        let drift = (target - synchronizer.currentTime()).seconds
        guard drift.isFinite, abs(drift) > Self.driftAllowance else { return }

        // Both directions matter. Ahead of the target means the tile has fallen behind live and
        // will stay there; behind it means the clock has run past the arriving frames and every one
        // of them is being dropped for arriving late. A re-anchor fixes either, and a camera whose
        // timeline restarts (a reconnect upstream, a codec change) lands here too.
        anchorClock(to: presentationTime)
    }

    // isVideoReady drives the poster-frame handoff, and a poster that lifts before there is a
    // picture underneath shows a black rectangle. The clock reaching the first frame's presentation
    // time is the moment that frame is on screen.
    private func watchForFirstFrame(at presentationTime: CMTime) {
        guard readyObserver == nil else { return }
        readyObserver = synchronizer.addPeriodicTimeObserver(
            forInterval: CMTime(value: 1, timescale: 20), queue: .main
        ) { [weak self] time in
            MainActor.assumeIsolated {
                guard let self, !self.isReady, time >= presentationTime else { return }
                self.isReady = true
                if let readyObserver = self.readyObserver {
                    self.synchronizer.removeTimeObserver(readyObserver)
                    self.readyObserver = nil
                }
                self.onReady()
            }
        }
    }

    // MARK: - Audio

    private func applyMute() {
        if isMuted || !wantsAudio {
            detachAudioRenderer()
        } else {
            attachAudioRenderer()
        }
    }

    // Attached on unmute rather than at connect. An audio renderer that never gets a sample - a
    // camera with no audio, or one whose track the demuxer couldn't describe - is a renderer the
    // synchronizer can wait on, and the cost of that would land on the picture.
    private func attachAudioRenderer() {
        guard isRunning, audioRenderer == nil else { return }
        let renderer = AVSampleBufferAudioRenderer()
        audioRenderer = renderer
        synchronizer.addRenderer(renderer)
    }

    private func detachAudioRenderer() {
        guard let audioRenderer else { return }
        audioRenderer.flush()
        synchronizer.removeRenderer(audioRenderer, at: .invalid) { _ in }
        self.audioRenderer = nil
    }

    // MARK: - Watchdog

    // One loop covers both "never arrived" and "stopped arriving". Neither is recoverable inside
    // this transport - the socket is either dead or the server stopped producing - so both hand the
    // tile back to the caller, which drops it to HLS.
    private func startWatchdog() {
        watchdogTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(1))
                guard !Task.isCancelled, let self, self.isRunning else { return }

                if let lastSampleAt = self.lastSampleAt {
                    if Date().timeIntervalSince(lastSampleAt) > Self.stallTimeout {
                        self.fail("media stopped arriving for \(Int(Self.stallTimeout))s")
                        return
                    }
                } else if Date().timeIntervalSince(self.startedAt) > Self.firstFrameTimeout {
                    self.fail("no media within \(Int(Self.firstFrameTimeout))s")
                    return
                }
            }
        }
    }

    private func fail(_ reason: String) {
        guard isRunning, !hasFailed else { return }
        hasFailed = true
        let elapsed = Int(Date().timeIntervalSince(startedAt) * 1000)
        stop()
        onFailure(reason, elapsed)
    }
}
