import OrganizerKit
import SwiftUI
#if os(iOS)
import WidgetKit
#endif

extension Notification.Name {
    // Posted when an alarm push is tapped; RootView switches to the Alarm section.
    static let sentinelAlarmOpened = Notification.Name("sentinelAlarmOpened")
}

// The home alarm: arm and disarm the house, see every sensor, and (for admins) set it up. The
// state machine and the sensor subscription live on the server (see AlarmEngine.cs); this
// screen polls /api/alarm/status while it is on screen and counts the exit and entry delays
// down from the deadlines the server hands it, so a phone that was asleep shows the truth the
// moment it wakes.
struct AlarmView: View {
    @EnvironmentObject private var model: AppModel

    let isActive: Bool

    @State private var status: AlarmStatus?
    @State private var errorMessage: String?
    @State private var isBusy = false
    @State private var clockOffset: TimeInterval = 0
    @State private var pinPrompt: PinPrompt?
    @State private var pinText = ""
    @State private var openConfirm: OpenConfirm?
    @State private var confirmPanic = false
    @State private var showingAddSensor = false
    @State private var editingSensor: AlarmSensor?
    @State private var sensorToDelete: AlarmSensor?
    @State private var showingSettings = false
    @State private var showingEvents = false
    @State private var hubDevices: [HubDevice] = []

    private static let pollInterval = Duration.seconds(2)

    private struct PinPrompt: Identifiable {
        let id = UUID()
        let title: String
        let action: (String) -> Void
    }

    private struct OpenConfirm: Identifiable {
        let id = UUID()
        let mode: String
        let message: String
    }

    var body: some View {
        Group {
            if let status, status.enabled {
                content(status)
            } else if status == nil {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ContentUnavailableView("Alarm is off", systemImage: "lock.slash",
                                       description: Text("The alarm is turned off in the server's Settings."))
            }
        }
        .navigationTitle("Alarm")
        .toolbar {
            if model.isAdmin, let status, status.enabled {
                ToolbarItem(placement: .primaryAction) {
                    Menu {
                        Button { showingAddSensor = true } label: { Label("Add Sensor", systemImage: "plus") }
                        Button { showingSettings = true } label: { Label("Alarm Settings", systemImage: "slider.horizontal.3") }
                        Button { Task { await runTest() } } label: { Label("Test the Alarm", systemImage: "waveform.path.ecg") }
                            .disabled(status.isTriggered)
                        Button { showingEvents = true } label: { Label("Activity", systemImage: "list.bullet.rectangle") }
                    } label: {
                        Label("More", systemImage: "ellipsis.circle")
                    }
                }
            } else {
                ToolbarItem(placement: .primaryAction) {
                    Button { showingEvents = true } label: { Label("Activity", systemImage: "list.bullet.rectangle") }
                }
            }
        }
        .refreshable { await refresh() }
        .task(id: isActive) {
            guard isActive else { return }
            await refresh()
            await loadDevices()
            while !Task.isCancelled {
                try? await Task.sleep(for: Self.pollInterval)
                if Task.isCancelled { return }
                await refresh()
            }
        }
        .sheet(isPresented: $showingAddSensor) {
            NavigationStack {
                AlarmSensorEditView(sensor: nil, devices: hubDevices, taken: takenDeviceIds(excluding: nil)) { Task { await refresh() } }
            }
            .deviceHubSheetFrame()
        }
        .sheet(item: $editingSensor) { sensor in
            NavigationStack {
                AlarmSensorEditView(sensor: sensor, devices: hubDevices, taken: takenDeviceIds(excluding: sensor.id)) { Task { await refresh() } }
            }
            .deviceHubSheetFrame()
        }
        .sheet(isPresented: $showingSettings) {
            if let status, let config = status.config {
                NavigationStack {
                    AlarmSettingsView(config: config, pinSet: status.pinSet ?? false, devices: hubDevices) { Task { await refresh() } }
                }
                .deviceHubSheetFrame()
            }
        }
        .sheet(isPresented: $showingEvents) {
            NavigationStack { AlarmEventsView() }
                .deviceHubSheetFrame()
        }
        .alert(pinPrompt?.title ?? "PIN", isPresented: Binding(get: { pinPrompt != nil }, set: { if !$0 { pinPrompt = nil } }), presenting: pinPrompt) { prompt in
            TextField("PIN", text: $pinText)
                #if os(iOS)
                .keyboardType(.numberPad)
                #endif
            Button("OK") {
                let pin = pinText
                pinText = ""
                prompt.action(pin)
            }
            Button("Cancel", role: .cancel) { pinText = "" }
        } message: { _ in
            Text(errorMessage ?? "")
        }
        .alert("Something is open", isPresented: Binding(get: { openConfirm != nil }, set: { if !$0 { openConfirm = nil } }), presenting: openConfirm) { confirm in
            Button("Arm Anyway") { Task { await arm(confirm.mode, force: true, pin: nil) } }
            Button("Cancel", role: .cancel) {}
        } message: { confirm in
            Text(confirm.message)
        }
        .confirmationDialog("Set the alarm off?", isPresented: $confirmPanic, titleVisibility: .visible) {
            Button("Set It Off", role: .destructive) { Task { await panic() } }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("The siren, everyone's phones and the cameras - right now.")
        }
        .confirmationDialog(
            sensorToDelete.map { "Remove \($0.name)?" } ?? "Remove sensor?",
            isPresented: Binding(get: { sensorToDelete != nil }, set: { if !$0 { sensorToDelete = nil } }),
            presenting: sensorToDelete
        ) { sensor in
            Button("Remove", role: .destructive) {
                sensorToDelete = nil
                Task { await delete(sensor) }
            }
            Button("Cancel", role: .cancel) { sensorToDelete = nil }
        } message: { sensor in
            Text("\(sensor.name) will no longer be part of the alarm. Its Device Hub device is not touched.")
        }
    }

    // MARK: - Content

    @ViewBuilder private func content(_ status: AlarmStatus) -> some View {
        List {
            Section {
                statusCard(status)
                    .listRowInsets(EdgeInsets(top: 12, leading: 16, bottom: 12, trailing: 16))
            }

            if let errorMessage, pinPrompt == nil {
                Section {
                    Text(errorMessage).foregroundStyle(SentinelTheme.red).font(.callout)
                }
            }

            let sensors = status.sensors ?? []
            if sensors.isEmpty {
                Section {
                    if status.hubEnabled == true {
                        Text(model.isAdmin
                             ? "No sensors yet. Add a Device Hub sensor - a door contact, a motion sensor, a leak sensor - and say what it guards."
                             : "No sensors yet. An admin account can add them.")
                        .foregroundStyle(.secondary)
                    } else {
                        Text("The alarm listens to Device Hub sensors, and the Device Hub is off. Turn on MQTT and the Device Hub in the server's Settings, add your sensors there, then come back here.")
                            .foregroundStyle(.secondary)
                    }
                }
            } else {
                Section("Sensors") {
                    ForEach(sensors) { sensor in
                        AlarmSensorRow(sensor: sensor, alarmStatus: status.status ?? "disarmed", mode: status.mode ?? "disarmed", now: serverNow)
                            .contentShape(Rectangle())
                            .onTapGesture { if model.isAdmin { editingSensor = sensor } }
                            .swipeActions(edge: .trailing, allowsFullSwipe: false) {
                                if model.isAdmin {
                                    Button(role: .destructive) { sensorToDelete = sensor } label: { Label("Remove", systemImage: "trash") }
                                    Button { editingSensor = sensor } label: { Label("Edit", systemImage: "pencil") }
                                }
                            }
                    }
                }
            }
        }
        #if os(macOS)
        .listStyle(.inset)
        #endif
    }

    private var serverNow: Date { Date().addingTimeInterval(clockOffset) }

    private func statusCard(_ status: AlarmStatus) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            TimelineView(.periodic(from: .now, by: 0.5)) { _ in
                VStack(alignment: .leading, spacing: 4) {
                    Text(headline(status))
                        .font(.system(.title, design: .rounded, weight: .heavy))
                        .foregroundStyle(headlineColor(status))
                    Text(detail(status))
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
            }

            if status.status == "disarmed" {
                Label(status.ready == true ? "Ready to arm - every sensor is closed and reporting" : "Not ready",
                      systemImage: status.ready == true ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                    .font(.footnote)
                    .foregroundStyle(status.ready == true ? SentinelTheme.green : SentinelTheme.amber)
            }
            if let issues = status.issues, !issues.isEmpty {
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(issues, id: \.self) { issue in
                        Text("• \(issue)").font(.footnote).foregroundStyle(SentinelTheme.amber)
                    }
                }
            }

            modeButtons(status)
        }
        .padding(.vertical, 4)
    }

    private func modeButtons(_ status: AlarmStatus) -> some View {
        let current = status.mode ?? "disarmed"
        return VStack(spacing: 10) {
            HStack(spacing: 10) {
                modeButton("Disarm", mode: "disarmed", current: current, status: status, systemImage: "lock.open.fill")
                modeButton("Home", mode: "home", current: current, status: status, systemImage: "house.fill")
            }
            HStack(spacing: 10) {
                modeButton("Night", mode: "night", current: current, status: status, systemImage: "moon.fill")
                modeButton("Away", mode: "away", current: current, status: status, systemImage: "figure.walk")
            }
            Button(role: .destructive) { confirmPanic = true } label: {
                Label("Panic", systemImage: "light.beacon.max.fill")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.bordered)
            .tint(SentinelTheme.red)
            .disabled(status.isTriggered || isBusy)
        }
    }

    private func modeButton(_ label: String, mode: String, current: String, status: AlarmStatus, systemImage: String) -> some View {
        let selected = current == mode
        return Button {
            Task {
                if mode == "disarmed" { await disarm(pin: nil) } else { await arm(mode, force: false, pin: nil) }
            }
        } label: {
            Label(label, systemImage: systemImage)
                .frame(maxWidth: .infinity)
                .padding(.vertical, 6)
        }
        .buttonStyle(.borderedProminent)
        .tint(selected ? (mode == "disarmed" ? SentinelTheme.greenDim : SentinelTheme.green) : Color.secondary.opacity(0.35))
        .disabled(isBusy || (selected && !status.isTriggered))
    }

    private func headline(_ status: AlarmStatus) -> String {
        switch status.status {
        case "arming": "Arming away · \(secondsLeft(status.exitUntil))s to leave"
        case "pending": "Entry · \(secondsLeft(status.pendingUntil))s to disarm"
        case "triggered": "🚨 Alarm"
        case "armed": "Armed \(AlarmMode.label(for: status.mode ?? "").lowercased())"
        default: "Disarmed"
        }
    }

    private func headlineColor(_ status: AlarmStatus) -> Color {
        switch status.status {
        case "armed": SentinelTheme.green
        case "arming", "pending": SentinelTheme.amber
        case "triggered": SentinelTheme.red
        default: .secondary
        }
    }

    private func detail(_ status: AlarmStatus) -> String {
        let by = status.changedBy.map { " by \($0)" } ?? ""
        switch status.status {
        case "triggered":
            let when = status.triggeredAt.map { " at \($0.formatted(date: .omitted, time: .shortened))" } ?? ""
            return "\(status.triggeredBy ?? "Alarm")\(when)\(status.sirenOn == true ? " · siren on" : ""). Disarm to silence it."
        case "disarmed":
            guard let at = status.changedAt else { return "Never armed" }
            return "Disarmed\(by) \(at.formatted(date: .abbreviated, time: .shortened))"
        default:
            let since = status.changedAt.map { " since \($0.formatted(date: .omitted, time: .shortened))" } ?? ""
            return "\(AlarmMode.label(for: status.mode ?? ""))\(by)\(since)"
        }
    }

    private func secondsLeft(_ until: Date?) -> Int {
        guard let until else { return 0 }
        return max(0, Int(until.timeIntervalSince(serverNow).rounded(.up)))
    }

    private func takenDeviceIds(excluding: UUID?) -> Set<UUID> {
        Set((status?.sensors ?? []).filter { $0.id != excluding }.map(\.deviceId))
    }

    // MARK: - Actions

    private func refresh() async {
        do {
            let fresh = try await model.client.alarmStatus()
            if let now = fresh.now { clockOffset = now.timeIntervalSinceNow }
            status = fresh
            AlarmWidgetBridge.write(fresh)
            if pinPrompt == nil { errorMessage = nil }
        } catch {
            if status == nil { status = AlarmStatus(enabled: false) }
            errorMessage = error.localizedDescription
        }
    }

    private func loadDevices() async {
        guard model.isAdmin else { return }
        hubDevices = (try? await model.client.devices()) ?? []
    }

    private func arm(_ mode: String, force: Bool, pin: String?) async {
        isBusy = true
        defer { isBusy = false }
        do {
            let reply = try await model.client.armAlarm(mode: mode, force: force, pin: pin)
            if let note = reply.note { errorMessage = note }
            await refresh()
        } catch let ClientError.server(code, message) {
            if code == 403, status?.pinSet == true {
                errorMessage = pin == nil ? nil : message
                pinPrompt = PinPrompt(title: "PIN to arm \(AlarmMode.label(for: mode).lowercased())") { entered in
                    Task { await arm(mode, force: force, pin: entered) }
                }
            } else if code == 409, let message, message.contains("open") {
                openConfirm = OpenConfirm(mode: mode, message: message)
            } else {
                errorMessage = message ?? "Could not arm (HTTP \(code))."
            }
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func disarm(pin: String?) async {
        isBusy = true
        defer { isBusy = false }
        do {
            _ = try await model.client.disarmAlarm(pin: pin)
            errorMessage = nil
            await refresh()
        } catch let ClientError.server(code, message) {
            if code == 403, status?.pinSet == true {
                errorMessage = pin == nil ? nil : message
                pinPrompt = PinPrompt(title: "PIN to disarm") { entered in
                    Task { await disarm(pin: entered) }
                }
            } else {
                errorMessage = message ?? "Could not disarm (HTTP \(code))."
            }
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func panic() async {
        do {
            _ = try await model.client.alarmPanic()
            await refresh()
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func runTest() async {
        do {
            let reply = try await model.client.alarmTest()
            errorMessage = reply.note ?? "Test sent: the siren for five seconds, a notification, and the cameras."
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func delete(_ sensor: AlarmSensor) async {
        do {
            try await model.client.deleteAlarmSensor(id: sensor.id)
            await refresh()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

// One sensor: what it guards, whether it is open, its battery and signal, and anything wrong.
private struct AlarmSensorRow: View {
    let sensor: AlarmSensor
    let alarmStatus: String
    let mode: String
    let now: Date

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: icon)
                .font(.title3)
                .foregroundStyle(stateColor)
                .frame(width: 28)
            VStack(alignment: .leading, spacing: 3) {
                HStack(alignment: .firstTextBaseline) {
                    Text(sensor.name).font(.headline)
                    Spacer()
                    Text(sensor.stateLabel)
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(stateColor)
                }
                HStack(spacing: 10) {
                    Text(sensor.zoneLabel)
                    if let battery = sensor.battery {
                        Label("\(Int(battery.rounded()))%", systemImage: batteryIcon(battery))
                            .foregroundStyle(sensor.lowBattery ? SentinelTheme.amber : .secondary)
                    }
                    if let link = sensor.linkQuality {
                        Label("\(Int(link.rounded()))", systemImage: "antenna.radiowaves.left.and.right")
                    }
                }
                .font(.caption)
                .foregroundStyle(.secondary)
                Text(meta)
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                if !flags.isEmpty {
                    Text(flags.joined(separator: " · "))
                        .font(.caption2.weight(.semibold))
                        .foregroundStyle(flagColor)
                }
            }
        }
        .opacity(sensor.enabled && !sensor.offline ? 1 : 0.6)
    }

    private var icon: String {
        switch sensor.zone {
        case "entry": "door.left.hand.\(sensor.isOpen ? "open" : "closed")"
        case "interior": sensor.isOpen ? "figure.walk.motion" : "figure.stand"
        case "always": "drop.fill"
        default: sensor.isOpen ? "window.vertical.open" : "window.vertical.closed"
        }
    }

    private var stateColor: Color {
        switch sensor.state {
        case "open": SentinelTheme.amber
        case "closed": SentinelTheme.green
        default: .secondary
        }
    }

    private func batteryIcon(_ battery: Double) -> String {
        switch battery {
        case ..<15: "battery.0percent"
        case ..<40: "battery.25percent"
        case ..<65: "battery.50percent"
        case ..<90: "battery.75percent"
        default: "battery.100percent"
        }
    }

    private var meta: String {
        var parts: [String] = []
        if let seen = sensor.lastSeen { parts.append("heard \(ago(seen))") } else { parts.append("never heard") }
        if let changed = sensor.lastChanged { parts.append("changed \(ago(changed))") }
        if let device = sensor.deviceName { parts.append(sensor.room.map { "\(device) · \($0)" } ?? device) }
        return parts.joined(separator: " · ")
    }

    private func ago(_ date: Date) -> String {
        let seconds = max(0, Int(now.timeIntervalSince(date)))
        if seconds < 60 { return "\(seconds)s ago" }
        if seconds < 3600 { return "\(seconds / 60)m ago" }
        if seconds < 86400 { return "\(seconds / 3600)h ago" }
        return "\(seconds / 86400)d ago"
    }

    private var flags: [String] {
        var flags: [String] = []
        if sensor.deviceMissing { flags.append("device missing") } else if sensor.offline { flags.append("offline") }
        if sensor.bypassed { flags.append("skipped until disarm") }
        if sensor.lowBattery { flags.append("low battery") }
        if !sensor.enabled { flags.append("disabled") } else if !sensor.watched && alarmStatus != "disarmed" {
            flags.append("not watched in \(AlarmMode.label(for: mode).lowercased())")
        }
        return flags
    }

    private var flagColor: Color {
        sensor.deviceMissing || sensor.offline ? SentinelTheme.red : (sensor.bypassed || sensor.lowBattery ? SentinelTheme.amber : .secondary)
    }
}

// Add or edit one sensor: which Device Hub device it is, what it guards, when it is watched.
struct AlarmSensorEditView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss

    let sensor: AlarmSensor?
    let devices: [HubDevice]
    let taken: Set<UUID>
    let onSaved: () -> Void

    @State private var input = AlarmSensorInput()
    @State private var isSaving = false
    @State private var error: String?

    private var candidates: [HubDevice] {
        var list = devices.filter { $0.kind == "sensor" && !taken.contains($0.id) }
        if let sensor, !list.contains(where: { $0.id == sensor.deviceId }) {
            // The device it points at, even if the Device Hub no longer has it, so the form
            // shows the truth rather than silently switching to another device.
            list.insert(HubDevice.placeholder(id: sensor.deviceId, name: sensor.deviceName ?? "(missing device)"), at: 0)
        }
        return list
    }

    var body: some View {
        Form {
            if let error {
                Section { Text(error).foregroundStyle(SentinelTheme.red).font(.callout) }
            }
            Section("Sensor") {
                if candidates.isEmpty {
                    Text("No unused sensor devices in the Device Hub. Add the sensor on the Device Hub tab first - Discover finds Zigbee2MQTT ones by itself.")
                        .foregroundStyle(.secondary)
                } else {
                    Picker("Device Hub device", selection: $input.deviceId) {
                        ForEach(candidates) { device in
                            Text(device.room.isEmpty ? device.name : "\(device.name) (\(device.room))").tag(device.id)
                        }
                    }
                    .onChange(of: input.deviceId) { _, id in
                        if input.name.isEmpty, let device = candidates.first(where: { $0.id == id }) { input.name = device.name }
                    }
                }
                SentinelTextField(label: "Name", prompt: "e.g. Front door", text: $input.name)
                Picker("What it guards", selection: $input.zone) {
                    ForEach(AlarmZone.all, id: \.self) { zone in
                        Text(AlarmZone.label(for: zone)).tag(zone)
                    }
                }
                .onChange(of: input.zone) { _, zone in
                    guard sensor == nil else { return }
                    input.armedHome = zone != "interior"
                    input.armedNight = zone != "interior"
                    input.armedAway = true
                }
                Text(AlarmZone.explanation(for: input.zone)).font(.footnote).foregroundStyle(.secondary)
            }
            if input.zone != "always" {
                Section("Watched in") {
                    Toggle("Home", isOn: $input.armedHome)
                    Toggle("Night", isOn: $input.armedNight)
                    Toggle("Away", isOn: $input.armedAway)
                }
            }
            Section {
                Toggle("Reads the other way round", isOn: $input.inverted)
                Text("Most sensors say ON for open, motion or wet. If this one shows Open while the door is shut, turn this on.")
                    .font(.footnote).foregroundStyle(.secondary)
                Toggle("Enabled", isOn: $input.enabled)
            }
        }
        .formStyle(.grouped)
        .navigationTitle(sensor == nil ? "Add Sensor" : "Edit Sensor")
        .toolbar {
            ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
            ToolbarItem(placement: .confirmationAction) {
                Button(isSaving ? "Saving…" : "Save") { Task { await save() } }
                    .disabled(isSaving || candidates.isEmpty)
            }
        }
        .onAppear {
            if let sensor {
                input = AlarmSensorInput(sensor)
            } else if let first = candidates.first {
                input.deviceId = first.id
                input.name = first.name
            }
        }
    }

    private func save() async {
        isSaving = true
        defer { isSaving = false }
        do {
            if let sensor {
                try await model.client.updateAlarmSensor(id: sensor.id, input)
            } else {
                try await model.client.createAlarmSensor(input)
            }
            onSaved()
            dismiss()
        } catch {
            self.error = error.localizedDescription
        }
    }
}

// Delays, sirens, cameras, notifications and the PIN.
struct AlarmSettingsView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss

    @State var config: AlarmConfig
    let pinSet: Bool
    let devices: [HubDevice]
    let onSaved: () -> Void

    @State private var newPin = ""
    @State private var removePin = false
    @State private var isSaving = false
    @State private var error: String?

    private var switchable: [HubDevice] {
        devices.filter { !["sensor", "button", "cover", "gate", "garage", "lock"].contains($0.kind) }
    }

    var body: some View {
        Form {
            if let error {
                Section { Text(error).foregroundStyle(SentinelTheme.red).font(.callout) }
            }
            Section("Delays") {
                Stepper("Exit delay: \(config.exitDelaySeconds)s", value: $config.exitDelaySeconds, in: 0...600, step: 5)
                Text("How long you have to leave after arming Away. Home and Night arm at once.").font(.footnote).foregroundStyle(.secondary)
                Stepper("Entry delay: \(config.entryDelaySeconds)s", value: $config.entryDelaySeconds, in: 0...600, step: 5)
                Text("How long an entry door may be open before the siren, so whoever came in can disarm.").font(.footnote).foregroundStyle(.secondary)
                Stepper("Siren time: \(config.sirenSeconds)s", value: $config.sirenSeconds, in: 0...3600, step: 30)
            }
            Section("Switch on when triggered") {
                if switchable.isEmpty {
                    Text("No switchable Device Hub devices yet. A siren, a strobe or a plug with a lamp on it, added on the Device Hub tab, can be switched on when the alarm goes off.")
                        .foregroundStyle(.secondary)
                }
                ForEach(switchable) { device in
                    Toggle(device.room.isEmpty ? device.name : "\(device.name) (\(device.room))", isOn: binding(for: device.id, in: \.sirenDeviceIds))
                }
            }
            Section("Cameras to snapshot") {
                if model.cameras.isEmpty {
                    Text("No cameras yet.").foregroundStyle(.secondary)
                } else {
                    Text("None ticked means all of them.").font(.footnote).foregroundStyle(.secondary)
                }
                ForEach(model.cameras) { camera in
                    Toggle(camera.name, isOn: binding(for: camera.id, in: \.cameraIds))
                }
            }
            Section("Notifications") {
                Toggle("Notify everyone's phones", isOn: $config.notifyPush)
                Toggle("Also on every arm and disarm", isOn: $config.notifyArmDisarm)
                Stepper("Low battery at \(config.lowBatteryPercent)%", value: $config.lowBatteryPercent, in: 1...90, step: 5)
                Stepper("Offline after \(config.offlineAfterHours)h", value: $config.offlineAfterHours, in: 1...720, step: 1)
            }
            Section("PIN") {
                Toggle("Ask for the PIN to arm as well", isOn: $config.requirePinToArm)
                SentinelTextField(label: pinSet ? "New PIN (leave blank to keep)" : "PIN (4-8 digits)", prompt: pinSet ? "unchanged" : "no PIN", text: $newPin, secure: true)
                if pinSet {
                    Toggle("Remove the PIN", isOn: $removePin)
                }
                Text(pinSet ? "Disarming needs the PIN." : "No PIN: anyone signed in can disarm.").font(.footnote).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .navigationTitle("Alarm Settings")
        .toolbar {
            ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
            ToolbarItem(placement: .confirmationAction) {
                Button(isSaving ? "Saving…" : "Save") { Task { await save() } }.disabled(isSaving)
            }
        }
    }

    private func binding(for id: UUID, in keyPath: WritableKeyPath<AlarmConfig, [UUID]>) -> Binding<Bool> {
        Binding(
            get: { config[keyPath: keyPath].contains(id) },
            set: { on in
                if on { if !config[keyPath: keyPath].contains(id) { config[keyPath: keyPath].append(id) } }
                else { config[keyPath: keyPath].removeAll { $0 == id } }
            })
    }

    private func save() async {
        isSaving = true
        defer { isSaving = false }
        do {
            config = try await model.client.saveAlarmConfig(config)
            let trimmed = newPin.trimmingCharacters(in: .whitespaces)
            if removePin {
                try await model.client.setAlarmPin("", currentPin: nil)
            } else if !trimmed.isEmpty {
                try await model.client.setAlarmPin(trimmed, currentPin: nil)
            }
            onSaved()
            dismiss()
        } catch {
            self.error = error.localizedDescription
        }
    }
}

// Every arm, disarm, trigger, skipped sensor, low battery and outage, newest first.
struct AlarmEventsView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss

    @State private var events: [AlarmEvent] = []
    @State private var loaded = false

    var body: some View {
        List {
            if events.isEmpty && loaded {
                Text("Nothing has happened yet.").foregroundStyle(.secondary)
            }
            ForEach(events) { event in
                VStack(alignment: .leading, spacing: 2) {
                    Text(event.message).foregroundStyle(color(for: event.kind))
                    Text(event.at.formatted(date: .abbreviated, time: .shortened)).font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        .navigationTitle("Alarm Activity")
        .toolbar {
            ToolbarItem(placement: .cancellationAction) { Button("Done") { dismiss() } }
        }
        .task {
            events = (try? await model.client.alarmEvents(limit: 200)) ?? []
            loaded = true
        }
        .refreshable {
            events = (try? await model.client.alarmEvents(limit: 200)) ?? []
        }
    }

    private func color(for kind: String) -> Color {
        switch kind {
        case "triggered", "panic": SentinelTheme.red
        case "pending", "bypassed", "batteryLow", "offline", "pinRejected": SentinelTheme.amber
        case "armed", "arming": SentinelTheme.green
        default: .primary
        }
    }
}

extension HubDevice {
    // A stand-in for a device the alarm points at but the Device Hub no longer lists, so the
    // sensor form can show it rather than silently switching to another device.
    static func placeholder(id: UUID, name: String) -> HubDevice {
        HubDevice(
            id: id, name: name, kind: "sensor", room: "", enabled: false, commandTopic: "", stateTopic: "",
            availabilityTopic: "", payloadOn: "ON", payloadOff: "OFF", payloadStop: nil, momentary: nil,
            payloadAvailable: "", payloadNotAvailable: "", schema: "raw", stateValueTemplate: "", optimistic: false,
            supportsBrightness: false, brightnessCommandTopic: "", brightnessStateTopic: "", brightnessScale: 255,
            discoverySource: "manual", sortOrder: nil, power: "unknown", brightness: nil, available: false,
            commandable: false, measurements: nil)
    }
}

// The app's half of the Alarm widget (ios/SentinelCoreWidgets/AlarmWidget.swift): the last
// status seen, written to the App Group so the widget can draw it. iOS only - the Mac has no
// widget extension, and the Mac target lacks the app group.
@MainActor
enum AlarmWidgetBridge {
    static func write(_ status: AlarmStatus) {
        #if os(iOS)
        import_write(status)
        #endif
    }

    #if os(iOS)
    private static var last: AlarmWidgetSnapshot?

    private static func import_write(_ status: AlarmStatus) {
        guard let store = AlarmWidgetStore() else { return }
        let sensors = (status.sensors ?? []).filter(\.enabled)
        let snapshot = AlarmWidgetSnapshot(
            enabled: status.enabled,
            mode: status.mode ?? "disarmed",
            status: status.status ?? "disarmed",
            exitUntil: status.exitUntil,
            pendingUntil: status.pendingUntil,
            triggeredBy: status.triggeredBy,
            sensorCount: sensors.count,
            openCount: sensors.filter(\.isOpen).count,
            problemCount: sensors.filter { $0.offline || $0.deviceMissing || $0.lowBattery }.count,
            updatedAt: Date())
        // Only a change is worth a redraw; the widget budget is small and this runs every poll.
        var comparable = snapshot
        comparable.updatedAt = last?.updatedAt ?? .distantPast
        if comparable == last { return }
        last = snapshot
        store.write(snapshot)
        WidgetKit.WidgetCenter.shared.reloadTimelines(ofKind: "Alarm")
    }
    #endif
}
