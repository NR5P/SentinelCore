import AVKit
import SwiftUI

// AVPlayer's default buffering waits to build up a comfortable cushion before it'll start
// playing, which is the right call for on-demand video but adds needless delay for a live feed
// where "as current as possible, right away" matters more than smooth playback of a buffer.
private func configureForFastStart(_ player: AVPlayer) {
    player.automaticallyWaitsToMinimizeStalling = false
    player.currentItem?.preferredForwardBufferDuration = 1
}

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
    @State private var player: AVPlayer?
    @State private var isVideoReady = false

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
                .opacity(isVideoReady ? 0 : 1)
            }
            if let player {
                VideoPlayer(player: player)
                    // The whole tile opens the camera detail; playback controls live there.
                    .allowsHitTesting(false)
                    .opacity(isVideoReady ? 1 : 0)
            }
            StatusChip("LIVE", systemImage: "circle.fill", color: SentinelTheme.green)
                .padding(10)
        }
        .task(id: isActive) {
            if isActive {
                startPlayback()
            } else {
                player?.pause()
                isVideoReady = false
                try? await Task.sleep(for: Self.teardownGrace)
                stopPlayback()
            }
        }
        .onDisappear(perform: stopPlayback)
    }

    private var posterURL: URL? {
        model.client.url(path: "/api/cameras/\(camera.id.uuidString)/thumbnail.jpg")
    }

    private func startPlayback() {
        guard player == nil else {
            player?.play()
            return
        }
        guard let url = model.client.url(
            path: "/api/cameras/\(camera.id.uuidString)/stream/stream.m3u8"
        ) else { return }

        let newPlayer = AVPlayer(url: url)
        newPlayer.isMuted = true
        configureForFastStart(newPlayer)
        player = newPlayer
        newPlayer.play()
        observeFirstFrame(newPlayer)
    }

    private func stopPlayback() {
        player?.pause()
        player = nil
        isVideoReady = false
    }

    // Reveals the video (and hides the poster) only once the player has actually started
    // advancing - readyToPlay alone still leaves a black frame up briefly.
    private func observeFirstFrame(_ player: AVPlayer) {
        var token: Any?
        token = player.addPeriodicTimeObserver(forInterval: CMTime(seconds: 0.1, preferredTimescale: 60), queue: .main) { time in
            guard time.seconds > 0 else { return }
            isVideoReady = true
            if let token { player.removeTimeObserver(token) }
        }
    }
}

struct LivePlayerView: View {
    @EnvironmentObject private var model: AppModel
    let camera: Camera
    @State private var player: AVPlayer?
    @State private var isVideoReady = false

    var body: some View {
        ZStack(alignment: .topLeading) {
            Color.black
            if let posterURL = model.client.url(path: "/api/cameras/\(camera.id.uuidString)/thumbnail.jpg") {
                AsyncImage(url: posterURL) { image in
                    image.resizable().aspectRatio(contentMode: .fit)
                } placeholder: { Color.clear }
                .opacity(isVideoReady ? 0 : 1)
            }
            if let player {
                VideoPlayer(player: player)
                    .opacity(isVideoReady ? 1 : 0)
            } else {
                ProgressView("Starting stream…")
                    .tint(.white)
                    .foregroundStyle(.white)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
            StatusChip("LIVE", systemImage: "circle.fill", color: SentinelTheme.green)
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
            let newPlayer = AVPlayer(url: url)
            configureForFastStart(newPlayer)
            player = newPlayer
            newPlayer.play()

            var token: Any?
            token = newPlayer.addPeriodicTimeObserver(forInterval: CMTime(seconds: 0.1, preferredTimescale: 60), queue: .main) { time in
                guard time.seconds > 0 else { return }
                isVideoReady = true
                if let token { newPlayer.removeTimeObserver(token) }
            }
        }
        .onDisappear {
            player?.pause()
            player = nil
            isVideoReady = false
        }
    }
}
