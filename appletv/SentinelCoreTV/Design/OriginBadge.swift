import SwiftUI

// Where a row came from, as the small pill every event card wears: the cameras noticing something,
// the record button, or a timed recording window. Without it a hand-started clip and a motion clip
// look identical in the list - and the whole reason recordings joined this list is so they are
// found here rather than nowhere.
struct OriginBadge: View {
    let event: ActivityItem

    var body: some View {
        Label(event.originLabel, systemImage: symbol)
            .labelStyle(.titleAndIcon)
            .font(.caption2)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(color.opacity(0.14), in: Capsule())
            .overlay(Capsule().strokeBorder(color.opacity(0.45), lineWidth: 0.5))
            .foregroundStyle(color)
            .accessibilityLabel("Source: \(event.originDescription)")
    }

    private var symbol: String {
        switch event.origin {
        case "manual": "record.circle"
        case "scheduled": "timer"
        default: "shield.lefthalf.filled"
        }
    }

    private var color: Color {
        switch event.origin {
        case "manual": SentinelTheme.amber
        case "scheduled": SentinelTheme.cyan
        default: .secondary
        }
    }
}
