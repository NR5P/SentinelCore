import AVKit
import SwiftUI

struct TriggerFeedView: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        ScrollView {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 280), spacing: 14)], spacing: 14) {
                ForEach(model.triggers) { trigger in
                    NavigationLink(value: trigger) {
                        TriggerCard(trigger: trigger)
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding()
        }
        .background(SentinelTheme.background)
        .navigationTitle("TRIGGERS")
        .navigationDestination(for: TriggerEvent.self) { TriggerDetailView(trigger: $0) }
        .refreshable { await model.connect() }
        .overlay {
            if model.triggers.isEmpty && !model.isLoading {
                ContentUnavailableView(
                    "No motion triggers",
                    systemImage: "wave.3.right",
                    description: Text("Motion detected inside configured camera zones will appear here.")
                )
                    .foregroundStyle(SentinelTheme.textDim)
            } else if model.triggers.isEmpty {
                ProgressView("Loading triggers…")
            }
        }
    }
}

struct TriggerCard: View {
    @EnvironmentObject private var model: AppModel
    let trigger: TriggerEvent

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            SentinelRemoteImage(url: model.client.url(path: "/api/triggers/\(trigger.id.uuidString)/image"))
            .frame(height: 165)
            .frame(maxWidth: .infinity)
            .background(SentinelTheme.panel)
            .clipShape(RoundedRectangle(cornerRadius: 5))
            .clipped()
            HStack {
                Text(trigger.cameraName).foregroundStyle(SentinelTheme.green).bold()
                Spacer()
                if trigger.hasClip {
                    Label("CLIP", systemImage: "play.fill").foregroundStyle(SentinelTheme.cyan)
                }
            }
            Text(trigger.occurredAt.formatted(date: .abbreviated, time: .shortened))
                .foregroundStyle(SentinelTheme.textDim)
                .font(.caption)
            Text("MOTION AREA \(trigger.triggerAreaFraction, format: .percent.precision(.fractionLength(1)))")
                .foregroundStyle(SentinelTheme.textDim)
                .font(.caption2)
        }
        .sentinelPanel()
    }
}

struct TriggerDetailView: View {
    @EnvironmentObject private var model: AppModel
    let trigger: TriggerEvent
    @State private var player: AVPlayer?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                if trigger.hasClip {
                    if let player {
                        VideoPlayer(player: player)
                            .aspectRatio(16 / 9, contentMode: .fit)
                            .background(.black)
                            .clipShape(RoundedRectangle(cornerRadius: 8))
                    } else {
                        ProgressView("Preparing clip…")
                            .frame(maxWidth: .infinity, minHeight: 240)
                    }
                } else {
                    SentinelRemoteImage(
                        url: model.client.url(path: "/api/triggers/\(trigger.id.uuidString)/image"),
                        contentMode: .fit
                    )
                    .frame(maxWidth: .infinity, minHeight: 240)
                    .background(.black)
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                }

                VStack(alignment: .leading, spacing: 10) {
                    LabeledContent("CAMERA", value: trigger.cameraName)
                    LabeledContent("TIME", value: trigger.occurredAt.formatted(date: .complete, time: .standard))
                    LabeledContent(
                        "MOTION AREA",
                        value: trigger.triggerAreaFraction.formatted(.percent.precision(.fractionLength(1)))
                    )
                    LabeledContent("MEDIA", value: trigger.hasClip ? "VIDEO CLIP" : "SNAPSHOT")
                }
                .sentinelPanel()
            }
            .frame(maxWidth: 900)
            .frame(maxWidth: .infinity)
            .padding()
        }
        .background(SentinelTheme.background)
        .navigationTitle(trigger.cameraName)
        .task {
            guard trigger.hasClip,
                  let url = model.client.url(path: "/api/triggers/\(trigger.id.uuidString)/clip")
            else { return }
            let player = AVPlayer(url: url)
            self.player = player
            player.play()
        }
        .onDisappear {
            player?.pause()
            player = nil
        }
    }
}
