import SwiftUI

// Simplified tvOS port of ios/SentinelCore/Views/SettingsView.swift - camera/account admin and
// the change-password sheet are dropped (management stays on iOS/macOS/web, where typing and
// fine-grained editing are practical); server address, sign-in status, push registration, and a
// connection log remain.
struct SettingsView: View {
    @EnvironmentObject private var model: AppModel
    @State private var address = ""
    @State private var remoteAddress = ""
    @State private var showingDiagnostics = false

    var body: some View {
        Form {
            Section("Server") {
                TextField("Dashboard address", text: $address)
                TextField("Remote address (optional)", text: $remoteAddress)
                HStack {
                    Text("Status")
                    Spacer()
                    if model.hasRemoteServerURL {
                        Text(model.isUsingRemote ? "via Remote" : "via Local")
                            .foregroundStyle(.secondary)
                    }
                    ConnectionBadge()
                }
                Text("A Cloudflare Tunnel (or any other externally-reachable) address to use when the local address can't be reached.")
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

            Section("System") {
                LabeledContent("Alerts loaded", value: "\(model.events.count)")
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
        .navigationTitle("Settings")
        .onAppear {
            address = model.serverURL
            remoteAddress = model.remoteServerURL
        }
        .onChange(of: model.serverURL) { _, newValue in address = newValue }
        .onChange(of: model.remoteServerURL) { _, newValue in remoteAddress = newValue }
    }

    private func normalized(_ value: String, defaultScheme: String = "http") -> String {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        let withScheme = trimmed.contains("://") ? trimmed : "\(defaultScheme)://\(trimmed)"
        return withScheme.hasSuffix("/") ? String(withScheme.dropLast()) : withScheme
    }
}
