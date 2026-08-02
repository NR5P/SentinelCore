import AVKit
import SwiftUI

// AVKit.framework is linked explicitly in the target's Frameworks phase - don't remove it as an
// unused link. `import AVKit` only autolinks _AVKit_SwiftUI (the cross-import overlay that
// defines VideoPlayer) and AVFoundation, not AVKit itself. On iOS/tvOS something else in the
// process has AVKit loaded anyway, but in an AppKit process nothing does, so the first time
// SwiftUI instantiates VideoPlayer's backing view the Swift runtime can't resolve its
// superclass (AVPlayerView) and traps: "failed to demangle superclass of VideoPlayerView from
// mangled name 'So12AVPlayerViewC'". That's an abort the moment the Live grid appears on macOS.

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
    let url: URL?
}

private struct LiveCameraPreview: View {
    // How long a tile keeps its player paused-but-alive after the Live tab stops being active
    // before actually releasing it. Flipping to another tab and back within this window just
    // resumes instead of paying for a full reconnect - see the isActive .task below.
    private static let teardownGrace: Duration = .seconds(90)

    @EnvironmentObject private var model: AppModel
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
            VideoPlayer(player: stream.player)
                // The whole tile opens the camera detail; playback controls live there.
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
                if let url = request.url { stream.start(url: url) }
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

    private var streamURL: URL? {
        model.client.url(path: "/api/cameras/\(camera.id.uuidString)/stream/stream.m3u8")
    }

    private var streamRequest: LiveStreamRequest {
        LiveStreamRequest(isActive: isActive, url: streamURL)
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
    let camera: Camera
    @StateObject private var stream = LiveStreamPlayer(muted: false)

    var body: some View {
        ZStack(alignment: .topLeading) {
            Color.black
            if let posterURL = model.client.url(path: "/api/cameras/\(camera.id.uuidString)/thumbnail.jpg") {
                AsyncImage(url: posterURL) { image in
                    image.resizable().aspectRatio(contentMode: .fit)
                } placeholder: { Color.clear }
                .opacity(stream.isVideoReady ? 0 : 1)
            }
            VideoPlayer(player: stream.player)
                .opacity(stream.isVideoReady ? 1 : 0)
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
        .task(id: streamURL) {
            guard let url = streamURL else { return }
            stream.start(url: url)
        }
        .onDisappear { stream.stop() }
    }

    private var streamURL: URL? {
        model.client.url(path: "/api/cameras/\(camera.id.uuidString)/stream/stream.m3u8")
    }
}
