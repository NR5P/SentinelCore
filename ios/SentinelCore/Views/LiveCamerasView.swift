import AVKit
import SwiftUI

struct LiveCamerasView: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        ScrollView {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 320), spacing: 16)], spacing: 16) {
                ForEach(model.cameras) { camera in
                    NavigationLink(value: camera) {
                        VStack(alignment: .leading, spacing: 10) {
                            ZStack {
                                Rectangle().fill(.black)
                                Image(systemName: "video.fill")
                                    .font(.largeTitle)
                                    .foregroundStyle(SentinelTheme.greenDim)
                            }
                            .aspectRatio(16 / 9, contentMode: .fit)
                            .clipShape(RoundedRectangle(cornerRadius: 5))
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
                                    .foregroundStyle(SentinelTheme.textDim)
                            }
                        }
                        .sentinelPanel()
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding()
        }
        .background(SentinelTheme.background)
        .navigationTitle("LIVE")
        .navigationDestination(for: Camera.self) { LivePlayerView(camera: $0) }
        .refreshable { await model.connect() }
        .overlay {
            if model.cameras.isEmpty && !model.isLoading {
                ContentUnavailableView(
                    "No cameras",
                    systemImage: "video.slash",
                    description: Text("Add cameras in the Sentinel Core dashboard.")
                )
                .foregroundStyle(SentinelTheme.textDim)
            } else if model.cameras.isEmpty {
                ProgressView("Loading cameras…")
            }
        }
    }
}

struct LivePlayerView: View {
    @EnvironmentObject private var model: AppModel
    let camera: Camera
    @State private var player: AVPlayer?

    var body: some View {
        ZStack(alignment: .topLeading) {
            Color.black
            if let player {
                VideoPlayer(player: player)
            } else {
                ProgressView("Starting stream…")
                    .foregroundStyle(SentinelTheme.text)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
            StatusChip("LIVE", systemImage: "circle.fill", color: SentinelTheme.green)
                .padding()
        }
        .navigationTitle(camera.name)
        .task {
            guard let url = model.client.url(path: "/api/cameras/\(camera.id.uuidString)/stream/stream.m3u8") else { return }
            player = AVPlayer(url: url)
            player?.play()
        }
        .onDisappear {
            player?.pause()
            player = nil
        }
    }
}
