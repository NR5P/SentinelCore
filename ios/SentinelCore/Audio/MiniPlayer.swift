import SwiftUI

// The bar above the tab bar (or along the bottom of the Mac/iPad detail column) on every section:
// what's playing, play/pause and next, and a thin progress line. Tapping it opens Now Playing.
// While this device is controlling another speaker, that speaker's bar takes its place.
struct MiniPlayerBar: View {
    #if os(macOS)
    @Environment(\.openWindow) private var openWindow
    #endif

    var body: some View {
        let player = AudioPlayer.shared
        let remote = SpeakerRemote.shared
        Group {
            if remote.target != nil && !player.isPlaying {
                RemoteSpeakerBar()
            } else if player.hasItem {
                bar(player)
            }
        }
        .background(.bar)
        .overlay(alignment: .top) {
            if player.hasItem || remote.target != nil { Divider() }
        }
    }

    private func bar(_ player: AudioPlayer) -> some View {
        let entry = player.current
        return HStack(spacing: 12) {
            ArtworkImage(path: entry?.artPath, size: 44, corner: 6, placeholder: entry?.isEpisode == true ? "mic" : "music.note")
                .shadow(color: .black.opacity(0.2), radius: 3, y: 1)
            VStack(alignment: .leading, spacing: 1) {
                Text(player.speaking != nil ? "Morning Briefing" : entry?.title ?? "")
                    .font(.subheadline.weight(.semibold)).lineLimit(1)
                Text(player.speaking ?? entry?.subtitle ?? "")
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer(minLength: 4)
            if player.isBuffering { ProgressView().controlSize(.small) }
            if player.isEpisode {
                iconButton("gobackward.15", "Back 15 seconds") { player.skip(by: -15) }
            }
            iconButton(player.isPlaying ? "pause.fill" : "play.fill", player.isPlaying ? "Pause" : "Play") { player.togglePlayPause() }
                .font(.title2)
            if player.isEpisode {
                iconButton("goforward.30", "Forward 30 seconds") { player.skip(by: 30) }
            } else {
                iconButton("forward.fill", "Next") { player.next() }
                    .disabled(player.upNext.isEmpty && player.repeatMode == .off)
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
        .overlay(alignment: .bottom) {
            GeometryReader { proxy in
                Rectangle()
                    .fill(SentinelTheme.green)
                    .frame(width: player.duration > 0 ? proxy.size.width * min(1, player.position / player.duration) : 0, height: 2)
            }
            .frame(height: 2)
        }
        .contentShape(Rectangle())
        .onTapGesture { openNowPlaying() }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Now playing: \(entry?.title ?? "")")
        .accessibilityAction(named: "Open Now Playing") { openNowPlaying() }
        .contextMenu {
            Button("Open Now Playing", systemImage: "rectangle.expand.vertical") { openNowPlaying() }
            Button("Stop", systemImage: "stop.fill", role: .destructive) { player.stop() }
        }
    }

    private func iconButton(_ symbol: String, _ label: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.title3)
                .frame(width: 36, height: 36)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(label)
    }

    private func openNowPlaying() {
        #if os(macOS)
        openWindow(id: NowPlayingView.windowId)
        #else
        AudioPlayer.shared.showNowPlaying = true
        #endif
    }
}

extension View {
    // Hangs the mini player under a section's content, above the tab bar.
    func audioMiniPlayer() -> some View {
        safeAreaInset(edge: .bottom, spacing: 0) {
            MiniPlayerBar()
        }
    }
}

// "Playing on Nathan's iPad - Continue here" (§7): the newest other device of this person's that
// has something queued and has been heard from in the last day.
struct ContinueListeningBanner: View {
    let kind: String   // music | podcast
    @EnvironmentObject private var model: AppModel
    @Environment(\.scenePhase) private var scenePhase
    @State private var device: NowPlayingDevice?
    // Shared by every copy of the banner and kept for the life of the app: as @State it was
    // forgotten whenever Music was opened afresh, so a session already taken over (or closed
    // with the x) came straight back - "Playing on Kitchen Dashboard - Continue Here" while
    // this phone was the one playing it. The key carries the other device's updatedAt, so a
    // new report from it still shows.
    @MainActor private static var dismissedKeys: Set<String> = []
    @State private var dismissed: Set<String> = ContinueListeningBanner.dismissedKeys

    private func dismiss(_ device: NowPlayingDevice) {
        dismissed.insert(key(device))
        Self.dismissedKeys.insert(key(device))
    }
    @State private var taking = false
    @State private var errorMessage: String?

    var body: some View {
        // A VStack with a zero-height spacer rather than a Group: an empty Group has no lifetime,
        // so the .task below never ran and the banner could never appear.
        VStack(spacing: 0) {
            Color.clear.frame(height: 0)
            if let device, !dismissed.contains(key(device)) {
                HStack(spacing: 10) {
                    Image(systemName: icon(device)).font(.title3).foregroundStyle(SentinelTheme.green)
                    VStack(alignment: .leading, spacing: 1) {
                        Text("\(device.playing == true ? "Playing" : "Paused") on \(device.deviceName ?? "another device")")
                            .font(.subheadline.weight(.semibold))
                        Text(errorMessage ?? device.context?.name ?? "Pick up where you left off")
                            .font(.caption).foregroundStyle(errorMessage == nil ? Color.secondary : SentinelTheme.red).lineLimit(1)
                    }
                    Spacer()
                    Button {
                        Task { await take(device) }
                    } label: {
                        if taking { ProgressView().controlSize(.small) } else { Text("Continue Here") }
                    }
                    .buttonStyle(.borderedProminent).foregroundStyle(Color.onSentinelGreen)
                    .tint(SentinelTheme.green)
                    .controlSize(.small)
                    Button {
                        dismiss(device)
                    } label: {
                        Image(systemName: "xmark").font(.caption)
                    }
                    .buttonStyle(.plain)
                    .foregroundStyle(.secondary)
                    .accessibilityLabel("Dismiss")
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 8)
                .background(SentinelTheme.green.opacity(0.1))
            }
        }
        .task(id: "\(model.activeServerURL)|\(scenePhase == .active)") {
            guard scenePhase == .active, !model.activeServerURL.isEmpty else { return }
            await refresh()
        }
    }

    private func key(_ device: NowPlayingDevice) -> String { "\(device.deviceId)|\(device.updatedAt ?? "")" }

    private func icon(_ device: NowPlayingDevice) -> String {
        let name = (device.deviceName ?? "").lowercased()
        if name.contains("ipad") { return "ipad" }
        if name.contains("iphone") { return "iphone" }
        if name.contains("mac") { return "desktopcomputer" }
        if name.contains("tv") { return "appletv" }
        return "hifispeaker"
    }

    private func refresh() async {
        guard let devices = try? await model.client.nowPlayingDevices() else { return }
        let cutoff = Date().addingTimeInterval(-24 * 3600)
        device = devices.first { device in
            device.deviceId != AudioDevice.id
                && (device.kind ?? kind) == kind
                && !(device.queue ?? []).isEmpty
                && (AudioDate.parse(device.updatedAt) ?? .distantPast) > cutoff
                // Not if this device is already playing that very thing.
                && !(AudioPlayer.shared.isPlaying && AudioPlayer.shared.current?.itemId == device.queue?[safe: device.index ?? 0]?.id)
        }
    }

    private func take(_ device: NowPlayingDevice) async {
        taking = true
        defer { taking = false }
        do {
            try await AudioPlayer.shared.takeOver(device)
            dismiss(device)
            errorMessage = nil
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

extension Array {
    subscript(safe index: Int) -> Element? { indices.contains(index) ? self[index] : nil }
}
