import SwiftUI

struct SentinelRemoteImage: View {
    let url: URL?
    var contentMode: ContentMode = .fill

    var body: some View {
        AsyncImage(url: url) { phase in
            switch phase {
            case let .success(image):
                image
                    .resizable()
                    .aspectRatio(contentMode: contentMode)
            case .failure:
                ZStack {
                    Rectangle().fill(.quaternary)
                    Image(systemName: "photo.badge.exclamationmark")
                        .font(.title2)
                        .foregroundStyle(.secondary)
                }
            case .empty:
                ZStack {
                    Rectangle().fill(.quaternary)
                    ProgressView().controlSize(.small)
                }
            @unknown default:
                Rectangle().fill(.quaternary)
            }
        }
        // Every camera still on any screen in this app goes through here - the alert feed, alert
        // detail, the alert takeover, the trigger feed - so this is the one place the mark needs
        // applying to cover all of them.
        .unlicensedWatermark()
        .accessibilityLabel("Security camera image")
    }
}
