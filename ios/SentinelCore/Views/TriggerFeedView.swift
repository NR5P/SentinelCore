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
        .navigationTitle("Triggers")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.large)
        #endif
        .navigationDestination(for: TriggerEvent.self) { TriggerDetailView(trigger: $0) }
        .refreshable { await model.connect() }
        .toolbar {
            ToolbarItem {
                Button { Task { await model.connect(resetNetworkSession: true) } } label: {
                    Label("Refresh", systemImage: "arrow.clockwise")
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

struct TriggerCard: View {
    @EnvironmentObject private var model: AppModel
    let trigger: TriggerEvent

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            SentinelRemoteImage(url: model.client.url(path: "/api/triggers/\(trigger.id.uuidString)/image"))
            .frame(height: 165)
            .frame(maxWidth: .infinity)
            .clipShape(RoundedRectangle(cornerRadius: 8))
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
                .font(.caption)
            Text("Motion area \(trigger.triggerAreaFraction, format: .percent.precision(.fractionLength(1)))")
                .foregroundStyle(.secondary)
                .font(.caption2)
        }
        .sentinelCard()
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
            .frame(maxWidth: 900)
            .frame(maxWidth: .infinity)
            .padding()
        }
        .navigationTitle(trigger.cameraName)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
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
