import AVKit
import SwiftUI

// The "interrupt while watching TV" feature from the plan. Presented full-screen by RootView
// whenever AppModel.alertTakeover is set - either immediately (push arrived while this app is
// already foregrounded, via PushNotificationCoordinator's willPresent handling) or after the
// user selects the system push banner while the app was backgrounded. tvOS can't draw over
// another app's video, so this only reaches the screen in those two cases - see the plan's
// "platform reality check" for why that's the ceiling here.
struct AlertTakeoverView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss
    let event: SecurityEvent
    @StateObject private var stream = LiveStreamPlayer(muted: false)

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()

            // The event's own snapshot stands in until the live feed has a frame, so the
            // takeover shows what triggered it instantly rather than opening on black.
            if !stream.isVideoReady {
                SentinelRemoteImage(url: imageURL, contentMode: .fit)
                    .ignoresSafeArea()
            }
            LiveVideoView(stream: stream)
                .ignoresSafeArea()
                .opacity(stream.isVideoReady ? 1 : 0)

            VStack {
                banner
                Spacer()
                dismissHint
            }
            .padding(50)
        }
        .task { startLivePlaybackIfAvailable() }
        .onDisappear { stream.stop() }
        // Menu button on the Siri Remote dismisses the takeover, same as backing out of any
        // other tvOS full-screen presentation.
        .onExitCommand { dismiss() }
    }

    private var banner: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 10) {
                Image(systemName: "exclamationmark.triangle.fill")
                Text("AI VERIFIED ALERT")
            }
            .font(.title.bold())
            .foregroundStyle(SentinelTheme.red)

            Text(event.camera)
                .font(.system(size: 44, weight: .bold))
                .foregroundStyle(.white)

            Text(event.summary.isEmpty ? "New security camera activity" : event.summary)
                .font(.title3)
                .foregroundStyle(.white.opacity(0.9))

            HStack(spacing: 16) {
                StatusChip(
                    event.eventType.replacingOccurrences(of: "_", with: " ").uppercased(),
                    color: SentinelTheme.cyan
                )
                StatusChip(
                    event.confidence.formatted(.percent.precision(.fractionLength(0))) + " confidence",
                    color: SentinelTheme.amber
                )
            }
        }
        .padding(28)
        .background(.black.opacity(0.55), in: RoundedRectangle(cornerRadius: 18))
        .frame(maxWidth: 900, alignment: .leading)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var dismissHint: some View {
        HStack(spacing: 8) {
            Image(systemName: "arrowshape.turn.up.left.fill")
            Text("Press Menu to dismiss")
        }
        .font(.callout)
        .foregroundStyle(.white.opacity(0.7))
        .padding(16)
        .background(.black.opacity(0.5), in: Capsule())
    }

    // The takeover is the one screen where being seconds behind is worst: it appears *because*
    // something just happened, and the whole point is to see it. So it takes the fast rung too.
    private func startLivePlaybackIfAvailable() {
        guard let camera = model.cameras.first(where: { $0.name == event.camera }),
              let hls = model.client.url(path: "/api/cameras/\(camera.id.uuidString)/stream/stream.m3u8")
        else { return }
        stream.start(
            LiveStreamSource(
                cameraId: camera.id,
                mse: model.client.webSocketURL(path: "/api/cameras/\(camera.id.uuidString)/mse"),
                hls: hls,
                wantsAudio: camera.includeAudio == true,
                isDevicePublished: camera.isDevicePublished),
            client: model.client)
    }

    private var imageURL: URL? {
        event.hasImage ? model.client.url(path: "/api/events/\(event.id.uuidString)/image") : nil
    }
}
