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
        .refreshable { try? await model.refreshAll() }
        .overlay {
            if model.cameras.isEmpty {
                ContentUnavailableView(
                    "No cameras",
                    systemImage: "video.slash",
                    description: Text("Add cameras in the Sentinel Core dashboard.")
                )
                .foregroundStyle(SentinelTheme.textDim)
            }
        }
    }
}

struct LivePlayerView: View {
    @EnvironmentObject private var model: AppModel
    let camera: Camera
    @State private var player: AVPlayer?

    var body: some View {
        Group {
            if let player {
                VideoPlayer(player: player)
                    .onAppear { player.play() }
                    .onDisappear { player.pause() }
            } else {
                ProgressView("Starting secure stream…")
                    .foregroundStyle(SentinelTheme.text)
            }
        }
        .background(.black)
        .navigationTitle(camera.name)
        .task {
            guard let url = model.client.url(path: "/api/cameras/\(camera.id.uuidString)/stream/stream.m3u8") else { return }
            player = AVPlayer(url: url)
            player?.play()
        }
    }
}
