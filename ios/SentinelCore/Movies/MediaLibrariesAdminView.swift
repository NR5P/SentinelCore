import SwiftUI

// Settings → Movies & TV Libraries (admin): the folders the Movies tab is built from. A library
// can be a folder on the server itself or a shared folder on another computer on the network
// (a Windows PC, a Mac with File Sharing on, a NAS) - the server reads those over SMB itself,
// so nothing has to be mounted. The folder picker walks both, and signs in to a computer when
// it asks for a password. Contract: core/docs/MOVIES.md.
//
// Twin of appletv/SentinelCoreTV/Movies/MediaLibrariesAdminView.swift - keep the two identical;
// the differences are the #if os(tvOS) branches.
struct MediaLibrariesAdminView: View {
    @EnvironmentObject private var model: AppModel

    @State private var libraries: [MediaLibrary] = []
    @State private var status: MediaStatus?
    @State private var loaded = false
    @State private var errorMessage: String?
    @State private var notice: String?
    @State private var refreshToken = 0

    var body: some View {
        Form {
            if let errorMessage, !loaded {
                Section {
                    Text(errorMessage).foregroundStyle(SentinelTheme.red)
                }
            }

            if let scan = status?.scan, status?.scanning == true {
                Section("Scanning") {
                    ScanProgressRow(scan: scan)
                }
            }

            Section {
                if !loaded && errorMessage == nil {
                    Text("Loading…").foregroundStyle(.secondary)
                } else if loaded && libraries.isEmpty {
                    Text("No libraries yet. Add the folder your movies or TV shows are in - on the server, or shared from another computer.")
                        .foregroundStyle(.secondary)
                }
                ForEach(libraries) { library in
                    NavigationLink {
                        MediaLibraryEditView(existing: library) { refreshToken += 1 }
                    } label: {
                        LibraryRow(library: library)
                    }
                }
            } header: {
                Text("Libraries")
            } footer: {
                if loaded {
                    Text("Each library is scanned when it's added and again every few hours. Watched progress is kept per person.")
                }
            }

            if loaded {
                Section {
                    NavigationLink {
                        MediaLibraryEditView(existing: nil) { refreshToken += 1 }
                    } label: {
                        Label("Add Library", systemImage: "plus")
                    }
                    if !libraries.isEmpty {
                        Button {
                            Task { await scanAll() }
                        } label: {
                            Label("Scan All Now", systemImage: "arrow.clockwise")
                        }
                        .disabled(status?.scanning == true)
                    }
                    if let notice {
                        Text(notice).font(.footnote).foregroundStyle(.secondary)
                    }
                }
            }
        }
        #if !os(tvOS)
        .formStyle(.grouped)
        #endif
        .navigationTitle("Movies & TV Libraries")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        .refreshable { await load() }
        #endif
        .task(id: refreshToken) { await load() }
        // While a scan runs the list keeps itself current: item counts and errors land as each
        // library finishes.
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

    private func load() async {
        do {
            let status = try await model.client.mediaStatus()
            self.status = status
            if !status.enabled {
                errorMessage = "Movies & TV is turned off. Turn it on in Server Settings → Movies & TV."
                loaded = false
                return
            }
            libraries = try await model.client.mediaLibraries()
            loaded = true
            errorMessage = nil
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func scanAll() async {
        do {
            try await model.client.scanMediaLibrary(id: nil)
            notice = "Scanning every library."
        } catch {
            notice = error.localizedDescription
        }
        await load()
    }
}

private struct ScanProgressRow: View {
    let scan: MediaScan

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(label)
            if let total = scan.total, total > 0 {
                ProgressView(value: Double(scan.processed ?? 0), total: Double(total))
            } else {
                ProgressView()
            }
        }
    }

    private var label: String {
        let what: String
        switch scan.phase {
        case "metadata": what = "Fetching titles and posters"
        case "probing": what = "Reading video details"
        default: what = "Looking for videos"
        }
        if let name = scan.libraryName { return "\(what) in \(name)…" }
        if let total = scan.total, total > 0 { return "\(what): \(scan.processed ?? 0) of \(total)" }
        return "\(what)…"
    }
}

private struct LibraryRow: View {
    let library: MediaLibrary

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(spacing: 6) {
                Image(systemName: library.network == true ? "externaldrive.connected.to.line.below" : (library.kind == "shows" ? "tv" : "film"))
                    .foregroundStyle(.secondary)
                Text(library.name)
            }
            Text(library.path ?? "")
                .font(.caption.monospaced())
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .truncationMode(.middle)
            Text(summary)
                .font(.caption)
                .foregroundStyle(library.lastScanError == nil ? Color.secondary : SentinelTheme.red)
                .lineLimit(3)
        }
    }

    private var summary: String {
        if let error = library.lastScanError { return error }
        let noun = library.kind == "shows" ? "show" : "movie"
        let count = library.itemCount ?? 0
        let titles = "\(count) \(noun)\(count == 1 ? "" : "s")"
        guard let when = library.lastScanFinishedAt else { return "\(titles) · not scanned yet" }
        return "\(titles) · scanned \(when.formatted(.relative(presentation: .named)))"
    }
}

// MARK: - Add / edit one library

struct MediaLibraryEditView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss

    let existing: MediaLibrary?
    var onChange: () -> Void

    @State private var name: String
    @State private var kind: String
    @State private var path: String
    @State private var isSaving = false
    @State private var errorMessage: String?
    @State private var notice: String?
    @State private var confirmingRemove = false

    init(existing: MediaLibrary?, onChange: @escaping () -> Void) {
        self.existing = existing
        self.onChange = onChange
        _name = State(initialValue: existing?.name ?? "Movies")
        _kind = State(initialValue: existing?.kind == "shows" ? "shows" : "movies")
        _path = State(initialValue: existing?.path ?? "")
    }

    private var canSave: Bool {
        !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && !path.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && !isSaving
    }

    var body: some View {
        Form {
            Section {
                LibraryTextField(label: "Name", prompt: "Movies", text: $name)
                Picker("Kind", selection: $kind) {
                    Text("Movies").tag("movies")
                    Text("TV Shows").tag("shows")
                }
                #if !os(tvOS)
                .pickerStyle(.segmented)
                #endif
                .onChange(of: kind) { _, newKind in
                    // A name nobody typed follows the kind, so "TV Shows" isn't left labelled "Movies".
                    if name.isEmpty || name == "Movies" || name == "TV Shows" {
                        name = newKind == "shows" ? "TV Shows" : "Movies"
                    }
                }
            }

            Section {
                LibraryTextField(label: "Folder", prompt: "/mnt/movies or smb://PC/Movies", text: $path, monospaced: true)
                // A NavigationLink handed a binding. Both alternatives hung the Mac app at 100% CPU
                // mid-push, the picker never appearing: .navigationDestination on this screen
                // (itself pushed by a NavigationLink), and a link given the path plus a callback.
                NavigationLink {
                    MediaFolderPickerView(path: $path)
                } label: {
                    Label("Choose Folder…", systemImage: "folder")
                }
            } footer: {
                Text(folderFooter)
            }

            Section {
                Button {
                    Task { await save() }
                } label: {
                    HStack {
                        Text(existing == nil ? "Add Library" : "Save")
                        if isSaving { Spacer(); ProgressView() }
                    }
                }
                .disabled(!canSave)
                if let errorMessage {
                    Text(errorMessage).foregroundStyle(SentinelTheme.red)
                }
            } footer: {
                Text(existing == nil
                     ? "Adding a library starts a scan of it straight away."
                     : "Changing the folder rescans the library. Watched progress is kept for files that are still there.")
            }

            if let existing {
                Section {
                    Button {
                        Task { await scan(existing) }
                    } label: {
                        Label("Scan Now", systemImage: "arrow.clockwise")
                    }
                    if let notice {
                        Text(notice).font(.footnote).foregroundStyle(.secondary)
                    }
                    Button(role: .destructive) {
                        confirmingRemove = true
                    } label: {
                        Label("Remove Library", systemImage: "trash")
                    }
                } footer: {
                    Text("Removing a library takes its titles and everyone's watched progress out of Sentinel Core. The video files themselves are not touched.")
                }
            }
        }
        #if !os(tvOS)
        .formStyle(.grouped)
        #endif
        .navigationTitle(existing == nil ? "Add Library" : existing!.name)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .confirmationDialog("Remove \(existing?.name ?? "this library")?", isPresented: $confirmingRemove, titleVisibility: .visible) {
            Button("Remove Library", role: .destructive) {
                Task { await remove() }
            }
        } message: {
            Text("Its titles and everyone's watched progress are removed from Sentinel Core. The video files are not touched.")
        }
    }

    private var folderFooter: String {
        let trimmed = path.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.lowercased().hasPrefix("smb://") || trimmed.hasPrefix("\\\\") {
            return "A shared folder on another computer. The server reads it over the network - nothing to mount."
        }
        return "A folder on the server, or a folder shared from another computer on your network. Choose Folder… lets you browse both and sign in to a computer."
    }

    private func save() async {
        isSaving = true
        errorMessage = nil
        defer { isSaving = false }
        let input = MediaLibraryInput(
            name: name.trimmingCharacters(in: .whitespacesAndNewlines),
            kind: kind,
            path: path.trimmingCharacters(in: .whitespacesAndNewlines))
        do {
            _ = try await model.client.saveMediaLibrary(id: existing?.id, input)
            onChange()
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func scan(_ library: MediaLibrary) async {
        do {
            try await model.client.scanMediaLibrary(id: library.id)
            notice = "Scanning \(library.name) now."
            onChange()
        } catch {
            notice = error.localizedDescription
        }
    }

    private func remove() async {
        guard let existing else { return }
        do {
            try await model.client.deleteMediaLibrary(id: existing.id)
            onChange()
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

// MARK: - Folder picker

// The picker is shared by Movies & TV and by Music / Podcasts: the same listing shape and the same
// saved network sign-ins, under /api/media or /api/audio.
enum MediaBrowseScope {
    case media, audio

    func count(_ count: Int) -> String {
        switch self {
        case .media: "\(count) video\(count == 1 ? "" : "s")"
        case .audio: "\(count) audio file\(count == 1 ? "" : "s")"
        }
    }
}

struct MediaFolderPickerView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss

    /// The library's folder box: read once to open the picker there, written by Use This Folder.
    @Binding var path: String
    /// Which picker routes to walk: Movies' /api/media/browse, or Music's /api/audio/browse (the
    /// same shape, counting audio files instead of videos - MUSIC_AND_PODCASTS.md §1).
    var scope: MediaBrowseScope = .media

    @State private var listing: MediaBrowseListing?
    @State private var typedPath = ""
    @State private var loading = false
    @State private var errorMessage: String?
    @State private var started = false
    @State private var discovered: [MediaDiscoveredComputer]?
    @State private var discovering = false
    @State private var openAfterSignIn: String?
    @State private var newFolderName = ""
    @State private var makingFolder = false
    @State private var newFolderError: String?

    var body: some View {
        Form {
            Section {
                LibraryTextField(label: "Path", prompt: "/mnt/movies or smb://computer/Shared", text: $typedPath, monospaced: true)
                    .onSubmit { Task { await load(typedPath.trimmingCharacters(in: .whitespacesAndNewlines)) } }
                HStack {
                    Button {
                        Task { await load(typedPath.trimmingCharacters(in: .whitespacesAndNewlines)) }
                    } label: {
                        Label("Go", systemImage: "arrow.right.circle")
                    }
                    Spacer()
                    Button {
                        Task { await load(listing?.parent ?? "") }
                    } label: {
                        Label("Up", systemImage: "arrow.up")
                    }
                    .disabled(listing == nil || listing?.isRoots == true)
                }
                #if !os(tvOS)
                .buttonStyle(.borderless)
                #endif
            } footer: {
                if loading { Text("Loading…") }
            }

            if let errorMessage {
                Section {
                    Text(errorMessage).foregroundStyle(SentinelTheme.red)
                }
            }

            if let listing {
                if let error = listing.error {
                    Section {
                        Text(error).foregroundStyle(SentinelTheme.red)
                        if listing.needsLogin == true, let host = listing.host {
                            NavigationLink {
                                signInView(host: host, computer: savedComputer(for: host))
                            } label: {
                                Label("Sign In to \(host)", systemImage: "person.badge.key")
                            }
                        }
                    }
                } else if listing.isRoots {
                    rootsSections(listing)
                } else {
                    Section {
                        if let hint = listing.hint {
                            Text(hint).font(.footnote).foregroundStyle(.secondary)
                        }
                        if listing.folders.isEmpty {
                            Text(listing.kind == "computer" ? "No shared folders listed." : "No folders inside this one.")
                                .foregroundStyle(.secondary)
                        }
                        ForEach(listing.folders) { folder in
                            folderButton(folder)
                        }
                    } header: {
                        Text(listing.kind == "computer" ? "Shared Folders" : "Folders")
                    } footer: {
                        if let count = listing.videoCount, count > 0 {
                            Text("\(scope.count(count)) directly in this folder.")
                        }
                    }
                }

                if listing.canMakeFolder {
                    newFolderSection(listing)
                }

                Section {
                    Button {
                        path = listing.path
                        dismiss()
                    } label: {
                        Label("Use This Folder", systemImage: "checkmark.circle")
                    }
                    .disabled(!listing.isPickable)
                } footer: {
                    if listing.kind == "computer" {
                        Text("Open one of this computer's shared folders first.")
                    } else if listing.isRoots {
                        Text("Open the folder your movies or shows are in.")
                    }
                }
            }
        }
        #if !os(tvOS)
        .formStyle(.grouped)
        #endif
        .navigationTitle("Choose a Folder")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .onChange(of: openAfterSignIn) { _, next in
            guard let next else { return }
            openAfterSignIn = nil
            Task { await load(next) }
        }
        .task {
            guard !started else { return }
            started = true
            await load(path.trimmingCharacters(in: .whitespacesAndNewlines), fallBackToRoots: true)
        }
    }

    @ViewBuilder
    private func rootsSections(_ listing: MediaBrowseListing) -> some View {
        Section("On This Server") {
            ForEach(listing.folders) { folder in
                folderButton(folder)
            }
        }
        Section {
            ForEach(listing.computers ?? []) { computer in
                HStack {
                    Button {
                        Task { await load(computer.path) }
                    } label: {
                        HStack {
                            Label(computer.name, systemImage: "desktopcomputer")
                            Spacer()
                            Text(computer.username.map { $0.isEmpty ? "guest" : "as \($0)" } ?? "guest")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                        .contentShape(Rectangle())
                    }
                    NavigationLink {
                        signInView(host: computer.host, computer: computer)
                    } label: {
                        Image(systemName: "person.badge.key")
                    }
                    .accessibilityLabel("Sign-in for \(computer.name)")
                }
                #if !os(tvOS)
                .buttonStyle(.borderless)
                #endif
            }
            NavigationLink {
                signInView(host: "", computer: nil)
            } label: {
                Label("Add a Computer…", systemImage: "plus")
            }
            Button {
                Task { await discover() }
            } label: {
                HStack {
                    Label("Find Computers", systemImage: "magnifyingglass")
                    if discovering { Spacer(); ProgressView() }
                }
            }
            .disabled(discovering)
            if let discovered {
                let unsaved = discovered.filter { $0.savedId == nil }
                if unsaved.isEmpty {
                    Text("No other computers answered. Windows PCs don't announce themselves - use Add a Computer with the PC's name or IP address.")
                        .font(.footnote).foregroundStyle(.secondary)
                }
                ForEach(unsaved) { found in
                    Button {
                        Task { await load(found.path) }
                    } label: {
                        HStack {
                            Label(found.name, systemImage: "desktopcomputer")
                            Spacer()
                            Text(found.address ?? found.host).font(.caption).foregroundStyle(.secondary)
                        }
                        .contentShape(Rectangle())
                    }
                }
            }
        } header: {
            Text("Network Computers")
        } footer: {
            Text("Folders shared from a Windows PC, a Mac with File Sharing on, or a NAS. The server reads them over the network itself - nothing to mount.")
        }
    }

    // The library's folder may not exist yet (a TV Shows folder beside Movies, Podcasts beside
    // Music): the server makes it inside the open folder and the picker opens it.
    private func newFolderSection(_ listing: MediaBrowseListing) -> some View {
        Section {
            LibraryTextField(label: "Name", prompt: scope == .audio ? "Podcasts" : "TV Shows", text: $newFolderName)
                .onSubmit { Task { await makeFolder(in: listing.path) } }
            Button {
                Task { await makeFolder(in: listing.path) }
            } label: {
                HStack {
                    Label("Create Folder Here", systemImage: "folder.badge.plus")
                    if makingFolder { Spacer(); ProgressView() }
                }
            }
            .disabled(makingFolder || newFolderName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            #if !os(tvOS)
            .buttonStyle(.borderless)
            #endif
            if let newFolderError {
                Text(newFolderError).foregroundStyle(SentinelTheme.red)
            }
        } header: {
            Text("New Folder")
        } footer: {
            Text("Makes a folder inside this one and opens it, for a library whose folder isn't there yet.")
        }
    }

    private func makeFolder(in parent: String) async {
        let name = newFolderName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty, !makingFolder else { return }
        makingFolder = true
        newFolderError = nil
        defer { makingFolder = false }
        do {
            let page = try await (scope == .audio
                ? model.client.audioMakeFolder(parent: parent, name: name)
                : model.client.mediaMakeFolder(parent: parent, name: name))
            newFolderName = ""
            listing = page
            typedPath = page.path
        } catch {
            newFolderError = error.localizedDescription
        }
    }

    // Pushed with a NavigationLink, never .navigationDestination: this screen is itself pushed by
    // a NavigationLink, and on macOS a destination declared here hung the app at 100% CPU the
    // moment it was presented (even with a bare Text as the destination). It hands back where to
    // go next through a binding for the same reason Choose Folder… takes one.
    private func signInView(host: String, computer: MediaNetworkComputer?) -> some View {
        MediaNetworkSignInView(host: host, computer: computer, openAfter: $openAfterSignIn, scope: scope)
    }

    private func folderButton(_ folder: MediaBrowseFolder) -> some View {
        Button {
            Task { await load(folder.path) }
        } label: {
            HStack {
                Label(folder.name, systemImage: listing?.kind == "computer" ? "externaldrive.connected.to.line.below" : "folder")
                    .lineLimit(1)
                Spacer()
                if let count = folder.videoCount, count > 0 {
                    Text(scope.count(count))
                        .font(.caption).foregroundStyle(.secondary)
                }
                Image(systemName: "chevron.right").font(.caption).foregroundStyle(.tertiary)
            }
            .contentShape(Rectangle())
        }
        #if !os(tvOS)
        .buttonStyle(.plain)
        #endif
    }

    private func savedComputer(for host: String) -> MediaNetworkComputer? {
        (listing?.computers ?? lastComputers).first { $0.host.caseInsensitiveCompare(host) == .orderedSame }
    }

    @State private var lastComputers: [MediaNetworkComputer] = []

    private func load(_ path: String, fallBackToRoots: Bool = false) async {
        loading = true
        errorMessage = nil
        defer { loading = false }
        do {
            let page = try await (scope == .audio ? model.client.audioBrowse(path: path) : model.client.mediaBrowse(path: path))
            // The picker opens on whatever the path box held, which may not exist any more;
            // then it opens on the starting page rather than on an error.
            if fallBackToRoots, !path.isEmpty, page.error != nil, page.needsLogin != true {
                await load("")
                return
            }
            if let computers = page.computers { lastComputers = computers }
            newFolderError = nil
            listing = page
            typedPath = page.path
        } catch {
            if fallBackToRoots, !path.isEmpty {
                await load("")
                return
            }
            errorMessage = error.localizedDescription
        }
    }

    private func discover() async {
        discovering = true
        defer { discovering = false }
        do {
            discovered = try await (scope == .audio ? model.client.discoverAudioComputers() : model.client.discoverMediaComputers())
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

// MARK: - Signing in to a computer

struct MediaNetworkSignInView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss

    let computer: MediaNetworkComputer?
    /// What the folder picker opens once this screen closes: the computer after signing in, the
    /// starting page ("") after forgetting it.
    @Binding var openAfter: String?
    let scope: MediaBrowseScope

    @State private var host: String
    @State private var displayName: String
    @State private var username: String
    @State private var password = ""
    @State private var isSaving = false
    @State private var errorMessage: String?
    @State private var confirmingForget = false

    init(host: String, computer: MediaNetworkComputer?, openAfter: Binding<String?>, scope: MediaBrowseScope = .media) {
        self.computer = computer
        self.scope = scope
        _openAfter = openAfter
        _host = State(initialValue: computer?.host ?? host)
        _displayName = State(initialValue: computer.map { $0.name == $0.host ? "" : $0.name } ?? "")
        _username = State(initialValue: computer?.username ?? "")
    }

    var body: some View {
        Form {
            Section {
                LibraryTextField(label: "Computer name or IP address", prompt: "DESKTOP-PC or 192.168.1.20", text: $host, monospaced: true)
                LibraryTextField(label: "Name to show (optional)", prompt: "Den PC", text: $displayName)
            }
            Section {
                LibraryTextField(label: "Username on that computer", prompt: "Blank to try as a guest", text: $username)
                LibraryTextField(
                    label: "Password",
                    prompt: computer?.hasPassword == true ? "Blank keeps the saved password" : "Password",
                    text: $password, secure: true)
            } footer: {
                Text("For a Windows PC, use the Windows account (a Microsoft account works: its email and password). On a Mac, turn on File Sharing in System Settings. Windows computers don't show up under Find Computers - type the PC's name or IP address.")
            }
            Section {
                Button {
                    Task { await save() }
                } label: {
                    HStack {
                        Text("Sign In")
                        if isSaving { Spacer(); ProgressView() }
                    }
                }
                .disabled(host.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || isSaving)
                if let errorMessage {
                    Text(errorMessage).foregroundStyle(SentinelTheme.red)
                }
            }
            if computer != nil {
                Section {
                    Button("Forget This Computer", role: .destructive) {
                        confirmingForget = true
                    }
                } footer: {
                    Text("Libraries on this computer stop scanning until you sign in to it again.")
                }
            }
        }
        #if !os(tvOS)
        .formStyle(.grouped)
        #endif
        .navigationTitle(computer == nil ? "Sign In to a Computer" : computer!.name)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .confirmationDialog("Forget \(computer?.name ?? "this computer")?", isPresented: $confirmingForget, titleVisibility: .visible) {
            Button("Forget", role: .destructive) {
                Task { await forget() }
            }
        }
    }

    private func save() async {
        isSaving = true
        errorMessage = nil
        defer { isSaving = false }
        let input = MediaNetworkComputerInput(
            host: host.trimmingCharacters(in: .whitespacesAndNewlines),
            name: displayName.trimmingCharacters(in: .whitespacesAndNewlines),
            username: username.trimmingCharacters(in: .whitespacesAndNewlines),
            password: password,
            domain: nil)
        do {
            let result = try await (scope == .audio ? model.client.saveAudioNetworkComputer(id: computer?.id, input) : model.client.saveMediaNetworkComputer(id: computer?.id, input))
            openAfter = result.computer.path
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func forget() async {
        guard let computer else { return }
        do {
            try await (scope == .audio ? model.client.deleteAudioNetworkComputer(id: computer.id) : model.client.deleteMediaNetworkComputer(id: computer.id))
            openAfter = ""
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

// A labelled text box that works the same in a Mac, iPhone and Apple TV form: the house
// SentinelTextField where it exists (a plain TextField in a grouped macOS Form right-aligns its
// text), a plain field on tvOS, where the system's own full-screen keyboard does the typing.
private struct LibraryTextField: View {
    let label: String
    var prompt: String = ""
    @Binding var text: String
    var secure = false
    var monospaced = false

    var body: some View {
        #if os(tvOS)
        if secure {
            SecureField(label, text: $text, prompt: Text(prompt))
        } else {
            TextField(label, text: $text, prompt: Text(prompt))
                .font(monospaced ? .body.monospaced() : .body)
                .autocorrectionDisabled()
                .textInputAutocapitalization(.never)
        }
        #else
        // `monospaced` only matters on tvOS: SentinelTextField styles its label and box as one.
        SentinelTextField(label: label, prompt: prompt, text: $text, secure: secure)
            .autocorrectionDisabled()
            #if os(iOS)
            .textInputAutocapitalization(.never)
            #endif
        #endif
    }
}
