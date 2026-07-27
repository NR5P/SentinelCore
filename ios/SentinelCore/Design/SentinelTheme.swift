import SwiftUI

enum SentinelTheme {
    static let background = Color(red: 0.02, green: 0.03, blue: 0.04)
    static let panel = Color(red: 0.04, green: 0.06, blue: 0.08)
    static let card = Color(red: 0.05, green: 0.075, blue: 0.10)
    static let green = Color(red: 0.22, green: 1.0, blue: 0.61)
    static let greenDim = Color(red: 0.12, green: 0.61, blue: 0.38)
    static let cyan = Color(red: 0.29, green: 0.91, blue: 1.0)
    static let amber = Color(red: 1.0, green: 0.71, blue: 0.27)
    static let red = Color(red: 1.0, green: 0.30, blue: 0.37)
    static let text = Color(red: 0.78, green: 0.96, blue: 0.85)
    static let textDim = Color(red: 0.42, green: 0.58, blue: 0.51)
}

struct SentinelPanel: ViewModifier {
    func body(content: Content) -> some View {
        content
            .padding()
            .background(SentinelTheme.card)
            .clipShape(RoundedRectangle(cornerRadius: 8))
            .overlay {
                RoundedRectangle(cornerRadius: 8)
                    .stroke(SentinelTheme.greenDim.opacity(0.45))
            }
    }
}

extension View {
    func sentinelPanel() -> some View { modifier(SentinelPanel()) }
}
