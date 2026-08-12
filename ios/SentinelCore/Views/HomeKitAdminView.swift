import SwiftUI

// Apple Home integration - the web dashboard's APPLE HOMEKIT section. Two halves that look alike
// but aren't: the settings below are ordinary config saved to appsettings, while the status and
// pairing code above come live from the sidecar and are the reason this section can't be
// schema-driven like the other 36.
//
// The pairing code is shown as text rather than as the dashboard's QR image on purpose: this app
// usually *is* the iPhone, and a code you can read and type beats a QR you can't point a camera at
// from the device displaying it. The Home app takes the typed code just the same.
struct HomeKitAdminView: View {
    @EnvironmentObject private var model: AppModel

    @State private var status: HomeKitStatus?
    @State private var settings: HomeKitSettings?
    @State private var isSaving = false
    @State private var confirmingReset = false
    @State private var statusMessage: String?
    @State private var errorMessage: String?

    var body: some View {
        Form {
            Section {
                if let status {
                    LabeledContent("Sidecar", value: status.running ? "Running" : "Not running")
                    if status.running {
                        LabeledContent("Advertising", value: (status.published ?? false) ? "Yes" : "No")
                        LabeledContent("Paired", value: (status.paired ?? false) ? "Yes" : "No")
                        if let accessories = status.accessories, !accessories.isEmpty {
                            LabeledContent("Accessories", value: "\(accessories.count)")
                        }
                    }
                    if let error = status.error {
                        Text(error).font(.caption).foregroundStyle(SentinelTheme.red)
                    }
                } else {
                    Text("Checking HomeKit…").foregroundStyle(.secondary)
                }
            } header: {
                Text("Status")
            } footer: {
                Text("Puts your cameras in Apple's Home app as motion sensors, so alert-worthy events become native iPhone notifications. Live video isn't included — that needs Apple's paid HomeKit Secure Video licensing. Nothing is put on your network until this is turned on.")
            }

            // Only worth showing when there's something to pair with: an unpublished sidecar has
            // no code, and a paired one doesn't need showing again.
            if let code = status?.pairingCode, status?.paired != true {
                Section {
                    Text(code)
                        .font(.title2.monospaced())
                        .foregroundStyle(SentinelTheme.green)
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .center)
                } header: {
                    Text("Pairing Code")
                } footer: {
                    Text("In the Home app choose Add Accessory, then “More options…”, then enter this code.")
                }
            }

            if let current = settings {
                Section("Settings") {
                    Toggle("Enabled", isOn: Binding(
                        get: { current.enabled },
                        set: { settings?.enabled = $0 }))
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Sidecar Address")
                        TextField("http://127.0.0.1:3321", text: Binding(
                            get: { current.sidecarBaseUrl },
                            set: { settings?.sidecarBaseUrl = $0 }))
                            .autocorrectionDisabled()
                            #if os(iOS)
                            .textInputAutocapitalization(.never)
                            .keyboardType(.URL)
                            #endif
                    }
                    VStack(alignment: .leading, spacing: 6) {
                        HStack {
                            Text("Timeout")
                            Spacer()
                            Text("\(current.timeoutSeconds)s").monospacedDigit().foregroundStyle(SentinelTheme.green)
                        }
                        Slider(
                            value: Binding(
                                get: { Double(current.timeoutSeconds) },
                                set: { settings?.timeoutSeconds = Int($0) }),
                            in: 1...120, step: 1)
                    }
                    VStack(alignment: .leading, spacing: 6) {
                        HStack {
                            Text("Minimum Confidence")
                            Spacer()
                            Text(String(format: "%.2f", current.minimumConfidence))
                                .monospacedDigit().foregroundStyle(SentinelTheme.green)
                        }
                        Slider(
                            value: Binding(
                                get: { current.minimumConfidence },
                                set: { settings?.minimumConfidence = $0 }),
                            in: 0...1, step: 0.05)
                        Text("How sure the AI must be before an event is passed to Apple Home.")
                            .font(.caption2).foregroundStyle(.secondary)
                    }
                }

                Section {
                    Button(isSaving ? "Saving…" : "Save") { Task { await save() } }
                        .disabled(isSaving)
                    Button("Reset Pairing", role: .destructive) { confirmingReset = true }
                } footer: {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Resetting drops the pairing and restarts the sidecar, so every Home app on the network has to pair again with a new code.")
                        if let statusMessage {
                            Text(statusMessage).foregroundStyle(SentinelTheme.green)
                        }
                        if let errorMessage {
                            Text(errorMessage).foregroundStyle(SentinelTheme.red)
                        }
                    }
                }
            }
        }
        .formStyle(.grouped)
        .navigationTitle("Apple HomeKit")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .task { await load() }
        .refreshable { await load() }
        .confirmationDialog(
            "Reset HomeKit pairing? Every Home app paired with this server has to pair again with a new code.",
            isPresented: $confirmingReset,
            titleVisibility: .visible
        ) {
            Button("Reset Pairing", role: .destructive) { Task { await reset() } }
            Button("Cancel", role: .cancel) {}
        }
    }

    private func load() async {
        // The status half is allowed to fail on its own - the sidecar is optional, and a settings
        // form that refused to appear because it isn't installed would be the wrong trade.
        status = try? await model.client.homeKitStatus()
        do {
            if settings == nil { settings = try await model.client.homeKitSettings() }
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func save() async {
        guard let settings else { return }
        isSaving = true
        statusMessage = nil
        errorMessage = nil
        defer { isSaving = false }
        do {
            try await model.client.saveHomeKitSettings(settings)
            statusMessage = "Saved."
            status = try? await model.client.homeKitStatus()
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func reset() async {
        statusMessage = nil
        errorMessage = nil
        do {
            try await model.client.resetHomeKitPairing()
            statusMessage = "Pairing reset — the sidecar is restarting."
            status = try? await model.client.homeKitStatus()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}
