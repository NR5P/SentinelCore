import SwiftUI

struct SettingsView: View {
    @EnvironmentObject private var model: AppModel
    @State private var address = ""
    @State private var remoteAddress = ""
    @State private var showingDiagnostics = false

    var body: some View {
        Form {
            Section("Server") {
                TextField("Dashboard address", text: $address)
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    .keyboardType(.URL)
                    #endif
                TextField("Remote address (optional)", text: $remoteAddress)
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    .keyboardType(.URL)
                    #endif
                HStack {
                    Text("Status")
                    Spacer()
                    if model.hasRemoteServerURL {
                        Text(model.isUsingRemote ? "via Remote" : "via Local")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    ConnectionBadge().frame(maxWidth: 190)
                }
                Text("A Cloudflare Tunnel (or any other externally-reachable) address to use when the local address can't be reached, such as away from home on cellular.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Button("Save and Reconnect") {
                    Task {
                        model.serverURL = normalized(address)
                        model.remoteServerURL = remoteAddress.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                            ? ""
                            : normalized(remoteAddress, defaultScheme: "https")
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
        .onAppear {
            address = model.serverURL
            remoteAddress = model.remoteServerURL
        }
        .onChange(of: model.serverURL) { _, newValue in address = newValue }
        .onChange(of: model.remoteServerURL) { _, newValue in remoteAddress = newValue }
    }

    // Local addresses default to http:// (a bare LAN IP typing "192.168.1.50" shouldn't force a
    // scheme nobody configured). The remote address defaults to https:// instead: it's meant to
    // reach a public internet-facing hostname (e.g. a Cloudflare Tunnel), and silently falling
    // back to plain HTTP there would send credentials/cookies over the internet in the clear.
    private func normalized(_ value: String, defaultScheme: String = "http") -> String {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        let withScheme = trimmed.contains("://") ? trimmed : "\(defaultScheme)://\(trimmed)"
        return withScheme.hasSuffix("/") ? String(withScheme.dropLast()) : withScheme
    }
}
