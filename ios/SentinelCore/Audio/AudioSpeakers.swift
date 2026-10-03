import SwiftUI

// Speakers (§8), both directions:
// - SpeakerHost is "Let others play here": this device registers itself, keeps a heartbeat going,
//   long-polls for commands and reports what it is playing, so the kiosk or another phone can
//   play to it and control it.
// - SpeakerRemote is "Play on…": hands this device's queue to another speaker and then controls it.

@MainActor
@Observable
final class SpeakerHost {
    static let shared = SpeakerHost()

    private(set) var running = false
    private(set) var lastCommand: String?
    private(set) var problem: String?

    var enabled: Bool {
        get { access(keyPath: \.enabled); return UserDefaults.standard.bool(forKey: "audio.speakerEnabled") }
        set {
            withMutation(keyPath: \.enabled) { UserDefaults.standard.set(newValue, forKey: "audio.speakerEnabled") }
            if newValue { start() } else { stop(unregister: true) }
        }
    }

    var allowAnnouncements: Bool {
        get { access(keyPath: \.allowAnnouncements); return UserDefaults.standard.object(forKey: "audio.allowAnnouncements") as? Bool ?? true }
        set {
            withMutation(keyPath: \.allowAnnouncements) { UserDefaults.standard.set(newValue, forKey: "audio.allowAnnouncements") }
            if running { Task { await heartbeat() } }
        }
    }

    @ObservationIgnored private var heartbeatTask: Task<Void, Never>?
    @ObservationIgnored private var pollTask: Task<Void, Never>?
    @ObservationIgnored private var stateTask: Task<Void, Never>?
    @ObservationIgnored private var lastStateSent = Date.distantPast

    private var client: SentinelClient { AppModel.shared.client }
    private var player: AudioPlayer { AudioPlayer.shared }

    // Called once the app is connected: a switch left on comes back on by itself.
    func resumeIfEnabled() {
        if enabled, !running { start() }
    }

    func start() {
        guard !running else { return }
        running = true
        problem = nil
        heartbeatTask = Task {
            while !Task.isCancelled {
                await heartbeat()
                try? await Task.sleep(for: .seconds(60))
            }
        }
        pollTask = Task { await pollCommands() }
        playerChanged()
    }

    func stop(unregister: Bool) {
        heartbeatTask?.cancel()
        pollTask?.cancel()
        stateTask?.cancel()
        heartbeatTask = nil
        pollTask = nil
        running = false
        if unregister {
            let client = client
            let id = AudioDevice.id
            Task { try? await client.removeSpeaker(deviceId: id) }
        }
    }

    private func heartbeat() async {
        do {
            try await client.speakerHeartbeat(deviceId: AudioDevice.id, SpeakerHeartbeat(
                name: AudioDevice.name, kind: AudioDevice.kind,
                allowAnnouncements: allowAnnouncements, volume: player.userVolume))
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }

    // One command at a time, in seq order, and an `announce` is spoken to the end before the next
    // one is applied: an alarm's briefing is `announce` then `play`, and the episodes must wait.
    private func pollCommands() async {
        var after = -1
        while !Task.isCancelled {
            do {
                let reply = try await client.waitForSpeakerCommands(deviceId: AudioDevice.id, after: after)
                for command in reply.commands.sorted(by: { ($0.seq ?? 0) < ($1.seq ?? 0) }) {
                    if Task.isCancelled { return }
                    if let seq = command.seq, seq <= after { continue }
                    await apply(command)
                    if let seq = command.seq { after = max(after, seq) }
                }
                if let latest = reply.latestSeq { after = max(after, latest) }
                problem = nil
            } catch {
                if Task.isCancelled { return }
                problem = error.localizedDescription
                try? await Task.sleep(for: .seconds(5))
            }
        }
    }

    private func apply(_ command: SpeakerCommand) async {
        lastCommand = [command.action, command.from.map { "from \($0)" }].compactMap { $0 }.joined(separator: " ")
        switch command.action {
        case "play":
            if let volume = command.volume { player.setUserVolume(volume, ramp: command.rampSeconds ?? 0) }
            else if let ramp = command.rampSeconds, ramp > 0 { player.setUserVolume(player.userVolume, ramp: ramp) }
            await player.playRefs(command.queue ?? [], index: command.index ?? 0, position: command.positionSeconds ?? 0,
                                  shuffle: command.shuffle ?? false, context: command.context)
        case "pause": player.pause()
        case "resume": player.resume()
        case "next": player.next()
        case "previous": player.previous()
        case "seek": player.seek(to: command.positionSeconds ?? 0)
        case "volume": player.setUserVolume(command.volume ?? player.userVolume, ramp: command.rampSeconds ?? 0)
        case "stop": player.stop()
        case "duck": player.setDucked(true)
        case "unduck": player.setDucked(false)
        case "announce":
            if allowAnnouncements, let text = command.text, !text.isEmpty {
                await player.announce(text, volume: command.volume)
            }
        default: break
        }
        playerChanged()
        await heartbeat()
    }

    // MARK: State reports

    func playerChanged() {
        guard running else { return }
        stateTask?.cancel()
        stateTask = Task {
            try? await Task.sleep(for: .milliseconds(300))
            guard !Task.isCancelled else { return }
            await sendState()
        }
    }

    func playerTicked() {
        guard running, Date().timeIntervalSince(lastStateSent) >= 15 else { return }
        Task { await sendState() }
    }

    private func sendState() async {
        lastStateSent = Date()
        let entry = player.current
        let state = SpeakerState(
            playing: player.isPlaying, title: entry?.title ?? player.speaking, subtitle: entry?.subtitle,
            artUrl: entry?.artPath, positionSeconds: (player.position * 10).rounded() / 10,
            durationSeconds: player.duration > 0 ? player.duration : nil, volume: player.userVolume,
            kind: entry.map { $0.isEpisode ? "podcast" : "music" }, itemId: entry?.itemId)
        try? await client.reportSpeakerState(deviceId: AudioDevice.id, state)
    }
}

// MARK: - Play on another speaker

@MainActor
@Observable
final class SpeakerRemote {
    static let shared = SpeakerRemote()

    private(set) var speakers: [AudioSpeaker] = []
    private(set) var loading = false
    private(set) var problem: String?
    // The speaker this device is controlling, kept current while it is.
    private(set) var target: AudioSpeaker?

    @ObservationIgnored private var pollTask: Task<Void, Never>?
    private var client: SentinelClient { AppModel.shared.client }

    // Everyone else's speakers: this device is "This iPhone", not a row of its own.
    var others: [AudioSpeaker] { speakers.filter { $0.deviceId != AudioDevice.id } }

    func refresh() async {
        loading = true
        defer { loading = false }
        do {
            speakers = try await client.speakers().sorted { ($0.online == true ? 0 : 1, $0.name) < ($1.online == true ? 0 : 1, $1.name) }
            if let target, let fresh = speakers.first(where: { $0.deviceId == target.deviceId }) { self.target = fresh }
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }

    // Sends what is playing here to the speaker, from the same spot, and pauses this device.
    func handOff(to speaker: AudioSpeaker) async {
        let player = AudioPlayer.shared
        guard player.current != nil else {
            control(speaker)
            return
        }
        let lower = max(0, min(player.index - 250, player.queue.count - 500))
        let refs = Array(player.queue[lower..<min(player.queue.count, lower + 500)]).map(\.ref)
        let command = SpeakerCommand(
            action: "play", queue: refs, index: player.index - lower, positionSeconds: player.position,
            shuffle: player.shuffle, volume: nil, rampSeconds: nil, text: nil, context: player.context)
        do {
            try await client.sendSpeakerCommand(deviceId: speaker.deviceId, command)
            player.pause()
            control(speaker)
            AudioToast.shared.show("Playing on \(speaker.name)")
        } catch {
            problem = error.localizedDescription
        }
    }

    func control(_ speaker: AudioSpeaker) {
        target = speaker
        pollTask?.cancel()
        pollTask = Task {
            while !Task.isCancelled, target != nil {
                await refresh()
                try? await Task.sleep(for: .seconds(2))
            }
        }
    }

    func release() {
        target = nil
        pollTask?.cancel()
        pollTask = nil
    }

    func send(_ action: String, position: Double? = nil, volume: Double? = nil) {
        guard let target else { return }
        let client = client
        let command = SpeakerCommand(action: action, queue: nil, index: nil, positionSeconds: position, shuffle: nil,
                                     volume: volume, rampSeconds: nil, text: nil, context: nil)
        // Show the change straight away; the next poll confirms it.
        if var state = target.state {
            switch action {
            case "pause": state.playing = false
            case "resume": state.playing = true
            case "seek": state.positionSeconds = position
            case "volume": state.volume = volume
            default: break
            }
            self.target?.state = state
        }
        Task {
            do { try await client.sendSpeakerCommand(deviceId: target.deviceId, command) }
            catch { self.problem = error.localizedDescription }
        }
    }

    // Brings the speaker's queue back to this device and stops it there.
    func bringBack() async {
        guard let target else { return }
        let device = NowPlayingDevice(deviceId: target.deviceId, deviceName: target.name, kind: target.state?.kind,
                                      queue: nil, index: nil, positionSeconds: target.state?.positionSeconds, playing: true,
                                      shuffle: nil, repeat: nil, speed: nil, context: nil, updatedAt: nil)
        do {
            try await AudioPlayer.shared.takeOver(device)
            send("stop")
            release()
        } catch {
            problem = "\(target.name) hasn't shared its queue: \(error.localizedDescription)"
        }
    }
}

// MARK: - Views

// "Play on…": this device, then every other speaker in the house.
struct SpeakerPickerView: View {
    @Environment(\.dismiss) private var dismiss
    private var remote: SpeakerRemote { SpeakerRemote.shared }
    private var host: SpeakerHost { SpeakerHost.shared }

    var body: some View {
        NavigationStack {
            List {
                Section {
                    Button {
                        remote.release()
                        dismiss()
                    } label: {
                        HStack {
                            Label("This \(AudioDevice.kind == "mac" ? "Mac" : AudioDevice.kind == "tablet" ? "iPad" : "iPhone")",
                                  systemImage: AudioDevice.kind == "mac" ? "desktopcomputer" : AudioDevice.kind == "tablet" ? "ipad" : "iphone")
                            Spacer()
                            if remote.target == nil { Image(systemName: "checkmark").foregroundStyle(SentinelTheme.green) }
                        }
                    }
                    #if os(iOS)
                    HStack {
                        Text("AirPlay and Bluetooth")
                        Spacer()
                        AirPlayButton().frame(width: 30, height: 30)
                    }
                    #endif
                }
                Section {
                    if remote.others.isEmpty && !remote.loading {
                        Text("No other speakers. Turn on \u{201C}Let others play here\u{201D} on a kiosk, Apple TV, Mac or phone and it shows up here.")
                            .font(.footnote).foregroundStyle(.secondary)
                    }
                    ForEach(remote.others) { speaker in
                        Button {
                            Task {
                                await remote.handOff(to: speaker)
                                if remote.problem == nil { dismiss() }
                            }
                        } label: {
                            SpeakerRow(speaker: speaker, selected: remote.target?.deviceId == speaker.deviceId)
                        }
                        .disabled(speaker.online != true)
                    }
                } header: {
                    HStack {
                        Text("Speakers")
                        if remote.loading { ProgressView().controlSize(.small) }
                    }
                } footer: {
                    if let problem = remote.problem { Text(problem).foregroundStyle(SentinelTheme.red) }
                }
                Section {
                    Toggle("Let Others Play Here", isOn: Binding(get: { host.enabled }, set: { host.enabled = $0 }))
                } footer: {
                    Text("Makes this device a speaker the rest of the house can play to and control - and wake-up alarms can use it. Keep the app open.")
                }
            }
            .navigationTitle("Play On")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } }
            }
            .task { await remote.refresh() }
            .refreshable { await remote.refresh() }
        }
        #if os(macOS)
        .frame(minWidth: 420, minHeight: 480)
        #endif
    }
}

private struct SpeakerRow: View {
    let speaker: AudioSpeaker
    let selected: Bool

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: speaker.icon)
                .font(.title3)
                .frame(width: 32)
                .foregroundStyle(speaker.online == true ? SentinelTheme.green : .secondary)
            VStack(alignment: .leading, spacing: 2) {
                Text(speaker.name).foregroundStyle(.primary)
                Text(statusLine).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer()
            if selected { Image(systemName: "checkmark").foregroundStyle(SentinelTheme.green) }
        }
        .contentShape(Rectangle())
    }

    private var statusLine: String {
        guard speaker.online == true else { return "Offline" }
        if let state = speaker.state, let title = state.title {
            return (state.playing == true ? "Playing " : "Paused · ") + title
        }
        return "Idle"
    }
}

// The bar shown in place of the local mini player while this device controls another speaker.
struct RemoteSpeakerBar: View {
    private var remote: SpeakerRemote { SpeakerRemote.shared }
    @State private var showingPanel = false

    var body: some View {
        if let target = remote.target {
            HStack(spacing: 12) {
                ArtworkImage(path: target.state?.artUrl, size: 44, corner: 6, placeholder: target.icon)
                VStack(alignment: .leading, spacing: 1) {
                    Text(target.state?.title ?? "Nothing playing").font(.subheadline.weight(.semibold)).lineLimit(1)
                    Label("On \(target.name)", systemImage: target.icon)
                        .font(.caption).foregroundStyle(SentinelTheme.green).lineLimit(1)
                }
                Spacer(minLength: 4)
                Button {
                    remote.send(target.state?.playing == true ? "pause" : "resume")
                } label: {
                    Image(systemName: target.state?.playing == true ? "pause.fill" : "play.fill").font(.title3)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(target.state?.playing == true ? "Pause" : "Play")
                Button { remote.send("next") } label: { Image(systemName: "forward.fill").font(.title3) }
                    .buttonStyle(.plain)
                    .accessibilityLabel("Next")
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            .contentShape(Rectangle())
            .onTapGesture { showingPanel = true }
            .sheet(isPresented: $showingPanel) { RemoteSpeakerPanel() }
        }
    }
}

struct RemoteSpeakerPanel: View {
    @Environment(\.dismiss) private var dismiss
    private var remote: SpeakerRemote { SpeakerRemote.shared }
    @State private var scrub: Double?
    @State private var volume: Double?

    var body: some View {
        NavigationStack {
            VStack(spacing: 20) {
                if let target = remote.target {
                    let state = target.state
                    ArtworkImage(path: state?.artUrl, size: 240, corner: 14, placeholder: target.icon)
                        .shadow(radius: 12, y: 6)
                    VStack(spacing: 4) {
                        Text(state?.title ?? "Nothing playing").font(.title3.bold()).multilineTextAlignment(.center)
                        if let subtitle = state?.subtitle { Text(subtitle).foregroundStyle(.secondary) }
                    }
                    if let duration = state?.durationSeconds, duration > 0 {
                        VStack(spacing: 2) {
                            Slider(value: Binding(get: { scrub ?? state?.positionSeconds ?? 0 }, set: { scrub = $0 }),
                                   in: 0...duration) { editing in
                                if !editing, let scrub {
                                    remote.send("seek", position: scrub)
                                    self.scrub = nil
                                }
                            }
                            HStack {
                                Text(AudioFormat.duration(scrub ?? state?.positionSeconds ?? 0))
                                Spacer()
                                Text(AudioFormat.duration(duration))
                            }
                            .font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                        }
                    }
                    HStack(spacing: 44) {
                        Button { remote.send("previous") } label: { Image(systemName: "backward.fill") }
                        Button { remote.send(state?.playing == true ? "pause" : "resume") } label: {
                            Image(systemName: state?.playing == true ? "pause.circle.fill" : "play.circle.fill").font(.system(size: 60))
                        }
                        Button { remote.send("next") } label: { Image(systemName: "forward.fill") }
                    }
                    .font(.title)
                    .buttonStyle(.plain)
                    HStack {
                        Image(systemName: "speaker.fill").foregroundStyle(.secondary)
                        Slider(value: Binding(get: { volume ?? state?.volume ?? target.volume ?? 0.5 }, set: { volume = $0 }),
                               in: 0...1) { editing in
                            if !editing, let volume {
                                remote.send("volume", volume: volume)
                                self.volume = nil
                            }
                        }
                        Image(systemName: "speaker.wave.3.fill").foregroundStyle(.secondary)
                    }
                    HStack {
                        Button("Play Here Instead") { Task { await remote.bringBack(); dismiss() } }
                            .buttonStyle(.bordered)
                        Button("Stop", role: .destructive) { remote.send("stop") }
                            .buttonStyle(.bordered)
                    }
                    if let problem = remote.problem {
                        Text(problem).font(.footnote).foregroundStyle(SentinelTheme.red)
                    }
                } else {
                    ContentUnavailableView("Not controlling a speaker", systemImage: "hifispeaker")
                }
            }
            .padding()
            .frame(maxWidth: 520)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .navigationTitle(remote.target?.name ?? "Speaker")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Disconnect") { remote.release(); dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } }
            }
        }
        #if os(macOS)
        .frame(minWidth: 460, minHeight: 620)
        #endif
    }
}
