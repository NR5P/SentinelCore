import SwiftUI
#if os(iOS)
import UIKit
#elseif os(macOS)
import AppKit
#endif

// Brand accent colors only - everything else (backgrounds, primary/secondary text, card
// surfaces) now comes from the system (.primary, .secondary, Material) so the app follows the
// user's light/dark appearance and matches other native apps, with Sentinel's green/cyan kept
// as an accent layered on top rather than a full-screen reskin.
enum SentinelTheme {
    static let green = Color(light: .init(red: 0.02, green: 0.55, blue: 0.32),
                              dark: .init(red: 0.22, green: 1.0, blue: 0.61))
    static let greenDim = Color(light: .init(red: 0.04, green: 0.42, blue: 0.25),
                                 dark: .init(red: 0.12, green: 0.61, blue: 0.38))
    static let cyan = Color(light: .init(red: 0.0, green: 0.55, blue: 0.62),
                             dark: .init(red: 0.29, green: 0.91, blue: 1.0))
    static let amber = Color(light: .init(red: 0.62, green: 0.42, blue: 0.0),
                              dark: .init(red: 1.0, green: 0.71, blue: 0.27))
    static let red = Color(light: .init(red: 0.75, green: 0.11, blue: 0.17),
                            dark: .init(red: 1.0, green: 0.30, blue: 0.37))
}

// Native Material-based card surface, replacing the old hand-painted panel/border combo -
// adapts to light/dark (and to context, e.g. sitting over a photo) automatically.
struct SentinelCard: ViewModifier {
    func body(content: Content) -> some View {
        content
            .padding()
            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
            .overlay {
                RoundedRectangle(cornerRadius: 12)
                    .stroke(.primary.opacity(0.08))
            }
    }
}

extension View {
    func sentinelCard() -> some View { modifier(SentinelCard()) }
}

extension Color {
    /// A color that resolves differently per light/dark appearance, defined here in code
    /// (rather than the asset catalog) so the whole brand palette lives in one file.
    init(light: Color, dark: Color) {
        #if os(iOS)
        self.init(UIColor { $0.userInterfaceStyle == .dark ? UIColor(dark) : UIColor(light) })
        #elseif os(macOS)
        self.init(NSColor(name: nil) { appearance in
            appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua ? NSColor(dark) : NSColor(light)
        })
        #else
        self = light
        #endif
    }
}
