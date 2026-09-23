import Foundation

// A field value exactly as it travels. Items carry free-form fields (see ORGANIZER_SYNC.md), and
// a field this build doesn't know about - one a newer app added - still has to be stored and sent
// back untouched, or an older phone would quietly erase it. So fields are kept as JSON, never
// decoded into fixed structs.
public enum JSONValue: Codable, Hashable, Sendable {
    case null
    case bool(Bool)
    case number(Double)
    case string(String)
    case array([JSONValue])
    case object([String: JSONValue])

    public init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() {
            self = .null
        } else if let value = try? container.decode(Bool.self) {
            self = .bool(value)
        } else if let value = try? container.decode(Double.self) {
            self = .number(value)
        } else if let value = try? container.decode(String.self) {
            self = .string(value)
        } else if let value = try? container.decode([JSONValue].self) {
            self = .array(value)
        } else {
            self = .object(try container.decode([String: JSONValue].self))
        }
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .null: try container.encodeNil()
        case let .bool(value): try container.encode(value)
        case let .number(value):
            // Whole numbers go out without a fraction, so a priority of 2 reads back as 2 on
            // every platform rather than 2.0.
            if value.rounded() == value, abs(value) < 9_007_199_254_740_992 {
                try container.encode(Int64(value))
            } else {
                try container.encode(value)
            }
        case let .string(value): try container.encode(value)
        case let .array(value): try container.encode(value)
        case let .object(value): try container.encode(value)
        }
    }

    public var string: String? { if case let .string(value) = self { value } else { nil } }
    public var bool: Bool? { if case let .bool(value) = self { value } else { nil } }
    public var number: Double? { if case let .number(value) = self { value } else { nil } }
    public var int: Int? { number.map { Int($0) } }
    public var array: [JSONValue]? { if case let .array(value) = self { value } else { nil } }
    public var object: [String: JSONValue]? { if case let .object(value) = self { value } else { nil } }
    public var isNull: Bool { if case .null = self { true } else { false } }
}

extension JSONValue: ExpressibleByStringLiteral, ExpressibleByBooleanLiteral, ExpressibleByIntegerLiteral,
    ExpressibleByFloatLiteral, ExpressibleByArrayLiteral, ExpressibleByDictionaryLiteral, ExpressibleByNilLiteral {
    public init(stringLiteral value: String) { self = .string(value) }
    public init(booleanLiteral value: Bool) { self = .bool(value) }
    public init(integerLiteral value: Int) { self = .number(Double(value)) }
    public init(floatLiteral value: Double) { self = .number(value) }
    public init(arrayLiteral elements: JSONValue...) { self = .array(elements) }
    public init(dictionaryLiteral elements: (String, JSONValue)...) { self = .object(Dictionary(elements, uniquingKeysWith: { $1 })) }
    public init(nilLiteral: ()) { self = .null }
}

extension JSONValue {
    public static func from(_ string: String?) -> JSONValue { string.map { .string($0) } ?? .null }
    public static func from(_ number: Double?) -> JSONValue { number.map { .number($0) } ?? .null }
    public static func from(_ number: Int?) -> JSONValue { number.map { .number(Double($0)) } ?? .null }
    public static func from(_ flag: Bool?) -> JSONValue { flag.map { .bool($0) } ?? .null }
    public static func from(_ strings: [String]?) -> JSONValue { strings.map { .array($0.map(JSONValue.string)) } ?? .null }
}
