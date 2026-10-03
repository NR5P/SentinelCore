import SwiftUI

// Wake-up alarms (§9): music, a show or the Morning Briefing on a speaker at a set time, fading in.
struct WakeAlarmsView: View {
    @State private var alarms: [WakeAlarm] = []
    @State private var speakers: [AudioSpeaker] = []
    @State private var loading = true
    @State private var errorMessage: String?
    @State private var editing: WakeAlarmEdit?

    var body: some View {
        List {
            ForEach(alarms) { alarm in
                HStack {
                    Button {
                        editing = WakeAlarmEdit(alarm: alarm)
                    } label: {
                        VStack(alignment: .leading, spacing: 3) {
                            Text(displayTime(alarm.time)).font(.system(size: 34, weight: .light).monospacedDigit())
                                .foregroundStyle(alarm.enabled ? .primary : .secondary)
                            Text([alarm.name.isEmpty ? nil : alarm.name, daysLabel(alarm.days)].compactMap { $0 }.joined(separator: " · "))
                                .font(.subheadline)
                            Text("\(sourceLabel(alarm.source)) on \(speakers.first { $0.deviceId == alarm.speakerId }?.name ?? "a speaker")")
                                .font(.caption).foregroundStyle(.secondary)
                            if let next = AudioDate.parse(alarm.nextFireAt), alarm.enabled {
                                Text("Next: \(next.formatted(.relative(presentation: .named)))").font(.caption2).foregroundStyle(.secondary)
                            }
                        }
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    Spacer()
                    Toggle("Enabled", isOn: Binding(get: { alarm.enabled }, set: { value in
                        Task { await toggle(alarm, value) }
                    }))
                    .labelsHidden()
                }
                .swipeActions {
                    Button("Delete", role: .destructive) { Task { await delete(alarm) } }
                }
                .contextMenu {
                    Button("Test Now", systemImage: "play") { Task { await test(alarm) } }
                    Button("Delete", systemImage: "trash", role: .destructive) { Task { await delete(alarm) } }
                }
            }
            if let errorMessage { Text(errorMessage).foregroundStyle(SentinelTheme.red) }
        }
        .overlay {
            if alarms.isEmpty {
                AudioLoadState(loading: loading, error: nil,
                               empty: loading ? nil : ("No wake-up alarms", "alarm", "Wake up to a playlist, a show or your Morning Briefing on any speaker - it fades in gently."))
            }
        }
        .navigationTitle("Wake-Up Alarms")
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button {
                    editing = WakeAlarmEdit(alarm: nil)
                } label: {
                    Label("Add Alarm", systemImage: "plus")
                }
            }
        }
        .sheet(item: $editing) { edit in
            WakeAlarmEditor(alarm: edit.alarm, speakers: speakers) { Task { await load() } }
        }
        .task { await load() }
        .refreshable { await load() }
    }

    private func load() async {
        let client = AppModel.shared.client
        do {
            async let a = client.wakeAlarms()
            async let s = client.speakers()
            alarms = try await a.sorted { $0.time < $1.time }
            speakers = (try? await s) ?? []
            errorMessage = nil
        } catch {
            if !error.isCancellation { errorMessage = error.localizedDescription }
        }
        loading = false
    }

    private func toggle(_ alarm: WakeAlarm, _ enabled: Bool) async {
        var updated = alarm
        updated.enabled = enabled
        do {
            let saved = try await AppModel.shared.client.saveWakeAlarm(updated)
            if let index = alarms.firstIndex(where: { $0.id == alarm.id }) { alarms[index] = saved }
        } catch { errorMessage = error.localizedDescription }
    }

    private func delete(_ alarm: WakeAlarm) async {
        guard let id = alarm.id else { return }
        try? await AppModel.shared.client.deleteWakeAlarm(id: id)
        await load()
    }

    private func test(_ alarm: WakeAlarm) async {
        guard let id = alarm.id else { return }
        do {
            try await AppModel.shared.client.testWakeAlarm(id: id)
            AudioToast.shared.show("Playing it now")
        } catch { errorMessage = error.localizedDescription }
    }
}

struct WakeAlarmEdit: Identifiable {
    let id = UUID()
    let alarm: WakeAlarm?
}

private let weekdays: [(id: String, short: String)] = [
    ("mon", "M"), ("tue", "T"), ("wed", "W"), ("thu", "T"), ("fri", "F"), ("sat", "S"), ("sun", "S"),
]

func displayTime(_ hhmm: String) -> String {
    let parts = hhmm.split(separator: ":").compactMap { Int($0) }
    guard parts.count >= 2 else { return hhmm }
    var components = DateComponents()
    components.hour = parts[0]
    components.minute = parts[1]
    guard let date = Calendar.current.date(from: components) else { return hhmm }
    return date.formatted(date: .omitted, time: .shortened)
}

func daysLabel(_ days: [String]) -> String {
    let set = Set(days)
    if set.isEmpty { return "Once" }
    if set.count == 7 { return "Every day" }
    if set == ["mon", "tue", "wed", "thu", "fri"] { return "Weekdays" }
    if set == ["sat", "sun"] { return "Weekends" }
    let names = ["mon": "Mon", "tue": "Tue", "wed": "Wed", "thu": "Thu", "fri": "Fri", "sat": "Sat", "sun": "Sun"]
    return weekdays.filter { set.contains($0.id) }.compactMap { names[$0.id] }.joined(separator: " ")
}

func sourceLabel(_ source: WakeAlarmSource) -> String {
    switch source.type {
    case "briefing": "Morning Briefing"
    case "shuffle": "Shuffle All"
    case "podcast": "Newest \(source.name ?? "episode")"
    default: source.name ?? source.type.capitalized
    }
}

struct WakeAlarmEditor: View {
    let alarm: WakeAlarm?
    let speakers: [AudioSpeaker]
    let onSaved: () -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var name = ""
    @State private var time = Date()
    @State private var days: Set<String> = ["mon", "tue", "wed", "thu", "fri"]
    @State private var speakerId = ""
    @State private var sourceType = "playlist"
    @State private var sourceId: String?
    @State private var sourceName: String?
    @State private var volume = 0.5
    @State private var ramp = 120.0
    @State private var enabled = true
    @State private var playlists: [MusicPlaylist] = []
    @State private var podcasts: [Podcast] = []
    @State private var mixes: [MusicMix] = []
    @State private var query = ""
    @State private var found: MusicSearchResults?
    @State private var saving = false
    @State private var errorMessage: String?
    @State private var seeded = false

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    DatePicker("Time", selection: $time, displayedComponents: .hourAndMinute)
                    HStack(spacing: 6) {
                        ForEach(weekdays, id: \.id) { day in
                            let on = days.contains(day.id)
                            Button {
                                if on { days.remove(day.id) } else { days.insert(day.id) }
                            } label: {
                                Text(day.short)
                                    .font(.subheadline.weight(.semibold))
                                    .frame(width: 34, height: 34)
                                    .background(on ? SentinelTheme.green : Color.secondary.opacity(0.15), in: Circle())
                                    .foregroundStyle(on ? .white : .primary)
                            }
                            .buttonStyle(.plain)
                            .accessibilityLabel(day.id.capitalized)
                            .accessibilityAddTraits(on ? .isSelected : [])
                        }
                    }
                    Text(daysLabel(Array(days))).font(.caption).foregroundStyle(.secondary)
                    TextField("", text: $name, prompt: Text("Label (optional)"))
                        .labelsHidden()
                        .multilineTextAlignment(.leading)
                        .accessibilityLabel("Label")
                } footer: {
                    Text("No days means once, at the next time it comes round. Times are the server's time zone.")
                }
                Section("Speaker") {
                    if speakers.isEmpty {
                        Text("No speakers yet. Turn on \u{201C}Let Others Play Here\u{201D} on the device that should wake you.")
                            .font(.footnote).foregroundStyle(.secondary)
                    }
                    Picker("Play on", selection: $speakerId) {
                        Text("Choose…").tag("")
                        ForEach(speakers) { speaker in
                            Text(speaker.online == true ? speaker.name : "\(speaker.name) (offline)").tag(speaker.deviceId)
                        }
                    }
                }
                Section("Wake Up To") {
                    Picker("Source", selection: $sourceType) {
                        Text("Playlist").tag("playlist")
                        Text("Album").tag("album")
                        Text("Artist").tag("artist")
                        Text("Mix").tag("mix")
                        Text("Podcast").tag("podcast")
                        Text("Morning Briefing").tag("briefing")
                        Text("Shuffle All").tag("shuffle")
                    }
                    .onChange(of: sourceType) { _, _ in
                        if seeded { sourceId = nil; sourceName = nil }
                    }
                    sourcePicker
                }
                Section {
                    VStack(alignment: .leading) {
                        Text("Volume \(Int(volume * 100))%")
                        Slider(value: $volume, in: 0.05...1)
                    }
                    Stepper("Fade in over \(AudioFormat.duration(ramp, style: .short))", value: $ramp, in: 0...600, step: 15)
                    Toggle("Enabled", isOn: $enabled)
                }
                if let errorMessage { Section { Text(errorMessage).foregroundStyle(SentinelTheme.red) } }
            }
            .formStyle(.grouped)
            .navigationTitle(alarm == nil ? "New Alarm" : "Edit Alarm")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button(saving ? "Saving…" : "Save") { Task { await save() } }
                        .disabled(saving || speakerId.isEmpty || needsSource)
                }
            }
            .onAppear(perform: seed)
            .task {
                let client = AppModel.shared.client
                async let p = client.musicPlaylists()
                async let c = client.podcasts()
                async let h = client.musicHome()
                playlists = (try? await p) ?? []
                podcasts = (try? await c) ?? []
                mixes = (try? await h)?.mixes ?? []
            }
        }
        #if os(macOS)
        .frame(minWidth: 520, minHeight: 640)
        #endif
    }

    private var needsSource: Bool { !["briefing", "shuffle"].contains(sourceType) && sourceId == nil }

    @ViewBuilder
    private var sourcePicker: some View {
        switch sourceType {
        case "playlist":
            Picker("Playlist", selection: sourceBinding) {
                Text("Choose…").tag(String?.none)
                ForEach(playlists) { Text($0.name).tag(String?.some($0.id)) }
            }
        case "mix":
            Picker("Mix", selection: sourceBinding) {
                Text("Choose…").tag(String?.none)
                ForEach(mixes) { Text($0.name).tag(String?.some($0.id)) }
            }
        case "podcast":
            Picker("Show", selection: sourceBinding) {
                Text("Choose…").tag(String?.none)
                ForEach(podcasts) { Text($0.title).tag(String?.some($0.id)) }
            }
        case "album", "artist":
            if let sourceName { LabeledContent(sourceType == "album" ? "Album" : "Artist", value: sourceName) }
            TextField("", text: $query, prompt: Text(sourceType == "album" ? "Search albums" : "Search artists"))
                .labelsHidden()
                .multilineTextAlignment(.leading)
                .autocorrectionDisabled()
                .task(id: query) {
                    guard query.count >= 2 else { found = nil; return }
                    try? await Task.sleep(for: .milliseconds(300))
                    found = try? await AppModel.shared.client.musicSearch(query)
                }
            if sourceType == "album" {
                ForEach(found?.albums ?? []) { album in
                    Button { sourceId = album.id; sourceName = album.title; query = "" } label: {
                        HStack {
                            ArtworkImage(path: album.artUrl, size: 36, corner: 4)
                            Text("\(album.title) · \(album.artist ?? "")").foregroundStyle(.primary)
                        }
                    }
                    .buttonStyle(.plain)
                }
            } else {
                ForEach(found?.artists ?? []) { artist in
                    Button { sourceId = artist.id; sourceName = artist.name; query = "" } label: {
                        HStack {
                            ArtworkImage(path: artist.artUrl, size: 36, placeholder: "music.mic", circle: true)
                            Text(artist.name).foregroundStyle(.primary)
                        }
                    }
                    .buttonStyle(.plain)
                }
            }
        case "briefing":
            Text("Today's weather, calendar and reminders read out, then the shows you've added to the briefing.")
                .font(.footnote).foregroundStyle(.secondary)
        default:
            EmptyView()
        }
    }

    private var sourceBinding: Binding<String?> {
        Binding(get: { sourceId }, set: { id in
            sourceId = id
            sourceName = playlists.first { $0.id == id }?.name ?? mixes.first { $0.id == id }?.name ?? podcasts.first { $0.id == id }?.title
        })
    }

    private func seed() {
        guard !seeded else { return }
        defer { seeded = true }
        guard let alarm else {
            speakerId = speakers.first { $0.online == true }?.deviceId ?? ""
            var components = DateComponents()
            components.hour = 6
            components.minute = 45
            time = Calendar.current.date(from: components) ?? Date()
            return
        }
        name = alarm.name
        let parts = alarm.time.split(separator: ":").compactMap { Int($0) }
        var components = DateComponents()
        components.hour = parts.first
        components.minute = parts.dropFirst().first
        time = Calendar.current.date(from: components) ?? Date()
        days = Set(alarm.days)
        speakerId = alarm.speakerId
        sourceType = alarm.source.type
        sourceId = alarm.source.id
        sourceName = alarm.source.name
        volume = alarm.volume
        ramp = Double(alarm.rampSeconds)
        enabled = alarm.enabled
    }

    private func save() async {
        saving = true
        defer { saving = false }
        let components = Calendar.current.dateComponents([.hour, .minute], from: time)
        let body = WakeAlarm(
            id: alarm?.id, name: name.trimmingCharacters(in: .whitespaces),
            time: String(format: "%02d:%02d", components.hour ?? 0, components.minute ?? 0),
            days: weekdays.map(\.id).filter { days.contains($0) }, speakerId: speakerId,
            source: WakeAlarmSource(type: sourceType, id: sourceId, name: sourceName ?? (sourceType == "briefing" ? "Morning Briefing" : sourceType == "shuffle" ? "Shuffle All" : nil)),
            volume: (volume * 100).rounded() / 100, rampSeconds: Int(ramp), enabled: enabled,
            nextFireAt: nil, lastFiredAt: nil)
        do {
            _ = try await AppModel.shared.client.saveWakeAlarm(body)
            onSaved()
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}
