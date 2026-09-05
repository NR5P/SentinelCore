import SwiftUI

struct SentinelRemoteImage: View {
    let url: URL?
    var contentMode: ContentMode = .fill

    var body: some View {
        Group {
            if let url {
                AsyncImage(url: url) { phase in
                    switch phase {
                    case let .success(image):
                        image
                            .resizable()
                            .aspectRatio(contentMode: contentMode)
                    case .failure:
                        placeholder(systemImage: "photo.badge.exclamationmark")
                    case .empty:
                        ZStack {
                            Rectangle().fill(.quaternary)
                            ProgressView().controlSize(.small)
                        }
                    @unknown default:
                        Rectangle().fill(.quaternary)
                    }
                }
            } else {
                // No address at all - an event whose snapshot was never saved, or has since been
                // aged out by retention. AsyncImage given a nil URL never leaves its .empty phase,
                // so this used to spin forever, which reads as a picture that is still on its way
                // rather than one that does not exist. The dashboard says "no image" for the same
                // row; this is that.
                placeholder(systemImage: "photo")
            }
        }
        // Every camera still on any screen in this app goes through here - the alert feed, alert
        // detail, the alert takeover, the trigger feed - so this is the one place the mark needs
        // applying to cover all of them.
        .unlicensedWatermark()
        .accessibilityLabel(url == nil ? "No camera image" : "Security camera image")
    }

    private func placeholder(systemImage: String) -> some View {
        ZStack {
            Rectangle().fill(.quaternary)
            Image(systemName: systemImage)
                .font(.title2)
                .foregroundStyle(.secondary)
        }
    }
}
