import AVKit
import SwiftUI

// tvOS port of ios/SentinelCore/Views/LiveCamerasView.swift - same grid-of-muted-previews ->
// full-screen player pattern, just without the iOS-only navigation bar modifiers and using
// tvOS's larger focus-friendly card sizing. Player lifetime, the poster-frame handoff, and
// reconnect-on-drop all live in LiveStreamPlayer.

struct LiveCamerasView: View {
    @EnvironmentObject private var model: AppModel
    let isActive: Bool

    var body: some View {
        ScrollView {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 420), spacing: 40)], spacing: 40) {
                ForEach(model.cameras) { camera in
                    NavigationLink(value: camera) {
                        CameraCard(camera: camera, isActive: isActive)
                    }
                    .buttonStyle(.card)
                }
            }
            .padding(.horizontal, 60)
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
                .allowsHitTesting(false)
                .opacity(stream.isVideoReady ? 1 : 0)
            StatusChip("LIVE", systemImage: "circle.fill", color: SentinelTheme.green)
                .padding(14)
        }
        // On the whole tile rather than the video alone, so the mark is there over the poster
        // frame too and doesn't blink in and out as playback starts.
        .unlicensedWatermark()
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

struct LivePlayerView: View {
    @EnvironmentObject private var model: AppModel
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
            VideoPlayer(player: stream.player)
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
        .task {
            guard let url = model.client.url(path: "/api/cameras/\(camera.id.uuidString)/stream/stream.m3u8") else { return }
            stream.start(url: url)
        }
        .onDisappear { stream.stop() }
    }
}
