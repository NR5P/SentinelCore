import SwiftUI

struct CameraDiscoveryView: View {
    @EnvironmentObject private var model: AppModel

    @State private var interfaces: [ScanInterface] = []
    @State private var selectedAddresses: Set<String> = []
    @State private var isScanning = false
    @State private var results: [ScanResultCamera] = []
    @State private var scanStatus = "Running"
    @State private var errorMessage: String?
    @State private var pollTask: Task<Void, Never>?

    var body: some View {
        List {
            if let errorMessage {
                Text(errorMessage).foregroundStyle(SentinelTheme.red)
            }

            if !isScanning {
                Section("Network Interfaces") {
                    ForEach(interfaces) { iface in
                        Button {
                            toggle(iface.address)
                        } label: {
                            HStack {
                                Image(systemName: selectedAddresses.contains(iface.address)
                                    ? "checkmark.square.fill" : "square")
                                VStack(alignment: .leading) {
                                    Text(iface.name).foregroundStyle(.primary)
                                    Text(iface.description)
                                        .font(.caption)
                                        .foregroundStyle(.secondary)
                                }
                            }
                        }
                        .buttonStyle(.plain)
                    }
                }
                Section {
                    Button("Scan") {
                        Task { await startScan() }
                    }
                    .disabled(selectedAddresses.isEmpty)
                }
            } else {
                Section {
                    HStack {
                        ProgressView()
                        Text(scanStatus == "Completed" ? "Scan complete" : "Scanning…")
                    }
                }
                Section("Results") {
                    if results.isEmpty {
                        Text(scanStatus == "Completed" ? "No cameras found." : "Looking for cameras…")
                            .foregroundStyle(.secondary)
                    }
                    ForEach(results) { result in
                        if result.alreadySaved {
                            HStack {
                                resultLabel(result)
                                Spacer()
                                Image(systemName: "checkmark.circle.fill")
                                    .foregroundStyle(SentinelTheme.green)
                            }
                        } else {
                            NavigationLink {
                                CameraEditView(camera: nil, prefillStreamUrl: result.ipAddress)
                            } label: {
                                resultLabel(result)
                            }
                        }
                    }
                }
            }
        }
        .navigationTitle("Discover Cameras")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .task { await loadInterfaces() }
        .onDisappear { pollTask?.cancel() }
    }

    @ViewBuilder
    private func resultLabel(_ result: ScanResultCamera) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack {
                Text(result.suggestedName).bold()
                if result.needsCredentials {
                    Text("NEEDS CREDENTIALS")
                        .font(.caption2)
                        .foregroundStyle(SentinelTheme.amber)
                }
            }
            Text([result.ipAddress, result.manufacturer, result.model].compactMap { $0 }.joined(separator: " · "))
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private func toggle(_ address: String) {
        if selectedAddresses.contains(address) {
            selectedAddresses.remove(address)
        } else {
            selectedAddresses.insert(address)
        }
    }

    private func loadInterfaces() async {
        do {
            interfaces = try await model.client.fetchScanInterfaces()
            selectedAddresses = Set(interfaces.filter { !$0.looksVirtual }.map(\.address))
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func startScan() async {
        errorMessage = nil
        do {
            let started = try await model.client.startScan(
                ScanRequest(interfaceAddresses: Array(selectedAddresses))
            )
            isScanning = true
            results = []
            scanStatus = "Running"
            pollTask?.cancel()
            pollTask = Task {
                while !Task.isCancelled {
                    do {
                        let snapshot = try await model.client.fetchScanStatus(scanId: started.scanId)
                        results = snapshot.results
                        scanStatus = snapshot.status
                        if snapshot.status == "Completed" { break }
                    } catch {
                        errorMessage = error.localizedDescription
                        break
                    }
                    try? await Task.sleep(for: .seconds(1))
                }
            }
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}
