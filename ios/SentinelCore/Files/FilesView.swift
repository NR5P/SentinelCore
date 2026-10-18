import ImageIO
import PhotosUI
import QuickLook
import SwiftUI
import UniformTypeIdentifiers

// The FILES section: the household's shared library on the server, browsed folder by folder.
// Anyone signed in can add, open, share, rename and trash files; the backup drives, the nightly
// pass and emptying the trash belong to an admin (FilesDrivesView, FilesTrashView). Only
// reachable when the server has Family Files switched on - see AppSection.visibleCases.
struct FilesView: View {
    @EnvironmentObject private var model: AppModel
    @StateObject private var session = FilesSession()

    var body: some View {
        FilesFolderView(path: "")
            .navigationDestination(for: FilesRoute.self) { route in
                Group {
                    switch route {
                    case .folder(let path): FilesFolderView(path: path)
                    case .file(let file): FileDetailView(file: file)
                    }
                }
                .environmentObject(session)
                .environmentObject(model)
            }
            .environmentObject(session)
            .task {
                // Files opened or shared last time are not worth keeping around.
                FilesTemporaryFiles.clear()
                await session.refreshStatus(model.client)
            }
            .onDisappear { session.stopPolling() }
    }
}

// Where the folder browser can go: deeper into the library, or onto one file.
enum FilesRoute: Hashable {
    case folder(String)
    case file(FamilyFileEntry)
}

// The library-wide screens, reached from the toolbar menu rather than from a row.
enum FilesLibraryRoute: String, Identifiable, Hashable {
    case drives, trash, duplicates
    var id: String { rawValue }
}

// One folder of the library. Folders first, then files; the search field looks through the
// whole library, not just this folder, because "that photo from the beach" rarely comes with a
// memory of where it was filed.
struct FilesFolderView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var session: FilesSession
    let path: String

    @State private var listing: FamilyFolderListing?
    @State private var isLoading = false
    @State private var loadError: String?
    @State private var searchText = ""
    @State private var searchResults: [FamilyFileEntry] = []
    @State private var searchError: String?
    @State private var showingPhotoPicker = false
    @State private var photoItems: [PhotosPickerItem] = []
    @State private var importing = false
    @State private var namingFolder = false
    @State private var newFolderName = ""
    @State private var libraryRoute: FilesLibraryRoute?

    private var isSearching: Bool { !searchText.trimmingCharacters(in: .whitespaces).isEmpty }

    var body: some View {
        List {
            if isSearching {
                searchSection
            } else {
                folderSections
            }
        }
        .navigationTitle(FamilyFilesFormat.folderTitle(path))
        #if os(iOS)
        .navigationBarTitleDisplayMode(path.isEmpty ? .large : .inline)
        #endif
        .searchable(text: $searchText, prompt: "Search the library")
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Menu {
                    Button { showingPhotoPicker = true } label: { Label("Add Photos or Videos", systemImage: "photo.on.rectangle") }
                    Button { importing = true } label: { Label("Add Files", systemImage: "doc.badge.plus") }
                    Button {
                        newFolderName = ""
                        namingFolder = true
                    } label: { Label("New Folder", systemImage: "folder.badge.plus") }
                    Divider()
                    Button { libraryRoute = .drives } label: { Label("Backup Drives", systemImage: "externaldrive") }
                    Button { libraryRoute = .trash } label: { Label("Trash", systemImage: "trash") }
                    if session.isAdmin {
                        Button { libraryRoute = .duplicates } label: { Label("Duplicates", systemImage: "doc.on.doc") }
                    }
                } label: {
                    Label("Add", systemImage: "plus")
                }
            }
        }
        .safeAreaInset(edge: .top, spacing: 0) { FilesBanners() }
        .refreshable {
            await load()
            await session.refreshStatus(model.client)
        }
        .task(id: session.version) { await load() }
        .task(id: searchText) { await search() }
        .photosPicker(isPresented: $showingPhotoPicker, selection: $photoItems, maxSelectionCount: 50, matching: .any(of: [.images, .videos]))
        .onChange(of: photoItems) { _, items in
            guard !items.isEmpty else { return }
            let chosen = items
            photoItems = []
            session.enqueue(pickerItems: chosen, into: path, client: model.client)
        }
        .fileImporter(isPresented: $importing, allowedContentTypes: [.item], allowsMultipleSelection: true) { result in
            guard case let .success(urls) = result else { return }
            session.enqueue(fileURLs: urls, into: path, client: model.client)
        }
        .alert("New Folder", isPresented: $namingFolder) {
            TextField("Name", text: $newFolderName)
            Button("Create") { Task { await createFolder() } }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(path.isEmpty ? "A folder at the top of the library." : "A folder inside \(FamilyFilesFormat.folderTitle(path)).")
        }
        .navigationDestination(item: $libraryRoute) { route in
            Group {
                switch route {
                case .drives: FilesDrivesView()
                case .trash: FilesTrashView()
                case .duplicates: FilesDuplicatesView()
                }
            }
            .environmentObject(session)
            .environmentObject(model)
        }
    }

    @ViewBuilder private var folderSections: some View {
        if let loadError, listing == nil {
            ContentUnavailableView {
                Label("Couldn't Load This Folder", systemImage: "exclamationmark.triangle")
            } description: {
                Text(loadError)
            } actions: {
                Button("Try Again") { Task { await load() } }
            }
        } else if let listing {
            if !listing.folders.isEmpty {
                Section {
                    ForEach(listing.folders) { folder in
                        NavigationLink(value: FilesRoute.folder(folder.path)) {
                            Label {
                                Text(folder.name)
                            } icon: {
                                Image(systemName: "folder.fill").foregroundStyle(SentinelTheme.cyan)
                            }
                        }
                        .swipeActions(edge: .trailing) {
                            Button(role: .destructive) { Task { await trash(folder.path, name: folder.name) } } label: {
                                Label("Trash", systemImage: "trash")
                            }
                        }
                        .contextMenu {
                            Button(role: .destructive) { Task { await trash(folder.path, name: folder.name) } } label: {
                                Label("Move to Trash", systemImage: "trash")
                            }
                        }
                    }
                } header: {
                    Text("Folders")
                }
            }
            if !listing.files.isEmpty {
                Section {
                    ForEach(listing.files) { file in
                        fileRow(file)
                    }
                } header: {
                    Text(listing.files.count == 1 ? "1 file" : "\(listing.files.count) files")
                }
            }
            if listing.folders.isEmpty && listing.files.isEmpty {
                ContentUnavailableView {
                    Label("Nothing Here Yet", systemImage: "folder")
                } description: {
                    Text("Use the + button to add photos, videos or files, or make a folder.")
                }
            }
        } else if isLoading {
            HStack { Spacer(); ProgressView(); Spacer() }
        }
    }

    @ViewBuilder private var searchSection: some View {
        if let searchError {
            Text(searchError).foregroundStyle(SentinelTheme.red)
        } else if searchResults.isEmpty {
            ContentUnavailableView.search(text: searchText)
        } else {
            Section {
                ForEach(searchResults) { file in
                    fileRow(file, showFolder: true)
                }
            } header: {
                Text(searchResults.count == 1 ? "1 match" : "\(searchResults.count) matches")
            }
        }
    }

    private func fileRow(_ file: FamilyFileEntry, showFolder: Bool = false) -> some View {
        NavigationLink(value: FilesRoute.file(file)) {
            FilesFileRow(file: file, showFolder: showFolder)
        }
        .swipeActions(edge: .trailing) {
            Button(role: .destructive) { Task { await trash(file.path, name: file.name) } } label: {
                Label("Trash", systemImage: "trash")
            }
        }
        .contextMenu {
            Button(role: .destructive) { Task { await trash(file.path, name: file.name) } } label: {
                Label("Move to Trash", systemImage: "trash")
            }
        }
    }

    private func load() async {
        isLoading = true
        defer { isLoading = false }
        do {
            listing = try await model.client.familyFolder(path: path)
            loadError = nil
        } catch {
            if error is CancellationError || (error as? URLError)?.code == .cancelled { return }
            loadError = error.localizedDescription
        }
    }

    private func search() async {
        let query = searchText.trimmingCharacters(in: .whitespaces)
        guard !query.isEmpty else {
            searchResults = []
            searchError = nil
            return
        }
        // A short pause so a word being typed is one request, not six.
        try? await Task.sleep(for: .milliseconds(300))
        guard !Task.isCancelled else { return }
        do {
            searchResults = try await model.client.familySearch(query)
            searchError = nil
        } catch {
            if error is CancellationError || (error as? URLError)?.code == .cancelled { return }
            searchError = error.localizedDescription
        }
    }

    private func createFolder() async {
        let name = newFolderName.trimmingCharacters(in: .whitespaces)
        guard !name.isEmpty else { return }
        do {
            _ = try await model.client.familyCreateFolder(path: FamilyFilesFormat.join(path, name))
            session.changed()
        } catch {
            session.error = error.localizedDescription
        }
    }

    private func trash(_ target: String, name: String) async {
        do {
            let result = try await model.client.familyDelete(path: target)
            session.lastUploadNote = result.trashed == 1 ? "Moved \(name) to the trash." : "Moved \(result.trashed) files to the trash."
            session.changed()
        } catch {
            session.error = error.localizedDescription
        }
    }
}

// MARK: - rows and thumbnails

struct FilesFileRow: View {
    let file: FamilyFileEntry
    var showFolder = false

    var body: some View {
        HStack(spacing: 12) {
            if file.isPicture {
                FilesThumbnail(file: file, maxPixels: 120)
                    .frame(width: 44, height: 44)
                    .clipShape(RoundedRectangle(cornerRadius: 6))
            } else {
                Image(systemName: FilesIcons.symbol(for: file))
                    .font(.title2)
                    .foregroundStyle(.secondary)
                    .frame(width: 44, height: 44)
            }
            VStack(alignment: .leading, spacing: 2) {
                Text(file.name).lineLimit(1)
                HStack(spacing: 6) {
                    Text(FamilyFilesFormat.bytes(file.size))
                    if let date = file.modifiedUtc {
                        Text("·")
                        Text(FamilyFilesFormat.date(date))
                    }
                }
                .font(.caption)
                .foregroundStyle(.secondary)
                if showFolder {
                    Text(file.folder.isEmpty ? "Family Files" : file.folder)
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                        .lineLimit(1)
                }
            }
        }
    }
}

enum FilesIcons {
    static func symbol(for file: FamilyFileEntry) -> String {
        let type = file.contentType ?? ""
        if file.isPicture || type.hasPrefix("image/") { return "photo" }
        if type.hasPrefix("video/") { return "film" }
        if type.hasPrefix("audio/") { return "music.note" }
        if type == "application/pdf" { return "doc.richtext" }
        if type.contains("zip") || type.contains("compressed") { return "doc.zipper" }
        if type.hasPrefix("text/") { return "doc.text" }
        return "doc"
    }
}

// The server's JPEG thumbnail of an image file, decoded down to the size it is shown at and
// kept in a small cache so scrolling back through a folder does not fetch it all again.
struct FilesThumbnail: View {
    @EnvironmentObject private var model: AppModel
    let file: FamilyFileEntry
    var maxPixels = 400

    @State private var image: CGImage?
    @State private var failed = false

    var body: some View {
        ZStack {
            Rectangle().fill(.quaternary)
            if let image {
                Image(decorative: image, scale: 1)
                    .resizable()
                    .scaledToFill()
            } else {
                Image(systemName: failed ? "photo.badge.exclamationmark" : "photo")
                    .foregroundStyle(.secondary)
            }
        }
        .clipped()
        .task(id: cacheKey) {
            if let cached = FilesThumbnailCache.shared.image(for: cacheKey) {
                image = cached
                return
            }
            do {
                let data = try await model.client.familyThumbnail(path: file.path)
                guard let decoded = FilesThumbnailCache.decode(data, maxPixels: maxPixels) else {
                    failed = true
                    return
                }
                FilesThumbnailCache.shared.store(decoded, for: cacheKey)
                image = decoded
            } catch {
                if error is CancellationError || (error as? URLError)?.code == .cancelled { return }
                failed = true
            }
        }
    }

    private var cacheKey: String { "\(file.path)|\(file.sha256 ?? "")|\(file.modifiedUtc?.timeIntervalSince1970 ?? 0)|\(maxPixels)" }
}

final class FilesThumbnailCache: @unchecked Sendable {
    static let shared = FilesThumbnailCache()
    private let cache = NSCache<NSString, CGImage>()

    private init() { cache.countLimit = 400 }

    func image(for key: String) -> CGImage? { cache.object(forKey: key as NSString) }
    func store(_ image: CGImage, for key: String) { cache.setObject(image, forKey: key as NSString) }

    static func decode(_ data: Data, maxPixels: Int) -> CGImage? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil) else { return nil }
        return CGImageSourceCreateThumbnailAtIndex(source, 0, [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixels,
        ] as CFDictionary)
    }
}

// MARK: - banners

// What is going on right now, above every folder: files going up from this device, and the job
// the server is running (a backup, a restore, the duplicate check). Notes and errors from the
// last thing that happened sit here too, until dismissed.
struct FilesBanners: View {
    @EnvironmentObject private var session: FilesSession

    var body: some View {
        VStack(spacing: 8) {
            if let upload = session.upload {
                banner(color: SentinelTheme.cyan) {
                    ProgressView().controlSize(.small)
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Uploading \(upload.index) of \(upload.total): \(upload.name)").lineLimit(1)
                        Text("Big videos take a while - keep the app open.").font(.caption2).foregroundStyle(.secondary)
                    }
                }
            }
            if let job = session.job {
                banner(color: SentinelTheme.amber) {
                    ProgressView().controlSize(.small)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(job.title).lineLimit(1)
                        if !job.message.isEmpty {
                            Text(job.message).font(.caption2).foregroundStyle(.secondary).lineLimit(2)
                        }
                        if job.total > 0 {
                            ProgressView(value: Double(job.done), total: Double(job.total))
                            Text("\(job.done) of \(job.total)").font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                }
            }
            if let note = session.lastUploadNote {
                banner(color: SentinelTheme.green) {
                    Image(systemName: "checkmark.circle.fill").foregroundStyle(SentinelTheme.green)
                    Text(note).lineLimit(2)
                    Spacer(minLength: 0)
                    Button { session.lastUploadNote = nil } label: { Image(systemName: "xmark") }
                        .buttonStyle(.plain)
                        .foregroundStyle(.secondary)
                }
            }
            // The library lives on another computer that can't be reached right now: every
            // listing fails until it's back, so say why rather than leaving an empty folder.
            if let problem = libraryProblem {
                banner(color: SentinelTheme.amber) {
                    Image(systemName: "desktopcomputer.trianglebadge.exclamationmark").foregroundStyle(SentinelTheme.amber)
                    VStack(alignment: .leading, spacing: 2) {
                        Text("The library's computer can't be reached").fontWeight(.semibold)
                        Text(problem).foregroundStyle(.secondary).lineLimit(3)
                    }
                }
            }
            if let error = session.error {
                banner(color: SentinelTheme.red) {
                    Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(SentinelTheme.red)
                    Text(error).lineLimit(3)
                    Spacer(minLength: 0)
                    Button { session.error = nil } label: { Image(systemName: "xmark") }
                        .buttonStyle(.plain)
                        .foregroundStyle(.secondary)
                }
            }
        }
        .padding(.horizontal)
        .padding(.top, session.upload != nil || session.job != nil || session.lastUploadNote != nil || session.error != nil || libraryProblem != nil ? 8 : 0)
        .animation(.easeInOut(duration: 0.2), value: session.upload)
        .animation(.easeInOut(duration: 0.2), value: session.job)
    }

    private var libraryProblem: String? {
        guard let problem = session.status?.libraryProblem, !problem.isEmpty else { return nil }
        return problem
    }

    private func banner<Content: View>(color: Color, @ViewBuilder content: () -> Content) -> some View {
        HStack(alignment: .center, spacing: 10) { content() }
            .font(.caption)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            .background(color.opacity(0.12), in: RoundedRectangle(cornerRadius: 10))
            .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(color.opacity(0.35)))
    }
}

// MARK: - one file

// A file's details, and what can be done with it: open it in Quick Look, share it (on the Mac,
// also save a copy), give it a new name, or send it to the trash. Opening or sharing fetches
// the file into a temporary folder first; the same copy serves both.
struct FileDetailView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var session: FilesSession
    @Environment(\.dismiss) private var dismiss

    @State private var file: FamilyFileEntry
    @State private var localURL: URL?
    @State private var preview: URL?
    @State private var isDownloading = false
    @State private var isBusy = false
    @State private var error: String?
    @State private var renaming = false
    @State private var newName = ""
    @State private var confirmingTrash = false
    #if os(macOS)
    @State private var exporting = false
    #endif

    init(file: FamilyFileEntry) {
        _file = State(initialValue: file)
    }

    var body: some View {
        Form {
            if file.isPicture {
                Section {
                    FilesThumbnail(file: file, maxPixels: 800)
                        .frame(maxWidth: .infinity)
                        .frame(height: 220)
                        .clipShape(RoundedRectangle(cornerRadius: 10))
                        .listRowInsets(EdgeInsets())
                        .onTapGesture { Task { await open() } }
                }
            }
            Section("Details") {
                LabeledContent("Name", value: file.name)
                LabeledContent("Size", value: FamilyFilesFormat.bytes(file.size))
                if let date = file.modifiedUtc {
                    LabeledContent("Date", value: FamilyFilesFormat.date(date))
                }
                LabeledContent("Folder", value: file.folder.isEmpty ? "Family Files" : file.folder)
                if let type = file.contentType, !type.isEmpty {
                    LabeledContent("Type", value: type)
                }
            }
            Section {
                Button {
                    Task { await open() }
                } label: {
                    HStack {
                        Label("Open", systemImage: "eye")
                        Spacer()
                        if isDownloading { ProgressView().controlSize(.small) }
                    }
                }
                .disabled(isDownloading)
                if let localURL {
                    ShareLink(item: localURL) { Label("Share", systemImage: "square.and.arrow.up") }
                    #if os(macOS)
                    Button { exporting = true } label: { Label("Save a Copy…", systemImage: "square.and.arrow.down") }
                    #endif
                } else {
                    Button {
                        Task { _ = await download() }
                    } label: {
                        Label("Download to Share", systemImage: "arrow.down.circle")
                    }
                    .disabled(isDownloading)
                }
            } footer: {
                if localURL != nil {
                    Text("A copy is on this device until the app is next opened.")
                }
            }
            Section {
                Button {
                    newName = file.name
                    renaming = true
                } label: {
                    Label("Rename", systemImage: "pencil")
                }
                Button(role: .destructive) {
                    confirmingTrash = true
                } label: {
                    Label("Move to Trash", systemImage: "trash")
                }
            } footer: {
                Text("Trashed files can be put back from the Trash screen until it is emptied.")
            }
            if let error {
                Section {
                    Text(error).foregroundStyle(SentinelTheme.red)
                }
            }
        }
        .formStyle(.grouped)
        .navigationTitle(file.name)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .disabled(isBusy)
        .quickLookPreview($preview)
        .alert("Rename", isPresented: $renaming) {
            TextField("Name", text: $newName)
            Button("Rename") { Task { await rename() } }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("The file stays in the same folder.")
        }
        .confirmationDialog("Move \(file.name) to the trash?", isPresented: $confirmingTrash, titleVisibility: .visible) {
            Button("Move to Trash", role: .destructive) { Task { await trash() } }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("It can be put back from the Trash screen.")
        }
        #if os(macOS)
        .fileExporter(
            isPresented: $exporting,
            document: localURL.map { ExportedFile(url: $0) },
            contentType: UTType(filenameExtension: (file.name as NSString).pathExtension) ?? .data,
            defaultFilename: file.name
        ) { result in
            if case let .failure(failure) = result { error = failure.localizedDescription }
        }
        #endif
    }

    private func download() async -> URL? {
        if let localURL { return localURL }
        isDownloading = true
        defer { isDownloading = false }
        do {
            let url = try await model.client.familyDownload(file)
            localURL = url
            error = nil
            return url
        } catch {
            self.error = error.localizedDescription
            return nil
        }
    }

    private func open() async {
        guard let url = await download() else { return }
        preview = url
    }

    private func rename() async {
        let name = newName.trimmingCharacters(in: .whitespaces)
        guard !name.isEmpty, name != file.name else { return }
        isBusy = true
        defer { isBusy = false }
        do {
            let path = try await model.client.familyMove(from: file.path, to: FamilyFilesFormat.join(file.folder, name))
            file = FamilyFileEntry(path: path, name: name, size: file.size, modifiedUtc: file.modifiedUtc,
                                   sha256: file.sha256, isImage: file.isImage, contentType: file.contentType)
            // The copy on disk carries the old name; the next open fetches it afresh.
            localURL = nil
            error = nil
            session.changed()
        } catch {
            self.error = error.localizedDescription
        }
    }

    private func trash() async {
        isBusy = true
        defer { isBusy = false }
        do {
            _ = try await model.client.familyDelete(path: file.path)
            session.lastUploadNote = "Moved \(file.name) to the trash."
            session.changed()
            dismiss()
        } catch {
            self.error = error.localizedDescription
        }
    }
}

#if os(macOS)
// Hands a downloaded file to the save panel. Wrapping the file on disk rather than reading it
// into a Data keeps a large video out of memory.
struct ExportedFile: FileDocument {
    static var readableContentTypes: [UTType] { [.data] }
    static var writableContentTypes: [UTType] { [.data] }

    let url: URL

    init(url: URL) { self.url = url }

    init(configuration: ReadConfiguration) throws {
        throw CocoaError(.fileReadUnsupportedScheme)
    }

    func fileWrapper(configuration: WriteConfiguration) throws -> FileWrapper {
        try FileWrapper(url: url, options: [])
    }
}
#endif
