import Foundation
import SwiftUI

// Offline listening (§11): tracks saved from /api/music/tracks/{id}/download with this device's
// codecs, episodes from the episode's audioUrl?download=1. Files live in Application Support (not
// Caches - the system must not throw away a playlist someone saved for a flight) and are excluded
// from iCloud backup. The player always prefers a saved file over streaming.
@MainActor
@Observable
final class AudioDownloads {
    static let shared = AudioDownloads()

    struct Entry: Codable, Identifiable, Hashable {
        var kind: String          // track | episode
        var itemId: String
        var fileName: String
        var bytes: Int64
        var savedAt: Date
        var track: MusicTrack?
        var episode: PodcastEpisode?
        var id: String { "\(kind):\(itemId)" }
        var title: String { track?.title ?? episode?.title ?? fileName }
    }

    private(set) var entries: [Entry] = []
    // key -> 0...1 while downloading; -1 while waiting its turn.
    private(set) var active: [String: Double] = [:]
    private(set) var lastError: String?

    @ObservationIgnored private var waiting: [(key: String, url: URL, entry: Entry)] = []
    @ObservationIgnored private var running = 0
    @ObservationIgnored private let fetcher = OfflineFetcher()

    let directory: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        var dir = base.appendingPathComponent("AudioDownloads", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        try? dir.setResourceValues(values)
        return dir
    }()

    private var indexFile: URL { directory.appendingPathComponent("index.json") }

    init() {
        if let data = try? Data(contentsOf: indexFile),
           let saved = try? JSONDecoder().decode([Entry].self, from: data) {
            // Only what is still really there.
            entries = saved.filter { FileManager.default.fileExists(atPath: directory.appendingPathComponent($0.fileName).path) }
        }
    }

    var totalBytes: Int64 { entries.reduce(0) { $0 + $1.bytes } }
    var tracks: [MusicTrack] { entries.compactMap(\.track) }
    var episodes: [PodcastEpisode] { entries.compactMap(\.episode) }

    func localURL(kind: String, id: String) -> URL? {
        guard let entry = entries.first(where: { $0.kind == kind && $0.itemId == id }) else { return nil }
        return directory.appendingPathComponent(entry.fileName)
    }

    func isSaved(_ kind: String, _ id: String) -> Bool { entries.contains { $0.kind == kind && $0.itemId == id } }
    func progress(_ kind: String, _ id: String) -> Double? { active["\(kind):\(id)"] }

    func isSaved(track: MusicTrack) -> Bool { isSaved("track", track.id) }
    func isSaved(episode: PodcastEpisode) -> Bool { isSaved("episode", episode.id) }

    // MARK: Saving

    func save(tracks: [MusicTrack]) {
        let client = AppModel.shared.client
        var added = 0
        for track in tracks where !isSaved(track: track) && active["track:\(track.id)"] == nil {
            guard let url = client.trackDownloadURL(track.id) else { continue }
            enqueue(key: "track:\(track.id)", url: url, entry: Entry(kind: "track", itemId: track.id, fileName: "", bytes: 0, savedAt: Date(), track: track))
            added += 1
        }
        if added > 0 { AudioToast.shared.show(added == 1 ? "Downloading" : "Downloading \(added) songs") }
    }

    func save(episode: PodcastEpisode) {
        guard !isSaved(episode: episode), active["episode:\(episode.id)"] == nil,
              let url = AppModel.shared.client.episodeAudioURL(episode, download: true) else { return }
        enqueue(key: "episode:\(episode.id)", url: url, entry: Entry(kind: "episode", itemId: episode.id, fileName: "", bytes: 0, savedAt: Date(), episode: episode))
        AudioToast.shared.show("Downloading episode")
    }

    private func enqueue(key: String, url: URL, entry: Entry) {
        active[key] = -1
        waiting.append((key, url, entry))
        pump()
    }

    private func pump() {
        while running < 2, !waiting.isEmpty {
            let job = waiting.removeFirst()
            running += 1
            Task {
                await run(job.key, url: job.url, entry: job.entry)
                running -= 1
                pump()
            }
        }
    }

    private func run(_ key: String, url: URL, entry: Entry) async {
        active[key] = 0
        var request = URLRequest(url: url)
        request.timeoutInterval = 120
        do {
            let result = try await fetcher.download(request) { fraction in
                Task { @MainActor in
                    if AudioDownloads.shared.active[key] != nil { AudioDownloads.shared.active[key] = fraction }
                }
            }
            guard active[key] != nil else {
                try? FileManager.default.removeItem(at: result.file)
                return
            }
            let ext = Self.fileExtension(result: result, entry: entry)
            let name = "\(entry.kind)-\(entry.itemId).\(ext)"
            let destination = directory.appendingPathComponent(name)
            try? FileManager.default.removeItem(at: destination)
            try FileManager.default.moveItem(at: result.file, to: destination)
            var saved = entry
            saved.fileName = name
            saved.savedAt = Date()
            saved.bytes = (try? FileManager.default.attributesOfItem(atPath: destination.path)[.size] as? Int64) ?? 0
            entries.removeAll { $0.id == saved.id }
            entries.append(saved)
            persist()
        } catch {
            lastError = "\(entry.title): \(error.localizedDescription)"
        }
        active[key] = nil
    }

    // AVFoundation picks the decoder by extension, so the file has to be named for what the server
    // actually sent (X-Audio-Codec, or the attachment name) - not for the original file.
    private static func fileExtension(result: OfflineFetcher.Result, entry: Entry) -> String {
        if let codec = result.codec?.lowercased() {
            switch codec {
            case "mp3": return "mp3"
            case "aac", "alac": return "m4a"
            case "flac": return "flac"
            case "wav": return "wav"
            case "aiff": return "aiff"
            default: break
            }
        }
        if let suggested = result.suggestedName, let ext = suggested.split(separator: ".").last, ext.count <= 5, suggested.contains(".") {
            return String(ext).lowercased()
        }
        switch result.mimeType ?? entry.episode?.mimeType ?? "" {
        case "audio/mpeg", "audio/mp3": return "mp3"
        case "audio/flac", "audio/x-flac": return "flac"
        case "audio/wav", "audio/x-wav": return "wav"
        default: return "m4a"
        }
    }

    func cancel(_ kind: String, _ id: String) {
        let key = "\(kind):\(id)"
        active[key] = nil
        waiting.removeAll { $0.key == key }
    }

    func remove(_ kind: String, _ id: String) {
        guard let entry = entries.first(where: { $0.kind == kind && $0.itemId == id }) else { return }
        try? FileManager.default.removeItem(at: directory.appendingPathComponent(entry.fileName))
        entries.removeAll { $0.id == entry.id }
        persist()
    }

    func removeAll(kind: String? = nil) {
        for entry in entries where kind == nil || entry.kind == kind {
            try? FileManager.default.removeItem(at: directory.appendingPathComponent(entry.fileName))
        }
        entries.removeAll { kind == nil || $0.kind == kind }
        persist()
    }

    private func persist() {
        if let data = try? JSONEncoder().encode(entries) { try? data.write(to: indexFile, options: .atomic) }
    }
}

// URLSession download tasks with progress. A delegate-based session because the async
// download(for:) API reports no progress; the finished file has to be moved before the delegate
// method returns, so it is moved to a temporary name there.
final class OfflineFetcher: NSObject, URLSessionDownloadDelegate, @unchecked Sendable {
    struct Result: Sendable {
        var file: URL
        var codec: String?
        var suggestedName: String?
        var mimeType: String?
    }

    private struct Job {
        var progress: @Sendable (Double) -> Void
        var continuation: CheckedContinuation<Result, Error>
        var result: Result?
    }

    private let lock = NSLock()
    private var jobs: [Int: Job] = [:]
    // Made once, under the lock. This was a lazy var, and two downloads starting together (an
    // album's first two songs) each made their own session; both sessions number their tasks from
    // 1, so the second job overwrote the first in `jobs` and one song spun forever, its finished
    // file left behind in tmp.
    private var madeSession: URLSession?
    private var session: URLSession {
        lock.withLock {
            if let madeSession { return madeSession }
            let configuration = URLSessionConfiguration.default
            configuration.httpCookieStorage = .shared
            configuration.timeoutIntervalForRequest = 120
            configuration.timeoutIntervalForResource = 60 * 60
            let made = URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
            madeSession = made
            return made
        }
    }

    func download(_ request: URLRequest, progress: @escaping @Sendable (Double) -> Void) async throws -> Result {
        try await withCheckedThrowingContinuation { continuation in
            let task = session.downloadTask(with: request)
            lock.withLock { jobs[task.taskIdentifier] = Job(progress: progress, continuation: continuation) }
            task.resume()
        }
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64,
                    totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        guard totalBytesExpectedToWrite > 0 else { return }
        let job = lock.withLock { jobs[downloadTask.taskIdentifier] }
        job?.progress(Double(totalBytesWritten) / Double(totalBytesExpectedToWrite))
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        let http = downloadTask.response as? HTTPURLResponse
        let status = http?.statusCode ?? 200
        guard (200..<300).contains(status) else { return }
        let keep = FileManager.default.temporaryDirectory.appendingPathComponent("audio-\(UUID().uuidString)")
        do {
            try FileManager.default.moveItem(at: location, to: keep)
        } catch { return }
        let result = Result(file: keep, codec: http?.value(forHTTPHeaderField: "X-Audio-Codec"),
                            suggestedName: downloadTask.response?.suggestedFilename, mimeType: downloadTask.response?.mimeType)
        lock.withLock { jobs[downloadTask.taskIdentifier]?.result = result }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard let job = lock.withLock({ jobs.removeValue(forKey: task.taskIdentifier) }) else { return }
        if let error {
            job.continuation.resume(throwing: error)
        } else if let result = job.result {
            job.continuation.resume(returning: result)
        } else {
            let status = (task.response as? HTTPURLResponse)?.statusCode ?? 0
            job.continuation.resume(throwing: ClientError.server(status: status, message: status == 404 ? "Not found on the server." : nil))
        }
    }
}

// MARK: - Downloads screen

struct AudioDownloadsView: View {
    private var downloads: AudioDownloads { AudioDownloads.shared }
    @State private var confirmingRemoveAll = false

    var body: some View {
        List {
            Section {
                LabeledContent("Storage used", value: AudioFormat.bytes(downloads.totalBytes))
                LabeledContent("Songs", value: "\(downloads.tracks.count)")
                LabeledContent("Episodes", value: "\(downloads.episodes.count)")
                if !downloads.active.isEmpty {
                    LabeledContent("Downloading", value: "\(downloads.active.count)")
                }
                if let error = downloads.lastError {
                    Text(error).font(.footnote).foregroundStyle(SentinelTheme.red)
                }
            } footer: {
                Text("Saved on this \(AudioDevice.kind == "mac" ? "Mac" : "device") for listening without a connection. Plays made offline are sent to the server once it's back.")
            }

            let tracks = downloads.entries.filter { $0.kind == "track" }
            if !tracks.isEmpty {
                Section {
                    Button {
                        AudioPlayer.shared.play(tracks: tracks.compactMap(\.track), context: AudioPlayContext(type: "queue", id: "downloads", name: "Downloads"), shuffle: true)
                    } label: {
                        Label("Shuffle Downloaded Songs", systemImage: "shuffle")
                    }
                    ForEach(groupedAlbums(tracks), id: \.0) { album, entries in
                        DisclosureGroup {
                            ForEach(entries) { entry in
                                if let track = entry.track {
                                    TrackRow(track: track, showArt: false, number: track.trackNumber) {
                                        let list = entries.compactMap(\.track)
                                        AudioPlayer.shared.play(tracks: list, startAt: list.firstIndex(of: track) ?? 0,
                                                                context: AudioPlayContext(type: "album", id: track.albumId, name: track.album))
                                    }
                                }
                            }
                            .onDelete { offsets in
                                for offset in offsets { downloads.remove("track", entries[offset].itemId) }
                            }
                        } label: {
                            HStack {
                                ArtworkImage(path: entries.first?.track?.artUrl, size: 40, corner: 5)
                                VStack(alignment: .leading) {
                                    Text(album).lineLimit(1)
                                    Text("\(entries.first?.track?.artistLine ?? "") · \(AudioFormat.bytes(entries.reduce(0) { $0 + $1.bytes }))")
                                        .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                                }
                            }
                        }
                    }
                } header: {
                    Text("Music")
                }
            }

            let episodes = downloads.entries.filter { $0.kind == "episode" }
            if !episodes.isEmpty {
                Section("Podcasts") {
                    ForEach(episodes) { entry in
                        if let episode = entry.episode {
                            Button {
                                AudioPlayer.shared.play(episode: episode)
                            } label: {
                                HStack {
                                    ArtworkImage(path: episode.artUrl, size: 44, corner: 6, placeholder: "mic")
                                    VStack(alignment: .leading, spacing: 2) {
                                        Text(episode.title).lineLimit(2).foregroundStyle(.primary)
                                        Text("\(episode.podcastTitle ?? "") · \(AudioFormat.bytes(entry.bytes))")
                                            .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                                    }
                                }
                            }
                            .buttonStyle(.plain)
                        }
                    }
                    .onDelete { offsets in
                        for offset in offsets { downloads.remove("episode", episodes[offset].itemId) }
                    }
                }
            }

            if !downloads.entries.isEmpty {
                Section {
                    Button("Remove All Downloads", role: .destructive) { confirmingRemoveAll = true }
                }
            } else if downloads.active.isEmpty {
                Section {
                    Text("Nothing downloaded yet. Use Download on an album, playlist, song or episode.")
                        .foregroundStyle(.secondary)
                }
            }
        }
        .navigationTitle("Downloads")
        .confirmationDialog("Remove every download from this device?", isPresented: $confirmingRemoveAll, titleVisibility: .visible) {
            Button("Remove All", role: .destructive) { downloads.removeAll() }
        }
    }

    private func groupedAlbums(_ entries: [AudioDownloads.Entry]) -> [(String, [AudioDownloads.Entry])] {
        let grouped = Dictionary(grouping: entries) { $0.track?.album ?? "Unknown Album" }
        return grouped.map { album, list in
            (album, list.sorted { (($0.track?.discNumber ?? 1), ($0.track?.trackNumber ?? 0)) < (($1.track?.discNumber ?? 1), ($1.track?.trackNumber ?? 0)) })
        }
        .sorted { $0.0.localizedCaseInsensitiveCompare($1.0) == .orderedAscending }
    }
}

// The small state icon on a row: saved on this device, or downloading.
struct OfflineBadge: View {
    let kind: String
    let id: String

    var body: some View {
        let downloads = AudioDownloads.shared
        if let progress = downloads.progress(kind, id) {
            if progress < 0 {
                Image(systemName: "clock").font(.caption2).foregroundStyle(.secondary)
            } else {
                ProgressView(value: progress).progressViewStyle(.circular).controlSize(.mini).frame(width: 14, height: 14)
            }
        } else if downloads.isSaved(kind, id) {
            Image(systemName: "arrow.down.circle.fill")
                .font(.caption)
                .foregroundStyle(.secondary)
                .accessibilityLabel("Downloaded")
        }
    }
}
