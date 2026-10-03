import SwiftUI

// Settings → Music & Podcasts on the TV: this Apple TV as a speaker ("Let others play here"),
// its own playback preferences, and - for an admin - the server's MUSIC and PODCASTS sections
// (drawn from /api/settings/schema), Music → Libraries and Podcasts → Download folder.
// Lists, not Forms: a tvOS Form draws button titles in the green tint.

struct AudioSettingsGroup: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var audio: AudioCenter

    var body: some View {
        Section {
            if audio.musicEnabled || audio.podcastsEnabled {
                Toggle("Let Others Play Here", isOn: Binding(get: { audio.speakerEnabled }, set: { audio.setSpeakerEnabled($0) }))
                NavigationLink {
                    AudioPlaybackSettingsTV()
                } label: {
                    LabeledContent("Speaker & Playback", value: AudioPrefs.speakerName)
                }
            }
            if model.isAdmin {
                NavigationLink("Music") { MusicSettingsTV() }
                NavigationLink("Podcasts") { PodcastSettingsTV() }
            }
        } header: {
            Text("Music & Podcasts")
        } footer: {
            if audio.musicEnabled || audio.podcastsEnabled {
                Text(audio.speakerEnabled
                     ? "Phones can play to this Apple TV, and wake-up alarms and announcements can use it\(audio.speakerOnline ? "" : " (connecting…)")."
                     : "This Apple TV isn't offered as a speaker.")
            }
        }
    }
}

// MARK: - This TV: speaker name, announcements, Sound Check, default podcast speed

struct AudioPlaybackSettingsTV: View {
    @EnvironmentObject private var audio: AudioCenter
    @State private var name = AudioPrefs.speakerName
    @State private var announcements = AudioPrefs.allowAnnouncements
    @State private var soundCheck = AudioPrefs.soundCheck
    @State private var podcastSpeed = AudioPrefs.podcastSpeed

    var body: some View {
        List {
            Section {
                Toggle("Let Others Play Here", isOn: Binding(get: { audio.speakerEnabled }, set: { audio.setSpeakerEnabled($0) }))
                TextField("Speaker name", text: $name)
                    .onSubmit {
                        AudioPrefs.speakerName = name
                        name = AudioPrefs.speakerName
                        audio.speakerSettingsChanged()
                    }
                Toggle("Allow Announcements", isOn: $announcements)
                    .onChange(of: announcements) { _, on in
                        AudioPrefs.allowAnnouncements = on
                        audio.speakerSettingsChanged()
                    }
            } header: {
                Text("Speaker")
            } footer: {
                Text("Announcements are spoken over whatever is playing (it dips while the voice speaks): someone at the door, the home alarm, a briefing at wake-up.")
            }
            Section {
                Toggle("Sound Check", isOn: $soundCheck)
                    .onChange(of: soundCheck) { _, on in
                        AudioPrefs.soundCheck = on
                        audio.player.applyVolume()
                    }
                Menu {
                    ForEach([0.8, 1.0, 1.1, 1.2, 1.25, 1.5, 1.75, 2.0], id: \.self) { speed in
                        Button(AudioFormat.speed(speed)) {
                            podcastSpeed = speed
                            AudioPrefs.podcastSpeed = speed
                        }
                    }
                } label: {
                    LabeledContent("Podcast Speed", value: AudioFormat.speed(podcastSpeed)).focusContrastForeground()
                }
            } header: {
                Text("Playback")
            } footer: {
                Text("Sound Check evens out loudness between songs using their ReplayGain tags. The podcast speed applies to shows without their own speed.")
            }
        }
        .navigationTitle("Speaker & Playback")
    }
}

// MARK: - Music (admin)

struct MusicSettingsTV: View {
    @EnvironmentObject private var audio: AudioCenter

    var body: some View {
        List {
            SchemaSectionEditor(key: "music") { audio.objectWillChange.send(); Task { await audio.refreshEnabled() } }
            MusicLibrariesSection()
        }
        .navigationTitle("Music")
    }
}

private struct MusicLibrariesSection: View {
    @EnvironmentObject private var audio: AudioCenter
    @State private var status: MusicStatus?
    @State private var libraries: [MusicLibrary] = []
    @State private var errorMessage: String?
    @State private var notice: String?
    @State private var refresh = 0

    var body: some View {
        Section {
            if let scan = status?.scan, status?.scanning == true {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Scanning \(scan.libraryName ?? "")… \(phaseLabel(scan.phase))")
                    if let total = scan.total, total > 0 {
                        ProgressView(value: Double(scan.processed ?? 0), total: Double(total))
                    } else {
                        ProgressView()
                    }
                }
            }
            if let errorMessage { Text(errorMessage).foregroundStyle(SentinelTheme.red) }
            ForEach(libraries) { library in
                NavigationLink {
                    MusicLibraryEditTV(existing: library) { refresh += 1 }
                } label: {
                    VStack(alignment: .leading, spacing: 4) {
                        Label(library.name, systemImage: library.network == true ? "externaldrive.connected.to.line.below" : "music.note.house")
                        Text(library.path ?? "").font(.caption.monospaced()).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                        Text(library.lastScanError ?? "\(library.trackCount ?? 0) songs · \(AudioDate.relative(library.lastScanFinishedAt).map { "scanned \($0)" } ?? "not scanned yet")")
                            .font(.caption).foregroundStyle(library.lastScanError == nil ? Color.secondary : SentinelTheme.red)
                    }
                }
            }
            NavigationLink {
                MusicLibraryEditTV(existing: nil) { refresh += 1 }
            } label: {
                Label("Add Library", systemImage: "plus")
            }
            if !libraries.isEmpty {
                Button {
                    Task {
                        do { try await audio.api?.scanMusic(libraryId: nil); notice = "Scanning every library." }
                        catch { notice = error.localizedDescription }
                        refresh += 1
                    }
                } label: {
                    Label("Scan All Now", systemImage: "arrow.clockwise")
                }
                .disabled(status?.scanning == true)
            }
            if let notice { Text(notice).font(.caption).foregroundStyle(.secondary) }
        } header: {
            Text("Libraries")
        } footer: {
            if let status, status.enabled == true {
                Text("\(status.trackCount ?? 0) songs · \(status.albumCount ?? 0) albums · \(status.artistCount ?? 0) artists\(AudioFormat.bytes(status.totalBytes).map { " · \($0)" } ?? "")\(status.ffmpegAvailable == false ? " · ffmpeg is missing, so files can't be converted" : "")")
            } else {
                Text("Turn Music on above, then add the folders your music is in - on the server or shared from another computer.")
            }
        }
        .task(id: refresh) { await load() }
        .task(id: status?.scanning == true) {
            guard status?.scanning == true else { return }
            while !Task.isCancelled, status?.scanning == true {
                try? await Task.sleep(for: .seconds(3))
                await load()
            }
        }
    }

    private func phaseLabel(_ phase: String?) -> String {
        switch phase {
        case "tags": "reading tags"
        case "artwork": "finding artwork"
        case "lyrics": "finding lyrics"
        default: "looking for files"
        }
    }

    private func load() async {
        guard let api = audio.api else { return }
        do {
            status = try await api.musicStatus()
            libraries = try await api.musicLibraries()
            errorMessage = nil
        } catch {
            if !AudioAPI.isCancellation(error) { errorMessage = error.localizedDescription }
        }
    }
}

private struct MusicLibraryEditTV: View {
    let existing: MusicLibrary?
    let onChange: () -> Void
    @EnvironmentObject private var audio: AudioCenter
    @Environment(\.dismiss) private var dismiss
    @State private var name: String
    @State private var path: String
    @State private var saving = false
    @State private var errorMessage: String?
    @State private var confirmingRemove = false

    init(existing: MusicLibrary?, onChange: @escaping () -> Void) {
        self.existing = existing
        self.onChange = onChange
        _name = State(initialValue: existing?.name ?? "Music")
        _path = State(initialValue: existing?.path ?? "")
    }

    var body: some View {
        List {
            Section {
                TextField("Name", text: $name)
                TextField("Folder", text: $path, prompt: Text("/mnt/music or smb://PC/Music"))
                    .font(.body.monospaced())
                    .autocorrectionDisabled()
                    .textInputAutocapitalization(.never)
                NavigationLink {
                    AudioFolderPickerTV(path: $path, noun: "music")
                } label: {
                    Label("Choose Folder…", systemImage: "folder")
                }
            } footer: {
                Text("A folder on the server, or one shared from another computer - the server reads it over the network, nothing to mount.")
            }
            Section {
                Button {
                    Task { await save() }
                } label: {
                    HStack { Text(existing == nil ? "Add Library" : "Save"); if saving { Spacer(); ProgressView() } }
                }
                .disabled(name.trimmingCharacters(in: .whitespaces).isEmpty || path.trimmingCharacters(in: .whitespaces).isEmpty || saving)
                if let errorMessage { Text(errorMessage).foregroundStyle(SentinelTheme.red) }
            } footer: {
                Text(existing == nil ? "Adding a library starts a scan straight away." : "Changing the folder rescans it. Plays, loves and playlists are kept.")
            }
            if let existing {
                Section {
                    Button {
                        Task {
                            try? await audio.api?.scanMusic(libraryId: existing.id)
                            onChange()
                        }
                    } label: { Label("Scan Now", systemImage: "arrow.clockwise") }
                    Button(role: .destructive) { confirmingRemove = true } label: { Label("Remove Library", systemImage: "trash") }
                } footer: {
                    Text("Removing a library forgets its songs. The files aren't touched, and history, playlists and loves are kept in case the folder comes back.")
                }
            }
        }
        .navigationTitle(existing?.name ?? "Add Library")
        .confirmationDialog("Remove \(existing?.name ?? "this library")?", isPresented: $confirmingRemove, titleVisibility: .visible) {
            Button("Remove Library", role: .destructive) {
                Task {
                    if let id = existing?.id {
                        do { try await audio.api?.deleteMusicLibrary(id: id); onChange(); dismiss() }
                        catch { errorMessage = error.localizedDescription }
                    }
                }
            }
        }
    }

    private func save() async {
        guard let api = audio.api else { return }
        saving = true
        defer { saving = false }
        do {
            _ = try await api.saveMusicLibrary(id: existing?.id, MusicLibraryInput(
                name: name.trimmingCharacters(in: .whitespacesAndNewlines), path: path.trimmingCharacters(in: .whitespacesAndNewlines)))
            onChange()
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

// MARK: - Podcasts (admin)

struct PodcastSettingsTV: View {
    @EnvironmentObject private var audio: AudioCenter
    @State private var status: PodcastsStatus?

    var body: some View {
        List {
            SchemaSectionEditor(key: "podcasts", folderField: "downloadFolder") {
                Task {
                    await audio.refreshEnabled()
                    status = try? await audio.api?.podcastsStatus()
                }
            }
            if let status, status.enabled == true {
                Section("Storage") {
                    LabeledContent("Download folder", value: status.downloadFolder ?? "Default")
                    LabeledContent("Used", value: [AudioFormat.bytes(status.storageBytes) ?? "0 bytes",
                                                    status.maxStorageBytes.flatMap { $0 > 0 ? "of \(AudioFormat.bytes($0) ?? "")" : nil }]
                        .compactMap { $0 }.joined(separator: " "))
                    LabeledContent("Shows", value: "\(status.podcastCount ?? 0) · \(status.episodeCount ?? 0) episodes")
                    if (status.downloadsActive ?? 0) + (status.downloadsQueued ?? 0) > 0 {
                        LabeledContent("Downloading", value: "\(status.downloadsActive ?? 0) now, \(status.downloadsQueued ?? 0) waiting")
                    }
                    if let transcripts = status.transcripts, transcripts.enabled == true {
                        LabeledContent("Transcripts", value: transcripts.available == true ? "Ready\(transcripts.queued.map { " · \($0) queued" } ?? "")" : (transcripts.error ?? "Not set up"))
                    }
                    Text("OPML import and export are in the dashboard and the phone apps.").font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        .navigationTitle("Podcasts")
        .task { status = try? await audio.api?.podcastsStatus() }
    }
}

// MARK: - A schema section (MUSIC / PODCASTS), drawn and saved

struct SchemaSectionEditor: View {
    let key: String
    var folderField: String?
    var onSaved: () -> Void = {}
    @EnvironmentObject private var audio: AudioCenter
    @State private var section: AudioSettingsSection?
    @State private var text: [String: String] = [:]
    @State private var flags: [String: Bool] = [:]
    @State private var loadError: String?
    @State private var saveMessage: String?
    @State private var saving = false
    @State private var folderPick = ""

    init(key: String, folderField: String? = nil, onSaved: @escaping () -> Void = {}) {
        self.key = key
        self.folderField = folderField
        self.onSaved = onSaved
    }

    var body: some View {
        Section {
            if let section {
                ForEach(section.fields) { field in fieldRow(field) }
                Button {
                    Task { await save() }
                } label: {
                    HStack { Label("Save", systemImage: "checkmark"); if saving { Spacer(); ProgressView() } }
                }
                .disabled(saving)
                if let saveMessage { Text(saveMessage).font(.caption).foregroundStyle(.secondary) }
            } else if let loadError {
                Text(loadError).foregroundStyle(SentinelTheme.red)
            } else {
                ProgressView()
            }
        } header: {
            Text(section?.title ?? key.uppercased())
        } footer: {
            if let description = section?.description, !description.isEmpty { Text(description) }
        }
        .task { await load() }
        .onChange(of: folderPick) { _, picked in
            if let folderField, !picked.isEmpty { text[folderField] = picked }
        }
    }

    @ViewBuilder
    private func fieldRow(_ field: AudioSettingsField) -> some View {
        switch field.type {
        case "checkbox":
            Toggle(field.label, isOn: Binding(get: { flags[field.name] ?? false }, set: { flags[field.name] = $0 }))
        case "select":
            Menu {
                ForEach(field.options ?? [], id: \.value) { option in
                    Button(option.label) { text[field.name] = option.value }
                }
            } label: {
                LabeledContent(field.label, value: field.options?.first { $0.value == text[field.name] }?.label ?? text[field.name] ?? "").focusContrastForeground()
            }
        case "password":
            SecureField(field.label, text: binding(field.name))
        case "number", "text":
            VStack(alignment: .leading, spacing: 4) {
                TextField(field.label, text: binding(field.name), prompt: Text(field.label))
                    .keyboardType(field.type == "number" ? .decimalPad : .default)
                    .autocorrectionDisabled()
                    .textInputAutocapitalization(.never)
                Text(fieldCaption(field)).font(.caption2).foregroundStyle(.secondary).lineLimit(2)
            }
            if field.name == folderField {
                NavigationLink {
                    AudioFolderPickerTV(path: $folderPick, noun: "podcast", startAt: text[field.name] ?? "")
                } label: {
                    Label("Choose Folder…", systemImage: "folder")
                }
            }
        default:
            LabeledContent(field.label, value: text[field.name] ?? "")
        }
    }

    private func fieldCaption(_ field: AudioSettingsField) -> String {
        var parts = [field.label]
        if let min = field.min, let max = field.max { parts.append("\(AudioSettingValue.number(min).text)-\(AudioSettingValue.number(max).text)") }
        if let tip = field.tip, !tip.isEmpty { parts.append(tip) }
        return parts.joined(separator: " · ")
    }

    private func binding(_ name: String) -> Binding<String> {
        Binding(get: { text[name] ?? "" }, set: { text[name] = $0 })
    }

    private func load() async {
        guard let api = audio.api else { return }
        do {
            async let schema = api.settingsSchema()
            async let snapshot = api.settingsSnapshot()
            let (sections, values) = try await (schema, snapshot)
            guard let found = sections.first(where: { $0.key == key }) else {
                loadError = "This server doesn't have \(key == "music" ? "Music" : "Podcasts") yet - update it first."
                return
            }
            let current = values[key]?.values ?? [:]
            for field in found.fields {
                let value = current[field.name]
                if field.type == "checkbox" { flags[field.name] = value?.bool ?? false } else { text[field.name] = value?.text ?? "" }
            }
            section = found
        } catch {
            if !AudioAPI.isCancellation(error) { loadError = error.localizedDescription }
        }
    }

    private func save() async {
        guard let api = audio.api, let section else { return }
        saving = true
        defer { saving = false }
        var payload: [String: AudioSettingValue] = [:]
        for field in section.fields {
            switch field.type {
            case "checkbox":
                payload[field.name] = .bool(flags[field.name] ?? false)
            case "number":
                let raw = (text[field.name] ?? "").trimmingCharacters(in: .whitespaces)
                guard let number = Double(raw) else {
                    saveMessage = "\(field.label) must be a number."
                    return
                }
                payload[field.name] = .number(number)
            case "text", "password", "select":
                payload[field.name] = .string(text[field.name] ?? "")
            default:
                continue
            }
        }
        do {
            try await api.saveSettings(endpoint: section.endpoint, values: payload)
            saveMessage = "Saved."
            onSaved()
        } catch {
            saveMessage = error.localizedDescription
        }
    }
}

// MARK: - Folder picker over /api/audio/browse (same shape as the Movies picker)

struct AudioFolderPickerTV: View {
    @Binding var path: String
    let noun: String
    var startAt: String = ""
    @EnvironmentObject private var audio: AudioCenter
    @Environment(\.dismiss) private var dismiss
    @State private var listing: MediaBrowseListing?
    @State private var loading = false
    @State private var errorMessage: String?
    @State private var openAfterSignIn: String?
    @State private var started = false
    @State private var newFolderName = ""
    @State private var newFolderError: String?

    var body: some View {
        List {
            if let listing {
                Section {
                    Text(listing.path.isEmpty ? "Start" : listing.path).font(.caption.monospaced()).foregroundStyle(.secondary)
                    if !listing.isRoots {
                        Button { Task { await load(listing.parent ?? "") } } label: { Label("Up", systemImage: "arrow.up") }
                    }
                    Button {
                        path = listing.path
                        dismiss()
                    } label: {
                        Label("Use This Folder", systemImage: "checkmark.circle")
                    }
                    .disabled(!listing.isPickable)
                } footer: {
                    if let count = listing.videoCount, count > 0 { Text("\(count) audio file\(count == 1 ? "" : "s") directly in this folder.") }
                    else if listing.isRoots { Text("Open the folder your \(noun) is in.") }
                }
                if let error = listing.error {
                    Section {
                        Text(error).foregroundStyle(SentinelTheme.red)
                        if listing.needsLogin == true, let host = listing.host {
                            NavigationLink {
                                MediaNetworkSignInView(host: host, computer: listing.computers?.first { $0.host == host }, openAfter: $openAfterSignIn)
                            } label: {
                                Label("Sign In to \(host)", systemImage: "person.badge.key")
                            }
                        }
                    }
                }
                Section(listing.isRoots ? "On This Server" : (listing.kind == "computer" ? "Shared Folders" : "Folders")) {
                    if listing.folders.isEmpty { Text("No folders here.").foregroundStyle(.secondary) }
                    ForEach(listing.folders) { folder in
                        Button { Task { await load(folder.path) } } label: {
                            HStack {
                                Label(folder.name, systemImage: "folder").lineLimit(1)
                                Spacer()
                                if let count = folder.videoCount, count > 0 { Text("\(count) files").font(.caption).foregroundStyle(.secondary) }
                            }
                        }
                    }
                }
                if listing.canMakeFolder {
                    // The library's folder may not exist yet: made here, then opened.
                    Section {
                        TextField("Name", text: $newFolderName, prompt: Text("Podcasts"))
                            .autocorrectionDisabled()
                            .onSubmit { Task { await makeFolder(in: listing.path) } }
                        Button { Task { await makeFolder(in: listing.path) } } label: {
                            Label("Create Folder Here", systemImage: "folder.badge.plus")
                        }
                        .disabled(newFolderName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                        if let newFolderError { Text(newFolderError).foregroundStyle(SentinelTheme.red) }
                    } header: {
                        Text("New Folder")
                    }
                }
                if listing.isRoots {
                    Section {
                        ForEach(listing.computers ?? []) { computer in
                            Button { Task { await load(computer.path) } } label: { Label(computer.name, systemImage: "desktopcomputer") }
                        }
                        NavigationLink {
                            MediaNetworkSignInView(host: "", computer: nil, openAfter: $openAfterSignIn)
                        } label: {
                            Label("Add a Computer…", systemImage: "plus")
                        }
                    } header: {
                        Text("Network Computers")
                    } footer: {
                        Text("The same sign-ins Movies uses.")
                    }
                }
            } else if let errorMessage {
                Text(errorMessage).foregroundStyle(SentinelTheme.red)
            } else {
                ProgressView()
            }
        }
        .navigationTitle("Choose a Folder")
        .onChange(of: openAfterSignIn) { _, next in
            guard let next else { return }
            openAfterSignIn = nil
            Task { await load(next) }
        }
        .task {
            guard !started else { return }
            started = true
            await load(startAt.isEmpty ? path : startAt, fallBack: true)
        }
    }

    private func load(_ target: String, fallBack: Bool = false) async {
        guard let api = audio.api else { return }
        loading = true
        defer { loading = false }
        do {
            let page = try await api.audioBrowse(path: target)
            if fallBack, !target.isEmpty, page.error != nil, page.needsLogin != true {
                await load("")
                return
            }
            listing = page
            errorMessage = nil
            newFolderError = nil
        } catch {
            if fallBack, !target.isEmpty { await load(""); return }
            errorMessage = error.localizedDescription
        }
    }

    private func makeFolder(in parent: String) async {
        let name = newFolderName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let api = audio.api, !name.isEmpty else { return }
        do {
            listing = try await api.audioMakeFolder(parent: parent, name: name)
            newFolderName = ""
            newFolderError = nil
        } catch {
            newFolderError = error.localizedDescription
        }
    }
}
