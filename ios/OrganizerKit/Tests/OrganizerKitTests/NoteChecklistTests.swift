import Foundation
import XCTest
@testable import OrganizerKit

// A note's checklist edits. The examples live in core/docs/note-checklist-cases.json, which the
// dashboard, Android and Windows tests read too - a tick or a new item must land on the same line
// whichever device made it.
final class NoteChecklistTests: XCTestCase {
    private struct Cases: Decodable {
        struct Progress: Decodable { var body: String; var done: Int; var total: Int }
        struct Add: Decodable { var body: String; var text: String; var expect: String }
        struct Remove: Decodable { var body: String; var line: Int; var expect: String }
        struct Rewrite: Decodable { var body: String; var expect: String }
        struct Enter: Decodable {
            struct Expect: Decodable { var insert: String?; var endList: Bool? }
            var line: String
            var expect: Expect?
        }
        var progress: [Progress]
        var addItem: [Add]
        var removeLine: [Remove]
        var clearChecked: [Rewrite]
        var makeChecklist: [Rewrite]
        var continueOnEnter: [Enter]
    }

    private func load() throws -> Cases {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("core/docs/note-checklist-cases.json")
        return try JSONDecoder().decode(Cases.self, from: Data(contentsOf: url))
    }

    func testProgress() throws {
        let cases = try load().progress
        XCTAssertFalse(cases.isEmpty)
        for example in cases {
            let progress = NoteChecklist.progress(example.body)
            XCTAssertEqual(progress.done, example.done, example.body)
            XCTAssertEqual(progress.total, example.total, example.body)
        }
    }

    func testAddItem() throws {
        let cases = try load().addItem
        XCTAssertFalse(cases.isEmpty)
        for example in cases {
            XCTAssertEqual(NoteChecklist.addItem(example.body, text: example.text), example.expect, example.body)
        }
    }

    func testRemoveLine() throws {
        let cases = try load().removeLine
        XCTAssertFalse(cases.isEmpty)
        for example in cases {
            XCTAssertEqual(NoteChecklist.removeLine(example.body, index: example.line), example.expect, example.body)
        }
    }

    func testClearChecked() throws {
        let cases = try load().clearChecked
        XCTAssertFalse(cases.isEmpty)
        for example in cases {
            XCTAssertEqual(NoteChecklist.clearChecked(example.body), example.expect, example.body)
        }
    }

    func testMakeChecklist() throws {
        let cases = try load().makeChecklist
        XCTAssertFalse(cases.isEmpty)
        for example in cases {
            XCTAssertEqual(NoteChecklist.makeChecklist(example.body), example.expect, example.body)
        }
    }

    func testContinueOnEnter() throws {
        let cases = try load().continueOnEnter
        XCTAssertFalse(cases.isEmpty)
        for example in cases {
            let expected: NoteChecklist.Continuation? = switch example.expect {
            case nil: nil
            case let .some(expect) where expect.endList == true: .endList
            case let .some(expect): .insert(expect.insert ?? "")
            }
            XCTAssertEqual(NoteChecklist.continueOnEnter(example.line), expected, example.line)
        }
    }

    // Not in the shared file: the Apple apps' tick, which keeps the rest of the line as written.
    func testToggle() {
        XCTAssertEqual(NoteChecklist.toggle("- [ ] milk\n  * [X] eggs", line: 0), "- [x] milk\n  * [X] eggs")
        XCTAssertEqual(NoteChecklist.toggle("- [ ] milk\n  * [X] eggs", line: 1), "- [ ] milk\n  * [ ] eggs")
        XCTAssertEqual(NoteChecklist.toggle("- [ ] see [x] marks the spot", line: 0), "- [x] see [x] marks the spot")
        XCTAssertEqual(NoteChecklist.toggle("```\n- [ ] code\n```", line: 1), "```\n- [ ] code\n```")
        XCTAssertEqual(NoteChecklist.toggle("plain", line: 0), "plain")
        XCTAssertEqual(NoteChecklist.toggle("- [ ] milk", line: 3), "- [ ] milk")
    }
}
