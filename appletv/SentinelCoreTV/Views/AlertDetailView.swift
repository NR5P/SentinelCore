import SwiftUI

// tvOS port of ios/SentinelCore/Views/EventDetailView.swift, plus a "Watch Live" action - since
// this view can be pushed from any tab's own NavigationStack, live playback opens as a local
// fullScreenCover here rather than a cross-tab navigation push.
struct AlertDetailView: View {
    @EnvironmentObject private var model: AppModel
    let event: SecurityEvent
    @State private var showingLive = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                SentinelRemoteImage(url: imageURL, contentMode: .fit)
                    .frame(maxWidth: .infinity, minHeight: 360)
                    .background(.black)
                    .clipShape(RoundedRectangle(cornerRadius: 12))

                VStack(alignment: .leading, spacing: 16) {
                    HStack(spacing: 10) {
                        StatusChip(
                            event.eventType.replacingOccurrences(of: "_", with: " ").uppercased(),
                            color: SentinelTheme.cyan
                        )
                        if event.notified {
                            StatusChip("AI VERIFIED", systemImage: "bell.fill", color: SentinelTheme.green)
                        }
                        Spacer()
                        Text(event.confidence, format: .percent.precision(.fractionLength(0)))
                            .foregroundStyle(.secondary)
                            .font(.title3)
                    }
                    LabeledContent("Camera", value: event.camera)
                    LabeledContent("Time", value: event.occurredAt.formatted(date: .complete, time: .standard))
                    LabeledContent("Status", value: event.status.capitalized)
                    Divider()
                    Text(event.summary).font(.title3)
                    if event.notified, !event.notificationReason.isEmpty {
                        Label(event.notificationReason, systemImage: "bell.fill")
                            .foregroundStyle(SentinelTheme.green)
                    }

                    if matchingCamera != nil {
                        Button {
                            showingLive = true
                        } label: {
                            Label("Watch Live", systemImage: "video.fill")
                        }
                        .buttonStyle(.borderedProminent)
                    }
                }
                .sentinelCard()
            }
            .padding(60)
        }
        .navigationTitle(event.camera)
        .fullScreenCover(isPresented: $showingLive) {
            if let camera = matchingCamera {
                NavigationStack {
                    LivePlayerView(camera: camera)
                        .toolbar {
                            ToolbarItem(placement: .cancellationAction) {
                                Button("Close") { showingLive = false }
                            }
                        }
                }
            }
        }
    }

    private var matchingCamera: Camera? {
        model.cameras.first { $0.name == event.camera }
    }

    private var imageURL: URL? {
        event.hasImage ? model.client.url(path: "/api/events/\(event.id.uuidString)/image") : nil
    }
}
