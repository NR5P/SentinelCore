import SwiftUI

// Whether this server requires a login at all - the web dashboard's LOGIN section. Distinct from
// Manage Accounts, which edits the accounts once login is on: this is the switch itself, and it
// was the one part of that section the apps never had.
//
// Turning it on creates the first admin account in the same step, because a server that demanded a
// login nobody could satisfy would be locked shut. Turning it off is a real loss of protection and
// says so rather than just confirming.
struct DashboardLoginView: View {
    @EnvironmentObject private var model: AppModel

    @State private var username = ""
    @State private var password = ""
    @State private var confirmPassword = ""
    @State private var isWorking = false
    @State private var confirmingDisable = false
    @State private var errorMessage: String?

    private var isEnabled: Bool { model.authStatus?.enabled == true }

    private var canEnable: Bool {
        !username.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && password.count >= 8
            && password == confirmPassword
    }

    var body: some View {
        Form {
            Section {
                LabeledContent("Status", value: isEnabled ? "Login required" : "No login required")
            } footer: {
                Text(isEnabled
                    ? "Everyone signs in to open this dashboard. Sessions live in memory, so a server restart signs everyone out."
                    : "Anyone who can reach this network can open the dashboard and change every setting. That's a reasonable default on a home network you trust — turn it on before this is reachable from anywhere else.")
            }

            if isEnabled {
                Section {
                    Button("Turn Off Login", role: .destructive) { confirmingDisable = true }
                        .disabled(isWorking)
                } footer: {
                    Text("Accounts are kept, not deleted — turning login back on later restores them.")
                }
            } else {
                Section("Create the First Account") {
                    TextField("Username", text: $username)
                        .autocorrectionDisabled()
                        #if os(iOS)
                        .textInputAutocapitalization(.never)
                        #endif
                    SecureField("Password (min. 8 characters)", text: $password)
                    SecureField("Confirm password", text: $confirmPassword)
                    Button(isWorking ? "Working…" : "Require Login") { Task { await enable() } }
                        .disabled(isWorking || !canEnable)
                }
            }

            if !confirmPassword.isEmpty && password != confirmPassword {
                Section {
                    Text("Passwords don't match.").foregroundStyle(SentinelTheme.red)
                }
            }

            if let errorMessage {
                Section {
                    Text(errorMessage).foregroundStyle(SentinelTheme.red)
                }
            }
        }
        .formStyle(.grouped)
        .navigationTitle("Dashboard Login")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .confirmationDialog(
            "Turn off login? Anyone on this network will then have full access to the dashboard and every setting in it.",
            isPresented: $confirmingDisable,
            titleVisibility: .visible
        ) {
            Button("Turn Off Login", role: .destructive) { Task { await disable() } }
            Button("Cancel", role: .cancel) {}
        }
    }

    private func enable() async {
        isWorking = true
        errorMessage = nil
        defer { isWorking = false }
        do {
            try await model.client.enableDashboardLogin(
                username: username.trimmingCharacters(in: .whitespacesAndNewlines),
                password: password)
            password = ""
            confirmPassword = ""
            await model.refreshAuthStatus()
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func disable() async {
        isWorking = true
        errorMessage = nil
        defer { isWorking = false }
        do {
            try await model.client.disableDashboardLogin()
            await model.refreshAuthStatus()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}
