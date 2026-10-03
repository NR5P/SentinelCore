#if DEBUG
import ObjectiveC
import UIKit

// Simulator testing without a remote: `-debugFocusWalk 8` waits 8 seconds, then moves focus
// through every focusable thing on screen below the tab bar, top-left to bottom-right, one step
// every `-debugFocusWalkInterval` seconds (default 3), logging each step as FOCUSWALK. Take a
// screenshot per step to see every control in its focused state - the state a remote shows.
// Built for checking label contrast on focused buttons: the tvOS simulator takes no touch input,
// and Xcode 27 ships no Simulator.app to send arrow keys to.
//
// UIFocusSystem.requestFocusUpdate(to:) on a SwiftUI item is silently ignored, so instead the
// window's preferredFocusEnvironments is overridden to name the target, and the window - which
// contains whatever is focused, so its request is honoured - asks for a focus update.
@MainActor
enum DebugFocusWalker {
    // Only touched on the main thread: by the walk, and by UIKit's focus engine asking the window.
    nonisolated(unsafe) private static var target: UIFocusEnvironment?

    static func startIfRequested() {
        let delay = UserDefaults.standard.double(forKey: "debugFocusWalk")
        guard delay > 0 else { return }
        let configured = UserDefaults.standard.double(forKey: "debugFocusWalkInterval")
        let interval = configured > 0 ? configured : 3
        installWindowOverride()
        Task { @MainActor in
            try? await Task.sleep(for: .seconds(delay))
            guard let window = UIApplication.shared.connectedScenes
                .compactMap({ ($0 as? UIWindowScene)?.keyWindow }).first else {
                print("FOCUSWALK no window"); return
            }
            let items = focusable(in: window)
            print("FOCUSWALK \(items.count) items")
            for (index, entry) in items.enumerated() {
                target = entry.item
                window.setNeedsFocusUpdate()
                window.updateFocusIfNeeded()
                target = nil
                let landed = UIFocusSystem.focusSystem(for: window)?.focusedItem === entry.item
                print("FOCUSWALK \(index) \(entry.frame.integral) \(landed ? "focused" : "NOT focused")")
                try? await Task.sleep(for: .seconds(interval))
            }
            print("FOCUSWALK done")
        }
    }

    private static func installWindowOverride() {
        let selector = #selector(getter: UIView.preferredFocusEnvironments)
        // UIWindow's own implementation if it has one, else the one it inherits.
        guard let current = class_getInstanceMethod(UIWindow.self, selector) else { return }
        typealias Getter = @convention(c) (AnyObject, Selector) -> NSArray
        let original = unsafeBitCast(method_getImplementation(current), to: Getter.self)
        let block: @convention(block) (AnyObject) -> NSArray = { window in
            if let target { return [target] as NSArray }
            return original(window, selector)
        }
        let replacement = imp_implementationWithBlock(block)
        if !class_addMethod(UIWindow.self, selector, replacement, method_getTypeEncoding(current)) {
            method_setImplementation(current, replacement)
        }
    }

    private static func focusable(in window: UIWindow) -> [(item: UIFocusItem, frame: CGRect)] {
        // The window's own container doesn't hand back SwiftUI's items, so ask every view.
        var seen = Set<ObjectIdentifier>()
        var found: [UIFocusItem] = []
        func visit(_ view: UIView) {
            let items: [UIFocusItem] = [view] + (view.focusItemContainer?.focusItems(in: view.bounds) ?? [])
            for item in items where item.canBecomeFocused && seen.insert(ObjectIdentifier(item)).inserted {
                found.append(item)
            }
            view.subviews.forEach(visit)
        }
        visit(window)
        return found
            .compactMap { item -> (UIFocusItem, CGRect)? in
                guard let space = item.parentFocusEnvironment?.focusItemContainer?.coordinateSpace else { return nil }
                let frame = space.convert(item.frame, to: window.coordinateSpace)
                // Below the tab bar (focusing a tab item switches tabs). Further down than the
                // screen is fine: focusing it scrolls it into view.
                guard frame.minY > 140, frame.minX < window.bounds.maxX else { return nil }
                return (item, frame)
            }
            .sorted { ($0.1.minY / 60).rounded() == ($1.1.minY / 60).rounded() ? $0.1.minX < $1.1.minX : $0.1.minY < $1.1.minY }
    }
}
#endif
