import SwiftUI
import UniformTypeIdentifiers

// "Import a list" on Music and Movies: pick a CSV/JSON exported from Spotify, Letterboxd, IMDb
// and the like, check what was found, and the server works through it in the background
// (search -> best release -> download queue). The imported lists then show under Downloads.
// Contract: core/docs/MOVIE_DOWNLOADS.md "Import a list"; the dashboard's version is
// wwwroot/media-import.js, whose wording this follows.

extension Notification.Name {
    // Posted with the MediaImportKind's rawValue as the object when a list is started, changed
    // or removed, so every Imported Lists section on screen reads the lists again.
    static let mediaImportsChanged = Notification.Name("mediaImportsChanged")
}

extension MediaImportKind {
    // The "where do I get a file" help, as Markdown (links open in the browser).
    var helpLines: [String] {
        switch self {
        case .music:
            [
                "**Spotify, quickest:** open [exportify.net](https://exportify.net), sign in with Spotify, and press *Export* next to Liked Songs or any playlist. Choose the .csv here.",
                "**Spotify, everything:** Spotify → Account → Privacy settings → *Download your data* (takes a few days). Choose `YourLibrary.json` and the `Playlist*.json` files here.",
                "Also reads TuneMyMusic and Soundiiz exports, or any spreadsheet saved as CSV with *Artist*, *Album* and *Track* columns.",
            ]
        case .movies:
            [
                "**Letterboxd:** Settings → Data → *Export your data*; choose `watchlist.csv` (or `watched.csv`) here.",
                "**IMDb:** open your Watchlist or a list → ⋯ → *Export*.",
                "Also reads Trakt JSON, or any CSV with *Title* and *Year* columns. TV shows are left out: add those with **+ Add** so you can pick seasons.",
            ]
        }
    }

    var helpFooter: String? {
        self == .music
            ? "Torrent sites share whole albums, not songs, so songs are grouped into the albums they come from and each album is downloaded once."
            : nil
    }
}

// MARK: - The import sheet

// Opens the import flow in its own sheet. `onStarted` runs with the number of albums or films
// once a list has been started and the sheet has closed.
struct MediaImportSheet: View {
    let kind: MediaImportKind
    let onStarted: (Int) -> Void
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            MediaImportView(kind: kind) { count in
                dismiss()
                onStarted(count)
            }
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Close") { dismiss() }
                }
            }
        }
        #if os(macOS)
        // A Mac sheet collapses around a List without a minimum size.
        .frame(minWidth: 600, minHeight: 640)
        #endif
    }
}

// The flow itself: choose files -> what was found -> start. No NavigationStack of its own.
struct MediaImportView: View {
    let kind: MediaImportKind
    let onStarted: (Int) -> Void

    @State private var picking = false
    @State private var files: [MediaImportFile] = []
    @State private var preview: MediaImportPreview?
    @State private var candidates: [MediaImportCandidate] = []
    @State private var checked: Set<String> = []
    @State private var filter = ""
    @State private var name = ""
    @State private var busy = false
    @State private var errorMessage: String?

    private static let shownAtOnce = 400
    private static let contentTypes: [UTType] = {
        var types: [UTType] = [.commaSeparatedText, .json, .plainText, .tabSeparatedText]
        if let tsv = UTType(filenameExtension: "tsv") { types.append(tsv) }
        return types
    }()

    var body: some View {
        List {
            if preview == nil {
                pickSections
            } else {
                previewSections
            }
        }
        #if os(macOS)
        .listStyle(.inset)
        #endif
        .navigationTitle("Import a List of \(kind.things.capitalized)")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .fileImporter(isPresented: $picking, allowedContentTypes: Self.contentTypes, allowsMultipleSelection: true) { result in
            Task { await read(result) }
        }
        .safeAreaInset(edge: .bottom, spacing: 0) {
            if preview != nil { bottomBar }
        }
    }

    // MARK: Choosing files

    @ViewBuilder
    private var pickSections: some View {
        Section {
            ForEach(kind.helpLines, id: \.self) { line in
                Text(LocalizedStringKey(line))
                    .font(.callout)
                    .fixedSize(horizontal: false, vertical: true)
            }
        } footer: {
            if let footer = kind.helpFooter { Text(footer) }
        }
        Section {
            Button {
                errorMessage = nil
                picking = true
            } label: {
                HStack(spacing: 10) {
                    if busy {
                        ProgressView().controlSize(.small)
                        Text("Reading…")
                    } else {
                        Label("Choose Files…", systemImage: "doc.badge.plus")
                    }
                }
            }
            .disabled(busy)
        } footer: {
            Text(".csv or .json · several files at once is fine")
        }
        if let errorMessage {
            Section { Text(errorMessage).foregroundStyle(SentinelTheme.red) }
        }
    }

    // MARK: What was found

    private var shown: [MediaImportCandidate] {
        candidates.filter { $0.matches(filter) }
    }

    @ViewBuilder
    private var previewSections: some View {
        if let preview {
            Section {
                Text(preview.facts(kind).joined(separator: " · "))
                    .font(.headline)
                ForEach(preview.notes(kind), id: \.self) { note in
                    Label(note, systemImage: "info.circle")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
            }
            if candidates.isEmpty {
                Section {
                    Text("No \(kind.things) found in \(files.count == 1 ? "that file" : "those files").")
                        .foregroundStyle(.secondary)
                }
            } else {
                let matching = shown
                Section {
                    HStack(spacing: 14) {
                        TextField("", text: $filter, prompt: Text("Filter"))
                            .labelsHidden()
                            .multilineTextAlignment(.leading)
                            .textFieldStyle(.roundedBorder)
                            .autocorrectionDisabled()
                        Button("All") { checked = Set(candidates.map(\.key)) }
                        Button("None") { checked = [] }
                        Button("Only Missing") { checked = Set(candidates.filter { !$0.inLibrary }.map(\.key)) }
                    }
                    .buttonStyle(.borderless)
                    ForEach(matching.prefix(Self.shownAtOnce)) { candidate in
                        candidateRow(candidate)
                    }
                    if matching.count > Self.shownAtOnce {
                        Text("…and \(matching.count - Self.shownAtOnce) more (all still included). Filter to find one.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                } header: {
                    Text("\(checked.count) of \(kind.count(candidates.count)) chosen")
                }
                Section {
                    TextField("", text: $name, prompt: Text("Name"))
                        .labelsHidden()
                        .multilineTextAlignment(.leading)
                } header: {
                    Text("Name This List")
                } footer: {
                    Text("The server searches your sites for each one through the VPN, picks the best release with people sharing it, and keeps a few downloading at a time. You can close the app; it carries on, and the list shows under Downloads.")
                }
            }
            if let errorMessage {
                Section { Text(errorMessage).foregroundStyle(SentinelTheme.red) }
            }
        }
    }

    private func candidateRow(_ candidate: MediaImportCandidate) -> some View {
        let isChecked = checked.contains(candidate.key)
        return Button {
            if isChecked { checked.remove(candidate.key) } else { checked.insert(candidate.key) }
        } label: {
            HStack(spacing: 12) {
                Image(systemName: isChecked ? "checkmark.circle.fill" : "circle")
                    .font(.title3)
                    .foregroundStyle(isChecked ? AnyShapeStyle(SentinelTheme.green) : AnyShapeStyle(.secondary))
                VStack(alignment: .leading, spacing: 2) {
                    HStack(spacing: 6) {
                        Text(candidate.title).lineLimit(1)
                        if let year = candidate.year {
                            Text(String(year)).font(.caption).foregroundStyle(.secondary)
                        }
                        if candidate.inLibrary { MediaChip(text: "in library", highlighted: true) }
                        if candidate.single { MediaChip(text: "single") }
                    }
                    if kind == .music {
                        let songs = (candidate.trackCount ?? 0) > 0
                            ? " · \(mediaImportPlural(candidate.trackCount ?? 0, "song")) on your list" : ""
                        Text("\(candidate.artist ?? "")\(songs)")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                }
                Spacer(minLength: 0)
            }
            .contentShape(Rectangle())
            .opacity(candidate.inLibrary && !isChecked ? 0.6 : 1)
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(isChecked ? .isSelected : [])
    }

    private var bottomBar: some View {
        HStack(spacing: 12) {
            Button("Choose Other Files") {
                preview = nil
                candidates = []
                checked = []
                filter = ""
                errorMessage = nil
            }
            .disabled(busy)
            Spacer(minLength: 8)
            if !candidates.isEmpty {
                Button {
                    Task { await start() }
                } label: {
                    if busy {
                        HStack(spacing: 6) { ProgressView().controlSize(.small); Text("Starting…") }
                    } else {
                        Text("Download \(kind.count(checked.count))")
                    }
                }
                .buttonStyle(.borderedProminent)
                .tint(SentinelTheme.green)
                .disabled(checked.isEmpty || busy)
            }
        }
        .padding(.horizontal)
        .padding(.vertical, 10)
        .background(.bar)
    }

    // MARK: Talking to the server

    private func read(_ result: Result<[URL], Error>) async {
        let urls: [URL]
        switch result {
        case .failure(let error):
            errorMessage = error.localizedDescription
            return
        case .success(let picked):
            urls = picked
        }
        guard !urls.isEmpty else { return }
        busy = true
        errorMessage = nil
        defer { busy = false }
        var read: [MediaImportFile] = []
        for url in urls {
            // Files from the Files app or another app's folder are outside the sandbox.
            let scoped = url.startAccessingSecurityScopedResource()
            defer { if scoped { url.stopAccessingSecurityScopedResource() } }
            guard let data = try? Data(contentsOf: url) else {
                errorMessage = "\(url.lastPathComponent) could not be read."
                return
            }
            var text = String(decoding: data, as: UTF8.self)
            // A spreadsheet's byte-order mark is not part of the first column's name.
            if text.hasPrefix("\u{FEFF}") { text.removeFirst() }
            read.append(MediaImportFile(name: url.lastPathComponent, text: text))
        }
        do {
            let answer = try await AppModel.shared.client.mediaImportPreview(kind: kind, files: read)
            files = read
            candidates = answer.candidates(kind)
            checked = Set(candidates.filter { !$0.inLibrary }.map(\.key))
            name = mediaImportDefaultName(kind, fileNames: read.map(\.name))
            filter = ""
            preview = answer
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            errorMessage = error.localizedDescription
        }
    }

    private func start() async {
        let chosen = candidates.filter { checked.contains($0.key) }
        guard !chosen.isEmpty else { return }
        busy = true
        errorMessage = nil
        defer { busy = false }
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        let input = MediaImportStartInput(
            name: trimmed.isEmpty ? mediaImportDefaultName(kind, fileNames: files.map(\.name)) : trimmed,
            libraryId: nil, items: chosen.map(\.startItem))
        do {
            _ = try await AppModel.shared.client.startMediaImport(kind: kind, input)
            NotificationCenter.default.post(name: .mediaImportsChanged, object: kind.rawValue)
            onStarted(chosen.count)
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            errorMessage = error.localizedDescription
        }
    }
}

// MARK: - Imported lists (under Downloads)

// The "Imported Lists" section of a Downloads screen: one card per list with how far it has got,
// what can be done with it, and the way into its details. Reads the lists every 5 s while one is
// working, every 30 s otherwise.
struct MediaImportListsSection: View {
    let kind: MediaImportKind
    let onImport: () -> Void

    @State private var jobs: [MediaImportJob]?
    @State private var loadError: String?
    @State private var working: Set<String> = []
    @State private var confirmingRemove: MediaImportJob?
    @State private var actionError: String?

    var body: some View {
        Section {
            // Always the first row, so the section's loop and dialogs hang off something that is
            // always there.
            Button(action: onImport) {
                Label("Import a List", systemImage: "list.bullet.rectangle")
            }
            .task {
                while !Task.isCancelled {
                    await load()
                    let busy = (jobs ?? []).contains(where: \.isBusy)
                    try? await Task.sleep(for: .seconds(busy ? 5 : 30))
                }
            }
            .onReceive(NotificationCenter.default.publisher(for: .mediaImportsChanged)) { note in
                guard (note.object as? String) == kind.rawValue else { return }
                Task { await load() }
            }
            .confirmationDialog(
                "Remove \(confirmingRemove?.displayName ?? "this list")?",
                isPresented: Binding(get: { confirmingRemove != nil }, set: { if !$0 { confirmingRemove = nil } }),
                titleVisibility: .visible,
                presenting: confirmingRemove
            ) { job in
                Button("Remove List", role: .destructive) { Task { await remove(job) } }
            } message: { _ in
                Text("Nothing more on it is searched for. Downloads it already started carry on under Downloads.")
            }
            .alert("Imported Lists", isPresented: Binding(get: { actionError != nil }, set: { if !$0 { actionError = nil } })) {
                Button("OK", role: .cancel) {}
            } message: {
                Text(actionError ?? "")
            }

            if let jobs {
                if jobs.isEmpty {
                    Text(kind.emptyLine).font(.callout).foregroundStyle(.secondary)
                }
                ForEach(jobs) { job in
                    // A row that opens the details, then a row of its own for the buttons, so a
                    // tap on Pause never opens the details by accident.
                    NavigationLink {
                        MediaImportDetailView(kind: kind, jobId: job.id ?? "", title: job.displayName)
                    } label: {
                        MediaImportJobSummary(kind: kind, job: job, showsName: true)
                    }
                    MediaImportJobButtons(
                        job: job, working: working.contains(job.id ?? ""),
                        pause: { Task { await change(job) { try await $0.pauseMediaImport(kind: kind, id: $1, paused: !job.isPaused) } } },
                        retry: { Task { await change(job) { try await $0.retryMediaImport(kind: kind, id: $1) } } },
                        remove: { confirmingRemove = job })
                }
            } else if let loadError {
                Text(loadError).font(.callout).foregroundStyle(SentinelTheme.red)
            }
        } header: {
            Text("Imported Lists")
        }
    }

    private func load() async {
        do {
            jobs = try await AppModel.shared.client.mediaImports(kind: kind)
            loadError = nil
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            loadError = error.localizedDescription
        }
    }

    private func change(_ job: MediaImportJob, _ action: (SentinelClient, String) async throws -> MediaImportJob) async {
        guard let id = job.id else { return }
        working.insert(id)
        defer { working.remove(id) }
        do {
            _ = try await action(AppModel.shared.client, id)
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            actionError = error.localizedDescription
        }
        await load()
    }

    private func remove(_ job: MediaImportJob) async {
        guard let id = job.id else { return }
        working.insert(id)
        defer { working.remove(id) }
        do {
            try await AppModel.shared.client.deleteMediaImport(kind: kind, id: id)
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            actionError = error.localizedDescription
        }
        await load()
    }
}

// A list's name, how it is going in numbers, and the bar.
struct MediaImportJobSummary: View {
    let kind: MediaImportKind
    let job: MediaImportJob
    var showsName = true

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            if showsName {
                HStack(spacing: 6) {
                    Text(job.displayName).font(.headline).lineLimit(1)
                    MediaChip(text: kind.count(job.total))
                }
            }
            HStack(spacing: 6) {
                MediaChip(text: job.statusWord, highlighted: job.isBusy)
                Text(job.summaryBits.joined(separator: " · "))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
            }
            ProgressView(value: job.settledFraction)
                .tint(SentinelTheme.green)
            if let message = job.shownMessage {
                Text(message)
                    .font(.caption)
                    .foregroundStyle(.orange)
            }
        }
        .padding(.vertical, 2)
    }
}

// Pause / Resume, Search Missing Again, Remove List.
struct MediaImportJobButtons: View {
    let job: MediaImportJob
    let working: Bool
    let pause: () -> Void
    let retry: () -> Void
    let remove: () -> Void

    var body: some View {
        HStack(spacing: 16) {
            if job.canPause {
                Button(action: pause) {
                    Label(job.isPaused ? "Resume" : "Pause", systemImage: job.isPaused ? "play.fill" : "pause.fill")
                }
            }
            if job.canRetry {
                Button(action: retry) {
                    Label("Search Missing Again", systemImage: "arrow.clockwise")
                }
            }
            Button(role: .destructive, action: remove) {
                Label("Remove List", systemImage: "trash")
            }
            .foregroundStyle(SentinelTheme.red)
            if working { ProgressView().controlSize(.small) }
            Spacer(minLength: 0)
        }
        .font(.caption.weight(.medium))
        .buttonStyle(.borderless)
        .disabled(working)
    }
}

// MARK: - One list's details

struct MediaImportDetailView: View {
    let kind: MediaImportKind
    let jobId: String
    let title: String

    @Environment(\.dismiss) private var dismiss
    @State private var job: MediaImportJob?
    @State private var loadError: String?
    @State private var filter: MediaImportItemFilter = .all
    @State private var working = false
    @State private var skipping: Set<String> = []
    @State private var confirmingRemove = false
    @State private var actionError: String?

    private static let shownAtOnce = 800

    var body: some View {
        List {
            if let job {
                Section {
                    MediaImportJobSummary(kind: kind, job: job, showsName: false)
                    MediaImportJobButtons(
                        job: job, working: working,
                        pause: { Task { await change { try await $0.pauseMediaImport(kind: kind, id: jobId, paused: !job.isPaused) } } },
                        retry: { Task { await change { try await $0.retryMediaImport(kind: kind, id: jobId) } } },
                        remove: { confirmingRemove = true })
                }
                Section {
                    Picker("Show", selection: $filter) {
                        ForEach(MediaImportItemFilter.allCases) { Text($0.title).tag($0) }
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                    let items = (job.items ?? []).filter { filter.matches($0.state) }
                    if items.isEmpty {
                        Text("Nothing here.").foregroundStyle(.secondary)
                    }
                    ForEach(items.prefix(Self.shownAtOnce)) { item in
                        itemRow(item)
                    }
                    if items.count > Self.shownAtOnce {
                        Text("…and \(items.count - Self.shownAtOnce) more.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                } header: {
                    Text(kind.count(job.total))
                }
            } else if let loadError {
                Text(loadError).foregroundStyle(SentinelTheme.red)
            } else {
                HStack { ProgressView(); Text("Loading…").foregroundStyle(.secondary) }
            }
        }
        #if os(macOS)
        .listStyle(.inset)
        #endif
        .navigationTitle(job?.displayName ?? title)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .refreshable { await load() }
        .task {
            while !Task.isCancelled {
                await load()
                try? await Task.sleep(for: .seconds(job?.isFinished == true ? 30 : 4))
            }
        }
        .confirmationDialog(
            "Remove \(job?.displayName ?? title)?",
            isPresented: $confirmingRemove,
            titleVisibility: .visible
        ) {
            Button("Remove List", role: .destructive) { Task { await remove() } }
        } message: {
            Text("Nothing more on it is searched for. Downloads it already started carry on under Downloads.")
        }
        .alert("Imported List", isPresented: Binding(get: { actionError != nil }, set: { if !$0 { actionError = nil } })) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(actionError ?? "")
        }
    }

    private func itemRow(_ item: MediaImportItem) -> some View {
        HStack(alignment: .top, spacing: 10) {
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(item.displayTitle).lineLimit(1)
                    if let year = item.year {
                        Text(String(year)).font(.caption).foregroundStyle(.secondary)
                    }
                }
                if let artist = item.artist, !artist.isEmpty {
                    Text(artist).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
                if let release = item.releaseTitle, !release.isEmpty {
                    Text(release)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                        .help(release)
                }
                if let message = item.message, !message.isEmpty {
                    Text(message)
                        .font(.caption)
                        .foregroundStyle(item.isFailed ? SentinelTheme.red : .orange)
                }
            }
            Spacer(minLength: 8)
            Text(item.stateWords)
                .font(.caption.weight(.medium))
                .monospacedDigit()
                .foregroundStyle(item.isDone ? AnyShapeStyle(SentinelTheme.green)
                                 : item.isFailed ? AnyShapeStyle(SentinelTheme.red) : AnyShapeStyle(.secondary))
            if item.canSkip, let itemId = item.id {
                if skipping.contains(itemId) {
                    ProgressView().controlSize(.small)
                } else {
                    Button {
                        Task { await skip(itemId) }
                    } label: {
                        Image(systemName: "xmark")
                    }
                    .buttonStyle(.borderless)
                    .foregroundStyle(.secondary)
                    .help("Leave this one out")
                    .accessibilityLabel("Leave \(item.displayTitle) out")
                }
            }
        }
        .padding(.vertical, 2)
    }

    private func load() async {
        guard !jobId.isEmpty else { return }
        do {
            job = try await AppModel.shared.client.mediaImport(kind: kind, id: jobId)
            loadError = nil
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            loadError = error.localizedDescription
        }
    }

    private func change(_ action: (SentinelClient) async throws -> MediaImportJob) async {
        working = true
        defer { working = false }
        do {
            job = try await action(AppModel.shared.client)
            NotificationCenter.default.post(name: .mediaImportsChanged, object: kind.rawValue)
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            actionError = error.localizedDescription
        }
    }

    private func skip(_ itemId: String) async {
        skipping.insert(itemId)
        defer { skipping.remove(itemId) }
        do {
            job = try await AppModel.shared.client.skipMediaImportItem(kind: kind, id: jobId, itemId: itemId)
            NotificationCenter.default.post(name: .mediaImportsChanged, object: kind.rawValue)
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            actionError = error.localizedDescription
        }
    }

    private func remove() async {
        working = true
        defer { working = false }
        do {
            try await AppModel.shared.client.deleteMediaImport(kind: kind, id: jobId)
            NotificationCenter.default.post(name: .mediaImportsChanged, object: kind.rawValue)
            dismiss()
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            actionError = error.localizedDescription
        }
    }
}
