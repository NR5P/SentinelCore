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
        .accessibilityLabel("Security camera image")
    }
}
