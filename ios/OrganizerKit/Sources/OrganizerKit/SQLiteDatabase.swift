import Foundation
import SQLite3

// Just enough SQLite for the Organizer's local copy: open, execute, bind text/real/int/blob,
// step rows. SQLite ships with every Apple OS, so this costs no dependency.
final class SQLiteDatabase {
    private var handle: OpaquePointer?
    private static let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

    struct Failure: Error, CustomStringConvertible {
        let message: String
        var description: String { message }
    }

    // nil path means an in-memory database - used when this device keeps no offline copy, and
    // by the tests.
    init(path: String?) throws {
        let target = path ?? ":memory:"
        let flags = SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX
        guard sqlite3_open_v2(target, &handle, flags, nil) == SQLITE_OK else {
            let message = handle.map { String(cString: sqlite3_errmsg($0)) } ?? "could not open \(target)"
            sqlite3_close(handle)
            throw Failure(message: message)
        }
        if path != nil { try execute("PRAGMA journal_mode=WAL;") }
        try execute("PRAGMA busy_timeout=5000;")
    }

    deinit { sqlite3_close(handle) }

    func execute(_ sql: String) throws {
        var error: UnsafeMutablePointer<CChar>?
        guard sqlite3_exec(handle, sql, nil, nil, &error) == SQLITE_OK else {
            let message = error.map { String(cString: $0) } ?? "unknown error"
            sqlite3_free(error)
            throw Failure(message: message)
        }
    }

    enum Value {
        case text(String?)
        case real(Double)
        case int(Int64)
        case blob(Data)
    }

    @discardableResult
    func run(_ sql: String, _ values: [Value] = []) throws -> Int {
        let statement = try prepare(sql, values)
        defer { sqlite3_finalize(statement) }
        let result = sqlite3_step(statement)
        guard result == SQLITE_DONE || result == SQLITE_ROW else { throw lastError }
        return Int(sqlite3_changes(handle))
    }

    func query(_ sql: String, _ values: [Value] = [], row: (Row) throws -> Void) throws {
        let statement = try prepare(sql, values)
        defer { sqlite3_finalize(statement) }
        while true {
            let result = sqlite3_step(statement)
            if result == SQLITE_DONE { return }
            guard result == SQLITE_ROW else { throw lastError }
            try row(Row(statement: statement))
        }
    }

    func transaction(_ work: () throws -> Void) throws {
        try execute("BEGIN IMMEDIATE;")
        do {
            try work()
            try execute("COMMIT;")
        } catch {
            try? execute("ROLLBACK;")
            throw error
        }
    }

    struct Row {
        let statement: OpaquePointer?
        func text(_ index: Int32) -> String? {
            guard let pointer = sqlite3_column_text(statement, index) else { return nil }
            return String(cString: pointer)
        }
        func real(_ index: Int32) -> Double { sqlite3_column_double(statement, index) }
        func int(_ index: Int32) -> Int64 { sqlite3_column_int64(statement, index) }
        func data(_ index: Int32) -> Data? {
            guard let bytes = sqlite3_column_blob(statement, index) else { return nil }
            return Data(bytes: bytes, count: Int(sqlite3_column_bytes(statement, index)))
        }
    }

    private var lastError: Failure { Failure(message: String(cString: sqlite3_errmsg(handle))) }

    private func prepare(_ sql: String, _ values: [Value]) throws -> OpaquePointer? {
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(handle, sql, -1, &statement, nil) == SQLITE_OK else { throw lastError }
        for (offset, value) in values.enumerated() {
            let index = Int32(offset + 1)
            switch value {
            case let .text(text):
                if let text { sqlite3_bind_text(statement, index, text, -1, Self.transient) } else { sqlite3_bind_null(statement, index) }
            case let .real(number): sqlite3_bind_double(statement, index, number)
            case let .int(number): sqlite3_bind_int64(statement, index, number)
            case let .blob(data):
                _ = data.withUnsafeBytes { sqlite3_bind_blob(statement, index, $0.baseAddress, Int32(data.count), Self.transient) }
            }
        }
        return statement
    }
}
