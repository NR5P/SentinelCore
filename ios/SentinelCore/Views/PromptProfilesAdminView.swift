import SwiftUI

// The AI prompt profiles - the web dashboard's PROMPT PROFILES section. Each profile is a system
// prompt plus the questions the model is asked about an image, and cameras and gate checks pick a
// profile by its key.
//
// Only the fields worth editing on a phone are shown. Everything else in a profile - most
// importantly its crop region, which is a rectangle no phone form should be drawing - is kept
// exactly as the server sent it and written back untouched. That matters because the save
// endpoint replaces the whole dictionary: a screen that modelled only what it displayed would
// erase the rest simply by saving. See promptProfiles() in SentinelClient.
struct PromptProfilesAdminView: View {
    @EnvironmentObject private var model: AppModel

    // The originals, kept verbatim so unedited fields survive the round trip.
    @State private var raw: [String: SettingsValue] = [:]
    @State private var keys: [String] = []
    @State private var loaded = false
    @State private var loadError: String?

    var body: some View {
        Group {
            if let loadError {
                VStack(spacing: 12) {
                    Text(loadError).foregroundStyle(.secondary).multilineTextAlignment(.center)
                    Button("Retry") { Task { await load(force: true) } }
                }
                .padding()
            } else if !loaded {
                ProgressView("Loading profiles…")
            } else if keys.isEmpty {
                Text("No prompt profiles configured.").foregroundStyle(.secondary)
            } else {
                List(keys, id: \.self) { key in
                    NavigationLink {
                        PromptProfileEditView(
                            profileKey: key,
                            profile: fields(for: key),
                            onSave: { updated in await save(key: key, fields: updated) })
                    } label: {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(displayName(for: key)).font(.body)
                            Text(key).font(.caption.monospaced()).foregroundStyle(.secondary)
                        }
                    }
                }
            }
        }
        .navigationTitle("Prompt Profiles")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .task { await load(force: false) }
    }

    private func fields(for key: String) -> [String: SettingsValue] {
        if case .object(let fields)? = raw[key] { return fields }
        return [:]
    }

    private func displayName(for key: String) -> String {
        let name = fields(for: key)["displayName"]?.displayString ?? ""
        return name.isEmpty ? key : name
    }

    private func load(force: Bool) async {
        guard force || !loaded else { return }
        loadError = nil
        do {
            raw = try await model.client.promptProfiles()
            // Sorted by key so the list doesn't reshuffle between visits - a dictionary has no
            // order of its own, and a settings list that reorders itself is disorienting.
            keys = raw.keys.sorted()
            loaded = true
        } catch {
            loadError = error.localizedDescription
        }
    }

    // Merges the edited fields back over the original profile object, so anything this screen
    // never showed is preserved, then sends the whole dictionary as the endpoint expects.
    private func save(key: String, fields updated: [String: SettingsValue]) async -> String? {
        var profile = fields(for: key)
        for (name, value) in updated { profile[name] = value }

        var all = raw
        all[key] = .object(profile)

        do {
            try await model.client.savePromptProfiles(all)
            raw = all
            return nil
        } catch {
            return error.localizedDescription
        }
    }
}

private struct PromptProfileEditView: View {
    @Environment(\.dismiss) private var dismiss

    let profileKey: String
    let profile: [String: SettingsValue]
    let onSave: ([String: SettingsValue]) async -> String?

    @State private var displayName = ""
    @State private var systemPrompt = ""
    @State private var questions: [String] = []
    @State private var newQuestion = ""
    @State private var licensePlate = false
    @State private var faceRecognition = false
    @State private var seeded = false
    @State private var isSaving = false
    @State private var statusMessage: String?
    @State private var errorMessage: String?

    var body: some View {
        Form {
            Section {
                TextField("Display name", text: $displayName)
            } header: {
                Text("Name")
            } footer: {
                Text("Only what's shown in the UI. Cameras and gate checks point at the key “\(profileKey)”, which never changes — so renaming here breaks nothing.")
            }

            Section {
                TextEditor(text: $systemPrompt)
                    .frame(minHeight: 120)
                    .font(.body.monospaced())
            } header: {
                Text("System Prompt")
            } footer: {
                Text("How the model is told to behave before it's shown the image.")
            }

            Section {
                ForEach(Array(questions.enumerated()), id: \.offset) { index, question in
                    Text(question).font(.callout)
                        .swipeActions {
                            Button("Delete", role: .destructive) { questions.remove(at: index) }
                        }
                }
                HStack {
                    TextField("Add a question", text: $newQuestion)
                    Button("Add") {
                        questions.append(newQuestion.trimmingCharacters(in: .whitespacesAndNewlines))
                        newQuestion = ""
                    }
                    .disabled(newQuestion.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
            } header: {
                Text("Questions")
            } footer: {
                Text("What the model is asked about each image. At least one is required.")
            }

            Section("Extras") {
                Toggle("License Plate Recognition", isOn: $licensePlate)
                Toggle("Face Recognition", isOn: $faceRecognition)
            }

            Section {
                Button(isSaving ? "Saving…" : "Save") { Task { await save() } }
                    .disabled(isSaving || questions.isEmpty || systemPrompt.trimmingCharacters(in: .whitespaces).isEmpty)
            } footer: {
                VStack(alignment: .leading, spacing: 6) {
                    Text("This profile's crop region and any other settings not shown here are kept exactly as they are.")
                    if let statusMessage {
                        Text(statusMessage).foregroundStyle(SentinelTheme.green)
                    }
                    if let errorMessage {
                        Text(errorMessage).foregroundStyle(SentinelTheme.red)
                    }
                }
            }
        }
        .formStyle(.grouped)
        .navigationTitle(displayName.isEmpty ? profileKey : displayName)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .onAppear(perform: seedOnce)
    }

    private func seedOnce() {
        guard !seeded else { return }
        seeded = true
        displayName = profile["displayName"]?.displayString ?? ""
        systemPrompt = profile["systemPrompt"]?.displayString ?? ""
        if case .array(let values)? = profile["questions"] {
            questions = values.map(\.displayString)
        }
        licensePlate = profile["licensePlateRecognitionEnabled"]?.boolValue ?? false
        faceRecognition = profile["faceRecognitionEnabled"]?.boolValue ?? false
    }

    // Only the five fields this screen actually shows. The parent merges these over the original,
    // so everything omitted here survives untouched.
    private func save() async {
        isSaving = true
        statusMessage = nil
        errorMessage = nil
        defer { isSaving = false }

        let edited: [String: SettingsValue] = [
            "displayName": .string(displayName),
            "systemPrompt": .string(systemPrompt),
            "questions": .array(questions.map { .string($0) }),
            "licensePlateRecognitionEnabled": .bool(licensePlate),
            "faceRecognitionEnabled": .bool(faceRecognition)
        ]

        if let failure = await onSave(edited) {
            errorMessage = failure
        } else {
            statusMessage = "Saved."
        }
    }
}
