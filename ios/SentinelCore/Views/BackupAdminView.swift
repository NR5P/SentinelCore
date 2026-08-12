import SwiftUI
import UniformTypeIdentifiers

// Exporting the whole configuration to one file and restoring from one - the web dashboard's
// BACKUP section. Worth doing before any big change, and once you first have things working the
// way you like.
//
// The backup travels through this app as opaque bytes, never decoded into a Swift type: it is
// whatever the server considers its whole configuration, and a client that parsed it would
// quietly drop any section added after this app was built. Restoring sends back exactly what was
// exported.
struct BackupAdminView: View {
    @EnvironmentObject private var model: AppModel

    @State private var exported: BackupFile?
    @State private var isExporting = false
    @State private var isPicking = false
    @State private var isRestoring = false
    @State private var confirmingRestore: Data?
    @State private var statusMessage: String?
    @State private var errorMessage: String?

    var body: some View {
        Form {
            Section {
                Button(isExporting ? "Exporting…" : "Export Backup…") { Task { await export() } }
                    .disabled(isExporting)
            } header: {
                Text("Export")
            } footer: {
                Text("Saves everything configurable here — settings, cameras, camera groups, trigger zones, gate checks, user accounts, known vehicles, prompt profiles — into one file you can restore later or move to another machine.")
            }

            Section {
                Button(isRestoring ? "Restoring…" : "Restore From File…") { isPicking = true }
                    .disabled(isRestoring)
            } header: {
                Text("Restore")
            } footer: {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Nothing in the current setup that isn't mentioned in the backup is touched or removed.")
                    if let statusMessage {
                        Text(statusMessage).foregroundStyle(SentinelTheme.green)
                    }
                    if let errorMessage {
                        Text(errorMessage).foregroundStyle(SentinelTheme.red)
                    }
                }
            }
        }
        .formStyle(.grouped)
        .navigationTitle("Backup")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .fileImporter(
            isPresented: $isPicking,
            allowedContentTypes: [.json, .data],
            allowsMultipleSelection: false
        ) { result in
            stage(result)
        }
        // Exported through the system's own save dialog rather than written somewhere this app
        // chose: a backup the owner can't find is no backup at all.
        .fileExporter(
            isPresented: Binding(get: { exported != nil }, set: { if !$0 { exported = nil } }),
            document: exported,
            contentType: .json,
            defaultFilename: Self.defaultFilename()
        ) { result in
            if case .failure(let error) = result {
                errorMessage = error.localizedDescription
            } else {
                statusMessage = "Backup saved."
            }
        }
        .confirmationDialog(
            "Restore this backup? Settings, cameras, trigger zones, and accounts in the file replace what's on the server now.",
            isPresented: Binding(get: { confirmingRestore != nil }, set: { if !$0 { confirmingRestore = nil } }),
            titleVisibility: .visible
        ) {
            Button("Restore", role: .destructive) {
                if let contents = confirmingRestore {
                    confirmingRestore = nil
                    Task { await restore(contents) }
                }
            }
            Button("Cancel", role: .cancel) { confirmingRestore = nil }
        }
    }

    private static func defaultFilename() -> String {
        let today = Date().formatted(.iso8601.year().month().day().dateSeparator(.dash))
        return "sentinelcore-backup-\(today)"
    }

    private func export() async {
        isExporting = true
        statusMessage = nil
        errorMessage = nil
        defer { isExporting = false }
        do {
            exported = BackupFile(data: try await model.client.exportBackup())
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    // Reads the file and holds it for confirmation rather than restoring on pick - choosing a file
    // in a system dialog isn't consent to overwrite a working configuration.
    private func stage(_ result: Result<[URL], Error>) {
        statusMessage = nil
        errorMessage = nil
        do {
            guard let url = try result.get().first else { return }
            let scoped = url.startAccessingSecurityScopedResource()
            defer { if scoped { url.stopAccessingSecurityScopedResource() } }
            confirmingRestore = try Data(contentsOf: url)
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func restore(_ contents: Data) async {
        isRestoring = true
        defer { isRestoring = false }
        do {
            let result = try await model.client.importBackup(contents)
            statusMessage = "Restored \(result.summary)."
            // The restore changed cameras, accounts and settings underneath the running app.
            try? await model.refreshAll()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

// The exported bytes wrapped for fileExporter, which insists on a FileDocument even when the
// document is just a blob the server produced.
private struct BackupFile: FileDocument {
    static var readableContentTypes: [UTType] { [.json] }

    let data: Data

    init(data: Data) { self.data = data }

    init(configuration: ReadConfiguration) throws {
        data = configuration.file.regularFileContents ?? Data()
    }

    func fileWrapper(configuration: WriteConfiguration) throws -> FileWrapper {
        FileWrapper(regularFileWithContents: data)
    }
}
