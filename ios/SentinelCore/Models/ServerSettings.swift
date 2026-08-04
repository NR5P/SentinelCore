import Foundation

// The server-side settings schema - which sections exist, what fields they contain, and how to
// render each one. Served by GET /api/settings/schema and shared verbatim with the web dashboard
// and the Android app, so a settings section added on the server appears here with no app
// update. This file only mirrors the shapes; ServerSettingsView renders them.

struct SettingsFieldOption: Decodable, Hashable {
    let value: String
    let label: String
}

// An extra button a section offers (currently just Bridge's "re-run setup wizard") - pure data,
// an endpoint to POST to, so no client needs bespoke code per action.
struct SettingsActionSchema: Decodable, Hashable {
    let key: String
    let label: String
    let tip: String
    let endpoint: String
    let method: String
}

struct SettingsFieldSchema: Decodable, Identifiable, Hashable {
    let name: String
    let label: String
    // "text", "number", "checkbox", "password", "select", or "csv". Anything unrecognized is
    // rendered read-only rather than guessed at - better than writing back something malformed.
    let type: String
    let tip: String
    let min: Double?
    let max: Double?
    let step: Double?
    let options: [SettingsFieldOption]?
    let requiresRestart: Bool
    let secret: Bool

    var id: String { name }
}

struct SettingsSectionSchema: Decodable, Identifiable, Hashable {
    let key: String
    let title: String
    let endpoint: String
    let basic: Bool
    let description: String
    let fields: [SettingsFieldSchema]
    let extraAction: SettingsActionSchema?

    var id: String { key }

    var needsRestart: Bool { fields.contains { $0.requiresRestart } }
}

// One JSON value of any shape. GET /api/settings mixes strings, numbers, booleans, arrays, and
// nested objects (PushNotifications' apple/firebase blocks) in a single payload whose shape the
// app deliberately doesn't hard-code - that's the schema's job.
enum SettingsValue: Codable, Equatable {
    case string(String)
    case number(Double)
    case bool(Bool)
    case object([String: SettingsValue])
    case array([SettingsValue])
    case null

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() {
            self = .null
        } else if let value = try? container.decode(Bool.self) {
            self = .bool(value)
        } else if let value = try? container.decode(Double.self) {
            self = .number(value)
        } else if let value = try? container.decode(String.self) {
            self = .string(value)
        } else if let value = try? container.decode([SettingsValue].self) {
            self = .array(value)
        } else if let value = try? container.decode([String: SettingsValue].self) {
            self = .object(value)
        } else {
            throw DecodingError.dataCorruptedError(in: container, debugDescription: "Unsupported JSON value")
        }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .string(let value): try container.encode(value)
        case .number(let value): try container.encode(value)
        case .bool(let value): try container.encode(value)
        case .object(let value): try container.encode(value)
        case .array(let value): try container.encode(value)
        case .null: try container.encodeNil()
        }
    }

    // Field names in the schema can be dotted paths into nested objects ("apple.teamId") -
    // the same convention the dashboard's readPath/writePath use.
    static func read(_ object: [String: SettingsValue]?, path: String) -> SettingsValue? {
        guard var current = object else { return nil }
        let segments = path.split(separator: ".").map(String.init)
        for segment in segments.dropLast() {
            guard case .object(let nested)? = current[segment] else { return nil }
            current = nested
        }
        return segments.last.flatMap { current[$0] }
    }

    static func write(_ object: inout [String: SettingsValue], path: String, value: SettingsValue) {
        let segments = path.split(separator: ".").map(String.init)
        guard let leaf = segments.last else { return }
        if segments.count == 1 {
            object[leaf] = value
            return
        }
        // Rebuild the nested object chain immutably - paths here are at most two levels deep
        // (apple.teamId), but this handles any depth the schema might grow.
        var nested: [String: SettingsValue]
        if case .object(let existing)? = object[segments[0]] {
            nested = existing
        } else {
            nested = [:]
        }
        var remainder = segments
        remainder.removeFirst()
        write(&nested, path: remainder.joined(separator: "."), value: value)
        object[segments[0]] = .object(nested)
    }

    // Display forms for seeding text fields. Integral numbers drop the ".0" so a port renders
    // as 1883, not 1883.0.
    var displayString: String {
        switch self {
        case .string(let value): return value
        case .bool(let value): return value ? "true" : "false"
        case .number(let value):
            return value == value.rounded() && abs(value) < 1e15
                ? String(Int64(value))
                : String(value)
        case .array(let values):
            return values.map(\.displayString).joined(separator: ", ")
        case .object, .null: return ""
        }
    }

    var boolValue: Bool {
        if case .bool(let value) = self { return value }
        return false
    }
}

struct SettingsSaveResult: Decodable {
    let saved: Bool?
    let restartRequired: Bool?
}

// Rebooting the machine the server runs on - mirrors HostRebootProbe in HostReboot.cs.
//
// Deliberately carries the server's prose rather than reconstructing it here. Which sudoers line
// to add, on which path, for which user, is a question only the server can answer about itself,
// and an app that guessed would eventually guess wrong on someone's install.
struct HostRebootProbe: Decodable, Equatable {
    let platform: String
    let platformLabel: String
    let readiness: String
    let enabled: Bool
    let runningAs: String
    let summary: String
    let delaySeconds: Int
    let setupScript: String?
    let setupNote: String?
    let scheduledFor: Date?

    // Counted down from locally. Preferred over subtracting scheduledFor from this device's
    // clock, which on a phone that has drifted would produce a confidently wrong countdown.
    let secondsRemaining: Int?

    var isReady: Bool { readiness == "ready" }
    var needsSetup: Bool { readiness == "setupRequired" }
    var isUnsupported: Bool { readiness == "unsupported" }

    // Ready and switched on are separate facts, and the difference is the whole reason this
    // screen exists: one is fixed in this app, the other only at a shell on the server.
    var canReboot: Bool { isReady && enabled }
}

struct HostRebootOutcome: Decodable {
    let ok: Bool
    let message: String
    let scheduledFor: Date?
    let secondsRemaining: Int?
}
