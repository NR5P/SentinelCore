import Foundation

public enum OrganizerKind: String, Codable, CaseIterable, Sendable {
    case note, event, reminder, board, column, card, attachment
}

// One item as this device holds it. spaceId/parentId/deleted are properties here but travel as
// ordinary fields in a change, each with its own clock - see ORGANIZER_SYNC.md.
public struct OrganizerItem: Codable, Hashable, Identifiable, Sendable {
    public var id: String
    public var kind: OrganizerKind
    public var spaceId: String
    public var parentId: String?
    public var deleted: Bool
    public var fields: [String: JSONValue]
    public var clocks: [String: String]
    public var seq: Int64?
    public var createdAt: Date?
    public var createdBy: String?
    public var updatedAt: Date?
    public var updatedBy: String?

    public init(
        id: String, kind: OrganizerKind, spaceId: String, parentId: String? = nil, deleted: Bool = false,
        fields: [String: JSONValue] = [:], clocks: [String: String] = [:], seq: Int64? = nil,
        createdAt: Date? = nil, createdBy: String? = nil, updatedAt: Date? = nil, updatedBy: String? = nil
    ) {
        self.id = id
        self.kind = kind
        self.spaceId = spaceId
        self.parentId = parentId
        self.deleted = deleted
        self.fields = fields
        self.clocks = clocks
        self.seq = seq
        self.createdAt = createdAt
        self.createdBy = createdBy
        self.updatedAt = updatedAt
        self.updatedBy = updatedBy
    }

    public subscript(field: String) -> JSONValue? { fields[field] }
    public func string(_ field: String) -> String? { fields[field]?.string }
    public func bool(_ field: String) -> Bool { fields[field]?.bool ?? false }
    public func number(_ field: String) -> Double? { fields[field]?.number }

    public var title: String { string("title") ?? "" }
    public var position: Double { number("position") ?? .greatestFiniteMagnitude }
}

public enum OrganizerSpaceKind: String, Codable, Sendable {
    case family = "Family"
    case personal = "Personal"
    case group = "Group"
}

public struct OrganizerSpace: Codable, Hashable, Identifiable, Sendable {
    public var id: String
    public var kind: OrganizerSpaceKind
    public var name: String
    public var members: [String]
    public var color: String?
    public var createdBy: String?

    public init(id: String, kind: OrganizerSpaceKind, name: String, members: [String] = [], color: String? = nil, createdBy: String? = nil) {
        self.id = id
        self.kind = kind
        self.name = name
        self.members = members
        self.color = color
        self.createdBy = createdBy
    }

    public static let family = OrganizerSpace(id: "family", kind: .family, name: "Family")
}

// Edits to one item not yet acknowledged by the server. Coalesces: a second edit to the same
// field replaces the first, keeping only the newest value and clock.
public struct OrganizerChange: Codable, Hashable, Sendable {
    public var id: String
    public var kind: OrganizerKind
    public var fields: [String: JSONValue]
    public var clocks: [String: String]
    public var baseClocks: [String: String]?

    public init(id: String, kind: OrganizerKind, fields: [String: JSONValue] = [:], clocks: [String: String] = [:], baseClocks: [String: String]? = nil) {
        self.id = id
        self.kind = kind
        self.fields = fields
        self.clocks = clocks
        self.baseClocks = baseClocks
    }

    // `newer` laid over `self`. The first base wins - it is the version the whole run of edits
    // started from, which is what the server's conflict check needs.
    public func merging(_ newer: OrganizerChange) -> OrganizerChange {
        var merged = self
        for (name, value) in newer.fields {
            merged.fields[name] = value
            if let clock = newer.clocks[name] { merged.clocks[name] = clock }
        }
        if let base = newer.baseClocks {
            merged.baseClocks = base.merging(baseClocks ?? [:]) { _, older in older }
        }
        return merged
    }
}

public struct OrganizerStatus: Codable, Hashable, Sendable {
    public var enabled: Bool
    public var serverId: String
    public var cursor: Int64?
    public var username: String?
    public var displayName: String?
    public var personalSpaces: Bool
    public var itemCount: Int?
    public var maxAttachmentBytes: Int64?
}

public struct OrganizerRejection: Codable, Hashable, Sendable {
    public var id: String
    public var error: String
}

struct SyncRequest: Encodable {
    var serverId: String?
    var cursor: Int64
    var knownSpaces: [String]
    var changes: [OrganizerChange]
    var restoreSpaces: [OrganizerSpace]?
    var limit: Int?
}

struct SyncResponse: Decodable {
    var serverId: String
    var reset: Bool
    var cursor: Int64
    var hasMore: Bool
    var spaces: [OrganizerSpace]
    var items: [OrganizerItem]
    var removed: [String]
    var rejected: [OrganizerRejection]
    var missingBlobs: [String]
}

public struct OrganizerPerson: Codable, Hashable, Identifiable, Sendable {
    public var username: String
    public var displayName: String?
    public var id: String { username }
    public var label: String { displayName?.isEmpty == false ? displayName! : username }
}

struct PeopleResponse: Decodable { var people: [OrganizerPerson] }
struct ItemsResponse: Decodable { var items: [OrganizerItem] }
struct FeedResponse: Decodable { var path: String }
struct WaitResponse: Decodable { var cursor: Int64 }

public struct OrganizerGroupInput: Encodable, Sendable {
    public var name: String
    public var members: [String]
    public var color: String?

    public init(name: String, members: [String], color: String? = nil) {
        self.name = name
        self.members = members
        self.color = color
    }
}

extension JSONDecoder {
    // ASP.NET writes DateTimeOffset with a varying number of fractional digits; accept any.
    static let organizer: JSONDecoder = {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let value = try decoder.singleValueContainer().decode(String.self)
            if let date = OrganizerDates.parseTimestamp(value) { return date }
            throw DecodingError.dataCorruptedError(in: try decoder.singleValueContainer(), debugDescription: "Invalid date: \(value)")
        }
        return decoder
    }()
}

extension JSONEncoder {
    static let organizer: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .custom { date, encoder in
            var container = encoder.singleValueContainer()
            try container.encode(OrganizerDates.timestamp(date))
        }
        return encoder
    }()
}
