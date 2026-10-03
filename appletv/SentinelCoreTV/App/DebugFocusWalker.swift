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
        if UserDefaults.standard.bool(forKey: "debugRemote") { startRemote() }
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

    // `-debugRemote YES`: a remote for the simulator. Write a command into the app's defaults
    // from the Mac (`simctl spawn <udid> defaults write com.sentinelcore.client.tv debugRemoteCmd
    // "<n> <verb> <arg>"`, n increasing so a repeat runs again):
    //   list            - every focusable item, with its index, frame and label (REMOTE lines)
    //   focus <i>       - move focus to item i of the last list
    //   find <text>     - focus the first item whose label contains text (tab bar included)
    //   press           - activate the focused item (the remote's select button)
    //   menu            - the remote's Menu/back button
    //   arrow <dir>     - up/down/left/right from the focused item
    private static var remoteItems: [(item: UIFocusItem, frame: CGRect)] = []

    private static func startRemote() {
        installWindowOverride()
        Task { @MainActor in
            var last = ""
            while true {
                try? await Task.sleep(for: .milliseconds(500))
                guard let raw = readCommand(), raw != last else { continue }
                last = raw
                guard let window = UIApplication.shared.connectedScenes
                    .compactMap({ ($0 as? UIWindowScene)?.keyWindow }).first else { continue }
                var parts = raw.split(separator: " ", maxSplits: 2).map(String.init)
                guard parts.count >= 2 else { continue }
                parts.removeFirst()
                let verb = parts[0], arg = parts.count > 1 ? parts[1] : ""
                switch verb {
                case "list":
                    remoteItems = allFocusable(in: window)
                    collectAX(window)
                    for (i, e) in remoteItems.enumerated() {
                        print("REMOTE \(i) \(e.frame.integral) [\(label(of: e.item, frame: e.frame))]\(isFocused(e.item, in: window) ? " *" : "")")
                    }
                    print("REMOTE listed \(remoteItems.count)")
                case "focus":
                    if let i = Int(arg), remoteItems.indices.contains(i) { move(to: remoteItems[i].item, in: window) }
                case "find":
                    remoteItems = allFocusable(in: window)
                    collectAX(window)
                    // Content first: a tab item matching would switch tabs. `tab:<name>` for those.
                    let wantsTab = arg.hasPrefix("tab:")
                    let text = wantsTab ? String(arg.dropFirst(4)) : arg
                    let pool = remoteItems.filter { wantsTab ? $0.frame.minY < 120 : $0.frame.minY >= 120 }
                    let exact = pool.first { label(of: $0.item, frame: $0.frame).caseInsensitiveCompare(text) == .orderedSame }
                    if let hit = exact ?? pool.first(where: { label(of: $0.item, frame: $0.frame).localizedCaseInsensitiveContains(text) }) {
                        move(to: hit.item, in: window)
                    } else { print("REMOTE find: nothing labelled \(arg)") }
                case "press":
                    let focused = UIFocusSystem.focusSystem(for: window)?.focusedItem
                    let ok = (focused as? NSObject)?.accessibilityActivate() ?? false
                    if !ok { pressKey(.select, in: window) }
                    print("REMOTE press [\(focused.map(label(of:)) ?? "nothing")] activate=\(ok)")
                case "menu":
                    pressKey(.menu, in: window)
                    print("REMOTE menu")
                case "scroll":
                    // Scrolls the biggest visible scroll view by `arg` points (default one screen),
                    // so lazily-built rows further down exist for list/focus/arrow.
                    var best: UIScrollView?
                    func visit(_ v: UIView) {
                        if let s = v as? UIScrollView, !s.isHidden, s.window != nil,
                           s.contentSize.height > s.bounds.height + 1,
                           s.bounds.width * s.bounds.height > (best.map { $0.bounds.width * $0.bounds.height } ?? 0) {
                            best = s
                        }
                        v.subviews.forEach(visit)
                    }
                    visit(window)
                    if let s = best {
                        let by = Double(arg) ?? 700
                        let maxY = max(0, s.contentSize.height - s.bounds.height + s.adjustedContentInset.bottom)
                        s.setContentOffset(CGPoint(x: s.contentOffset.x, y: min(maxY, max(-s.adjustedContentInset.top, s.contentOffset.y + by))), animated: false)
                        print("REMOTE scroll to \(Int(s.contentOffset.y)) of \(Int(s.contentSize.height))")
                    } else { print("REMOTE scroll: no scroll view") }
                case "vcs":
                    func dump(_ vc: UIViewController, _ depth: Int) {
                        let nav = (vc as? UINavigationController).map { " stack=\($0.viewControllers.count)" } ?? ""
                        print("REMOTE vc \(String(repeating: "  ", count: depth))\(type(of: vc)) \(vc.view.frame.integral) hidden=\(vc.view.isHidden) alpha=\(vc.view.alpha)\(nav)")
                        vc.children.forEach { dump($0, depth + 1) }
                        if let p = vc.presentedViewController, p.presentingViewController === vc { print("REMOTE vc presented:"); dump(p, depth + 1) }
                    }
                    if let root = window.rootViewController { dump(root, 0) }
                case "arrow":
                    let type: UIPress.PressType = switch arg {
                    case "up": .upArrow
                    case "down": .downArrow
                    case "left": .leftArrow
                    default: .rightArrow
                    }
                    moveDirection(type, in: window)
                default:
                    print("REMOTE unknown \(verb)")
                }
            }
        }
    }

    private static func readCommand() -> String? {
        CFPreferencesAppSynchronize(kCFPreferencesCurrentApplication)
        return CFPreferencesCopyAppValue("debugRemoteCmd" as CFString, kCFPreferencesCurrentApplication) as? String
    }

    private static func move(to item: UIFocusItem, in window: UIWindow) {
        target = item
        window.setNeedsFocusUpdate()
        window.updateFocusIfNeeded()
        target = nil
        print("REMOTE focus [\(label(of: item))] \(isFocused(item, in: window) ? "focused" : "NOT focused")")
    }

    // The nearest focusable item in a direction from the focused one, by frame.
    private static func moveDirection(_ type: UIPress.PressType, in window: UIWindow) {
        let items = allFocusable(in: window)
        guard let focused = UIFocusSystem.focusSystem(for: window)?.focusedItem,
              let from = items.first(where: { $0.item === focused })?.frame else {
            print("REMOTE arrow: nothing focused"); return
        }
        let candidates = items.filter { $0.item !== focused }.filter { e in
            switch type {
            case .upArrow: e.frame.midY < from.minY
            case .downArrow: e.frame.midY > from.maxY
            case .leftArrow: e.frame.midX < from.minX && abs(e.frame.midY - from.midY) < max(from.height, 60)
            default: e.frame.midX > from.maxX && abs(e.frame.midY - from.midY) < max(from.height, 60)
            }
        }
        func cost(_ f: CGRect) -> CGFloat {
            let dx = f.midX - from.midX, dy = f.midY - from.midY
            return (type == .upArrow || type == .downArrow) ? abs(dy) + abs(dx) * 2 : abs(dx) + abs(dy) * 2
        }
        guard let next = candidates.min(by: { cost($0.frame) < cost($1.frame) }) else {
            print("REMOTE arrow: no item that way"); return
        }
        move(to: next.item, in: window)
    }

    private static func pressKey(_ type: UIPress.PressType, in window: UIWindow) {
        // Without a real UIPress, ask the responder chain the way the Menu button would.
        if type == .menu {
            var vc = window.rootViewController
            while let presented = vc?.presentedViewController { vc = presented }
            if let presented = vc, presented !== window.rootViewController {
                presented.dismiss(animated: true)
                return
            }
            // A pushed NavigationStack page: find the deepest UINavigationController.
            func deepestNav(_ v: UIViewController?) -> UINavigationController? {
                guard let v else { return nil }
                for child in v.children { if let n = deepestNav(child) { return n } }
                if let n = v as? UINavigationController, n.viewControllers.count > 1 { return n }
                return nil
            }
            deepestNav(window.rootViewController)?.popViewController(animated: true)
        }
    }

    private static func isFocused(_ item: UIFocusItem, in window: UIWindow) -> Bool {
        UIFocusSystem.focusSystem(for: window)?.focusedItem === item
    }

    // Every labelled accessibility element on screen, with its frame - how a SwiftUI button that
    // UIKit only knows as a bare focus item gets a name.
    private static var axElements: [(label: String, frame: CGRect)] = []

    private static func collectAX(_ window: UIWindow) {
        var out: [(String, CGRect)] = []
        func walk(_ o: NSObject, _ depth: Int) {
            guard depth < 40, out.count < 600 else { return }
            if let l = o.accessibilityLabel, !l.isEmpty, o.isAccessibilityElement {
                out.append((l, o.accessibilityFrame))
            }
            if let elements = o.accessibilityElements as? [NSObject] {
                elements.forEach { walk($0, depth + 1) }
            } else {
                let n = o.accessibilityElementCount()
                if n > 0 && n != NSNotFound {
                    for i in 0..<min(n, 200) { if let e = o.accessibilityElement(at: i) as? NSObject { walk(e, depth + 1) } }
                }
            }
            if let v = o as? UIView { v.subviews.forEach { walk($0, depth + 1) } }
        }
        walk(window, 0)
        axElements = out
    }

    private static func label(of item: UIFocusItem, frame: CGRect) -> String {
        let own = label(of: item)
        guard own.hasPrefix("UIKit") || own.hasPrefix("ListTableViewCell") || own.hasPrefix("_") else { return own }
        let inside = axElements.filter { frame.insetBy(dx: -4, dy: -4).contains(CGPoint(x: $0.frame.midX, y: $0.frame.midY)) }
        return inside.isEmpty ? own : inside.prefix(3).map(\.label).joined(separator: " | ")
    }

    private static func label(of item: UIFocusItem) -> String {
        guard let object = item as? NSObject else { return "" }
        if let text = object.accessibilityLabel, !text.isEmpty { return text }
        var texts: [String] = []
        func collect(_ o: NSObject, depth: Int) {
            guard depth < 12, texts.count < 4 else { return }
            if o !== object, let l = o.accessibilityLabel, !l.isEmpty { texts.append(l); return }
            if let label = o as? UILabel, let t = label.text, !t.isEmpty { texts.append(t); return }
            if let elements = o.accessibilityElements as? [NSObject] {
                elements.forEach { collect($0, depth: depth + 1) }
            } else {
                let n = o.accessibilityElementCount()
                if n > 0 && n != NSNotFound {
                    for i in 0..<min(n, 8) { if let e = o.accessibilityElement(at: i) as? NSObject { collect(e, depth: depth + 1) } }
                }
            }
            if let v = o as? UIView { v.subviews.forEach { collect($0, depth: depth + 1) } }
        }
        collect(object, depth: 0)
        if !texts.isEmpty { return texts.prefix(4).joined(separator: " | ") }
        return String(describing: type(of: object))
    }

    private static func allFocusable(in window: UIWindow) -> [(item: UIFocusItem, frame: CGRect)] {
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
                return (item, space.convert(item.frame, to: window.coordinateSpace))
            }
            .sorted { ($0.1.minY / 60).rounded() == ($1.1.minY / 60).rounded() ? $0.1.minX < $1.1.minX : $0.1.minY < $1.1.minY }
    }

    nonisolated(unsafe) private static var overrideInstalled = false

    private static func installWindowOverride() {
        guard !overrideInstalled else { return }
        overrideInstalled = true
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
