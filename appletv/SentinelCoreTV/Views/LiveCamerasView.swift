import AVKit
import SwiftUI

// tvOS port of ios/SentinelCore/Views/LiveCamerasView.swift - same grid-of-muted-previews ->
// full-screen player pattern, just without the iOS-only navigation bar modifiers and using
// tvOS's larger focus-friendly card sizing.
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
            .padding(60)
        }
        .navigationTitle("Live")
        .navigationDestination(for: Camera.self) { LivePlayerView(camera: $0) }
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
        VStack(alignment: .leading, spacing: 14) {
            LiveCameraPreview(camera: camera, isActive: isActive)
                .aspectRatio(16 / 9, contentMode: .fit)
                .clipShape(RoundedRectangle(cornerRadius: 12))
            HStack {
                Circle().fill(SentinelTheme.green).frame(width: 9, height: 9)
                Text(camera.name).foregroundStyle(SentinelTheme.green).bold()
                Spacer()
                if camera.includeAudio == true {
                    Image(systemName: "speaker.wave.2.fill")
                        .foregroundStyle(SentinelTheme.cyan)
                        .accessibilityLabel("Audio enabled")
                }
            }
            .font(.title3)
        }
    }
}

private struct LiveCameraPreview: View {
    @EnvironmentObject private var model: AppModel
    let camera: Camera
    let isActive: Bool
    @State private var player: AVPlayer?

    var body: some View {
        ZStack(alignment: .topLeading) {
            Rectangle().fill(.black)
            if let player {
                VideoPlayer(player: player)
                    .allowsHitTesting(false)
            } else {
                ProgressView("Starting stream…")
                    .tint(.white)
                    .foregroundStyle(.white)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
            StatusChip("LIVE", systemImage: "circle.fill", color: SentinelTheme.green)
                .padding(14)
        }
        .task(id: isActive) {
            if isActive {
                startPlayback()
            } else {
                stopPlayback()
            }
        }
        .onDisappear(perform: stopPlayback)
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
        player = newPlayer
        newPlayer.play()
    }

    private func stopPlayback() {
        player?.pause()
        player = nil
    }
}

struct LivePlayerView: View {
    @EnvironmentObject private var model: AppModel
    let camera: Camera
    @State private var player: AVPlayer?

    var body: some View {
        ZStack {
            Color.black
            if let player {
                VideoPlayer(player: player)
            } else {
                ProgressView("Starting stream…")
                    .tint(.white)
                    .foregroundStyle(.white)
            }
        }
        .ignoresSafeArea()
        .navigationTitle(camera.name)
        .task {
            guard let url = model.client.url(path: "/api/cameras/\(camera.id.uuidString)/stream/stream.m3u8") else { return }
            let newPlayer = AVPlayer(url: url)
            player = newPlayer
            newPlayer.play()
        }
        .onDisappear {
            player?.pause()
            player = nil
        }
    }
}
