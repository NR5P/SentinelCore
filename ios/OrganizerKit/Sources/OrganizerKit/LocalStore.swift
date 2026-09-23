import Foundation

// This device's copy of the Organizer: items, the queue of edits not yet acknowledged, sync state
// and the index of attachment files on disk. One SQLite file plus two directories, all under
// `directory`, so wiping the copy (signed in as someone else, or chose the new server's copy after
// a rebuild) is one call.
//
// Items are stored whole as JSON rather than spread over columns. Only this device reads them, the
// sync engine keeps them in memory while running, and JSON is what lets fields this build doesn't
// know about survive untouched.
final class LocalStore {
    let directory: URL
    private let database: SQLiteDatabase
    private let encoder = JSONEncoder.organizer
    private let decoder = JSONDecoder.organizer

    var blobDirectory: URL { directory.appendingPathComponent("blobs", isDirectory: true) }
    var thumbnailDirectory: URL { directory.appendingPathComponent("thumbnails", isDirectory: true) }

    // nil directory: everything in memory and a temporary folder for files - the tests, and
    // nothing else.
    init(directory: URL?) throws {
        let root = directory ?? FileManager.default.temporaryDirectory.appendingPathComponent("organizer-\(UUID().uuidString)", isDirectory: true)
        self.directory = root
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        database = try SQLiteDatabase(path: directory == nil ? nil : root.appendingPathComponent("organizer.sqlite").path)
        try FileManager.default.createDirectory(at: blobDirectory, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: thumbnailDirectory, withIntermediateDirectories: true)
        try database.execute("""
            CREATE TABLE IF NOT EXISTS items (id TEXT PRIMARY KEY, json BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS pending (id TEXT PRIMARY KEY, json BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS blobs (
                hash TEXT PRIMARY KEY,
                needsUpload INTEGER NOT NULL DEFAULT 0,
                hasOriginal INTEGER NOT NULL DEFAULT 0,
                size INTEGER NOT NULL DEFAULT 0,
                lastUsed REAL NOT NULL DEFAULT 0
            );
            """)
    }

    // MARK: items

    func loadItems() -> [OrganizerItem] {
        var items: [OrganizerItem] = []
        try? database.query("SELECT json FROM items") { row in
            if let data = row.data(0), let item = try? decoder.decode(OrganizerItem.self, from: data) { items.append(item) }
        }
        return items
    }

    func save(_ items: [OrganizerItem]) {
        guard !items.isEmpty else { return }
        try? database.transaction {
            for item in items {
                guard let data = try? encoder.encode(item) else { continue }
                try database.run("INSERT INTO items (id, json) VALUES (?, ?) ON CONFLICT(id) DO UPDATE SET json = excluded.json",
                                 [.text(item.id), .blob(data)])
            }
        }
    }

    func deleteItems(_ ids: [String]) {
        guard !ids.isEmpty else { return }
        try? database.transaction {
            for id in ids { try database.run("DELETE FROM items WHERE id = ?", [.text(id)]) }
        }
    }

    // MARK: pending edits

    func loadPending() -> [String: OrganizerChange] {
        var pending: [String: OrganizerChange] = [:]
        try? database.query("SELECT id, json FROM pending") { row in
            if let id = row.text(0), let data = row.data(1), let change = try? decoder.decode(OrganizerChange.self, from: data) {
                pending[id] = change
            }
        }
        return pending
    }

    func savePending(_ change: OrganizerChange?, id: String) {
        if let change, let data = try? encoder.encode(change) {
            _ = try? database.run("INSERT INTO pending (id, json) VALUES (?, ?) ON CONFLICT(id) DO UPDATE SET json = excluded.json",
                              [.text(id), .blob(data)])
        } else {
            _ = try? database.run("DELETE FROM pending WHERE id = ?", [.text(id)])
        }
    }

    // MARK: meta

    func meta(_ key: String) -> String? {
        var value: String?
        try? database.query("SELECT value FROM meta WHERE key = ?", [.text(key)]) { row in value = row.text(0) }
        return value
    }

    func setMeta(_ key: String, _ value: String?) {
        if let value {
            _ = try? database.run("INSERT INTO meta (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                              [.text(key), .text(value)])
        } else {
            _ = try? database.run("DELETE FROM meta WHERE key = ?", [.text(key)])
        }
    }

    // MARK: attachment files

    struct BlobRecord: Hashable {
        var hash: String
        var needsUpload: Bool
        var hasOriginal: Bool
        var size: Int64
        var lastUsed: Date
    }

    func blob(_ hash: String) -> BlobRecord? {
        var record: BlobRecord?
        try? database.query("SELECT hash, needsUpload, hasOriginal, size, lastUsed FROM blobs WHERE hash = ?", [.text(hash)]) { row in
            record = Self.blobRecord(row)
        }
        return record
    }

    func allBlobs() -> [BlobRecord] {
        var records: [BlobRecord] = []
        try? database.query("SELECT hash, needsUpload, hasOriginal, size, lastUsed FROM blobs") { row in
            records.append(Self.blobRecord(row))
        }
        return records
    }

    func saveBlob(_ record: BlobRecord) {
        _ = try? database.run("""
            INSERT INTO blobs (hash, needsUpload, hasOriginal, size, lastUsed) VALUES (?, ?, ?, ?, ?)
            ON CONFLICT(hash) DO UPDATE SET needsUpload = excluded.needsUpload, hasOriginal = excluded.hasOriginal,
                size = excluded.size, lastUsed = excluded.lastUsed
            """, [.text(record.hash), .int(record.needsUpload ? 1 : 0), .int(record.hasOriginal ? 1 : 0),
                  .int(record.size), .real(record.lastUsed.timeIntervalSince1970)])
    }

    func deleteBlob(_ hash: String) {
        _ = try? database.run("DELETE FROM blobs WHERE hash = ?", [.text(hash)])
        try? FileManager.default.removeItem(at: originalURL(hash))
        try? FileManager.default.removeItem(at: thumbnailURL(hash))
    }

    func originalURL(_ hash: String) -> URL { blobDirectory.appendingPathComponent(hash) }
    func thumbnailURL(_ hash: String) -> URL { thumbnailDirectory.appendingPathComponent(hash + ".jpg") }

    private static func blobRecord(_ row: SQLiteDatabase.Row) -> BlobRecord {
        BlobRecord(hash: row.text(0) ?? "", needsUpload: row.int(1) != 0, hasOriginal: row.int(2) != 0,
                   size: row.int(3), lastUsed: Date(timeIntervalSince1970: row.real(4)))
    }

    // MARK: wiping

    // Everything this device holds for the Organizer - except edits still waiting to go up when
    // `keepPending` is set - including attachment files not yet uploaded when it isn't.
    func wipe(keepPending: Bool) {
        try? database.execute("DELETE FROM items; DELETE FROM meta;")
        if !keepPending {
            try? database.execute("DELETE FROM pending;")
            try? database.execute("DELETE FROM blobs;")
            try? FileManager.default.removeItem(at: blobDirectory)
            try? FileManager.default.removeItem(at: thumbnailDirectory)
            try? FileManager.default.createDirectory(at: blobDirectory, withIntermediateDirectories: true)
            try? FileManager.default.createDirectory(at: thumbnailDirectory, withIntermediateDirectories: true)
        }
    }

    // Every item except those with edits still waiting to go up - those have to stay visible, or
    // an unsent note would vanish until it synced.
    func clearItems(keeping: Set<String> = []) {
        guard !keeping.isEmpty else {
            try? database.execute("DELETE FROM items;")
            return
        }
        var doomed: [String] = []
        try? database.query("SELECT id FROM items") { row in
            if let id = row.text(0), !keeping.contains(id) { doomed.append(id) }
        }
        deleteItems(doomed)
    }
}
