import Foundation

// The wire shapes of /api/family-files - see core/SentinelCore.Web/FamilyFilesEndpoints.cs and the
// contract it implements. Everything the server may leave out or send as null is optional here,
// so a status from a server with the feature switched off (which carries only `enabled`,
// `isAdmin` and `username`) decodes as happily as a full one.

struct FamilyFilesStatus: Codable {
    let enabled: Bool
    let isAdmin: Bool?
    let username: String?
    let libraryFolder: String?
    let maxUploadBytes: Int64?
    let library: FamilyFilesLibraryTotals?
    let drives: [FamilyDrive]?
    let nightly: FamilyFilesNightly?
    let duplicates: FamilyDuplicateReport?
    let trash: FamilyFilesTrashTotals?
    let job: FamilyFilesJob?
}

struct FamilyFilesLibraryTotals: Codable, Hashable {
    let files: Int
    let bytes: Int64
    let indexedUtc: Date?
}

struct FamilyFilesTrashTotals: Codable, Hashable {
    let batches: Int
    let files: Int
    let bytes: Int64
}

struct FamilyFilesNightly: Codable, Hashable {
    let hour: Int?
    let minute: Int?
    let removeDuplicates: Bool?
    let trashKeepDays: Int?
    let nextRunUtc: Date?
    let lastRunUtc: Date?
    let lastResult: String?
    let lastMessage: String?
}

// What is running on the server right now - a backup, a restore, the duplicate check, the
// nightly pass or the index. Clients poll /status every couple of seconds while one exists.
struct FamilyFilesJob: Codable, Hashable {
    let kind: String
    let driveId: String?
    let driveName: String?
    let startedUtc: Date?
    let message: String
    let done: Int
    let total: Int

    var title: String {
        switch kind {
        case "nightly": "Nightly pass"
        case "sync": driveName.map { "Backing up to \($0)" } ?? "Backing up"
        case "restore": driveName.map { "Bringing files back from \($0)" } ?? "Bringing files back"
        case "duplicates": "Checking for duplicates"
        case "index": "Indexing the library"
        default: kind.capitalized
        }
    }
}

struct FamilyFilesJobResponse: Codable {
    let job: FamilyFilesJob?
}

// A drive the library is copied to: a folder the server can see, or another computer over SFTP.
// The connection details come only to an admin; a viewer gets the name, where it points in
// words, and how the last backup went.
struct FamilyDrive: Codable, Identifiable, Hashable {
    let id: String
    let name: String
    let kind: String
    let enabled: Bool
    let location: String?
    let folderPath: String?
    let sftpHost: String?
    let sftpPort: Int?
    let sftpUsername: String?
    let sftpFolder: String?
    let sftpHostKeyFingerprint: String?
    let hasPassword: Bool?
    let lastAttemptUtc: Date?
    let lastSuccessUtc: Date?
    let lastResult: String?
    let lastMessage: String?
    let lastFileCount: Int?
    let lastBytes: Int64?
    let lastCopied: Int?
    let lastRemoved: Int?
    let lastFreeBytes: Int64?
    let lastHostKeyFingerprint: String?

    static let kindFolder = "folder"
    static let kindSftp = "sftp"

    var isSftp: Bool { kind.lowercased() == Self.kindSftp }
    var kindLabel: String { isSftp ? "Another computer (SFTP)" : "Folder" }
}

// What the drive forms send. Every field goes every time; blanks are fine for the kind not in
// use, and a blank password on an existing drive keeps the saved one. `id` is only read by
// /drives/test, so it can tell a blank password means "the saved one".
struct FamilyDriveInput: Encodable {
    var id: String?
    var name: String
    var kind: String
    var enabled: Bool
    var folderPath: String
    var sftpHost: String
    var sftpPort: Int
    var sftpUsername: String
    var sftpPassword: String
    var sftpFolder: String
    var sftpHostKeyFingerprint: String
}

struct FamilyDrivesResponse: Codable {
    let drives: [FamilyDrive]
}

struct FamilyDriveTestResult: Codable {
    let detail: String?
    let hostKeyFingerprint: String?
}

// The last duplicate check. `sample` (the first groups, for showing) is only on GET /duplicates,
// not in the status summary.
struct FamilyDuplicateReport: Codable, Hashable {
    let ranUtc: Date?
    let removed: Bool
    let groups: Int
    let copies: Int
    let bytes: Int64
    let filesChecked: Int
    let trashBatchId: String?
    let sample: [FamilyDuplicateGroup]?
}

struct FamilyDuplicateGroup: Codable, Hashable, Identifiable {
    let kept: String
    let copies: [String]
    let size: Int64
    var id: String { kept }
}

struct FamilyDuplicatesResponse: Codable {
    let report: FamilyDuplicateReport?
}

// One folder of the library: its subfolders and files. `path` is "" at the root.
struct FamilyFolderListing: Codable {
    let path: String
    let name: String
    let parent: String?
    let folders: [FamilyFolderEntry]
    let files: [FamilyFileEntry]
}

struct FamilyFolderEntry: Codable, Identifiable, Hashable {
    let path: String
    let name: String
    var id: String { path }
}

struct FamilyFileEntry: Codable, Identifiable, Hashable {
    let path: String
    let name: String
    let size: Int64
    let modifiedUtc: Date?
    let sha256: String?
    let isImage: Bool?
    let contentType: String?
    var id: String { path }

    var isPicture: Bool { isImage ?? false }

    // The folder the file sits in, "" for the root.
    var folder: String {
        guard let slash = path.lastIndex(of: "/") else { return "" }
        return String(path[..<slash])
    }
}

struct FamilySearchResult: Codable {
    let files: [FamilyFileEntry]
}

struct FamilyUploadResult: Codable {
    let path: String
    let name: String
    let size: Int64
    let sha256: String?
    let alreadyThere: Bool
}

struct FamilyPathResult: Codable {
    let path: String
}

struct FamilyDeleteResult: Codable {
    let trashed: Int
    let batchId: String
}

// The trash: deletions and removed duplicates, batched by the action that made them, so a
// whole mistaken delete goes back in one tap.
struct FamilyTrashListing: Codable {
    let batches: [FamilyTrashBatch]
}

struct FamilyTrashBatch: Codable, Identifiable, Hashable {
    let id: String
    let deletedUtc: Date?
    let reason: String
    let by: String?
    let files: [FamilyTrashFile]

    var reasonLabel: String {
        switch reason {
        case "duplicate": "Duplicate copies"
        default: "Deleted"
        }
    }
}

struct FamilyTrashFile: Codable, Identifiable, Hashable {
    let path: String
    let name: String
    let size: Int64
    let keptCopy: String?
    var id: String { path }
}

struct FamilyRestoreResult: Codable {
    let restored: Int
    let detail: String?
}

// The answer to a "start this" POST (202) and to the removals: whatever the server said about it.
struct FamilyDetailResult: Codable {
    let started: Bool?
    let removed: Int?
    let detail: String?
}

struct FamilyDriveRemovedResult: Codable {
    let removed: Bool?
    let detail: String?
}

enum FamilyFilesFormat {
    static func bytes(_ count: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: count, countStyle: .file)
    }

    static func date(_ date: Date?) -> String {
        guard let date else { return "" }
        return date.formatted(date: .abbreviated, time: .shortened)
    }

    // A path shown to the person: "Family Files" for the root, otherwise the folder as stored.
    static func folderTitle(_ path: String) -> String {
        path.isEmpty ? "Family Files" : (path.split(separator: "/").last.map(String.init) ?? path)
    }

    static func join(_ folder: String, _ name: String) -> String {
        folder.isEmpty ? name : "\(folder)/\(name)"
    }
}
