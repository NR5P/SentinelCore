import AVKit
import SwiftUI

// tvOS port of ios/SentinelCore/Views/ActivityDetailView.swift: one event, showing whatever
// exists for that moment - the clip if one was recorded, the AI's read of the frame if anything
// analysed it, and the motion facts when a trigger is behind it.
//
// Replaced TriggerDetailView, which could only ever show the trigger half and sat behind its own
// Triggers tab. AlertDetailView stays for the push/takeover path, which starts from an alert
// rather than from this list.
struct ActivityDetailView: View {
    @EnvironmentObject private var model: AppModel
    let event: ActivityItem
    @State private var player: AVPlayer?
    @State private var playerItem: AVPlayerItem?
    @State private var videoAspectRatio: CGFloat = 16 / 9

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                media

                if event.isAnalyzed {
                    analysisCard
                } else if event.isRecording {
                    recordingCard
                }
                factsCard
            }
            .padding(60)
        }
        .navigationTitle(event.camera)
        .task {
            guard let clipUrl = event.clipUrl, let url = model.client.url(path: clipUrl) else { return }
            let item = AVPlayerItem(url: url)
            let player = AVPlayer(playerItem: item)
            self.playerItem = item
            self.player = player
            player.play()
        }
        .onDisappear {
            player?.pause()
            player = nil
            playerItem = nil
        }
    }

    @ViewBuilder private var media: some View {
        if event.hasClip {
            if let player, let playerItem {
                VideoPlayer(player: player)
                    .aspectRatio(videoAspectRatio, contentMode: .fit)
                    .frame(maxHeight: SentinelTheme.detailMediaMaxHeight)
                    .frame(maxWidth: .infinity)
                    .background(.black)
                    .clipShape(RoundedRectangle(cornerRadius: 12))
                    .onReceive(playerItem.publisher(for: \.presentationSize)) { size in
                        guard size.width > 0, size.height > 0 else { return }
                        videoAspectRatio = size.width / size.height
                    }
            } else {
                ProgressView("Preparing clip…")
                    .frame(maxWidth: .infinity, minHeight: 360)
            }
        } else {
            SentinelRemoteImage(url: imageURL, contentMode: .fit)
                .frame(maxWidth: .infinity, minHeight: 360, maxHeight: SentinelTheme.detailMediaMaxHeight)
                .background(.black)
                .clipShape(RoundedRectangle(cornerRadius: 12))
        }
    }

    private var analysisCard: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(event.summary?.isEmpty == false ? event.summary! : "No description")
                .font(.title3)
            HStack(spacing: 16) {
                Text((event.eventType ?? "unknown").replacingOccurrences(of: "_", with: " ").capitalized)
                    .foregroundStyle(SentinelTheme.cyan)
                if event.notified {
                    Label("Notified", systemImage: "bell.fill").foregroundStyle(SentinelTheme.green)
                }
                Spacer()
                if let confidence = event.confidence {
                    Text(confidence, format: .percent.precision(.fractionLength(0)))
                        .foregroundStyle(.secondary)
                }
            }
            if event.notified, let reason = event.notificationReason, !reason.isEmpty {
                Text(reason).foregroundStyle(SentinelTheme.green)
            }
            if let reason = event.packageTheftReason {
                Label(reason, systemImage: "shippingbox.fill").foregroundStyle(SentinelTheme.amber)
            }
            if let linked = event.linkedCameras {
                Label("Also recorded: \(linked)", systemImage: "link")
                    .foregroundStyle(.secondary)
                    .font(.caption)
            }
        }
        .sentinelCard()
    }

    // The recording's own line - which kind, how long - where the AI's description would sit on an
    // analysed row. There is no AI here: this is footage somebody asked for.
    private var recordingCard: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 8) {
                OriginBadge(event: event)
                if let length = event.durationLabel {
                    Text(length).foregroundStyle(.secondary)
                }
                Spacer()
            }
            Text(event.summary?.isEmpty == false ? event.summary! : "Recording")
                .font(.title3)
        }
        .sentinelCard()
    }

    private var factsCard: some View {
        VStack(alignment: .leading, spacing: 12) {
            LabeledContent("Camera", value: event.camera)
            if let zoneName = event.zoneName {
                LabeledContent("Zone", value: zoneName)
            }
            LabeledContent("Time", value: event.occurredAt.formatted(date: .complete, time: .standard))
            if let area = event.triggerAreaFraction {
                LabeledContent("Motion area", value: area.formatted(.percent.precision(.fractionLength(1))))
            }
            LabeledContent("Media", value: event.hasClip ? "Video clip" : "Snapshot")
            LabeledContent("Source", value: event.originDescription)
            if let length = event.durationLabel {
                LabeledContent("Length", value: length)
            }
            // Only worth stating when it isn't the ordinary case. "Confirmed" on every card would
            // be noise; the other two explain why a row is only visible with Show hidden on.
            if let confirmation = event.confirmation, confirmation != "confirmed" {
                LabeledContent(
                    "AI verdict",
                    value: ActivityVerdict.detailLabel(for: confirmation)
                )
            }
            if !event.isAnalyzed && !event.isRecording {
                Text("Nothing analysed this frame - this camera doesn't send to AI analysis.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .sentinelCard()
    }

    private var imageURL: URL? {
        event.imageUrl.flatMap { model.client.url(path: $0) }
    }
}
