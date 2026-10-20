import SwiftUI

// tvOS port of ios/SentinelCore/Views/ServerSetupView.swift - text entry opens the standard
// tvOS full-screen keyboard, so no iOS-only keyboard/autocapitalization modifiers are needed.
struct ServerSetupView: View {
    @EnvironmentObject private var model: AppModel
    @State private var address = ""

    var body: some View {
        VStack(spacing: 36) {
            BrandHeader().frame(maxWidth: 520)
            HStack(alignment: .top, spacing: 40) {
                welcome
                VStack(alignment: .leading, spacing: 18) {
                    Text("Connect to your server")
                        .foregroundStyle(SentinelTheme.cyan)
                        .font(.title2.bold())

                    discoverySection

                    // verbatim: a plain string literal gets its example URLs turned into links.
                    Text(verbatim: "Or enter its address: the one on your home network, like http://192.168.1.50:3320, or a web address such as https://home.example.com.")
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
        }
        .padding(80)
        .onAppear {
            address = model.serverURL
            model.startServerDiscovery()
        }
        .onSubmit(connect)
    }

    // What someone who just found the app in the App Store needs before an address field means
    // anything. A TV can't open a link, so it names the site to visit on another device.
    private var welcome: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Welcome to Sentinel Core")
                .foregroundStyle(SentinelTheme.green)
                .font(.title2.bold())
            Text("This app connects to Sentinel Core, a home hub that runs on a computer in your own home: camera alerts, live video, movies, music, the family calendar and weather. Your footage and data stay on that computer; this app is how you reach it.")
            Text("Don't have a server yet? Visit nr5p.com on a phone or computer to install it on a Linux PC, then come back here.")
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: 560, alignment: .leading)
        .sentinelCard()
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
        ServerIdentity.forget(normalized(address))
        model.serverURL = normalized(address)
    }

    private func normalized(_ value: String) -> String { Self.normalizedAddress(value) }

    // A home-network address (an IP, a .local name, a bare computer name) gets http://, which is
    // how the dashboard ships. Anything else is an internet address and gets https://: typing
    // "home.example.com" must never send a password across the internet in plain text. Same rule
    // as the iPhone's ServerSetupView and Android's SentinelRepository.normalizeAddress.
    static func normalizedAddress(_ value: String) -> String {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        let scheme = isHomeNetworkAddress(trimmed) ? "http" : "https"
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
