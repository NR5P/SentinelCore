import SwiftUI

// The library's backup drives - a folder the server can see, or another computer over SFTP -
// with how the last backup to each went, the nightly pass, and the duplicate check. An admin
// runs and edits all of it here; anyone else gets the same screen read-only, and the server has
// already left the connection details out of what it sends them.
struct FilesDrivesView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var session: FilesSession

    @State private var addingDrive = false
    @State private var editingDrive: FamilyDrive?
    @State private var editingLocation = false
    @State private var restoringDrive: FamilyDrive?
    @State private var confirmingRemoveDuplicates = false
    @State private var busyAction: String?
    @State private var note: String?
    @State private var error: String?

    private var status: FamilyFilesStatus? { session.status }
    private var drives: [FamilyDrive] { status?.drives ?? [] }
    private var isAdmin: Bool { session.isAdmin }
    private var jobRunning: Bool { session.job != nil }

    var body: some View {
        Form {
            drivesSection
            if isAdmin {
                Section {
                    actionButton("Back Up to All Drives Now", systemImage: "externaldrive.badge.checkmark", key: "sync-all",
                                 disabled: !drives.contains { $0.enabled }) {
                        try await model.client.familySyncAll()
                    }
                } footer: {
                    Text("Copies anything new or changed to every drive that is switched on. The nightly pass does this on its own.")
                }
            }
            nightlySection
            duplicatesSection
            locationSection
            librarySection
            if let note {
                Section { Text(note).foregroundStyle(SentinelTheme.green) }
            }
            if let error {
                Section { Text(error).foregroundStyle(SentinelTheme.red) }
            }
        }
        .formStyle(.grouped)
        .navigationTitle("Backup Drives")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            if isAdmin {
                ToolbarItem(placement: .primaryAction) {
                    Button { addingDrive = true } label: { Label("Add Drive", systemImage: "plus") }
                }
            }
        }
        .safeAreaInset(edge: .top, spacing: 0) { FilesBanners() }
        .refreshable { await session.refreshStatus(model.client) }
        .task { await session.refreshStatus(model.client) }
        .sheet(isPresented: $addingDrive) {
            NavigationStack {
                DriveEditView(drive: nil)
            }
            .filesSheetFrame()
            .environmentObject(model)
            .environmentObject(session)
        }
        .sheet(isPresented: $editingLocation) {
            NavigationStack {
                LibraryLocationEditView()
            }
            .filesSheetFrame()
            .environmentObject(model)
            .environmentObject(session)
        }
        .sheet(item: $editingDrive) { drive in
            NavigationStack {
                DriveEditView(drive: drive)
            }
            .filesSheetFrame()
            .environmentObject(model)
            .environmentObject(session)
        }
        .confirmationDialog(
            restoringDrive.map { "Bring files back from \($0.name)?" } ?? "Bring files back?",
            isPresented: Binding(get: { restoringDrive != nil }, set: { if !$0 { restoringDrive = nil } }),
            titleVisibility: .visible,
            presenting: restoringDrive
        ) { drive in
            Button("Bring Files Back") {
                restoringDrive = nil
                Task { await run("restore-\(drive.id)") { try await model.client.familyRestoreDrive(id: drive.id) } }
            }
            Button("Cancel", role: .cancel) { restoringDrive = nil }
        } message: { drive in
            Text("Anything on \(drive.name) that the library doesn't have is copied into the library. Nothing in the library is removed or replaced, and nothing on the drive is touched.")
        }
        .confirmationDialog("Check for duplicates and move the copies to the trash?", isPresented: $confirmingRemoveDuplicates, titleVisibility: .visible) {
            Button("Check and Remove", role: .destructive) {
                Task { await run("duplicates-remove") { try await model.client.familyCheckDuplicates(remove: true) } }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("The oldest copy of each file is kept. The others go to the trash, where they can be put back.")
        }
    }

    // MARK: - sections

    @ViewBuilder private var drivesSection: some View {
        Section {
            if drives.isEmpty {
                Text(isAdmin
                     ? "No backup drives yet. Add a folder on an external drive or a NAS, or another computer over SFTP."
                     : "No backup drives have been set up. An admin account can add them.")
                    .foregroundStyle(.secondary)
            }
            ForEach(drives) { drive in
                DriveRow(drive: drive, showsDetails: isAdmin)
                    .contentShape(Rectangle())
                    .onTapGesture { if isAdmin { editingDrive = drive } }
                if isAdmin {
                    HStack {
                        Button {
                            Task { await run("sync-\(drive.id)") { try await model.client.familySyncDrive(id: drive.id) } }
                        } label: {
                            Label("Back Up Now", systemImage: "arrow.up.doc")
                        }
                        .disabled(jobRunning || busyAction != nil)
                        Spacer()
                        Button {
                            restoringDrive = drive
                        } label: {
                            Label("Bring Files Back", systemImage: "arrow.down.doc")
                        }
                        .disabled(jobRunning || busyAction != nil)
                        Spacer()
                        Button {
                            editingDrive = drive
                        } label: {
                            Label("Edit", systemImage: "pencil")
                        }
                    }
                    .buttonStyle(.borderless)
                    .font(.caption)
                }
            }
        } header: {
            Text("Drives")
        } footer: {
            if isAdmin {
                Text("Each drive holds a full copy of the library. A backup copies what's new or changed and removes what was trashed here.")
            }
        }
    }

    @ViewBuilder private var nightlySection: some View {
        Section {
            if let nightly = status?.nightly {
                if let next = nightly.nextRunUtc {
                    LabeledContent("Next run", value: FamilyFilesFormat.date(next))
                } else if let hour = nightly.hour, let minute = nightly.minute {
                    LabeledContent("Runs at", value: String(format: "%02d:%02d", hour, minute))
                }
                if let last = nightly.lastRunUtc {
                    LabeledContent("Last run") {
                        VStack(alignment: .trailing, spacing: 2) {
                            HStack(spacing: 6) {
                                ResultDot(result: nightly.lastResult ?? "")
                                Text(FamilyFilesFormat.date(last))
                            }
                            if let message = nightly.lastMessage, !message.isEmpty {
                                Text(message).font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.trailing)
                            }
                        }
                    }
                } else {
                    LabeledContent("Last run", value: "Never")
                }
                LabeledContent("Remove duplicates", value: nightly.removeDuplicates == true ? "On" : "Off")
                if let days = nightly.trashKeepDays {
                    LabeledContent("Trash kept for", value: days == 1 ? "1 day" : "\(days) days")
                }
            } else {
                Text("Loading…").foregroundStyle(.secondary)
            }
            if isAdmin {
                actionButton("Run the Nightly Pass Now", systemImage: "moon.stars", key: "nightly") {
                    try await model.client.familyRunNightly()
                }
            }
        } header: {
            Text("Nightly")
        } footer: {
            Text("Every night the library is indexed, checked for duplicates, backed up to every drive, and old trash is cleared. The time and options are in Settings → Family Files.")
        }
    }

    @ViewBuilder private var duplicatesSection: some View {
        Section {
            if let report = status?.duplicates {
                LabeledContent("Last check", value: FamilyFilesFormat.date(report.ranUtc))
                LabeledContent("Found") {
                    Text(report.groups == 0
                         ? "No duplicates in \(report.filesChecked) files"
                         : "\(report.copies) extra \(report.copies == 1 ? "copy" : "copies") in \(report.groups) \(report.groups == 1 ? "group" : "groups"), \(FamilyFilesFormat.bytes(report.bytes))")
                        .multilineTextAlignment(.trailing)
                }
                LabeledContent("Copies", value: report.removed ? "Moved to the trash" : "Listed only")
            } else {
                Text("Not checked yet.").foregroundStyle(.secondary)
            }
            if isAdmin {
                actionButton("Check Now", systemImage: "doc.on.doc", key: "duplicates") {
                    try await model.client.familyCheckDuplicates(remove: false)
                }
                Button {
                    confirmingRemoveDuplicates = true
                } label: {
                    Label("Check and Remove Now", systemImage: "doc.on.doc.fill")
                }
                .disabled(jobRunning || busyAction != nil)
                NavigationLink {
                    FilesDuplicatesView()
                        .environmentObject(model)
                        .environmentObject(session)
                } label: {
                    Label("See the Last Report", systemImage: "list.bullet")
                }
            }
        } header: {
            Text("Duplicates")
        } footer: {
            Text("Two files with exactly the same contents, whatever they are called. The oldest is kept.")
        }
    }

    // Where the library's files live: a folder on the server, or a shared folder on another
    // computer. Only an admin can move it.
    @ViewBuilder private var locationSection: some View {
        if let status, status.enabled {
            Section {
                if let folder = status.libraryFolder, !folder.isEmpty {
                    LabeledContent(status.libraryOnNetwork == true ? "Shared folder" : "Folder on the server") {
                        Text(folder).font(.caption.monospaced()).textSelection(.enabled).multilineTextAlignment(.trailing)
                    }
                }
                LabeledContent("Where", value: status.libraryOnNetwork == true ? "Another computer" : "This server")
                if let problem = status.libraryProblem, !problem.isEmpty {
                    Label {
                        Text(problem).font(.callout)
                    } icon: {
                        Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(SentinelTheme.amber)
                    }
                }
                if isAdmin {
                    Button {
                        editingLocation = true
                    } label: {
                        Label("Change Location…", systemImage: "folder.badge.gearshape")
                    }
                    .disabled(jobRunning)
                }
            } header: {
                Text("Library Location")
            } footer: {
                if isAdmin {
                    Text("The library can live in a folder on the server or in a shared folder on another computer, such as a Windows PC or a Linux machine running Samba.")
                }
            }
        }
    }

    @ViewBuilder private var librarySection: some View {
        if let library = status?.library {
            Section("Library") {
                LabeledContent("Files", value: "\(library.files)")
                LabeledContent("Size", value: FamilyFilesFormat.bytes(library.bytes))
                if let indexed = library.indexedUtc {
                    LabeledContent("Indexed", value: FamilyFilesFormat.date(indexed))
                }
                if let trash = status?.trash, trash.files > 0 {
                    LabeledContent("In the trash", value: "\(trash.files) \(trash.files == 1 ? "file" : "files"), \(FamilyFilesFormat.bytes(trash.bytes))")
                }
            }
        }
    }

    // MARK: - actions

    private func actionButton(_ title: String, systemImage: String, key: String, disabled: Bool = false,
                              _ action: @escaping () async throws -> FamilyDetailResult) -> some View {
        Button {
            Task { await run(key, action) }
        } label: {
            HStack {
                Label(title, systemImage: systemImage)
                Spacer()
                if busyAction == key { ProgressView().controlSize(.small) }
            }
        }
        .disabled(disabled || jobRunning || busyAction != nil)
    }

    // A 202 means the job exists from this moment; the banner picks it up on the next status.
    private func run(_ key: String, _ action: () async throws -> FamilyDetailResult) async {
        busyAction = key
        defer { busyAction = nil }
        do {
            let result = try await action()
            note = result.detail
            error = nil
            session.jobStarted(model.client)
        } catch {
            self.error = error.localizedDescription
            note = nil
        }
    }
}

// One drive: name, kind, where it points, and how the last backup went.
struct DriveRow: View {
    let drive: FamilyDrive
    var showsDetails = true

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 8) {
                Image(systemName: drive.isSftp ? "desktopcomputer" : "externaldrive")
                    .foregroundStyle(drive.enabled ? SentinelTheme.cyan : Color.secondary)
                Text(drive.name).font(.headline)
                if !drive.enabled {
                    Text("Off")
                        .font(.caption2.weight(.semibold))
                        .padding(.horizontal, 6)
                        .padding(.vertical, 2)
                        .background(Color.secondary.opacity(0.15), in: Capsule())
                        .foregroundStyle(.secondary)
                }
                Spacer()
                ResultDot(result: drive.lastResult ?? "")
            }
            if let location = drive.location, !location.isEmpty {
                Text(location).font(.caption.monospaced()).foregroundStyle(.secondary).lineLimit(2)
            }
            HStack(spacing: 6) {
                if let attempt = drive.lastAttemptUtc {
                    Text(resultLabel(drive.lastResult ?? "")) + Text(" · ") + Text(FamilyFilesFormat.date(attempt))
                } else {
                    Text("Not backed up yet")
                }
            }
            .font(.caption)
            .foregroundStyle(.secondary)
            if let message = drive.lastMessage, !message.isEmpty {
                // A held-back removal explains itself and says what to press, so show all of it.
                Text(message).font(.caption).foregroundStyle(.secondary).lineLimit(drive.lastResult == "held" ? nil : 3)
            }
            if showsDetails {
                HStack(spacing: 12) {
                    if let count = drive.lastFileCount, let bytes = drive.lastBytes {
                        Text("\(count) files, \(FamilyFilesFormat.bytes(bytes))")
                    }
                    if let free = drive.lastFreeBytes {
                        Text("\(FamilyFilesFormat.bytes(free)) free")
                    }
                }
                .font(.caption2)
                .foregroundStyle(.tertiary)
            }
        }
        .padding(.vertical, 2)
        .accessibilityElement(children: .combine)
    }

    private func resultLabel(_ result: String) -> String {
        switch result {
        case "ok": "Backed up"
        case "failed": "Failed"
        case "incomplete": "Incomplete"
        case "held": "Held back"
        default: "Last attempt"
        }
    }
}

struct ResultDot: View {
    let result: String

    var body: some View {
        Circle()
            .fill(color)
            .frame(width: 8, height: 8)
            .shadow(color: color.opacity(0.6), radius: 3)
            .accessibilityLabel(result.isEmpty ? "No result yet" : result)
    }

    private var color: Color {
        switch result {
        case "ok": SentinelTheme.green
        case "failed": SentinelTheme.red
        case "incomplete", "held": SentinelTheme.amber
        default: .secondary
        }
    }
}

// MARK: - editing a drive

// Add or edit one drive. "Test Connection" sends the form as typed, before anything is saved,
// and when an SFTP host's key is not the one on the form the server says which one it saw, so
// it can be pasted in with one tap rather than typed from an ssh printout.
struct DriveEditView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var session: FilesSession
    @Environment(\.dismiss) private var dismiss

    let drive: FamilyDrive?

    @State private var name = ""
    @State private var kind = FamilyDrive.kindFolder
    @State private var enabled = true
    @State private var folderPath = ""
    @State private var sftpHost = ""
    @State private var sftpPort = "22"
    @State private var sftpUsername = ""
    @State private var sftpPassword = ""
    @State private var sftpFolder = ""
    @State private var fingerprint = ""
    @State private var isTesting = false
    @State private var isSaving = false
    @State private var testDetail: String?
    @State private var testFailed = false
    @State private var seenFingerprint: String?
    @State private var error: String?
    @State private var confirmingDelete = false

    var body: some View {
        Form {
            Section {
                SentinelTextField(label: "Name", prompt: "USB drive, NAS, Dad's computer…", text: $name)
                Picker("Kind", selection: $kind) {
                    Text("Folder").tag(FamilyDrive.kindFolder)
                    Text("Another computer (SFTP)").tag(FamilyDrive.kindSftp)
                }
                Toggle("Switched on", isOn: $enabled)
            } footer: {
                Text("A drive that is switched off is left alone by backups but stays in the list.")
            }
            if kind == FamilyDrive.kindSftp {
                Section {
                    SentinelTextField(label: "Host", prompt: "nas.local or 192.168.1.20", text: $sftpHost)
                        #if os(iOS)
                        .textInputAutocapitalization(.never)
                        .keyboardType(.URL)
                        #endif
                    SentinelTextField(label: "Port", prompt: "22", text: $sftpPort)
                        #if os(iOS)
                        .keyboardType(.numberPad)
                        #endif
                    SentinelTextField(label: "Username", text: $sftpUsername)
                        #if os(iOS)
                        .textInputAutocapitalization(.never)
                        #endif
                    SentinelTextField(label: drive?.hasPassword == true ? "Password (blank keeps the saved one)" : "Password",
                                      prompt: SentinelInput.typeVerb, text: $sftpPassword, secure: true)
                    SentinelTextField(label: "Folder on that computer", prompt: "/backups/family", text: $sftpFolder)
                        #if os(iOS)
                        .textInputAutocapitalization(.never)
                        #endif
                    SentinelTextField(label: "Host key fingerprint (optional)", prompt: "SHA256:…", text: $fingerprint)
                        #if os(iOS)
                        .textInputAutocapitalization(.never)
                        #endif
                    if let seenFingerprint, seenFingerprint != fingerprint.trimmingCharacters(in: .whitespaces) {
                        Button {
                            fingerprint = seenFingerprint
                        } label: {
                            Label("Use the Fingerprint the Server Saw", systemImage: "key")
                        }
                    }
                } header: {
                    Text("Connection")
                } footer: {
                    Text("The fingerprint pins the computer's identity so a backup never goes to an impostor. Leave it blank to accept whatever answers the first time; the fingerprint that was seen is recorded on the drive afterwards.")
                }
            } else {
                Section {
                    SentinelTextField(label: "Folder on the server", prompt: "/mnt/usb/Family Files", text: $folderPath)
                        #if os(iOS)
                        .textInputAutocapitalization(.never)
                        #endif
                } header: {
                    Text("Where")
                } footer: {
                    Text("A full path as the server sees it - an external drive plugged into the server, or a network share it has mounted.")
                }
            }
            Section {
                Button {
                    Task { await test() }
                } label: {
                    HStack {
                        Label("Test Connection", systemImage: "bolt.horizontal")
                        Spacer()
                        if isTesting { ProgressView().controlSize(.small) }
                    }
                }
                .disabled(isTesting || isSaving)
                if let testDetail {
                    Text(testDetail)
                        .font(.callout)
                        .foregroundStyle(testFailed ? SentinelTheme.red : SentinelTheme.green)
                        .textSelection(.enabled)
                }
            }
            if let error {
                Section { Text(error).foregroundStyle(SentinelTheme.red) }
            }
            if drive != nil {
                Section {
                    Button("Remove This Drive", role: .destructive) { confirmingDelete = true }
                        .disabled(isSaving)
                } footer: {
                    Text("Only the entry here is removed. Nothing on the drive itself is touched.")
                }
            }
        }
        .formStyle(.grouped)
        .navigationTitle(drive == nil ? "Add Drive" : "Edit Drive")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItem(placement: .cancellationAction) {
                Button("Cancel") { dismiss() }
            }
            ToolbarItem(placement: .confirmationAction) {
                Button {
                    Task { await save() }
                } label: {
                    if isSaving { ProgressView().controlSize(.small) } else { Text("Save") }
                }
                .disabled(isSaving || name.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .onAppear(perform: fill)
        .confirmationDialog("Remove \(drive?.name ?? "this drive") from the list?", isPresented: $confirmingDelete, titleVisibility: .visible) {
            Button("Remove", role: .destructive) { Task { await delete() } }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Backups stop going to it. The files already on it stay where they are.")
        }
    }

    private func fill() {
        guard let drive else { return }
        name = drive.name
        kind = drive.isSftp ? FamilyDrive.kindSftp : FamilyDrive.kindFolder
        enabled = drive.enabled
        folderPath = drive.folderPath ?? ""
        sftpHost = drive.sftpHost ?? ""
        sftpPort = String(drive.sftpPort ?? 22)
        sftpUsername = drive.sftpUsername ?? ""
        sftpFolder = drive.sftpFolder ?? ""
        fingerprint = drive.sftpHostKeyFingerprint ?? ""
        if let seen = drive.lastHostKeyFingerprint, !seen.isEmpty { seenFingerprint = seen }
    }

    private var input: FamilyDriveInput {
        FamilyDriveInput(
            id: drive?.id,
            name: name.trimmingCharacters(in: .whitespaces),
            kind: kind,
            enabled: enabled,
            folderPath: folderPath.trimmingCharacters(in: .whitespaces),
            sftpHost: sftpHost.trimmingCharacters(in: .whitespaces),
            sftpPort: Int(sftpPort.trimmingCharacters(in: .whitespaces)) ?? 22,
            sftpUsername: sftpUsername.trimmingCharacters(in: .whitespaces),
            sftpPassword: sftpPassword,
            sftpFolder: sftpFolder.trimmingCharacters(in: .whitespaces),
            sftpHostKeyFingerprint: fingerprint.trimmingCharacters(in: .whitespaces))
    }

    private func test() async {
        isTesting = true
        defer { isTesting = false }
        do {
            let result = try await model.client.familyTestDrive(input)
            testDetail = result.detail ?? "Reached it."
            testFailed = false
            if let seen = result.hostKeyFingerprint, !seen.isEmpty { seenFingerprint = seen }
        } catch let failure as FamilyDriveTestFailure {
            testDetail = failure.errorDescription
            testFailed = true
            if let seen = failure.hostKeyFingerprint, !seen.isEmpty { seenFingerprint = seen }
        } catch {
            testDetail = error.localizedDescription
            testFailed = true
        }
    }

    private func save() async {
        isSaving = true
        defer { isSaving = false }
        do {
            if let drive {
                _ = try await model.client.familyUpdateDrive(id: drive.id, input)
            } else {
                _ = try await model.client.familyCreateDrive(input)
            }
            SaveFeedback.hapticSuccess()
            await session.refreshStatus(model.client)
            dismiss()
        } catch {
            self.error = error.localizedDescription
        }
    }

    private func delete() async {
        guard let drive else { return }
        isSaving = true
        defer { isSaving = false }
        do {
            _ = try await model.client.familyDeleteDrive(id: drive.id)
            await session.refreshStatus(model.client)
            dismiss()
        } catch {
            self.error = error.localizedDescription
        }
    }
}

// MARK: - the library's location

// Point the library at a folder on the server or a shared folder on another computer. "Test"
// signs in and writes a test file without changing anything; "Use This Location" runs the same
// check and then moves the library there. Files already in the old place are never moved.
struct LibraryLocationEditView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var session: FilesSession
    @Environment(\.dismiss) private var dismiss

    private enum Kind: Hashable { case local, network }

    @State private var kind = Kind.local
    @State private var localPath = ""
    @State private var networkPath = ""
    @State private var username = ""
    @State private var password = ""
    @State private var isTesting = false
    @State private var isSaving = false
    @State private var testDetail: String?
    @State private var testFailed = false
    @State private var needsLogin = false
    @State private var confirmingMove = false
    @State private var filled = false

    private var current: String { session.status?.libraryFolder ?? "" }

    var body: some View {
        Form {
            Section {
                Picker("Where the files live", selection: $kind) {
                    Text("A folder on this server").tag(Kind.local)
                    Text("A shared folder on another computer").tag(Kind.network)
                }
            }
            if kind == .network {
                Section {
                    SentinelTextField(label: "Shared folder", prompt: "smb://192.168.1.20/Family/Files", text: $networkPath)
                        #if os(iOS)
                        .textInputAutocapitalization(.never)
                        .keyboardType(.URL)
                        #endif
                        .autocorrectionDisabled()
                    SentinelTextField(label: "Username", prompt: "the account on that computer", text: $username)
                        #if os(iOS)
                        .textInputAutocapitalization(.never)
                        #endif
                        .autocorrectionDisabled()
                    SentinelTextField(label: "Password", prompt: "blank = the one already saved", text: $password, secure: true)
                } header: {
                    Text("Shared Folder")
                } footer: {
                    Text("The computer's name or address, then the shared folder's name, then any folder inside it (made if it isn't there), like smb://192.168.1.20/Family/Files or \\\\DESKTOP-PC\\Family. On Windows, share the folder (Properties → Sharing) and give the account \"Change\" permission. On Linux, share it with Samba (smbpasswd -a for the user) with writable = yes.\n\nThe sign-in is saved for that computer and shared with Movies libraries on it. Leave both blank to use a saved sign-in, or to try as a guest.")
                }
            } else {
                Section {
                    SentinelTextField(label: "Folder", prompt: "/mnt/bigdisk/Family Files (blank = the data folder)", text: $localPath)
                        #if os(iOS)
                        .textInputAutocapitalization(.never)
                        #endif
                        .autocorrectionDisabled()
                } header: {
                    Text("Folder on the Server")
                } footer: {
                    Text("A full path as the server sees it. Blank keeps the library under the server's data folder.")
                }
            }
            Section {
                Button {
                    Task { await test() }
                } label: {
                    HStack {
                        Label("Test", systemImage: "bolt.horizontal")
                        Spacer()
                        if isTesting { ProgressView().controlSize(.small) }
                    }
                }
                .disabled(isTesting || isSaving)
                if let testDetail {
                    Text(testDetail)
                        .font(.callout)
                        .foregroundStyle(testFailed ? SentinelTheme.red : SentinelTheme.green)
                        .textSelection(.enabled)
                }
                if needsLogin && kind == .network {
                    Text("Enter a username and password for an account on that computer that can change files in the folder.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            } footer: {
                Text("Changing this doesn't move anything: files in the old place stay there. To fill the new place, copy them across yourself, or use \"Bring Files Back\" on a backup drive.")
            }
        }
        .formStyle(.grouped)
        .navigationTitle("Library Location")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItem(placement: .cancellationAction) {
                Button("Cancel") { dismiss() }
            }
            ToolbarItem(placement: .confirmationAction) {
                Button {
                    if input.location == current { dismiss() } else { confirmingMove = true }
                } label: {
                    if isSaving { ProgressView().controlSize(.small) } else { Text("Use This Location") }
                }
                .disabled(isSaving || isTesting || (kind == .network && input.location.isEmpty))
            }
        }
        .onAppear(perform: fill)
        .confirmationDialog("Move the library to \(input.location.isEmpty ? "the server's data folder" : input.location)?",
                            isPresented: $confirmingMove, titleVisibility: .visible) {
            Button("Use This Location") { Task { await save() } }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(current.isEmpty
                 ? "Files already in the old place stay there and stop showing in the library."
                 : "Files already in \(current) stay there and stop showing in the library.")
        }
    }

    private func fill() {
        guard !filled else { return }
        filled = true
        if session.status?.libraryOnNetwork == true {
            kind = .network
            networkPath = current
        } else {
            localPath = current
        }
    }

    private var input: FamilyLibraryLocationInput {
        let network = kind == .network
        var location = (network ? networkPath : localPath).trimmingCharacters(in: .whitespacesAndNewlines)
        // A bare "computer/Share" typed into the shared-folder box.
        if network, !location.isEmpty, !Self.looksLikeShare(location) { location = "smb://\(location)" }
        return FamilyLibraryLocationInput(
            location: location,
            username: network ? username.trimmingCharacters(in: .whitespaces) : "",
            password: network ? password : "")
    }

    static func looksLikeShare(_ location: String) -> Bool {
        let lower = location.lowercased()
        let rest = lower.hasPrefix("smb:") ? String(lower.dropFirst(4)) : lower
        return rest.hasPrefix("//") || rest.hasPrefix("\\\\") || rest.hasPrefix("/\\") || rest.hasPrefix("\\/")
    }

    private func test() async {
        isTesting = true
        defer { isTesting = false }
        do {
            let result = try await model.client.familyTestLibraryLocation(input)
            show(result.detail ?? "That folder works.", failed: false, needsLogin: false)
        } catch let failure as FamilyLibraryLocationFailure {
            show(failure.errorDescription ?? "", failed: true, needsLogin: failure.needsLogin == true)
        } catch {
            show(error.localizedDescription, failed: true, needsLogin: false)
        }
    }

    private func save() async {
        isSaving = true
        defer { isSaving = false }
        do {
            let result = try await model.client.familySetLibraryLocation(input)
            SaveFeedback.hapticSuccess()
            session.lastUploadNote = result.detail
            await session.refreshStatus(model.client)
            session.changed()
            dismiss()
        } catch let failure as FamilyLibraryLocationFailure {
            show(failure.errorDescription ?? "", failed: true, needsLogin: failure.needsLogin == true)
        } catch {
            show(error.localizedDescription, failed: true, needsLogin: false)
        }
    }

    private func show(_ detail: String, failed: Bool, needsLogin: Bool) {
        testDetail = detail
        testFailed = failed
        self.needsLogin = needsLogin
    }
}

// MARK: - the duplicate report

// The last duplicate check in full: each group's kept file and the copies that were (or would
// be) trashed. Admin-only on the server, like the check itself.
struct FilesDuplicatesView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var session: FilesSession

    @State private var report: FamilyDuplicateReport?
    @State private var loaded = false
    @State private var error: String?
    @State private var busy = false
    @State private var confirmingRemove = false
    @State private var note: String?

    var body: some View {
        List {
            if let error {
                Text(error).foregroundStyle(SentinelTheme.red)
            }
            if let report {
                Section {
                    LabeledContent("Checked", value: FamilyFilesFormat.date(report.ranUtc))
                    LabeledContent("Files looked at", value: "\(report.filesChecked)")
                    LabeledContent("Groups", value: "\(report.groups)")
                    LabeledContent("Extra copies", value: "\(report.copies) (\(FamilyFilesFormat.bytes(report.bytes)))")
                    LabeledContent("Copies", value: report.removed ? "Moved to the trash" : "Listed only")
                } header: {
                    Text("Last Check")
                } footer: {
                    if let sample = report.sample, sample.count < report.groups {
                        Text("Showing the first \(sample.count) of \(report.groups) groups.")
                    }
                }
                ForEach(report.sample ?? []) { group in
                    Section {
                        Label {
                            Text(group.kept).font(.callout)
                        } icon: {
                            Image(systemName: "checkmark.circle.fill").foregroundStyle(SentinelTheme.green)
                        }
                        ForEach(group.copies, id: \.self) { copy in
                            Label {
                                Text(copy).font(.callout).foregroundStyle(.secondary)
                            } icon: {
                                Image(systemName: report.removed ? "trash" : "doc.on.doc").foregroundStyle(.secondary)
                            }
                        }
                    } header: {
                        Text("\(group.copies.count + 1) × \(FamilyFilesFormat.bytes(group.size))")
                    }
                }
            } else if loaded {
                ContentUnavailableView {
                    Label("Not Checked Yet", systemImage: "doc.on.doc")
                } description: {
                    Text("Run a check to see which files exist more than once.")
                }
            }
            Section {
                Button {
                    Task { await check(remove: false) }
                } label: {
                    HStack {
                        Label("Check Now", systemImage: "doc.on.doc")
                        Spacer()
                        if busy { ProgressView().controlSize(.small) }
                    }
                }
                Button {
                    confirmingRemove = true
                } label: {
                    Label("Check and Remove Now", systemImage: "doc.on.doc.fill")
                }
                if let note {
                    Text(note).font(.caption).foregroundStyle(.secondary)
                }
            }
            .disabled(busy || session.job != nil)
        }
        .navigationTitle("Duplicates")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .safeAreaInset(edge: .top, spacing: 0) { FilesBanners() }
        .task(id: session.version) { await load() }
        .refreshable { await load() }
        .confirmationDialog("Check for duplicates and move the copies to the trash?", isPresented: $confirmingRemove, titleVisibility: .visible) {
            Button("Check and Remove", role: .destructive) { Task { await check(remove: true) } }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("The oldest copy of each file is kept. The others go to the trash, where they can be put back.")
        }
    }

    private func load() async {
        do {
            report = try await model.client.familyDuplicateReport()
            error = nil
        } catch {
            if error is CancellationError || (error as? URLError)?.code == .cancelled { return }
            self.error = error.localizedDescription
        }
        loaded = true
    }

    private func check(remove: Bool) async {
        busy = true
        defer { busy = false }
        do {
            let result = try await model.client.familyCheckDuplicates(remove: remove)
            note = result.detail
            error = nil
            session.jobStarted(model.client)
        } catch {
            self.error = error.localizedDescription
        }
    }
}

extension View {
    // A Mac sheet sizes itself to its content, and a Form's content measures to almost nothing,
    // so without this the drive editor opens as a sliver. See the same fix on the Device Hub.
    func filesSheetFrame() -> some View {
        #if os(macOS)
        self.frame(minWidth: 480, minHeight: 560)
        #else
        self
        #endif
    }
}
