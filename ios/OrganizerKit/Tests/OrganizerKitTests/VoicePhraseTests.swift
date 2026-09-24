import Foundation
import XCTest
@testable import OrganizerKit

// Siri's words to a reminder. The examples live in core/docs/voice-phrase-cases.json, which the
// Android tests read too - a phrase must mean the same thing on both phones.
final class VoicePhraseTests: XCTestCase {
    private struct Cases: Decodable {
        struct Case: Decodable {
            var say: String
            var title: String
            var due: String?
        }
        var now: String
        var timeZone: String
        var cases: [Case]
    }

    func testSharedCases() throws {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("core/docs/voice-phrase-cases.json")
        let fixture = try JSONDecoder().decode(Cases.self, from: Data(contentsOf: url))
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try XCTUnwrap(TimeZone(identifier: fixture.timeZone))
        let now = try XCTUnwrap(OrganizerDates.parseTimestamp(fixture.now))
        XCTAssertGreaterThan(fixture.cases.count, 20)
        for example in fixture.cases {
            let phrase = VoicePhrase.parse(example.say, now: now, calendar: calendar)
            XCTAssertEqual(phrase.title, example.title, example.say)
            XCTAssertEqual(phrase.dueField(calendar: calendar), example.due, example.say)
        }
    }

    func testDateOnlyPhrasesAreAllDay() {
        let phrase = VoicePhrase.parse("dentist Friday")
        XCTAssertNotNil(phrase.due)
        XCTAssertFalse(phrase.hasTime)
    }
}
