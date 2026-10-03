import SwiftUI
import UIKit

// Brand accent colors only - everything else (backgrounds, primary/secondary text, card
// surfaces) comes from the system so the app follows tvOS's light/dark appearance, with
// Sentinel's green/cyan kept as an accent layered on top. Mirrors ios/SentinelCore/Design/SentinelTheme.swift.
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

    // Ceiling on the clip/snapshot at the top of a detail screen. A detail screen is a ScrollView,
    // so its height proposal is unbounded and a 16:9 image asked to fit the full 1800pt content
    // width sizes itself to ~1010pt - taller than the 1080pt screen has left under the title,
    // which pushes its own bottom edge and everything after it off the screen. It scrolls, but on
    // tvOS scrolling means moving focus, and the media is the one thing up there that isn't
    // focusable, so it reads as simply cut off. This leaves room for the detail card below it.
    static let detailMediaMaxHeight: CGFloat = 560
}

struct SentinelCard: ViewModifier {
    func body(content: Content) -> some View {
        content
            .padding()
            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16))
            .overlay {
                RoundedRectangle(cornerRadius: 16)
                    .stroke(.primary.opacity(0.08))
            }
    }
}

extension View {
    func sentinelCard() -> some View { modifier(SentinelCard()) }
}

// The label colour for a standard (non-card) tvOS button or Menu. Left alone, a button under the
// app's green tint draws its title in the tint - a blank green pill - and a title forced white
// vanishes the moment focus turns the pill white. So: light until focused, dark once it is.
// Goes INSIDE the label (or on the Menu's label): \.isFocused reads the nearest focusable
// ancestor, so on the Button itself, or on a container around several, it never turns true.
struct FocusContrastForeground: ViewModifier {
    @Environment(\.isFocused) private var isFocused
    var unfocused: Color

    func body(content: Content) -> some View {
        content.foregroundStyle(isFocused ? Color.black : unfocused)
    }
}

extension View {
    /// `unfocused` is `.white` over artwork or video; `.primary` on the app's own surfaces, so
    /// it still reads in light mode.
    func focusContrastForeground(unfocused: Color = .primary) -> some View {
        modifier(FocusContrastForeground(unfocused: unfocused))
    }
}

// The app-wide default button style (set in RootView): the platform's own button, with the label
// coloured by FocusContrastForeground. Under the green tint a focused tvOS button fills with the
// bright mint and keeps a pale label - Surprise Me, the library's filter buttons and the movie
// page's Play / Mark as Watched were all close to blank under the remote. A label that sets its
// own colour still wins; one forced white needs focusContrastForeground(unfocused: .white).
// Lists and Forms put .automatic back: their rows already go white with a dark label on focus,
// and keep the green and red their titles are drawn in. Menus aren't buttons, so a Menu label
// carries focusContrastForeground itself.
struct FocusContrastButtonStyle: PrimitiveButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        Button(role: configuration.role, action: configuration.trigger) {
            configuration.label
                .focusContrastForeground(unfocused: configuration.role == .destructive ? SentinelTheme.red : .primary)
        }
        .buttonStyle(.automatic)
    }
}

extension Color {
    init(light: Color, dark: Color) {
        self.init(UIColor { $0.userInterfaceStyle == .dark ? UIColor(dark) : UIColor(light) })
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
        HStack(spacing: 6) {
            if let systemImage { Image(systemName: systemImage) }
            Text(text)
        }
        .font(.caption.bold())
        .foregroundStyle(color)
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
        // A dark backing under the tint: these chips sit on top of live camera video as often
        // as on the app's own surfaces, and a purely translucent fill disappears completely
        // over a bright frame - the LIVE badge was unreadable against anything but a dark scene.
        .background(.black.opacity(0.55), in: Capsule())
        .background(color.opacity(0.18), in: Capsule())
        .overlay { Capsule().stroke(color.opacity(0.55)) }
    }
}
