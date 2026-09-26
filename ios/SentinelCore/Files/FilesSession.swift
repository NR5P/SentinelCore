import AVFoundation
import Foundation
import ImageIO
import PhotosUI
import SwiftUI
import UniformTypeIdentifiers

// Everything the Files screens share while the section is open: the server's status (which
// carries the drives, the nightly summary and whatever job is running), the upload queue, and a
// change counter each folder listing reloads on. One per FilesView, handed down as an
// environment object so a folder three levels deep shows the same upload banner as the root.
@MainActor
final class FilesSession: ObservableObject {
    struct UploadProgress: Equatable {
        var index: Int
        var total: Int
        var name: String
    }

    // One file waiting to go up: where its bytes are, what to call it, and the date it was
    // taken when that is known.
    struct PendingUpload {
        var fileURL: URL?
        var data: Data?
        var name: String
        var contentType: UTType?
        var modified: Date?
        // A picked file we were given security-scoped access to and must hand back afterwards.
        var securityScoped = false
        // A copy we made ourselves (from the photo picker) and should delete afterwards.
        var temporary = false
    }

    @Published private(set) var status: FamilyFilesStatus?
    @Published private(set) var job: FamilyFilesJob?
    @Published private(set) var upload: UploadProgress?
    @Published var lastUploadNote: String?
    @Published var error: String?
    // Bumped after anything that changes the library, so every listing on screen reloads.
    @Published private(set) var version = 0

    private static let jobPollInterval = Duration.seconds(2)
    private var pollTask: Task<Void, Never>?
    private var uploadTask: Task<Void, Never>?
    private var queue: [PendingUpload] = []

    var isAdmin: Bool { status?.isAdmin ?? false }
    var maxUploadBytes: Int64? { status?.maxUploadBytes }

    func changed() { version += 1 }

    // MARK: - Status and the running job

    func refreshStatus(_ client: SentinelClient) async {
        do {
            let fresh = try await client.familyFilesStatus()
            status = fresh
            setJob(fresh.job, client: client)
        } catch {
            if Self.isCancellation(error) { return }
            self.error = error.localizedDescription
        }
    }

    // Called after any 202: the job exists from that moment, so polling starts at once rather
    // than waiting for the next status refresh.
    func jobStarted(_ client: SentinelClient) {
        Task { await refreshStatus(client) }
    }

    private func setJob(_ next: FamilyFilesJob?, client: SentinelClient) {
        let hadJob = job != nil
        job = next
        if next != nil {
            startPolling(client)
        } else if hadJob {
            // A finished backup, restore or duplicate check may have changed the library.
            pollTask?.cancel()
            pollTask = nil
            changed()
        }
    }

    private func startPolling(_ client: SentinelClient) {
        guard pollTask == nil else { return }
        pollTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: Self.jobPollInterval)
                guard !Task.isCancelled, let self else { return }
                await self.refreshStatus(client)
                if self.job == nil { return }
            }
        }
    }

    func stopPolling() {
        pollTask?.cancel()
        pollTask = nil
    }

    // MARK: - Uploads

    // Files the person picked with the file importer. Access is kept for the whole upload so the
    // bytes stream straight from wherever they are rather than being copied first.
    func enqueue(fileURLs: [URL], into folder: String, client: SentinelClient) {
        for url in fileURLs {
            let scoped = url.startAccessingSecurityScopedResource()
            let type = UTType(filenameExtension: url.pathExtension) ?? .data
            let modified = (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate
            queue.append(PendingUpload(fileURL: url, name: url.lastPathComponent, contentType: type, modified: modified, securityScoped: scoped))
        }
        start(into: folder, client: client)
    }

    // Photos and videos from the picker. Each is copied to a temporary file first (see
    // PickedMedia) so a long video goes up from disk; the original name is kept when the picker
    // gives one, and the date it was taken is read from the file itself.
    func enqueue(pickerItems: [PhotosPickerItem], into folder: String, client: SentinelClient) {
        Task {
            for item in pickerItems {
                if let pending = await Self.pending(for: item) { queue.append(pending) }
            }
            start(into: folder, client: client)
        }
    }

    private static func pending(for item: PhotosPickerItem) async -> PendingUpload? {
        let type = item.supportedContentTypes.first { $0.conforms(to: .movie) || $0.conforms(to: .image) }
            ?? item.supportedContentTypes.first ?? .jpeg
        if let media = try? await item.loadTransferable(type: PickedMedia.self) {
            let taken = await takenDate(of: media.url, type: type)
            let name = Self.name(for: media.url.lastPathComponent, type: type, taken: taken)
            return PendingUpload(fileURL: media.url, name: name, contentType: type, modified: taken, temporary: true)
        }
        // A picker item that has no file form - the bytes come through memory instead.
        guard let data = try? await item.loadTransferable(type: Data.self) else { return nil }
        let name = Self.name(for: nil, type: type, taken: nil)
        return PendingUpload(data: data, name: name, contentType: type, modified: nil)
    }

    // The picker's file usually keeps the camera's name (IMG_1234.HEIC); a transfer that lost it
    // arrives as a UUID or a generic name, and those get IMG-<date>.<ext> instead.
    private static func name(for original: String?, type: UTType, taken: Date?) -> String {
        if let original, !original.isEmpty {
            let stem = (original as NSString).deletingPathExtension
            let looksGenerated = UUID(uuidString: stem) != nil || stem.isEmpty
                || stem.lowercased().hasPrefix("fullsizerender") || stem.lowercased() == "image" || stem.lowercased() == "video"
            if !looksGenerated { return original }
        }
        let ext = type.preferredFilenameExtension ?? (type.conforms(to: .movie) ? "mov" : "jpg")
        let stamp = (taken ?? Date()).formatted(Date.FormatStyle().year().month(.twoDigits).day(.twoDigits).hour(.twoDigits(amPM: .omitted)).minute(.twoDigits).second(.twoDigits))
            .filter(\.isNumber)
        return "IMG-\(stamp).\(ext)"
    }

    // When the picture or video was taken, from the file's own metadata.
    private static func takenDate(of url: URL, type: UTType) async -> Date? {
        if type.conforms(to: .image) {
            guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
                  let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any] else { return nil }
            let exif = properties[kCGImagePropertyExifDictionary] as? [CFString: Any]
            let tiff = properties[kCGImagePropertyTIFFDictionary] as? [CFString: Any]
            let text = (exif?[kCGImagePropertyExifDateTimeOriginal] as? String) ?? (tiff?[kCGImagePropertyTIFFDateTime] as? String)
            guard let text else { return nil }
            let formatter = DateFormatter()
            formatter.locale = Locale(identifier: "en_US_POSIX")
            formatter.dateFormat = "yyyy:MM:dd HH:mm:ss"
            return formatter.date(from: text)
        }
        if type.conforms(to: .movie) {
            let asset = AVURLAsset(url: url)
            guard let item = try? await asset.load(.creationDate) else { return nil }
            return try? await item.load(.dateValue)
        }
        return nil
    }

    private func start(into folder: String, client: SentinelClient) {
        guard uploadTask == nil, !queue.isEmpty else { return }
        uploadTask = Task { [weak self] in
            guard let self else { return }
            await self.drain(into: folder, client: client)
            self.uploadTask = nil
            // Anything queued while the loop was finishing.
            if !self.queue.isEmpty { self.start(into: folder, client: client) }
        }
    }

    private func drain(into folder: String, client: SentinelClient) async {
        let total = queue.count
        var index = 0
        var saved = 0
        var alreadyThere = 0
        var failed: [String] = []
        while !queue.isEmpty {
            let item = queue.removeFirst()
            index += 1
            upload = UploadProgress(index: index, total: max(total, index), name: item.name)
            defer { finish(item) }
            let path = FamilyFilesFormat.join(folder, item.name)
            let contentType = item.contentType?.preferredMIMEType
            do {
                let result: FamilyUploadResult
                if let fileURL = item.fileURL {
                    result = try await client.familyUpload(fileURL: fileURL, to: path, modified: item.modified, contentType: contentType)
                } else if let data = item.data {
                    result = try await client.familyUpload(data: data, to: path, modified: item.modified, contentType: contentType)
                } else {
                    continue
                }
                if result.alreadyThere { alreadyThere += 1 } else { saved += 1 }
                changed()
            } catch {
                if Self.isCancellation(error) { break }
                failed.append("\(item.name): \(error.localizedDescription)")
            }
        }
        upload = nil
        var notes: [String] = []
        if saved > 0 { notes.append(saved == 1 ? "Added 1 file." : "Added \(saved) files.") }
        if alreadyThere > 0 { notes.append(alreadyThere == 1 ? "1 was already in the library." : "\(alreadyThere) were already in the library.") }
        lastUploadNote = notes.isEmpty ? nil : notes.joined(separator: " ")
        error = failed.isEmpty ? nil : (failed.count == 1 ? "Couldn't add \(failed[0])" : "Couldn't add \(failed.count) files. \(failed[0])")
    }

    private func finish(_ item: PendingUpload) {
        guard let url = item.fileURL else { return }
        if item.securityScoped { url.stopAccessingSecurityScopedResource() }
        if item.temporary { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
    }

    private static func isCancellation(_ error: Error) -> Bool {
        error is CancellationError || (error as? URLError)?.code == .cancelled
    }
}

// A photo or video from the picker, received as a file rather than as Data so a video never has
// to be held in memory. The picker's copy only exists inside the importing closure, so it is
// moved into a folder of our own straight away.
struct PickedMedia: Transferable {
    let url: URL

    static var transferRepresentation: some TransferRepresentation {
        FileRepresentation(importedContentType: .movie) { received in
            PickedMedia(url: try keep(received.file))
        }
        FileRepresentation(importedContentType: .image) { received in
            PickedMedia(url: try keep(received.file))
        }
    }

    private static func keep(_ file: URL) throws -> URL {
        let folder = FilesTemporaryFiles.directory().appendingPathComponent("picked-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let destination = folder.appendingPathComponent(file.lastPathComponent)
        try FileManager.default.copyItem(at: file, to: destination)
        return destination
    }
}
