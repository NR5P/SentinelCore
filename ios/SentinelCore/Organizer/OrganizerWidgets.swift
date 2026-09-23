#if os(iOS)
import Foundation
import OrganizerKit
import WidgetKit

// The app's half of the Home Screen widgets (ios/SentinelCoreWidgets): keeps the snapshot they
// read up to date, and carries out the reminders people check off on them.
//
// The snapshot is rewritten shortly after anything in the Organizer changes - an edit here or one
// synced in - so the widgets are as fresh as this device's copy. A check-off on a widget waits in
// the shared container until this runs: at once if the app is open (the widget posts a Darwin
// notification), otherwise the next time it comes to the foreground.
@MainActor
final class OrganizerWidgetBridge {
    private static weak var current: OrganizerWidgetBridge?

    private let engine: OrganizerEngine
    private let store: OrganizerWidgetStore
    private var enabled: Bool
    private var pendingWrite: Task<Void, Never>?
    private var lastWritten: OrganizerWidgetSnapshot?

    init?(engine: OrganizerEngine, enabled: Bool) {
        guard let store = OrganizerWidgetStore() else { return nil }
        self.engine = engine
        self.store = store
        self.enabled = enabled
        Self.current = self
        CFNotificationCenterAddObserver(
            CFNotificationCenterGetDarwinNotifyCenter(), nil,
            { _, _, _, _, _ in Task { @MainActor in OrganizerWidgetBridge.current?.applyActions() } },
            OrganizerWidgetStore.actionsPosted as CFString, nil, .deliverImmediately)
        applyActions()
        observe()
    }

    func setEnabled(_ enabled: Bool) {
        guard enabled != self.enabled else { return }
        self.enabled = enabled
        scheduleWrite()
    }

    // Called when the app comes forward: whatever was checked off meanwhile, and a fresh snapshot
    // (the two-week window moves on with the date even when nothing was edited).
    func appBecameActive() {
        applyActions()
        scheduleWrite()
    }

    func applyActions() {
        for action in store.takeActions() {
            guard let reminder = engine.item(action.reminderId), reminder.kind == .reminder, !reminder.bool("completed") else { continue }
            // Moved on since the widget showed it (done elsewhere, or a repeat already advanced).
            let due = reminder.string("due").flatMap { $0.isEmpty ? nil : $0 }
            guard due == action.dueField else { continue }
            completeReminder(reminder, done: true, engine: engine)
        }
    }

    // Anything the snapshot reads: the items (through `revision`), spaces and holidays.
    private func observe() {
        withObservationTracking {
            _ = engine.revision
            _ = engine.visibleSpaces
            _ = engine.holidays(from: .distantFuture, to: .distantPast)
        } onChange: { [weak self] in
            Task { @MainActor in
                self?.scheduleWrite()
                self?.observe()
            }
        }
        scheduleWrite()
    }

    // A sync applies a reply in several steps; one write after they settle is enough.
    private func scheduleWrite() {
        pendingWrite?.cancel()
        pendingWrite = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(500))
            guard !Task.isCancelled else { return }
            self?.write()
        }
    }

    private func write() {
        // A check-off not yet applied stays off the widgets rather than reappearing.
        let waiting = Set(store.pendingActions().map(\.reminderId))
        var snapshot = engine.widgetSnapshot(enabled: enabled, skippingReminders: waiting)
        let unchanged = lastWritten.map { previous in
            snapshot.generatedAt = previous.generatedAt
            return previous == snapshot
        } ?? false
        snapshot.generatedAt = Date()
        guard !unchanged else { return }
        store.writeSnapshot(snapshot)
        lastWritten = snapshot
        WidgetCenter.shared.reloadAllTimelines()
    }
}
#endif
