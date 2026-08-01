import SwiftUI

// Every server settings section, rendered generically from GET /api/settings/schema - the same
// definitions the web dashboard renders, so a section added on the server appears here with no
// app update. This is deliberately a renderer, not a settings screen: it knows how to draw a
// "number" or "select" field, and nothing about what any particular section means.
//
// Reached only through the Admin-gated part of SettingsView - the server would 403 a Viewer on
// every endpoint used here anyway (the whole /api/settings group is AdminOnly).
struct ServerSettingsView: View {
    @EnvironmentObject private var model: AppModel
    @State private var sections: [SettingsSectionSchema] = []
    @State private var snapshot: [String: SettingsValue] = [:]
    @State private var loadError: String?

    var body: some View {
        Group {
            if let loadError {
                VStack(spacing: 12) {
                    Text(loadError)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                    Button("Retry") { Task { await load() } }
                }
                .padding()
            } else if sections.isEmpty {
                ProgressView("Loading settings…")
            } else {
                List(sections) { section in
                    NavigationLink {
                        ServerSettingsSectionView(
                            section: section,
                            initialData: sectionData(for: section)
                        )
                    } label: {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(section.title)
                                .font(.body.smallCaps())
                            Text(section.description)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                                .lineLimit(2)
                        }
                    }
                }
            }
        }
        .navigationTitle("Server Settings")
        .task { await load() }
        .refreshable { await load() }
    }

    private func sectionData(for section: SettingsSectionSchema) -> [String: SettingsValue] {
        if case .object(let data)? = snapshot[section.key] { return data }
        return [:]
    }

    private func load() async {
        loadError = nil
        do {
            async let schema = model.client.settingsSchema()
            async let data = model.client.settingsSnapshot()
            sections = try await schema
            snapshot = try await data
        } catch {
            loadError = error.localizedDescription
        }
    }
}

struct ServerSettingsSectionView: View {
    @EnvironmentObject private var model: AppModel

    let section: SettingsSectionSchema
    let initialData: [String: SettingsValue]

    // Field values keyed by schema field name (dotted paths stay flat here; nesting is
    // reassembled when building the save payload).
    @State private var text: [String: String] = [:]
    @State private var flags: [String: Bool] = [:]

    @State private var isSaving = false
    @State private var saveErrors: [String] = []
    @State private var statusMessage: String?
    @State private var confirmingRestart = false
    @State private var confirmingAction = false
    @State private var initialized = false

    var body: some View {
        Form {
            Section {
                ForEach(section.fields) { field in
                    fieldRow(field)
                }
            } header: {
                Text(section.description)
                    .font(.caption)
                    .textCase(nil)
            }

            Section {
                Button(isSaving ? "Saving…" : "Save") { Task { await save() } }
                    .disabled(isSaving)

                if let extraAction = section.extraAction {
                    Button(extraAction.label.capitalized) { confirmingAction = true }
                        .confirmationDialog(extraAction.tip, isPresented: $confirmingAction, titleVisibility: .visible) {
                            Button(extraAction.label.capitalized) { Task { await run(extraAction) } }
                            Button("Cancel", role: .cancel) {}
                        }
                }

                if section.needsRestart {
                    Button("Restart Bridge Service", role: .destructive) { confirmingRestart = true }
                        .confirmationDialog(
                            "Restart the main bridge service so saved restart-required settings take effect? Alert processing pauses for a few seconds; running as a service, it comes back on its own.",
                            isPresented: $confirmingRestart,
                            titleVisibility: .visible
                        ) {
                            Button("Restart", role: .destructive) { Task { await restart() } }
                            Button("Cancel", role: .cancel) {}
                        }
                }
            } footer: {
                VStack(alignment: .leading, spacing: 6) {
                    if section.needsRestart {
                        Text("Fields marked “restart required” only take effect after the bridge service restarts.")
                    }
                    if let statusMessage {
                        Text(statusMessage).foregroundStyle(SentinelTheme.green)
                    }
                    ForEach(saveErrors, id: \.self) { error in
                        Text(error).foregroundStyle(SentinelTheme.red)
                    }
                }
            }
        }
        .navigationTitle(Text(section.title).font(.body.smallCaps()))
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .onAppear(perform: seedOnce)
    }

    @ViewBuilder
    private func fieldRow(_ field: SettingsFieldSchema) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            switch field.type {
            case "checkbox":
                Toggle(field.label, isOn: flagBinding(field.name))

            case "select":
                Picker(field.label, selection: textBinding(field.name)) {
                    ForEach(field.options ?? [], id: \.value) { option in
                        Text(option.label).tag(option.value)
                    }
                }

            case "password":
                LabeledContent(field.label) {
                    SecureField("not set", text: textBinding(field.name))
                        .multilineTextAlignment(.trailing)
                        .autocorrectionDisabled()
                        #if os(iOS)
                        .textInputAutocapitalization(.never)
                        #endif
                }

            case "number":
                LabeledContent(field.label) {
                    TextField("0", text: textBinding(field.name))
                        .multilineTextAlignment(.trailing)
                        #if os(iOS)
                        .keyboardType(.numbersAndPunctuation)
                        #endif
                }

            case "text", "csv":
                VStack(alignment: .leading, spacing: 2) {
                    Text(field.label)
                    TextField(field.type == "csv" ? "comma, separated" : "", text: textBinding(field.name))
                        .autocorrectionDisabled()
                        #if os(iOS)
                        .textInputAutocapitalization(.never)
                        #endif
                }

            default:
                // A field type this app version doesn't know. Shown read-only rather than
                // guessed at - the value still round-trips untouched because saves only send
                // fields this renderer produced.
                LabeledContent(field.label, value: SettingsValue.read(initialData, path: field.name)?.displayString ?? "")
            }

            Text(field.requiresRestart ? "\(field.tip) (Restart required.)" : field.tip)
                .font(.caption2)
                .foregroundStyle(.secondary)
        }
    }

    private func textBinding(_ name: String) -> Binding<String> {
        Binding(get: { text[name] ?? "" }, set: { text[name] = $0 })
    }

    private func flagBinding(_ name: String) -> Binding<Bool> {
        Binding(get: { flags[name] ?? false }, set: { flags[name] = $0 })
    }

    // onAppear rather than init so @EnvironmentObject is available, and once so navigation
    // back-and-forward doesn't wipe in-progress edits.
    private func seedOnce() {
        guard !initialized else { return }
        initialized = true
        for field in section.fields {
            let value = SettingsValue.read(initialData, path: field.name)
            switch field.type {
            case "checkbox":
                flags[field.name] = value?.boolValue ?? false
            case "select":
                // A select must always hold a legal value or the Picker shows nothing selected.
                text[field.name] = value?.displayString ?? field.options?.first?.value ?? ""
            default:
                text[field.name] = value?.displayString ?? ""
            }
        }
    }

    private func save() async {
        isSaving = true
        saveErrors = []
        statusMessage = nil
        defer { isSaving = false }

        var payload: [String: SettingsValue] = [:]
        for field in section.fields {
            let value: SettingsValue
            switch field.type {
            case "checkbox":
                value = .bool(flags[field.name] ?? false)
            case "number":
                let raw = text[field.name] ?? ""
                guard let number = Double(raw.trimmingCharacters(in: .whitespaces)), raw.isEmpty == false else {
                    saveErrors.append("\(field.label) must be a number.")
                    continue
                }
                value = .number(number)
            case "csv":
                let items = (text[field.name] ?? "")
                    .split(separator: ",")
                    .map { $0.trimmingCharacters(in: .whitespaces) }
                    .filter { !$0.isEmpty }
                value = .array(items.map { .string($0) })
            default:
                value = .string(text[field.name] ?? "")
            }
            SettingsValue.write(&payload, path: field.name, value: value)
        }
        guard saveErrors.isEmpty else { return }

        do {
            let result = try await model.client.saveSettingsSection(endpoint: section.endpoint, values: payload)
            statusMessage = result.restartRequired == true
                ? "Saved. Takes effect after the bridge service restarts - use the restart button below."
                : "Saved."
        } catch {
            saveErrors = error.localizedDescription.components(separatedBy: "\n")
        }
    }

    private func run(_ action: SettingsActionSchema) async {
        saveErrors = []
        statusMessage = nil
        do {
            try await model.client.runSettingsAction(action)
            statusMessage = "Done."
        } catch {
            saveErrors = [error.localizedDescription]
        }
    }

    private func restart() async {
        saveErrors = []
        statusMessage = "Restarting - the server should be back in a few seconds…"
        do {
            try await model.client.restartServer(service: "bridge")
        } catch {
            statusMessage = nil
            // The refusal for an unsupervised process comes back as a plain server message and
            // is worth showing verbatim - it explains the systemd situation better than a
            // generic error would.
            saveErrors = [error.localizedDescription]
        }
    }
}
