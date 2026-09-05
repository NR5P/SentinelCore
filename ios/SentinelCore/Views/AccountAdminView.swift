import SwiftUI

struct AccountAdminView: View {
    @EnvironmentObject private var model: AppModel
    @State private var isLoading = false
    @State private var errorMessage: String?

    var body: some View {
        List {
            if let errorMessage {
                Text(errorMessage).foregroundStyle(SentinelTheme.red)
            }
            Section {
                ForEach(model.accounts) { account in
                    NavigationLink {
                        AccountDetailView(account: account)
                    } label: {
                        VStack(alignment: .leading, spacing: 3) {
                            Text(account.displayName.map { "\(account.username) (\($0))" } ?? account.username)
                                .bold()
                            Text(account.role)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                    }
                }
            }
        }
        .navigationTitle("Accounts")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        // Both links carry their own destination, like every other admin screen here. Routing the
        // toolbar button through a value plus a navigationDestination(for:) did not work: toolbar
        // content is hosted by the navigation bar rather than by the List these modifiers are
        // attached to, so the declaration was not visible from the button's location and tapping
        // Add did nothing. The rows kept working the whole time, being genuinely inside the List.
        .toolbar {
            ToolbarItem {
                NavigationLink {
                    AccountEditView()
                } label: {
                    Label("Add Account", systemImage: "plus")
                }
            }
        }
        .refreshable { await load() }
        .task { await load() }
        .overlay {
            if model.accounts.isEmpty && isLoading {
                ProgressView()
            } else if model.accounts.isEmpty {
                ContentUnavailableView(
                    "No accounts",
                    systemImage: "person.slash",
                    description: Text("Add one to give someone else access.")
                )
            }
        }
    }

    private func load() async {
        isLoading = true
        defer { isLoading = false }
        do {
            model.accounts = try await model.client.accounts()
            errorMessage = nil
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

struct AccountEditView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss

    @State private var username = ""
    @State private var password = ""
    @State private var displayName = ""
    @State private var role = "Viewer"
    @State private var isSaving = false
    @State private var errorMessage: String?

    var body: some View {
        Form {
            Section("New Account") {
                SentinelTextField(label: "Username", text: $username)
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    #endif
                SentinelTextField(
                    label: "Password",
                    prompt: "At least 8 characters",
                    text: $password,
                    secure: true)
                SentinelTextField(label: "Display name (optional)", text: $displayName)
                Picker("Role", selection: $role) {
                    Text("Viewer").tag("Viewer")
                    Text("Admin").tag("Admin")
                }
            }
            if let errorMessage {
                Section {
                    Text(errorMessage).foregroundStyle(SentinelTheme.red)
                }
            }
        }
        .formStyle(.grouped)
        .navigationTitle("Add Account")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItem(placement: .confirmationAction) {
                Button(isSaving ? "Saving…" : "Save") {
                    Task { await save() }
                }
                .disabled(isSaving || username.trimmingCharacters(in: .whitespaces).isEmpty || password.isEmpty)
            }
        }
    }

    private func save() async {
        isSaving = true
        defer { isSaving = false }
        errorMessage = nil
        do {
            let created = try await model.client.createAccount(
                username: username.trimmingCharacters(in: .whitespaces),
                password: password,
                role: role,
                displayName: displayName.trimmingCharacters(in: .whitespaces).isEmpty
                    ? nil : displayName.trimmingCharacters(in: .whitespaces)
            )
            model.accounts.append(created)
            SaveFeedback.hapticSuccess()
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

struct AccountDetailView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss
    let account: Account

    @State private var isWorking = false
    @State private var errorMessage: String?
    @State private var showingResetPassword = false
    @State private var newPassword = ""
    @State private var showingDeleteConfirm = false

    private var otherRole: String { account.role == "Admin" ? "Viewer" : "Admin" }

    var body: some View {
        Form {
            Section {
                LabeledContent("Username", value: account.username)
                if let displayName = account.displayName {
                    LabeledContent("Display name", value: displayName)
                }
                LabeledContent("Role", value: account.role)
            }

            Section {
                Button(isWorking ? "Working…" : "Make \(otherRole)") {
                    Task { await changeRole() }
                }
                .disabled(isWorking)

                Button("Reset Password") {
                    newPassword = ""
                    showingResetPassword = true
                }
                .disabled(isWorking)
            }

            Section {
                Button("Delete Account", role: .destructive) {
                    showingDeleteConfirm = true
                }
                .disabled(isWorking)
            }

            if let errorMessage {
                Section {
                    Text(errorMessage).foregroundStyle(SentinelTheme.red)
                }
            }
        }
        .formStyle(.grouped)
        .navigationTitle(account.username)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .alert("Reset Password", isPresented: $showingResetPassword) {
            SecureField("New password (min. 8 characters)", text: $newPassword)
            Button("Cancel", role: .cancel) {}
            Button("Reset") { Task { await resetPassword() } }
        } message: {
            Text("Choose a new password for \"\(account.username)\".")
        }
        .confirmationDialog(
            "Delete \"\(account.username)\"? This cannot be undone.",
            isPresented: $showingDeleteConfirm,
            titleVisibility: .visible
        ) {
            Button("Delete", role: .destructive) { Task { await delete() } }
            Button("Cancel", role: .cancel) {}
        }
    }

    private func changeRole() async {
        isWorking = true
        defer { isWorking = false }
        errorMessage = nil
        do {
            try await model.client.updateAccountRole(id: account.id, role: otherRole)
            model.accounts = try await model.client.accounts()
            SaveFeedback.hapticSuccess()
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func resetPassword() async {
        guard newPassword.count >= 8 else {
            errorMessage = "Password must be at least 8 characters."
            return
        }
        isWorking = true
        defer { isWorking = false }
        errorMessage = nil
        do {
            try await model.client.resetAccountPassword(id: account.id, newPassword: newPassword)
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func delete() async {
        isWorking = true
        defer { isWorking = false }
        errorMessage = nil
        do {
            try await model.client.deleteAccount(id: account.id)
            model.accounts.removeAll { $0.id == account.id }
            SaveFeedback.hapticSuccess()
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}
