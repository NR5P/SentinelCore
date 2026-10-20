import SwiftUI

struct ServerSetupView: View {
    @EnvironmentObject private var model: AppModel
    @State private var address = ""

    var body: some View {
        ScrollView {
            VStack(spacing: 24) {
                BrandHeader().frame(maxWidth: 420)
                welcome
                VStack(alignment: .leading, spacing: 14) {
                    Text("Connect to your server")
                        .foregroundStyle(SentinelTheme.cyan)
                        .font(.headline)

                    discoverySection

                    // verbatim: a plain string literal gets its example URLs turned into links.
                    Text(verbatim: "Or enter its address: the one on your home network, like http://192.168.1.50:3320, or a web address such as https://home.example.com.")
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                    TextField("http://192.168.1.50:3320", text: $address)
                        .textFieldStyle(.roundedBorder)
                        #if os(iOS)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .keyboardType(.URL)
                        #endif
                    Button("Connect") {
                        connect()
                    }
                    .buttonStyle(.sentinelProminent)
                    .disabled(address.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
                .sentinelCard()
                .frame(maxWidth: 500)
            }
            .padding()
            .frame(maxWidth: .infinity)
        }
        .onAppear {
            address = model.serverURL
            model.startServerDiscovery()
        }
        .onSubmit(connect)
    }

    // What someone who just found the app in a store needs before an address field means anything:
    // what the app is for, that it talks to a server in their own home, and where that server comes
    // from. The link is to the project's site (downloads and docs, no prices) - never a purchase.
    private var welcome: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Welcome to Sentinel Core")
                .foregroundStyle(SentinelTheme.green)
                .font(.headline)
            Text("This app connects to Sentinel Core, a home hub that runs on a computer in your own home: AI camera alerts, the family organizer, the alarm, movies, music and weather. Your footage and data stay on that computer; this app is how you reach it.")
                .fixedSize(horizontal: false, vertical: true)
            Text("Don't have a server yet? Install it on a Linux PC first, then come back here.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Link(destination: URL(string: "https://nr5p.com")!) {
                Label("Set up a server at nr5p.com", systemImage: "arrow.up.right.square")
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .sentinelCard()
        .frame(maxWidth: 500)
    }

    // Purely an assist on top of manual entry below, which always stays visible - discovery can
    // legitimately find nothing (client/AP isolation, a different VLAN, local-network permission
    // denied, ...), and that must never block getting connected by typing the address instead.
    @ViewBuilder
    private var discoverySection: some View {
        if model.discoveredServers.isEmpty {
            Text("Searching your local network for a server…")
                .font(.caption)
                .foregroundStyle(.secondary)
        } else {
            VStack(alignment: .leading, spacing: 8) {
                Text("Found on your network")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                ForEach(model.discoveredServers) { server in
                    Button {
                        address = server.addressString
                        connect()
                    } label: {
                        HStack {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(server.name).foregroundStyle(.primary)
                                Text(server.addressString)
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                            }
                            Spacer()
                            Image(systemName: "chevron.right")
                                .foregroundStyle(.secondary)
                        }
                        .padding(10)
                        .background(SentinelTheme.cyan.opacity(0.08), in: RoundedRectangle(cornerRadius: 8))
                    }
                    .buttonStyle(.plain)
                }
            }
        }
    }

    private func connect() {
        ServerIdentity.forget(normalized(address))
        model.serverURL = normalized(address)
    }

    // A home-network address (an IP, a .local name, a bare computer name) gets http://, which is
    // how the dashboard ships. Anything else is an internet address and gets https://: typing
    // "home.example.com" must never send a password across the internet in plain text.
    private func normalized(_ value: String) -> String {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        let scheme = Self.isHomeNetworkAddress(trimmed) ? "http" : "https"
        let withScheme = trimmed.contains("://") ? trimmed : "\(scheme)://\(trimmed)"
        return withScheme.hasSuffix("/") ? String(withScheme.dropLast()) : withScheme
    }

    static func isHomeNetworkAddress(_ typed: String) -> Bool {
        guard let host = URLComponents(string: "x://\(typed)")?.host?.lowercased(), !host.isEmpty else { return true }
        if host == "localhost" || !host.contains(".") || host.contains(":") { return true }
        if host.split(separator: ".").allSatisfy({ Int($0) != nil }) { return true }
        return [".local", ".lan", ".home", ".internal", ".home.arpa"].contains { host.hasSuffix($0) }
    }
}
