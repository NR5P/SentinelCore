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
                    SentinelTheme.panel
                    Image(systemName: "photo.badge.exclamationmark")
                        .font(.title2)
                        .foregroundStyle(SentinelTheme.textDim)
                }
            case .empty:
                ZStack {
                    SentinelTheme.panel
                    ProgressView().controlSize(.small)
                }
            @unknown default:
                SentinelTheme.panel
            }
        }
        .accessibilityLabel("Security camera image")
    }
}
