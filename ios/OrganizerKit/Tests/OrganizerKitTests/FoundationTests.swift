import Foundation
import XCTest
@testable import OrganizerKit

// The building blocks every sync decision rests on. A mistake in any of these doesn't crash - it
// makes one device quietly disagree with the others about a date or about which edit is newer.
final class JSONValueTests: XCTestCase {
    func testRoundTripsEveryShapeAndKeepsUnknownFields() throws {
        let json = #"{"title":"x","n":2,"f":1.5,"flag":true,"none":null,"list":[1,"a",{"k":false}],"futureField":{"deep":[null]}}"#
        let value = try JSONDecoder().decode([String: JSONValue].self, from: Data(json.utf8))
        let again = try JSONDecoder().decode([String: JSONValue].self, from: JSONEncoder().encode(value))
        XCTAssertEqual(value, again)
        XCTAssertEqual(value["futureField"], .object(["deep": .array([.null])]))
    }

    func testWholeNumbersEncodeWithoutAFraction() throws {
        let data = try JSONEncoder().encode(["priority": JSONValue.number(2), "position": .number(1.5)])
        let text = String(decoding: data, as: UTF8.self)
        XCTAssertTrue(text.contains("\"priority\":2"), text)
        XCTAssertTrue(text.contains("\"position\":1.5"), text)
    }

    func testBoolsAndNumbersStayDistinct() throws {
        let value = try JSONDecoder().decode([JSONValue].self, from: Data("[true, 1, 0, false]".utf8))
        XCTAssertEqual(value, [.bool(true), .number(1), .number(0), .bool(false)])
    }
}

final class HybridClockTests: XCTestCase {
    func testClocksAreFixedWidthAndSortInTimeOrder() {
        var clock = HybridClock(deviceId: "phone")
        let a = clock.next(now: Date(timeIntervalSince1970: 1_790_000_000))
        let b = clock.next(now: Date(timeIntervalSince1970: 1_790_000_000))
        let c = clock.next(now: Date(timeIntervalSince1970: 1_790_000_001))
        XCTAssertEqual(a, "1790000000000-0000-phone")
        XCTAssertEqual(b, "1790000000000-0001-phone")
        XCTAssertLessThan(a, b)
        XCTAssertLessThan(b, c)
    }

    func testNeverGoesBackwardsOrBehindAClockItHasSeen() {
        var clock = HybridClock(deviceId: "phone")
        // Another device, a minute ahead, wrote the field this one is about to edit.
        clock.observe("1790000060000-0003-laptop")
        let mine = clock.next(now: Date(timeIntervalSince1970: 1_790_000_000))
        XCTAssertGreaterThan(mine, "1790000060000-0003-laptop")
    }

    func testDeviceIdsAreCleanedToWhatTheServerAccepts() {
        let clock = HybridClock(deviceId: "Nathan’s iPhone (2)")
        XCTAssertEqual(clock.deviceId, "NathansiPhone2")
        XCTAssertEqual(HybridClock(deviceId: "   ").deviceId, "apple")
    }
}

final class OrganizerDatesTests: XCTestCase {
    func testParsesDotNetTimestampsWithSevenFractionalDigits() {
        XCTAssertNotNil(OrganizerDates.parseTimestamp("2026-09-23T19:08:19.8631234+00:00"))
        XCTAssertNotNil(OrganizerDates.parseTimestamp("2026-09-23T19:08:19Z"))
    }

    func testFieldValuesMatchTheDashboardsFormat() {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "America/Chicago")!
        let date = calendar.date(from: DateComponents(year: 2026, month: 9, day: 23, hour: 17))!
        XCTAssertEqual(OrganizerDates.fieldValue(date, allDay: false, calendar: calendar), "2026-09-23T17:00:00-05:00")
        XCTAssertEqual(OrganizerDates.fieldValue(date, allDay: true, calendar: calendar), "2026-09-23")
        XCTAssertEqual(OrganizerDates.parse("2026-09-23T17:00:00-05:00", allDay: false), date)
    }
}

final class RecurrenceTests: XCTestCase {
    private var chicago: Calendar = {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "America/Chicago")!
        return calendar
    }()

    private func event(_ start: String, end: String? = nil, rule: String?, allDay: Bool = false, exdates: [String] = []) -> OrganizerItem {
        var fields: [String: JSONValue] = ["title": "x", "start": .string(start), "allDay": .bool(allDay)]
        if let end { fields["end"] = .string(end) }
        if let rule { fields["recurrence"] = .string(rule) }
        if !exdates.isEmpty { fields["exdates"] = .array(exdates.map(JSONValue.string)) }
        return OrganizerItem(id: "e", kind: .event, spaceId: "family", fields: fields)
    }

    private func days(_ occurrences: [Occurrence]) -> [String] {
        occurrences.map { OrganizerDates.ymd($0.start, calendar: chicago) }
    }

    private func range(_ from: String, _ to: String) -> (Date, Date) {
        (OrganizerDates.day(from, calendar: chicago)!, OrganizerDates.day(to, calendar: chicago)!)
    }

    func testWeeklyKeepsItsWallClockTimeAcrossDaylightSaving() {
        // 2026-11-01 is when Central time falls back.
        let series = event("2026-10-21T09:00:00-05:00", rule: "FREQ=WEEKLY")
        let (from, to) = range("2026-10-20", "2026-11-12")
        let hours = Recurrence.occurrences(of: series, from: from, to: to, calendar: chicago)
            .map { chicago.component(.hour, from: $0.start) }
        XCTAssertEqual(hours, [9, 9, 9, 9])
    }

    func testByDayUntilAndExdates() {
        let series = event("2026-09-21T18:00:00-05:00", rule: "FREQ=WEEKLY;BYDAY=MO,WE;UNTIL=20261007",
                           exdates: ["2026-09-30T18:00:00-05:00"])
        let (from, to) = range("2026-09-01", "2026-12-01")
        XCTAssertEqual(days(Recurrence.occurrences(of: series, from: from, to: to, calendar: chicago)),
                       ["2026-09-21", "2026-09-23", "2026-09-28", "2026-10-05", "2026-10-07"])
    }

    func testMonthlyOnThe31stSkipsShortMonths() {
        let series = event("2026-01-31", rule: "FREQ=MONTHLY", allDay: true)
        let (from, to) = range("2026-01-01", "2026-06-01")
        XCTAssertEqual(days(Recurrence.occurrences(of: series, from: from, to: to, calendar: chicago)),
                       ["2026-01-31", "2026-03-31", "2026-05-31"])
    }

    func testYearlyOnLeapDayOnlyHappensInLeapYears() {
        let series = event("2028-02-29", rule: "FREQ=YEARLY", allDay: true)
        let (from, to) = range("2028-01-01", "2033-01-01")
        XCTAssertEqual(days(Recurrence.occurrences(of: series, from: from, to: to, calendar: chicago)), ["2028-02-29", "2032-02-29"])
    }

    func testCountLimitsTheSeriesAndIntervalSkips() {
        let series = event("2026-09-01", rule: "FREQ=DAILY;INTERVAL=2;COUNT=3", allDay: true)
        let (from, to) = range("2026-08-01", "2026-12-01")
        XCTAssertEqual(days(Recurrence.occurrences(of: series, from: from, to: to, calendar: chicago)),
                       ["2026-09-01", "2026-09-03", "2026-09-05"])
    }

    func testOccurrenceOriginalStartIsWhatExdatesAndDetachedCopiesUse() {
        let series = event("2026-09-23T15:00:00-05:00", rule: "FREQ=WEEKLY")
        let (from, to) = range("2026-10-13", "2026-10-15")
        let only = Recurrence.occurrences(of: series, from: from, to: to, calendar: chicago)
        XCTAssertEqual(only.map(\.originalStart), ["2026-10-14T15:00:00-05:00"])
    }

    func testMultiDayEventsOverlapEveryDayTheyCover() {
        let trip = event("2026-10-01", end: "2026-10-04", rule: nil, allDay: true)
        let (from, to) = range("2026-10-03", "2026-10-04")
        XCTAssertEqual(Recurrence.occurrences(of: trip, from: from, to: to, calendar: chicago).count, 1)
    }

    func testNextDueIsTheOccurrenceAfterTheOneJustDone() {
        var reminder = OrganizerItem(id: "r", kind: .reminder, spaceId: "family",
                                     fields: ["due": "2026-09-22", "allDay": true, "recurrence": "FREQ=WEEKLY"])
        XCTAssertEqual(Recurrence.nextDue(of: reminder, calendar: chicago), "2026-09-29")
        reminder.fields["due"] = "2026-09-22T19:30:00-05:00"
        reminder.fields["allDay"] = false
        XCTAssertEqual(Recurrence.nextDue(of: reminder, calendar: chicago), "2026-09-29T19:30:00-05:00")
    }

    func testRuleRoundTripsItsOwnOutput() {
        let rule = RecurrenceRule(frequency: .weekly, interval: 2, byWeekday: [1, 3])
        XCTAssertEqual(rule.rruleValue, "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE")
        XCTAssertEqual(RecurrenceRule(rule.rruleValue), rule)
    }

    func testLastEndKnowsOpenEndedFromFinishedSeries() {
        XCTAssertEqual(Recurrence.lastEnd(of: event("2026-01-01", rule: "FREQ=WEEKLY", allDay: true), calendar: chicago), .forever)
        guard case let .at(end)? = Recurrence.lastEnd(of: event("2026-01-01", rule: "FREQ=DAILY;COUNT=3", allDay: true), calendar: chicago) else {
            return XCTFail("expected an end")
        }
        XCTAssertEqual(OrganizerDates.ymd(end, calendar: chicago), "2026-01-04")
    }
}

final class OfflinePolicyTests: XCTestCase {
    private let now = OrganizerDates.parseTimestamp("2026-09-23T12:00:00Z")!

    private func item(_ id: String, _ kind: OrganizerKind, parent: String? = nil, updatedDaysAgo: Int = 0, _ fields: [String: JSONValue] = [:]) -> OrganizerItem {
        OrganizerItem(id: id, kind: kind, spaceId: "family", parentId: parent, fields: fields,
                      updatedAt: now.addingTimeInterval(TimeInterval(-updatedDaysAgo * 86_400)))
    }

    func testLightCopyDropsOldThingsButNeverUpcomingOrOpenEndedOnes() {
        let items: [OrganizerItem] = [
            item("old-note", .note, updatedDaysAgo: 200),
            item("old-note-photo", .attachment, parent: "old-note", ["hash": "a"]),
            item("pinned-note", .note, updatedDaysAgo: 400, ["pinned": true]),
            item("recent-note", .note, updatedDaysAgo: 3),
            item("past-event", .event, ["start": "2026-01-10", "allDay": true]),
            item("weekly-since-2020", .event, ["start": "2020-01-06", "allDay": true, "recurrence": "FREQ=WEEKLY"]),
            item("next-month", .event, ["start": "2026-10-10T10:00:00Z"]),
            item("open-reminder", .reminder, updatedDaysAgo: 900),
            item("done-long-ago", .reminder, ["completed": true, "completedAt": "2025-01-01T00:00:00Z"]),
            item("board", .board, updatedDaysAgo: 900),
            item("column", .column, parent: "board", updatedDaysAgo: 900),
            item("stale-open-card", .card, parent: "column", updatedDaysAgo: 900),
            item("finished-card", .card, parent: "column", updatedDaysAgo: 300, ["completed": true]),
        ]
        let map = Dictionary(uniqueKeysWithValues: items.map { ($0.id, $0) })
        let pruned = OfflinePolicy.phoneDefault.itemsToPrune(map, protected: [], now: now)
        XCTAssertEqual(pruned, ["old-note", "old-note-photo", "past-event", "done-long-ago", "finished-card"])
    }

    func testPendingEditsAndPinsAreNeverDroppedAndKeepTheirParents() {
        let items: [OrganizerItem] = [
            item("old-note", .note, updatedDaysAgo: 500),
            item("photo", .attachment, parent: "old-note", ["hash": "a"]),
            item("other", .note, updatedDaysAgo: 500),
        ]
        let map = Dictionary(uniqueKeysWithValues: items.map { ($0.id, $0) })
        // The attachment has an unsent edit; its note must stay so it can be shown.
        XCTAssertEqual(OfflinePolicy.phoneDefault.itemsToPrune(map, protected: ["photo"], now: now), ["other"])
        XCTAssertEqual(OfflinePolicy.phoneDefault.itemsToPrune(map, protected: ["other"], now: now), ["old-note", "photo"])
    }

    func testFullCopyKeepsEverything() {
        let map = ["n": item("n", .note, updatedDaysAgo: 5000)]
        XCTAssertTrue(OfflinePolicy.computerDefault.itemsToPrune(map, protected: [], now: now).isEmpty)
    }

    func testWideningIsDetected() {
        XCTAssertTrue(OfflinePolicy.computerDefault.widens(.phoneDefault))
        XCTAssertFalse(OfflinePolicy.phoneDefault.widens(.computerDefault))
        var longer = OfflinePolicy.phoneDefault
        longer.windowDays = 365
        XCTAssertTrue(longer.widens(.phoneDefault))
    }
}

final class AlertPlannerTests: XCTestCase {
    private var chicago: Calendar = {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "America/Chicago")!
        return calendar
    }()

    func testPlansEventAlertsRemindersAtTheirDueTimeAndSkipsFinishedOnes() {
        let now = OrganizerDates.parse("2026-09-23T08:00:00-05:00", allDay: false)!
        let items = [
            OrganizerItem(id: "practice", kind: .event, spaceId: "family",
                          fields: ["title": "Practice", "start": "2026-09-23T15:00:00-05:00", "recurrence": "FREQ=WEEKLY", "alerts": [30], "location": "Field 3"]),
            OrganizerItem(id: "dentist", kind: .reminder, spaceId: "family", fields: ["title": "Call dentist", "due": "2026-09-23T17:30:00-05:00"]),
            OrganizerItem(id: "trash", kind: .reminder, spaceId: "family", fields: ["title": "Trash", "due": "2026-09-24", "allDay": true]),
            OrganizerItem(id: "done", kind: .reminder, spaceId: "family", fields: ["title": "Done", "due": "2026-09-23T18:00:00-05:00", "completed": true]),
            OrganizerItem(id: "silent", kind: .event, spaceId: "family", fields: ["title": "No alert", "start": "2026-09-23T12:00:00-05:00"]),
        ]
        let plan = AlertPlanner.plan(items: items, now: now, horizon: 8 * 86_400, calendar: chicago)
        let fired = plan.map { "\($0.title) \(OrganizerDates.isoLocal($0.fireDate, timeZone: chicago.timeZone))" }
        XCTAssertEqual(fired, [
            "Practice 2026-09-23T14:30:00-05:00",
            "Call dentist 2026-09-23T17:30:00-05:00",
            "Trash 2026-09-24T09:00:00-05:00",
            "Practice 2026-09-30T14:30:00-05:00",
        ])
        XCTAssertEqual(plan.first?.body.replacingOccurrences(of: "\u{202F}", with: " "), "3:00 PM · Field 3")
        XCTAssertEqual(Set(plan.map(\.id)).count, plan.count)
    }

    func testNeverPlansMoreThanTheLimit() {
        let now = Date()
        let daily = OrganizerItem(id: "d", kind: .event, spaceId: "family",
                                  fields: ["title": "d", "start": .string(OrganizerDates.isoLocal(now.addingTimeInterval(3600))), "recurrence": "FREQ=DAILY", "alerts": [0, 5]])
        XCTAssertEqual(AlertPlanner.plan(items: [daily], now: now, horizon: 60 * 86_400, limit: 48).count, 48)
    }
}
