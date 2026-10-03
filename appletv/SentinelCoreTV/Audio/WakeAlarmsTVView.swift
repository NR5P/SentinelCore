import SwiftUI

// Wake-up alarms (core/docs/MUSIC_AND_PODCASTS.md §9): music, a show or the Morning Briefing,
// started on a speaker at a set time, fading up. Per person; the server fires them.
struct WakeAlarmsTVView: View {
    @EnvironmentObject private var audio: AudioCenter
    @State private var alarms: [WakeAlarm] = []
    @State private var loaded = false
    @State private var errorMessage: String?
    @State private var refresh = 0

    var body: some View {
        List {
            Section {
                NavigationLink {
                    WakeAlarmEditView(existing: nil) { refresh += 1 }
                } label: {
                    Label("New Alarm", systemImage: "plus")
                }
            }
            Section {
                if loaded, alarms.isEmpty {
                    Text("No alarms yet.").foregroundStyle(.secondary)
                }
                ForEach(alarms) { alarm in
                    NavigationLink {
                        WakeAlarmEditView(existing: alarm) { refresh += 1 }
                    } label: {
                        HStack(spacing: 30) {
                            Text(WakeAlarmText.time(alarm.time)).font(.system(size: 48, weight: .light).monospacedDigit())
                                .foregroundStyle(alarm.enabled == false ? .secondary : .primary)
                            VStack(alignment: .leading, spacing: 4) {
                                Text(alarm.name?.isEmpty == false ? alarm.name! : "Alarm")
                                Text([WakeAlarmText.days(alarm.days ?? []), WakeAlarmText.source(alarm.source)].joined(separator: " · "))
                                    .font(.caption).foregroundStyle(.secondary)
                                if alarm.enabled != false, let next = AudioDate.parse(alarm.nextFireAt) {
                                    Text("Next: \(next.formatted(date: .abbreviated, time: .shortened))").font(.caption2).foregroundStyle(.secondary)
                                }
                            }
                            Spacer()
                            Text(alarm.enabled == false ? "Off" : "On").foregroundStyle(alarm.enabled == false ? .secondary : SentinelTheme.green)
                        }
                    }
                }
            } footer: {
                if let errorMessage { Text(errorMessage).foregroundStyle(SentinelTheme.red) }
                else { Text("Alarms play on a speaker - this TV, a kiosk, or a phone left open. An offline speaker misses the alarm and you're told.") }
            }
        }
        .navigationTitle("Wake-up Alarms")
        .task(id: refresh) {
            do {
                alarms = try await audio.api?.alarms() ?? []
                errorMessage = nil
            } catch { errorMessage = error.localizedDescription }
            loaded = true
        }
    }
}

enum WakeAlarmText {
    static let dayKeys = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"]
    static let dayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]

    static func time(_ value: String) -> String {
        let parts = value.split(separator: ":").compactMap { Int($0) }
        guard parts.count >= 2 else { return value }
        var components = DateComponents()
        components.hour = parts[0]
        components.minute = parts[1]
        guard let date = Calendar.current.date(from: components) else { return value }
        return date.formatted(date: .omitted, time: .shortened)
    }

    static func days(_ days: [String]) -> String {
        let set = Set(days)
        if set.isEmpty { return "Once" }
        if set.count == 7 { return "Every day" }
        if set == ["mon", "tue", "wed", "thu", "fri"] { return "Weekdays" }
        if set == ["sat", "sun"] { return "Weekends" }
        return dayKeys.enumerated().filter { set.contains($0.element) }.map { dayNames[$0.offset] }.joined(separator: " ")
    }

    static func source(_ source: AlarmSource?) -> String {
        guard let source else { return "Shuffle" }
        switch source.type {
        case "briefing": return "Morning Briefing"
        case "shuffle": return "Shuffle All"
        default: return source.name ?? source.type.capitalized
        }
    }
}

private struct WakeAlarmEditView: View {
    let existing: WakeAlarm?
    let onChange: () -> Void
    @EnvironmentObject private var audio: AudioCenter
    @Environment(\.dismiss) private var dismiss
    @State private var name: String
    @State private var hour: Int
    @State private var minute: Int
    @State private var days: Set<String>
    @State private var speakerId: String?
    @State private var source: AlarmSource
    @State private var volume: Double
    @State private var ramp: Double
    @State private var enabled: Bool
    @State private var speakers: [Speaker] = []
    @State private var playlists: [Playlist] = []
    @State private var mixes: [Mix] = []
    @State private var shows: [Podcast] = []
    @State private var message: String?
    @State private var saving = false
    @State private var confirmingDelete = false

    init(existing: WakeAlarm?, onChange: @escaping () -> Void) {
        self.existing = existing
        self.onChange = onChange
        let parts = (existing?.time ?? "06:45").split(separator: ":").compactMap { Int($0) }
        _name = State(initialValue: existing?.name ?? "Wake Up")
        _hour = State(initialValue: parts.first ?? 6)
        _minute = State(initialValue: parts.dropFirst().first ?? 45)
        _days = State(initialValue: Set(existing?.days ?? ["mon", "tue", "wed", "thu", "fri"]))
        _speakerId = State(initialValue: existing?.speakerId ?? AudioPrefs.deviceId)
        _source = State(initialValue: existing?.source ?? AlarmSource(type: "briefing", id: nil, name: "Morning Briefing"))
        _volume = State(initialValue: existing?.volume ?? 0.5)
        _ramp = State(initialValue: existing?.rampSeconds ?? 120)
        _enabled = State(initialValue: existing?.enabled ?? true)
    }

    var body: some View {
        List {
            Section {
                TextField("Name", text: $name)
                Toggle("On", isOn: $enabled)
            }
            Section("Time") {
                Menu {
                    ForEach(0..<24, id: \.self) { h in Button(String(format: "%02d", h)) { hour = h } }
                } label: { LabeledContent("Hour", value: String(format: "%02d", hour)) }
                Menu {
                    ForEach(Array(stride(from: 0, to: 60, by: 5)), id: \.self) { m in Button(String(format: ":%02d", m)) { minute = m } }
                } label: { LabeledContent("Minute", value: String(format: ":%02d", minute)) }
                Text("Goes off at \(WakeAlarmText.time(String(format: "%02d:%02d", hour, minute))), the server's time zone.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section {
                ForEach(Array(WakeAlarmText.dayKeys.enumerated()), id: \.offset) { i, key in
                    Toggle(Calendar.current.weekdaySymbols[(i + 1) % 7], isOn: Binding(
                        get: { days.contains(key) },
                        set: { on in if on { days.insert(key) } else { days.remove(key) } }))
                }
            } header: {
                Text("Repeat")
            } footer: {
                Text(days.isEmpty ? "No days: goes off once, then turns itself off." : WakeAlarmText.days(Array(days)))
            }
            Section("Plays") {
                Menu {
                    Button("Morning Briefing") { source = AlarmSource(type: "briefing", id: nil, name: "Morning Briefing") }
                    Button("Shuffle All") { source = AlarmSource(type: "shuffle", id: nil, name: "Shuffle All") }
                    if !mixes.isEmpty {
                        Section("Mixes") {
                            ForEach(mixes) { mix in Button(mix.name) { source = AlarmSource(type: "mix", id: mix.id, name: mix.name) } }
                        }
                    }
                    if !playlists.isEmpty {
                        Section("Playlists") {
                            ForEach(playlists) { p in Button(p.name) { source = AlarmSource(type: "playlist", id: p.id, name: p.name) } }
                        }
                    }
                    if !shows.isEmpty {
                        Section("Newest episode of") {
                            ForEach(shows) { s in Button(s.title) { source = AlarmSource(type: "podcast", id: s.id, name: s.title) } }
                        }
                    }
                } label: {
                    LabeledContent("Source", value: WakeAlarmText.source(source))
                }
                Menu {
                    Button("This Apple TV (\(AudioPrefs.speakerName))") { speakerId = AudioPrefs.deviceId }
                    ForEach(speakers) { s in Button("\(s.name)\(s.online == true ? "" : " (offline)")") { speakerId = s.deviceId } }
                } label: {
                    LabeledContent("Speaker", value: speakerName)
                }
                Menu {
                    ForEach([0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 1.0], id: \.self) { v in Button("\(Int(v * 100))%") { volume = v } }
                } label: { LabeledContent("Volume", value: "\(Int(volume * 100))%") }
                Menu {
                    ForEach([0.0, 30, 60, 120, 300, 600], id: \.self) { s in
                        Button(s == 0 ? "No fade" : (s < 60 ? "\(Int(s)) seconds" : "\(Int(s / 60)) minutes")) { ramp = s }
                    }
                } label: { LabeledContent("Fade In", value: ramp == 0 ? "None" : (ramp < 60 ? "\(Int(ramp)) s" : "\(Int(ramp / 60)) min")) }
            }
            Section {
                Button {
                    Task { await save() }
                } label: {
                    HStack { Label(existing == nil ? "Add Alarm" : "Save", systemImage: "checkmark"); if saving { Spacer(); ProgressView() } }
                }
                if let existing {
                    Button {
                        Task {
                            do { try await audio.api?.testAlarm(id: existing.id); message = "Playing it now on \(speakerName)." }
                            catch { message = error.localizedDescription }
                        }
                    } label: { Label("Test Now", systemImage: "play.circle") }
                    Button(role: .destructive) { confirmingDelete = true } label: { Label("Delete Alarm", systemImage: "trash") }
                }
                if let message { Text(message).foregroundStyle(.secondary) }
            }
        }
        .navigationTitle(existing == nil ? "New Alarm" : (existing?.name ?? "Alarm"))
        .confirmationDialog("Delete this alarm?", isPresented: $confirmingDelete, titleVisibility: .visible) {
            Button("Delete", role: .destructive) {
                Task {
                    if let id = existing?.id { try? await audio.api?.deleteAlarm(id: id) }
                    onChange()
                    dismiss()
                }
            }
        }
        .task {
            guard let api = audio.api else { return }
            speakers = await audio.speakers()
            if audio.musicEnabled {
                playlists = (try? await api.playlists()) ?? []
                mixes = (try? await api.musicHome())?.mixes ?? []
            }
            if audio.podcastsEnabled { shows = (try? await api.subscriptions()) ?? [] }
        }
    }

    private var speakerName: String {
        if speakerId == AudioPrefs.deviceId { return "This Apple TV" }
        return speakers.first { $0.deviceId == speakerId }?.name ?? "Choose…"
    }

    private func save() async {
        guard let api = audio.api else { return }
        saving = true
        defer { saving = false }
        let input = WakeAlarmInput(
            name: name.trimmingCharacters(in: .whitespaces), time: String(format: "%02d:%02d", hour, minute),
            days: WakeAlarmText.dayKeys.filter { days.contains($0) }, speakerId: speakerId, source: source,
            volume: volume, rampSeconds: ramp, enabled: enabled)
        do {
            _ = try await api.saveAlarm(id: existing?.id, input)
            onChange()
            dismiss()
        } catch {
            message = error.localizedDescription
        }
    }
}
