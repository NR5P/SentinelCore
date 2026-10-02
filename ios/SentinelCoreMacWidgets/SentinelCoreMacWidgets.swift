import SwiftUI
import WidgetKit

// The Mac's widgets: just Quick Add (QuickAddWidget, shared with the iPhone's extension). The
// Organizer widgets stay iPhone-only - they read a snapshot from the App Group container, which
// the Mac app isn't signed for. Quick Add only opens the app, so it needs nothing shared.
@main
struct SentinelCoreMacWidgets: WidgetBundle {
    var body: some Widget {
        QuickAddWidget()
    }
}
