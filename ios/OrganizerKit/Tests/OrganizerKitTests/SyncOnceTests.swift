import Foundation
import XCTest
@testable import OrganizerKit

// syncOnce is the background pass (silent push, background refresh). The app coming forward while
// one is running must still end up connected - it used to see the borrowed transport, skip its
// own connect, and then be left with nothing when the pass hung up.
final class SyncOnceTests: XCTestCase {
    @MainActor
    func testTheAppStillConnectsWhileABackgroundPassIsRunning() async throws {
        let engine = try OrganizerEngine(directory: nil, deviceId: "t", policy: .computerDefault)
        let transport = ScriptedTransport { request in
            request.path == "/api/organizer/status" ? ScriptedTransport.status(username: nil) : ScriptedTransport.reply()
        }
        transport.syncDelay = .milliseconds(400)
        let background = Task { await engine.syncOnce(transport) }
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertFalse(engine.isConnected, "a borrowed transport isn't the app's connection")

        engine.connect(transport)
        XCTAssertTrue(engine.isConnected)
        _ = await background.value
        XCTAssertTrue(engine.isConnected, "the background pass finishing must not hang up the app's connection")
        engine.disconnect()
    }

    @MainActor
    func testABackgroundPassOnItsOwnHangsUpAfterwards() async throws {
        let engine = try OrganizerEngine(directory: nil, deviceId: "t", policy: .computerDefault)
        let transport = ScriptedTransport { request in
            request.path == "/api/organizer/status" ? ScriptedTransport.status(username: nil) : ScriptedTransport.reply()
        }
        let online = await engine.syncOnce(transport)
        XCTAssertTrue(online)
        XCTAssertFalse(engine.isConnected)
    }
}
