import Foundation
import Security

// Recognising this app's own server when its HTTPS certificate isn't trusted.
// The same file lives at appletv/SentinelCoreTV/Networking/ServerIdentity.swift - keep the two identical.
//
// The server makes its own certificate authority, which no Apple device trusts out of the box, so
// AppModel falls back to the server's plain-HTTP port when the HTTPS address fails trust. That
// fallback used to happen for anything that failed trust at the configured address - which, on
// someone else's network, can be anything at all that answers at the same private IP. It then got
// the session cookie, and the Keychain password on the automatic re-login.
//
// So the fallback is now only taken for a server that proves it is the same one: the first time
// the fallback works, the server's certificate authority is remembered (trust on first use - in
// practice at home, during setup); every fallback after that requires the server to complete a
// TLS handshake with a certificate that authority signed. Completing the handshake - not just
// showing the certificate - is what proves it holds the key, so a copy of the real certificate
// isn't enough. The check itself sends nothing: an ephemeral session with no cookies, and the
// handshake is abandoned whenever the certificate doesn't verify.
//
// The server reissues its own certificate when its IP address changes but keeps its authority,
// which is why it's the authority that's remembered. A reinstalled server has a new authority and
// is refused until its address is saved again in Settings (see forget).
enum ServerIdentity {
    enum Check {
        // Completed a handshake with a certificate the remembered authority signed.
        case verified
        // Something answered with a certificate the remembered authority didn't sign.
        case mismatch
        // Nothing remembered for this address yet; the certificate it showed, to check the
        // authority against once the fallback has worked.
        case unknown(SecCertificate)
        // No certificate to judge - the address stopped answering between the failed attempt and
        // this check.
        case unavailable
    }

    static func check(_ httpsAddress: String) async -> Check {
        guard let url = URL(string: httpsAddress.trimmingCharacters(in: .whitespacesAndNewlines)),
              url.scheme == "https", url.host != nil else { return .unavailable }
        let authority = rememberedAuthority(for: httpsAddress)
        let inspector = CertificateInspector(authority: authority)
        let session = URLSession(configuration: .ephemeral, delegate: inspector, delegateQueue: nil)
        defer { session.invalidateAndCancel() }
        var request = URLRequest(url: url.appending(path: "api/health"))
        request.timeoutInterval = 4
        let answered = (try? await session.data(for: request)) != nil

        guard let leaf = inspector.leaf else { return .unavailable }
        guard authority != nil else { return .unknown(leaf) }
        if inspector.chainedToAuthority { return answered ? .verified : .unavailable }
        return .mismatch
    }

    // Called once the fallback has actually worked with nothing remembered: fetches the server's
    // authority and keeps it only if it really did sign the certificate the server presented.
    static func remember(authority data: Data, signing leaf: SecCertificate, for httpsAddress: String) -> Bool {
        guard let key = storageKey(for: httpsAddress), let authority = certificate(from: data),
              signs(authority, leaf) else { return false }
        UserDefaults.standard.set(SecCertificateCopyData(authority) as Data, forKey: key)
        return true
    }

    // The server's certificate authority, from the plain-HTTP address the fallback just reached.
    // Ephemeral, like check: nothing of the app's own session goes with it.
    static func fetchAuthority(from httpAddress: String) async -> Data? {
        guard let url = URL(string: httpAddress.trimmingCharacters(in: .whitespacesAndNewlines))?
            .appending(path: "api/certificate-authority") else { return nil }
        let session = URLSession(configuration: .ephemeral)
        defer { session.finishTasksAndInvalidate() }
        var request = URLRequest(url: url)
        request.timeoutInterval = 5
        guard let (data, response) = try? await session.data(for: request),
              (response as? HTTPURLResponse)?.statusCode == 200 else { return nil }
        return data
    }

    static func forget(_ httpsAddress: String) {
        guard let key = storageKey(for: httpsAddress) else { return }
        UserDefaults.standard.removeObject(forKey: key)
    }

    private static func rememberedAuthority(for httpsAddress: String) -> SecCertificate? {
        guard let key = storageKey(for: httpsAddress),
              let data = UserDefaults.standard.data(forKey: key) else { return nil }
        return SecCertificateCreateWithData(nil, data as CFData)
    }

    // Per host and port - the same server whichever path or trailing slash the address was saved with.
    private static func storageKey(for httpsAddress: String) -> String? {
        guard let components = URLComponents(string: httpsAddress.trimmingCharacters(in: .whitespacesAndNewlines)),
              let host = components.host?.lowercased(), !host.isEmpty else { return nil }
        return "serverIdentity.authority.\(host):\(components.port ?? 443)"
    }

    // The server serves ca.crt as PEM; DER is accepted too.
    private static func certificate(from data: Data) -> SecCertificate? {
        if let text = String(data: data, encoding: .utf8), text.contains("-----BEGIN CERTIFICATE-----") {
            let body = text
                .components(separatedBy: "-----BEGIN CERTIFICATE-----").dropFirst().first?
                .components(separatedBy: "-----END CERTIFICATE-----").first?
                .components(separatedBy: .whitespacesAndNewlines).joined() ?? ""
            guard let der = Data(base64Encoded: body) else { return nil }
            return SecCertificateCreateWithData(nil, der as CFData)
        }
        return SecCertificateCreateWithData(nil, data as CFData)
    }

    // Plain X.509 chain building against that one authority only. Not the SSL policy: the question
    // is "did our authority sign this", and the host name is already fixed by the address the
    // certificate was fetched from.
    fileprivate static func signs(_ authority: SecCertificate, _ leaf: SecCertificate) -> Bool {
        var trust: SecTrust?
        guard SecTrustCreateWithCertificates(leaf, SecPolicyCreateBasicX509(), &trust) == errSecSuccess,
              let trust else { return false }
        return evaluate(trust, against: authority)
    }

    fileprivate static func evaluate(_ trust: SecTrust, against authority: SecCertificate) -> Bool {
        SecTrustSetPolicies(trust, SecPolicyCreateBasicX509())
        SecTrustSetAnchorCertificates(trust, [authority] as CFArray)
        SecTrustSetAnchorCertificatesOnly(trust, true)
        return SecTrustEvaluateWithError(trust, nil)
    }
}

// Sees the server's certificate during the handshake and decides whether to finish it.
private final class CertificateInspector: NSObject, URLSessionDelegate, @unchecked Sendable {
    private let authority: SecCertificate?
    private let lock = NSLock()
    private var _leaf: SecCertificate?
    private var _chainedToAuthority = false

    init(authority: SecCertificate?) {
        self.authority = authority
    }

    var leaf: SecCertificate? { lock.withLock { _leaf } }
    var chainedToAuthority: Bool { lock.withLock { _chainedToAuthority } }

    func urlSession(_ session: URLSession, didReceive challenge: URLAuthenticationChallenge) async
        -> (URLSession.AuthChallengeDisposition, URLCredential?) {
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              let trust = challenge.protectionSpace.serverTrust else {
            return (.cancelAuthenticationChallenge, nil)
        }
        let leaf = (SecTrustCopyCertificateChain(trust) as? [SecCertificate])?.first
        let chained = authority.map { ServerIdentity.evaluate(trust, against: $0) } ?? false
        lock.withLock {
            _leaf = leaf
            _chainedToAuthority = chained
        }
        // Finish the handshake only for our own authority's certificate; anything else is
        // abandoned before a single byte of the request is sent.
        return chained ? (.useCredential, URLCredential(trust: trust)) : (.cancelAuthenticationChallenge, nil)
    }
}
