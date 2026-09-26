import AVFoundation
import Combine
import Foundation

// One film or episode on screen, and the ones that follow it. Asks the server how to play the item
// (direct file or HLS session), builds the AVPlayer, puts the viewer where they left off, reports
// progress back, and rolls on to the next episode at the end of one. The views around it only
// present `player` and react to `errorMessage` / `isFinished`.
//
// The one rule that matters (core/docs/MOVIES.md, Playback): an HLS session's clock starts at
// `startSeconds`. The player's time 0 is the film at startSeconds, so every position reported to
// the server is startSeconds + currentTime. Direct play is the whole file instead, and the player is
// seeked to startSeconds once the item is ready.
//
// Twin of appletv/SentinelCoreTV/Movies/MoviePlaybackController.swift - keep the two identical.
@MainActor
final class MoviePlaybackController: ObservableObject {
    @Published private(set) var player: AVPlayer?
    @Published private(set) var playback: MediaPlayback?
    @Published private(set) var errorMessage: String?
    @Published private(set) var isLoading = false
    // The last item ended with nothing after it; the screen closes itself.
    @Published private(set) var isFinished = false

    private let client: SentinelClient
    private let firstRequest: MoviePlayRequest
    private(set) var itemId: String
    private var timeObserver: Any?
    private var endObserver: NSObjectProtocol?
    private var itemTask: Task<Void, Never>?
    private var statusObservation: NSKeyValueObservation?
    private var pauseObservation: NSKeyValueObservation?
    // The item whose readiness has been acted on, so a repeated readyToPlay does not seek again.
    private weak var readyItem: AVPlayerItem?
    // Nothing is reported until the item is playing where it should be: a resume that reported
    // the pre-seek position of 0 would be harmless (the server ignores anything under 30 seconds)
    // but pointless.
    private var hasStarted = false
    private var lastReported: Double?
    private var stopped = false

    init(client: SentinelClient, request: MoviePlayRequest) {
        self.client = client
        firstRequest = request
        itemId = request.itemId
    }

    func start() async {
        guard player == nil, !isLoading else { return }
        await load(itemId: firstRequest.itemId, start: firstRequest.start, overview: firstRequest.overview)
    }

    // The film's own clock: the player's time plus the HLS offset.
    var currentPosition: Double? {
        guard let player, let playback else { return nil }
        let seconds = player.currentTime().seconds
        guard seconds.isFinite else { return nil }
        return playback.clockOffset + max(0, seconds)
    }

    private var currentDuration: Double? {
        if let duration = playback?.durationSeconds, duration > 0 { return duration }
        guard let item = player?.currentItem, let playback else { return nil }
        let seconds = item.duration.seconds
        return seconds.isFinite && seconds > 0 ? playback.clockOffset + seconds : nil
    }

    private func load(itemId: String, start: Double?, overview: String?) async {
        isLoading = true
        errorMessage = nil
        hasStarted = false
        lastReported = nil
        do {
            let playback = try await client.mediaPlayback(id: itemId, start: start)
            guard !stopped else { return }
            // Built now, from the address in use now - a plain-HTTP fallback since the list was
            // loaded must not send the player to the address that failed.
            guard let url = client.mediaURL(playback.url) else { throw ClientError.invalidServerAddress }
            self.itemId = itemId
            self.playback = playback
            attach(makeItem(url: url, playback: playback, overview: overview), playback: playback)
        } catch {
            guard !stopped, !(error is CancellationError), (error as? URLError)?.code != .cancelled else { return }
            isLoading = false
            errorMessage = error.localizedDescription
        }
    }

    private func makeItem(url: URL, playback: MediaPlayback, overview: String?) -> AVPlayerItem {
        // The session cookie, handed over explicitly: AVFoundation loads the HLS playlists and
        // segments itself and must be signed in for every one of those requests.
        var options: [String: Any] = [:]
        if let cookies = HTTPCookieStorage.shared.cookies(for: url), !cookies.isEmpty {
            options[AVURLAssetHTTPCookiesKey] = cookies
        }
        let item = AVPlayerItem(asset: AVURLAsset(url: url, options: options))
        #if os(tvOS) || os(iOS)
        applyMetadata(to: item, playback: playback, overview: overview)
        #endif
        return item
    }

    private func attach(_ item: AVPlayerItem, playback: MediaPlayback) {
        itemTask?.cancel()
        if let endObserver { NotificationCenter.default.removeObserver(endObserver) }

        let player: AVPlayer
        if let existing = self.player {
            player = existing
            player.replaceCurrentItem(with: item)
        } else {
            #if os(iOS)
            try? AVAudioSession.sharedInstance().setCategory(.playback, mode: .moviePlayback)
            try? AVAudioSession.sharedInstance().setActive(true)
            #elseif os(tvOS)
            try? AVAudioSession.sharedInstance().setCategory(.playback, mode: .moviePlayback)
            #endif
            player = AVPlayer(playerItem: item)
            self.player = player
            observePlayer(player)
        }

        endObserver = NotificationCenter.default.addObserver(
            forName: AVPlayerItem.didPlayToEndTimeNotification, object: item, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.itemEnded() }
        }
        // Plain KVO, not `publisher(for:).values`: that async sequence holds one value at a time
        // and the KVO publisher drops changes made while nobody is asking, so a status that went
        // unknown -> readyToPlay while the first value was being handled was lost and the player
        // sat on its first frame forever (seen on tvOS 2026-09-26).
        statusObservation = item.observe(\.status, options: [.initial, .new]) { [weak self] item, _ in
            let status = item.status
            let message = item.error?.localizedDescription
            Task { @MainActor [weak self] in
                self?.itemStatusChanged(item, status: status, error: message, playback: playback)
            }
        }
    }

    private func itemStatusChanged(_ item: AVPlayerItem, status: AVPlayerItem.Status, error: String?, playback: MediaPlayback) {
        guard !stopped, player?.currentItem === item else { return }
        switch status {
        case .readyToPlay:
            guard readyItem !== item else { return }
            readyItem = item
            itemTask?.cancel()
            itemTask = Task { [weak self] in await self?.itemReady(item, playback: playback) }
        case .failed:
            isLoading = false
            errorMessage = error ?? "This video couldn't be played."
        default:
            break
        }
    }

    private func itemReady(_ item: AVPlayerItem, playback: MediaPlayback) async {
        guard let player, player.currentItem === item else { return }
        if playback.isHLS {
            // An EVENT playlist that is still growing reads as live to AVPlayer, which would
            // start near its end; the film starts at the top of the playlist.
            if player.currentTime().seconds > 1 {
                await player.seek(to: .zero, toleranceBefore: .zero, toleranceAfter: .zero)
            }
        } else if let start = playback.startSeconds, start > 1 {
            await player.seek(to: CMTime(seconds: start, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
        }
        guard !stopped, player.currentItem === item else { return }
        isLoading = false
        hasStarted = true
        player.play()
    }

    // Once per player: the 10-second progress tick and a report whenever playback pauses.
    private func observePlayer(_ player: AVPlayer) {
        timeObserver = player.addPeriodicTimeObserver(
            forInterval: CMTime(seconds: 10, preferredTimescale: 1), queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.reportProgress() }
        }
        // KVO for the same reason as the item status above: a pause must never be missed.
        pauseObservation = player.observe(\.timeControlStatus, options: [.new]) { [weak self] player, _ in
            guard player.timeControlStatus == .paused else { return }
            Task { @MainActor [weak self] in self?.reportProgress() }
        }
    }

    func reportProgress(force: Bool = false) {
        guard hasStarted, let position = currentPosition else { return }
        if !force, let lastReported, abs(lastReported - position) < 1 { return }
        lastReported = position
        let id = itemId
        let duration = currentDuration
        let client = client
        Task { try? await client.reportMediaProgress(id: id, position: position, duration: duration) }
    }

    private func itemEnded() {
        guard let playback else { return }
        // The end is the end, whatever the last tick said: the server marks it watched at 92%.
        if hasStarted {
            let id = itemId
            let duration = currentDuration
            let position = duration ?? currentPosition ?? 0
            lastReported = position
            let client = client
            Task { try? await client.reportMediaProgress(id: id, position: position, duration: duration) }
        }
        hasStarted = false
        if let next = playback.nextEpisodeId, !next.isEmpty {
            Task { await load(itemId: next, start: nil, overview: nil) }
        } else {
            isFinished = true
        }
    }

    func pause() {
        player?.pause()
    }

    // The screen is going away: say where the viewer got to, then let go of everything.
    func stop() {
        guard !stopped else { return }
        reportProgress(force: true)
        stopped = true
        itemTask?.cancel()
        statusObservation?.invalidate()
        statusObservation = nil
        pauseObservation?.invalidate()
        pauseObservation = nil
        if let endObserver { NotificationCenter.default.removeObserver(endObserver) }
        endObserver = nil
        if let timeObserver, let player { player.removeTimeObserver(timeObserver) }
        timeObserver = nil
        player?.pause()
        player?.replaceCurrentItem(with: nil)
    }

    #if os(tvOS) || os(iOS)
    // The tvOS info panel (and the iOS Now Playing card) read these: title, "S1 · E3 · Title",
    // the overview, and the poster once it has downloaded.
    private func applyMetadata(to item: AVPlayerItem, playback: MediaPlayback, overview: String?) {
        var metadata: [AVMetadataItem] = []
        if let title = playback.title, !title.isEmpty {
            metadata.append(Self.metadataItem(.commonIdentifierTitle, value: title as NSString))
        }
        if let subtitle = playback.subtitle, !subtitle.isEmpty {
            metadata.append(Self.metadataItem(.iTunesMetadataTrackSubTitle, value: subtitle as NSString))
        }
        if let overview, !overview.isEmpty {
            metadata.append(Self.metadataItem(.commonIdentifierDescription, value: overview as NSString))
        }
        item.externalMetadata = metadata
        guard let artwork = client.mediaURL(playback.posterUrl ?? playback.backdropUrl) else { return }
        Task { [weak item] in
            guard let (data, response) = try? await URLSession.shared.data(from: artwork),
                  (response as? HTTPURLResponse)?.statusCode == 200,
                  let item else { return }
            let art = Self.metadataItem(.commonIdentifierArtwork, value: data as NSData, dataType: kCMMetadataBaseDataType_JPEG as String)
            item.externalMetadata.append(art)
        }
    }

    private static func metadataItem(_ identifier: AVMetadataIdentifier, value: NSCopying & NSObjectProtocol, dataType: String? = nil) -> AVMetadataItem {
        let item = AVMutableMetadataItem()
        item.identifier = identifier
        item.value = value
        item.extendedLanguageTag = "und"
        if let dataType { item.dataType = dataType }
        return item.copy() as! AVMetadataItem
    }
    #endif
}
