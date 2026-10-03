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

    init(request: MoviePlayRequest, client: SentinelClient) {
        _controller = StateObject(wrappedValue: MoviePlaybackController(client: client, request: request))
    }

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            if let player = controller.player {
                TVPlayerViewController(player: player, onClose: { dismiss() })
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
        // While the player is up it owns Menu (see TVPlayerViewController); this is only for the
        // loading and error screens.
        .onExitCommand { if controller.player == nil { dismiss() } }
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
            if let player { TVPlayerViewController(player: player, onClose: {}).ignoresSafeArea() }
        }
        .onAppear {
            let player = AVPlayer(url: url)
            self.player = player
            player.play()
        }
    }
}
#endif

// Embedded AVPlayerViewController hands Menu straight up to SwiftUI, so a viewer who pressed Menu
// to put the progress bar away got thrown out of the film. Menu and Up are caught on the player's
// own view instead: with the transport bar showing they only hide it; Menu with it hidden closes.
private struct TVPlayerViewController: UIViewControllerRepresentable {
    let player: AVPlayer
    let onClose: () -> Void

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeUIViewController(context: Context) -> AVPlayerViewController {
        let controller = AVPlayerViewController()
        controller.player = player
        // Switch the TV to the film's frame rate and dynamic range when "Match Content" is on.
        controller.appliesPreferredDisplayCriteriaAutomatically = true
        controller.delegate = context.coordinator
        context.coordinator.attach(to: controller)
        context.coordinator.onClose = onClose
        return controller
    }

    func updateUIViewController(_ controller: AVPlayerViewController, context: Context) {
        if controller.player !== player { controller.player = player }
        context.coordinator.onClose = onClose
    }

    @MainActor
    final class Coordinator: NSObject, @preconcurrency AVPlayerViewControllerDelegate {
        var onClose: () -> Void = {}
        private weak var controller: AVPlayerViewController?
        private var transportBarVisible = false
        private var closed = false

        func attach(to controller: AVPlayerViewController) {
            self.controller = controller
            let menu = UITapGestureRecognizer(target: self, action: #selector(menuPressed))
            menu.allowedPressTypes = [NSNumber(value: UIPress.PressType.menu.rawValue)]
            controller.view.addGestureRecognizer(menu)
            let up = UITapGestureRecognizer(target: self, action: #selector(upPressed))
            up.allowedPressTypes = [NSNumber(value: UIPress.PressType.upArrow.rawValue)]
            // Only steal Up while the bar is showing; otherwise the player keeps it.
            up.delegate = self
            controller.view.addGestureRecognizer(up)
        }

        @objc private func menuPressed() {
            if transportBarVisible { hideTransportBar() } else { close() }
        }

        @objc private func upPressed() {
            hideTransportBar()
        }

        // No API hides the bar directly; turning the controls off and straight back on does, and
        // the next click or swipe brings it back as usual.
        private func hideTransportBar() {
            guard let controller else { return }
            transportBarVisible = false
            controller.showsPlaybackControls = false
            controller.showsPlaybackControls = true
        }

        private func close() {
            guard !closed else { return }
            closed = true
            onClose()
        }

        func playerViewController(_ playerViewController: AVPlayerViewController,
                                  willTransitionToVisibilityOfTransportBar visible: Bool,
                                  with coordinator: any AVPlayerViewControllerAnimationCoordinator) {
            transportBarVisible = visible
        }

        // In case the player's own Menu handling gets there first: it only asks once the bar is
        // already away, so closing here agrees with menuPressed.
        func playerViewControllerShouldDismiss(_ playerViewController: AVPlayerViewController) -> Bool {
            close()
            return false
        }
    }
}

extension TVPlayerViewController.Coordinator: @preconcurrency UIGestureRecognizerDelegate {
    func gestureRecognizerShouldBegin(_ gestureRecognizer: UIGestureRecognizer) -> Bool {
        let isUp = (gestureRecognizer as? UITapGestureRecognizer)?.allowedPressTypes
            .contains(NSNumber(value: UIPress.PressType.upArrow.rawValue)) ?? false
        return isUp ? transportBarVisible : true
    }
}
