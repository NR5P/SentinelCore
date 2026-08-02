import Foundation

// The server's build identity as /api/health reports it - see BuildInfo.cs, which produces these
// exact field names. That endpoint is deliberately unauthenticated, so this works even while the
// app is sitting at the login screen.
struct ServerBuild: Decodable, Equatable {
    let version: String
    let commit: String
    let isDirty: Bool
    let display: String
}

struct ServerHealth: Decodable {
    let status: String
    // Absent on any server older than the version-stamping update, in the same way AuthStatus.role
    // is absent before multi-account. Its absence is itself a version signal - see
    // ServerCompatibility.evaluate.
    let build: ServerBuild?
}

// Just the numeric major.minor.patch, which is all this app ever needs to order two builds.
// SemVer pre-release and build metadata ("1.2.0-beta.1", "1.0.0+ab12cd.dirty") are dropped rather
// than parsed: the commit is already reported separately, and nothing here should refuse to
// compare two versions because one of them was built from a dirty tree.
struct ServerVersion: Comparable, Equatable, CustomStringConvertible {
    let major: Int
    let minor: Int
    let patch: Int

    init(_ major: Int, _ minor: Int, _ patch: Int) {
        self.major = major
        self.minor = minor
        self.patch = patch
    }

    init?(_ text: String) {
        let numeric = text.trimmingCharacters(in: .whitespaces).prefix { $0.isNumber || $0 == "." }
        let parts = numeric.split(separator: ".").map(String.init)
        guard let major = parts.first.flatMap({ Int($0) }) else { return nil }
        self.major = major
        self.minor = parts.count > 1 ? Int(parts[1]) ?? 0 : 0
        self.patch = parts.count > 2 ? Int(parts[2]) ?? 0 : 0
    }

    var description: String { "\(major).\(minor).\(patch)" }

    static func < (lhs: ServerVersion, rhs: ServerVersion) -> Bool {
        (lhs.major, lhs.minor, lhs.patch) < (rhs.major, rhs.minor, rhs.patch)
    }

    // The oldest server this app is known to work against. Bump this in the same change that
    // starts relying on a server feature the previous release didn't have - that is the entire
    // point of it, and a bump with no such dependency will only nag people for no reason.
    //
    // Worth knowing: every core assembly shares one VersionPrefix (core/Directory.Build.props), and
    // it has sat at 1.0.0 since the beginning. Until releases actually carry distinct version
    // numbers, the only mismatch this can detect is a server old enough to report no build at all.
    static let minimumSupported = ServerVersion(1, 0, 0)
}

// Advisory only, deliberately. A version mismatch between an app and a server on the same LAN is
// usually survivable - most endpoints don't change between releases - and an app that refuses to
// start is far more disruptive than a slightly wrong screen. So this never blocks: it explains
// what's mismatched and leaves the decision to the person holding the phone.
enum ServerCompatibility: Equatable {
    case ok
    // Reported a version older than minimumSupported.
    case outdated(serverVersion: String)
    // Answered, but reported no build at all - older than version reporting itself, so older than
    // anything this app has been built against.
    case predatesVersionReporting
    // /api/health didn't answer, or didn't parse. Says nothing conclusive about the server's
    // version, so it stays silent rather than guessing - a transient blip on a working connection
    // must not raise a scary banner.
    case unknown

    static func evaluate(_ build: ServerBuild?) -> ServerCompatibility {
        guard let build else { return .predatesVersionReporting }
        guard let version = ServerVersion(build.version) else { return .unknown }
        return version < ServerVersion.minimumSupported ? .outdated(serverVersion: build.version) : .ok
    }

    // nil when there is nothing worth telling the user, which is the overwhelmingly common case.
    var advisory: String? {
        switch self {
        case .ok, .unknown:
            nil
        case .outdated(let serverVersion):
            "This server is running version \(serverVersion), older than the \(ServerVersion.minimumSupported) this app expects. Most things still work, but some screens may be wrong or empty until the server is updated."
        case .predatesVersionReporting:
            "This server is too old to report its version, which means it predates several features this app uses. Most things still work, but some screens may be wrong or empty until the server is updated."
        }
    }
}
