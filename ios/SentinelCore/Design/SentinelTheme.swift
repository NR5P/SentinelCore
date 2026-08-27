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

// A text box that looks like one. Inside a grouped Form, SwiftUI's TextField draws no border of
// its own, so an empty field is a blank gap under its label with nothing about it to say it can
// be typed in - which is exactly the "where am I supposed to tap?" that the Location section's
// town and coordinates kept provoking. This draws the box: full row width, a faint fill, and a
// border that turns green and thickens while the field has the keyboard. The whole box is the
// tap target, not just the line of text inside it.
struct SentinelInputBox: ViewModifier {
    var isFocused: Bool
    var onTap: () -> Void

    private var shape: RoundedRectangle { RoundedRectangle(cornerRadius: 8) }

    func body(content: Content) -> some View {
        content
            .padding(.horizontal, 10)
            .padding(.vertical, 9)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Color.primary.opacity(0.05), in: shape)
            .overlay {
                shape.strokeBorder(
                    isFocused ? SentinelTheme.green : Color.secondary.opacity(0.4),
                    lineWidth: isFocused ? 2 : 1)
            }
            .contentShape(shape)
            .onTapGesture(perform: onTap)
            .animation(.easeOut(duration: 0.15), value: isFocused)
    }
}

extension View {
    func sentinelInputBox(focused: Bool = false, onTap: @escaping () -> Void = {}) -> some View {
        modifier(SentinelInputBox(isFocused: focused, onTap: onTap))
    }
}

// The gesture this platform actually has, so a hint can name it. Here rather than in any one
// screen because every typed field in the app says the same thing.
enum SentinelInput {
    #if os(macOS)
    static let typeVerb = "Click to type"
    static let typeVerbLower = "click to type"
    #else
    static let typeVerb = "Tap to type"
    static let typeVerbLower = "tap to type"
    #endif
}

// One field the user fills in by hand: its name above it, and a box below that looks like
// somewhere to type even when it is empty. The name stays visible once there is a value in the
// box - a placeholder doing double duty as the label vanishes the moment you type, which leaves
// a row of text with nothing saying what it is.
//
// Keyboard modifiers belong at the call site: .keyboardType, .textContentType and
// .textInputAutocapitalization all travel down the environment and reach the field inside.
// The same box without a label above it, for the handful of fields that sit in a row beside the
// button that acts on them - "Add a question" next to Add - where the button already says what
// the field is for and a label on top would only make the row taller.
struct SentinelInlineField: View {
    let prompt: String
    @Binding var text: String

    @FocusState private var focused: Bool

    var body: some View {
        TextField("", text: $text, prompt: Text(prompt))
            .labelsHidden()
            .focused($focused)
            .sentinelInputBox(focused: focused) { focused = true }
    }
}

struct SentinelTextField: View {
    let label: String
    var prompt: String = SentinelInput.typeVerb
    @Binding var text: String
    var secure = false
    var axis: Axis = .horizontal

    @FocusState private var focused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            // fixedSize so a label long enough to wrap makes the row taller instead of drawing
            // over whatever sits under it.
            Text(label).fixedSize(horizontal: false, vertical: true)
            Group {
                if secure {
                    // prompt:, not the title argument: macOS draws a field's title as a label
                    // beside the box and only prompt: as placeholder text inside it.
                    SecureField("", text: $text, prompt: Text(prompt))
                } else if axis == .vertical {
                    // A vertical-axis field measures to zero height inside a macOS Form, which
                    // leaves an empty pill with the value invisible inside it - the schedule
                    // editor's Payload box looked unfillable for exactly this reason. A line
                    // range gives it something to measure, and a field meant for several lines
                    // may as well show several.
                    TextField("", text: $text, prompt: Text(prompt), axis: .vertical)
                        .lineLimit(3...8)
                } else {
                    TextField("", text: $text, prompt: Text(prompt))
                }
            }
            .labelsHidden()
            .focused($focused)
            .sentinelInputBox(focused: focused) { focused = true }
        }
    }
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
