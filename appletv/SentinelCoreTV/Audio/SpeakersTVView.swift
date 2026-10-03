import SwiftUI

// "Play on…": every speaker in the household (kiosks, other TVs, phones left open). Hand this
// TV's queue to one, or drive one from the sofa: pause, skip, volume, say something.
struct SpeakersTVView: View {
    @EnvironmentObject private var audio: AudioCenter
    @ObservedObject private var player = AudioCenter.shared.player
    @State private var speakers: [Speaker] = []
    @State private var loaded = false
    @State private var message: String?

    var body: some View {
        List {
            Section {
                LabeledContent("This Apple TV", value: audio.speakerEnabled
                               ? "\(AudioPrefs.speakerName) · \(audio.speakerOnline ? "others can play here" : "connecting…")"
                               : "Not a speaker (Settings → Let Others Play Here)")
            }
            Section {
                if loaded, speakers.isEmpty {
                    Text("No other speakers. A kiosk, another Apple TV or an app with “Let others play here” on shows up here.")
                        .foregroundStyle(.secondary)
                }
                ForEach(speakers) { speaker in
                    NavigationLink {
                        SpeakerControlView(speaker: speaker)
                    } label: {
                        SpeakerRow(speaker: speaker)
                    }
                }
            } header: {
                Text("Speakers")
            } footer: {
                if let message { Text(message) }
            }
        }
        .navigationTitle("Play On…")
        .task {
            while !Task.isCancelled {
                speakers = await audio.speakers()
                loaded = true
                try? await Task.sleep(for: .seconds(5))
            }
        }
    }
}

private struct SpeakerRow: View {
    let speaker: Speaker

    var body: some View {
        HStack(spacing: 24) {
            Image(systemName: speaker.icon).font(.title2).frame(width: 60)
            VStack(alignment: .leading, spacing: 4) {
                Text(speaker.name)
                Text(line).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer()
            Circle().fill(speaker.online == true ? SentinelTheme.green : Color.gray).frame(width: 14, height: 14)
        }
    }

    private var line: String {
        guard speaker.online == true else { return "Offline" }
        if let state = speaker.state, let title = state.title {
            return "\(state.playing == true ? "Playing" : "Paused"): \(title)\(state.subtitle.map { " — \($0)" } ?? "")"
        }
        return "Idle"
    }
}

struct SpeakerControlView: View {
    let speaker: Speaker
    @EnvironmentObject private var audio: AudioCenter
    @ObservedObject private var player = AudioCenter.shared.player
    @State private var live: Speaker?
    @State private var announcement = ""
    @State private var message: String?

    private var current: Speaker { live ?? speaker }

    var body: some View {
        List {
            Section {
                HStack(spacing: 30) {
                    AudioArt(path: current.state?.artUrl, title: current.state?.title ?? current.name, size: 300)
                        .frame(width: 160, height: 160)
                        .clipShape(RoundedRectangle(cornerRadius: 12))
                    VStack(alignment: .leading, spacing: 6) {
                        Text(current.name).font(.title3.bold())
                        Text(current.online == true ? (current.state?.title ?? "Nothing playing") : "Offline")
                        if let subtitle = current.state?.subtitle { Text(subtitle).font(.caption).foregroundStyle(.secondary) }
                        if let volume = current.state?.volume ?? current.volume {
                            Text("Volume \(Int(volume * 100))%").font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
            }
            if player.current != nil {
                Section {
                    Button {
                        act { try await audio.handOff(to: speaker); message = "Playing on \(speaker.name). This TV paused." }
                    } label: {
                        Label("Play What's Playing Here on \(speaker.name)", systemImage: "arrow.right.circle")
                    }
                }
            }
            Section("Control") {
                let playing = current.state?.playing == true
                Button { act { try await audio.send(playing ? "pause" : "resume", to: speaker) } } label: {
                    Label(playing ? "Pause" : "Resume", systemImage: playing ? "pause.fill" : "play.fill")
                }
                Button { act { try await audio.send("next", to: speaker) } } label: { Label("Next", systemImage: "forward.fill") }
                Button { act { try await audio.send("previous", to: speaker) } } label: { Label("Previous", systemImage: "backward.fill") }
                Menu {
                    ForEach([0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0], id: \.self) { level in
                        Button("\(Int(level * 100))%") { act { try await audio.send("volume", to: speaker, volume: level) } }
                    }
                } label: {
                    Label("Volume", systemImage: "speaker.wave.2")
                }
                Button(role: .destructive) { act { try await audio.send("stop", to: speaker) } } label: {
                    Label("Stop", systemImage: "stop.fill")
                }
            }
            if current.allowAnnouncements == true {
                Section {
                    TextField("Say something on \(speaker.name)", text: $announcement)
                    Button {
                        let text = announcement
                        act {
                            try await audio.send("announce", to: speaker, text: text)
                            announcement = ""
                            message = "Announced."
                        }
                    } label: {
                        Label("Announce", systemImage: "megaphone")
                    }
                    .disabled(announcement.trimmingCharacters(in: .whitespaces).isEmpty)
                } header: {
                    Text("Announce")
                }
            }
            if let message {
                Section { Text(message).foregroundStyle(.secondary) }
            }
        }
        .navigationTitle(speaker.name)
        .task {
            while !Task.isCancelled {
                live = await audio.speakers().first { $0.deviceId == speaker.deviceId }
                try? await Task.sleep(for: .seconds(3))
            }
        }
    }

    private func act(_ work: @escaping () async throws -> Void) {
        Task {
            do { try await work() } catch { message = error.localizedDescription }
        }
    }
}
