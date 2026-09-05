import SwiftUI
#if canImport(UIKit)
import UIKit
#endif

// The moment after Save, made unmissable.
//
// Every screen that saves in place used to report success as a small green "Saved." line in the
// form footer. On a long form that line sits below the fold, and once it has appeared it never
// goes away, so the second save looks identical to the first - the button just blinks and the
// page is exactly as it was. Hence the recurring question of whether it actually saved.
//
// This gives those screens the same three signals the web dashboard's toast gives:
//   - the Save button itself turns into a green "Saved" tick for a few seconds,
//   - a banner slides up over the bottom of the screen, anchored to the window rather than the
//     form so it's visible whatever is scrolled into view,
//   - on iPhone, the success haptic.
// Failures get the same treatment in red, alongside whatever detail the screen shows inline.
@MainActor
final class SaveFeedback: ObservableObject {
    struct Toast: Equatable {
        let text: String
        let isError: Bool
    }

    @Published private(set) var toast: Toast?
    // True for a few seconds after a successful save; SaveButton reads it to show the tick.
    @Published private(set) var justSaved = false

    private static let duration: Duration = .seconds(3)
    private var clearTask: Task<Void, Never>?

    func saved(_ message: String = "Saved") {
        Self.haptic(.success)
        justSaved = true
        show(Toast(text: message, isError: false))
    }

    // Something that went right but wasn't a save - "Test email sent to you@example.com."
    func note(_ message: String) {
        justSaved = false
        show(Toast(text: message, isError: false))
    }

    func failed(_ message: String) {
        Self.haptic(.error)
        justSaved = false
        show(Toast(text: message, isError: true))
    }

    // For screens that close themselves on success: the sheet going away is the visual signal,
    // this is the one you feel.
    static func hapticSuccess() { haptic(.success) }

    private func show(_ next: Toast) {
        withAnimation(.spring(duration: 0.35)) { toast = next }
        clearTask?.cancel()
        clearTask = Task { [weak self] in
            try? await Task.sleep(for: Self.duration)
            guard !Task.isCancelled, let self else { return }
            withAnimation(.easeOut(duration: 0.25)) {
                self.toast = nil
                self.justSaved = false
            }
        }
    }

    private enum HapticKind { case success, error }

    private static func haptic(_ kind: HapticKind) {
        #if os(iOS)
        let generator = UINotificationFeedbackGenerator()
        switch kind {
        case .success: generator.notificationOccurred(.success)
        case .error: generator.notificationOccurred(.error)
        }
        #endif
    }
}

// The Save button for a screen that saves in place. "Save" → "Saving…" → green "✓ Saved" → "Save".
// Works both as a form row and as a toolbar item.
struct SaveButton: View {
    @ObservedObject var feedback: SaveFeedback
    let isSaving: Bool
    var disabled = false
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 6) {
                if isSaving {
                    ProgressView().controlSize(.small)
                    Text("Saving…")
                } else if feedback.justSaved {
                    Image(systemName: "checkmark.circle.fill")
                    Text("Saved")
                } else {
                    Text("Save")
                }
            }
            .foregroundStyle(feedback.justSaved && !isSaving ? SentinelTheme.green : Color.accentColor)
            .animation(.easeInOut(duration: 0.2), value: feedback.justSaved)
        }
        .disabled(isSaving || disabled)
    }
}

struct SaveToastView: View {
    let toast: SaveFeedback.Toast

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: toast.isError ? "exclamationmark.triangle.fill" : "checkmark.circle.fill")
                .font(.title3)
            Text(toast.text)
                .font(.callout.weight(.medium))
                .lineLimit(3)
                .multilineTextAlignment(.leading)
        }
        .foregroundStyle(toast.isError ? SentinelTheme.red : SentinelTheme.green)
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .strokeBorder((toast.isError ? SentinelTheme.red : SentinelTheme.green).opacity(0.45), lineWidth: 1)
        )
        .shadow(color: .black.opacity(0.25), radius: 12, y: 4)
        .accessibilityAddTraits(.isStaticText)
    }
}

extension View {
    // Lays the banner over the bottom edge of this view. Attach it to the screen's root - the
    // Form, not a row - so it floats over the content instead of scrolling with it.
    func saveFeedback(_ feedback: SaveFeedback) -> some View {
        modifier(SaveFeedbackOverlay(feedback: feedback))
    }
}

private struct SaveFeedbackOverlay: ViewModifier {
    @ObservedObject var feedback: SaveFeedback

    func body(content: Content) -> some View {
        content.overlay(alignment: .bottom) {
            if let toast = feedback.toast {
                SaveToastView(toast: toast)
                    .padding(.horizontal, 24)
                    .padding(.bottom, 20)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
                    .allowsHitTesting(false)
            }
        }
    }
}
