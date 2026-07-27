import SwiftUI

struct SettingsView: View {
    @EnvironmentObject private var model: AppModel
    @State private var address = ""
    @State private var showingDiagnostics = false

    var body: some View {
        Form {
            Section("SERVER") {
                TextField("Dashboard address", text: $address)
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    .keyboardType(.URL)
                    #endif
                HStack {
                    Text("STATUS")
                    Spacer()
                    ConnectionBadge().frame(maxWidth: 190)
                }
                Button("SAVE AND RECONNECT") {
                    Task {
                        model.serverURL = normalized(address)
                        await model.connect(resetNetworkSession: true)
                    }
                }
                .disabled(address.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }

            Section("ACCOUNT") {
                if let username = model.authStatus?.username {
                    LabeledContent("SIGNED IN AS", value: username)
                }
                if let role = model.authStatus?.role {
                    LabeledContent("ROLE", value: role.uppercased())
                }
                if model.authStatus?.enabled == true {
                    Button("SIGN OUT", role: .destructive) {
                        Task { await model.signOut() }
                    }
                } else {
                    Text("Dashboard login is not enabled.")
                        .foregroundStyle(SentinelTheme.textDim)
                }
            }

            Section("SYSTEM") {
                LabeledContent("EVENTS LOADED", value: "\(model.events.count)")
                LabeledContent("CAMERAS", value: "\(model.cameras.count)")
                LabeledContent("MOTION TRIGGERS", value: "\(model.triggers.count)")
                if let lastError = model.lastError {
                    Text(lastError).foregroundStyle(SentinelTheme.red)
                }
            }

            Section("PUSH NOTIFICATIONS") {
                LabeledContent("PERMISSION", value: model.pushAuthorization)
                if let status = model.pushStatus {
                    LabeledContent("CORE", value: status.enabled ? "ENABLED" : "DISABLED")
                    LabeledContent("APPLE PROVIDER", value: status.appleConfigured ? "CONFIGURED" : "NOT CONFIGURED")
                    LabeledContent("REGISTERED DEVICES", value: "\(status.registeredDevices)")
                }
                Button(model.pushRegistrationEnabled ? "REFRESH REGISTRATION" : "ENABLE NOTIFICATIONS") {
                    Task { await model.enablePushNotifications() }
                }
                if model.pushRegistrationEnabled {
                    Button("DISABLE ON THIS DEVICE", role: .destructive) {
                        Task { await model.disablePushNotifications() }
                    }
                }
                // Admin-only on the server (it messages every registered device, not just this
                // one) - hidden rather than disabled for a Viewer, since there's nothing they
                // could do to enable it.
                if model.isAdmin {
                    Button("SEND TEST NOTIFICATION") {
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
                        .foregroundStyle(SentinelTheme.textDim)
                }
            }

            Section("DIAGNOSTICS") {
                DisclosureGroup("CONNECTION LOG", isExpanded: $showingDiagnostics) {
                    if model.diagnostics.isEmpty {
                        Text("No requests recorded yet.")
                            .foregroundStyle(SentinelTheme.textDim)
                    } else {
                        ForEach(model.diagnostics.prefix(30)) { entry in
                            VStack(alignment: .leading, spacing: 3) {
                                HStack {
                                    Text(entry.level.rawValue)
                                        .foregroundStyle(entry.level == .error ? SentinelTheme.red : SentinelTheme.cyan)
                                    Spacer()
                                    Text(entry.timestamp.formatted(date: .omitted, time: .standard))
                                        .foregroundStyle(SentinelTheme.textDim)
                                }
                                .font(.caption2)
                                Text(entry.message)
                                    .font(.caption)
                                    .textSelection(.enabled)
                            }
                        }
                    }
                    Button("CLEAR LOG") { model.clearDiagnostics() }
                }
                Text("Request bodies, passwords, cookies, and server response contents are never written here.")
                    .font(.caption)
                    .foregroundStyle(SentinelTheme.textDim)
            }

            Section {
                Text("This native client manages its connection and push registration locally. Full server configuration, camera editing, motion-zone drawing, and account management remain in the Core dashboard.")
                    .font(.caption)
                    .foregroundStyle(SentinelTheme.textDim)
            }
        }
        .formStyle(.grouped)
        .scrollContentBackground(.hidden)
        .background(SentinelTheme.background)
        .navigationTitle("SETTINGS")
        .onAppear { address = model.serverURL }
        .onChange(of: model.serverURL) { _, newValue in address = newValue }
    }

    private func normalized(_ value: String) -> String {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        let withScheme = trimmed.contains("://") ? trimmed : "http://\(trimmed)"
        return withScheme.hasSuffix("/") ? String(withScheme.dropLast()) : withScheme
    }
}
