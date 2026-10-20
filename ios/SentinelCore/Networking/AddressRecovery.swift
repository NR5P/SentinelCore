import Foundation

// The rules for "the address this app picked stopped answering" - the phone connected on home
// Wi-Fi, then left. connect() picks between the local and remote address once; without this every
// later request (CarPlay's, a background sync's) kept going to the home address until something
// happened to reconnect. SentinelClient.withAddressRecovery applies them and AppModel.recoverAddress
// does the re-picking. Foundation only, so ios/scripts/check-address-recovery.sh can compile it on
// its own against a real local server.
enum AddressRecovery {
    // Failures where the request provably never left this device or never got a connection: safe
    // to send again anywhere, a command included, because the server can't have run it.
    static func neverReachedServer(_ error: Error) -> Bool {
        guard let code = (error as? URLError)?.code else { return false }
        switch code {
        case .cannotConnectToHost, .cannotFindHost, .dnsLookupFailed,
             .notConnectedToInternet, .internationalRoamingOff, .dataNotAllowed:
            return true
        default:
            return false
        }
    }

    // Failures that are also what a vanished address looks like, but that URLError can't place
    // before or after the request went out: a timeout may have been waiting on the server's answer,
    // and a lost connection may have dropped mid-response. Enough to re-check the address; a retry
    // only for a read (see mayRetry), so an arm or a light switch never runs twice.
    static func mayHaveReachedServer(_ error: Error) -> Bool {
        guard let code = (error as? URLError)?.code else { return false }
        return code == .timedOut || code == .networkConnectionLost
    }

    static func shouldRecheckAddress(_ error: Error) -> Bool {
        neverReachedServer(error) || mayHaveReachedServer(error)
    }

    static func mayRetry(_ error: Error, method: String) -> Bool {
        if neverReachedServer(error) { return true }
        let idempotent = ["GET", "HEAD"].contains(method.uppercased())
        return idempotent && mayHaveReachedServer(error)
    }

    // The same request against another address: scheme, host and port from `base`, path and query
    // kept - the way SentinelClient.url(path:) builds every URL from activeServerURL.
    static func rebase(_ request: URLRequest, onto base: String) -> URLRequest? {
        guard let url = request.url,
              let old = URLComponents(url: url, resolvingAgainstBaseURL: false),
              var new = URLComponents(string: base.trimmingCharacters(in: .whitespacesAndNewlines)),
              new.host != nil else { return nil }
        new.path = old.path
        new.percentEncodedQuery = old.percentEncodedQuery
        guard let rebased = new.url else { return nil }
        var copy = request
        copy.url = rebased
        return copy
    }

    // scheme://host:port, so two spellings of the same address compare equal.
    static func origin(of address: String) -> String? {
        guard let components = URLComponents(string: address.trimmingCharacters(in: .whitespacesAndNewlines)),
              let scheme = components.scheme?.lowercased(), let host = components.host?.lowercased() else { return nil }
        let port = components.port ?? (scheme == "https" ? 443 : 80)
        return "\(scheme)://\(host):\(port)"
    }

    static func origin(of url: URL?) -> String? {
        url.flatMap { origin(of: $0.absoluteString) }
    }

    // What a reachability probe saw.
    enum Probe: Equatable {
        case answered
        // A TLS handshake got far enough to judge the certificate and this device said no: a server
        // is there, just not one this device trusts - the cue for the identity-gated HTTP fallback.
        case untrustedCertificate
        case unreachable
    }

    // The order connect() uses: the local address whenever it answers at all, else the remote one.
    // `fallback` turns a local address with an untrusted certificate into the plain-HTTP address,
    // or nil when that isn't allowed (the server hasn't proved who it is). Returns nil when nothing
    // answers.
    @MainActor static func choose(
        local: String, remote: String?,
        probe: (String) async -> Probe,
        fallback: (String) async -> String?
    ) async -> (address: String, isRemote: Bool, isHttpFallback: Bool)? {
        switch await probe(local) {
        case .answered:
            return (local, false, false)
        case .untrustedCertificate:
            if let http = await fallback(local) { return (http, false, true) }
        case .unreachable:
            break
        }
        if let remote, await probe(remote) != .unreachable {
            return (remote, true, false)
        }
        return nil
    }
}
