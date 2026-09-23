import SwiftUI
import WidgetKit

// Home Screen and Lock Screen widgets for the Organizer. They show the snapshot the app writes
// (see OrganizerWidgetStore in OrganizerKit and OrganizerWidgets in the app), so they work
// offline and are only as fresh as the app's last sync.
@main
struct SentinelCoreWidgets: WidgetBundle {
    var body: some Widget {
        OrganizerCalendarWidget()
        OrganizerRemindersWidget()
        OrganizerNotesWidget()
    }
}
