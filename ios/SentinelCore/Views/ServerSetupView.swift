import SwiftUI

struct ServerSetupView: View {
    @EnvironmentObject private var model: AppModel
    @State private var address = ""

    var body: some View {
        VStack(spacing: 24) {
            BrandHeader().frame(maxWidth: 420)
            VStack(alignment: .leading, spacing: 14) {
                Text("CONNECT TO YOUR SERVER")
                    .foregroundStyle(SentinelTheme.cyan)
                    .font(.headline)
                Text("Enter the local-network address of the Sentinel Core dashboard.")
                    .foregroundStyle(SentinelTheme.textDim)
                TextField("http://192.168.1.50:3320", text: $address)
                    .textFieldStyle(.roundedBorder)
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    .keyboardType(.URL)
                    #endif
                Button("CONNECT") {
                    connect()
                }
                .buttonStyle(.borderedProminent)
                .disabled(address.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
            .sentinelPanel()
            .frame(maxWidth: 500)
        }
        .padding()
        .foregroundStyle(SentinelTheme.text)
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
