import SwiftUI

struct SettingsView: View {
    @EnvironmentObject private var model: AppModel
    @State private var address = ""
    @State private var showingDiagnostics = false

    var body: some View {
        Form {
            Section("Server") {
                TextField("Dashboard address", text: $address)
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    .keyboardType(.URL)
                    #endif
                HStack {
                    Text("Status")
                    Spacer()
                    ConnectionBadge().frame(maxWidth: 190)
                }
                Button("Save and Reconnect") {
                    Task {
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

            // Camera CRUD, discovery, and trigger-zone configuration are all AdminOnly on the
            // server - hidden entirely for a Viewer rather than shown-disabled, same convention
            // as the Send Test Notification button below.
            if model.isAdmin {
                Section("Cameras") {
                    NavigationLink("Manage Cameras") {
                        CameraAdminView()
                    }
                }
            }

            Section("System") {
                LabeledContent("Events loaded", value: "\(model.events.count)")
                LabeledContent("Cameras", value: "\(model.cameras.count)")
                LabeledContent("Motion triggers", value: "\(model.triggers.count)")
                if let lastError = model.lastError {
                    Text(lastError).foregroundStyle(SentinelTheme.red)
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
                    Button("Disable on This Device", role: .destructive) {
                        Task { await model.disablePushNotifications() }
                    }
                }
                // Admin-only on the server (it messages every registered device, not just this
                // one) - hidden rather than disabled for a Viewer, since there's nothing they
                // could do to enable it.
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

            Section("Diagnostics") {
                DisclosureGroup("Connection Log", isExpanded: $showingDiagnostics) {
                    if model.diagnostics.isEmpty {
                        Text("No requests recorded yet.")
                            .foregroundStyle(.secondary)
                    } else {
                        // Monospace is kept here on purpose - this is genuinely tabular/log
                        // content, unlike the rest of the app's now-system-font text.
                        ForEach(model.diagnostics.prefix(30)) { entry in
                            VStack(alignment: .leading, spacing: 3) {
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
                                    .textSelection(.enabled)
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
                Text("This native client manages its connection and push registration locally. Full server configuration and account management remain in the Core dashboard.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .navigationTitle("Settings")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.large)
        #endif
        .onAppear { address = model.serverURL }
        .onChange(of: model.serverURL) { _, newValue in address = newValue }
    }

    private func normalized(_ value: String) -> String {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        let withScheme = trimmed.contains("://") ? trimmed : "http://\(trimmed)"
        return withScheme.hasSuffix("/") ? String(withScheme.dropLast()) : withScheme
    }
}
