// Compiled together with SentinelCore/Networking/AddressRecovery.swift by
// check-address-recovery.sh. A real URLSession against a local address with nothing listening
// (the home server once the phone has left Wi-Fi) and a live stub standing in for the remote
// address. `Harness` follows SentinelClient.withAddressRecovery and AppModel.repickAddress step for
// step, minus the app around them.
import Foundation

let deadLocal = CommandLine.arguments[1]
let remote = CommandLine.arguments[2]

@MainActor
final class Harness {
    let local: String
    let remote: String
    var active: String
    var isRemote = false
    let session: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 10
        configuration.waitsForConnectivity = false
        return URLSession(configuration: configuration)
    }()

    init(local: String, remote: String) {
        self.local = local
        self.remote = remote
        active = local
    }

    func probe(_ address: String) async -> AddressRecovery.Probe {
        var request = URLRequest(url: URL(string: "\(address)/api/auth/status")!)
        request.timeoutInterval = 2.5
        return (try? await session.data(for: request)) != nil ? .answered : .unreachable
    }

    func recoverAddress(after failedOrigin: String) async -> Bool {
        if let current = AddressRecovery.origin(of: active), current != failedOrigin { return true }
        if await probe(active) != .unreachable { return false }
        guard let choice = await AddressRecovery.choose(
            local: local, remote: remote, probe: { await self.probe($0) }, fallback: { _ in nil }) else { return false }
        guard AddressRecovery.origin(of: choice.address) != AddressRecovery.origin(of: active) else { return false }
        active = choice.address
        isRemote = choice.isRemote
        return true
    }

    func send(_ method: String, _ path: String, timeout: TimeInterval = 10) async throws -> Data {
        var request = URLRequest(url: URL(string: "\(active)\(path)")!)
        request.httpMethod = method
        request.timeoutInterval = timeout
        if method == "POST" { request.httpBody = Data("{}".utf8) }
        do {
            return try await session.data(for: request).0
        } catch {
            guard AddressRecovery.shouldRecheckAddress(error),
                  let failedOrigin = AddressRecovery.origin(of: request.url),
                  await recoverAddress(after: failedOrigin),
                  AddressRecovery.mayRetry(error, method: method),
                  let retry = AddressRecovery.rebase(request, onto: active),
                  AddressRecovery.origin(of: retry.url) != failedOrigin else { throw error }
            return try await session.data(for: retry).0
        }
    }
}

var failures = 0
@MainActor func check(_ ok: Bool, _ what: String) {
    print("\(ok ? "PASS" : "FAIL")  \(what)")
    if !ok { failures += 1 }
}

func counts() async -> [String: Int] {
    let data = try! await URLSession.shared.data(from: URL(string: "\(remote)/counts")!).0
    return try! JSONSerialization.jsonObject(with: data) as! [String: Int]
}

@MainActor
func run() async {
    // The rules on their own.
    check(AddressRecovery.neverReachedServer(URLError(.cannotConnectToHost)), "cannotConnectToHost never reached the server")
    check(AddressRecovery.neverReachedServer(URLError(.notConnectedToInternet)), "notConnectedToInternet never reached the server")
    check(!AddressRecovery.neverReachedServer(URLError(.timedOut)), "a timeout might have reached the server")
    check(AddressRecovery.mayRetry(URLError(.timedOut), method: "GET"), "a read that timed out may go again")
    check(!AddressRecovery.mayRetry(URLError(.timedOut), method: "POST"), "a command that timed out may not")
    check(!AddressRecovery.mayRetry(URLError(.networkConnectionLost), method: "PUT"), "a command that lost its connection may not")
    check(AddressRecovery.mayRetry(URLError(.cannotConnectToHost), method: "POST"), "a command that never connected may")
    check(!AddressRecovery.shouldRecheckAddress(URLError(.serverCertificateUntrusted)), "a certificate verdict is not a vanished address")
    let rebased = AddressRecovery.rebase(URLRequest(url: URL(string: "https://192.168.10.8:3443/api/music/albums?limit=5&q=a%20b")!), onto: "https://home.example.com")
    check(rebased?.url?.absoluteString == "https://home.example.com/api/music/albums?limit=5&q=a%20b", "rebase keeps path and query: \(rebased?.url?.absoluteString ?? "nil")")
    check(AddressRecovery.origin(of: "HTTP://Box:80/") == AddressRecovery.origin(of: "http://box"), "origins compare by scheme, host and port")

    // Connected at home, then left: the home address is dead, the remote one answers.
    let harness = Harness(local: deadLocal, remote: remote)
    do {
        let data = try await harness.send("GET", "/api/echo?n=1")
        check(String(decoding: data, as: UTF8.self).contains("/api/echo?n=1"), "a read to the dead home address is answered by the remote one")
    } catch {
        check(false, "a read to the dead home address is answered by the remote one (\(error))")
    }
    check(harness.active == remote && harness.isRemote, "the active address switched to the remote one (\(harness.active))")

    // A command whose first attempt never connected runs exactly once.
    harness.active = deadLocal
    harness.isRemote = false
    do {
        _ = try await harness.send("POST", "/api/alarm/arm")
        check(true, "a command to the dead home address went to the remote one")
    } catch {
        check(false, "a command to the dead home address went to the remote one (\(error))")
    }
    check(await counts()["POST /api/alarm/arm"] == 1, "...and the server ran it exactly once")

    // A command that reached the server and then timed out is not sent again.
    do {
        _ = try await harness.send("POST", "/api/slow", timeout: 1)
        check(false, "a timed-out command fails instead of going again")
    } catch {
        check((error as? URLError)?.code == .timedOut, "a timed-out command fails instead of going again (\(error))")
    }
    try? await Task.sleep(for: .seconds(3))
    check(await counts()["POST /api/slow"] == 1, "...and the server saw it once")

    // Nothing answers at all: no switch, the original error comes back.
    let stranded = Harness(local: deadLocal, remote: deadLocal.replacingOccurrences(of: "127.0.0.1", with: "127.0.0.2"))
    do {
        _ = try await stranded.send("GET", "/api/echo")
        check(false, "with no address answering the request fails")
    } catch {
        check(AddressRecovery.neverReachedServer(error), "with no address answering the request fails with the original error (\(error))")
    }
    check(stranded.active == deadLocal, "...and the address is left alone")

    print(failures == 0 ? "All address-recovery checks passed" : "\(failures) check(s) failed")
    exit(failures == 0 ? 0 : 1)
}

Task { @MainActor in await run() }
RunLoop.main.run()
