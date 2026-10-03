import AVKit
import SwiftUI

// A film or episode on an iPhone, iPad or Mac. iOS gets the system AVPlayerViewController in a
// full-screen cover (transport bar, AirPlay, and the subtitle/audio menu, which
// is where an HLS session's text subtitles show up). The Mac gets VideoPlayer in a sheet, which
// needs a real minimum size - a macOS sheet otherwise collapses around its content.
// The playing itself - resume, progress, next episode - is MoviePlaybackController's.
struct MoviePlayerView: View {
    @Environment(\.dismiss) private var dismiss
    @Environment(\.scenePhase) private var scenePhase
    @StateObject private var controller: MoviePlaybackController

    init(request: MoviePlayRequest, client: SentinelClient) {
        _controller = StateObject(wrappedValue: MoviePlaybackController(client: client, request: request))
    }

    var body: some View {
        VStack(spacing: 0) {
            topBar
            ZStack {
                Color.black
                if let player = controller.player {
                    #if os(iOS)
                    PhonePlayerViewController(player: player)
                    #else
                    VideoPlayer(player: player)
                    #endif
                }
                if let message = controller.errorMessage {
                    ContentUnavailableView {
                        Label("This can't be played right now", systemImage: "exclamationmark.triangle.fill")
                    } description: {
                        Text(message)
                    } actions: {
                        Button("Close") { dismiss() }
                            .buttonStyle(.sentinelProminent)
                    }
                } else if controller.isLoading {
                    ProgressView()
                        .controlSize(.large)
                        .tint(.white)
                }
            }
        }
        .background(Color.black.ignoresSafeArea())
        .environment(\.colorScheme, .dark)
        #if os(macOS)
        .frame(minWidth: 960, idealWidth: 1280, minHeight: 600, idealHeight: 780)
        #else
        .statusBarHidden()
        #endif
        .task { await controller.start() }
        .onDisappear { controller.stop() }
        .onChange(of: controller.isFinished) { _, finished in
            if finished { dismiss() }
        }
        .onChange(of: scenePhase) { _, phase in
            #if os(iOS)
            // Backgrounded with the player on screen: stop and save the place.
            if phase == .background {
                controller.pause()
                controller.reportProgress(force: true)
            }
            #else
            if phase != .active { controller.reportProgress(force: true) }
            #endif
        }
    }

    // The way out, and what is playing. Its own strip above the picture rather than floating over
    // it, where it would sit on top of the system player's own corner buttons.
    private var topBar: some View {
        HStack(spacing: 14) {
            Button {
                dismiss()
            } label: {
                #if os(iOS)
                Image(systemName: "xmark")
                    .font(.headline)
                    .frame(width: 36, height: 36)
                    .background(.white.opacity(0.15), in: Circle())
                #else
                Text("Done")
                #endif
            }
            .buttonStyle(.plain)
            .keyboardShortcut(.cancelAction)
            .accessibilityLabel("Close")
            VStack(alignment: .leading, spacing: 1) {
                Text(controller.playback?.title ?? "Loading…")
                    .font(.headline)
                    .lineLimit(1)
                if let subtitle = controller.playback?.subtitle, !subtitle.isEmpty {
                    Text(subtitle)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
            }
            Spacer()
        }
        .foregroundStyle(.white)
        .padding(.horizontal, 16)
        .padding(.vertical, 8)
        .background(Color.black)
    }
}

#if os(iOS)
private struct PhonePlayerViewController: UIViewControllerRepresentable {
    let player: AVPlayer

    func makeUIViewController(context: Context) -> AVPlayerViewController {
        let controller = AVPlayerViewController()
        controller.player = player
        // Picture in Picture needs the "audio" background mode, which the app doesn't declare.
        controller.allowsPictureInPicturePlayback = false
        return controller
    }

    func updateUIViewController(_ controller: AVPlayerViewController, context: Context) {
        if controller.player !== player { controller.player = player }
    }
}
#endif

extension View {
    // Full screen on iOS, a sheet on the Mac.
    func moviePlayer(_ request: Binding<MoviePlayRequest?>, client: SentinelClient, onDismiss: @escaping () -> Void) -> some View {
        #if os(iOS)
        fullScreenCover(item: request, onDismiss: onDismiss) { request in
            MoviePlayerView(request: request, client: client)
        }
        #else
        sheet(item: request, onDismiss: onDismiss) { request in
            MoviePlayerView(request: request, client: client)
        }
        #endif
    }
}
