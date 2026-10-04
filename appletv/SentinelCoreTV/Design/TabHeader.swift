import SwiftUI
import UIKit

extension View {
    // A tab's own header - the Home / Movies / TV Shows picker and the like - pinned above the
    // pane's scroll view rather than stacked on top of it.
    //
    // tvOS slides the tab bar away while a page's full-screen scroll view is scrolled down and
    // brings it back at the top. A header in a VStack above the scroll view means the page has
    // no full-screen scroll view, so the tab bar never follows it - and after Back from a
    // scrolled detail page the bar stayed away for good: Up stopped at the header with the tab
    // bar missing, Menu then focused the invisible tab bar, and the next Menu left the app.
    // As a safe-area inset the scroll view runs the full height under the header and the tab bar
    // tracks it again. The header goes with the tab bar - faded out while the page is scrolled
    // down, back at the top - so rows don't show through it.
    func tabHeader<Header: View>(spacing: CGFloat = 24, @ViewBuilder _ header: () -> Header) -> some View {
        modifier(TabHeaderModifier(spacing: spacing, header: header()))
    }
}

private struct TabHeaderModifier<Header: View>: ViewModifier {
    let spacing: CGFloat
    let header: Header
    @State private var scrolledDown = false

    func body(content: Content) -> some View {
        content.safeAreaInset(edge: .top, spacing: spacing) {
            header
                .frame(maxWidth: .infinity, alignment: .leading)
                .opacity(scrolledDown ? 0 : 1)
                .animation(.easeInOut(duration: 0.2), value: scrolledDown)
                .background { TabScrollWatcher(scrolledDown: $scrolledDown) }
        }
    }
}

// Follows whichever scroll view the page has handed the tab bar (UIKit's content scroll view for
// the top edge), so it works the same for a pane that is a ScrollView or a List, and picks up
// the new one when the pane changes.
private struct TabScrollWatcher: UIViewRepresentable {
    @Binding var scrolledDown: Bool

    func makeUIView(context: Context) -> WatcherView {
        let view = WatcherView()
        view.isUserInteractionEnabled = false
        return view
    }

    func updateUIView(_ view: WatcherView, context: Context) {
        let binding = $scrolledDown
        view.onChange = { value in
            if binding.wrappedValue != value { binding.wrappedValue = value }
        }
        view.resolve()
    }

    final class WatcherView: UIView {
        var onChange: ((Bool) -> Void)?
        private weak var watched: UIScrollView?
        private var observation: NSKeyValueObservation?
        private var timer: Timer?

        override func didMoveToWindow() {
            super.didMoveToWindow()
            timer?.invalidate()
            timer = nil
            guard window != nil else { return }
            resolve()
            // A pane switch swaps the scroll view without telling this view; look again now and then.
            timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.resolve() }
            }
        }

        func resolve() {
            let scrollView = pageController()?.contentScrollView(for: .top)
            guard scrollView !== watched else { return }
            watched = scrollView
            observation = scrollView?.observe(\.contentOffset, options: [.initial, .new]) { [weak self] view, _ in
                MainActor.assumeIsolated { self?.report(view) }
            }
            if scrollView == nil { onChange?(false) }
        }

        private func report(_ scrollView: UIScrollView) {
            onChange?(scrollView.contentOffset.y > -scrollView.adjustedContentInset.top + 40)
        }

        // The page this header belongs to: the controller the navigation stack holds.
        private func pageController() -> UIViewController? {
            var responder: UIResponder? = self
            while let next = responder?.next {
                if let controller = next as? UIViewController, controller.parent is UINavigationController {
                    return controller
                }
                responder = next
            }
            return nil
        }
    }
}
