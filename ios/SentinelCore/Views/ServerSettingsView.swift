import SwiftUI

// Every server settings section, rendered generically from GET /api/settings/schema - the same
// definitions the web dashboard renders, so a section added on the server appears here with no
// app update. This is deliberately a renderer, not a settings screen: it knows how to draw a
// "number" or "select" field, and nothing about what any particular section means.
//
// Reached only through the Admin-gated part of SettingsView - the server would 403 a Viewer on
// every endpoint used here anyway (the whole /api/settings group is AdminOnly).
// One heading and the sections filed under it. Built from the group/groupOrder the server puts on
// each section, so this app and the web dashboard divide the same 36 sections the same way.
private struct SettingsSectionGroup: Identifiable {
    let name: String
    let order: Int
    let sections: [SettingsSectionSchema]

    var id: String { name }
}

struct ServerSettingsView: View {
    @EnvironmentObject private var model: AppModel
    @State private var sections: [SettingsSectionSchema] = []
    @State private var snapshot: [String: SettingsValue] = [:]
    @State private var externalData: [String: [String: SettingsValue]] = [:]
    @State private var loadError: String?
    @State private var searchText = ""

    // Matches the web dashboard's advanced toggle, including it being remembered per device: the
    // full list is 36 sections, and most of them are tuning nobody touches twice. Searching
    // overrides it, so a setting is never unreachable because of where this switch happens to be.
    @AppStorage("settings.advanced") private var showAdvanced = false

    private var visibleGroups: [SettingsSectionGroup] {
        let query = searchText.trimmingCharacters(in: .whitespaces).lowercased()

        let matching = sections.filter { section in
            guard query.isEmpty else { return matches(section, query: query) }
            return showAdvanced || section.basic
        }

        // Sections keep their server order within a group - the descriptions lean on it ("see
        // VISION ROUTING below"), so it isn't this app's to re-sort.
        let grouped = Dictionary(grouping: matching) { $0.group ?? "Other" }
        return grouped
            .map { name, sections in
                SettingsSectionGroup(
                    name: name,
                    order: sections.compactMap(\.groupOrder).min() ?? Int.max,
                    sections: sections)
            }
            .sorted { ($0.order, $0.name) < ($1.order, $1.name) }
    }

    // Field tips are searched as well as labels, because they're where a setting is described in
    // the words someone would actually type - nobody searches for "nuisanceAdvisor".
    private func matches(_ section: SettingsSectionSchema, query: String) -> Bool {
        if section.title.lowercased().contains(query) { return true }
        if section.description.lowercased().contains(query) { return true }
        return section.fields.contains {
            $0.label.lowercased().contains(query) || $0.tip.lowercased().contains(query)
        }
    }

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
                List {
                    Section {
                        Toggle("Show advanced settings", isOn: $showAdvanced)
                    } footer: {
                        Text("Basic shows the settings most people actually change. Advanced adds every tuning option. Searching looks through both.")
                    }

                    ForEach(visibleGroups) { group in
                        Section(group.name) {
                            ForEach(group.sections) { section in
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

                    if visibleGroups.isEmpty {
                        Text("No settings match “\(searchText)”.")
                            .foregroundStyle(.secondary)
                    }
                }
                .searchable(text: $searchText, prompt: "Search settings")
            }
        }
        .navigationTitle("Server Settings")
        .task { await load() }
        .refreshable { await load() }
    }

    private func sectionData(for section: SettingsSectionSchema) -> [String: SettingsValue] {
        if section.dataEndpoint != nil { return externalData[section.key] ?? [:] }
        if case .object(let data)? = snapshot[section.key] { return data }
        return [:]
    }

    private func load() async {
        loadError = nil
        do {
            async let schema = model.client.settingsSchema()
            async let data = model.client.settingsSnapshot()
            let loaded = try await schema
            sections = loaded
            snapshot = try await data

            // Sections that keep their values outside the bulk snapshot. A failure here leaves the
            // section rendering empty rather than failing the whole screen - the same outcome a
            // missing snapshot key already has, and better than losing the other 35 sections
            // because one extra request didn't come back.
            var fetched: [String: [String: SettingsValue]] = [:]
            for section in loaded {
                guard let endpoint = section.dataEndpoint else { continue }
                fetched[section.key] = try? await model.client.settingsSectionData(endpoint: endpoint)
            }
            externalData = fetched
        } catch {
            loadError = error.localizedDescription
        }
    }
}

// What a lookup has to say for itself between presses - "nothing found", "filled in from X", or
// whatever the server explained went wrong. Kept apart from saveErrors so a failed lookup doesn't
// read as a failed save.
private struct LookupNote {
    let text: String
    let isError: Bool

    init(_ text: String, isError: Bool) {
        self.text = text
        self.isError = isError
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

    // Lookup state, keyed by the field the button belongs to rather than held as three loose
    // properties - the schema can grow a second lookup field without this view changing.
    @State private var lookupResults: [String: [SettingsLookupCandidate]] = [:]
    @State private var lookupNotes: [String: LookupNote] = [:]
    @State private var lookupBusy: String?

    // The values fetched for the fields that carry an optionsSource (OLLAMA's Model, from what's
    // pulled on the box). An entry missing means "not fetched, or the fetch failed" and the field
    // falls back to its plain text box - which is exactly what it was before this existed.
    @State private var fieldOptions: [String: [SettingsFieldOption]] = [:]
    @State private var optionsNotes: [String: LookupNote] = [:]

    // Fields where the picker has been swapped for the keyboard on purpose, because the wanted
    // value isn't installed yet. Never sticky beyond this screen.
    @State private var typingManually: Set<String> = []

    // Which field the keyboard is in. Drives the highlight on that field's box, and lets a tap
    // anywhere in the box - not just on the line of text - start typing.
    @FocusState private var focusedField: String?

    @State private var isSaving = false
    @State private var saveErrors: [String] = []
    @State private var statusMessage: String?
    @StateObject private var feedback = SaveFeedback()
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
                SaveButton(feedback: feedback, isSaving: isSaving) { Task { await save() } }

                if let extraAction = section.extraAction {
                    Button(extraAction.label.capitalized) { confirmingAction = true }
                        .confirmationDialog(extraAction.tip, isPresented: $confirmingAction, titleVisibility: .visible) {
                            Button(extraAction.label.capitalized) { Task { await run(extraAction) } }
                            Button("Cancel", role: .cancel) {}
                        }
                }

                if section.needsRestart {
                    Button(restartButtonLabel, role: .destructive) { confirmingRestart = true }
                        .confirmationDialog(
                            restartPrompt,
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
                        Text("Fields marked “restart required” only take effect after the \(section.restartTarget) service restarts.")
                    }
                    if let statusMessage {
                        Text(statusMessage).foregroundStyle(SentinelTheme.green)
                    }
                    ForEach(saveErrors, id: \.self) { error in
                        Text(error).foregroundStyle(SentinelTheme.red)
                    }
                }
            }

            // The one section that isn't only its fields. Everything else here saves a value and
            // is done; this one also has to report what the server's operating system will
            // actually permit, which no schema can describe. See HostRebootPanel.
            if section.key == "hostReboot" {
                HostRebootPanel()
            }
        }
        .formStyle(.grouped)
        .saveFeedback(feedback)
        .navigationTitle(Text(section.title).font(.body.smallCaps()))
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .onAppear(perform: seedOnce)
        // Only the section being looked at asks, and only when it is opened - this app reaches a
        // section by drilling into it, so there is nothing further to defer.
        .task { await loadFieldOptions() }
    }

    // Restarting the dashboard is a different promise from restarting the bridge - this app is
    // talking to the dashboard, so that one drops the connection it's using and says so, rather
    // than pausing alert processing in the background.
    private var restartButtonLabel: String {
        section.restartTarget == "dashboard" ? "Restart Dashboard" : "Restart Bridge Service"
    }

    private var restartPrompt: String {
        section.restartTarget == "dashboard"
            ? "Restart the dashboard service so saved restart-required settings take effect? This app talks to the dashboard, so it will lose its connection for a few seconds and reconnect on its own."
            : "Restart the main bridge service so saved restart-required settings take effect? Alert processing pauses for a few seconds; running as a service, it comes back on its own."
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
                // Label above the box rather than beside it, like every other typed field here:
                // a value squeezed into what is left of the row reads as a piece of text the row
                // is reporting, not as somewhere to type. See wrappingLabel for why the label
                // needs its intrinsic height.
                VStack(alignment: .leading, spacing: 4) {
                    wrappingLabel(field.label)
                    // prompt:, not the title argument: macOS draws a TextField's title as a
                    // label beside the box and only prompt: as placeholder text inside it, so
                    // the title form would put the hint in the wrong place there.
                    SecureField("", text: textBinding(field.name), prompt: Text("Not set — \(SentinelInput.typeVerbLower)"))
                        .labelsHidden()
                        .autocorrectionDisabled()
                        #if os(iOS)
                        .textInputAutocapitalization(.never)
                        #endif
                        .focused($focusedField, equals: field.name)
                        .sentinelInputBox(focused: focusedField == field.name) {
                            focusedField = field.name
                        }
                }

            case "number":
                numberField(field)

            case "text", "csv":
                VStack(alignment: .leading, spacing: 4) {
                    wrappingLabel(field.label)
                    // A field whose values the server can list is a picker instead of a keyboard,
                    // for as long as there is a list and the user hasn't asked to type - see
                    // optionsPicker.
                    if let source = field.optionsSource,
                       let options = fieldOptions[field.name],
                       !typingManually.contains(field.name) {
                        optionsPicker(field, source, options)
                    } else {
                        // See the password field above for why the hint goes in prompt:.
                        TextField("", text: textBinding(field.name), prompt: Text(placeholder(for: field)))
                            .labelsHidden()
                            .autocorrectionDisabled()
                            #if os(iOS)
                            .textInputAutocapitalization(.never)
                            #endif
                            .focused($focusedField, equals: field.name)
                            .onSubmit {
                                // Return runs the lookup, which is what a box with a Find button
                                // beside it invites. Harmless on the fields without one.
                                if let lookup = field.lookup {
                                    Task { await runLookup(field, lookup) }
                                }
                            }
                            .sentinelInputBox(focused: focusedField == field.name) {
                                focusedField = field.name
                            }

                        // Why there's a box here rather than the promised list, or the way back to
                        // it once there is one.
                        if let note = optionsNotes[field.name] {
                            Text(note.text)
                                .font(.caption2)
                                .foregroundStyle(note.isError ? SentinelTheme.red : .secondary)
                        }
                        if let source = field.optionsSource, fieldOptions[field.name] != nil {
                            Button("Back to \(source.label)") { typingManually.remove(field.name) }
                                .font(.caption)
                        }
                    }
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

            // Below the tip, not above it: the tip describes the box, and a candidate list
            // sitting between the two reads as though the explanation belongs to the results.
            if let lookup = field.lookup {
                lookupControls(field, lookup)
            }
        }
    }

    // The button, whatever the last attempt had to say, and the candidates to choose from. Laid
    // out down the row rather than in a sheet: a place name is long, there are at most five of
    // them, and picking one is a single tap either way.
    @ViewBuilder
    private func lookupControls(_ field: SettingsFieldSchema, _ lookup: SettingsLookupSchema) -> some View {
        let busy = lookupBusy == field.name

        HStack {
            Button(busy ? "Looking…" : lookup.label.capitalized) {
                Task { await runLookup(field, lookup) }
            }
            .buttonStyle(.borderless)
            .disabled(busy)
            Spacer(minLength: 0)
        }
        .padding(.top, 4)

        if let note = lookupNotes[field.name] {
            Text(note.text)
                .font(.caption2)
                .foregroundStyle(note.isError ? SentinelTheme.red : SentinelTheme.green)
                .fixedSize(horizontal: false, vertical: true)
        }

        // Indexed because two candidates can share a display name (there is more than one
        // Springfield, and Nominatim will happily return both).
        ForEach(Array((lookupResults[field.name] ?? []).enumerated()), id: \.offset) { _, candidate in
            Button {
                apply(candidate, from: field)
            } label: {
                Text(candidate.label)
                    .font(.caption)
                    .multilineTextAlignment(.leading)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .buttonStyle(.borderless)
        }
    }

    private func runLookup(_ field: SettingsFieldSchema, _ lookup: SettingsLookupSchema) async {
        let query = (text[field.name] ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty else {
            lookupResults[field.name] = []
            lookupNotes[field.name] = LookupNote("Type a town or city first.", isError: true)
            return
        }

        lookupBusy = field.name
        lookupNotes[field.name] = nil
        defer { lookupBusy = nil }

        do {
            let candidates = try await model.client.settingsLookup(lookup, query: query)
            lookupResults[field.name] = candidates
            if candidates.isEmpty {
                lookupNotes[field.name] = LookupNote(
                    "Nothing found for “\(query)”. Try adding a state or country, or enter the coordinates yourself.",
                    isError: true)
            }
        } catch {
            lookupResults[field.name] = []
            // The server's message separates "this machine has no route to the internet" from
            // "the lookup service said no", and each ends with what to do instead - better than
            // anything this app could say without knowing which happened.
            lookupNotes[field.name] = LookupNote(error.localizedDescription, isError: true)
        }
    }

    // Writes into the section's *other* fields, which is the whole point: the values wanted are
    // the ones nobody can type from memory. Names this section doesn't have are skipped, so a
    // server that starts returning a third value can't upset an app that knows two.
    private func apply(_ candidate: SettingsLookupCandidate, from field: SettingsFieldSchema) {
        for (name, value) in candidate.values {
            guard let target = section.fields.first(where: { $0.name == name }) else { continue }
            if target.type == "checkbox" {
                flags[name] = value.boolValue
            } else {
                text[name] = value.displayString
            }
        }
        lookupResults[field.name] = []
        lookupNotes[field.name] = LookupNote("Filled in from \(candidate.label). Save to keep it.", isError: false)
    }

    // LabeledContent gives its label whatever width is left over and then lays the row out at the
    // height of a single line. A label that wraps - "Full AI Minimum Confidence" is the one that
    // showed it - draws its second line straight over the tip below instead of pushing it down.
    // fixedSize makes the label's own intrinsic height authoritative, so the row grows to fit it.
    private func wrappingLabel(_ label: String) -> some View {
        Text(label).fixedSize(horizontal: false, vertical: true)
    }

    // Every typed field says so, because a box with nothing in it still leaves "am I meant to
    // type here, or is this waiting on something?" open. Worded for the gesture the platform
    // actually has, and for a number it says a number, so "Latitude" doesn't read as a box to
    // paste a place name into.
    private func placeholder(for field: SettingsFieldSchema) -> String {
        switch field.type {
        case "csv": return "\(SentinelInput.typeVerb) — comma, separated"
        case "number": return "\(SentinelInput.typeVerb) a number"
        default: return SentinelInput.typeVerb
        }
    }

    // A field's value picked from what the server says is installed, with two things that keep it
    // honest: whatever is saved now is always in the list even if the server didn't return it (so
    // opening the picker can't quietly change a working setting), and the last entry always goes
    // back to the keyboard (so a model you are about to pull can still be named).
    @ViewBuilder
    private func optionsPicker(
        _ field: SettingsFieldSchema,
        _ source: SettingsOptionsSource,
        _ options: [SettingsFieldOption]
    ) -> some View {
        let current = text[field.name] ?? ""
        let entries = options.contains(where: { $0.value == current }) || current.isEmpty
            ? options
            : [SettingsFieldOption(value: current, label: "\(current) (not listed)")] + options

        Picker(source.label, selection: pickerBinding(field.name, source.typeItMessage)) {
            ForEach(entries, id: \.value) { option in
                Text(option.label).tag(option.value)
            }
            Text("Type a name instead…").tag(Self.typeItYourself)
        }
        .labelsHidden()
    }

    // The last entry's tag. A NUL can't collide with a model name, so no list the server returns
    // can shadow it.
    private static let typeItYourself = "\u{0}type-it"

    // Selecting "type a name instead" must not write itself into the setting - it swaps the
    // control, and leaves the value alone for the keyboard to edit.
    private func pickerBinding(_ name: String, _ typeItMessage: String) -> Binding<String> {
        Binding(
            get: { text[name] ?? "" },
            set: { selected in
                if selected == Self.typeItYourself {
                    typingManually.insert(name)
                    optionsNotes[name] = LookupNote(typeItMessage, isError: false)
                } else {
                    text[name] = selected
                }
            }
        )
    }

    // One request per field that asks for one, when the section opens. A failure is not an error
    // worth a banner: the field simply stays the text box it has always been, with the server's
    // own explanation under it.
    private func loadFieldOptions() async {
        for field in section.fields {
            guard let source = field.optionsSource, fieldOptions[field.name] == nil else { continue }
            do {
                let options = try await model.client.settingsOptions(source)
                if options.isEmpty {
                    optionsNotes[field.name] = LookupNote(source.emptyMessage, isError: false)
                } else {
                    fieldOptions[field.name] = options
                    optionsNotes[field.name] = nil
                }
            } catch {
                optionsNotes[field.name] = LookupNote(error.localizedDescription, isError: true)
            }
        }
    }

    // A slider where dragging one really is better than typing, and a keyboard everywhere else.
    // The line between them is how many stops the range has: a 0-1 confidence in twentieths is
    // twenty positions and every one of them is reachable by thumb, while a port (1-65535) or an
    // image width (256-4096) is a number you already know and just want to enter. 100 is the
    // cutoff - above it a slider stops being able to land on the value you meant.
    private func usesSlider(_ field: SettingsFieldSchema) -> Bool {
        guard let min = field.min, let max = field.max, max > min else { return false }
        let step = field.step ?? 1
        guard step > 0 else { return false }
        return (max - min) / step <= 100
    }

    @ViewBuilder
    private func numberField(_ field: SettingsFieldSchema) -> some View {
        if usesSlider(field), let min = field.min, let max = field.max {
            let step = field.step ?? 1
            VStack(alignment: .leading, spacing: 6) {
                HStack(alignment: .firstTextBaseline, spacing: 12) {
                    wrappingLabel(field.label)
                    Spacer(minLength: 0)
                    Text(formatted(numberValue(field), step: step))
                        .monospacedDigit()
                        .foregroundStyle(SentinelTheme.green)
                }
                Slider(value: numberBinding(field, min: min, max: max), in: min...max, step: step)
                recommendation(field, step: step)
            }
        } else {
            VStack(alignment: .leading, spacing: 4) {
                wrappingLabel(field.label)
                TextField("", text: textBinding(field.name), prompt: Text(placeholder(for: field)))
                    .labelsHidden()
                    #if os(iOS)
                    .keyboardType(.numbersAndPunctuation)
                    #endif
                    .focused($focusedField, equals: field.name)
                    .sentinelInputBox(focused: focusedField == field.name) {
                        focusedField = field.name
                    }
                recommendation(field, step: field.step ?? 1)
            }
        }
    }

    // "What do I even put here?" is the actual question a confidence threshold provokes, and the
    // tip underneath explains what the number does without ever answering it. This answers it,
    // and offers to apply it - hidden once the value is already there, so it stops being noise
    // for someone who has made their choice.
    @ViewBuilder
    private func recommendation(_ field: SettingsFieldSchema, step: Double) -> some View {
        if let recommended = field.recommended {
            HStack(spacing: 8) {
                Text("Recommended: \(formatted(recommended, step: step))")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                if abs(numberValue(field) - recommended) > step / 2 {
                    Button("Use") { text[field.name] = formatted(recommended, step: step) }
                        .font(.caption2)
                        .buttonStyle(.borderless)
                }
                Spacer(minLength: 0)
            }
        }
    }

    // The stored value is a string, because that is what every other field type here is and what
    // save() parses back. Falling back to the recommendation rather than zero matters on a field
    // that has never been set: a slider parked at the bottom of its range reads as a decision.
    private func numberValue(_ field: SettingsFieldSchema) -> Double {
        let raw = (text[field.name] ?? "").trimmingCharacters(in: .whitespaces)
        let value = Double(raw) ?? field.recommended ?? field.min ?? 0
        guard let min = field.min, let max = field.max else { return value }
        return Swift.min(Swift.max(value, min), max)
    }

    private func numberBinding(_ field: SettingsFieldSchema, min: Double, max: Double) -> Binding<Double> {
        Binding(
            get: { numberValue(field) },
            set: { text[field.name] = formatted(Swift.min(Swift.max($0, min), max), step: field.step ?? 1) }
        )
    }

    // Decimal places from the step, so a field that moves in twentieths shows 0.65 and one that
    // moves in whole numbers doesn't show 7.00.
    private func formatted(_ value: Double, step: Double) -> String {
        let places = step >= 1 ? 0 : (step >= 0.1 ? 1 : 2)
        return String(format: "%.\(places)f", value)
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

    // What is wrong with a number the user typed, or nil if nothing is. Only the fields that fall
    // back to a keyboard can be wrong at all - a slider cannot leave its own range - but that is
    // most of them: every port, size, count and timeout has more than 100 stops. See usesSlider.
    //
    // The server checks all of this again on arrival ([Range] on the DTOs); doing it here as well
    // is what turns "400: The field Port must be between 1 and 65535" arriving after a round trip
    // into a line under the box that says which box, before anything is sent.
    private func boundsProblem(_ field: SettingsFieldSchema, _ number: Double) -> String? {
        if field.integer == true, number.rounded() != number {
            return "\(field.label) must be a whole number."
        }
        if let min = field.min, let max = field.max, number < min || number > max {
            return "\(field.label) must be between \(formatted(min, step: field.step ?? 1)) and \(formatted(max, step: field.step ?? 1))."
        }
        return nil
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
                if let problem = boundsProblem(field, number) {
                    saveErrors.append(problem)
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
            // The banner and the button say it saved; the footer only keeps what's still worth
            // reading once they've gone - that a restart stands between the save and the effect.
            if result.restartRequired == true {
                statusMessage = "Saved. Takes effect after the \(section.restartTarget) service restarts - use the restart button below."
                feedback.saved("Saved - restart the \(section.restartTarget) to apply")
            } else {
                feedback.saved()
            }
        } catch {
            saveErrors = error.localizedDescription.components(separatedBy: "\n")
            feedback.failed("Not saved")
        }
    }

    private func run(_ action: SettingsActionSchema) async {
        saveErrors = []
        statusMessage = nil
        do {
            // The server's own sentence when it has one - "Test email sent to you@example.com."
            // is the answer someone pressed the button for, and "Done." throws it away.
            let outcome = try await model.client.runSettingsAction(action) ?? "Done."
            statusMessage = outcome
            feedback.note(outcome)
        } catch {
            saveErrors = [error.localizedDescription]
            feedback.failed(action.label.capitalized + " failed")
        }
    }

    private func restart() async {
        saveErrors = []
        statusMessage = "Restarting - the server should be back in a few seconds…"
        do {
            // Whichever service this section's fields actually belong to - the bridge for all but
            // INTERNET EXPOSED MODE, which binds the dashboard's own port.
            try await model.client.restartServer(service: section.restartTarget)
        } catch {
            statusMessage = nil
            // The refusal for an unsupervised process comes back as a plain server message and
            // is worth showing verbatim - it explains the systemd situation better than a
            // generic error would.
            saveErrors = [error.localizedDescription]
        }
    }
}
