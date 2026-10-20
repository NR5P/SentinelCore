import SwiftUI
import UniformTypeIdentifiers

// Installing the .lic file emailed after purchase, and saying whether one is installed. The web
// dashboard has had this since licensing shipped; this is the same two operations on the phone and
// the Mac, so a licence can be installed from whichever device the email was opened on.
//
// Nothing here is urgent by design: an unlicensed install keeps every feature and only shows a
// watermark, so this screen never blocks or nags beyond stating the position.
struct LicenseAdminView: View {
    @EnvironmentObject private var model: AppModel

    @State private var state: LicenseState?
    @State private var statusMessage: String?
    @State private var errorMessage: String?
    @State private var isPicking = false
    @State private var isInstalling = false

    var body: some View {
        Form {
            Section {
                if let state {
                    if state.valid {
                        LabeledContent("Status", value: "Licensed")
                        if let customerName = state.customerName {
                            LabeledContent("Licensed to", value: customerName)
                        }
                    } else {
                        LabeledContent("Status", value: "Unlicensed")
                    }
                } else {
                    Text("Checking license…").foregroundStyle(.secondary)
                }
            } header: {
                Text("License")
            } footer: {
                // No purchase wording: App Review reads any mention of buying outside the app as a call to
                // action (guideline 3.1.1). This only says what the file does.
                Text("Installs this server's license file (license.lic). Without one, camera images carry a watermark and nothing else changes — every feature keeps working, so this is never urgent.")
            }

            Section {
                Button(isInstalling ? "Installing…" : "Choose License File…") { isPicking = true }
                    .disabled(isInstalling)
            } footer: {
                VStack(alignment: .leading, spacing: 6) {
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
        .navigationTitle("License")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .task { await refresh() }
        // .lic isn't a type the system knows, so it's matched by filename extension. .data as well,
        // because a file arriving through Mail or Files can land with no declared type at all and
        // would otherwise be greyed out in the picker.
        .fileImporter(
            isPresented: $isPicking,
            allowedContentTypes: [UTType(filenameExtension: "lic") ?? .data, .data],
            allowsMultipleSelection: false
        ) { result in
            Task { await install(result) }
        }
    }

    private func refresh() async {
        do {
            state = try await model.client.licenseState()
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func install(_ result: Result<[URL], Error>) async {
        statusMessage = nil
        errorMessage = nil

        do {
            guard let url = try result.get().first else { return }

            // A file picked outside the app's own container is security-scoped: without this the
            // read fails with a permissions error that reads like a corrupt file.
            let scoped = url.startAccessingSecurityScopedResource()
            defer { if scoped { url.stopAccessingSecurityScopedResource() } }

            let contents = try String(contentsOf: url, encoding: .utf8)

            isInstalling = true
            defer { isInstalling = false }

            let installed = try await model.client.installLicense(contents: contents)
            state = installed
            statusMessage = installed.customerName.map { "Licensed to \($0)." } ?? "License installed."

            // The watermark is drawn from the app model's own copy, which would otherwise keep
            // saying unlicensed until the next refresh.
            model.applyLicenseState(installed)
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}
