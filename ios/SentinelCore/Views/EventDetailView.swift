import SwiftUI

struct EventDetailView: View {
    @EnvironmentObject private var model: AppModel
    let event: SecurityEvent

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                SentinelRemoteImage(url: imageURL, contentMode: .fit)
                .frame(maxWidth: .infinity)
                .frame(minHeight: 240)
                .background(.black)
                .clipShape(RoundedRectangle(cornerRadius: 8))

                VStack(alignment: .leading, spacing: 12) {
                    HStack(spacing: 8) {
                        StatusChip(
                            event.eventType.replacingOccurrences(of: "_", with: " ").uppercased(),
                            color: SentinelTheme.cyan
                        )
                        if event.notified {
                            StatusChip("NOTIFIED", systemImage: "bell.fill", color: SentinelTheme.green)
                        }
                        Spacer()
                        Text(event.confidence, format: .percent.precision(.fractionLength(0)))
                            .foregroundStyle(.secondary)
                    }
                    LabeledContent("Camera", value: event.camera)
                    LabeledContent("Time", value: event.occurredAt.formatted(date: .complete, time: .standard))
                    LabeledContent("Status", value: event.status.capitalized)
                    Divider()
                    Text(event.summary).font(.title3)
                    if event.notified {
                        Label(event.notificationReason.isEmpty ? "Notification sent" : event.notificationReason,
                              systemImage: "bell.fill")
                            .foregroundStyle(SentinelTheme.green)
                    }
                }
                .sentinelCard()
            }
            .padding()
            .frame(maxWidth: 900)
            .frame(maxWidth: .infinity)
        }
        .navigationTitle(event.camera)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
    }

    private var imageURL: URL? {
        event.hasImage ? model.client.url(path: "/api/events/\(event.id.uuidString)/image") : nil
    }
}

struct StatusChip: View {
    let text: String
    let systemImage: String?
    let color: Color

    init(_ text: String, systemImage: String? = nil, color: Color) {
        self.text = text
        self.systemImage = systemImage
        self.color = color
    }

    var body: some View {
        HStack(spacing: 5) {
            if let systemImage { Image(systemName: systemImage) }
            Text(text)
        }
        .font(.caption2.bold())
        .foregroundStyle(color)
        .padding(.horizontal, 8)
        .padding(.vertical, 5)
        .background(color.opacity(0.1))
        .clipShape(Capsule())
        .overlay { Capsule().stroke(color.opacity(0.4)) }
    }
}
