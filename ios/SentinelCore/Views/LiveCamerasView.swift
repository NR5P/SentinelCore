import AVKit
import SwiftUI

// AVKit.framework is linked explicitly in the target's Frameworks phase - don't remove it as an
// unused link. `import AVKit` only autolinks _AVKit_SwiftUI (the cross-import overlay that
// defines VideoPlayer) and AVFoundation, not AVKit itself. On iOS/tvOS something else in the
// process has AVKit loaded anyway, but in an AppKit process nothing does, so the first time
// SwiftUI instantiates VideoPlayer's backing view the Swift runtime can't resolve its
// superclass (AVPlayerView) and traps: "failed to demangle superclass of VideoPlayerView from
// mangled name 'So12AVPlayerViewC'". Live playback no longer builds a VideoPlayer on macOS (see
// LiveVideoView), but the recorded-clip screens still do, so the link is still load-bearing.

// Player lifetime, the poster-frame handoff, fast-start buffering, and reconnect-on-drop all
// live in LiveStreamPlayer.

struct LiveCamerasView: View {
    @EnvironmentObject private var model: AppModel
    let isActive: Bool

    var body: some View {
        ScrollView {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 320), spacing: 16)], spacing: 16) {
                // Ordered as this account arranged it (see AppModel.liveCameras) - press and hold
                // a tile to drag it somewhere else. `cameras` itself stays in the server's order
                // for every other screen.
                ForEach(model.liveCameras) { camera in
                    NavigationLink(value: camera) {
                        CameraCard(camera: camera, isActive: isActive)
                    }
                    .buttonStyle(.plain)
                    // A single camera has nowhere to go, and starting a drag would only get in
                    // the way of tapping through to it.
                    .modifier(LiveTileReorder(camera: camera, isEnabled: model.cameras.count > 1))
                }
            }
            .padding()
            .animation(.snappy, value: model.liveCameraOrder)
        }
        .navigationTitle("Live")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.large)
        #endif
        .navigationDestination(for: Camera.self) { LivePlayerView(camera: $0) }
        .refreshable { await model.connect() }
        .toolbar {
            ToolbarItem {
                Button { Task { await model.connect(resetNetworkSession: true) } } label: {
                    Label("Refresh", systemImage: "arrow.clockwise")
                }
                .disabled(model.isLoading)
            }
        }
        .overlay {
            if model.cameras.isEmpty && !model.isLoading {
                ContentUnavailableView(
                    "No cameras",
                    systemImage: "video.slash",
                    description: Text("Add cameras in the Sentinel Core dashboard.")
                )
                .foregroundStyle(.secondary)
            } else if model.cameras.isEmpty {
                ProgressView("Loading cameras…")
            }
        }
    }
}

// Drag one live tile onto another to put it in that tile's place. The order is saved against the
// signed-in account (see LiveOrderEndpoints on the server), so rearranging here doesn't move
// anyone else's grid - and it's the same arrangement as that person's dashboard.
//
// The drag payload is the camera's id as a string rather than the Camera itself: a plain String
// is already Transferable, and nothing outside this grid should be able to make sense of the
// payload anyway. The preview is a name chip, not a snapshot of the tile - dragging a live video
// frame around renders as a black rectangle.
private struct LiveTileReorder: ViewModifier {
    @EnvironmentObject private var model: AppModel
    let camera: Camera
    let isEnabled: Bool

    @State private var isTargeted = false

    func body(content: Content) -> some View {
        if isEnabled {
            content
                .overlay {
                    if isTargeted {
                        RoundedRectangle(cornerRadius: 10)
                            .strokeBorder(SentinelTheme.green, lineWidth: 2)
                    }
                }
                .draggable(camera.id.uuidString) {
                    Label(camera.name, systemImage: "rectangle.grid.2x2")
                        .padding(8)
                        .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 8))
                }
                .dropDestination(for: String.self) { items, _ in
                    guard let dropped = items.first, let movingId = UUID(uuidString: dropped) else { return false }
                    model.moveLiveCamera(movingId, toPositionOf: camera.id)
                    return true
                } isTargeted: { isTargeted = $0 }
        } else {
            content
        }
    }
}

private struct CameraCard: View {
    let camera: Camera
    let isActive: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            LiveCameraPreview(camera: camera, isActive: isActive)
                .aspectRatio(16 / 9, contentMode: .fit)
                .clipShape(RoundedRectangle(cornerRadius: 8))
            HStack {
                Circle().fill(SentinelTheme.green).frame(width: 7, height: 7)
                Text(camera.name).foregroundStyle(SentinelTheme.green).bold()
                Spacer()
                if camera.includeAudio == true {
                    Image(systemName: "speaker.wave.2.fill")
                        .foregroundStyle(SentinelTheme.cyan)
                        .accessibilityLabel("Audio enabled")
                }
                Image(systemName: "arrow.up.right")
                    .foregroundStyle(.secondary)
            }
        }
        .sentinelCard()
    }
}

private struct LiveStreamRequest: Hashable {
    let isActive: Bool
    let source: LiveStreamSource?
}

private struct LiveCameraPreview: View {
    // How long a tile keeps its player paused-but-alive after the Live tab stops being active
    // before actually releasing it. Flipping to another tab and back within this window just
    // resumes instead of paying for a full reconnect - see the isActive .task below.
    private static let teardownGrace: Duration = .seconds(90)

    @EnvironmentObject private var model: AppModel
    @Environment(\.scenePhase) private var scenePhase
    let camera: Camera
    let isActive: Bool
    @StateObject private var stream = LiveStreamPlayer(muted: true)

    var body: some View {
        ZStack(alignment: .topLeading) {
            Rectangle().fill(.black)
            // Poster frame from the server's cached thumbnail, shown until the live video
            // actually has a frame to display - makes the tile feel instant instead of a
            // blank/spinner tile while the stream connects and buffers.
            if let posterURL {
                AsyncImage(url: posterURL) { image in
                    image.resizable().aspectRatio(contentMode: .fill)
                } placeholder: { Color.clear }
                .opacity(stream.isVideoReady ? 0 : 1)
            }
            LiveVideoView(stream: stream)
                // The whole tile opens the camera detail.
                .allowsHitTesting(false)
                .opacity(stream.isVideoReady ? 1 : 0)
            LiveStatusChip(isVideoReady: stream.isVideoReady)
                .padding(10)
        }
        // On the whole tile rather than the video alone, so the mark is there over the poster
        // frame too and doesn't blink in and out as playback starts.
        .unlicensedWatermark()
        // Include the resolved URL in the task identity. Connection setup can replace an
        // untrusted HTTPS address with the server's HTTP listener while this view is already
        // active; keying this only by isActive left AVPlayer permanently attached to the old
        // certificate-rejected URL even though every API request had moved to HTTP.
        .task(id: streamRequest) {
            let request = streamRequest
            if request.isActive {
                if let source = request.source { stream.start(source, client: model.client) }
            } else {
                stream.pause()
                do {
                    try await Task.sleep(for: Self.teardownGrace)
                } catch {
                    return
                }
                guard !Task.isCancelled else { return }
                stream.stop()
            }
        }
        .onDisappear { stream.stop() }
    }

    private var posterURL: URL? {
        model.client.url(path: "/api/cameras/\(camera.id.uuidString)/thumbnail.jpg")
    }

    // The low rendition, not the main one. A tile is a few hundred points wide, and a grid of them
    // pulling full-resolution streams is what saturated the phone's Wi-Fi: measured against three
    // 2560x1920 Reolinks, each tile got ~8 Mbit/s of a ~25 Mbit/s ceiling, every tile stalled and
    // reconnected, and dropped frames stayed at zero throughout - starved for data, not short of
    // decode. The full-screen view below keeps the main stream, which is where the detail is
    // wanted and where it is the only stream being pulled.
    //
    // A camera with no low stream is served its main one, so this costs nothing where it can't
    // help. A server *older* than this path is a different matter - it routes /stream/{file} only,
    // so the extra segment doesn't match and the tile gets a 404. This app therefore has to reach
    // a server carrying the matching change; the reverse is fine, since /stream/{file} still means
    // the main stream and older clients keep working against a new server.
    private var streamURL: URL? {
        model.client.url(path: "/api/cameras/\(camera.id.uuidString)/stream/sub/stream.m3u8")
    }

    // The same low rendition, over the transport that isn't seconds behind. A tile is muted and
    // has no way not to be, so it never asks for audio.
    private var mseURL: URL? {
        model.client.webSocketURL(path: "/api/cameras/\(camera.id.uuidString)/mse/sub")
    }

    // Backgrounded counts as inactive, which the tab selection alone never noticed. It matters more
    // now than it did on HLS: a suspended app can't decode, so the WebSocket would keep being fed
    // frames nothing was drawing until the watchdog wrote the whole tier off - and on a phone away
    // from the house, those are cellular megabytes spent on a picture in somebody's pocket.
    // Deliberately `!= .background` rather than `== .active`: on macOS a window merely losing focus
    // goes .inactive, and a monitoring app that stops showing cameras when you click elsewhere is
    // not a monitoring app.
    private var isStreaming: Bool { isActive && scenePhase != .background }

    private var streamRequest: LiveStreamRequest {
        guard let streamURL else { return LiveStreamRequest(isActive: isStreaming, source: nil) }
        return LiveStreamRequest(
            isActive: isStreaming,
            source: LiveStreamSource(
                cameraId: camera.id, mse: mseURL, hls: streamURL, wantsAudio: false))
    }
}

// A green "LIVE" badge over a tile that is actually frozen or reconnecting is worse than no
// badge at all - it's the one thing on screen claiming the feed is current. Until the first
// frame lands (and again after a drop, while LiveStreamPlayer backs off and retries) this says
// so instead.
private struct LiveStatusChip: View {
    let isVideoReady: Bool

    var body: some View {
        if isVideoReady {
            StatusChip("LIVE", systemImage: "circle.fill", color: SentinelTheme.green)
        } else {
            // StatusChip tints its text with this colour, and it sits on the tile's black
            // backdrop rather than the app background - .secondary would resolve to dark grey
            // on black in light mode and be unreadable.
            StatusChip("CONNECTING", systemImage: "circle.dotted", color: .white.opacity(0.7))
        }
    }
}

struct LivePlayerView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.scenePhase) private var scenePhase
    let camera: Camera
    @StateObject private var stream = LiveStreamPlayer(muted: false)

    @State private var zoom = DigitalZoomState()
    @StateObject private var notice = NoticeState()
    @State private var isRecording = false

    var body: some View {
        ZStack(alignment: .topLeading) {
            Color.black
            // Poster and video zoom together; everything below this - the status chip, the mute
            // and zoom controls, the PTZ pad - deliberately stays put at its own scale.
            DigitalZoom(state: $zoom) {
                ZStack {
                    if let posterURL = model.client.url(path: "/api/cameras/\(camera.id.uuidString)/thumbnail.jpg") {
                        AsyncImage(url: posterURL) { image in
                            image.resizable().aspectRatio(contentMode: .fit)
                        } placeholder: { Color.clear }
                        .opacity(stream.isVideoReady ? 0 : 1)
                    }
                    LiveVideoView(stream: stream)
                        .opacity(stream.isVideoReady ? 1 : 0)
                }
            }
            if !stream.isVideoReady {
                ProgressView("Starting stream…")
                    .tint(.white)
                    .foregroundStyle(.white)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
            LiveStatusChip(isVideoReady: stream.isVideoReady)
                .padding()
        }
        // Applied before the PTZ overlay below so the joystick stays on top of the mark and fully
        // usable - the watermark never gets in the way of operating a camera.
        .unlicensedWatermark()
        // The live video surface has no controls of its own on either platform (see
        // LiveVideoView), and a camera with audio would otherwise play into the room with no way
        // to stop it short of leaving the screen.
        .overlay(alignment: .topTrailing) {
            VStack(alignment: .trailing, spacing: 8) {
                HStack(spacing: 8) {
                    recordButton
                    muteButton
                }
                if let text = notice.text {
                    TransientNotice(text: text)
                }
            }
            .padding()
            .animation(.easeOut(duration: 0.15), value: notice.text)
        }
        .overlay(alignment: .bottomLeading) {
            DigitalZoomControls(state: $zoom)
                .padding()
        }
        .overlay(alignment: .bottomTrailing) {
            if camera.ptzEnabled == true {
                PtzControlsView(cameraId: camera.id)
                    .padding()
            }
        }
        .navigationTitle(camera.name)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .task(id: streamRequest) {
            guard streamRequest.isActive, let source else {
                stream.pause()
                return
            }
            stream.start(source, client: model.client)
        }
        .onDisappear {
            stream.stop()
            notice.cancel()
        }
    }

    // Every camera gets this, not just the PTZ one: the moment worth keeping is just as likely on a
    // fixed camera you happened to be looking at. The server turns it into an ordinary trigger, so
    // what comes out the other end is a card in the Feed exactly like a motion event.
    private var recordButton: some View {
        Button {
            record()
        } label: {
            Label("Record now", systemImage: isRecording ? "record.circle.fill" : "record.circle")
                .labelStyle(.iconOnly)
                .padding(8)
        }
        .buttonStyle(.plain)
        .disabled(isRecording)
        .background(.black.opacity(0.5), in: .circle)
        .foregroundStyle(isRecording ? SentinelTheme.red : .white)
        .accessibilityLabel("Record now")
    }

    private var muteButton: some View {
            Button {
                stream.isMuted.toggle()
            } label: {
                Label(
                    stream.isMuted ? "Unmute" : "Mute",
                    systemImage: stream.isMuted ? "speaker.slash.fill" : "speaker.wave.2.fill"
                )
                .labelStyle(.iconOnly)
                .padding(8)
            }
            .buttonStyle(.plain)
            // The video behind it is arbitrary camera footage, so the control carries its own
            // backdrop rather than trusting whatever it happens to sit on.
            .background(.black.opacity(0.5), in: .circle)
            .foregroundStyle(.white)
    }

    private func record() {
        guard !isRecording else { return }
        isRecording = true
        let client = model.client
        let id = camera.id
        Task {
            do {
                try await client.recordNow(cameraId: id)
                notice.show("Recording saved to the feed.")
            } catch {
                notice.show((error as? LocalizedError)?.errorDescription ?? "Could not record that.")
            }
            isRecording = false
        }
    }

    private var streamRequest: LiveStreamRequest {
        LiveStreamRequest(isActive: scenePhase != .background, source: source)
    }

    private var source: LiveStreamSource? {
        guard let hls = model.client.url(path: "/api/cameras/\(camera.id.uuidString)/stream/stream.m3u8")
        else { return nil }
        return LiveStreamSource(
            cameraId: camera.id,
            mse: model.client.webSocketURL(path: "/api/cameras/\(camera.id.uuidString)/mse"),
            hls: hls,
            // Only where there is something to hear: asking go2rtc for an audio track it then has
            // to carry costs bandwidth on every camera, and only two of these have audio at all.
            wantsAudio: camera.includeAudio == true)
    }
}

// MARK: - Digital zoom
//
// Zoom that has nothing to do with the camera's own lens. A PTZ camera's optical zoom is a command
// sent to the camera (PtzControlsView) and it moves the picture for everyone watching; this scales
// the frames already on this screen, works on every camera including fixed ones, and is nobody
// else's business.
//
// The transform is applied to the video surface rather than to the AVPlayer, so it costs nothing:
// no extra stream, no re-request at a different resolution, no server involvement. What it can't
// do is add detail that isn't in the stream - past a few times magnification you are looking at
// the encoder's pixels. Hence the 8x ceiling.

private struct DigitalZoomState {
    static let maxScale: CGFloat = 8

    var scale: CGFloat = 1
    var offset: CGSize = .zero

    // Where the gesture started from. A pinch reports magnification relative to its own start, and
    // a drag reports translation relative to its own start, so both need the value they began with
    // rather than the running one - accumulating onto the live value squares the pinch and makes
    // the picture bolt away from the finger.
    var gestureStartScale: CGFloat = 1
    var gestureStartOffset: CGSize = .zero

    var isZoomed: Bool { scale > 1.01 }

    mutating func reset() {
        scale = 1
        offset = .zero
        gestureStartScale = 1
        gestureStartOffset = .zero
    }
}

private struct DigitalZoom<Content: View>: View {
    @Binding var state: DigitalZoomState
    @ViewBuilder let content: Content

    var body: some View {
        GeometryReader { geo in
            content
                .scaleEffect(state.scale)
                .offset(state.offset)
                .frame(width: geo.size.width, height: geo.size.height)
                // Without this the magnified picture spills over the status chip, the PTZ pad and
                // whatever the screen puts next to it.
                .clipped()
                .contentShape(Rectangle())
                .gesture(magnify(in: geo.size).simultaneously(with: pan(in: geo.size)))
                // Two taps to get back to the whole frame - the same gesture every photo viewer
                // uses, and the only one available while a finger-sized control would be in the
                // way of the picture.
                .onTapGesture(count: 2) {
                    withAnimation(.snappy) {
                        if state.isZoomed { state.reset() } else { zoom(to: 2, in: geo.size) }
                    }
                }
        }
    }

    private func magnify(in size: CGSize) -> some Gesture {
        MagnifyGesture()
            .onChanged { value in
                let newScale = clampedScale(state.gestureStartScale * value.magnification)
                // Keep whatever was under the pinch under the pinch. `startAnchor` is a unit point
                // in this view, measured from its top-left; the maths below wants it as a distance
                // from the centre, because that is what scaleEffect scales about.
                let anchor = CGSize(
                    width: (value.startAnchor.x - 0.5) * size.width,
                    height: (value.startAnchor.y - 0.5) * size.height
                )
                let ratio = newScale / state.gestureStartScale
                state.scale = newScale
                state.offset = clampedOffset(
                    CGSize(
                        width: anchor.width - ratio * (anchor.width - state.gestureStartOffset.width),
                        height: anchor.height - ratio * (anchor.height - state.gestureStartOffset.height)
                    ),
                    scale: newScale,
                    in: size
                )
            }
            .onEnded { _ in commit() }
    }

    // Only while zoomed in. At 1x there is nothing to pan, and a drag gesture that swallows every
    // sideways swipe would take the interactive back-swipe out of this screen with it.
    private func pan(in size: CGSize) -> some Gesture {
        DragGesture()
            .onChanged { value in
                guard state.isZoomed else { return }
                state.offset = clampedOffset(
                    CGSize(
                        width: state.gestureStartOffset.width + value.translation.width,
                        height: state.gestureStartOffset.height + value.translation.height
                    ),
                    scale: state.scale,
                    in: size
                )
            }
            .onEnded { _ in commit() }
    }

    private func zoom(to scale: CGFloat, in size: CGSize) {
        state.scale = clampedScale(scale)
        state.offset = clampedOffset(state.offset, scale: state.scale, in: size)
        commit()
    }

    private func commit() {
        state.gestureStartScale = state.scale
        state.gestureStartOffset = state.offset
    }

    private func clampedScale(_ scale: CGFloat) -> CGFloat {
        min(max(scale, 1), DigitalZoomState.maxScale)
    }

    // Stops the picture being dragged off the edge and leaving a black screen with nothing to
    // aim at: at scale s there are (s - 1) / 2 of a frame's width hidden on each side, and that is
    // exactly how far it may travel.
    private func clampedOffset(_ offset: CGSize, scale: CGFloat, in size: CGSize) -> CGSize {
        let limitX = max(size.width * (scale - 1) / 2, 0)
        let limitY = max(size.height * (scale - 1) / 2, 0)
        return CGSize(
            width: min(max(offset.width, -limitX), limitX),
            height: min(max(offset.height, -limitY), limitY)
        )
    }
}

// The pinch is the main way in, but it is invisible, it doesn't exist on a Mac without a trackpad,
// and a stuck-at-3x picture with no obvious way back is worse than no zoom at all. These buttons
// are the discoverable version of all three.
private struct DigitalZoomControls: View {
    @Binding var state: DigitalZoomState

    var body: some View {
        HStack(spacing: 4) {
            button("minus.magnifyingglass", label: "Zoom out") { step(by: 1 / 1.5) }
            Button {
                withAnimation(.snappy) { state.reset() }
            } label: {
                Text(state.isZoomed ? String(format: "%.1f×", state.scale) : "1×")
                    .font(.caption.monospacedDigit())
                    .frame(minWidth: 34)
                    .padding(.vertical, 8)
            }
            .buttonStyle(.plain)
            .disabled(!state.isZoomed)
            .accessibilityLabel("Reset zoom")
            button("plus.magnifyingglass", label: "Zoom in") { step(by: 1.5) }
        }
        .foregroundStyle(.white)
        // Arbitrary camera footage sits behind this, so the cluster carries its own backdrop
        // rather than trusting whatever it happens to land on.
        .background(.black.opacity(0.5), in: .capsule)
    }

    private func button(_ systemImage: String, label: String, action: @escaping () -> Void) -> some View {
        Button(action: { withAnimation(.snappy) { action() } }) {
            Image(systemName: systemImage)
                .padding(.horizontal, 10)
                .padding(.vertical, 8)
        }
        .buttonStyle(.plain)
        .accessibilityLabel(label)
    }

    // Zooming with the buttons keeps the centre of the *picture* where it is, which is the only
    // fixed point there is without a finger to zoom about.
    private func step(by factor: CGFloat) {
        let newScale = min(max(state.scale * factor, 1), DigitalZoomState.maxScale)
        let ratio = newScale / state.scale
        state.scale = newScale
        state.offset = CGSize(width: state.offset.width * ratio, height: state.offset.height * ratio)
        state.gestureStartScale = state.scale
        state.gestureStartOffset = state.offset
    }
}
