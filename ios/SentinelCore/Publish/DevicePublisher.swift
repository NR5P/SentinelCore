import AVFoundation
import Combine
import Foundation
import WebRTC
#if os(iOS)
import UIKit
#elseif os(macOS)
import AppKit
#endif

// Turns the device this app runs on into one of the dashboard's cameras: capture its camera and
// push the video to the server over WebRTC, exactly what the web dashboard's publish page
// (wwwroot/publisher.js) does from a browser. Same signaling endpoint, same session cookie, same
// go2rtc sidecar on the other end - so from the server's point of view a phone running this and a
// laptop running the browser page are the same kind of camera, and clips, trigger zones and AI
// analysis all work without knowing which it was.
//
// One instance for the whole app, not one per screen. Streaming has to outlive the screen that
// started it: the person who props a phone up in a room and presses start may well flick back to
// the Live tab to check the tile before they leave, and closing the screen must not end the
// stream. It also has to survive the app being relaunched - the intent is remembered, and
// RootView asks for it to be picked back up as soon as the server is reachable again.
//
// The awkward part, as in the browser, is running unattended: keeping the device awake as far as
// the platform allows (see KeepAwake), reconnecting after a network blip, a sleep, or on iOS a
// trip to the background - and telling the truth about whether the server is actually receiving
// frames, which a "connected" peer connection does not establish on its own.
@MainActor
final class DevicePublisher: NSObject, ObservableObject {
    static let shared = DevicePublisher()

    // The dashboard camera this device streams into. Only the id and name are kept: the name is
    // for the screen, the id is what the signaling endpoint is keyed by.
    struct Target: Codable, Equatable, Sendable {
        let id: UUID
        let name: String
    }

    // A camera on this device, by AVCaptureDevice.uniqueID - stable across launches, which is what
    // lets the remembered intent name the same camera next time.
    struct CaptureDevice: Identifiable, Hashable, Sendable {
        let id: String
        let name: String
    }

    enum Phase: Equatable, Sendable {
        case idle
        case previewing
        case starting
        case connecting
        case connected
        case streaming      // the server has confirmed frames are arriving
        case reconnecting
        case paused         // iOS, in the background: the OS has taken the camera away
    }

    @Published private(set) var phase: Phase = .idle
    @Published private(set) var target: Target?
    @Published private(set) var deviceId = ""
    @Published private(set) var includeMic = false
    @Published private(set) var status = ""
    @Published private(set) var errorMessage: String?
    @Published private(set) var devices: [CaptureDevice] = []
    // Whatever the camera is currently producing - shown by PublisherPreviewView, streaming or not.
    @Published private(set) var previewTrack: RTCVideoTrack?

    // "This device is meant to be streaming", regardless of whether the connection is up at this
    // instant. What the Live toolbar badge and the auto-resume both key on.
    var isPublishing: Bool { target != nil }

    private var client: SentinelClient?
    private var factory: RTCPeerConnectionFactory?
    private var videoSource: RTCVideoSource?
    private var videoTrack: RTCVideoTrack?
    private var audioTrack: RTCAudioTrack?
    private var capturer: RTCCameraVideoCapturer?
    private var captureRunning = false
    private var peer: RTCPeerConnection?
    private var observer: PeerConnectionObserver?

    private var statusTask: Task<Void, Never>?
    private var retryTask: Task<Void, Never>?
    private var heartbeatTask: Task<Void, Never>?
    private var lifecycleObservers: [NSObjectProtocol] = []

    // Bumped every time a session starts or stops, so a callback from a session that has since
    // been torn down (a slow SDP exchange, a late retry) can tell it is stale and do nothing.
    private var generation = 0

    private static let intentKey = "sentinelcore.publisher.intent"
    // Which dashboard camera this device last streamed to - kept across a stop, unlike the
    // intent, so the picker offers the same camera next time rather than creating another.
    private static let lastTargetKey = "sentinelcore.publisher.lastTarget"
    private static let statusPollInterval: Duration = .seconds(10)
    private static let retryDelay: Duration = .seconds(3)
    // A device that slept froze this loop; on waking, the gap between two ticks is however long
    // it slept, while the peer connection may never announce that it died.
    private static let heartbeatInterval: Duration = .seconds(15)
    private static let suspendGap: TimeInterval = 3 * 60

    private override init() {
        super.init()
        observeLifecycle()
    }

    // ---------------- Devices ----------------

    // Every camera this device has, by name. On a Mac that includes a plugged-in webcam and an
    // iPhone offered through Continuity Camera; on an iPhone, the front and back cameras.
    func refreshDevices() {
        devices = RTCCameraVideoCapturer.captureDevices().map {
            CaptureDevice(id: $0.uniqueID, name: $0.localizedName)
        }
    }

    // The camera to use when nobody has chosen one. A phone left in a room is propped up with its
    // screen facing the room's owner and its back to the room, so the back camera is what "the
    // camera" means there; a Mac has a built-in one, and a plugged-in one only by choice.
    func defaultDeviceId() -> String {
        let all = RTCCameraVideoCapturer.captureDevices()
        #if os(iOS)
        if let back = all.first(where: { $0.position == .back }) { return back.uniqueID }
        #endif
        return all.first?.uniqueID ?? ""
    }

    // ---------------- Preview ----------------

    // Shows the chosen camera without sending it anywhere. This is what asks for camera
    // permission, and what the screen renders while the person decides which camera and whether
    // to start - the browser page does the same, for the same reason: a page that shows nothing
    // until "start" reads as a page that does nothing.
    func preview(deviceId requested: String) async {
        if isPublishing { return }
        errorMessage = nil
        status = "opening camera…"
        do {
            try await ensureCapture(deviceId: requested.isEmpty ? defaultDeviceId() : requested)
            phase = .previewing
            status = "previewing - nothing is being sent yet"
        } catch {
            phase = .idle
            status = ""
            errorMessage = error.localizedDescription
        }
    }

    // Releases the camera when the screen goes away without streaming. While streaming the
    // capture is the stream and stays.
    func endPreview() {
        if isPublishing { return }
        stopCapture()
        phase = .idle
        status = ""
    }

    // ---------------- Publishing ----------------

    func start(client: SentinelClient, target: Target, deviceId requested: String, includeMic wantsMic: Bool) async {
        retryTask?.cancel()
        statusTask?.cancel()
        closePeer()
        generation += 1
        let mine = generation

        self.client = client
        self.target = target
        self.includeMic = wantsMic
        errorMessage = nil
        phase = .starting
        status = "requesting camera…"

        do {
            try await ensureCapture(deviceId: requested.isEmpty ? defaultDeviceId() : requested)
            if wantsMic { try await ensureMicrophone() } else { audioTrack = nil }
        } catch {
            guard mine == generation else { return }
            self.target = nil
            phase = captureRunning ? .previewing : .idle
            status = ""
            errorMessage = error.localizedDescription
            return
        }
        guard mine == generation else { return }

        rememberIntent()
        UserDefaults.standard.set(target.id.uuidString, forKey: Self.lastTargetKey)
        KeepAwake.hold()
        startHeartbeat()
        await connect(mine)
    }

    var lastTargetId: UUID? {
        UserDefaults.standard.string(forKey: Self.lastTargetKey).flatMap(UUID.init(uuidString:))
    }

    // Ends streaming. A deliberate stop forgets the remembered intent - that is the one way the
    // app stops trying to be a camera on its next launch. The preview stays up so the screen still
    // shows the camera, and endPreview releases it when the screen goes.
    func stop() {
        generation += 1
        forgetIntent()
        retryTask?.cancel()
        statusTask?.cancel()
        heartbeatTask?.cancel()
        closePeer()
        audioTrack = nil
        target = nil
        KeepAwake.release()
        errorMessage = nil
        phase = captureRunning ? .previewing : .idle
        status = captureRunning ? "stopped - previewing only, nothing is being sent" : ""
    }

    private func connect(_ mine: Int) async {
        guard let client, let target, let factory = ensureFactory(), let videoTrack else { return }
        phase = phase == .reconnecting ? .reconnecting : .connecting
        if phase != .reconnecting { status = "connecting…" }

        let configuration = RTCConfiguration()
        configuration.sdpSemantics = .unifiedPlan
        // No STUN, like the browser page: the server is on the LAN and go2rtc answers with its
        // own host candidates. A relay address would need the server to offer a TURN server too.
        let constraints = RTCMediaConstraints(mandatoryConstraints: nil, optionalConstraints: nil)
        let observer = PeerConnectionObserver(
            onConnectionState: { [weak self] state in
                Task { @MainActor in self?.connectionStateChanged(state, generation: mine) }
            },
            onDiagnostic: { [weak self] line in
                Task { @MainActor in self?.client?.log("Device camera: \(line)") }
            })
        guard let peer = factory.peerConnection(with: configuration, constraints: constraints, delegate: observer) else {
            fail("Couldn't create a connection on this device.", mine)
            return
        }
        self.peer = peer
        self.observer = observer

        // A source, and only a source: saying so keeps the offer from asking for anything back.
        let videoSender = peer.add(videoTrack, streamIds: ["sentinelcore"])
        if let audioTrack { peer.add(audioTrack, streamIds: ["sentinelcore"]) }
        Self.applyVideoBitrate(to: videoSender)
        for transceiver in peer.transceivers {
            transceiver.setDirection(.sendOnly, error: nil)
        }

        do {
            let offer = try await peer.offer(for: constraints)
            try await peer.setLocalDescription(offer)
            await waitForIceGathering(peer, timeout: .milliseconds(1500))
            guard mine == generation, let local = peer.localDescription else { return }

            let answer = try await client.publishOffer(cameraId: target.id, sdp: local.sdp)
            guard mine == generation else { return }
            try await peer.setRemoteDescription(RTCSessionDescription(type: .answer, sdp: answer))
        } catch {
            guard mine == generation else { return }
            // The camera is gone from the server - deleted on the dashboard, or this app was
            // pointed at a different server. Retrying would loop on 404 forever; the honest
            // answer is to stop and say so, and to forget the intent so the next launch doesn't
            // start the loop again.
            if case let ClientError.server(status, _) = error, status == 404 {
                let name = target.name
                stop()
                errorMessage = "\"\(name)\" no longer exists on this server. Pick another camera, or make a new one."
                return
            }
            fail("Couldn't start streaming: \(Self.describe(error))", mine)
            return
        }

        guard mine == generation else { return }
        phase = .connected
        status = "connected - waiting for the server to confirm it's receiving video…"
        pollStatus(mine)
    }

    private func connectionStateChanged(_ state: RTCPeerConnectionState, generation mine: Int) {
        guard mine == generation, isPublishing else { return }
        switch state {
        case .failed, .disconnected:
            phase = .reconnecting
            status = "connection lost - reconnecting…"
            scheduleRetry(mine)
        case .connected:
            if phase == .connecting || phase == .reconnecting {
                phase = .connected
                status = "connected - waiting for the server to confirm it's receiving video…"
            }
        default:
            break
        }
    }

    private func fail(_ message: String, _ mine: Int) {
        errorMessage = message
        phase = .reconnecting
        status = "will retry in a few seconds…"
        scheduleRetry(mine)
    }

    // A peer connection reporting "connected" only means the handshake completed. Asking the
    // server what it can actually see is the only honest confirmation, and it is what makes a
    // streaming phone trustworthy to walk away from.
    private func pollStatus(_ mine: Int) {
        statusTask?.cancel()
        statusTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let self, mine == self.generation, let client = self.client, let target = self.target else { return }
                self.logOutboundStats()
                if let result = try? await client.publishStatus(cameraId: target.id), mine == self.generation, self.isPublishing {
                    if result.publishing {
                        self.phase = .streaming
                        self.status = "streaming - the server is receiving video from this device"
                        self.errorMessage = nil
                    } else if self.phase == .streaming || self.phase == .connected {
                        self.phase = .connected
                        self.status = "connected, but the server isn't receiving video yet…"
                    }
                }
                try? await Task.sleep(for: Self.statusPollInterval)
            }
        }
    }

    // What the encoder and the RTP sender are actually doing, for the diagnostics log: frames
    // and keyframes encoded, keyframe requests honoured, bytes sent. "Connected but the server
    // sees no video" is only diagnosable from these.
    private func logOutboundStats() {
        guard let peer, let client else { return }
        peer.statistics { report in
            var lines: [String] = []
            for (_, stat) in report.statistics where stat.type == "outbound-rtp" || stat.type == "codec" {
                let keys = ["kind", "framesEncoded", "keyFramesEncoded", "pliCount", "firCount", "nackCount", "bytesSent", "packetsSent", "frameWidth", "frameHeight", "mimeType", "sdpFmtpLine", "encoderImplementation", "qualityLimitationReason"]
                let values = keys.compactMap { key in stat.values[key].map { "\(key)=\($0)" } }
                if !values.isEmpty { lines.append("\(stat.type): " + values.joined(separator: " ")) }
            }
            let summary = lines.joined(separator: " | ")
            Task { @MainActor in client.log("Device camera stats: \(summary)") }
        }
    }

    private func scheduleRetry(_ mine: Int) {
        guard mine == generation, isPublishing else { return }
        retryTask?.cancel()
        retryTask = Task { [weak self] in
            try? await Task.sleep(for: Self.retryDelay)
            guard let self, !Task.isCancelled, mine == self.generation, self.isPublishing else { return }
            await self.reconnect(restartCapture: false)
        }
    }

    // A fresh connection, keeping the target. After a sleep or a spell in the background the
    // capture itself may have been taken away, so it is restarted too when asked.
    private func reconnect(restartCapture: Bool) async {
        guard let client, let target else { return }
        let wantsMic = includeMic
        let device = deviceId
        if restartCapture { stopCapture() }
        // Not cancelled: this very often *is* the retry task, and cancelling it here made the
        // URLSession call below throw CancellationError - so no reconnect ever finished. A pending
        // retry that isn't the caller is left alone; the generation bump makes it a no-op.
        retryTask = nil
        statusTask?.cancel()
        closePeer()
        generation += 1
        let mine = generation
        phase = .reconnecting
        do {
            try await ensureCapture(deviceId: device)
            if wantsMic { try await ensureMicrophone() }
        } catch {
            guard mine == generation else { return }
            fail("Couldn't reopen the camera: \(Self.describe(error))", mine)
            return
        }
        guard mine == generation else { return }
        _ = client
        _ = target
        await connect(mine)
    }

    // ---------------- Capture ----------------

    private func ensureFactory() -> RTCPeerConnectionFactory? {
        if let factory { return factory }
        RTCInitializeSSL()
        // H.264 only, deliberately. Everything downstream of go2rtc that writes a clip does so
        // with `-c copy` into MP4, and VP8 or VP9 in that container is not a thing - so an offer
        // that let the server pick another codec would stream fine and record nothing. Every
        // Apple device has a hardware H.264 encoder, so nothing is lost by insisting.
        let created = RTCPeerConnectionFactory(
            encoderFactory: RTCVideoEncoderFactoryH264(),
            decoderFactory: RTCDefaultVideoDecoderFactory())
        factory = created
        return created
    }

    private func ensureCapture(deviceId requested: String) async throws {
        guard let factory = ensureFactory() else { throw PublishError.noFactory }

        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized:
            break
        case .notDetermined:
            guard await AVCaptureDevice.requestAccess(for: .video) else { throw PublishError.cameraDenied }
        default:
            throw PublishError.cameraDenied
        }

        refreshDevices()
        let all = RTCCameraVideoCapturer.captureDevices()
        guard let device = all.first(where: { $0.uniqueID == requested }) ?? all.first else {
            throw PublishError.noCamera
        }

        if captureRunning, device.uniqueID == deviceId { return }
        stopCapture()

        if videoSource == nil {
            let source = factory.videoSource()
            videoSource = source
            videoTrack = factory.videoTrack(with: source, trackId: "sentinelcore-video")
        }
        guard let videoSource, let videoTrack else { throw PublishError.noFactory }

        let capturer = RTCCameraVideoCapturer(delegate: videoSource)
        let (format, fps) = Self.chooseFormat(for: device)
        do {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                capturer.startCapture(with: device, format: format, fps: fps) { error in
                    if let error { continuation.resume(throwing: error) } else { continuation.resume() }
                }
            }
        } catch {
            // A capturer whose start failed still believes it is running, and asserts (fatally)
            // if it is released in that state - which is exactly what letting it go out of scope
            // here did. Stopping it first is what makes a busy camera an error instead of a crash.
            await capturer.stopCapture()
            throw PublishError.cameraBusy(error.localizedDescription)
        }
        self.capturer = capturer
        captureRunning = true
        deviceId = device.uniqueID
        previewTrack = videoTrack
    }

    private func stopCapture() {
        capturer?.stopCapture()
        capturer = nil
        captureRunning = false
        previewTrack = nil
    }

    private func ensureMicrophone() async throws {
        if audioTrack != nil { return }
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized:
            break
        case .notDetermined:
            guard await AVCaptureDevice.requestAccess(for: .audio) else { throw PublishError.microphoneDenied }
        default:
            throw PublishError.microphoneDenied
        }
        guard let factory = ensureFactory() else { throw PublishError.noFactory }
        let source = factory.audioSource(with: RTCMediaConstraints(mandatoryConstraints: Self.audioConstraints, optionalConstraints: nil))
        audioTrack = factory.audioTrack(with: source, trackId: "sentinelcore-audio")
    }

    // WebRTC's audio processing is tuned for a call, and a camera is not a call: nothing plays
    // out of this device, so the echo canceller has nothing to cancel and instead eats any sound
    // that resembles what the device last played; and noise suppression flattens exactly the
    // faint room sounds a security recording is for. Gain control stays - a voice across the
    // room should still be audible.
    private static let audioConstraints = [
        "googEchoCancellation": "false",
        "googNoiseSuppression": "false",
        "googHighpassFilter": "true",
        "googAutoGainControl": "true"
    ]

    // 720p at up to 30fps, or the nearest the camera offers below it. Enough for a room; more
    // than the server keeps (its HLS output for a published camera is a 640x480 encode) and the
    // hardware encoder is what keeps it cheap on a phone that has to run all day.
    private static func chooseFormat(for device: AVCaptureDevice) -> (AVCaptureDevice.Format, Int) {
        let formats = RTCCameraVideoCapturer.supportedFormats(for: device)
        let targetWidth: Int32 = 1280
        var best: AVCaptureDevice.Format?
        var bestScore = Int32.max
        for format in formats {
            let size = CMVideoFormatDescriptionGetDimensions(format.formatDescription)
            guard size.width <= targetWidth else { continue }
            let score = abs(targetWidth - size.width)
            if score < bestScore { bestScore = score; best = format }
        }
        let chosen = best ?? formats.first ?? device.activeFormat
        let maxFps = chosen.videoSupportedFrameRateRanges.map(\.maxFrameRate).max() ?? 30
        return (chosen, Int(min(30, maxFps)))
    }

    // go2rtc sends no bandwidth feedback (no REMB, no transport-cc), so the encoder's estimator
    // never leaves its cautious starting point and the picture settles at 480x270 - measured on
    // a Mac on its own LAN, "qualityLimitationReason=bandwidth". A floor on the bitrate is what
    // keeps the encoder at the capture's resolution; the ceiling keeps a phone honest on Wi-Fi.
    private static func applyVideoBitrate(to sender: RTCRtpSender?) {
        guard let sender else { return }
        let parameters = sender.parameters
        for encoding in parameters.encodings {
            encoding.minBitrateBps = 1_500_000 as NSNumber
            encoding.maxBitrateBps = 4_000_000 as NSNumber
            encoding.maxFramerate = 30 as NSNumber
        }
        // A security camera would rather drop frames than detail: a face at 15fps is still a
        // face, a face at 480x270 is a smudge.
        parameters.degradationPreference = RTCDegradationPreference.maintainResolution.rawValue as NSNumber
        sender.parameters = parameters
    }

    private func closePeer() {
        peer?.close()
        peer = nil
        observer = nil
    }

    private func waitForIceGathering(_ peer: RTCPeerConnection, timeout: Duration) async {
        let deadline = ContinuousClock.now + timeout
        while peer.iceGatheringState != .complete, ContinuousClock.now < deadline {
            try? await Task.sleep(for: .milliseconds(100))
        }
    }

    // ---------------- Surviving a relaunch, a sleep, and the background ----------------

    private struct Intent: Codable {
        let target: Target
        let deviceId: String
        let includeMic: Bool
    }

    private func rememberIntent() {
        guard let target else { return }
        let intent = Intent(target: target, deviceId: deviceId, includeMic: includeMic)
        if let data = try? JSONEncoder().encode(intent) {
            UserDefaults.standard.set(data, forKey: Self.intentKey)
        }
    }

    private func forgetIntent() {
        UserDefaults.standard.removeObject(forKey: Self.intentKey)
    }

    var storedTarget: Target? {
        guard let data = UserDefaults.standard.data(forKey: Self.intentKey),
              let intent = try? JSONDecoder().decode(Intent.self, from: data) else { return nil }
        return intent.target
    }

    // Picks streaming back up after the app was relaunched, if it was streaming when it went. The
    // app is what has to be opened for the camera to run again, but the person shouldn't then also
    // have to find the screen and press start - a phone that reboots overnight for an update comes
    // back as a camera on its own once the app is opened.
    @discardableResult
    func resumeIfIntended(client: SentinelClient) -> Target? {
        guard !isPublishing,
              let data = UserDefaults.standard.data(forKey: Self.intentKey),
              let intent = try? JSONDecoder().decode(Intent.self, from: data) else { return nil }
        Task { await start(client: client, target: intent.target, deviceId: intent.deviceId, includeMic: intent.includeMic) }
        return intent.target
    }

    private func startHeartbeat() {
        heartbeatTask?.cancel()
        heartbeatTask = Task { [weak self] in
            var last = Date()
            while !Task.isCancelled {
                try? await Task.sleep(for: Self.heartbeatInterval)
                let now = Date()
                let gap = now.timeIntervalSince(last)
                last = now
                guard let self, self.isPublishing, gap >= Self.suspendGap else { continue }
                let minutes = Int((gap / 60).rounded())
                self.status = "this device was asleep for about \(minutes) minute\(minutes == 1 ? "" : "s") - reconnecting…"
                await self.reconnect(restartCapture: true)
            }
        }
    }

    private func observeLifecycle() {
        let center = NotificationCenter.default
        #if os(iOS)
        // iOS takes the camera away from an app in the background - there is no entitlement an
        // App Store app can hold that changes this - so what the stream can do is say so, and
        // pick straight back up when the app is in front again.
        lifecycleObservers.append(center.addObserver(forName: UIApplication.didEnterBackgroundNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in
                guard let self, self.isPublishing else { return }
                self.phase = .paused
                self.status = "paused - an iPhone can't use its camera while Sentinel Core is in the background. Bring it back to the front to resume."
            }
        })
        lifecycleObservers.append(center.addObserver(forName: UIApplication.didBecomeActiveNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in
                guard let self, self.isPublishing, self.phase == .paused else { return }
                self.status = "back in front - reconnecting…"
                await self.reconnect(restartCapture: true)
                KeepAwake.hold()
            }
        })
        #elseif os(macOS)
        lifecycleObservers.append(NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in
                guard let self, self.isPublishing else { return }
                self.status = "this Mac woke up - reconnecting…"
                await self.reconnect(restartCapture: true)
            }
        })
        #endif
    }

    private static func describe(_ error: Error) -> String {
        if let client = error as? ClientError {
            switch client {
            case .authenticationRequired: return "this device is no longer signed in - sign in again"
            case let .server(status, message):
                if status == 503 { return "the server can't accept published video because go2rtc isn't running on it" }
                return message ?? "the server rejected the connection (HTTP \(status))"
            default: break
            }
        }
        return error.localizedDescription
    }
}

enum PublishError: LocalizedError {
    case cameraDenied
    case microphoneDenied
    case noCamera
    case noFactory
    case cameraBusy(String)

    var errorDescription: String? {
        switch self {
        case .cameraDenied:
            #if os(iOS)
            return "Sentinel Core isn't allowed to use the camera. Allow it under Settings → Apps → Sentinel Core → Camera, then try again."
            #else
            return "Sentinel Core isn't allowed to use the camera. Allow it under System Settings → Privacy & Security → Camera, then try again."
            #endif
        case .microphoneDenied:
            #if os(iOS)
            return "Sentinel Core isn't allowed to use the microphone. Allow it under Settings → Apps → Sentinel Core → Microphone, or turn the microphone off here."
            #else
            return "Sentinel Core isn't allowed to use the microphone. Allow it under System Settings → Privacy & Security → Microphone, or turn the microphone off here."
            #endif
        case .noCamera:
            return "No camera was found on this device, or the chosen one has gone away. Pick another one and try again."
        case .noFactory:
            return "The video engine couldn't start on this device."
        case let .cameraBusy(detail):
            return "Couldn't open the camera - another app may be using it, or it was just released. Trying again in a moment usually works. (\(detail))"
        }
    }
}

// The one WebRTC callback this needs, as a plain object: the delegate protocol has a dozen
// required methods, all of them about receiving media, and forwarding only the connection state
// keeps the actor-isolated publisher out of WebRTC's signaling thread.
private final class PeerConnectionObserver: NSObject, RTCPeerConnectionDelegate {
    private let onConnectionState: @Sendable (RTCPeerConnectionState) -> Void
    // ICE and connection transitions, as text, for the Settings diagnostics log: "connected but
    // no video" is only diagnosable from these.
    private let onDiagnostic: @Sendable (String) -> Void

    init(onConnectionState: @escaping @Sendable (RTCPeerConnectionState) -> Void,
         onDiagnostic: @escaping @Sendable (String) -> Void) {
        self.onConnectionState = onConnectionState
        self.onDiagnostic = onDiagnostic
    }

    func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCPeerConnectionState) {
        onDiagnostic("peer connection state \(newState.rawValue) (0 new, 1 connecting, 2 connected, 3 disconnected, 4 failed, 5 closed)")
        onConnectionState(newState)
    }

    func peerConnection(_ peerConnection: RTCPeerConnection, didChange stateChanged: RTCSignalingState) {}
    func peerConnection(_ peerConnection: RTCPeerConnection, didAdd stream: RTCMediaStream) {}
    func peerConnection(_ peerConnection: RTCPeerConnection, didRemove stream: RTCMediaStream) {}
    func peerConnectionShouldNegotiate(_ peerConnection: RTCPeerConnection) {}
    func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCIceConnectionState) {
        onDiagnostic("ICE connection state \(newState.rawValue) (0 new, 1 checking, 2 connected, 3 completed, 4 failed, 5 disconnected, 6 closed)")
    }
    func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCIceGatheringState) {
        onDiagnostic("ICE gathering state \(newState.rawValue) (0 new, 1 gathering, 2 complete)")
    }
    func peerConnection(_ peerConnection: RTCPeerConnection, didGenerate candidate: RTCIceCandidate) {
        onDiagnostic("local candidate \(candidate.sdp.prefix(80))")
    }
    func peerConnection(_ peerConnection: RTCPeerConnection, didRemove candidates: [RTCIceCandidate]) {}
    func peerConnection(_ peerConnection: RTCPeerConnection, didOpen dataChannel: RTCDataChannel) {}
}
