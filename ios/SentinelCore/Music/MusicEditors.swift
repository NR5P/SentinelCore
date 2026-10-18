import SwiftUI

// MARK: - Playlist editor (regular and smart)

struct PlaylistEditorView: View {
    let existing: MusicPlaylist?
    var initialTrackIds: [String] = []
    var startSmart = false
    let onSaved: (MusicPlaylist) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var name = ""
    @State private var description = ""
    @State private var shared = false
    @State private var collaborative = false
    @State private var smart = false
    @State private var rules = SmartRules(match: "all", rules: [SmartRule(field: "genre", op: "is", value: .string(""))])
    @State private var libraries: [MusicLibrary] = []
    @State private var saving = false
    @State private var errorMessage: String?
    @State private var seeded = false

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("", text: $name, prompt: Text("Playlist name"))
                        .labelsHidden()
                        .multilineTextAlignment(.leading)
                        .accessibilityLabel("Name")
                    TextField("", text: $description, prompt: Text("Description (optional)"), axis: .vertical)
                        .labelsHidden()
                        .lineLimit(2...5)
                        .multilineTextAlignment(.leading)
                        .accessibilityLabel("Description")
                }
                Section {
                    Toggle("Shared with the household", isOn: $shared)
                    Toggle("Collaborative", isOn: $collaborative)
                        .disabled(smart)
                        .onChange(of: collaborative) { _, on in if on { shared = true } }
                } footer: {
                    Text("Shared playlists can be seen and played by everyone. Collaborative ones can also be added to and reordered by everyone.")
                }
                if existing == nil || existing?.isSmart == true {
                    Section {
                        Toggle("Smart Playlist", isOn: $smart)
                            .disabled(existing != nil)
                    } footer: {
                        Text("A smart playlist fills itself from rules, and is worked out fresh each time it's opened.")
                    }
                }
                if smart {
                    SmartRulesEditor(rules: $rules, libraries: libraries)
                }
                if let errorMessage {
                    Section { Text(errorMessage).foregroundStyle(SentinelTheme.red) }
                }
            }
            .formStyle(.grouped)
            .navigationTitle(existing == nil ? (smart ? "New Smart Playlist" : "New Playlist") : "Edit Playlist")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button(saving ? "Saving…" : (existing == nil ? "Create" : "Save")) { Task { await save() } }
                        .disabled(name.trimmingCharacters(in: .whitespaces).isEmpty || saving)
                }
            }
            .onAppear(perform: seed)
            .task { libraries = (try? await AppModel.shared.client.musicLibraries()) ?? [] }
        }
        #if os(macOS)
        .frame(minWidth: 560, minHeight: 600)
        #endif
    }

    private func seed() {
        guard !seeded else { return }
        seeded = true
        smart = startSmart
        guard let existing else { return }
        name = existing.name
        description = existing.description ?? ""
        shared = existing.shared == true
        collaborative = existing.collaborative == true
        smart = existing.isSmart
        if let saved = existing.rules { rules = saved }
    }

    private func save() async {
        saving = true
        defer { saving = false }
        var cleaned = rules
        cleaned.rules = rules.rules.filter { rule in
            if case .string(let text) = rule.value { return !text.trimmingCharacters(in: .whitespaces).isEmpty }
            return true
        }
        let input = MusicPlaylistInput(
            name: name.trimmingCharacters(in: .whitespaces),
            description: description.trimmingCharacters(in: .whitespacesAndNewlines),
            shared: shared, collaborative: smart ? false : collaborative,
            rules: smart ? cleaned : nil,
            trackIds: existing == nil && !smart && !initialTrackIds.isEmpty ? initialTrackIds : nil)
        do {
            let client = AppModel.shared.client
            let saved: MusicPlaylist
            if let existing {
                saved = try await client.updatePlaylist(id: existing.id, input)
            } else {
                saved = try await client.createPlaylist(input)
            }
            NotificationCenter.default.post(name: .audioLibraryChanged, object: nil)
            if existing == nil { AudioToast.shared.show("Made \u{201C}\(saved.name)\u{201D}") }
            onSaved(saved)
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

// The rule builder: match all/any, one row per rule, then how to sort and how many.
struct SmartRulesEditor: View {
    @Binding var rules: SmartRules
    let libraries: [MusicLibrary]

    static let fields: [(id: String, title: String)] = [
        ("title", "Title"), ("artist", "Artist"), ("album", "Album"), ("genre", "Genre"), ("composer", "Composer"),
        ("year", "Year"), ("rating", "Rating"), ("playCount", "Plays"), ("durationSeconds", "Length (seconds)"),
        ("bpm", "BPM"), ("loved", "Loved"), ("added", "Added"), ("lastPlayed", "Last Played"), ("codec", "Format"),
        ("library", "Library"),
    ]

    static func ops(for field: String) -> [(id: String, title: String)] {
        switch field {
        case "title", "artist", "album", "genre", "composer":
            [("is", "is"), ("isNot", "is not"), ("contains", "contains"), ("notContains", "doesn't contain")]
        case "year", "rating", "playCount", "durationSeconds", "bpm":
            [("is", "is"), ("gte", "is at least"), ("lte", "is at most")]
        case "loved":
            [("is", "is")]
        case "added", "lastPlayed":
            [("inLast", "in the last"), ("notInLast", "not in the last")]
        default:
            [("is", "is"), ("isNot", "is not")]
        }
    }

    static func defaultValue(for field: String) -> AudioJSONValue {
        switch field {
        case "year": .number(2000)
        case "rating": .number(4)
        case "playCount": .number(5)
        case "durationSeconds": .number(300)
        case "bpm": .number(120)
        case "loved": .bool(true)
        case "added", "lastPlayed": .number(30)
        case "codec": .string("flac")
        default: .string("")
        }
    }

    var body: some View {
        Section {
            Picker("Match", selection: $rules.match) {
                Text("All of these rules").tag("all")
                Text("Any of these rules").tag("any")
            }
            ForEach($rules.rules) { $rule in
                RuleRow(rule: $rule, libraries: libraries) {
                    rules.rules.removeAll { $0.id == rule.id }
                }
            }
            Button {
                rules.rules.append(SmartRule(field: "artist", op: "is", value: .string("")))
            } label: {
                Label("Add Rule", systemImage: "plus.circle")
            }
        } header: {
            Text("Rules")
        } footer: {
            Text("\u{201C}Last Played not in the last N days\u{201D} also matches songs never played.")
        }
        Section("Order") {
            Picker("Sort by", selection: Binding(get: { rules.sort ?? "random" }, set: { rules.sort = $0 })) {
                Text("Random").tag("random")
                Text("Title").tag("title")
                Text("Artist").tag("artist")
                Text("Album").tag("album")
                Text("Year").tag("year")
                Text("Recently Added").tag("added")
                Text("Plays").tag("plays")
                Text("Last Played").tag("lastPlayed")
                Text("Rating").tag("rating")
            }
            if rules.sort != "random" {
                Picker("Order", selection: Binding(get: { rules.order ?? "desc" }, set: { rules.order = $0 })) {
                    Text("Ascending").tag("asc")
                    Text("Descending").tag("desc")
                }
                .pickerStyle(.segmented)
            }
            Stepper(value: Binding(get: { rules.limit ?? 0 }, set: { rules.limit = $0 }), in: 0...5000, step: 25) {
                Text((rules.limit ?? 0) == 0 ? "No limit" : "Limit to \(rules.limit ?? 0) songs")
            }
        }
    }
}

private struct RuleRow: View {
    @Binding var rule: SmartRule
    let libraries: [MusicLibrary]
    let onDelete: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Picker("Field", selection: Binding(get: { rule.field }, set: { field in
                    rule.field = field
                    rule.op = SmartRulesEditor.ops(for: field).first?.id ?? "is"
                    rule.value = SmartRulesEditor.defaultValue(for: field)
                })) {
                    ForEach(SmartRulesEditor.fields, id: \.id) { Text($0.title).tag($0.id) }
                }
                .labelsHidden()
                Picker("Condition", selection: $rule.op) {
                    ForEach(SmartRulesEditor.ops(for: rule.field), id: \.id) { Text($0.title).tag($0.id) }
                }
                .labelsHidden()
                Spacer()
                Button(role: .destructive, action: onDelete) {
                    Image(systemName: "minus.circle.fill").foregroundStyle(SentinelTheme.red)
                }
                .buttonStyle(.borderless)
                .accessibilityLabel("Remove rule")
            }
            valueEditor
        }
        .padding(.vertical, 2)
    }

    @ViewBuilder
    private var valueEditor: some View {
        switch rule.field {
        case "loved":
            Picker("Value", selection: Binding(get: { if case .bool(let b) = rule.value { b } else { true } }, set: { rule.value = .bool($0) })) {
                Text("Loved").tag(true)
                Text("Not loved").tag(false)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
        case "codec":
            Picker("Format", selection: Binding(get: { rule.value.text }, set: { rule.value = .string($0) })) {
                ForEach(["flac", "alac", "mp3", "aac", "opus", "vorbis", "wav", "aiff"], id: \.self) { Text($0.uppercased()).tag($0) }
            }
            .labelsHidden()
        case "library":
            Picker("Library", selection: Binding(get: { rule.value.text }, set: { rule.value = .string($0) })) {
                Text("Choose…").tag("")
                ForEach(libraries) { Text($0.name).tag($0.id) }
            }
            .labelsHidden()
        case "year", "rating", "playCount", "durationSeconds", "bpm", "added", "lastPlayed":
            let range: ClosedRange<Double> = rule.field == "rating" ? 0...5 : rule.field == "year" ? 1900...2100 : 0...100000
            Stepper(value: Binding(get: { if case .number(let n) = rule.value { n } else { Double(rule.value.text) ?? 0 } },
                                   set: { rule.value = .number($0) }),
                    in: range, step: rule.field == "durationSeconds" ? 30 : 1) {
                Text(numberLabel)
            }
        default:
            TextField("", text: Binding(get: { rule.value.text }, set: { rule.value = .string($0) }), prompt: Text("Value"))
                .labelsHidden()
                .multilineTextAlignment(.leading)
                .textFieldStyle(.roundedBorder)
                .autocorrectionDisabled()
        }
    }

    private var numberLabel: String {
        let value = Int(rule.value.text) ?? 0
        switch rule.field {
        case "added", "lastPlayed": return "\(value) day\(value == 1 ? "" : "s")"
        case "rating": return value == 0 ? "Unrated" : String(repeating: "★", count: value)
        case "durationSeconds": return AudioFormat.duration(Double(value))
        default: return "\(value)"
        }
    }
}

// MARK: - "+ Add": find an album and download it into the library (§4 Getting music)

struct MusicAddAlbumView: View {
    @Environment(\.dismiss) private var dismiss
    @State private var query = ""
    @State private var results: [MusicLookupResult] = []
    @State private var looking = false
    @State private var errorMessage: String?
    @State private var chosen: MusicLookupResult?

    var body: some View {
        NavigationStack {
            List {
                Section {
                    HStack {
                        TextField("", text: $query, prompt: Text("Artist and album, e.g. fleetwood mac rumours"))
                            .labelsHidden()
                            .multilineTextAlignment(.leading)
                            .autocorrectionDisabled()
                            .onSubmit { Task { await lookup() } }
                        Button("Search") { Task { await lookup() } }
                            .disabled(query.trimmingCharacters(in: .whitespaces).isEmpty || looking)
                    }
                } footer: {
                    Text("Searches MusicBrainz. Type the artist and album for the best results.")
                }
                if looking { ProgressView().frame(maxWidth: .infinity) }
                if let errorMessage { Text(errorMessage).foregroundStyle(SentinelTheme.red) }
                ForEach(results) { result in
                    NavigationLink {
                        MusicReleasePickerView(album: result) { dismiss() }
                    } label: {
                        HStack(spacing: 12) {
                            RemoteCover(url: result.artUrl)
                                .frame(width: 56, height: 56)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(result.title).lineLimit(1)
                                Text([result.artist, result.year.map(String.init), result.type, result.trackCount.map { "\($0) tracks" }]
                                    .compactMap { $0 }.joined(separator: " · "))
                                    .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                                if result.inLibrary == true {
                                    Label("Already in your library", systemImage: "checkmark.circle.fill")
                                        .font(.caption2).foregroundStyle(SentinelTheme.green)
                                }
                            }
                        }
                    }
                }
            }
            .navigationTitle("Add an Album")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar { ToolbarItem(placement: .cancellationAction) { Button("Close") { dismiss() } } }
        }
        #if os(macOS)
        .frame(minWidth: 560, minHeight: 600)
        #endif
    }

    private func lookup() async {
        let q = query.trimmingCharacters(in: .whitespaces)
        guard !q.isEmpty else { return }
        looking = true
        defer { looking = false }
        do {
            results = try await AppModel.shared.client.musicDownloadLookup(q)
            errorMessage = results.isEmpty ? "Nothing found on MusicBrainz for \u{201C}\(q)\u{201D}." : nil
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

// Cover Art Archive pictures are absolute URLs that may 404 (§4).
private struct RemoteCover: View {
    let url: String?

    var body: some View {
        AsyncImage(url: url.flatMap(URL.init(string:))) { phase in
            if let image = phase.image {
                image.resizable().scaledToFill()
            } else {
                ArtworkPlaceholder(seed: url ?? "album", symbol: "opticaldisc")
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: 6))
    }
}

private struct MusicReleasePickerView: View {
    let album: MusicLookupResult
    let onGrabbed: () -> Void
    @State private var search: MusicReleaseSearch?
    @State private var errorMessage: String?
    @State private var showingOthers = false
    @State private var grabbing: String?

    var body: some View {
        List {
            Section {
                HStack(spacing: 14) {
                    RemoteCover(url: album.artUrl).frame(width: 80, height: 80)
                    VStack(alignment: .leading, spacing: 3) {
                        Text(album.title).font(.headline)
                        Text([album.artist, album.year.map(String.init)].compactMap { $0 }.joined(separator: " · ")).foregroundStyle(.secondary)
                    }
                }
            }
            if let search, !search.isFinished {
                Section {
                    HStack(spacing: 10) {
                        ProgressView()
                        Text(search.message ?? (search.phase == "connecting" ? "Connecting to the VPN…" : "Searching the sites…"))
                            .foregroundStyle(.secondary)
                    }
                }
            } else if search == nil, errorMessage == nil {
                Section { ProgressView().frame(maxWidth: .infinity) }
            }
            if let error = search?.error ?? errorMessage {
                Section { Text(error).foregroundStyle(SentinelTheme.red) }
            }
            if let results = search?.results, search?.isFinished == true {
                Section(results.isEmpty ? "No matching releases" : "Releases") {
                    ForEach(results) { releaseRow($0) }
                }
                if let others = search?.others, !others.isEmpty {
                    Section {
                        DisclosureGroup("Show \(others.count) more", isExpanded: $showingOthers) {
                            ForEach(others) { releaseRow($0) }
                        }
                    }
                }
            }
        }
        .navigationTitle("Choose a Release")
        .task { await run() }
    }

    private func releaseRow(_ release: MusicRelease) -> some View {
        Button {
            Task { await grab(release) }
        } label: {
            VStack(alignment: .leading, spacing: 4) {
                HStack {
                    if let format = release.format {
                        Text(format)
                            .font(.caption2.weight(.bold))
                            .padding(.horizontal, 6).padding(.vertical, 2)
                            .background((release.lossless == true ? SentinelTheme.green : Color.secondary).opacity(0.18), in: Capsule())
                            .foregroundStyle(release.lossless == true ? SentinelTheme.green : .secondary)
                    }
                    Text(release.title ?? "Release").lineLimit(2).foregroundStyle(.primary)
                    Spacer()
                    if grabbing == release.id { ProgressView().controlSize(.small) }
                }
                Text([release.sizeBytes.map(AudioFormat.bytes), release.seeders.map { "\($0) seeders" }, release.site]
                    .compactMap { $0 }.joined(separator: " · "))
                    .font(.caption).foregroundStyle(.secondary)
                if let warning = release.warning {
                    Label(warning, systemImage: "exclamationmark.triangle").font(.caption).foregroundStyle(SentinelTheme.amber)
                }
            }
        }
        .buttonStyle(.plain)
        .disabled(grabbing != nil)
    }

    private func run() async {
        let client = AppModel.shared.client
        do {
            var current = try await client.startMusicDownloadSearch(
                artist: album.artist ?? "", album: album.title, year: album.year, mbid: album.mbid, artUrl: album.artUrl)
            search = current
            while !current.isFinished, !Task.isCancelled {
                try await Task.sleep(for: .seconds(2))
                current = try await client.musicDownloadSearch(id: current.id)
                search = current
            }
        } catch {
            if !error.isCancellation { errorMessage = error.localizedDescription }
        }
    }

    private func grab(_ release: MusicRelease) async {
        guard let search else { return }
        grabbing = release.id
        defer { grabbing = nil }
        do {
            _ = try await AppModel.shared.client.startMusicDownload(searchId: search.id, releaseId: release.id, libraryId: nil)
            AudioToast.shared.show("Downloading \(album.title)")
            onGrabbed()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

struct MusicDownloadsListView: View {
    @State private var list: MusicDownloadList?
    @State private var errorMessage: String?
    @State private var importing = false

    var body: some View {
        List {
            if let status = list?.status {
                Section {
                    let look = MediaVpnLook(status)
                    Text(look.text).font(.footnote).foregroundStyle(.secondary)
                }
            }
            let active = list?.active ?? []
            if !active.isEmpty {
                Section("Downloading") { ForEach(active) { row($0) } }
            }
            MediaImportListsSection(kind: .music) { importing = true }
            let recent = list?.recent ?? []
            if !recent.isEmpty {
                Section("Recent") { ForEach(recent) { row($0) } }
            }
            if list != nil && active.isEmpty && recent.isEmpty {
                Text("No album downloads yet. Use + in Music to find one.").foregroundStyle(.secondary)
            }
            if let errorMessage { Text(errorMessage).foregroundStyle(SentinelTheme.red) }
        }
        .navigationTitle("Album Downloads")
        .sheet(isPresented: $importing) {
            MediaImportSheet(kind: .music) { _ in }
                .environmentObject(AppModel.shared)
        }
        .task {
            while !Task.isCancelled {
                await load()
                try? await Task.sleep(for: .seconds(3))
            }
        }
    }

    private func row(_ download: MediaDownload) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text(download.displayTitle).lineLimit(1)
                    Text([download.subtitle, download.state?.capitalized].compactMap { $0 }.joined(separator: " · "))
                        .font(.caption).foregroundStyle(download.isFailed ? SentinelTheme.red : .secondary)
                }
                Spacer()
                if download.isFailed, let id = download.id {
                    Button("Retry") { Task { _ = try? await AppModel.shared.client.retryMusicDownload(id: id); await load() } }
                        .buttonStyle(.borderless)
                }
                if let id = download.id {
                    Button(role: .destructive) {
                        Task { try? await AppModel.shared.client.deleteMusicDownload(id: id); await load() }
                    } label: {
                        Image(systemName: download.isActive ? "xmark.circle" : "trash")
                    }
                    .buttonStyle(.borderless)
                    .accessibilityLabel(download.isActive ? "Cancel" : "Clear")
                }
            }
            if download.isActive { ProgressView(value: download.fraction) }
            if let error = download.error { Text(error).font(.caption).foregroundStyle(.secondary) }
        }
    }

    private func load() async {
        do {
            list = try await AppModel.shared.client.musicDownloads()
            errorMessage = nil
        } catch {
            if !error.isCancellation { errorMessage = error.localizedDescription }
        }
    }
}
