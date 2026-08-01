import SwiftUI

// Add or edit one Device Hub device. The advanced payload fields are tucked behind a disclosure
// because discovery fills them in correctly for the overwhelming majority of devices - someone
// typing a device in by hand usually only needs a name and two topics.
struct DeviceEditView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss

    let device: HubDevice?
    // A discovery candidate the user chose to review before saving, if this was opened that way.
    let prefill: HubDeviceInput?
    let onSaved: () -> Void

    @State private var input = HubDeviceInput()
    @State private var isSaving = false
    @State private var errors: [String] = []
    @State private var showAdvanced = false

    init(device: HubDevice?, prefill: HubDeviceInput? = nil, onSaved: @escaping () -> Void) {
        self.device = device
        self.prefill = prefill
        self.onSaved = onSaved
    }

    private var isNew: Bool { device == nil }

    private var kinds: [String] { ["light", "switch", "outlet", "fan", "cover", "lock", "sensor"] }

    var body: some View {
        Form {
            if !errors.isEmpty {
                Section {
                    ForEach(errors, id: \.self) { error in
                        Text(error).foregroundStyle(SentinelTheme.red).font(.callout)
                    }
                }
            }

            Section("Device") {
                TextField("Name", text: $input.name)
                TextField("Room (optional)", text: $input.room)
                Picker("Kind", selection: $input.kind) {
                    ForEach(kinds, id: \.self) { kind in
                        Text(kind.capitalized).tag(kind)
                    }
                }
                Toggle("Enabled", isOn: $input.enabled)
            }

            Section {
                TextField("Command Topic", text: $input.commandTopic)
                    .textFieldStyleForTopic()
                TextField("State Topic", text: $input.stateTopic)
                    .textFieldStyleForTopic()
            } header: {
                Text("MQTT Topics")
            } footer: {
                Text("The topic this device takes commands on, e.g. cmnd/kitchen/POWER or zigbee2mqtt/kitchen/set, and the one it reports its state on. No + or # wildcards.")
            }

            Section("Brightness") {
                Toggle("Supports brightness", isOn: $input.supportsBrightness)
                if input.supportsBrightness {
                    TextField("Brightness Command Topic", text: $input.brightnessCommandTopic)
                        .textFieldStyleForTopic()
                    TextField("Brightness State Topic", text: $input.brightnessStateTopic)
                        .textFieldStyleForTopic()
                    Stepper("Scale: \(input.brightnessScale)", value: $input.brightnessScale, in: 1...65535, step: 1)
                    Text("The device's own value for full brightness - 255 for most Zigbee bulbs, 254 for Zigbee2MQTT, 100 for Tasmota. The slider is always 0-100 and is converted for you.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }

            Section(isExpanded: $showAdvanced) {
                Picker("Payload Schema", selection: $input.schema) {
                    Text("Raw (ON / OFF)").tag("raw")
                    Text("JSON").tag("json")
                }
                if input.schema == "json" {
                    TextField("State Value Key", text: $input.stateValueTemplate)
                        .textFieldStyleForTopic()
                }
                TextField("Availability Topic", text: $input.availabilityTopic)
                    .textFieldStyleForTopic()
                TextField("Payload for ON", text: $input.payloadOn).textFieldStyleForTopic()
                TextField("Payload for OFF", text: $input.payloadOff).textFieldStyleForTopic()
                TextField("Payload for online", text: $input.payloadAvailable).textFieldStyleForTopic()
                TextField("Payload for offline", text: $input.payloadNotAvailable).textFieldStyleForTopic()
                Toggle("Optimistic", isOn: $input.optimistic)
                Text("Optimistic assumes a command worked instead of waiting for the device to confirm. Needed for devices that never report their state.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } header: {
                Text("Advanced")
            }
        }
        .formStyle(.grouped)
        .navigationTitle(isNew ? "Add Device" : "Edit Device")
        .toolbar {
            ToolbarItem(placement: .cancellationAction) {
                Button("Cancel") { dismiss() }
            }
            ToolbarItem(placement: .confirmationAction) {
                Button("Save") { Task { await save() } }
                    .disabled(isSaving || input.name.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .onAppear {
            if let device {
                input = HubDeviceInput(device: device)
            } else if let prefill {
                input = prefill
                // A JSON device found by discovery always keys its state off "state"; without
                // this the server rejects the save for a missing template.
                if input.schema == "json" && input.stateValueTemplate.isEmpty {
                    input.stateValueTemplate = "state"
                }
            }
        }
    }

    private func save() async {
        isSaving = true
        defer { isSaving = false }
        errors = []

        do {
            if let device {
                _ = try await model.client.updateDevice(id: device.id, input)
            } else {
                _ = try await model.client.createDevice(input)
            }
            onSaved()
            dismiss()
        } catch {
            errors = [error.localizedDescription]
        }
    }
}

private extension View {
    // Topics and payloads are case-sensitive and never sentences - autocapitalisation and
    // autocorrect actively corrupt them.
    func textFieldStyleForTopic() -> some View {
        #if os(iOS)
        self.textInputAutocapitalization(.never).autocorrectionDisabled()
        #else
        self.autocorrectionDisabled()
        #endif
    }
}
