import AVKit
import Combine
import SwiftUI

// One event, opened from the merged Events list. What it can show depends on what exists for that
// moment rather than on which screen you came from - the clip if one was recorded, the AI's read
// of the frame if anything analysed it, and the motion facts when a trigger is behind it.
//
// This replaced TriggerDetailView, which could only ever show the trigger half, and sat behind a
// separate Triggers tab. See ActivityEndpoints for the join that makes one screen possible.
struct ActivityDetailView: View {
    @EnvironmentObject private var model: AppModel
    let event: ActivityItem
    @State private var player: AVPlayer?
    @State private var playerItem: AVPlayerItem?
    // Starts at the common 16:9 guess so the layout isn't empty before the clip's metadata
    // loads, then snaps to the clip's actual size once presentationSize reports it - cameras
    // don't all record in 16:9, and forcing that ratio on a taller/narrower clip just adds a
    // second layer of letterboxing on top of AVKit's own, wasting space that could be video.
    @State private var videoAspectRatio: CGFloat = 16 / 9

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                media

                if event.isAnalyzed {
                    analysisCard
                }
                factsCard
            }
            .frame(maxWidth: 900)
            .frame(maxWidth: .infinity)
            .padding()
        }
        .navigationTitle(event.camera)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
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
                    .background(.black)
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                    .onReceive(playerItem.publisher(for: \.presentationSize)) { size in
                        guard size.width > 0, size.height > 0 else { return }
                        videoAspectRatio = size.width / size.height
                    }
            } else {
                ProgressView("Preparing clip…")
                    .frame(maxWidth: .infinity, minHeight: 240)
            }
        } else {
            SentinelRemoteImage(url: imageURL, contentMode: .fit)
                .frame(maxWidth: .infinity, minHeight: 240)
                .background(.black)
                .clipShape(RoundedRectangle(cornerRadius: 8))
        }
    }

    private var analysisCard: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 8) {
                StatusChip(
                    (event.eventType ?? "unknown").replacingOccurrences(of: "_", with: " ").uppercased(),
                    color: SentinelTheme.cyan
                )
                if event.notified {
                    StatusChip("NOTIFIED", systemImage: "bell.fill", color: SentinelTheme.green)
                }
                Spacer()
                if let confidence = event.confidence {
                    Text(confidence, format: .percent.precision(.fractionLength(0)))
                        .foregroundStyle(.secondary)
                }
            }
            Text(event.summary?.isEmpty == false ? event.summary! : "No description")
                .font(.title3)
            if event.notified {
                Label(
                    event.notificationReason?.isEmpty == false ? event.notificationReason! : "Notification sent",
                    systemImage: "bell.fill"
                )
                .foregroundStyle(SentinelTheme.green)
            }
            if let reason = event.packageTheftReason {
                Label(reason, systemImage: "shippingbox.fill")
                    .foregroundStyle(SentinelTheme.amber)
            }
            if let linked = event.linkedCameras {
                Label("Also recorded: \(linked)", systemImage: "link")
                    .foregroundStyle(.secondary)
                    .font(.caption)
            }
        }
        .sentinelCard()
    }

    private var factsCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            LabeledContent("Camera", value: event.camera)
            if let zoneName = event.zoneName {
                LabeledContent("Zone", value: zoneName)
            }
            LabeledContent("Time", value: event.occurredAt.formatted(date: .complete, time: .standard))
            if let area = event.triggerAreaFraction {
                LabeledContent("Motion area", value: area.formatted(.percent.precision(.fractionLength(1))))
            }
            LabeledContent("Media", value: event.hasClip ? "Video clip" : "Snapshot")
            // Only worth stating when it isn't the ordinary case. "Confirmed" on every card would
            // be noise; the other two explain why a row is only visible with Show hidden on.
            if let confirmation = event.confirmation, confirmation != "confirmed" {
                LabeledContent(
                    "AI verdict",
                    value: ActivityVerdict.detailLabel(for: confirmation)
                )
            }
            if !event.isAnalyzed {
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
