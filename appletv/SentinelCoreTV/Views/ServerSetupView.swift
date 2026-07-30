import SwiftUI

// tvOS port of ios/SentinelCore/Views/ServerSetupView.swift - text entry opens the standard
// tvOS full-screen keyboard, so no iOS-only keyboard/autocapitalization modifiers are needed.
struct ServerSetupView: View {
    @EnvironmentObject private var model: AppModel
    @State private var address = ""

    var body: some View {
        VStack(spacing: 36) {
            BrandHeader().frame(maxWidth: 520)
            VStack(alignment: .leading, spacing: 18) {
                Text("Connect to your server")
                    .foregroundStyle(SentinelTheme.cyan)
                    .font(.title2.bold())

                discoverySection

                Text("Or enter the local-network address of the Sentinel Core dashboard, for example http://192.168.1.50:3320")
                    .foregroundStyle(.secondary)
                TextField("http://192.168.1.50:3320", text: $address)
                Button("Connect") {
                    connect()
                }
                .buttonStyle(.borderedProminent)
                .disabled(address.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
            .sentinelCard()
            .frame(maxWidth: 640)
        }
        .padding(80)
        .onAppear {
            address = model.serverURL
            model.startServerDiscovery()
        }
        .onSubmit(connect)
    }

    // Purely an assist on top of manual entry below, which always stays visible - discovery can
    // legitimately find nothing (client/AP isolation, a different VLAN, local-network permission
    // denied, ...), and that must never block getting connected by typing the address instead.
    @ViewBuilder
    private var discoverySection: some View {
        if model.discoveredServers.isEmpty {
            Label("Searching your local network for a server…", systemImage: "antenna.radiowaves.left.and.right")
                .font(.callout)
                .foregroundStyle(.secondary)
        } else {
            VStack(alignment: .leading, spacing: 14) {
                Text("Found on your network")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                ForEach(model.discoveredServers) { server in
                    Button {
                        address = server.addressString
                        connect()
                    } label: {
                        HStack {
                            VStack(alignment: .leading, spacing: 4) {
                                Text(server.name).foregroundStyle(.primary)
                                Text(server.addressString)
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                            }
                            Spacer()
                            Image(systemName: "chevron.right")
                                .foregroundStyle(.secondary)
                        }
                    }
                    .buttonStyle(.card)
                }
            }
        }
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
