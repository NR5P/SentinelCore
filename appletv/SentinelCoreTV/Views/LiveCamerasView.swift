import AVKit
import SwiftUI

// tvOS port of ios/SentinelCore/Views/LiveCamerasView.swift - same grid-of-muted-previews ->
// full-screen player pattern, just without the iOS-only navigation bar modifiers and using
// tvOS's larger focus-friendly card sizing. Player lifetime, the poster-frame handoff, and
// reconnect-on-drop all live in LiveStreamPlayer.

struct LiveCamerasView: View {
    @EnvironmentObject private var model: AppModel
    let isActive: Bool

    private static let tileSpacing: CGFloat = 40
    private static let tileMinimum: CGFloat = 420
    private static let horizontalPadding: CGFloat = 60

    var body: some View {
        GeometryReader { proxy in
        ScrollView {
            LazyVGrid(
                columns: liveGridColumns(
                    availableWidth: proxy.size.width - Self.horizontalPadding * 2,
                    cameraCount: model.liveCameras.count,
                    minimum: Self.tileMinimum,
                    spacing: Self.tileSpacing),
                spacing: Self.tileSpacing) {
                // In the order this account arranged on a phone or in the dashboard - there's no
                // way to rearrange it from here, a remote being a poor drag device.
                ForEach(model.liveCameras) { camera in
                    NavigationLink(value: camera) {
                        CameraCard(camera: camera, isActive: isActive)
                    }
                    .buttonStyle(.card)
                }
            }
            .padding(.horizontal, Self.horizontalPadding)
            .padding(.bottom, 60)
        }
        .navigationDestination(for: Camera.self) { LivePlayerView(camera: $0) }
        .toolbar {
            ToolbarItem {
                Button { Task { await model.connect(resetNetworkSession: true) } } label: {
                    // A bare Image rather than a Label: tvOS gives a toolbar item far less
                    // width than the title needs and truncates it to something like "R…sh",
                    // and .labelStyle(.iconOnly) doesn't survive the toolbar's own styling.
                    Image(systemName: "arrow.clockwise").accessibilityLabel("Refresh")
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
}

// Ported from ios/SentinelCore/Views/LiveCamerasView.swift, and it matters more here than
// anywhere else: `.adaptive(minimum:)` keeps every column the width can hold whether or not a
// camera goes in one, so on a 3840pt television two cameras sat as two 420pt cards in the top
// corner with the rest of the screen black. A TV is the display furthest from the viewer and the
// one where a small picture is least usable.
private func liveGridColumns(
    availableWidth: CGFloat, cameraCount: Int, minimum: CGFloat, spacing: CGFloat) -> [GridItem]
{
    // GeometryReader reports zero on its first pass, and a NaN would trap the Int conversion.
    let usable = availableWidth.isFinite ? max(0, availableWidth) : 0
    let fits = max(1, Int((usable + spacing) / (minimum + spacing)))
    return Array(
        repeating: GridItem(.flexible(), spacing: spacing),
        count: max(1, min(cameraCount, fits)))
}

private struct CameraCard: View {
    let camera: Camera
    let isActive: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            LiveCameraPreview(camera: camera, isActive: isActive)
                .aspectRatio(16 / 9, contentMode: .fit)
                .clipShape(RoundedRectangle(cornerRadius: 12))
            HStack(spacing: 12) {
                Text(camera.name)
                    .foregroundStyle(SentinelTheme.green)
                    .bold()
                    .lineLimit(1)
                Spacer(minLength: 0)
                if camera.includeAudio == true {
                    Image(systemName: "speaker.wave.2.fill")
                        .foregroundStyle(SentinelTheme.cyan)
                        .accessibilityLabel("Audio enabled")
                }
            }
            // .callout rather than .title3: at tvOS type sizes the camera name was competing
            // with the video for attention, and a long name pushed the audio icon off the tile.
            .font(.callout)
            // The preview above is deliberately full-bleed, but the label underneath sits in
            // the .card button's own chrome - with no inset it hugs the bottom-left corner and
            // the descenders run into the card's rounded edge.
            .padding(.horizontal, 8)
            .padding(.bottom, 6)
        }
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
                .allowsHitTesting(false)
                .opacity(stream.isVideoReady ? 1 : 0)
            LiveStatusChip(isVideoReady: stream.isVideoReady)
                .padding(14)
        }
        // On the whole tile rather than the video alone, so the mark is there over the poster
        // frame too and doesn't blink in and out as playback starts.
        .unlicensedWatermark()
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

    // The low rendition for tiles - see the note on the iPhone's twin of this view. A TV grid is
    // several tiles at once over the same one link, which is the case that saturates.
    private var streamURL: URL? {
        model.client.url(path: "/api/cameras/\(camera.id.uuidString)/stream/sub/stream.m3u8")
    }

    // The same low rendition, over the transport that isn't seconds behind. A tile is muted and
    // has no way not to be, so it never asks for audio.
    private var mseURL: URL? {
        model.client.webSocketURL(path: "/api/cameras/\(camera.id.uuidString)/mse/sub")
    }

    // Backgrounded counts as inactive, which the tab selection alone never noticed. An Apple TV
    // that has been put to sleep can't decode, so the socket would keep being fed frames nothing
    // was drawing until the watchdog wrote the whole tier off.
    private var isStreaming: Bool { isActive && scenePhase != .background }

    private var streamRequest: LiveStreamRequest {
        guard let streamURL else { return LiveStreamRequest(isActive: isStreaming, source: nil) }
        return LiveStreamRequest(
            isActive: isStreaming,
            source: LiveStreamSource(
                cameraId: camera.id, mse: mseURL, hls: streamURL, wantsAudio: false))
    }
}

private struct LiveStatusChip: View {
    let isVideoReady: Bool

    var body: some View {
        if isVideoReady {
            StatusChip("LIVE", systemImage: "circle.fill", color: SentinelTheme.green)
        } else {
            StatusChip("CONNECTING", systemImage: "circle.dotted", color: .white.opacity(0.7))
        }
    }
}

struct LivePlayerView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.scenePhase) private var scenePhase
    let camera: Camera
    @StateObject private var stream = LiveStreamPlayer(muted: false)

    var body: some View {
        ZStack {
            Color.black
            if let posterURL = model.client.url(path: "/api/cameras/\(camera.id.uuidString)/thumbnail.jpg") {
                AsyncImage(url: posterURL) { image in
                    image.resizable().aspectRatio(contentMode: .fit)
                } placeholder: { Color.clear }
                .opacity(stream.isVideoReady ? 0 : 1)
            }
            LiveVideoView(stream: stream)
                .opacity(stream.isVideoReady ? 1 : 0)
            if !stream.isVideoReady {
                ProgressView("Starting stream…")
                    .tint(.white)
                    .foregroundStyle(.white)
            }
        }
        .unlicensedWatermark()
        .ignoresSafeArea()
        .navigationTitle(camera.name)
        .task(id: streamRequest) {
            guard streamRequest.isActive, let source else {
                stream.pause()
                return
            }
            stream.start(source, client: model.client)
        }
        .onDisappear { stream.stop() }
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
            // to carry costs bandwidth on every camera, and only some of these have audio at all.
            wantsAudio: camera.includeAudio == true)
    }
}
