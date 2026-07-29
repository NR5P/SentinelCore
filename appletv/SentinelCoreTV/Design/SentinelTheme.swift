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
        .padding(.horizontal, 10)
        .padding(.vertical, 6)
        .background(color.opacity(0.12))
        .clipShape(Capsule())
        .overlay { Capsule().stroke(color.opacity(0.4)) }
    }
}
