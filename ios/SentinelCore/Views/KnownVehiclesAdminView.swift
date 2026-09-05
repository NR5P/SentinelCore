import SwiftUI

// Number plates the AI should recognise as household rather than a stranger's - the same list the
// web dashboard's KNOWN VEHICLES section edits, which is also what PACKAGE THEFT WATCH consults
// before deciding a pickup was unrecognised.
//
// The save endpoint replaces the whole list rather than patching it, so this edits a local copy
// and sends all of it at once. That also means Save is a deliberate step: an accidental swipe
// doesn't reach the server until it's confirmed.
struct KnownVehiclesAdminView: View {
    @EnvironmentObject private var model: AppModel

    @State private var vehicles: [KnownVehicle] = []
    @State private var newPlate = ""
    @State private var newLabel = ""
    @State private var loaded = false
    @State private var isSaving = false
    @State private var errorMessage: String?
    @StateObject private var feedback = SaveFeedback()

    private var trimmedPlate: String {
        newPlate.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
    }

    private var canAdd: Bool {
        !trimmedPlate.isEmpty
            && !newLabel.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && !vehicles.contains { $0.plate.uppercased() == trimmedPlate }
    }

    var body: some View {
        Form {
            Section {
                if !loaded {
                    Text("Loading…").foregroundStyle(.secondary)
                } else if vehicles.isEmpty {
                    Text("No known vehicles yet.").foregroundStyle(.secondary)
                } else {
                    ForEach(vehicles) { vehicle in
                        VStack(alignment: .leading, spacing: 2) {
                            Text(vehicle.plate).font(.body.monospaced())
                            Text(vehicle.label).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    .onDelete { offsets in
                        vehicles.remove(atOffsets: offsets)
                    }
                }
            } header: {
                Text("Known Vehicles")
            } footer: {
                Text("Plates the AI treats as household rather than a stranger's. Matching ignores spaces and punctuation, so “ABC 123” and “ABC-123” are the same plate.")
            }

            Section("Add a Vehicle") {
                SentinelTextField(label: "Plate", prompt: "ABC 123", text: $newPlate)
                    .autocorrectionDisabled()
                    #if os(iOS)
                    .textInputAutocapitalization(.characters)
                    #endif
                SentinelTextField(label: "Label", prompt: "e.g. Dad's truck", text: $newLabel)
                Button("Add") {
                    vehicles.append(KnownVehicle(plate: trimmedPlate, label: newLabel.trimmingCharacters(in: .whitespacesAndNewlines)))
                    newPlate = ""
                    newLabel = ""
                }
                .disabled(!canAdd)
            }

            Section {
                SaveButton(feedback: feedback, isSaving: isSaving, disabled: !loaded) { Task { await save() } }
            } footer: {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Changes here aren't sent to the server until you save.")
                    if let errorMessage {
                        Text(errorMessage).foregroundStyle(SentinelTheme.red)
                    }
                }
            }
        }
        .formStyle(.grouped)
        .saveFeedback(feedback)
        .navigationTitle("Known Vehicles")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .task { await load() }
    }

    private func load() async {
        guard !loaded else { return }
        do {
            vehicles = try await model.client.knownVehicles()
            loaded = true
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func save() async {
        isSaving = true
        errorMessage = nil
        defer { isSaving = false }
        do {
            try await model.client.saveKnownVehicles(vehicles)
            feedback.saved()
        } catch {
            errorMessage = error.localizedDescription
            feedback.failed("Not saved")
        }
    }
}
