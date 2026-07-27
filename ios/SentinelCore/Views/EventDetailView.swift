import SwiftUI

struct EventDetailView: View {
    @EnvironmentObject private var model: AppModel
    let event: SecurityEvent

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                AsyncImage(url: imageURL) { phase in
                    switch phase {
                    case let .success(image):
                        image.resizable().scaledToFit()
                    case .failure:
                        ContentUnavailableView("Image unavailable", systemImage: "photo.badge.exclamationmark")
                    default:
                        ProgressView().frame(maxWidth: .infinity, minHeight: 240)
                    }
                }
                .frame(maxWidth: .infinity)
                .background(.black)
                .clipShape(RoundedRectangle(cornerRadius: 8))

                VStack(alignment: .leading, spacing: 12) {
                    LabeledContent("CAMERA", value: event.camera)
                    LabeledContent("TIME", value: event.occurredAt.formatted(date: .abbreviated, time: .standard))
                    LabeledContent("TYPE", value: event.eventType.replacingOccurrences(of: "_", with: " "))
                    LabeledContent("CONFIDENCE", value: event.confidence.formatted(.percent.precision(.fractionLength(0))))
                    LabeledContent("STATUS", value: event.status)
                    Divider().overlay(SentinelTheme.greenDim)
                    Text(event.summary).font(.title3).foregroundStyle(SentinelTheme.text)
                    if event.notified {
                        Label(event.notificationReason.isEmpty ? "Notification sent" : event.notificationReason,
                              systemImage: "bell.fill")
                            .foregroundStyle(SentinelTheme.green)
                    }
                }
                .sentinelPanel()
            }
            .padding()
            .frame(maxWidth: 900)
            .frame(maxWidth: .infinity)
        }
        .background(SentinelTheme.background)
        .navigationTitle(event.camera)
    }

    private var imageURL: URL? {
        event.hasImage ? model.client.url(path: "/api/events/\(event.id.uuidString)/image") : nil
    }
}
