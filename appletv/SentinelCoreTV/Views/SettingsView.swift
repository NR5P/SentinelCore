import SwiftUI

// Simplified tvOS port of ios/SentinelCore/Views/SettingsView.swift - camera/account admin and
// the change-password sheet are dropped (management stays on iOS/macOS/web, where typing and
// fine-grained editing are practical); server address, sign-in status, the movie libraries,
// push registration, and a connection log remain.
struct SettingsView: View {
    @EnvironmentObject private var model: AppModel
    @State private var address = ""
    @State private var showingDiagnostics = false

    var body: some View {
        #if DEBUG
        // Simulator testing without a remote: `-debugSection settings -debugSettingsPage
        // mediaLibraries` (or `folderPicker`) shows that screen in place of Settings.
        if let page = UserDefaults.standard.string(forKey: "debugSettingsPage") {
            if page == "folderPicker" {
                MediaFolderPickerView(startPath: "") { _ in }
            } else {
                MediaLibrariesAdminView()
            }
        } else {
            settingsForm
        }
        #else
        settingsForm
        #endif
    }

    private var settingsForm: some View {
        Form {
            Section("Server") {
                TextField("Dashboard address", text: $address)
                HStack {
                    Text("Status")
                    Spacer()
                    ConnectionBadge()
                }
                Button("Save and Reconnect") {
                    Task {
                        // The way past a server whose certificate authority changed - see ServerIdentity.
                        ServerIdentity.forget(normalized(address))
                        model.serverURL = normalized(address)
                        await model.connect(resetNetworkSession: true)
                    }
                }
                .disabled(address.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }

            Section("Account") {
                if let username = model.authStatus?.username {
                    LabeledContent("Signed in as", value: username)
                }
                if let role = model.authStatus?.role {
                    LabeledContent("Role", value: role.capitalized)
                }
                if model.authStatus?.enabled == true {
                    Button("Sign Out", role: .destructive) {
                        Task { await model.signOut() }
                    }
                } else {
                    Text("Dashboard login is not enabled.")
                        .foregroundStyle(.secondary)
                }
            }

            Section("System") {
                LabeledContent("Events loaded", value: "\(model.activity.count)")
                LabeledContent("Cameras", value: "\(model.cameras.count)")
                // Of those events, the ones nothing analyzed - motion the camera never forwarded
                // for analysis, or whose forward was refused.
                LabeledContent("Motion only", value: "\(model.activity.filter { !$0.isAnalyzed && !$0.isRecording }.count)")
                if let lastError = model.lastError {
                    Text(lastError).foregroundStyle(SentinelTheme.red)
                }
            }

            // The one piece of server management kept on the TV: the movie folders, since this is
            // where the family watches them. Typing goes through the system keyboard (or the
            // iPhone's Apple TV Remote keyboard).
            if model.isAdmin {
                Section("Movies & TV") {
                    NavigationLink("Movies & TV Libraries") {
                        MediaLibrariesAdminView()
                    }
                }
            }

            Section("Push Notifications") {
                LabeledContent("Permission", value: model.pushAuthorization.capitalized)
                if let status = model.pushStatus {
                    LabeledContent("Core", value: status.enabled ? "Enabled" : "Disabled")
                    LabeledContent("Apple provider", value: status.appleConfigured ? "Configured" : "Not configured")
                    LabeledContent("Registered devices", value: "\(status.registeredDevices)")
                }
                Button(model.pushRegistrationEnabled ? "Refresh Registration" : "Enable Notifications") {
                    Task { await model.enablePushNotifications() }
                }
                if model.pushRegistrationEnabled {
                    Button("Disable on This Apple TV", role: .destructive) {
                        Task { await model.disablePushNotifications() }
                    }
                }
                if model.isAdmin {
                    Button("Send Test Notification") {
                        Task { await model.sendTestPushNotification() }
                    }
                    .disabled(
                        model.pushStatus?.appleConfigured != true ||
                            model.pushAuthorization == "DENIED" ||
                            !model.pushRegistrationEnabled
                    )
                }
                if let pushMessage = model.pushMessage {
                    Text(pushMessage)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }

            // DisclosureGroup isn't available on tvOS - a plain toggle button standing in for
            // the expand/collapse chevron.
            Section("Diagnostics") {
                Button(showingDiagnostics ? "Hide Connection Log" : "Show Connection Log") {
                    showingDiagnostics.toggle()
                }
                if showingDiagnostics {
                    if model.diagnostics.isEmpty {
                        Text("No requests recorded yet.")
                            .foregroundStyle(.secondary)
                    } else {
                        ForEach(model.diagnostics.prefix(30)) { entry in
                            VStack(alignment: .leading, spacing: 4) {
                                HStack {
                                    Text(entry.level.rawValue)
                                        .foregroundStyle(entry.level == .error ? SentinelTheme.red : SentinelTheme.cyan)
                                    Spacer()
                                    Text(entry.timestamp.formatted(date: .omitted, time: .standard))
                                        .foregroundStyle(.secondary)
                                }
                                .font(.caption2)
                                Text(entry.message)
                                    .font(.caption)
                            }
                            .fontDesign(.monospaced)
                        }
                    }
                    Button("Clear Log") { model.clearDiagnostics() }
                }
                Text("Request bodies, passwords, cookies, and server response contents are never written here.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section {
                Text("This Apple TV client manages its own connection and push registration. Camera setup, accounts, and recordings are managed from the iOS, macOS, or web dashboard.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .onAppear {
            address = model.serverURL
        }
        .onChange(of: model.serverURL) { _, newValue in address = newValue }
    }

    private func normalized(_ value: String) -> String {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        let withScheme = trimmed.contains("://") ? trimmed : "http://\(trimmed)"
        return withScheme.hasSuffix("/") ? String(withScheme.dropLast()) : withScheme
    }
}

