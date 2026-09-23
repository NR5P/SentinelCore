import Foundation
import XCTest
@testable import OrganizerKit

@MainActor
final class WidgetSnapshotTests: XCTestCase {
    private var directories: [URL] = []
    private var calendar: Calendar = {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "America/Chicago")!
        return calendar
    }()

    override func tearDown() async throws {
        for directory in directories { try? FileManager.default.removeItem(at: directory) }
    }

    private func directory() -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("organizer-widgets-\(UUID().uuidString)")
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        directories.append(url)
        return url
    }

    private func engine() throws -> OrganizerEngine {
        try OrganizerEngine(directory: directory(), deviceId: "phone", policy: .computerDefault, calendar: calendar)
    }

    private func at(_ text: String) -> Date { OrganizerDates.parseTimestamp(text)! }

    func testEventsAreExpandedAndWhatHasEndedIsLeftOut() throws {
        let engine = try engine()
        let now = at("2026-09-23T12:00:00-05:00")
        engine.create(.event, fields: ["title": "Standup", "start": "2026-09-21T09:00:00-05:00", "end": "2026-09-21T09:15:00-05:00", "recurrence": "FREQ=DAILY"])
        engine.create(.event, fields: ["title": "Lunch", "start": "2026-09-23T11:30:00-05:00", "end": "2026-09-23T12:30:00-05:00"])
        engine.create(.event, fields: ["title": "Trip", "start": "2026-09-25", "end": "2026-09-27", "allDay": true])
        engine.create(.event, fields: ["title": "Next year", "start": "2027-09-25T10:00:00-05:00"])

        let snapshot = engine.widgetSnapshot(enabled: true, now: now, calendar: calendar)

        // Today's standup is over; lunch is still going; the standup repeats for the rest of the window.
        XCTAssertEqual(snapshot.events.first?.title, "Lunch")
        XCTAssertEqual(snapshot.events.filter { $0.title == "Standup" }.count, 13)
        XCTAssertTrue(snapshot.events.contains { $0.title == "Trip" && $0.allDay })
        XCTAssertFalse(snapshot.events.contains { $0.title == "Next year" })
        XCTAssertEqual(Set(snapshot.events.map(\.id)).count, snapshot.events.count)
        XCTAssertEqual(snapshot.events.map(\.start), snapshot.events.map(\.start).sorted())
        // Later entries in the widget's timeline drop what ended before them.
        XCTAssertFalse(snapshot.upcomingEvents(at: at("2026-09-23T13:00:00-05:00")).contains { $0.title == "Lunch" })
    }

    func testRemindersAreOpenTopLevelOnesInDueOrder() throws {
        let engine = try engine()
        let now = at("2026-09-23T12:00:00-05:00")
        let later = engine.create(.reminder, fields: ["title": "Later", "due": "2026-09-30T09:00:00-05:00", "completed": false])
        engine.create(.reminder, fields: ["title": "Overdue", "due": "2026-09-20", "allDay": true, "completed": false])
        engine.create(.reminder, fields: ["title": "Someday", "completed": false])
        engine.create(.reminder, fields: ["title": "Flagged someday", "flagged": true, "completed": false])
        engine.create(.reminder, fields: ["title": "Done", "due": "2026-09-22", "allDay": true, "completed": true])
        engine.create(.reminder, fields: ["title": "Step", "completed": false], parentId: later.id)

        let snapshot = engine.widgetSnapshot(enabled: true, now: now, calendar: calendar)

        XCTAssertEqual(snapshot.reminders.map(\.title), ["Overdue", "Later", "Flagged someday", "Someday"])
        XCTAssertTrue(snapshot.reminders[0].isOverdue(at: now, calendar: calendar))
        XCTAssertFalse(snapshot.reminders[1].isOverdue(at: now, calendar: calendar))
        XCTAssertEqual(snapshot.reminders[0].dueField, "2026-09-20")

        let skipped = engine.widgetSnapshot(enabled: true, now: now, calendar: calendar, skippingReminders: [later.id])
        XCTAssertFalse(skipped.reminders.contains { $0.id == later.id })
    }

    func testNotesArePinnedFirstWithAPlainPreview() throws {
        let engine = try engine()
        engine.create(.note, fields: ["title": "Groceries", "body": "# List\n- [ ] **milk**\n- eggs"])
        engine.create(.note, fields: ["title": "Wifi", "body": "`hunter2`", "pinned": true])
        engine.create(.note, fields: ["title": "Old", "archived": true])

        let snapshot = engine.widgetSnapshot(enabled: true, calendar: calendar)

        XCTAssertEqual(snapshot.notes.map(\.title), ["Wifi", "Groceries"])
        XCTAssertEqual(snapshot.notes[1].preview, "List\nmilk\neggs")
        XCTAssertEqual(snapshot.notes[0].preview, "hunter2")
    }

    func testACheckOffFromAWidgetIsQueuedAndLeavesTheSnapshot() throws {
        let store = OrganizerWidgetStore(directory: directory())
        XCTAssertEqual(store.readSnapshot(), .empty)

        let reminder = OrganizerWidgetSnapshot.Reminder(id: "r1", title: "Bins", due: nil, dueField: "2026-09-23", allDay: true,
                                                        flagged: false, priority: 0, recurring: true)
        let other = OrganizerWidgetSnapshot.Reminder(id: "r2", title: "Call", due: nil, dueField: nil, allDay: false,
                                                     flagged: false, priority: 0, recurring: false)
        store.writeSnapshot(OrganizerWidgetSnapshot(generatedAt: Date(timeIntervalSince1970: 100), enabled: true, reminders: [reminder, other]))

        store.complete(reminder)
        store.complete(reminder)  // a double tap records it once

        XCTAssertEqual(store.readSnapshot().reminders.map(\.id), ["r2"])
        XCTAssertEqual(store.pendingActions().map(\.reminderId), ["r1"])
        XCTAssertEqual(store.pendingActions().first?.dueField, "2026-09-23")

        XCTAssertEqual(store.takeActions().map(\.reminderId), ["r1"])
        XCTAssertTrue(store.pendingActions().isEmpty)
    }

    // A silent push wakes the app in the background: one sync, then nothing left running.
    func testSyncOnceSendsWaitingEditsAndLeavesNothingRunning() async throws {
        let engine = try engine()
        engine.create(.reminder, fields: ["title": "Bins", "completed": false], spaceId: "family")
        let transport = ScriptedTransport { request in
            request.path == "/api/organizer/status" ? ScriptedTransport.status(username: nil) : ScriptedTransport.reply()
        }

        let online = await engine.syncOnce(transport)

        XCTAssertTrue(online)
        XCTAssertFalse(engine.isConnected)
        XCTAssertTrue(transport.requests.contains { $0.path == "/api/organizer/sync" && $0.body.map { String(decoding: $0, as: UTF8.self).contains("Bins") } == true })
        XCTAssertFalse(transport.requests.contains { $0.path == "/api/organizer/wait" })
    }

    func testSyncOnceWhenAlreadyConnectedStaysConnected() async throws {
        let engine = try engine()
        let transport = ScriptedTransport { request in
            request.path == "/api/organizer/status" ? ScriptedTransport.status(username: nil) : ScriptedTransport.reply()
        }
        engine.connect(transport)
        await engine.syncOnce(transport)
        XCTAssertTrue(engine.isConnected)
        engine.disconnect()
    }
}
