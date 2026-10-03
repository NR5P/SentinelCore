import SwiftUI

// MARK: - Settings → Music & Podcasts (this device, and the links into the server parts)

struct AudioSettingsSection: View {
    @EnvironmentObject private var model: AppModel
    @State private var speakerName = UserDefaults.standard.string(forKey: "audio.speakerName") ?? ""
    @State private var soundCheck = AudioPlayer.shared.soundCheck
    @State private var dataSaver = AudioPlayer.shared.dataSaver
    @State private var podcastSpeed = AudioPlayer.shared.defaultPodcastSpeed

    var body: some View {
        let host = SpeakerHost.shared
        Section {
            Toggle("Let Others Play Here", isOn: Binding(get: { host.enabled }, set: { host.enabled = $0 }))
            if host.enabled {
                Toggle("Allow Announcements", isOn: Binding(get: { host.allowAnnouncements }, set: { host.allowAnnouncements = $0 }))
                SentinelTextField(label: "Speaker name", prompt: AudioDevice.name, text: $speakerName)
                    .onSubmit { UserDefaults.standard.set(speakerName, forKey: "audio.speakerName") }
                    .onChange(of: speakerName) { _, value in UserDefaults.standard.set(value, forKey: "audio.speakerName") }
                if let problem = host.problem {
                    Text(problem).font(.caption).foregroundStyle(SentinelTheme.red)
                } else if let last = host.lastCommand {
                    Text("Last command: \(last)").font(.caption).foregroundStyle(.secondary)
                }
            }
            Toggle("Sound Check", isOn: $soundCheck)
                .onChange(of: soundCheck) { _, value in AudioPlayer.shared.soundCheck = value }
            Toggle("Data Saver", isOn: $dataSaver)
                .onChange(of: dataSaver) { _, value in AudioPlayer.shared.dataSaver = value }
            Picker("Podcast Speed", selection: $podcastSpeed) {
                ForEach([0.8, 0.9, 1.0, 1.1, 1.2, 1.25, 1.5, 1.75, 2.0], id: \.self) { Text(speedLabel($0)).tag($0) }
            }
            .onChange(of: podcastSpeed) { _, value in AudioPlayer.shared.defaultPodcastSpeed = value }
            NavigationLink {
                AudioDownloadsView()
            } label: {
                LabeledContent("Downloads on This Device", value: AudioFormat.bytes(AudioDownloads.shared.totalBytes))
            }
            NavigationLink("Wake-Up Alarms") { WakeAlarmsView() }
            NavigationLink("ListenBrainz") { ListenBrainzView() }
        } header: {
            Text("Music & Podcasts")
        } footer: {
            Text("Let Others Play Here makes this device a speaker the rest of the house - and wake-up alarms - can play to; keep the app open. Sound Check evens out loudness between songs. Data Saver streams music at 128 kbps. Each show can have its own speed; this is the speed for the rest.")
        }
        if model.isAdmin {
            Section {
                if model.musicEnabled {
                    NavigationLink("Music Libraries") { MusicLibrariesAdminView() }
                }
                if model.podcastsEnabled {
                    NavigationLink("Podcast Storage") { PodcastStorageView() }
                }
            } footer: {
                Text("The folders Music is built from, and where the server keeps downloaded episodes. Everything else is in Server Settings → Music and → Podcasts.")
            }
        }
    }
}

// MARK: - Server Settings → MUSIC (the hand-built part under the schema's fields)

struct MusicServerSettingsPanel: View {
    @State private var status: MusicStatus?

    var body: some View {
        Section {
            NavigationLink {
                MusicLibrariesAdminView()
            } label: {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Libraries")
                    if let status {
                        Text("\(status.libraries?.count ?? 0) folder\(status.libraries?.count == 1 ? "" : "s") · \((status.trackCount ?? 0).formatted()) songs\(status.scanning == true ? " · scanning…" : "")")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            if status?.ffmpegAvailable == false {
                Label("ffmpeg isn't installed on the server, so files can't be converted for this device.", systemImage: "exclamationmark.triangle")
                    .font(.caption).foregroundStyle(SentinelTheme.amber)
            }
        } header: {
            Text("Libraries")
        }
        .task { status = try? await AppModel.shared.client.musicStatus() }
    }
}

// MARK: - Server Settings → PODCASTS (download folder Choose…, storage, OPML)

struct PodcastServerSettingsPanel: View {
    @Binding var downloadFolder: String
    @State private var status: PodcastStatus?

    var body: some View {
        Section {
            NavigationLink {
                MediaFolderPickerView(path: $downloadFolder, scope: .audio)
            } label: {
                Label("Choose Download Folder…", systemImage: "folder")
            }
            if let status {
                PodcastStorageLine(status: status)
            }
            NavigationLink("Import or Export OPML") { PodcastLibraryView() }
        } header: {
            Text("Download Folder")
        } footer: {
            Text("Choosing a folder fills in Download folder above; press Save to keep it. Blank keeps episodes in the server's data folder.")
        }
        .task { status = try? await AppModel.shared.client.podcastStatus() }
    }
}

struct PodcastStorageLine: View {
    let status: PodcastStatus

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            let used = status.storageBytes ?? 0
            let max = status.maxStorageBytes ?? 0
            // The server's limit is whole GiB (Gb × 1024³), so both figures use the binary
            // formatter - .file showed a 50 GB limit as "53.7 GB".
            LabeledContent("Storage", value: max > 0 ? "\(AudioFormat.binaryBytes(used)) of \(AudioFormat.binaryBytes(max))" : AudioFormat.binaryBytes(used))
            if max > 0 { ProgressView(value: min(1, Double(used) / Double(max))) }
            if let folder = status.downloadFolder {
                Text(folder).font(.caption.monospaced()).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
            }
            Text("\(status.podcastCount ?? 0) shows · \((status.episodeCount ?? 0).formatted()) episodes · \(status.downloadsActive ?? 0) downloading, \(status.downloadsQueued ?? 0) waiting")
                .font(.caption).foregroundStyle(.secondary)
            if let transcripts = status.transcripts, transcripts.enabled == true {
                Text(transcripts.available == true
                     ? "Transcripts: ready\((transcripts.queued ?? 0) > 0 ? " · \(transcripts.queued!) waiting" : "")"
                     : "Transcripts: \(transcripts.error ?? "not available")")
                    .font(.caption).foregroundStyle(transcripts.available == true ? Color.secondary : SentinelTheme.amber)
            }
        }
    }
}

struct PodcastStorageView: View {
    @State private var status: PodcastStatus?
    @State private var error: String?

    var body: some View {
        Form {
            if let status {
                Section { PodcastStorageLine(status: status) }
            }
            Section {
                NavigationLink("Server Settings → Podcasts") { ServerSettingsSectionByKeyView(key: "podcasts") }
            } footer: {
                Text("The download folder, how many episodes to keep, the storage limit and transcription are all in Server Settings → Podcasts.")
            }
            if let error { Text(error).foregroundStyle(SentinelTheme.red) }
        }
        .formStyle(.grouped)
        .navigationTitle("Podcast Storage")
        .task {
            do { status = try await AppModel.shared.client.podcastStatus() }
            catch { self.error = error.localizedDescription }
        }
    }
}

// MARK: - Music libraries (admin) - §2

struct MusicLibrariesAdminView: View {
    @State private var libraries: [MusicLibrary] = []
    @State private var status: MusicStatus?
    @State private var loaded = false
    @State private var errorMessage: String?
    @State private var notice: String?
    @State private var refreshToken = 0

    var body: some View {
        Form {
            if let errorMessage, !loaded {
                Section { Text(errorMessage).foregroundStyle(SentinelTheme.red) }
            }
            if status?.scanning == true, let scan = status?.scan {
                Section("Scanning") {
                    VStack(alignment: .leading, spacing: 6) {
                        Text(scanLabel(scan))
                        if let total = scan.total, total > 0 {
                            ProgressView(value: Double(scan.processed ?? 0), total: Double(total))
                        } else {
                            ProgressView()
                        }
                    }
                }
            }
            Section {
                if !loaded && errorMessage == nil { Text("Loading…").foregroundStyle(.secondary) }
                if loaded && libraries.isEmpty {
                    Text("No music libraries yet. Add the folder your music is in - on the server, or shared from another computer.")
                        .foregroundStyle(.secondary)
                }
                ForEach(libraries) { library in
                    NavigationLink {
                        MusicLibraryEditView(existing: library) { refreshToken += 1 }
                    } label: {
                        VStack(alignment: .leading, spacing: 3) {
                            Label(library.name, systemImage: library.network == true ? "externaldrive.connected.to.line.below" : "music.note.house")
                            Text(library.path ?? "").font(.caption.monospaced()).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                            Text(summary(library)).font(.caption)
                                .foregroundStyle(library.lastScanError == nil ? Color.secondary : SentinelTheme.red).lineLimit(3)
                        }
                    }
                }
            } header: {
                Text("Libraries")
            } footer: {
                if let status, loaded {
                    Text("\((status.trackCount ?? 0).formatted()) songs, \((status.albumCount ?? 0).formatted()) albums, \((status.artistCount ?? 0).formatted()) artists\(status.totalBytes.map { " · \(AudioFormat.bytes($0))" } ?? ""). Each library is rescanned every few hours; loves, ratings and plays are kept per person.")
                }
            }
            if loaded {
                Section {
                    NavigationLink {
                        MusicLibraryEditView(existing: nil) { refreshToken += 1 }
                    } label: {
                        Label("Add Library", systemImage: "plus")
                    }
                    if !libraries.isEmpty {
                        Button {
                            Task {
                                do {
                                    try await AppModel.shared.client.scanMusic(libraryId: nil)
                                    notice = "Scanning every library."
                                } catch { notice = error.localizedDescription }
                                await load()
                            }
                        } label: {
                            Label("Scan All Now", systemImage: "arrow.clockwise")
                        }
                        .disabled(status?.scanning == true)
                    }
                    if let notice { Text(notice).font(.footnote).foregroundStyle(.secondary) }
                }
            }
        }
        .formStyle(.grouped)
        .navigationTitle("Music Libraries")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        .refreshable { await load() }
        #endif
        .task(id: refreshToken) { await load() }
        .task(id: status?.scanning == true) {
            guard status?.scanning == true else { return }
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(3))
                if Task.isCancelled { return }
                await load()
                if status?.scanning != true { return }
            }
        }
    }

    private func scanLabel(_ scan: MusicScan) -> String {
        let what: String
        switch scan.phase {
        case "tags": what = "Reading tags"
        case "artwork": what = "Finding album art"
        case "lyrics": what = "Finding lyrics"
        default: what = "Looking for music"
        }
        if let total = scan.total, total > 0 {
            return "\(what)\(scan.libraryName.map { " in \($0)" } ?? ""): \(scan.processed ?? 0) of \(total)"
        }
        return "\(what)\(scan.libraryName.map { " in \($0)" } ?? "")…"
    }

    private func summary(_ library: MusicLibrary) -> String {
        if let error = library.lastScanError { return error }
        let count = library.trackCount ?? 0
        let songs = "\(count.formatted()) song\(count == 1 ? "" : "s")"
        guard let when = AudioDate.parse(library.lastScanFinishedAt) else { return "\(songs) · not scanned yet" }
        return "\(songs) · scanned \(when.formatted(.relative(presentation: .named)))"
    }

    private func load() async {
        do {
            let status = try await AppModel.shared.client.musicStatus()
            self.status = status
            if status.enabled == false {
                errorMessage = "Music is turned off. Turn it on in Server Settings → Music."
                loaded = false
                return
            }
            libraries = try await AppModel.shared.client.musicLibraries()
            loaded = true
            errorMessage = nil
        } catch {
            if !error.isCancellation { errorMessage = error.localizedDescription }
        }
    }
}

struct MusicLibraryEditView: View {
    let existing: MusicLibrary?
    var onChange: () -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var name: String
    @State private var path: String
    @State private var saving = false
    @State private var errorMessage: String?
    @State private var notice: String?
    @State private var confirmingRemove = false

    init(existing: MusicLibrary?, onChange: @escaping () -> Void) {
        self.existing = existing
        self.onChange = onChange
        _name = State(initialValue: existing?.name ?? "Music")
        _path = State(initialValue: existing?.path ?? "")
    }

    var body: some View {
        Form {
            Section {
                SentinelTextField(label: "Name", prompt: "Music", text: $name)
            }
            Section {
                SentinelTextField(label: "Folder", prompt: "/mnt/music or smb://PC/Music", text: $path)
                    .autocorrectionDisabled()
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    #endif
                // A NavigationLink handed a binding - never .navigationDestination on a pushed
                // screen, which hangs the Mac app (see MediaLibrariesAdminView).
                NavigationLink {
                    MediaFolderPickerView(path: $path, scope: .audio)
                } label: {
                    Label("Choose Folder…", systemImage: "folder")
                }
            } footer: {
                Text("A folder on the server, or one shared from another computer on your network - the server reads it over the network itself. Computers you've signed in to for Movies work here too.")
            }
            Section {
                Button {
                    Task { await save() }
                } label: {
                    HStack {
                        Text(existing == nil ? "Add Library" : "Save")
                        if saving { Spacer(); ProgressView() }
                    }
                }
                .disabled(name.trimmingCharacters(in: .whitespaces).isEmpty || path.trimmingCharacters(in: .whitespaces).isEmpty || saving)
                if let errorMessage { Text(errorMessage).foregroundStyle(SentinelTheme.red) }
            } footer: {
                Text(existing == nil ? "Adding a library starts a scan of it straight away." : "Changing the folder rescans the library.")
            }
            if let existing {
                Section {
                    Button {
                        Task {
                            do {
                                try await AppModel.shared.client.scanMusic(libraryId: existing.id)
                                notice = "Scanning \(existing.name) now."
                                onChange()
                            } catch { notice = error.localizedDescription }
                        }
                    } label: {
                        Label("Scan Now", systemImage: "arrow.clockwise")
                    }
                    if let notice { Text(notice).font(.footnote).foregroundStyle(.secondary) }
                    Button(role: .destructive) { confirmingRemove = true } label: {
                        Label("Remove Library", systemImage: "trash")
                    }
                } footer: {
                    Text("Removing a library forgets its songs. Everyone's plays, loves and playlists are kept in case the folder comes back. The music files are not touched.")
                }
            }
        }
        .formStyle(.grouped)
        .navigationTitle(existing?.name ?? "Add Library")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .confirmationDialog("Remove \(existing?.name ?? "this library")?", isPresented: $confirmingRemove, titleVisibility: .visible) {
            Button("Remove Library", role: .destructive) {
                Task {
                    guard let existing else { return }
                    do {
                        try await AppModel.shared.client.deleteMusicLibrary(id: existing.id)
                        onChange()
                        dismiss()
                    } catch { errorMessage = error.localizedDescription }
                }
            }
        }
    }

    private func save() async {
        saving = true
        errorMessage = nil
        defer { saving = false }
        do {
            _ = try await AppModel.shared.client.saveMusicLibrary(
                id: existing?.id,
                MusicLibraryInput(name: name.trimmingCharacters(in: .whitespaces), path: path.trimmingCharacters(in: .whitespaces)))
            onChange()
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

// MARK: - ListenBrainz (§9)

struct ListenBrainzView: View {
    @State private var status: ListenBrainzStatus?
    @State private var token = ""
    @State private var saving = false
    @State private var errorMessage: String?

    var body: some View {
        Form {
            Section {
                if let status {
                    LabeledContent("Status", value: status.connected == true ? "Connected as \(status.username ?? "you")" : "Not connected")
                } else {
                    ProgressView()
                }
            } footer: {
                Text("ListenBrainz keeps a public record of what you listen to. Every song that counts as a play here is sent to it.")
            }
            Section {
                SentinelTextField(label: "User token", prompt: "Paste from listenbrainz.org/settings", text: $token, secure: true)
                Button(saving ? "Checking…" : "Connect") { Task { await save(token) } }
                    .disabled(token.trimmingCharacters(in: .whitespaces).isEmpty || saving)
                if status?.connected == true {
                    Button("Disconnect", role: .destructive) { Task { await save("") } }
                }
                if let errorMessage { Text(errorMessage).foregroundStyle(SentinelTheme.red) }
                if let url = URL(string: "https://listenbrainz.org/settings/") {
                    Link("Get Your Token", destination: url)
                }
            }
        }
        .formStyle(.grouped)
        .navigationTitle("ListenBrainz")
        .task { status = try? await AppModel.shared.client.listenBrainz() }
    }

    private func save(_ value: String) async {
        saving = true
        defer { saving = false }
        do {
            status = try await AppModel.shared.client.setListenBrainz(token: value.trimmingCharacters(in: .whitespaces))
            token = ""
            errorMessage = nil
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}
