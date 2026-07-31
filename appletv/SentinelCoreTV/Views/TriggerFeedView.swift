import AVKit
import SwiftUI

// tvOS port of ios/SentinelCore/Views/TriggerFeedView.swift.
struct TriggerFeedView: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        ScrollView {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 380), spacing: 36)], spacing: 36) {
                ForEach(model.triggers) { trigger in
                    NavigationLink(value: trigger) {
                        TriggerCard(trigger: trigger)
                    }
                    .buttonStyle(.card)
                }
            }
            .padding(.horizontal, 60)
            .padding(.bottom, 60)
        }
        .navigationDestination(for: TriggerEvent.self) { TriggerDetailView(trigger: $0) }
        .toolbar {
            ToolbarItem {
                Button { Task { await model.connect(resetNetworkSession: true) } } label: {
                    Image(systemName: "arrow.clockwise").accessibilityLabel("Refresh")
                }
                .disabled(model.isLoading)
            }
        }
        .overlay {
            if model.triggers.isEmpty && !model.isLoading {
                ContentUnavailableView(
                    "No motion triggers",
                    systemImage: "wave.3.right",
                    description: Text("Motion detected inside configured camera zones will appear here.")
                )
                .foregroundStyle(.secondary)
            } else if model.triggers.isEmpty {
                ProgressView("Loading triggers…")
            }
        }
    }
}

private struct TriggerCard: View {
    @EnvironmentObject private var model: AppModel
    let trigger: TriggerEvent

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            SentinelRemoteImage(url: model.client.url(path: "/api/triggers/\(trigger.id.uuidString)/image"))
                .frame(height: 200)
                .frame(maxWidth: .infinity)
                .clipShape(RoundedRectangle(cornerRadius: 10))
                .clipped()
            HStack {
                Text(trigger.cameraName).foregroundStyle(SentinelTheme.green).bold()
                Spacer()
                if trigger.hasClip {
                    Label("Clip", systemImage: "play.fill").foregroundStyle(SentinelTheme.cyan)
                }
            }
            Text(trigger.occurredAt.formatted(date: .abbreviated, time: .shortened))
                .foregroundStyle(.secondary)
            Text("Motion area \(trigger.triggerAreaFraction, format: .percent.precision(.fractionLength(1)))")
                .foregroundStyle(.secondary)
                .font(.caption)
        }
        .padding(16)
    }
}

private struct TriggerDetailView: View {
    @EnvironmentObject private var model: AppModel
    let trigger: TriggerEvent
    @State private var player: AVPlayer?
    @State private var playerItem: AVPlayerItem?
    @State private var videoAspectRatio: CGFloat = 16 / 9

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                if trigger.hasClip {
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
                    SentinelRemoteImage(
                        url: model.client.url(path: "/api/triggers/\(trigger.id.uuidString)/image"),
                        contentMode: .fit
                    )
                    .frame(maxWidth: .infinity, minHeight: 360, maxHeight: SentinelTheme.detailMediaMaxHeight)
                    .background(.black)
                    .clipShape(RoundedRectangle(cornerRadius: 12))
                }

                VStack(alignment: .leading, spacing: 12) {
                    LabeledContent("Camera", value: trigger.cameraName)
                    LabeledContent("Time", value: trigger.occurredAt.formatted(date: .complete, time: .standard))
                    LabeledContent(
                        "Motion area",
                        value: trigger.triggerAreaFraction.formatted(.percent.precision(.fractionLength(1)))
                    )
                    LabeledContent("Media", value: trigger.hasClip ? "Video clip" : "Snapshot")
                }
                .sentinelCard()
            }
            .padding(60)
        }
        .navigationTitle(trigger.cameraName)
        .task {
            guard trigger.hasClip,
                  let url = model.client.url(path: "/api/triggers/\(trigger.id.uuidString)/clip")
            else { return }
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
}
