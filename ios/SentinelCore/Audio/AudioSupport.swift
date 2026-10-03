import Foundation
import SwiftUI
#if os(iOS)
import UIKit
#endif

// One entry in the player's queue: a music track or a podcast episode, plus a queue-entry id of its
// own so the same track queued twice is still two rows that can be moved or removed separately.
struct AudioItem: Identifiable, Hashable, Codable, Sendable {
    var uid = UUID()
    var track: MusicTrack?
    var episode: PodcastEpisode?

    init(track: MusicTrack) { self.track = track }
    init(episode: PodcastEpisode) { self.episode = episode }

    var id: UUID { uid }
    var isEpisode: Bool { episode != nil }
    var kind: String { isEpisode ? "episode" : "track" }
    var itemId: String { track?.id ?? episode?.id ?? "" }
    var ref: AudioQueueRef { AudioQueueRef(type: kind, id: itemId) }
    var title: String { track?.title ?? episode?.title ?? "" }
    var subtitle: String { track?.artistLine ?? episode?.podcastTitle ?? "" }
    var album: String? { track?.album ?? episode?.podcastTitle }
    var artPath: String? { track?.artUrl ?? episode?.artUrl }
    var duration: Double { track?.durationSeconds ?? episode?.durationSeconds ?? 0 }
}

extension Color {
    // Text on a filled SentinelTheme.green button: the green is dark in light mode and a bright
    // mint in dark mode, where white text on it all but disappears.
    static let onSentinelGreen = Color(light: .white, dark: .black)
}

enum AudioRepeat: String, CaseIterable, Sendable {
    case off, all, one

    var icon: String { self == .one ? "repeat.1" : "repeat" }
    var next: AudioRepeat {
        switch self {
        case .off: .all
        case .all: .one
        case .one: .off
        }
    }
}

// Who this device is, to the speaker list and to "Playing on Nathan's iPhone".
@MainActor
enum AudioDevice {
    static var id: String { OrganizerDevice.id }

    private static var cachedName: String?
    static var name: String {
        if let custom = UserDefaults.standard.string(forKey: "audio.speakerName"), !custom.trimmingCharacters(in: .whitespaces).isEmpty {
            return custom
        }
        if let cachedName { return cachedName }
        #if os(iOS)
        let name = UIDevice.current.name
        #else
        let name = Host.current().localizedName ?? "Mac"
        #endif
        cachedName = name
        return name
    }

    static var kind: String {
        #if os(iOS)
        UIDevice.current.userInterfaceIdiom == .pad ? "tablet" : "phone"
        #else
        "mac"
        #endif
    }
}

// Plays and progress that couldn't reach the server (offline listening, a dropped connection),
// kept on disk and sent the next time anything gets through. `playedAt` carries the real time, so
// a play heard on a plane counts on the day it was heard; the server ignores duplicates.
@MainActor
final class AudioReportQueue {
    static let shared = AudioReportQueue()

    struct Pending: Codable {
        var key = UUID()
        var kind: String          // played | progress
        var id: String
        var played: MusicPlayedBody?
        var position: Double?
        var duration: Double?
        var speed: Double?
        var deviceName: String?
    }

    private var pending: [Pending] = []
    private var flushing = false
    private let file: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        let dir = base.appendingPathComponent("Audio", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir.appendingPathComponent("pending-reports.json")
    }()

    init() {
        if let data = try? Data(contentsOf: file), let saved = try? JSONDecoder().decode([Pending].self, from: data) {
            pending = saved
        }
    }

    var count: Int { pending.count }

    func played(trackId: String, body: MusicPlayedBody) {
        var body = body
        if body.playedAt == nil { body.playedAt = AudioDate.iso(Date()) }
        send(Pending(kind: "played", id: trackId, played: body))
    }

    func progress(episodeId: String, position: Double, duration: Double?, speed: Double) {
        // Only the newest position of an episode matters.
        pending.removeAll { $0.kind == "progress" && $0.id == episodeId }
        send(Pending(kind: "progress", id: episodeId, position: position, duration: duration, speed: speed, deviceName: AudioDevice.name))
    }

    private func send(_ report: Pending) {
        Task {
            if await deliver(report) {
                await flush()
            } else {
                pending.append(report)
                save()
            }
        }
    }

    // True when it got through or the server refused it for good (a 4xx won't get better later).
    private func deliver(_ report: Pending) async -> Bool {
        let client = AppModel.shared.client
        guard !AppModel.shared.activeServerURL.isEmpty else { return false }
        do {
            switch report.kind {
            case "played":
                guard let body = report.played else { return true }
                try await client.reportTrackPlayed(id: report.id, body)
            default:
                try await client.reportEpisodeProgress(
                    id: report.id, position: report.position ?? 0, duration: report.duration,
                    speed: report.speed ?? 1, deviceName: report.deviceName ?? AudioDevice.name)
            }
            return true
        } catch ClientError.server(let status, _) where (400..<500).contains(status) && status != 408 && status != 429 {
            return true
        } catch {
            return false
        }
    }

    func flush() async {
        guard !flushing, !pending.isEmpty else { return }
        flushing = true
        defer { flushing = false }
        var delivered = Set<UUID>()
        for report in pending {
            // Still offline: keep the rest for later.
            guard await deliver(report) else { break }
            delivered.insert(report.key)
        }
        pending.removeAll { delivered.contains($0.key) }
        save()
    }

    private func save() {
        if let data = try? JSONEncoder().encode(pending) { try? data.write(to: file, options: .atomic) }
    }
}

// Where a "Go to Album" from a context menu or the Now Playing screen lands. The Music and
// Podcasts roots each push whatever is set here; RootView switches to the section first.
enum MusicRoute: Hashable, Identifiable {
    case album(String)
    case artist(String)
    case playlist(String)
    case genre(String)
    case decade(Int, String)
    case mix(MusicMix)

    var id: String {
        switch self {
        case .album(let id): "album-\(id)"
        case .artist(let id): "artist-\(id)"
        case .playlist(let id): "playlist-\(id)"
        case .genre(let name): "genre-\(name)"
        case .decade(let decade, _): "decade-\(decade)"
        case .mix(let mix): "mix-\(mix.id)"
        }
    }
}

enum PodcastRoute: Hashable, Identifiable {
    case show(String)
    case episode(String)

    var id: String {
        switch self {
        case .show(let id): "show-\(id)"
        case .episode(let id): "episode-\(id)"
        }
    }
}

@MainActor
@Observable
final class AudioRouter {
    static let shared = AudioRouter()
    var musicRoute: MusicRoute?
    var podcastRoute: PodcastRoute?

    func open(_ route: MusicRoute) {
        musicRoute = route
        NotificationCenter.default.post(name: .sentinelOpenSection, object: "music")
    }

    func open(_ route: PodcastRoute) {
        podcastRoute = route
        NotificationCenter.default.post(name: .sentinelOpenSection, object: "podcasts")
    }
}

extension Notification.Name {
    // object: an AppSection raw value. Lets the player, which lives above every tab, send the
    // person to the Music or Podcasts section.
    static let sentinelOpenSection = Notification.Name("sentinelOpenSection")
    static let audioLibraryChanged = Notification.Name("audioLibraryChanged")
}

// A brief "Added to Up Next" style confirmation, shown over the bottom of the window.
@MainActor
@Observable
final class AudioToast {
    static let shared = AudioToast()
    private(set) var message: String?
    @ObservationIgnored private var hideTask: Task<Void, Never>?

    func show(_ text: String) {
        message = text
        hideTask?.cancel()
        hideTask = Task {
            try? await Task.sleep(for: .seconds(2.2))
            guard !Task.isCancelled else { return }
            withAnimation { self.message = nil }
        }
    }
}

struct AudioToastOverlay: View {
    var body: some View {
        if let message = AudioToast.shared.message {
            Text(message)
                .font(.subheadline.weight(.semibold))
                .padding(.horizontal, 16)
                .padding(.vertical, 10)
                .background(.thickMaterial, in: Capsule())
                .shadow(radius: 8, y: 2)
                .transition(.move(edge: .bottom).combined(with: .opacity))
                .padding(.bottom, 90)
                .accessibilityAddTraits(.isStaticText)
        }
    }
}
