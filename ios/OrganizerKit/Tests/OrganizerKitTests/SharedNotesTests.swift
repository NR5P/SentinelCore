import Foundation
import XCTest
@testable import OrganizerKit

// What a share becomes, and the drop-box between the share extension and the app.
final class SharedNotesTests: XCTestCase {
    func testPlainTextIsItsOwnTitle() {
        let f = SharedNote(text: "  Garage code changes Friday  ").fields
        XCTAssertEqual(f["title"], .string("Garage code changes Friday"))
        XCTAssertEqual(f["body"], .string(""))
        XCTAssertNil(f["links"])
    }

    func testLongTextIsTitledWithItsOpeningWords() {
        let text = "The plumber said the water heater needs a new anode rod and the pressure valve should be checked next spring"
        let f = SharedNote(text: text).fields
        XCTAssertEqual(f["title"], .string("The plumber said the water heater needs a new\u{2026}"))
        XCTAssertEqual(f["body"], .string(text))
    }

    func testAWebPageKeepsItsTitleAndLink() {
        let f = SharedNote(text: "https://example.com/recipe", subject: "Best chili", url: "https://example.com/recipe").fields
        XCTAssertEqual(f["title"], .string("Best chili"))
        XCTAssertEqual(f["body"], .string("https://example.com/recipe"))
        XCTAssertEqual(f["links"], .array([.object(["url": .string("https://example.com/recipe"), "title": .string("Best chili")])]))
    }

    func testABareLinkIsTitledWithItsSite() {
        let f = SharedNote(text: "", url: "https://www.example.com/a").fields
        XCTAssertEqual(f["title"], .string("example.com"))
        XCTAssertEqual(f["body"], .string("https://www.example.com/a"))
    }

    func testTextAndASeparateLinkBothLand() {
        let f = SharedNote(text: "Look at this", url: "https://example.com").fields
        XCTAssertEqual(f["title"], .string("Look at this"))
        XCTAssertEqual(f["body"], .string("https://example.com"))
    }

    func testEmptyShareIsEmpty() {
        XCTAssertTrue(SharedNote(text: "  \n ").isEmpty)
        XCTAssertFalse(SharedNote(text: "", url: "https://example.com").isEmpty)
    }

    func testInboxKeepsOrderAndEmptiesOnTake() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let inbox = SharedNoteInbox(directory: directory)
        XCTAssertTrue(inbox.isEmpty)
        inbox.add(SharedNote(text: "one"))
        inbox.add(SharedNote(text: "two"))
        XCTAssertFalse(inbox.isEmpty)
        XCTAssertEqual(inbox.take().map(\.text), ["one", "two"])
        XCTAssertTrue(inbox.take().isEmpty)
    }
}
