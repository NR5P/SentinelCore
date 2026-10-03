import SwiftUI

// Listens to the MQTT broker for devices that announce themselves (Home Assistant discovery) and
// for topics that look like a switch, then offers what it found. Nothing is added until the user
// picks it - the same scan/poll/pick shape as CameraDiscoveryView's network scan.
struct DeviceDiscoveryView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss

    let onSaved: () -> Void

    @State private var isScanning = false
    @State private var isComplete = false
    @State private var results: [DeviceCandidate] = []
    @State private var errorMessage: String?
    @State private var addingIds: Set<String> = []
    @State private var addedIds: Set<String> = []
    @State private var reviewing: HubDeviceInput?

    var body: some View {
        List {
            if let errorMessage {
                Text(errorMessage).foregroundStyle(SentinelTheme.red).font(.callout)
            }

            if !isScanning && !isComplete {
                Section {
                    Button("Scan the broker") { Task { await scan() } }
                } footer: {
                    Text("Devices that announce themselves for Home Assistant show up immediately. Others are found by listening for topics that look like a switch, so a device only appears if it says something while the scan runs.")
                }
            }

            if isScanning {
                Section {
                    HStack {
                        ProgressView()
                        Text("Listening to the broker…").foregroundStyle(.secondary)
                    }
                }
            }

            if isComplete {
                Section("Found") {
                    if results.isEmpty {
                        Text("Nothing found. Try again, or add the device by hand if you know its topics.")
                            .foregroundStyle(.secondary)
                    }
                    ForEach(results) { candidate in
                        row(for: candidate)
                    }
                }
                Section {
                    Button("Scan again") { Task { await scan() } }
                }
            }
        }
        .navigationTitle("Discover Devices")
        .toolbar {
            ToolbarItem(placement: .cancellationAction) {
                Button("Done") { dismiss() }
            }
        }
        .sheet(item: $reviewing) { prefill in
            NavigationStack {
                DeviceEditView(device: nil, prefill: prefill) {
                    onSaved()
                    dismiss()
                }
            }
            .deviceHubSheetFrame()
        }
    }

    @ViewBuilder
    private func row(for candidate: DeviceCandidate) -> some View {
        let isAdded = candidate.alreadyAdded || addedIds.contains(candidate.id)

        VStack(alignment: .leading, spacing: 6) {
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text(candidate.suggestedName)
                    Text("\(candidate.kind) · \(candidate.schema) payloads")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Text(candidate.commandTopic.isEmpty ? candidate.stateTopic : candidate.commandTopic)
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }

                Spacer()

                if addingIds.contains(candidate.id) {
                    ProgressView()
                } else if isAdded {
                    Image(systemName: "checkmark.circle.fill").foregroundStyle(SentinelTheme.green)
                } else {
                    HStack(spacing: 12) {
                        Button("Review") { reviewing = HubDeviceInput(candidate: candidate) }
                            .buttonStyle(.bordered)
                        Button("Add") { Task { await add(candidate) } }
                            .buttonStyle(.sentinelProminent)
                    }
                }
            }
        }
        .padding(.vertical, 2)
    }

    private func scan() async {
        isScanning = true
        isComplete = false
        results = []
        errorMessage = nil
        defer { isScanning = false }

        do {
            let start = try await model.client.startDeviceScan()
            // The server reports its own scan window, so a longer window configured in Settings
            // is waited out rather than timing out early here.
            let deadline = Date().addingTimeInterval(Double(start.scanSeconds) + 15)

            while Date() < deadline {
                try await Task.sleep(for: .seconds(1))
                let snapshot = try await model.client.deviceScanStatus(scanId: start.scanId)
                if snapshot.status == "completed" {
                    results = snapshot.results
                    isComplete = true
                    return
                }
            }
            errorMessage = "The scan did not finish in time."
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func add(_ candidate: DeviceCandidate) async {
        addingIds.insert(candidate.id)
        defer { addingIds.remove(candidate.id) }

        var input = HubDeviceInput(candidate: candidate)
        if input.schema == "json" && input.stateValueTemplate.isEmpty {
            input.stateValueTemplate = "state"
        }

        do {
            _ = try await model.client.createDevice(input)
            addedIds.insert(candidate.id)
            onSaved()
        } catch {
            // Most failures here are a field the scan couldn't infer - send the user to the
            // pre-filled form where they can see and fix it, rather than just an error string.
            errorMessage = error.localizedDescription
            reviewing = input
        }
    }
}

// Needed for .sheet(item:) - a prefilled input has no natural identity of its own.
extension HubDeviceInput: Identifiable {
    var id: String { commandTopic.isEmpty ? name : commandTopic }
}
