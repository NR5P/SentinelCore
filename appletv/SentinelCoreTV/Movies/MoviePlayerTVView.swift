import AVKit
import SwiftUI

// Full-screen film or episode: the system AVPlayerViewController, so the Siri Remote gets the
// real transport bar, the info panel (title, "S1 · E3 · Title", overview, poster) and the
// subtitle/audio menus - HLS sessions carry the text subtitles as renditions, which is where that
// menu finds them. Menu (and Up) first put the transport bar away; Menu with nothing showing closes
// the player, reporting where the viewer got to.
struct MoviePlayerTVView: View {
    @Environment(\.dismiss) private var dismiss
    @Environment(\.scenePhase) private var scenePhase
    @StateObject private var controller: MoviePlaybackController
    @State private var transportBar = TVTransportBar()

    init(request: MoviePlayRequest, client: SentinelClient) {
        _controller = StateObject(wrappedValue: MoviePlaybackController(client: client, request: request))
    }

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            if let player = controller.player {
                TVPlayerViewController(player: player, transportBar: transportBar)
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
        // The embedded player doesn't take Menu itself - it lands here even with the transport bar
        // up - so put the bar away first, the way the TV app does, and only then close.
        .onExitCommand {
            if !transportBar.hideIfVisible() { dismiss() }
        }
    }
}

#if DEBUG
// A bare AVPlayer on a URL, none of the controller's logic - for telling a player bug from a
// stream or simulator one. `-debugPlayURL <url>` as a launch argument.
struct DebugRawPlayerView: View {
    let url: URL
    @State private var player: AVPlayer?
    @State private var transportBar = TVTransportBar()

    var body: some View {
        ZStack {
            if let player { TVPlayerViewController(player: player, transportBar: transportBar).ignoresSafeArea() }
        }
        .onAppear {
            let player = AVPlayer(url: url)
            self.player = player
            player.play()
        }
    }
}
#endif

// Tracks whether the player's transport bar is on screen, and puts it away on Menu or Up.
@MainActor
final class TVTransportBar: NSObject, @preconcurrency AVPlayerViewControllerDelegate {
    private(set) var isVisible = false
    fileprivate weak var controller: AVPlayerViewController?

    /// Hides the bar if it's showing; false when there was nothing to hide.
    func hideIfVisible() -> Bool {
        guard isVisible, let controller else { return false }
        isVisible = false
        // No API hides it. Toggling showsPlaybackControls or playbackControlsIncludeTransportBar
        // brings it straight back; handing the player over again is what clears it, without a
        // stall or losing the subtitle/audio picks (those live on the item).
        let player = controller.player
        controller.player = nil
        controller.player = player
        return true
    }

    @objc fileprivate func upPressed() {
        _ = hideIfVisible()
    }

    func playerViewController(_ playerViewController: AVPlayerViewController,
                              willTransitionToVisibilityOfTransportBar visible: Bool,
                              with coordinator: any AVPlayerViewControllerAnimationCoordinator) {
        isVisible = visible
    }
}

extension TVTransportBar: @preconcurrency UIGestureRecognizerDelegate {
    // Up only belongs to us while the bar is showing; otherwise the player keeps it.
    func gestureRecognizerShouldBegin(_ gestureRecognizer: UIGestureRecognizer) -> Bool {
        isVisible
    }
}

private struct TVPlayerViewController: UIViewControllerRepresentable {
    let player: AVPlayer
    let transportBar: TVTransportBar

    func makeUIViewController(context: Context) -> AVPlayerViewController {
        let controller = AVPlayerViewController()
        controller.player = player
        // Switch the TV to the film's frame rate and dynamic range when "Match Content" is on.
        controller.appliesPreferredDisplayCriteriaAutomatically = true
        controller.delegate = transportBar
        transportBar.controller = controller
        let up = UITapGestureRecognizer(target: transportBar, action: #selector(TVTransportBar.upPressed))
        up.allowedPressTypes = [NSNumber(value: UIPress.PressType.upArrow.rawValue)]
        up.delegate = transportBar
        controller.view.addGestureRecognizer(up)
        return controller
    }

    func updateUIViewController(_ controller: AVPlayerViewController, context: Context) {
        if controller.player !== player { controller.player = player }
    }
}
