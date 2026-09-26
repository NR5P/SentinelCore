import AVKit
import SwiftUI

// Full-screen film or episode: the system AVPlayerViewController, so the Siri Remote gets the
// real transport bar, the info panel (title, "S1 · E3 · Title", overview, poster) and the
// subtitle/audio menus - HLS sessions carry the text subtitles as renditions, which is where that
// menu finds them. Menu closes it, reporting where the viewer got to.
struct MoviePlayerTVView: View {
    @Environment(\.dismiss) private var dismiss
    @Environment(\.scenePhase) private var scenePhase
    @StateObject private var controller: MoviePlaybackController

    init(request: MoviePlayRequest, client: SentinelClient) {
        _controller = StateObject(wrappedValue: MoviePlaybackController(client: client, request: request))
    }

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            if let player = controller.player {
                TVPlayerViewController(player: player)
                    .ignoresSafeArea()
            }
            if let message = controller.errorMessage {
                VStack(spacing: 30) {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .font(.system(size: 70))
                        .foregroundStyle(SentinelTheme.amber)
                    Text("This can't be played right now")
                        .font(.title3.bold())
                    Text(message)
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                        .frame(maxWidth: 1100)
                    Button("Close") { dismiss() }
                }
                .padding(60)
                .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 24))
            } else if controller.isLoading {
                VStack(spacing: 20) {
                    ProgressView()
                    if let title = controller.playback?.title {
                        Text(title).font(.headline)
                    } else {
                        Text("Loading…").foregroundStyle(.secondary)
                    }
                }
            }
        }
        .environment(\.colorScheme, .dark)
        .task { await controller.start() }
        .onDisappear { controller.stop() }
        .onChange(of: controller.isFinished) { _, finished in
            if finished { dismiss() }
        }
        .onChange(of: scenePhase) { _, phase in
            if phase != .active {
                controller.pause()
                controller.reportProgress(force: true)
            }
        }
        // Reaches here only once the player itself has nothing left to close (its transport bar
        // or a menu), so Menu behaves the way it does in the TV app.
        .onExitCommand { dismiss() }
    }
}

#if DEBUG
// A bare AVPlayer on a URL, none of the controller's logic - for telling a player bug from a
// stream or simulator one. `-debugPlayURL <url>` as a launch argument.
struct DebugRawPlayerView: View {
    let url: URL
    @State private var player: AVPlayer?

    var body: some View {
        ZStack {
            if let player { TVPlayerViewController(player: player).ignoresSafeArea() }
        }
        .onAppear {
            let player = AVPlayer(url: url)
            self.player = player
            player.play()
        }
    }
}
#endif

private struct TVPlayerViewController: UIViewControllerRepresentable {
    let player: AVPlayer

    func makeUIViewController(context: Context) -> AVPlayerViewController {
        let controller = AVPlayerViewController()
        controller.player = player
        // Switch the TV to the film's frame rate and dynamic range when "Match Content" is on.
        controller.appliesPreferredDisplayCriteriaAutomatically = true
        return controller
    }

    func updateUIViewController(_ controller: AVPlayerViewController, context: Context) {
        if controller.player !== player { controller.player = player }
    }
}
