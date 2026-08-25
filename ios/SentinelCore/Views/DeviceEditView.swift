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

    private var kinds: [String] { ["light", "switch", "outlet", "fan", "cover", "gate", "garage", "lock", "button", "sensor"] }

    // Kind-flavoured wording so a gate's form says Open/Close and a lock's says Lock/Unlock -
    // the payloads land in the same PayloadOn/PayloadOff either way.
    private var isCoverLike: Bool { ["cover", "gate", "garage"].contains(input.kind) }
    private var isButton: Bool { input.kind == "button" }
    private var isMomentary: Bool { isButton || input.momentary }
    private var onLabel: String {
        if isCoverLike { return "Payload for Open" }
        if input.kind == "lock" { return "Payload for Lock" }
        if isMomentary { return "Payload for Press" }
        return "Payload for ON"
    }
    private var offLabel: String {
        if isCoverLike { return "Payload for Close" }
        if input.kind == "lock" { return "Payload for Unlock" }
        return "Payload for OFF"
    }
    // Brightness only makes sense for things that can hold a level.
    private var supportsBrightnessAtAll: Bool {
        !isMomentary && ["light", "switch", "outlet", "fan"].contains(input.kind)
    }

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
                SentinelTextField(label: "Name", prompt: "e.g. Garage Door", text: $input.name)
                SentinelTextField(label: "Room (optional)", prompt: "e.g. Garage", text: $input.room)
                Picker("Kind", selection: $input.kind) {
                    ForEach(kinds, id: \.self) { kind in
                        Text(kind.capitalized).tag(kind)
                    }
                }
                Toggle("Enabled", isOn: $input.enabled)
                if !isButton {
                    Toggle("Momentary (press only)", isOn: $input.momentary)
                }
                if isMomentary {
                    Text("A momentary device is a pulse: one press publishes the Press payload and nothing more is expected back - a gate relay wired to pulse, a garage remote, a doorbell.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }

            Section {
                SentinelTextField(label: "Command Topic", prompt: "cmnd/kitchen/POWER", text: $input.commandTopic)
                    .textFieldStyleForTopic()
                SentinelTextField(label: "State Topic", prompt: "stat/kitchen/POWER", text: $input.stateTopic)
                    .textFieldStyleForTopic()
            } header: {
                Text("MQTT Topics")
            } footer: {
                Text("The topic this device takes commands on, e.g. cmnd/kitchen/POWER or zigbee2mqtt/kitchen/set, and the one it reports its state on. No + or # wildcards.")
            }

            if isCoverLike && !isMomentary {
                Section {
                    SentinelTextField(label: "Payload for Stop (optional)", prompt: "STOP", text: $input.payloadStop)
                        .textFieldStyleForTopic()
                } footer: {
                    Text("Some motors take a third command to halt mid-travel - STOP for a Home Assistant cover. Leave blank if this one doesn't, and no Stop button is shown.")
                }
            }

            if supportsBrightnessAtAll {
            Section("Brightness") {
                Toggle("Supports brightness", isOn: $input.supportsBrightness)
                if input.supportsBrightness {
                    SentinelTextField(label: "Brightness Command Topic", text: $input.brightnessCommandTopic)
                        .textFieldStyleForTopic()
                    SentinelTextField(label: "Brightness State Topic", text: $input.brightnessStateTopic)
                        .textFieldStyleForTopic()
                    Stepper("Scale: \(input.brightnessScale)", value: $input.brightnessScale, in: 1...65535, step: 1)
                    Text("The device's own value for full brightness - 255 for most Zigbee bulbs, 254 for Zigbee2MQTT, 100 for Tasmota. The slider is always 0-100 and is converted for you.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            }

            Section(isExpanded: $showAdvanced) {
                Picker("Payload Schema", selection: $input.schema) {
                    Text("Raw (ON / OFF)").tag("raw")
                    Text("JSON").tag("json")
                }
                if input.schema == "json" {
                    SentinelTextField(label: "State Value Key", prompt: "POWER", text: $input.stateValueTemplate)
                        .textFieldStyleForTopic()
                }
                SentinelTextField(label: "Availability Topic", prompt: "tele/kitchen/LWT", text: $input.availabilityTopic)
                    .textFieldStyleForTopic()
                SentinelTextField(label: onLabel, text: $input.payloadOn).textFieldStyleForTopic()
                if !isMomentary {
                    SentinelTextField(label: offLabel, text: $input.payloadOff).textFieldStyleForTopic()
                }
                SentinelTextField(label: "Payload for online", prompt: "Online", text: $input.payloadAvailable).textFieldStyleForTopic()
                SentinelTextField(label: "Payload for offline", prompt: "Offline", text: $input.payloadNotAvailable).textFieldStyleForTopic()
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

        // Brightness left switched on from an earlier kind choice would be rejected server-side
        // for a momentary device - clear it rather than surface a confusing error.
        var toSave = input
        if isMomentary { toSave.supportsBrightness = false }

        do {
            if let device {
                _ = try await model.client.updateDevice(id: device.id, toSave)
            } else {
                _ = try await model.client.createDevice(toSave)
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
