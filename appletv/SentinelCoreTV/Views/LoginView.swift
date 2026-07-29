import SwiftUI

// tvOS port of ios/SentinelCore/Views/LoginView.swift.
struct LoginView: View {
    @EnvironmentObject private var model: AppModel
    @State private var username = ""
    @State private var password = ""
    @State private var errorMessage: String?
    @State private var signingIn = false

    var body: some View {
        VStack(spacing: 36) {
            BrandHeader().frame(maxWidth: 520)
            VStack(alignment: .leading, spacing: 18) {
                Text("Sign in").foregroundStyle(SentinelTheme.cyan).font(.title2.bold())
                TextField("Username", text: $username)
                SecureField("Password", text: $password)
                if let errorMessage {
                    Text(errorMessage).foregroundStyle(SentinelTheme.red)
                }
                Button(signingIn ? "Connecting…" : "Sign In") {
                    Task { await signIn() }
                }
                .buttonStyle(.borderedProminent)
                .disabled(signingIn || username.isEmpty || password.isEmpty)
            }
            .sentinelCard()
            .frame(maxWidth: 600)
        }
        .padding(80)
        .onSubmit {
            guard !signingIn, !username.isEmpty, !password.isEmpty else { return }
            Task { await signIn() }
        }
    }

    private func signIn() async {
        signingIn = true
        defer { signingIn = false }
        do {
            try await model.signIn(username: username, password: password)
            password = ""
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}
