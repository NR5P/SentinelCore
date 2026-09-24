import Foundation
import XCTest
@testable import OrganizerKit

// Apple Reminders into the Organizer. What must hold: the same reminder gets the same id on every
// device, dates don't shift, alerts survive, and anything the Organizer can't do stays in Apple.
final class AppleReminderImportTests: XCTestCase {
    private var calendar: Calendar = {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "America/Chicago")!
        return calendar
    }()

    private func fields(_ reminder: AppleReminder) throws -> [String: JSONValue] {
        guard case let .bring(_, fields) = AppleReminderImport.outcome(for: reminder, calendar: calendar) else {
            XCTFail("expected it to be brought in")
            return [:]
        }
        return fields
    }

    func testIdIsStableAndValidOnTheWire() {
        let a = AppleReminderImport.itemId(for: "ABC-123/x")
        XCTAssertEqual(a, AppleReminderImport.itemId(for: "ABC-123/x"))
        XCTAssertNotEqual(a, AppleReminderImport.itemId(for: "ABC-124/x"))
        XCTAssertNotNil(a.range(of: #"^[A-Za-z0-9_.:-]{1,80}$"#, options: .regularExpression))
    }

    func testTimedReminderKeepsItsLocalTimeAndDefaultAlert() throws {
        let due = DateComponents(calendar: calendar, timeZone: calendar.timeZone, year: 2026, month: 9, day: 24, hour: 17, minute: 0)
        let absolute = calendar.date(from: due)!
        let f = try fields(AppleReminder(externalId: "1", title: " Call mom ", due: due, priority: 1, absoluteAlarms: [absolute]))
        XCTAssertEqual(f["title"], .string("Call mom"))
        XCTAssertEqual(f["due"], .string("2026-09-24T17:00:00-05:00"))
        XCTAssertEqual(f["allDay"], .bool(false))
        XCTAssertEqual(f["priority"], .number(3))
        XCTAssertNil(f["alerts"], "an alert at the due time is already what a due date means")
        XCTAssertEqual(f["appleReminderId"], .string("1"))
    }

    func testDayOnlyReminderStaysOnItsDay() throws {
        let f = try fields(AppleReminder(externalId: "2", title: "Pay rent", due: DateComponents(year: 2026, month: 10, day: 1)))
        XCTAssertEqual(f["due"], .string("2026-10-01"))
        XCTAssertEqual(f["allDay"], .bool(true))
    }

    func testEarlyAlarmsBecomeMinutesBefore() throws {
        let due = DateComponents(calendar: calendar, timeZone: calendar.timeZone, year: 2026, month: 9, day: 24, hour: 17, minute: 0)
        let f = try fields(AppleReminder(externalId: "3", title: "Dentist", due: due, relativeAlarms: [-3600, 0]))
        XCTAssertEqual(f["alerts"], .array([.number(0), .number(60)]))
    }

    func testAnAlarmTimeWithNoDueTimeBecomesTheDueTime() throws {
        let alarm = calendar.date(from: DateComponents(year: 2026, month: 9, day: 25, hour: 8, minute: 30))!
        let dayOnly = try fields(AppleReminder(externalId: "4", title: "Trash", due: DateComponents(year: 2026, month: 9, day: 25), absoluteAlarms: [alarm]))
        XCTAssertEqual(dayOnly["due"], .string("2026-09-25T08:30:00-05:00"))
        XCTAssertEqual(dayOnly["allDay"], .bool(false))
        let noDue = try fields(AppleReminder(externalId: "5", title: "Trash", absoluteAlarms: [alarm]))
        XCTAssertEqual(noDue["due"], .string("2026-09-25T08:30:00-05:00"))
    }

    func testNoDueDateMeansNoDueFields() throws {
        let f = try fields(AppleReminder(externalId: "6", title: "Buy milk", notes: "  2%  ", url: URL(string: "https://example.com")))
        XCTAssertNil(f["due"])
        XCTAssertNil(f["allDay"])
        XCTAssertEqual(f["notes"], .string("2%"))
        XCTAssertEqual(f["links"], .array([.object(["url": .string("https://example.com")])]))
    }

    func testRepeatsComeAcross() throws {
        let rule = RecurrenceRule(frequency: .weekly, interval: 2, byWeekday: [1, 3])
        let f = try fields(AppleReminder(externalId: "7", title: "Mow", due: DateComponents(year: 2026, month: 9, day: 28), recurrence: rule))
        XCTAssertEqual(f["recurrence"], .string("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE"))
    }

    func testWhatTheOrganizerCantDoStaysInApple() {
        XCTAssertEqual(AppleReminderImport.outcome(for: AppleReminder(externalId: "8", title: "Milk", hasLocationAlarm: true)),
                       .leave(reason: "alerts by location"))
        if case .bring = AppleReminderImport.outcome(for: AppleReminder(externalId: "9", title: "x", hasUnsupportedRecurrence: true)) {
            XCTFail("an unsupported repeat must stay in Apple Reminders")
        }
    }

    func testPriorityScale() {
        XCTAssertEqual([0, 1, 4, 5, 6, 9].map(AppleReminderImport.organizerPriority), [0, 3, 3, 2, 1, 1])
    }

    @MainActor
    func testSecondDeviceUpdatesTheSameItemAndNeverRevivesADeletedOne() throws {
        let engine = try OrganizerEngine(directory: nil, deviceId: "t", policy: .computerDefault)
        let id = AppleReminderImport.itemId(for: "x")
        XCTAssertFalse(engine.knows(id))
        engine.create(.reminder, fields: ["title": "A"], spaceId: "family", id: id)
        XCTAssertEqual(engine.item(id)?.title, "A")
        engine.delete(id)
        XCTAssertNil(engine.item(id))
        XCTAssertTrue(engine.knows(id), "a deleted import must still be recognised, or the next pass brings it back")
    }
}
