import Foundation

// The part of /api/family-files the TV reads - the same names as the iPhone app's FilesModels,
// trimmed to browsing. Everything the server may leave out or send as null is optional, so a
// status from a server with the feature switched off decodes as happily as a full one.

struct FamilyFilesStatus: Codable {
    let enabled: Bool
    let isAdmin: Bool?
    let username: String?
    // Why the library's computer can't be reached right now, when the library is a shared
    // folder on another computer that is off or turned down the sign-in.
    let libraryProblem: String?
    let library: FamilyFilesLibraryTotals?
    let drives: [FamilyDrive]?
    let nightly: FamilyFilesNightly?
    let job: FamilyFilesJob?
}

struct FamilyFilesLibraryTotals: Codable, Hashable {
    let files: Int
    let bytes: Int64
    let indexedUtc: Date?
}

struct FamilyFilesNightly: Codable, Hashable {
    let nextRunUtc: Date?
    let lastRunUtc: Date?
    let lastResult: String?
    let lastMessage: String?
}

struct FamilyFilesJob: Codable, Hashable {
    let kind: String
    let driveName: String?
    let message: String
    let done: Int
    let total: Int
}

// A backup drive as anyone signed in sees it: the name, where it points in words, and how the
// last backup went. The connection details are only sent to an admin and are not needed here.
struct FamilyDrive: Codable, Identifiable, Hashable {
    let id: String
    let name: String
    let kind: String
    let enabled: Bool
    let location: String?
    let lastAttemptUtc: Date?
    let lastSuccessUtc: Date?
    let lastResult: String?
    let lastMessage: String?
    let lastFileCount: Int?
    let lastBytes: Int64?
    let lastFreeBytes: Int64?

    var isSftp: Bool { kind.lowercased() == "sftp" }
}

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
}

enum FamilyFilesFormat {
    static func bytes(_ count: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: count, countStyle: .file)
    }

    static func date(_ date: Date?) -> String {
        guard let date else { return "" }
        return date.formatted(date: .abbreviated, time: .shortened)
    }

    static func folderTitle(_ path: String) -> String {
        path.isEmpty ? "Family Files" : (path.split(separator: "/").last.map(String.init) ?? path)
    }
}
