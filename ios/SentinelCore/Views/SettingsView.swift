import SwiftUI

struct SettingsView: View {
    @EnvironmentObject private var model: AppModel
    @State private var address = ""
    @State private var remoteAddress = ""
    @State private var showingDiagnostics = false
    @State private var showingChangePassword = false

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
                if model.usingHttpFallback {
                    Text("Connected over plain HTTP: this device doesn't trust the certificate the server generated for itself, so the https:// address can't be used yet. Everything works, and traffic stays on your local network. For an encrypted connection, install the server's certificate authority (certs/ca.crt on the server) on this device and mark it trusted.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Text("A Cloudflare Tunnel (or any other externally-reachable) address to use when the local address can't be reached, such as away from home on cellular. Point it at the server's Internet Exposed port, not the normal dashboard port — that port always requires a login, while the normal one trusts the local network.")
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
                    Button("Change Password") { showingChangePassword = true }
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
                Section("Accounts") {
                    NavigationLink("Manage Accounts") {
                        AccountAdminView()
                    }
                }
                Section("Known Faces") {
                    NavigationLink("Manage Known Faces") {
                        KnownFacesAdminView()
                    }
                }
                Section {
                    NavigationLink("Server Settings") {
                        ServerSettingsView()
                    }
                } header: {
                    Text("Server Configuration")
                } footer: {
                    Text("Every server setting the web dashboard has — alerts, AI backends, MQTT, Device Hub, and more — editable from here.")
                }
            }

            Section("Server Health") {
                if let status = model.systemStatus {
                    LabeledContent("CPU") {
                        Text("\(Int(status.cpuUsagePercent.rounded()))% of \(status.processorCount) core\(status.processorCount == 1 ? "" : "s")")
                    }
                    ProgressView(value: min(status.cpuUsagePercent / 100, 1))
                    LabeledContent("Memory") {
                        Text("\(Self.formatBytes(status.memoryUsedBytes)) / \(Self.formatBytes(status.memoryTotalBytes))")
                    }
                    ProgressView(value: min(status.memoryUsedFraction, 1))
                    LabeledContent("Disk") {
                        Text("\(Self.formatBytes(status.diskUsedBytes)) / \(Self.formatBytes(status.diskTotalBytes))")
                    }
                    ProgressView(value: min(status.diskUsedFraction, 1))
                    LabeledContent("Analysis queue", value: "\(status.queuedAlerts) pending")
                    LabeledContent("Vision backend", value: status.visionBackendHealthy ? "Reachable" : "Unreachable")
                        .foregroundStyle(status.visionBackendHealthy ? .primary : SentinelTheme.red)
                    LabeledContent("Uptime", value: Self.formatUptime(status.uptimeSeconds))
                } else {
                    Text("Could not load server status.")
                        .foregroundStyle(.secondary)
                }
                Button("Refresh") {
                    Task { await model.refreshSystemStatus() }
                }
            }

            Section("System") {
                LabeledContent("Events loaded", value: "\(model.activity.count)")
                LabeledContent("Cameras", value: "\(model.cameras.count)")
                // Of those events, the ones nothing analyzed - motion the camera never forwarded
                // for analysis, or whose forward was refused.
                LabeledContent("Motion only", value: "\(model.activity.filter { !$0.isAnalyzed }.count)")
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
                Text("This native client manages its connection and push registration locally. Full server configuration remains in the Core dashboard.")
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
            Task { await model.refreshSystemStatus() }
        }
        .onChange(of: model.serverURL) { _, newValue in address = newValue }
        .onChange(of: model.remoteServerURL) { _, newValue in remoteAddress = newValue }
        .sheet(isPresented: $showingChangePassword) {
            ChangePasswordSheet()
        }
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

    private static func formatBytes(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: bytes, countStyle: .binary)
    }

    private static func formatUptime(_ seconds: Double) -> String {
        let totalMinutes = Int(seconds / 60)
        let days = totalMinutes / 1440
        let hours = (totalMinutes % 1440) / 60
        let minutes = totalMinutes % 60
        if days > 0 { return "\(days)d \(hours)h" }
        if hours > 0 { return "\(hours)h \(minutes)m" }
        return "\(minutes)m"
    }
}

private struct ChangePasswordSheet: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss

    @State private var currentPassword = ""
    @State private var newPassword = ""
    @State private var confirmPassword = ""
    @State private var isSaving = false
    @State private var errorMessage: String?

    private var canSave: Bool {
        !currentPassword.isEmpty && newPassword.count >= 8 && newPassword == confirmPassword
    }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    SecureField("Current password", text: $currentPassword)
                    SecureField("New password (min. 8 characters)", text: $newPassword)
                    SecureField("Confirm new password", text: $confirmPassword)
                }
                if !confirmPassword.isEmpty && newPassword != confirmPassword {
                    Section {
                        Text("New passwords don't match.").foregroundStyle(SentinelTheme.red)
                    }
                }
                if let errorMessage {
                    Section {
                        Text(errorMessage).foregroundStyle(SentinelTheme.red)
                    }
                }
            }
            .formStyle(.grouped)
            .navigationTitle("Change Password")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(isSaving ? "Saving…" : "Save") {
                        Task { await save() }
                    }
                    .disabled(isSaving || !canSave)
                }
            }
        }
    }

    private func save() async {
        isSaving = true
        defer { isSaving = false }
        errorMessage = nil
        do {
            try await model.client.changeMyPassword(currentPassword: currentPassword, newPassword: newPassword)
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}
