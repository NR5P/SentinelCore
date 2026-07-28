import SwiftUI

struct ServerSetupView: View {
    @EnvironmentObject private var model: AppModel
    @State private var address = ""

    var body: some View {
        VStack(spacing: 24) {
            BrandHeader().frame(maxWidth: 420)
            VStack(alignment: .leading, spacing: 14) {
                Text("Connect to your server")
                    .foregroundStyle(SentinelTheme.cyan)
                    .font(.headline)
                Text("Enter the local-network address of the Sentinel Core dashboard.")
                    .foregroundStyle(.secondary)
                TextField("http://192.168.1.50:3320", text: $address)
                    .textFieldStyle(.roundedBorder)
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    .keyboardType(.URL)
                    #endif
                Button("Connect") {
                    connect()
                }
                .buttonStyle(.borderedProminent)
                .disabled(address.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
            .sentinelCard()
            .frame(maxWidth: 500)
        }
        .padding()
        .onAppear { address = model.serverURL }
        .onSubmit(connect)
    }

    private func connect() {
        model.serverURL = normalized(address)
    }

    private func normalized(_ value: String) -> String {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        let withScheme = trimmed.contains("://") ? trimmed : "http://\(trimmed)"
        return withScheme.hasSuffix("/") ? String(withScheme.dropLast()) : withScheme
    }
}
