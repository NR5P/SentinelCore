import SwiftUI

// The "UNLICENSED" mark drawn across camera images and live video when the server has no valid
// licence file. Twin of the dashboard's body.unlicensed rules in wwwroot/styles.css - same words,
// same amber, same rotation - so a customer who sees it in one place recognises it in the others.
//
// Deliberately cosmetic, matching the "nag, never gate" rule everywhere else licensing touches:
// allowsHitTesting(false) means it never intercepts a tap, and nothing about it stops an image or
// a stream from loading, playing, or being opened full screen.

// Read through the environment rather than an @EnvironmentObject so that the low-level views this
// sits on - SentinelRemoteImage in particular - stay usable anywhere, including in previews, with
// no AppModel injected. The default is "licensed", which is also the right answer when the check
// itself fails: this is visibility, not enforcement, and falsely accusing a paying customer is a
// worse failure than missing a nag.
private struct IsLicensedKey: EnvironmentKey {
    static let defaultValue = true
}

extension EnvironmentValues {
    var isLicensed: Bool {
        get { self[IsLicensedKey.self] }
        set { self[IsLicensedKey.self] = newValue }
    }
}

struct UnlicensedWatermark: ViewModifier {
    @Environment(\.isLicensed) private var isLicensed

    func body(content: Content) -> some View {
        content.overlay {
            if !isLicensed {
                GeometryReader { proxy in
                    Text("UNLICENSED")
                        .font(.system(size: watermarkFontSize(for: proxy.size), weight: .bold))
                        .tracking(watermarkFontSize(for: proxy.size) * 0.2)
                        .foregroundStyle(SentinelTheme.amber)
                        .opacity(0.4)
                        .shadow(color: .black.opacity(0.7), radius: 4)
                        .lineLimit(1)
                        // The mark has to stay legible on a thumbnail without overflowing it, and
                        // fixedSize keeps the rotation from being measured against a squeezed
                        // frame - it lays out at its natural width first, then rotates.
                        .fixedSize()
                        .rotationEffect(.degrees(-16))
                        .frame(width: proxy.size.width, height: proxy.size.height)
                }
                .allowsHitTesting(false)
                .accessibilityHidden(true)
            }
        }
    }

    // Scales with the tile the way the dashboard's clamp() does, so it reads the same on a
    // thumbnail in a feed and on a full-screen player.
    private func watermarkFontSize(for size: CGSize) -> CGFloat {
        min(max(size.width * 0.11, 11), 48)
    }
}

extension View {
    /// Draws the UNLICENSED mark over this view when the server has no valid licence.
    /// Purely informational - it never blocks the content underneath.
    func unlicensedWatermark() -> some View { modifier(UnlicensedWatermark()) }
}
