import SwiftUI

// Add or edit one schedule. The form only ever shows the fields that belong to the timing and
// action currently chosen - a daily schedule has no cron box, a webhook has no device picker -
// because the alternative is a wall of inputs where most are irrelevant to what is being set up.
//
// The live preview at the bottom is doing real work: a cron expression or a darkness level is
// close to unverifiable by reading it, and seeing the next five firings is what makes either
// safe to commit to.
struct ScheduleEditView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss

    let schedule: Schedule?
    let metadata: ScheduleMetadata
    let onSaved: () -> Void

    @State private var name = ""
    @State private var enabled = true
    @State private var kind = "daily"

    @State private var timeOfDay = Date()
    @State private var daysOfWeek: Set<Int> = []

    @State private var intervalAmount = 15
    @State private var intervalUnit = "minutes"
    @State private var useWindow = false
    @State private var windowStart = Date()
    @State private var windowEnd = Date()

    @State private var cronExpression = "0 6 * * 1-5"
    @State private var runAt = Date().addingTimeInterval(3600)

    @State private var solarEvent = "dusk"
    @State private var solarElevation = -6.0
    @State private var solarOffsetMinutes = 0

    @State private var actionType = "device"
    @State private var deviceId: UUID?
    @State private var deviceAction = "on"
    @State private var useBrightness = false
    @State private var brightness = 100

    @State private var mqttTopic = ""
    @State private var mqttPayload = ""
    @State private var mqttRetain = false

    @State private var webhookUrl = ""
    @State private var webhookMethod = "POST"
    @State private var webhookContentType = "application/json"
    @State private var webhookHeaders = ""
    @State private var webhookBody = ""

    @State private var catchUpPolicy = "skip"

    @State private var preview: SchedulePreview?
    @State private var previewError: String?
    @State private var isSaving = false
    @State private var errors: [String] = []

    private var isNew: Bool { schedule == nil }

    private var usesElevation: Bool { solarEvent == "dusk" || solarEvent == "dawn" }

    private var selectedDevice: ScheduleDeviceOption? {
        metadata.devices.first { $0.id == deviceId }
    }

    var body: some View {
        Form {
            Section("Schedule") {
                SentinelTextField(label: "Name", prompt: "e.g. Close the coop at dusk", text: $name)
                Toggle("Enabled", isOn: $enabled)
            }

            whenSection
            if kind == "daily" || kind == "solar" { daysSection }

            actionSection
            catchUpSection
            previewSection

            if !errors.isEmpty {
                Section {
                    ForEach(errors, id: \.self) { message in
                        Text(message).foregroundStyle(SentinelTheme.red)
                    }
                }
            }
        }
        // Every other Form in the app is grouped. Left on the default columns style this one
        // sized its label column to the longest label, pushed the fields off the side of the
        // Mac sheet, and - since the columns style is not a scroll view - clipped whatever ran
        // past the bottom instead of scrolling to it.
        .formStyle(.grouped)
        .navigationTitle(isNew ? "New Schedule" : "Edit Schedule")
        .toolbar {
            ToolbarItem(placement: .cancellationAction) {
                Button("Cancel") { dismiss() }
            }
            ToolbarItem(placement: .confirmationAction) {
                Button("Save") { Task { await save() } }
                    .disabled(isSaving)
            }
        }
        .onAppear(perform: loadExisting)
        .task(id: previewKey) { await refreshPreview() }
    }

    // MARK: - When

    @ViewBuilder
    private var whenSection: some View {
        Section("When") {
            Picker("Timing", selection: $kind) {
                Text("At a time of day").tag("daily")
                Text("Sunset / sunrise / darkness").tag("solar")
                Text("Every so often").tag("interval")
                Text("Once, at a date and time").tag("once")
                Text("Cron expression").tag("cron")
            }

            switch kind {
            case "daily":
                DatePicker("Time", selection: $timeOfDay, displayedComponents: .hourAndMinute)

            case "interval":
                Stepper("Every \(intervalAmount) \(intervalUnit)", value: $intervalAmount, in: 1...240)
                Picker("Unit", selection: $intervalUnit) {
                    Text("minutes").tag("minutes")
                    Text("hours").tag("hours")
                }
                .pickerStyle(.segmented)
                Toggle("Only during part of the day", isOn: $useWindow)
                if useWindow {
                    DatePicker("From", selection: $windowStart, displayedComponents: .hourAndMinute)
                    DatePicker("Until", selection: $windowEnd, displayedComponents: .hourAndMinute)
                }

            case "cron":
                SentinelTextField(label: "Cron expression", prompt: "0 6 * * 1-5", text: $cronExpression)
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                    #endif
                Text("Five fields: minute hour day-of-month month day-of-week. \"0 6 * * 1-5\" is 6am on weekdays.")
                    .font(.caption)
                    .foregroundStyle(.secondary)

            case "once":
                DatePicker("Date and time", selection: $runAt)
                Text("The schedule turns itself off after it runs, keeping its history.")
                    .font(.caption)
                    .foregroundStyle(.secondary)

            case "solar":
                solarFields

            default:
                EmptyView()
            }
        }
    }

    @ViewBuilder
    private var solarFields: some View {
        Picker("Sun position", selection: $solarEvent) {
            Text("When it gets dark (evening)").tag("dusk")
            Text("When it gets light (morning)").tag("dawn")
            Text("Sunset").tag("sunset")
            Text("Sunrise").tag("sunrise")
        }

        if usesElevation {
            Picker("How dark", selection: $solarElevation) {
                ForEach(metadata.darknessLevels) { level in
                    Text("\(level.label) (\(Int(level.elevationDegrees))°)").tag(level.elevationDegrees)
                }
            }

            if let level = metadata.darknessLevels.first(where: { abs($0.elevationDegrees - solarElevation) < 0.01 }) {
                Text(level.description)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }

        Stepper("Offset: \(offsetDescription)", value: $solarOffsetMinutes, in: -180...180, step: 5)

        // The clock time this resolves to moves through the year on its own, which is the entire
        // reason to pick this over a daily time - worth saying, since it isn't obvious.
        Text("The clock time follows the seasons by itself — nothing to adjust twice a year.")
            .font(.caption)
            .foregroundStyle(.secondary)

        if !metadata.locationSet {
            Text("No location is set, so sunrise and sunset cannot be worked out. Add your town under Server Settings → LOCATION first.")
                .font(.caption)
                .foregroundStyle(SentinelTheme.amber)
        }
    }

    private var offsetDescription: String {
        if solarOffsetMinutes == 0 { return "exactly then" }
        if solarOffsetMinutes > 0 { return "\(solarOffsetMinutes) min after" }
        return "\(abs(solarOffsetMinutes)) min before"
    }

    private var daysSection: some View {
        Section {
            ForEach(0..<7, id: \.self) { day in
                Toggle(fullDayNames[day], isOn: Binding(
                    get: { daysOfWeek.contains(day) },
                    set: { isOn in
                        if isOn { daysOfWeek.insert(day) } else { daysOfWeek.remove(day) }
                    }
                ))
            }
        } header: {
            Text("Days")
        } footer: {
            Text("Leave every day switched off to run on all of them.")
        }
    }

    private var fullDayNames: [String] {
        ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"]
    }

    // MARK: - Action

    @ViewBuilder
    private var actionSection: some View {
        Section("Then") {
            Picker("Action", selection: $actionType) {
                Text("Switch a device").tag("device")
                Text("Publish MQTT").tag("mqtt")
                Text("Call an API endpoint").tag("webhook")
            }

            switch actionType {
            case "device":
                if metadata.devices.isEmpty {
                    Text("No devices in the Device Hub yet. Add one there first, or choose a different action.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                } else {
                    Picker("Device", selection: $deviceId) {
                        ForEach(metadata.devices) { device in
                            Text(device.room.isEmpty ? device.name : "\(device.name) (\(device.room))")
                                .tag(Optional(device.id))
                        }
                    }
                    Picker("Do what", selection: $deviceAction) {
                        ForEach(availableDeviceActions, id: \.self) { action in
                            Text(action).tag(action)
                        }
                    }
                    // Only offered for devices that can actually dim - on anything else it would
                    // produce nothing but a save error.
                    if selectedDevice?.supportsBrightness == true {
                        Toggle("Also set brightness", isOn: $useBrightness)
                        if useBrightness {
                            Stepper("Brightness: \(brightness)%", value: $brightness, in: 0...100, step: 5)
                        }
                    }
                }

            case "mqtt":
                SentinelTextField(label: "Topic", prompt: "home/garage/door/set", text: $mqttTopic)
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                    #endif
                SentinelTextField(label: "Payload", text: $mqttPayload, axis: .vertical)
                Toggle("Retain", isOn: $mqttRetain)

            default:
                SentinelTextField(label: "URL", prompt: "https://example.com/hook", text: $webhookUrl)
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                    #endif
                Picker("Method", selection: $webhookMethod) {
                    ForEach(["POST", "PUT", "PATCH", "GET", "DELETE"], id: \.self) { method in
                        Text(method).tag(method)
                    }
                }
                SentinelTextField(label: "Content-Type", prompt: "application/json", text: $webhookContentType)
                SentinelTextField(label: "Extra headers (one per line)", text: $webhookHeaders, axis: .vertical)
                SentinelTextField(label: "Body", text: $webhookBody, axis: .vertical)
            }
        }
    }

    // A cover-like device speaks open/close, a momentary one only presses - offering the whole
    // vocabulary for every device would mostly offer nonsense.
    private var availableDeviceActions: [String] {
        guard let device = selectedDevice else { return metadata.deviceActions }
        if device.momentary { return ["press"] }
        if device.coverLike { return device.hasStop ? ["open", "close", "stop"] : ["open", "close"] }
        if device.kind == "lock" { return ["lock", "unlock"] }
        return ["on", "off", "toggle"]
    }

    private var catchUpSection: some View {
        Section {
            // The question this asks belongs in the header, not in the picker's own label: a
            // macOS Form sizes its whole label column to the longest label in the form, and a
            // sentence there pushed every field on this screen off the right of the sheet.
            Picker("Missed runs", selection: $catchUpPolicy) {
                Text("Skip it").tag("skip")
                Text("Run it once on the way back up").tag("runOnce")
            }
        } header: {
            Text("If the server was off when it was due")
        } footer: {
            Text("Skipping suits anything where being late is pointless, like flashing a light. Running once suits anything where the state it leaves behind is the point, like shutting a door.")
        }
    }

    // MARK: - Preview

    // Everything the next firing times depend on, in one value, so the preview re-runs when any
    // of it changes and not when the name or the action does.
    private var previewKey: String {
        [
            kind, timeString(timeOfDay), daysOfWeek.sorted().map(String.init).joined(separator: ","),
            "\(intervalAmount)", intervalUnit, "\(useWindow)", timeString(windowStart), timeString(windowEnd),
            cronExpression, runAt.description, solarEvent, "\(solarElevation)", "\(solarOffsetMinutes)"
        ].joined(separator: "|")
    }

    @ViewBuilder
    private var previewSection: some View {
        Section("Next runs") {
            if let previewError {
                Text(previewError).font(.caption).foregroundStyle(.secondary)
            } else if let preview {
                Text(preview.explanation).font(.caption)
                if preview.occurrences.isEmpty {
                    Text("This never fires as configured.")
                        .font(.caption)
                        .foregroundStyle(SentinelTheme.amber)
                } else {
                    ForEach(preview.occurrences, id: \.utc) { occurrence in
                        Text(occurrence.local).font(.caption).foregroundStyle(.secondary)
                    }
                }
            } else {
                Text("Working out the next runs…").font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    private func refreshPreview() async {
        // The server rejects a nameless schedule, and the preview shares that validation - so it
        // gets a placeholder rather than reporting an error about a field this section is not
        // about.
        var input = buildInput()
        if input.name.isEmpty { input.name = "Preview" }

        do {
            preview = try await model.client.previewSchedule(input)
            previewError = nil
        } catch {
            preview = nil
            previewError = "Fill in the timing above to see the next runs."
        }
    }

    // MARK: - Loading and saving

    private func loadExisting() {
        guard let schedule else {
            deviceId = metadata.devices.first?.id
            return
        }

        name = schedule.name
        enabled = schedule.enabled
        kind = schedule.kind
        timeOfDay = parseTime(schedule.timeOfDay) ?? timeOfDay
        daysOfWeek = Set(schedule.daysOfWeek)

        if schedule.intervalSeconds % 3600 == 0 && schedule.intervalSeconds >= 3600 {
            intervalAmount = schedule.intervalSeconds / 3600
            intervalUnit = "hours"
        } else {
            intervalAmount = max(1, schedule.intervalSeconds / 60)
            intervalUnit = "minutes"
        }

        useWindow = !schedule.windowStart.isEmpty && !schedule.windowEnd.isEmpty
        windowStart = parseTime(schedule.windowStart) ?? windowStart
        windowEnd = parseTime(schedule.windowEnd) ?? windowEnd

        cronExpression = schedule.cronExpression.isEmpty ? cronExpression : schedule.cronExpression
        runAt = schedule.runAtUtc ?? runAt

        solarEvent = schedule.solarEvent
        solarElevation = schedule.solarElevationDegrees
        solarOffsetMinutes = schedule.solarOffsetMinutes

        actionType = schedule.action.type
        deviceId = schedule.action.deviceId ?? metadata.devices.first?.id
        deviceAction = schedule.action.deviceAction.isEmpty ? "on" : schedule.action.deviceAction
        useBrightness = schedule.action.deviceBrightness != nil
        brightness = schedule.action.deviceBrightness ?? 100

        mqttTopic = schedule.action.mqttTopic
        mqttPayload = schedule.action.mqttPayload
        mqttRetain = schedule.action.mqttRetain

        webhookUrl = schedule.action.webhookUrl
        webhookMethod = schedule.action.webhookMethod
        webhookContentType = schedule.action.webhookContentType
        webhookHeaders = schedule.action.webhookHeaders
        webhookBody = schedule.action.webhookBody

        catchUpPolicy = schedule.catchUpPolicy
    }

    private func buildInput() -> ScheduleInput {
        ScheduleInput(
            name: name.trimmingCharacters(in: .whitespacesAndNewlines),
            enabled: enabled,
            kind: kind,
            timeOfDay: timeString(timeOfDay),
            // Cron carries its own day-of-week field and a one-off is a single instant, so
            // sending days for either would be ignored at best and misleading at worst.
            daysOfWeek: kind == "daily" || kind == "solar" ? daysOfWeek.sorted() : [],
            intervalSeconds: intervalAmount * (intervalUnit == "hours" ? 3600 : 60),
            windowStart: useWindow ? timeString(windowStart) : "",
            windowEnd: useWindow ? timeString(windowEnd) : "",
            cronExpression: cronExpression.trimmingCharacters(in: .whitespaces),
            runAtUtc: kind == "once" ? isoString(runAt) : nil,
            solarEvent: solarEvent,
            solarOffsetMinutes: solarOffsetMinutes,
            solarElevationDegrees: solarElevation,
            actionType: actionType,
            deviceId: actionType == "device" ? deviceId : nil,
            deviceAction: actionType == "device" ? deviceAction : "",
            deviceBrightness: actionType == "device" && useBrightness ? brightness : nil,
            mqttTopic: mqttTopic.trimmingCharacters(in: .whitespaces),
            mqttPayload: mqttPayload,
            mqttRetain: mqttRetain,
            webhookUrl: webhookUrl.trimmingCharacters(in: .whitespaces),
            webhookMethod: webhookMethod,
            webhookHeaders: webhookHeaders,
            webhookBody: webhookBody,
            webhookContentType: webhookContentType.trimmingCharacters(in: .whitespaces),
            catchUpPolicy: catchUpPolicy
        )
    }

    private func save() async {
        isSaving = true
        errors = []
        defer { isSaving = false }

        do {
            if let schedule {
                _ = try await model.client.updateSchedule(id: schedule.id, buildInput())
            } else {
                _ = try await model.client.createSchedule(buildInput())
            }
            onSaved()
            SaveFeedback.hapticSuccess()
            dismiss()
        } catch {
            errors = [error.localizedDescription]
        }
    }

    // MARK: - Time helpers

    private func timeString(_ date: Date) -> String {
        let components = Calendar.current.dateComponents([.hour, .minute], from: date)
        return String(format: "%02d:%02d", components.hour ?? 0, components.minute ?? 0)
    }

    private func parseTime(_ value: String) -> Date? {
        let parts = value.split(separator: ":")
        guard parts.count == 2, let hour = Int(parts[0]), let minute = Int(parts[1]) else { return nil }
        return Calendar.current.date(bySettingHour: hour, minute: minute, second: 0, of: Date())
    }

    private func isoString(_ date: Date) -> String {
        date.ISO8601Format()
    }
}
