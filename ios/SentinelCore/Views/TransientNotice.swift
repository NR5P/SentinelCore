import SwiftUI

// A line of text that appears over live video for a few seconds and then goes away.
//
// Both users of it are controls whose success or failure is invisible in the picture: pressing Home
// on a PTZ camera, and the record button. Everything else in the live view reports itself - a pan
// is obvious, a mute is obvious - so this is deliberately not a general notification system, just
// the one shape those two share.
struct TransientNotice: View {
    let text: String

    var body: some View {
        Text(text)
            .font(.caption)
            .foregroundStyle(.white)
            .padding(.horizontal, 8)
            .padding(.vertical, 4)
            .background(.black.opacity(0.7), in: RoundedRectangle(cornerRadius: 6))
            .transition(.opacity)
    }
}

// Owns the "show this for a few seconds, then clear it" timer, so the two call sites don't each
// carry their own copy of the cancel-and-restart dance.
@MainActor
final class NoticeState: ObservableObject {
    @Published private(set) var text: String?

    private static let duration: Duration = .seconds(4)
    private var clearTask: Task<Void, Never>?

    func show(_ message: String) {
        text = message
        clearTask?.cancel()
        clearTask = Task { [weak self] in
            try? await Task.sleep(for: Self.duration)
            guard !Task.isCancelled else { return }
            self?.text = nil
        }
    }

    func cancel() {
        clearTask?.cancel()
        clearTask = nil
    }
}
