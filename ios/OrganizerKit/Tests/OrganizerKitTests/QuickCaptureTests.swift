import Foundation
import XCTest
@testable import OrganizerKit

// A Quick Add line to a reminder, note or event. The examples live in
// core/docs/quick-capture-cases.json, which the Android tests read too.
final class QuickCaptureTests: XCTestCase {
    private struct Cases: Decodable {
        struct Case: Decodable {
            var say: String
            var kind: String
            var named: Bool
            var title: String
            var body: String?
            var due: String?
            var start: String?
            var end: String?
        }
        var now: String
        var timeZone: String
        var cases: [Case]
    }

    func testSharedCases() throws {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("core/docs/quick-capture-cases.json")
        let fixture = try JSONDecoder().decode(Cases.self, from: Data(contentsOf: url))
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try XCTUnwrap(TimeZone(identifier: fixture.timeZone))
        let now = try XCTUnwrap(OrganizerDates.parseTimestamp(fixture.now))
        XCTAssertGreaterThan(fixture.cases.count, 15)
        for example in fixture.cases {
            let capture = QuickCapture.parse(example.say)
            XCTAssertEqual(capture.kind.rawValue, example.kind, example.say)
            XCTAssertEqual(capture.named, example.named, example.say)
            let draft = capture.draft(now: now, calendar: calendar)
            XCTAssertEqual(draft.title, example.title, example.say)
            let fields = draft.fields(calendar: calendar)
            switch capture.kind {
            case .note:
                XCTAssertEqual(fields["body"], .string(example.body ?? ""), example.say)
            case .reminder:
                XCTAssertEqual(fields["due"], example.due.map(JSONValue.string), example.say)
            case .event:
                XCTAssertEqual(fields["start"], example.start.map(JSONValue.string), example.say)
                XCTAssertEqual(fields["end"], example.end.map(JSONValue.string), example.say)
                XCTAssertEqual(fields["timeZone"], .string(fixture.timeZone), example.say)
            }
        }
    }

    // Switching kind in the sheet keeps the words, without the ones that named the old kind.
    func testSwitchingKind() {
        let capture = QuickCapture.parse("remind me to water the plants")
        XCTAssertEqual(capture.draft(as: .note).title, "Water the plants")
        XCTAssertEqual(QuickCapture.parse("buy milk").draft(as: .note).title, "Buy milk")
        XCTAssertEqual(QuickCapture.parse("note the meter reading is 4410").draft(as: .reminder).title, "The meter reading is 4410")
    }

    func testEmptyLines() {
        XCTAssertTrue(QuickCapture.parse("").draft().isEmpty)
        XCTAssertTrue(QuickCapture.parse("add a note").draft().isEmpty)
    }
}
