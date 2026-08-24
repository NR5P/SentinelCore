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

// What an action reports back. Only the test sends fill this in ("Test email sent to …"), and
// only they need to: an action whose whole result is "it worked" says it with its status code.
// The schema's own reloadAfter is deliberately not modelled here - it tells a browser to reload
// the page, which this app has no equivalent of.
struct SettingsActionResult: Decodable {
    let detail: String?
}

// A "look this up for me" button on a text field (today: LOCATION's Town or City, which fills in
// latitude and longitude). Pure data like SettingsActionSchema - an endpoint taking ?query= - so
// nothing here is location-specific, and a field without one is just a text box.
struct SettingsLookupSchema: Decodable, Hashable {
    let label: String
    let tip: String
    let endpoint: String
}

// One option a lookup came back with: something to show in the list, and the field values to
// write if it's picked. The keys are field names in the same section; a client applies the ones
// it recognises and ignores the rest, so a newer server can't break an older app.
struct SettingsLookupCandidate: Decodable {
    let label: String
    let values: [String: SettingsValue]
}

struct SettingsLookupResponse: Decodable {
    let results: [SettingsLookupCandidate]
}

// Where a text field's likely values can be fetched from (today: the models pulled on the Ollama
// machine, for the two Model boxes). Pure data like SettingsLookupSchema - an endpoint taking no
// parameters - so nothing here is Ollama-specific.
//
// A field carrying one is drawn as a picker, but never as a plain picker: the value that is saved
// now is always in the list, and "Type a name instead…" is always the last entry. The list is only
// what is installed at this moment, and naming a model you are about to pull has to keep working.
struct SettingsOptionsSource: Decodable, Hashable {
    let label: String
    let tip: String
    let endpoint: String
    // Shown when the endpoint answers with an empty list. From the server because it is the one
    // message that names what to go and do ("ollama pull …"), which this app shouldn't know.
    let emptyMessage: String
}

struct SettingsOptionsResponse: Decodable {
    let options: [SettingsFieldOption]
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

    // Whether a "number" field holds a whole number, so 3.7 in a port box is caught here with a
    // sentence rather than by the server as a model-binding failure. Optional, unlike secret and
    // requiresRestart next to it, only because it was added later: against a server that predates
    // it the value is nil, and nil has to mean "don't apply a restriction this server never asked
    // for" rather than failing to decode the whole schema.
    let integer: Bool?

    // What to use if you have no opinion, for the numeric settings where that is the honest
    // position - see SettingsFieldSchema.Recommended on the server. Absent on most fields, and
    // deliberately not the same thing as the shipped default.
    let recommended: Double?
    let options: [SettingsFieldOption]?

    // Absent on all but a handful of text fields - see SettingsLookupSchema.
    let lookup: SettingsLookupSchema?

    // Absent on all but a handful of text fields - see SettingsOptionsSource. Optional for the
    // same reason `integer` above is: against a server that predates it the key is simply missing,
    // and that has to mean "an ordinary text box", not "fail to decode the whole schema".
    let optionsSource: SettingsOptionsSource?
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

    // Which heading this section belongs under, and where that heading sorts. Both come from the
    // server (SettingsGroups) so this app and the web dashboard divide the settings up the same
    // way without either holding its own copy of the grouping.
    //
    // Optional purely so this app still renders against an older server that predates grouping -
    // there, everything lands in one unnamed group, which is exactly the flat list it had before.
    let group: String?
    let groupOrder: Int?

    // Which service has to restart for requiresRestart fields here to take effect. Defaults to
    // the bridge, as every section but INTERNET EXPOSED MODE does.
    let restartService: String?

    // Where to GET this section's current values, when they aren't in the bulk /api/settings
    // snapshot. Only INTERNET EXPOSED MODE sets it - see SettingsSectionSchema.DataEndpoint.
    let dataEndpoint: String?

    var id: String { key }

    var needsRestart: Bool { fields.contains { $0.requiresRestart } }

    var restartTarget: String { restartService ?? "bridge" }
}

// One JSON value of any shape. GET /api/settings mixes strings, numbers, booleans, arrays, and
// nested objects (PushNotifications' apple/firebase blocks) in a single payload whose shape the
// app deliberately doesn't hard-code - that's the schema's job.
enum SettingsValue: Codable, Equatable, Sendable {
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
