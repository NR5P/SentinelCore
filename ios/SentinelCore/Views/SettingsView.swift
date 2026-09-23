import OrganizerKit
import SwiftUI

struct SettingsView: View {
    private var deviceCameraLabel: String {
        #if os(iOS)
        "Use This iPhone as a Camera"
        #else
        "Use This Mac as a Camera"
        #endif
    }
    @EnvironmentObject private var model: AppModel
    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
    @State private var address = ""
    @State private var remoteAddress = ""
    @State private var showingDiagnostics = false
    @State private var showingChangePassword = false

    var body: some View {
        Form {
            // On an iPhone with both the Organizer and the Device Hub on, the Device Hub lives
            // here rather than in the tab bar - see AppSection.compactTabCases.
            if horizontalSizeClass == .compact,
               AppSection.deviceHubIsInSettings(deviceHubEnabled: model.deviceHubEnabled,
                                                organizerEnabled: model.organizer.status?.enabled ?? model.organizerLastKnownEnabled) {
                Section {
                    NavigationLink {
                        DeviceHubView(isActive: true)
                    } label: {
                        Label("Device Hub", systemImage: "lightbulb.fill")
                    }
                }
            }
            Section("Server") {
                // Both addresses are typed in by hand, and the label has to survive being filled
                // in: a bare TextField in a Form draws no box, and its placeholder - the only
                // thing naming it - disappears the moment there's an address in it.
                SentinelTextField(
                    label: "Dashboard address",
                    prompt: "http://192.168.1.50:3320",
                    text: $address)
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    .keyboardType(.URL)
                    #endif
                SentinelTextField(
                    label: "Remote address (optional)",
                    prompt: "https://sentinel.example.com",
                    text: $remoteAddress)
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

            if model.organizer.status?.enabled ?? model.organizerLastKnownEnabled {
                OrganizerOfflineSettingsSection()
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
                    Button {
                        model.showDeviceCamera = true
                    } label: {
                        Label(deviceCameraLabel, systemImage: "video.badge.plus")
                    }
                }
                Section {
                    NavigationLink("Manage Accounts") {
                        AccountAdminView()
                    }
                    // The switch itself, as opposed to the accounts it governs - the web
                    // dashboard's LOGIN section, which had no counterpart here until now.
                    NavigationLink("Dashboard Login") {
                        DashboardLoginView()
                    }
                } header: {
                    Text("Accounts")
                } footer: {
                    Text("Whether opening the dashboard requires a login at all, and who can.")
                }
                Section("Known Faces") {
                    NavigationLink("Manage Known Faces") {
                        KnownFacesAdminView()
                    }
                }
                Section {
                    NavigationLink("Known Vehicles") {
                        KnownVehiclesAdminView()
                    }
                } header: {
                    Text("Known Vehicles")
                } footer: {
                    Text("Number plates the AI treats as household rather than a stranger's.")
                }
                Section {
                    NavigationLink("Manage Schedules") {
                        ScheduleListView()
                    }
                } header: {
                    Text("Schedules")
                } footer: {
                    // A row here rather than a tab of its own: the iPhone tab bar is already at
                    // the five items UIKit can show without collapsing into a "More" list, which
                    // these screens cannot survive - see AppSection.compactTabCases.
                    Text("Switch a device, publish MQTT, or call an API endpoint at a time of your choosing — or at sunset, or when it gets dark.")
                }
                Section {
                    NavigationLink("Server Settings") {
                        ServerSettingsView()
                    }
                    NavigationLink("Prompt Profiles") {
                        PromptProfilesAdminView()
                    }
                    NavigationLink("Apple HomeKit") {
                        HomeKitAdminView()
                    }
                    NavigationLink("Backup") {
                        BackupAdminView()
                    }
                    NavigationLink("License") {
                        LicenseAdminView()
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
                LabeledContent("Motion only", value: "\(model.activity.filter { !$0.isAnalyzed && !$0.isRecording }.count)")
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
                Text("This app manages its own connection and push registration. Server settings edited here and on the web dashboard are the same settings - a change in either place shows up in both.")
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
                    SentinelTextField(label: "Current password", text: $currentPassword, secure: true)
                    SentinelTextField(label: "New password", prompt: "At least 8 characters", text: $newPassword, secure: true)
                    SentinelTextField(label: "Confirm new password", text: $confirmPassword, secure: true)
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
            SaveFeedback.hapticSuccess()
            dismiss()

            // The server ends every session for the account when the password changes, this
            // device's included, so that a cookie somebody else is holding stops working.
            // Reconnecting immediately is what turns that into a sign-in prompt: without it the
            // app looks connected until whatever the user does next fails, which reads as a bug
            // rather than as the sign-out it is.
            await model.connect()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}
