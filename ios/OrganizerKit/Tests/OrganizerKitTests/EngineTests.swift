import Foundation
import XCTest
@testable import OrganizerKit

// A server that answers what each test tells it to, and remembers what it was asked. For the
// engine behaviours that don't depend on the real merge rules - those run against the real
// server in LiveServerTests.
final class ScriptedTransport: OrganizerTransport, @unchecked Sendable {
    struct Request { let method: String; let path: String; let body: Data? }

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
        let request = Request(method: method, path: path, body: body)
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

    func testSigningInAsSomeoneElseStartsFromAnEmptyCopy() async throws {
        let folder = directory()
        let engine = try OrganizerEngine(directory: folder, deviceId: "phone", policy: .computerDefault)
        let transport = ScriptedTransport { request in
            request.path == "/api/organizer/status" ?ScriptedTransport.status(username: "nathan") :ScriptedTransport.reply(items: [[
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
        let group: [String: Any] = ["id": "group:abc", "kind": "Group", "name": "Parents", "members": ["nathan", "terry"]]
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
}
