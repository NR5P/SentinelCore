import ImageIO
import SwiftUI

// The household's file library on the television: browse the folders, look at the pictures big,
// and see how the backup drives are doing. Viewing only - adding, renaming and trashing stay on
// the phones, computers and dashboard, where there is a keyboard and a file picker. Only shown
// when the server has Family Files switched on (AppSection.visibleCases).
struct FilesTVView: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        FilesTVFolder(path: "")
            .navigationDestination(for: FilesTVRoute.self) { route in
                switch route {
                case .folder(let path): FilesTVFolder(path: path)
                }
            }
    }
}

enum FilesTVRoute: Hashable {
    case folder(String)
}

struct FilesTVFolder: View {
    @EnvironmentObject private var model: AppModel
    let path: String

    @State private var listing: FamilyFolderListing?
    @State private var status: FamilyFilesStatus?
    @State private var error: String?
    @State private var isLoading = false
    @State private var shownPicture: FamilyFileEntry?

    private var isRoot: Bool { path.isEmpty }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 40) {
                if let problem = status?.libraryProblem, !problem.isEmpty {
                    HStack(spacing: 16) {
                        Image(systemName: "desktopcomputer.trianglebadge.exclamationmark")
                            .font(.title2)
                            .foregroundStyle(SentinelTheme.amber)
                        VStack(alignment: .leading, spacing: 4) {
                            Text("The library's computer can't be reached").font(.headline)
                            Text(problem).font(.caption).foregroundStyle(.secondary).lineLimit(3)
                        }
                    }
                    .padding(24)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 16))
                }
                if let error, listing == nil {
                    ContentUnavailableView {
                        Label("Couldn't Load the Library", systemImage: "exclamationmark.triangle")
                    } description: {
                        Text(error)
                    } actions: {
                        Button("Try Again") { Task { await load() } }
                    }
                } else if let listing {
                    if listing.folders.isEmpty && listing.files.isEmpty {
                        ContentUnavailableView(
                            "Nothing Here Yet",
                            systemImage: "folder",
                            description: Text("Add photos, videos and files from the iPhone or Mac app, Android, or the dashboard. They show up here.")
                        )
                        .frame(maxWidth: .infinity)
                    } else {
                        LazyVGrid(columns: [GridItem(.adaptive(minimum: 360), spacing: 40)], spacing: 40) {
                            ForEach(listing.folders) { folder in
                                NavigationLink(value: FilesTVRoute.folder(folder.path)) {
                                    FolderCard(folder: folder)
                                }
                                .buttonStyle(.card)
                            }
                            ForEach(listing.files) { file in
                                Button {
                                    if file.isPicture { shownPicture = file }
                                } label: {
                                    FileCard(file: file)
                                }
                                .buttonStyle(.card)
                            }
                        }
                    }
                } else if isLoading {
                    ProgressView().frame(maxWidth: .infinity)
                }
                if isRoot, let status {
                    DrivesTVSection(status: status)
                }
                if let job = status?.job, isRoot {
                    HStack(spacing: 16) {
                        ProgressView()
                        VStack(alignment: .leading, spacing: 4) {
                            Text(jobTitle(job)).font(.headline)
                            if !job.message.isEmpty { Text(job.message).font(.caption).foregroundStyle(.secondary) }
                            if job.total > 0 { Text("\(job.done) of \(job.total)").font(.caption).foregroundStyle(.secondary) }
                        }
                    }
                    .padding(24)
                    .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 16))
                }
            }
            .padding(.horizontal, 60)
            .padding(.vertical, 30)
        }
        .navigationTitle(FamilyFilesFormat.folderTitle(path))
        .toolbar {
            ToolbarItem {
                Button { Task { await load() } } label: {
                    Image(systemName: "arrow.clockwise").accessibilityLabel("Refresh")
                }
                .disabled(isLoading)
            }
        }
        .task { await load() }
        .fullScreenCover(item: $shownPicture) { file in
            FilesTVPictureViewer(file: file).environmentObject(model)
        }
    }

    private func load() async {
        isLoading = true
        defer { isLoading = false }
        do {
            listing = try await model.client.familyFolder(path: path)
            error = nil
        } catch {
            if error is CancellationError || (error as? URLError)?.code == .cancelled { return }
            self.error = error.localizedDescription
        }
        // Every folder, not just the top: a library on another computer can drop off anywhere,
        // and the status is what says why.
        status = try? await model.client.familyFilesStatus()
    }

    private func jobTitle(_ job: FamilyFilesJob) -> String {
        switch job.kind {
        case "nightly": "Nightly pass running"
        case "sync": job.driveName.map { "Backing up to \($0)" } ?? "Backing up"
        case "restore": job.driveName.map { "Bringing files back from \($0)" } ?? "Bringing files back"
        case "duplicates": "Checking for duplicates"
        case "index": "Indexing the library"
        default: job.kind.capitalized
        }
    }
}

private struct FolderCard: View {
    let folder: FamilyFolderEntry

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Image(systemName: "folder.fill")
                .font(.system(size: 64))
                .foregroundStyle(SentinelTheme.cyan)
                .frame(maxWidth: .infinity, minHeight: 180)
            Text(folder.name).font(.headline).lineLimit(1)
            Text("Folder").font(.caption).foregroundStyle(.secondary)
        }
        .padding(24)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

private struct FileCard: View {
    let file: FamilyFileEntry

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if file.isPicture {
                FilesTVThumbnail(file: file)
                    .frame(maxWidth: .infinity)
                    .frame(height: 180)
                    .clipShape(RoundedRectangle(cornerRadius: 10))
            } else {
                Image(systemName: symbol)
                    .font(.system(size: 56))
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, minHeight: 180)
            }
            Text(file.name).font(.headline).lineLimit(1)
            HStack(spacing: 8) {
                Text(FamilyFilesFormat.bytes(file.size))
                if let date = file.modifiedUtc {
                    Text("·")
                    Text(FamilyFilesFormat.date(date))
                }
            }
            .font(.caption)
            .foregroundStyle(.secondary)
            .lineLimit(1)
        }
        .padding(24)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var symbol: String {
        let type = file.contentType ?? ""
        if type.hasPrefix("video/") { return "film" }
        if type.hasPrefix("audio/") { return "music.note" }
        if type == "application/pdf" { return "doc.richtext" }
        if type.contains("zip") || type.contains("compressed") { return "doc.zipper" }
        if type.hasPrefix("text/") { return "doc.text" }
        return "doc"
    }
}

// The server's JPEG thumbnail, decoded down to card size.
private struct FilesTVThumbnail: View {
    @EnvironmentObject private var model: AppModel
    let file: FamilyFileEntry
    @State private var image: Image?

    var body: some View {
        ZStack {
            Rectangle().fill(.quaternary)
            if let image {
                image.resizable().scaledToFill()
            } else {
                Image(systemName: "photo").foregroundStyle(.secondary)
            }
        }
        .clipped()
        .task(id: file.path) {
            guard let data = try? await model.client.familyThumbnail(path: file.path),
                  let cgImage = FilesTVImages.decode(data, maxPixels: 640) else { return }
            image = Image(decorative: cgImage, scale: 1)
        }
    }
}

// One picture, full screen. The Menu button on the remote closes it.
struct FilesTVPictureViewer: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss
    let file: FamilyFileEntry

    @State private var image: Image?
    @State private var error: String?

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            if let image {
                image.resizable().scaledToFit().ignoresSafeArea()
            } else if let error {
                ContentUnavailableView {
                    Label("Couldn't Show \(file.name)", systemImage: "photo.badge.exclamationmark")
                } description: {
                    Text(error)
                }
            } else {
                ProgressView()
            }
            VStack {
                Spacer()
                HStack {
                    Text(file.name).font(.caption).foregroundStyle(.secondary)
                    Spacer()
                    Text("Press Menu to close").font(.caption).foregroundStyle(.secondary)
                }
                .padding(40)
            }
        }
        .onExitCommand { dismiss() }
        .task {
            do {
                let data = try await model.client.familyFileData(path: file.path)
                guard let cgImage = FilesTVImages.decode(data, maxPixels: 2160) else {
                    error = "That file isn't a picture this TV can show."
                    return
                }
                image = Image(decorative: cgImage, scale: 1)
            } catch {
                self.error = error.localizedDescription
            }
        }
    }
}

// The backup drives, read-only: name, where it points, and how the last backup went.
private struct DrivesTVSection: View {
    let status: FamilyFilesStatus

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            Text("Backup Drives").font(.headline).foregroundStyle(SentinelTheme.cyan)
            if let drives = status.drives, !drives.isEmpty {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 520), spacing: 40)], spacing: 40) {
                    ForEach(drives) { drive in
                        Button {} label: {
                            VStack(alignment: .leading, spacing: 8) {
                                HStack(spacing: 12) {
                                    Image(systemName: drive.isSftp ? "desktopcomputer" : "externaldrive")
                                        .foregroundStyle(drive.enabled ? SentinelTheme.cyan : Color.secondary)
                                    Text(drive.name).font(.headline)
                                    if !drive.enabled { Text("Off").font(.caption).foregroundStyle(.secondary) }
                                    Spacer()
                                    Circle().fill(resultColor(drive.lastResult ?? "")).frame(width: 12, height: 12)
                                }
                                if let location = drive.location, !location.isEmpty {
                                    Text(location).font(.caption.monospaced()).foregroundStyle(.secondary).lineLimit(1)
                                }
                                if let attempt = drive.lastAttemptUtc {
                                    Text("\(resultLabel(drive.lastResult ?? "")) · \(FamilyFilesFormat.date(attempt))")
                                        .font(.caption).foregroundStyle(.secondary)
                                } else {
                                    Text("Not backed up yet").font(.caption).foregroundStyle(.secondary)
                                }
                                if let message = drive.lastMessage, !message.isEmpty {
                                    Text(message).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                                }
                            }
                            .padding(24)
                            .frame(maxWidth: .infinity, alignment: .leading)
                        }
                        .buttonStyle(.card)
                    }
                }
            } else {
                Text("No backup drives are set up. Add them from the iPhone or Mac app, or the dashboard.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            if let library = status.library {
                Text("\(library.files) files, \(FamilyFilesFormat.bytes(library.bytes)) in the library")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            if let nightly = status.nightly, let last = nightly.lastRunUtc {
                Text("Nightly pass: \(resultLabel(nightly.lastResult ?? "")) · \(FamilyFilesFormat.date(last))\(nightly.lastMessage.map { $0.isEmpty ? "" : " · \($0)" } ?? "")")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
            }
        }
    }

    private func resultColor(_ result: String) -> Color {
        switch result {
        case "ok": SentinelTheme.green
        case "failed": SentinelTheme.red
        case "incomplete": SentinelTheme.amber
        default: .secondary
        }
    }

    private func resultLabel(_ result: String) -> String {
        switch result {
        case "ok": "Backed up"
        case "failed": "Failed"
        case "incomplete": "Incomplete"
        default: "Last attempt"
        }
    }
}

enum FilesTVImages {
    static func decode(_ data: Data, maxPixels: Int) -> CGImage? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil) else { return nil }
        return CGImageSourceCreateThumbnailAtIndex(source, 0, [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixels,
        ] as CFDictionary)
    }
}
