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
        .refreshable { try? await model.refreshAll() }
        .overlay {
            if model.triggers.isEmpty {
                ContentUnavailableView("No motion triggers", systemImage: "wave.3.right")
                    .foregroundStyle(SentinelTheme.textDim)
            }
        }
    }
}

struct TriggerCard: View {
    @EnvironmentObject private var model: AppModel
    let trigger: TriggerEvent

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            AsyncImage(url: model.client.url(path: "/api/triggers/\(trigger.id.uuidString)/image")) { phase in
                if case let .success(image) = phase {
                    image.resizable().scaledToFill()
                } else {
                    ProgressView()
                }
            }
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
        }
        .sentinelPanel()
    }
}

struct TriggerDetailView: View {
    @EnvironmentObject private var model: AppModel
    let trigger: TriggerEvent

    var body: some View {
        VStack {
            if trigger.hasClip, let url = model.client.url(path: "/api/triggers/\(trigger.id.uuidString)/clip") {
                VideoPlayer(player: AVPlayer(url: url))
            } else {
                AsyncImage(url: model.client.url(path: "/api/triggers/\(trigger.id.uuidString)/image")) { phase in
                    if case let .success(image) = phase {
                        image.resizable().scaledToFit()
                    } else {
                        ProgressView()
                    }
                }
            }
        }
        .padding()
        .background(SentinelTheme.background)
        .navigationTitle(trigger.cameraName)
    }
}
