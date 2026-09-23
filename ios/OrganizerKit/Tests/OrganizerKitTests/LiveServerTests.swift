import Foundation
import ImageIO
import UniformTypeIdentifiers
import XCTest
@testable import OrganizerKit

// Two devices and the real SentinelCore server - the merge rules, conflicts, attachments and the
// rebuilt-server refill exercised end to end rather than against a stand-in.
//
// Needs two throwaway servers, which scripts/test-with-server.sh starts on loopback ports with
// scratch data and tears down afterwards. Plain `swift test` skips these.
final class SwitchableTransport: OrganizerTransport, @unchecked Sendable {
    private let lock = NSLock()
    private var _base: URL
    private var _offline = false
    private let inner: URLSessionOrganizerTransport

    init(_ base: URL) {
        _base = base
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 30
        let session = URLSession(configuration: configuration)
        // The inner transport asks for the address on every request, so switching `base` mid-test
        // points the device at a different server - the rebuilt-server case.
        let holder = Box()
        inner = URLSessionOrganizerTransport(session: session) { holder.get() }
        holder.source = { [unowned self] in self.lock.withLock { self._offline ? nil : self._base } }
    }

    var base: URL {
        get { lock.withLock { _base } }
        set { lock.withLock { _base = newValue } }
    }

    var offline: Bool {
        get { lock.withLock { _offline } }
        set { lock.withLock { _offline = newValue } }
    }

    func send(method: String, path: String, query: [URLQueryItem], body: Data?, contentType: String?, timeout: TimeInterval?) async throws -> OrganizerHTTPResponse {
        if offline { throw OrganizerError.offline }
        return try await inner.send(method: method, path: path, query: query, body: body, contentType: contentType, timeout: timeout)
    }

    final class Box: @unchecked Sendable {
        var source: (() -> URL?)?
        func get() -> URL? { source?() }
    }
}

@MainActor
final class LiveServerTests: XCTestCase {
    private var serverA: URL!
    private var serverB: URL!
    private var engines: [OrganizerEngine] = []
    private var directories: [URL] = []

    override func setUp() async throws {
        let environment = ProcessInfo.processInfo.environment
        guard let a = environment["ORGANIZER_TEST_SERVER_A"].flatMap(URL.init(string:)),
              let b = environment["ORGANIZER_TEST_SERVER_B"].flatMap(URL.init(string:)) else {
            throw XCTSkip("Run scripts/test-with-server.sh to exercise the real server.")
        }
        serverA = a
        serverB = b
    }

    override func tearDown() async throws {
        for engine in engines { engine.disconnect() }
        for directory in directories { try? FileManager.default.removeItem(at: directory) }
    }

    private func device(_ name: String, policy: OfflinePolicy = .computerDefault, server: URL? = nil) throws -> (OrganizerEngine, SwitchableTransport) {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("organizer-live-\(name)-\(UUID().uuidString)")
        directories.append(folder)
        let engine = try OrganizerEngine(directory: folder, deviceId: name, policy: policy)
        let transport = SwitchableTransport(server ?? serverA)
        engines.append(engine)
        return (engine, transport)
    }

    private func start(_ engine: OrganizerEngine, _ transport: SwitchableTransport) async {
        engine.connect(transport)
        await engine.refreshStatus()
        await engine.sync()
    }

    private func unique(_ label: String) -> String { "\(label) \(UUID().uuidString.prefix(8))" }

    func testAnEditOnOneDeviceAppearsOnTheOther() async throws {
        let (phone, phoneLink) = try device("phone")
        let (laptop, laptopLink) = try device("laptop")
        await start(phone, phoneLink)
        await start(laptop, laptopLink)

        let title = unique("Soccer practice")
        let event = phone.create(.event, fields: [
            "title": .string(title), "start": "2026-09-23T15:00:00-05:00", "end": "2026-09-23T16:00:00-05:00",
            "recurrence": "FREQ=WEEKLY", "links": [["url": "https://example.com/schedule", "title": "Schedule"]],
        ], spaceId: "family")
        await phone.sync()
        await laptop.sync()

        let seen = try XCTUnwrap(laptop.item(event.id))
        XCTAssertEqual(seen.title, title)
        XCTAssertEqual(seen.string("recurrence"), "FREQ=WEEKLY")
        XCTAssertEqual(seen.fields["links"]?.array?.first?.object?["title"], "Schedule")
    }

    func testOfflineEditsToDifferentFieldsBothSurvive() async throws {
        let (phone, phoneLink) = try device("phone")
        let (laptop, laptopLink) = try device("laptop")
        await start(phone, phoneLink)
        await start(laptop, laptopLink)

        let card = phone.create(.reminder, fields: ["title": .string(unique("Call vet"))], spaceId: "family")
        await phone.sync()
        await laptop.sync()

        // The phone loses signal and renames it; meanwhile the laptop gives it a due date.
        phoneLink.offline = true
        phone.update(card.id, ["title": "Call the vet about Max"])
        await phone.sync()
        XCTAssertEqual(phone.syncState.waitingChanges, 1)

        laptop.update(card.id, ["due": "2026-09-30", "allDay": true])
        await laptop.sync()

        phoneLink.offline = false
        await phone.sync()
        await laptop.sync()

        for device in [phone, laptop] {
            let merged = try XCTUnwrap(device.item(card.id))
            XCTAssertEqual(merged.title, "Call the vet about Max")
            XCTAssertEqual(merged.string("due"), "2026-09-30")
        }
        XCTAssertEqual(phone.syncState.waitingChanges, 0)
    }

    func testTwoPeopleRewritingOneNoteKeepBoth() async throws {
        let (phone, phoneLink) = try device("phone")
        let (laptop, laptopLink) = try device("laptop")
        await start(phone, phoneLink)
        await start(laptop, laptopLink)

        let title = unique("Trip")
        let note = phone.create(.note, fields: ["title": .string(title), "body": "pack bags"], spaceId: "family")
        await phone.sync()
        await laptop.sync()

        phoneLink.offline = true
        phone.update(note.id, ["body": "pack bags, snacks"])
        laptop.update(note.id, ["body": "pack bags, passports"])
        await laptop.sync()
        phoneLink.offline = false
        await phone.sync()
        await laptop.sync()

        for device in [phone, laptop] {
            let notes = device.items(.note).filter { $0.title.hasPrefix(title) }
            XCTAssertEqual(Set(notes.compactMap { $0.string("body") }), ["pack bags, snacks", "pack bags, passports"])
            XCTAssertTrue(notes.contains { $0.title == "\(title) (conflicted copy)" })
        }
    }

    func testDeletesReachEveryDeviceAndAStaleEditCantBringItBack() async throws {
        let (phone, phoneLink) = try device("phone")
        let (laptop, laptopLink) = try device("laptop")
        await start(phone, phoneLink)
        await start(laptop, laptopLink)

        let note = phone.create(.note, fields: ["title": .string(unique("Old list"))], spaceId: "family")
        await phone.sync()
        await laptop.sync()

        phoneLink.offline = true
        phone.update(note.id, ["title": "Old list (edited offline)"])
        laptop.delete(note.id)
        await laptop.sync()
        phoneLink.offline = false
        await phone.sync()

        XCTAssertNil(phone.item(note.id))
        XCTAssertNil(laptop.item(note.id))
    }

    func testABoardMadeOfflineArrivesWhole() async throws {
        let (phone, phoneLink) = try device("phone")
        let (laptop, laptopLink) = try device("laptop")
        await start(phone, phoneLink)
        await start(laptop, laptopLink)

        phoneLink.offline = true
        let board = phone.create(.board, fields: ["title": .string(unique("House"))], spaceId: "family")
        let todo = phone.create(.column, fields: ["title": "To do", "position": 1], parentId: board.id)
        let done = phone.create(.column, fields: ["title": "Done", "position": 2], parentId: board.id)
        let card = phone.create(.card, fields: ["title": "Paint fence", "position": 1, "checklist": [["id": "a", "text": "buy paint", "done": false]]], parentId: todo.id)
        phone.update(card.id, ["parentId": .string(done.id), "position": 1.5])
        await phone.sync()
        phoneLink.offline = false
        await phone.sync()
        await laptop.sync()

        XCTAssertEqual(laptop.children(of: board.id, kind: .column).count, 2)
        let moved = try XCTUnwrap(laptop.item(card.id))
        XCTAssertEqual(moved.parentId, done.id)
        XCTAssertEqual(moved.fields["checklist"]?.array?.count, 1)
        XCTAssertEqual(phone.syncState.waitingChanges, 0)
    }

    func testAttachmentsTravelAndALightDeviceKeepsOnlyAThumbnail() async throws {
        let (phone, phoneLink) = try device("phone")
        let (laptop, laptopLink) = try device("laptop")
        let (lightPhone, lightLink) = try device("light", policy: .phoneDefault)
        await start(phone, phoneLink)
        await start(laptop, laptopLink)
        await start(lightPhone, lightLink)

        let jpeg = try makeJPEG(width: 1600, height: 1200)
        let note = phone.create(.note, fields: ["title": .string(unique("Field map"))], spaceId: "family")
        // Attached with no signal - the file is kept here and goes up later.
        phoneLink.offline = true
        let attachment = try phone.attach(jpeg, name: "map.jpg", contentType: "image/jpeg", to: note.id)
        let hash = try XCTUnwrap(attachment.string("hash"))
        phoneLink.offline = false
        await phone.sync()
        try await waitUntil { phone.storageSummary().waitingUploads == 0 }

        await laptop.sync()
        try await waitUntil { laptop.hasOriginal(hash) }
        let original = await laptop.imageData(hash: hash, thumbnail: false)
        XCTAssertEqual(original, jpeg)

        await lightPhone.sync()
        try await waitUntil { lightPhone.storageSummary().bytesOnDevice > 0 }
        XCTAssertFalse(lightPhone.hasOriginal(hash), "a light copy keeps thumbnails, not originals")
        let fetched = await lightPhone.imageData(hash: hash, thumbnail: true)
        let thumbnail = try XCTUnwrap(fetched)
        XCTAssertLessThan(thumbnail.count, jpeg.count)
        let size = try XCTUnwrap(imageSize(thumbnail))
        XCTAssertLessThanOrEqual(max(size.width, size.height), 512)
    }

    // The whole point of keeping a full copy on the computers: the server dies, a new one is
    // installed, and a device fills it back up - items, groups and pictures.
    func testARebuiltServerIsRefilledFromADevice() async throws {
        let (laptop, link) = try device("laptop")
        await start(laptop, link)

        let title = unique("Survives the server")
        let note = laptop.create(.note, fields: ["title": .string(title), "body": "important"], spaceId: "family")
        let jpeg = try makeJPEG(width: 64, height: 48)
        let attachment = try laptop.attach(jpeg, name: "photo.jpg", contentType: "image/jpeg", to: note.id)
        let hash = try XCTUnwrap(attachment.string("hash"))
        await laptop.sync()
        try await waitUntil { laptop.storageSummary().waitingUploads == 0 }

        // The old server is gone; the new one answers at the same address.
        link.base = serverB
        laptop.update(note.id, ["body": "important - edited after the server died"])
        await laptop.sync()
        let prompt = try XCTUnwrap(laptop.resetPrompt)
        XCTAssertGreaterThan(prompt.localCount, 0)

        await laptop.resolveReset(upload: true)
        try await waitUntil { laptop.storageSummary().waitingUploads == 0 && laptop.syncState.syncing == false }

        // A brand-new device joining the rebuilt server gets it all, picture included.
        let (fresh, freshLink) = try device("fresh", server: serverB)
        await start(fresh, freshLink)
        let restored = try XCTUnwrap(fresh.items(.note).first { $0.title == title })
        XCTAssertEqual(restored.string("body"), "important - edited after the server died")
        try await waitUntil { fresh.hasOriginal(hash) }
        let bytes = await fresh.imageData(hash: hash, thumbnail: false)
        XCTAssertEqual(bytes, jpeg)
    }

    func testTheSubscriptionFeedListsEvents() async throws {
        let (phone, link) = try device("phone")
        await start(phone, link)
        let title = unique("Dentist")
        phone.create(.event, fields: ["title": .string(title), "start": "2026-10-01T09:00:00Z"], spaceId: "family")
        await phone.sync()

        let path = try await phone.calendarFeedPath()
        let (data, _) = try await URLSession.shared.data(from: serverA.appendingPathComponent(String(path.dropFirst())))
        let ics = String(decoding: data, as: UTF8.self)
        XCTAssertTrue(ics.contains("SUMMARY:\(title)"), "path \(path):\n\(ics.prefix(600))")
    }

    // MARK: helpers

    // Holiday calendars are a per-person choice: made on one device, shown on the others, with
    // the days worked out by the server.
    func testAHolidayChoiceFollowsThePersonToTheirOtherDevices() async throws {
        let (phone, phoneLink) = try device("phone")
        let (tv, tvLink) = try device("tv")
        await start(phone, phoneLink)
        await start(tv, tvLink)

        phone.setHolidaySets(["jewish", "us-federal"])
        try await waitUntil { !phone.holidayState.dirty && !phone.holidayState.list.isEmpty }
        await tv.refreshHolidays(force: true)
        XCTAssertEqual(Set(tv.holidaySets), ["jewish", "us-federal"])

        let calendar = Calendar.current
        let september = calendar.date(from: DateComponents(year: 2026, month: 9, day: 1))!
        let october = calendar.date(from: DateComponents(year: 2026, month: 10, day: 1))!
        let names = tv.holidays(from: september, to: october).map(\.holiday.name)
        XCTAssertTrue(names.contains("Rosh Hashanah"), "\(names)")
        XCTAssertTrue(names.contains("Labor Day"), "\(names)")
        let roshHashanah = try XCTUnwrap(tv.holidays(from: september, to: october).first { $0.holiday.name == "Rosh Hashanah" })
        XCTAssertEqual(calendar.dateComponents([.month, .day], from: roshHashanah.start), DateComponents(month: 9, day: 12))
        XCTAssertEqual(calendar.dateComponents([.day], from: roshHashanah.start, to: roshHashanah.end).day, 2)

        // Turned off on the TV, gone from the phone.
        tv.setHolidaySets([])
        try await waitUntil { !tv.holidayState.dirty }
        await phone.refreshHolidays(force: true)
        XCTAssertTrue(phone.holidaySets.isEmpty)
        XCTAssertTrue(phone.holidays(from: september, to: october).isEmpty)
    }

    private func waitUntil(timeout: TimeInterval = 15, _ condition: @MainActor () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition() {
            if Date() > deadline { return XCTFail("timed out waiting") }
            try await Task.sleep(for: .milliseconds(100))
        }
    }

    private func makeJPEG(width: Int, height: Int) throws -> Data {
        let context = try XCTUnwrap(CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                              space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue))
        context.setFillColor(red: 0.1, green: 0.6, blue: 0.4, alpha: 1)
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        context.setFillColor(red: 1, green: 1, blue: 1, alpha: 1)
        context.fill(CGRect(x: width / 4, y: height / 4, width: width / 2, height: height / 3))
        let image = try XCTUnwrap(context.makeImage())
        let data = NSMutableData()
        let destination = try XCTUnwrap(CGImageDestinationCreateWithData(data, UTType.jpeg.identifier as CFString, 1, nil))
        CGImageDestinationAddImage(destination, image, nil)
        XCTAssertTrue(CGImageDestinationFinalize(destination))
        return data as Data
    }

    private func imageSize(_ data: Data) -> CGSize? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? Int,
              let height = properties[kCGImagePropertyPixelHeight] as? Int else { return nil }
        return CGSize(width: width, height: height)
    }
}
