import Combine
import Foundation
import SwiftUI

// Everything Music & Podcasts share on the TV, alive for the whole app rather than one tab:
// whether each tab exists, the player, Now Playing on screen or not, the "Continue from <device>"
// offer, and this Apple TV as a speaker that phones can play to and alarms can wake people with
// (core/docs/MUSIC_AND_PODCASTS.md §7-8).
@MainActor
final class AudioCenter: ObservableObject {
    static let shared = AudioCenter()

    @Published private(set) var musicEnabled = AudioPrefs.musicEnabled
    @Published private(set) var podcastsEnabled = AudioPrefs.podcastsEnabled
    @Published var showNowPlaying = false
    // Another of this person's devices is playing (or was): offer to carry on here.
    @Published private(set) var continueOffer: ContinueOffer?
    @Published private(set) var speakerOnline = false
    @Published private(set) var speakerError: String?
    @Published private(set) var lastCommand: String?

    let player = AudioPlayer()
    private(set) var api: AudioAPI?
    private weak var model: AppModel?
    private var speakerTask: Task<Void, Never>?
    private var heartbeatTask: Task<Void, Never>?
    private var stateTask: Task<Void, Never>?
    private var dismissedOffers: Set<String> = []
    private var playerChanges: AnyCancellable?

    struct ContinueOffer: Identifiable {
        var nowPlaying: NowPlaying
        var entries: [AudioEntry]
        var id: String { nowPlaying.deviceId + (nowPlaying.updatedAt ?? "") }
        var current: AudioEntry? { entries[safe: nowPlaying.index ?? 0] }
    }

    private init() {
        player.attach(self)
        player.onStateChange = { [weak self] in self?.scheduleStateReport() }
        // Keep views that read through the center (Continue banner, mini bar) in step with the player.
        playerChanges = player.objectWillChange.sink { [weak self] _ in self?.objectWillChange.send() }
    }

    // Called by RootView whenever the connection settles on an address.
    func connect(model: AppModel) async {
        self.model = model
        if api == nil { api = AudioAPI(client: model.client) }
        guard model.connectionState == .online, !model.activeServerURL.isEmpty else { return }
        await refreshEnabled()
        startSpeakerIfWanted()
        await refreshContinueOffer()
    }

    func refreshEnabled() async {
        guard let api else { return }
        do {
            let flags = try await api.enabled()
            setEnabled(music: flags.music == true, podcasts: flags.podcasts == true)
        } catch {
            // An older server answers 404: both off. Anything else keeps what was last known.
            if AudioAPI.isNotFound(error) { setEnabled(music: false, podcasts: false) }
        }
    }

    private func setEnabled(music: Bool, podcasts: Bool) {
        musicEnabled = music
        podcastsEnabled = podcasts
        AudioPrefs.musicEnabled = music
        AudioPrefs.podcastsEnabled = podcasts
        if !music, !podcasts { stopSpeaker(unregister: false) }
    }

    // MARK: - Continue from <device> (§7)

    func refreshContinueOffer() async {
        guard let api, musicEnabled || podcastsEnabled, !player.hasQueue else {
            if player.hasQueue { continueOffer = nil }
            return
        }
        guard let devices = try? await api.nowPlayingDevices() else { return }
        let mine = AudioPrefs.deviceId
        let candidate = devices.first { np in
            np.deviceId != mine && !(np.queue ?? []).isEmpty && !dismissedOffers.contains(np.deviceId + (np.updatedAt ?? ""))
                && (AudioDate.parse(np.updatedAt).map { Date().timeIntervalSince($0) < 3 * 86400 } ?? true)
        }
        guard let candidate else { continueOffer = nil; return }
        guard let resolved = try? await api.resolveNowPlaying(deviceId: candidate.deviceId) else { return }
        let entries = Self.entries(for: resolved.nowPlaying.queue ?? [], tracks: resolved.tracks ?? [], episodes: resolved.episodes ?? [])
        continueOffer = entries.isEmpty ? nil : ContinueOffer(nowPlaying: resolved.nowPlaying, entries: entries)
    }

    func acceptContinue() {
        guard let offer = continueOffer else { return }
        let np = offer.nowPlaying
        player.load(offer.entries, startAt: np.index ?? 0, position: np.positionSeconds ?? 0, shuffle: false,
                    context: np.context, autoplay: true, repeatMode: RepeatMode(rawValue: np.repeat ?? "off"), speed: np.speed)
        continueOffer = nil
        showNowPlaying = true
    }

    func dismissContinue() {
        if let offer = continueOffer { dismissedOffers.insert(offer.id) }
        continueOffer = nil
    }

    // A queue of refs, in order, from the resolved tracks and episodes.
    static func entries(for refs: [QueueRef], tracks: [Track], episodes: [Episode]) -> [AudioEntry] {
        let trackById = Dictionary(tracks.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        let episodeById = Dictionary(episodes.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        return refs.compactMap { ref in
            if ref.type == "episode" { return episodeById[ref.id].map(AudioEntry.init(episode:)) }
            return trackById[ref.id].map(AudioEntry.init(track:))
        }
    }

    // MARK: - This Apple TV as a speaker (§8)

    var speakerEnabled: Bool { AudioPrefs.speakerEnabled }

    func setSpeakerEnabled(_ on: Bool) {
        AudioPrefs.speakerEnabled = on
        objectWillChange.send()
        if on { startSpeakerIfWanted() } else { stopSpeaker(unregister: true) }
    }

    func speakerSettingsChanged() {
        objectWillChange.send()
        guard speakerTask != nil else { return }
        Task { await sendHeartbeat() }
    }

    func startSpeakerIfWanted() {
        guard AudioPrefs.speakerEnabled, musicEnabled || podcastsEnabled, api != nil, speakerTask == nil else { return }
        heartbeatTask = Task { [weak self] in
            while !Task.isCancelled {
                await self?.sendHeartbeat()
                try? await Task.sleep(for: .seconds(60))
            }
        }
        speakerTask = Task { [weak self] in await self?.pollCommands() }
    }

    func stopSpeaker(unregister: Bool) {
        heartbeatTask?.cancel()
        heartbeatTask = nil
        speakerTask?.cancel()
        speakerTask = nil
        speakerOnline = false
        guard unregister, let api else { return }
        let id = AudioPrefs.deviceId
        Task { try? await api.removeSpeaker(deviceId: id) }
    }

    private func sendHeartbeat() async {
        guard let api else { return }
        let body = SpeakerHeartbeat(name: AudioPrefs.speakerName, kind: "tv",
                                    allowAnnouncements: AudioPrefs.allowAnnouncements, volume: player.volume)
        do {
            try await api.heartbeat(deviceId: AudioPrefs.deviceId, body)
            speakerOnline = true
            speakerError = nil
            scheduleStateReport(immediately: true)
        } catch {
            if AudioAPI.isCancellation(error) { return }
            speakerOnline = false
            speakerError = error.localizedDescription
        }
    }

    // The long poll. `after=-1` on first connect asks for only new commands.
    private func pollCommands() async {
        var after = -1
        var failures = 0
        while !Task.isCancelled {
            guard let api else { return }
            do {
                let reply = try await api.commands(deviceId: AudioPrefs.deviceId, after: after)
                failures = 0
                // In seq order, one at a time: handle() only returns once an announce has been spoken.
                for command in (reply.commands ?? []).sorted(by: { ($0.seq ?? 0) < ($1.seq ?? 0) }) {
                    if let seq = command.seq {
                        if seq <= after { continue }
                        after = seq
                    }
                    await handle(command)
                }
                if let latest = reply.latestSeq { after = max(after, latest) }
            } catch {
                if Task.isCancelled || AudioAPI.isCancellation(error) { return }
                failures += 1
                try? await Task.sleep(for: .seconds(min(30, 2 * failures)))
            }
        }
    }

    func handle(_ command: SpeakerCommand) async {
        lastCommand = command.action + (command.from.map { " from \($0)" } ?? "")
        switch command.action {
        case "play":
            await playCommand(command)
        case "pause":
            player.pause()
        case "resume":
            player.resume()
        case "next":
            player.next()
        case "previous":
            player.previous()
        case "seek":
            if let to = command.positionSeconds { player.seek(to: to) }
        case "volume":
            if let volume = command.volume { player.setVolume(volume, ramp: command.rampSeconds ?? 0) }
        case "stop":
            player.stop()
        case "duck":
            player.duck(true)
        case "unduck":
            player.duck(false)
        case "announce":
            // Awaited: the next command (an alarm's `play` after its spoken briefing) must wait
            // until the voice has finished (§8).
            guard AudioPrefs.allowAnnouncements, let text = command.text else { return }
            await player.announce(text, volume: command.volume)
        default:
            break
        }
        scheduleStateReport(immediately: true)
    }

    // `play` replaces the queue. The refs are resolved by reporting them as this TV's now-playing
    // and resolving that - one request for any mix of tracks and episodes.
    private func playCommand(_ command: SpeakerCommand) async {
        guard let api, let refs = command.queue, !refs.isEmpty else {
            player.resume()
            return
        }
        let me = AudioPrefs.deviceId
        let snapshot = NowPlaying(deviceId: me, deviceName: AudioPrefs.speakerName,
                                  kind: refs.first?.type == "episode" ? "podcast" : "music",
                                  queue: refs, index: command.index ?? 0, positionSeconds: command.positionSeconds ?? 0,
                                  playing: true, shuffle: command.shuffle ?? false, repeat: "off", speed: nil,
                                  context: command.context, updatedAt: nil)
        do {
            try await api.putNowPlaying(snapshot)
            let resolved = try await api.resolveNowPlaying(deviceId: me)
            let entries = Self.entries(for: refs, tracks: resolved.tracks ?? [], episodes: resolved.episodes ?? [])
            guard !entries.isEmpty else { return }
            if let volume = command.volume { player.setVolume(volume, ramp: command.rampSeconds ?? 0) }
            else if let ramp = command.rampSeconds, ramp > 0 { player.setVolume(player.volume, ramp: ramp) }
            player.load(entries, startAt: min(command.index ?? 0, entries.count - 1), position: command.positionSeconds ?? 0,
                        shuffle: command.shuffle ?? false, context: command.context, autoplay: true)
            continueOffer = nil
            // Music sent here from a phone, or a wake-up alarm, shows what's playing (and how to
            // stop it) instead of playing behind whatever screen the TV was left on.
            showNowPlaying = true
        } catch {
            speakerError = error.localizedDescription
        }
    }

    // Speaker state goes up whenever it changes, at most once a second.
    private func scheduleStateReport(immediately: Bool = false) {
        guard speakerTask != nil, let api else { return }
        stateTask?.cancel()
        stateTask = Task { [weak self] in
            if !immediately { try? await Task.sleep(for: .seconds(1)) }
            guard let self, !Task.isCancelled else { return }
            try? await api.putSpeakerState(deviceId: AudioPrefs.deviceId, self.player.speakerState())
        }
    }

    // MARK: - Playing on another speaker ("Play on…")

    func speakers() async -> [Speaker] {
        guard let api else { return [] }
        let all = (try? await api.speakers()) ?? []
        return all.filter { $0.deviceId != AudioPrefs.deviceId }
    }

    // Hands the queue over: the other speaker plays from here, this TV pauses.
    func handOff(to speaker: Speaker) async throws {
        guard let api, let snapshot = player.nowPlayingSnapshot() else { return }
        let command = SpeakerCommand(action: "play", queue: snapshot.queue, index: snapshot.index,
                                     positionSeconds: snapshot.positionSeconds, shuffle: false, context: snapshot.context)
        try await api.sendCommand(deviceId: speaker.deviceId, command)
        player.pause()
    }

    func send(_ action: String, to speaker: Speaker, volume: Double? = nil, position: Double? = nil, text: String? = nil) async throws {
        guard let api else { return }
        try await api.sendCommand(deviceId: speaker.deviceId,
                                  SpeakerCommand(action: action, positionSeconds: position, volume: volume, text: text))
    }

    // MARK: - Playing things

    func playBriefing() async throws {
        guard let api else { return }
        let briefing = try await api.briefing()
        showNowPlaying = true
        await player.playBriefing(briefing)
    }

    func playMix(id: String, name: String) async throws {
        guard let api else { return }
        let mix = try await api.mix(id: id)
        let tracks = mix.tracks ?? []
        guard !tracks.isEmpty else { throw AudioAPI.Failure(status: 404, message: "That mix is empty right now.") }
        player.play(tracks: tracks, context: PlayContext(type: "mix", id: id, name: mix.name ?? name))
        showNowPlaying = true
    }

    // Radio/instant mix from a seed; it keeps topping itself up as it plays.
    func startRadio(seedType: String, seedId: String, name: String) async throws {
        guard let api else { return }
        let mix = try await api.radio(seedType: seedType, seedId: seedId)
        let tracks = mix.tracks ?? []
        guard !tracks.isEmpty else { throw AudioAPI.Failure(status: 404, message: "Nothing to make a radio from yet.") }
        player.play(tracks: tracks, context: PlayContext(type: "radio", id: "\(seedType):\(seedId)", name: mix.name ?? "\(name) Radio"))
        showNowPlaying = true
    }

    func shuffleAll() async throws {
        guard let api else { return }
        let tracks = try await api.shuffleAll()
        guard !tracks.isEmpty else { throw AudioAPI.Failure(status: 404, message: "The library is empty.") }
        player.play(tracks: tracks, context: PlayContext(type: "mix", id: "shuffle", name: "Shuffle All"))
        showNowPlaying = true
    }

    func play(tracks: [Track], startAt: Int = 0, shuffle: Bool = false, context: PlayContext?) {
        player.play(tracks: tracks, startAt: startAt, shuffle: shuffle, context: context)
        showNowPlaying = true
    }

    func play(episodes: [Episode], startAt: Int = 0, position: Double? = nil, context: PlayContext?) {
        player.play(episodes: episodes, startAt: startAt, position: position, context: context)
        showNowPlaying = true
    }
}
