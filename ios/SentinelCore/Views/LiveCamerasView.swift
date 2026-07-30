import AVKit
import SwiftUI

// Player lifetime, the poster-frame handoff, fast-start buffering, and reconnect-on-drop all
// live in LiveStreamPlayer.

struct LiveCamerasView: View {
    @EnvironmentObject private var model: AppModel
    let isActive: Bool

    var body: some View {
        ScrollView {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 320), spacing: 16)], spacing: 16) {
                ForEach(model.cameras) { camera in
                    NavigationLink(value: camera) {
                        CameraCard(camera: camera, isActive: isActive)
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding()
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
        .task(id: isActive) {
            if isActive {
                if let streamURL { stream.start(url: streamURL) }
            } else {
                stream.pause()
                try? await Task.sleep(for: Self.teardownGrace)
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
        .task {
            guard let url = model.client.url(path: "/api/cameras/\(camera.id.uuidString)/stream/stream.m3u8") else { return }
            stream.start(url: url)
        }
        .onDisappear { stream.stop() }
    }
}
