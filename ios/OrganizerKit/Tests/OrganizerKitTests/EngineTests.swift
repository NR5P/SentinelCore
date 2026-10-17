import Foundation
import XCTest
@testable import OrganizerKit

// A server that answers what each test tells it to, and remembers what it was asked. For the
// engine behaviours that don't depend on the real merge rules - those run against the real
// server in LiveServerTests.
final class ScriptedTransport: OrganizerTransport, @unchecked Sendable {
    struct Request { let method: String; let path: String; let body: Data?; var query: [URLQueryItem] = [] }

    private let lock = NSLock()
    private var _requests: [Request] = []
    private var handler: (Request) throws -> OrganizerHTTPResponse

    init(_ handler: @escaping (Request) throws -> OrganizerHTTPResponse) {
        self.handler = handler
    }

    var requests: [Request] { lock.withLock { _requests } }

    // Holds each sync request this long before answering, so a test can edit while one is out.
    nonisolated(unsafe) var syncDelay: Duration?

    func setHandler(_ handler: @escaping (Request) throws -> OrganizerHTTPResponse) {
        lock.withLock { self.handler = handler }
    }

    func send(method: String, path: String, query: [URLQueryItem], body: Data?, contentType: String?, timeout: TimeInterval?) async throws -> OrganizerHTTPResponse {
        let request = Request(method: method, path: path, body: body, query: query)
        let handler = lock.withLock { () -> (Request) throws -> OrganizerHTTPResponse in
            _requests.append(request)
            return self.handler
        }
        // Park the long poll so it never races the test.
        if path == "/api/organizer/wait" {
            try await Task.sleep(for: .seconds(3600))
        }
        if path == "/api/organizer/sync", let delay = syncDelay {
            try await Task.sleep(for: delay)
        }
        return try handler(request)
    }

    static func json(_ object: Any, status: Int = 200) -> OrganizerHTTPResponse {
        OrganizerHTTPResponse(status: status, data: try! JSONSerialization.data(withJSONObject: object))
    }

    static func status(username: String?, serverId: String = "server-a") -> OrganizerHTTPResponse {
        json(["enabled": true, "serverId": serverId, "cursor": 0, "username": username as Any, "personalSpaces": username != nil,
              "itemCount": 0, "maxAttachmentBytes": 52_428_800])
    }

    static func reply(serverId: String = "server-a", reset: Bool = false, cursor: Int = 1, spaces: [[String: Any]]? = nil,
                      items: [[String: Any]] = [], removed: [String] = [], rejected: [[String: Any]] = []) -> OrganizerHTTPResponse {
        json(["serverId": serverId, "reset": reset, "cursor": cursor, "hasMore": false,
              "spaces": spaces ?? [["id": "family", "kind": "Family", "name": "Family", "members": []]],
              "items": items, "removed": removed, "rejected": rejected, "missingBlobs": []])
    }
}

// Unreachable for requests begun while `down` is set, however long they take to fail - like a long
// poll left over from before the network dropped.
final class FlakyTransport: OrganizerTransport, @unchecked Sendable {
    private let lock = NSLock()
    private var _down = false
    private var _sentIds: [String] = []

    var down: Bool {
        get { lock.withLock { _down } }
        set { lock.withLock { _down = newValue } }
    }

    var sentIds: [String] { lock.withLock { _sentIds } }

    func send(method: String, path: String, query: [URLQueryItem], body: Data?, contentType: String?, timeout: TimeInterval?) async throws -> OrganizerHTTPResponse {
        let wasDown = down
        if path == "/api/organizer/wait" { try await Task.sleep(for: .seconds(3600)) }
        if path == "/api/organizer/status" { return ScriptedTransport.status(username: nil) }
        try await Task.sleep(for: .milliseconds(200))
        if wasDown { throw OrganizerError.offline }
        let object = try JSONSerialization.jsonObject(with: body ?? Data()) as? [String: Any]
        let ids = (object?["changes"] as? [[String: Any]] ?? []).compactMap { $0["id"] as? String }
        lock.withLock { _sentIds.append(contentsOf: ids) }
        return ScriptedTransport.reply()
    }
}

@MainActor
final class EngineTests: XCTestCase {
    private var directories: [URL] = []

    override func tearDown() async throws {
        for directory in directories { try? FileManager.default.removeItem(at: directory) }
    }

    private func directory() -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("organizer-engine-\(UUID().uuidString)")
        directories.append(url)
        return url
    }

    private func changes(in request: ScriptedTransport.Request?) throws -> [[String: Any]] {
        let body = try XCTUnwrap(request?.body)
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: body) as? [String: Any])
        return object["changes"] as? [[String: Any]] ?? []
    }

    private func settle(_ engine: OrganizerEngine) async {
        // connect() runs status + sync on its own task; give it a moment and then drain a sync.
        try? await Task.sleep(for: .milliseconds(50))
        await engine.sync()
    }

    func testEditsMadeOfflineSurviveARestartAndGoUpAsOneChange() async throws {
        let folder = directory()
        var noteId = ""
        do {
            let engine = try OrganizerEngine(directory: folder, deviceId: "phone", policy: .computerDefault)
            let note = engine.create(.note, fields: ["title": "Groceries", "body": "milk"], spaceId: "family")
            engine.update(note.id, ["body": "milk, eggs"])
            engine.update(note.id, ["body": "milk, eggs, bread"])
            noteId = note.id
            XCTAssertEqual(engine.syncState.waitingChanges, 1)
        }

        // The app was killed with no signal. Next launch still has the note and the queued edit.
        let engine = try OrganizerEngine(directory: folder, deviceId: "phone", policy: .computerDefault)
        XCTAssertEqual(engine.item(noteId)?.string("body"), "milk, eggs, bread")
        XCTAssertEqual(engine.syncState.waitingChanges, 1)

        let transport = ScriptedTransport { request in
            request.path == "/api/organizer/status" ?ScriptedTransport.status(username: nil) :ScriptedTransport.reply()
        }
        engine.connect(transport)
        await settle(engine)

        let sync = transport.requests.first { $0.path == "/api/organizer/sync" }
        let sent = try changes(in: sync)
        XCTAssertEqual(sent.count, 1)
        let fields = try XCTUnwrap(sent.first?["fields"] as? [String: Any])
        XCTAssertEqual(fields["body"] as? String, "milk, eggs, bread")
        XCTAssertEqual(fields["spaceId"] as? String, "family")
        // Every field travels with its own clock.
        XCTAssertEqual(Set((sent.first?["clocks"] as? [String: String] ?? [:]).keys), ["title", "body", "spaceId"])
    }

    // An edit made while a sync is out, followed by sync(), must go up before sync() returns.
    func testSyncAfterAnEditMadeDuringASyncSendsThatEdit() async throws {
        let engine = try OrganizerEngine(directory: directory(), deviceId: "phone", policy: .computerDefault)
        let transport = ScriptedTransport { request in
            request.path == "/api/organizer/status" ? ScriptedTransport.status(username: nil) : ScriptedTransport.reply()
        }
        engine.connect(transport)
        await settle(engine)

        transport.syncDelay = .milliseconds(200)
        engine.create(.note, fields: ["title": "first"], spaceId: "family")
        let first = Task { await engine.sync() }
        try await Task.sleep(for: .milliseconds(50))
        let second = engine.create(.note, fields: ["title": "second"], spaceId: "family")
        await engine.sync()

        let sentIds = try transport.requests.filter { $0.path == "/api/organizer/sync" }
            .flatMap { try changes(in: $0) }.compactMap { $0["id"] as? String }
        XCTAssertTrue(sentIds.contains(second.id), "the edit made mid-sync was not sent before sync() returned")
        await first.value
    }

    // A sync asked for the moment the network came back must not be answered by a pass that
    // started, and failed, while it was still down.
    func testASyncAfterReconnectingIsNotAnsweredByAPassThatFailedWhileOffline() async throws {
        let engine = try OrganizerEngine(directory: directory(), deviceId: "phone", policy: .computerDefault)
        let transport = FlakyTransport()
        engine.connect(transport)
        await settle(engine)

        transport.down = true
        let failing = Task { await engine.sync() }
        try await Task.sleep(for: .milliseconds(50))
        transport.down = false
        let note = engine.create(.note, fields: ["title": "written as the network came back"], spaceId: "family")
        await engine.sync()
        XCTAssertTrue(transport.sentIds.contains(note.id), "the sync after reconnecting was swallowed by the failed pass")
        await failing.value
    }

    func testAFailedSyncKeepsEverythingQueued() async throws {
        let engine = try OrganizerEngine(directory: directory(), deviceId: "phone", policy: .computerDefault)
        let transport = ScriptedTransport { request in
            if request.path == "/api/organizer/status" { return ScriptedTransport.status(username: nil) }
            throw OrganizerError.offline
        }
        engine.connect(transport)
        await settle(engine)
        engine.create(.reminder, fields: ["title": "Call vet"], spaceId: "family")
        await engine.sync()

        XCTAssertFalse(engine.syncState.online)
        XCTAssertEqual(engine.syncState.waitingChanges, 1)
        XCTAssertEqual(engine.items(.reminder).map(\.title), ["Call vet"])
    }

    func testANoteBodyEditCarriesTheClockItStartedFrom() async throws {
        let engine = try OrganizerEngine(directory: directory(), deviceId: "phone", policy: .computerDefault)
        let transport = ScriptedTransport { request in
            if request.path == "/api/organizer/status" { return ScriptedTransport.status(username: nil) }
            return ScriptedTransport.reply(items: [[
                "id": "n1", "kind": "note", "spaceId": "family", "parentId": NSNull(), "deleted": false,
                "fields": ["title": "Trip", "body": "pack"], "clocks": ["title": "1790000000000-0000-web", "body": "1790000000000-0000-web", "spaceId": "1790000000000-0000-web"],
                "seq": 1,
            ]])
        }
        engine.connect(transport)
        await settle(engine)
        XCTAssertEqual(engine.item("n1")?.string("body"), "pack")

        transport.setHandler { _ in ScriptedTransport.reply(cursor: 2) }
        engine.update("n1", ["body": "pack bags"])
        engine.update("n1", ["body": "pack bags, snacks"])
        await engine.sync()

        let sent = try changes(in: transport.requests.last { $0.path == "/api/organizer/sync" })
        XCTAssertEqual(sent.first?["baseClocks"] as? [String: String], ["body": "1790000000000-0000-web"])
    }

    func testARebuiltServerAsksBeforeUploadingAnything() async throws {
        let engine = try OrganizerEngine(directory: directory(), deviceId: "phone", policy: .computerDefault)
        let transport = ScriptedTransport { request in
            request.path == "/api/organizer/status" ?ScriptedTransport.status(username: nil) :ScriptedTransport.reply(serverId: "server-a")
        }
        engine.connect(transport)
        await settle(engine)
        engine.create(.note, fields: ["title": "Keep me"], spaceId: "family")
        await engine.sync()

        transport.setHandler { request in
            request.path == "/api/organizer/status" ?ScriptedTransport.status(username: nil, serverId: "server-b") :ScriptedTransport.reply(serverId: "server-b", reset: true, cursor: 0)
        }
        engine.create(.note, fields: ["title": "Written after"], spaceId: "family")
        await engine.sync()

        XCTAssertEqual(engine.resetPrompt?.id, "server-b")
        XCTAssertEqual(engine.resetPrompt?.localCount, 2)
        XCTAssertEqual(engine.syncState.waitingChanges, 1, "the unsent edit is still queued")
        XCTAssertEqual(engine.items(.note).count, 2, "nothing was thrown away")
    }

    func testNotNowToARebuiltServerIsRememberedForThatServer() async throws {
        let folder = directory()
        let engine = try OrganizerEngine(directory: folder, deviceId: "phone", policy: .computerDefault)
        let transport = ScriptedTransport { request in
            request.path == "/api/organizer/status" ?ScriptedTransport.status(username: nil) :ScriptedTransport.reply(serverId: "server-a")
        }
        engine.connect(transport)
        await settle(engine)
        engine.create(.note, fields: ["title": "Keep me"], spaceId: "family")
        await engine.sync()

        transport.setHandler { request in
            request.path == "/api/organizer/status" ?ScriptedTransport.status(username: nil, serverId: "server-b") :ScriptedTransport.reply(serverId: "server-b", reset: true, cursor: 0)
        }
        await engine.sync()
        XCTAssertEqual(engine.resetPrompt?.id, "server-b")

        engine.declineReset()
        await engine.sync()
        XCTAssertNil(engine.resetPrompt, "not asked again on the next sync")
        XCTAssertEqual(engine.declinedReset?.id, "server-b")
        XCTAssertEqual(engine.items(.note).count, 1, "nothing was thrown away")

        let relaunched = try OrganizerEngine(directory: folder, deviceId: "phone", policy: .computerDefault)
        relaunched.connect(transport)
        await settle(relaunched)
        XCTAssertNil(relaunched.resetPrompt, "not asked again after a relaunch")
        XCTAssertEqual(relaunched.declinedReset?.id, "server-b")

        relaunched.reconsiderReset()
        XCTAssertEqual(relaunched.resetPrompt?.id, "server-b")
        XCTAssertNil(relaunched.declinedReset)
    }

    func testSigningInAsSomeoneElseStartsFromAnEmptyCopy() async throws {
        let folder = directory()
        let engine = try OrganizerEngine(directory: folder, deviceId: "phone", policy: .computerDefault)
        let transport = ScriptedTransport { request in
            request.path == "/api/organizer/status" ?ScriptedTransport.status(username: "morgan") :ScriptedTransport.reply(items: [[
                "id": "diary", "kind": "note", "spaceId": "family", "deleted": false, "fields": ["title": "Mine"], "clocks": [:], "seq": 1,
            ]])
        }
        engine.connect(transport)
        await settle(engine)
        XCTAssertEqual(engine.items(.note).count, 1)

        transport.setHandler { request in
            request.path == "/api/organizer/status" ?ScriptedTransport.status(username: "terry") :ScriptedTransport.reply(cursor: 0)
        }
        await engine.refreshStatus()
        XCTAssertTrue(engine.items(.note).isEmpty)
    }

    func testLosingAccessToAGroupDropsWhatWasInIt() async throws {
        let engine = try OrganizerEngine(directory: directory(), deviceId: "phone", policy: .computerDefault)
        let group: [String: Any] = ["id": "group:abc", "kind": "Group", "name": "Parents", "members": ["morgan", "terry"]]
        let family: [String: Any] = ["id": "family", "kind": "Family", "name": "Family", "members": []]
        let transport = ScriptedTransport { request in
            request.path == "/api/organizer/status" ?ScriptedTransport.status(username: "terry") :ScriptedTransport.reply(spaces: [family, group], items: [[
                "id": "gift", "kind": "reminder", "spaceId": "group:abc", "deleted": false, "fields": ["title": "Bike"], "clocks": [:], "seq": 1,
            ]])
        }
        engine.connect(transport)
        await settle(engine)
        XCTAssertEqual(engine.items(.reminder).map(\.title), ["Bike"])

        transport.setHandler { _ in ScriptedTransport.reply(cursor: 2, spaces: [family]) }
        await engine.sync()
        XCTAssertTrue(engine.items(.reminder).isEmpty)
        XCTAssertEqual(engine.visibleSpaces.map(\.id), ["family"])
    }

    func testANewItemTheServerRefusesIsRemovedAndExplained() async throws {
        let engine = try OrganizerEngine(directory: directory(), deviceId: "phone", policy: .computerDefault)
        let transport = ScriptedTransport { request in
            request.path == "/api/organizer/status" ?ScriptedTransport.status(username: nil) :ScriptedTransport.reply()
        }
        engine.connect(transport)
        await settle(engine)

        let doomed = engine.create(.note, fields: ["title": "x"], spaceId: "personal:someone")
        transport.setHandler { _ in ScriptedTransport.reply(cursor: 2, rejected: [["id": doomed.id, "error": "You don't have access to that space."]]) }
        await engine.sync()

        XCTAssertNil(engine.item(doomed.id))
        XCTAssertEqual(engine.syncState.lastError, "You don't have access to that space.")
        XCTAssertEqual(engine.syncState.waitingChanges, 0)
    }

    func testDeletingABoardDeletesItsColumnsAndCards() throws {
        let engine = try OrganizerEngine(directory: nil, deviceId: "phone", policy: .computerDefault)
        let board = engine.create(.board, fields: ["title": "House"], spaceId: "family")
        let column = engine.create(.column, fields: ["title": "To do"], parentId: board.id)
        let card = engine.create(.card, fields: ["title": "Paint"], parentId: column.id)
        XCTAssertEqual(card.spaceId, "family", "children take their parent's space")

        engine.deleteTree(board.id)
        XCTAssertTrue(engine.items(.board).isEmpty)
        XCTAssertTrue(engine.items(.column).isEmpty)
        XCTAssertTrue(engine.items(.card).isEmpty)
    }

    // A comment from someone else arrives by sync (before `comment` was a known kind, one such item
    // failed the whole response); it lists oldest first next to one posted here, only the author
    // may edit, and an edit stamps editedAt.
    func testCardCommentsSyncInOldestFirstAndOnlyTheAuthorEdits() async throws {
        let engine = try OrganizerEngine(directory: directory(), deviceId: "phone", policy: .computerDefault)
        let board = engine.create(.board, fields: ["title": "House"], spaceId: "family")
        let column = engine.create(.column, fields: ["title": "To do"], parentId: board.id)
        let card = engine.create(.card, fields: ["title": "Paint"], parentId: column.id)
        let transport = ScriptedTransport { request in
            request.path == "/api/organizer/status" ? ScriptedTransport.status(username: "Morgan") : ScriptedTransport.reply(items: [[
                "id": "from-anna", "kind": "comment", "spaceId": "family", "parentId": card.id, "deleted": false,
                "fields": ["body": "Which colour?"], "clocks": [:], "seq": 1,
                "createdAt": "2020-01-01T10:00:00.1234567+00:00", "createdBy": "anna",
            ]])
        }
        engine.connect(transport)
        await settle(engine)

        let mine = try XCTUnwrap(engine.postComment("  Blue  ", on: card.id))
        XCTAssertEqual(mine.string("body"), "Blue")
        XCTAssertEqual(mine.createdBy, "Morgan")
        XCTAssertNil(engine.postComment("   ", on: card.id))
        XCTAssertEqual(engine.comments(on: card.id).map(\.id), ["from-anna", mine.id])

        let theirs = try XCTUnwrap(engine.item("from-anna"))
        XCTAssertFalse(theirs.isAuthored(by: "morgan"))
        XCTAssertTrue(mine.isAuthored(by: "MORGAN"))
        XCTAssertTrue(theirs.isAuthored(by: nil), "login off: anyone may edit")
        XCTAssertTrue(OrganizerItem(id: "x", kind: .comment, spaceId: "family").isAuthored(by: "morgan"), "unknown author")

        XCTAssertNil(engine.item(mine.id)?.editedAt)
        XCTAssertFalse(engine.editComment(mine.id, body: "Blue"), "unchanged body is not an edit")
        XCTAssertTrue(engine.editComment(mine.id, body: "Dark blue"))
        XCTAssertEqual(engine.item(mine.id)?.string("body"), "Dark blue")
        XCTAssertNotNil(engine.item(mine.id)?.editedAt)

        engine.delete("from-anna")
        XCTAssertEqual(engine.comments(on: card.id).map(\.id), [mine.id])
        engine.deleteTree(card.id)
        XCTAssertTrue(engine.comments(on: card.id).isEmpty)
        XCTAssertNil(engine.item(mine.id))
    }

    func testAKindThisBuildDoesNotKnow_IsSkipped_NotAFailedSync() async throws {
        let engine = try OrganizerEngine(directory: directory(), deviceId: "phone", policy: .computerDefault)
        let transport = ScriptedTransport { request in
            request.path == "/api/organizer/status" ? ScriptedTransport.status(username: "Morgan") : ScriptedTransport.reply(items: [
                ["id": "from-the-future", "kind": "sticker", "spaceId": "family", "deleted": false,
                 "fields": ["shape": "star"], "clocks": [:], "seq": 1],
                ["id": "n1", "kind": "note", "spaceId": "family", "deleted": false,
                 "fields": ["title": "Groceries"], "clocks": [:], "seq": 2],
            ])
        }
        engine.connect(transport)
        await settle(engine)

        XCTAssertEqual(engine.item("n1")?.string("title"), "Groceries")
        XCTAssertNil(engine.item("from-the-future"))
    }

    func testAnUpdateThatAddsKinds_StartsTheCursorOver() throws {
        let dir = directory()
        do {
            let engine = try OrganizerEngine(directory: dir, deviceId: "phone", policy: .computerDefault)
            _ = engine
        }
        // Pretend the previous build synced up to 500 knowing fewer kinds.
        let store = try LocalStore(directory: dir)
        store.setMeta("cursor", "500")
        store.setMeta("kinds", "note,event")
        let upgraded = try OrganizerEngine(directory: dir, deviceId: "phone", policy: .computerDefault)
        XCTAssertEqual(try LocalStore(directory: dir).meta("cursor"), "0")
        _ = upgraded

        // And a build that knows the same kinds keeps its place.
        let same = try LocalStore(directory: dir)
        same.setMeta("cursor", "500")
        _ = try OrganizerEngine(directory: dir, deviceId: "phone", policy: .computerDefault)
        XCTAssertEqual(try LocalStore(directory: dir).meta("cursor"), "500")
    }

    func testAttachingWorksOfflineAndKeepsTheFile() throws {
        let engine = try OrganizerEngine(directory: directory(), deviceId: "phone", policy: .computerDefault)
        let note = engine.create(.note, fields: ["title": "Photo"], spaceId: "family")
        let bytes = Data("not really a photo".utf8)
        let attachment = try engine.attach(bytes, name: "photo.jpg", contentType: "image/jpeg", to: note.id)

        let hash = try XCTUnwrap(attachment.string("hash"))
        XCTAssertEqual(hash.count, 64)
        XCTAssertTrue(engine.hasOriginal(hash))
        XCTAssertEqual(engine.children(of: note.id, kind: .attachment).map(\.id), [attachment.id])
        XCTAssertEqual(engine.storageSummary().waitingUploads, 1)
    }

    func testLightModeOffNothingSurvivesARestartExceptUnsentEdits() async throws {
        let folder = directory()
        var off = OfflinePolicy.phoneDefault
        off.mode = .off
        do {
            let engine = try OrganizerEngine(directory: folder, deviceId: "phone", policy: off)
            let transport = ScriptedTransport { request in
                request.path == "/api/organizer/status" ?ScriptedTransport.status(username: nil) :ScriptedTransport.reply(items: [[
                    "id": "synced", "kind": "note", "spaceId": "family", "deleted": false, "fields": ["title": "From server"], "clocks": [:], "seq": 1,
                ]])
            }
            engine.connect(transport)
            await settle(engine)
            engine.disconnect()
            engine.create(.note, fields: ["title": "Unsent"], spaceId: "family")
            XCTAssertEqual(engine.items(.note).count, 2)
        }
        let engine = try OrganizerEngine(directory: folder, deviceId: "phone", policy: off)
        XCTAssertEqual(engine.items(.note).map(\.title), ["Unsent"])
        XCTAssertEqual(engine.syncState.waitingChanges, 1)
    }

    // Nothing is shown until someone chooses, and the choice survives a restart.
    func testHolidaysAreOffByDefaultAndTheChoiceIsKept() async throws {
        let folder = directory()
        do {
            let engine = try OrganizerEngine(directory: folder, deviceId: "phone", policy: .computerDefault)
            XCTAssertTrue(engine.holidaySets.isEmpty)
            XCTAssertTrue(engine.holidayState.list.isEmpty)
            engine.setHolidaySets(["christian", "christian", "jewish"])
            XCTAssertEqual(engine.holidaySets, ["christian", "jewish"])
            XCTAssertTrue(engine.holidayState.dirty)
        }
        let engine = try OrganizerEngine(directory: folder, deviceId: "phone", policy: .computerDefault)
        XCTAssertEqual(engine.holidaySets, ["christian", "jewish"])
        XCTAssertTrue(engine.holidayState.dirty, "an unsent choice is still unsent after a restart")
    }

    // A rebuilt server has no choice on record; the device that still has one sends it back.
    func testARebuiltServerGetsTheHolidayChoiceBackFromTheDevice() async throws {
        let folder = directory()
        do {
            let engine = try OrganizerEngine(directory: folder, deviceId: "phone", policy: .computerDefault)
            engine.setHolidaySets(["jewish"])
        }
        let engine = try OrganizerEngine(directory: folder, deviceId: "phone", policy: .computerDefault)
        var sent: [String]?
        let transport = ScriptedTransport { request in
            switch request.path {
            case "/api/organizer/status": return ScriptedTransport.status(username: nil)
            case "/api/organizer/holidays/sets": return ScriptedTransport.json(["sets": [["id": "jewish", "name": "Jewish holidays", "description": ""]]])
            case "/api/organizer/preferences" where request.method == "PUT":
                sent = (try? JSONSerialization.jsonObject(with: request.body ?? Data()) as? [String: Any])?["holidaySets"] as? [String]
                return ScriptedTransport.json(["holidaySets": sent ?? []])
            case "/api/organizer/preferences": return ScriptedTransport.json(["holidaySets": NSNull()])
            case "/api/organizer/holidays":
                return ScriptedTransport.json(["holidays": [["id": "holiday:jewish:20260912:rosh-hashanah", "setId": "jewish", "name": "Rosh Hashanah", "date": "2026-09-12", "days": 2]]])
            default: return ScriptedTransport.reply()
            }
        }
        engine.connect(transport)
        await settle(engine)
        await engine.refreshHolidays(force: true)
        XCTAssertEqual(sent, ["jewish"])
        XCTAssertFalse(engine.holidayState.dirty)
        XCTAssertEqual(engine.holidayState.list.map(\.name), ["Rosh Hashanah"])
        XCTAssertEqual(engine.holidaySetName("jewish"), "Jewish holidays")
    }
}
