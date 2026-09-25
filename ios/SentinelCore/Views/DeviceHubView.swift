import SwiftUI

// Lights, plugs, and anything else on the MQTT broker, switchable from the phone or the Mac.
// Only reachable when the server has the Device Hub turned on (see AppSection.visibleCases).
// Any signed-in account can switch a device; adding, editing, deleting, and discovery are
// Admin-only server-side, so those controls are hidden for a Viewer rather than left to 403.
struct DeviceHubView: View {
    @EnvironmentObject private var model: AppModel

    // Passed down from RootView so the state poll only runs while this section is on screen,
    // matching how LiveCamerasView stops its streams when you navigate away.
    let isActive: Bool

    @State private var isLoading = false
    @State private var errorMessage: String?
    @State private var pendingCommandIds: Set<UUID> = []
    @State private var showingAddSheet = false
    @State private var showingDiscoverySheet = false
    @State private var editingDevice: HubDevice?
    // Set by the Mac's row delete button. A click is far easier to hit by accident than a swipe,
    // so that path confirms first; the iPhone's swipe action deletes outright as it always has.
    @State private var deviceToDelete: HubDevice?
    // The wall display: big tap-to-switch tiles for an iPad mounted on a wall, presented over
    // this view. Remembered on this device so the app comes back to it - RootView opens the
    // Device Hub on launch while it's set - and cleared by the wall display's own Exit.
    @AppStorage(DeviceWall.storageKey) private var wallDisplay = false

    private static let statePollInterval = Duration.seconds(5)

    var body: some View {
        Group {
            if model.devices.isEmpty && !isLoading {
                emptyState
            } else {
                deviceList
            }
        }
        .navigationTitle("Device Hub")
        .toolbar {
            #if os(iOS)
            // Drag-to-reorder needs edit mode on iPhone; on the Mac rows drag directly.
            if model.isAdmin && !model.devices.isEmpty {
                ToolbarItem(placement: .topBarLeading) {
                    EditButton()
                }
            }
            // For everyone, not just admins - switching devices is the whole point of it. Not on
            // the Mac: there is no full-screen cover there, and no Mac hangs on a wall.
            if !model.devices.isEmpty {
                ToolbarItem(placement: .topBarTrailing) {
                    Button {
                        wallDisplay = true
                    } label: {
                        Label("Wall Display", systemImage: "rectangle.inset.filled.on.rectangle")
                    }
                }
            }
            #endif
            if model.isAdmin {
                ToolbarItem(placement: .primaryAction) {
                    Menu {
                        Button {
                            showingDiscoverySheet = true
                        } label: {
                            Label("Discover Devices", systemImage: "antenna.radiowaves.left.and.right")
                        }
                        Button {
                            showingAddSheet = true
                        } label: {
                            Label("Add Device", systemImage: "plus")
                        }
                    } label: {
                        Label("Add", systemImage: "plus")
                    }
                }
            }
        }
        .refreshable { await reload() }
        #if os(iOS)
        // The commands are this view's own, so the wall display needs no networking; the state
        // poll below keeps running underneath the cover and feeds the tiles through model.devices.
        .fullScreenCover(isPresented: $wallDisplay) {
            DeviceWallView(
                isBusy: { pendingCommandIds.contains($0.id) },
                errorMessage: errorMessage,
                onToggle: { device in Task { await toggle(device) } },
                onAction: { device, action in Task { await send(device, action: action) } },
                onBrightness: { device, percent in Task { await setBrightness(device, percent: percent) } }
            )
        }
        #endif
        .sheet(isPresented: $showingAddSheet) {
            NavigationStack {
                DeviceEditView(device: nil) { Task { await reload() } }
            }
            .deviceHubSheetFrame()
        }
        .sheet(item: $editingDevice) { device in
            NavigationStack {
                DeviceEditView(device: device) { Task { await reload() } }
            }
            .deviceHubSheetFrame()
        }
        .confirmationDialog(
            deviceToDelete.map { "Delete \($0.name)?" } ?? "Delete device?",
            isPresented: Binding(
                get: { deviceToDelete != nil },
                set: { if !$0 { deviceToDelete = nil } }
            ),
            presenting: deviceToDelete
        ) { device in
            Button("Delete", role: .destructive) {
                deviceToDelete = nil
                Task { await delete(device) }
            }
            Button("Cancel", role: .cancel) { deviceToDelete = nil }
        } message: { device in
            Text("\(device.name) will be removed from the Device Hub. This can't be undone, but Discover Devices can find it again if it's still on the broker.")
        }
        .sheet(isPresented: $showingDiscoverySheet) {
            NavigationStack {
                DeviceDiscoveryView { Task { await reload() } }
            }
            .deviceHubSheetFrame()
        }
        .task(id: isActive) {
            guard isActive else { return }
            await reload()
            // Cancelled automatically when isActive flips or the view goes away, which is what
            // stops the poll - there's no separate teardown to keep in sync.
            while !Task.isCancelled {
                try? await Task.sleep(for: Self.statePollInterval)
                if Task.isCancelled { return }
                await refreshStates()
            }
        }
    }

    private var emptyState: some View {
        ContentUnavailableView {
            Label("No Devices", systemImage: "lightbulb.slash")
        } description: {
            Text(model.isAdmin
                 ? "Use Discover Devices to find what's already on your MQTT broker, or add one by hand."
                 : "No devices have been added yet. An admin account can add them.")
        }
    }

    private var deviceList: some View {
        List {
            if let errorMessage {
                Text(errorMessage)
                    .foregroundStyle(SentinelTheme.red)
                    .font(.callout)
            }

            // Grouped by room so a house with more than a handful of devices stays navigable.
            // Devices with no room fall into one "Other" group at the end.
            ForEach(groupedRooms, id: \.self) { room in
                Section(room.isEmpty ? "Other" : room) {
                    ForEach(devicesIn(room: room)) { device in
                        DeviceRow(
                            device: device,
                            isBusy: pendingCommandIds.contains(device.id),
                            canEdit: model.isAdmin,
                            onToggle: { Task { await toggle(device) } },
                            onAction: { action in Task { await send(device, action: action) } },
                            onBrightness: { percent in Task { await setBrightness(device, percent: percent) } },
                            onEdit: { editingDevice = device },
                            onDelete: { deviceToDelete = device }
                        )
                        .swipeActions(edge: .trailing) {
                            if model.isAdmin {
                                Button(role: .destructive) {
                                    Task { await delete(device) }
                                } label: {
                                    Label("Delete", systemImage: "trash")
                                }
                                Button {
                                    editingDevice = device
                                } label: {
                                    Label("Edit", systemImage: "pencil")
                                }
                                .tint(SentinelTheme.cyan)
                            }
                        }
                    }
                    .onMove { source, destination in
                        move(inRoom: room, from: source, to: destination)
                    }
                    .moveDisabled(!model.isAdmin)
                }
            }
        }
    }

    private var groupedRooms: [String] {
        var seen: [String] = []
        for device in model.devices where !seen.contains(device.room) {
            seen.append(device.room)
        }
        // The server already sorts by room then name, so the only reordering needed is pushing
        // the unnamed group to the bottom.
        return seen.sorted { lhs, rhs in
            if lhs.isEmpty != rhs.isEmpty { return !lhs.isEmpty }
            return lhs.localizedCaseInsensitiveCompare(rhs) == .orderedAscending
        }
    }

    private func devicesIn(room: String) -> [HubDevice] {
        model.devices.filter { $0.room == room }
    }

    // MARK: - Actions

    private func reload() async {
        isLoading = true
        defer { isLoading = false }
        do {
            model.devices = try await model.client.devices()
            errorMessage = nil
            // The list carries the server's latest cached values, then this asks the dedicated
            // state endpoint immediately. Do not make startup wait for the first five-second poll.
            await refreshStates()
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func refreshStates() async {
        do {
            let snapshot = try await model.client.deviceStates()
            for state in snapshot.devices {
                guard let index = model.devices.firstIndex(where: { $0.id == state.id }) else { continue }
                model.devices[index].power = state.power
                model.devices[index].brightness = state.brightness
                model.devices[index].available = state.available
                model.devices[index].measurements = state.measurements
            }
            errorMessage = snapshot.connected ? nil : "Not connected to the MQTT broker."
        } catch {
            // A dropped poll isn't worth surfacing - the next one is five seconds away.
        }
    }

    private func toggle(_ device: HubDevice) async {
        await send(device, action: "toggle")
    }

    // One path for every named action - toggle, open, close, stop, press, lock, unlock. The
    // server maps the kind-flavoured names onto the device's own payloads.
    private func send(_ device: HubDevice, action: String) async {
        pendingCommandIds.insert(device.id)
        defer { pendingCommandIds.remove(device.id) }

        do {
            let result = try await model.client.sendDeviceCommand(id: device.id, action: action)
            apply(result, to: device.id)
            // A momentary press has no echoed state to wait for, so "unconfirmed" is its normal
            // successful outcome, not something to warn about.
            errorMessage = result.acknowledged || device.isMomentary
                ? nil
                : "\(device.name) did not confirm the change."
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    // Reorder within one room section, then persist the complete flattened arrangement - the
    // same optimistic pattern as AppModel.moveLiveCamera: the list moves now, the save happens
    // in the background.
    private func move(inRoom room: String, from source: IndexSet, to destination: Int) {
        var roomDevices = devicesIn(room: room)
        roomDevices.move(fromOffsets: source, toOffset: destination)

        var reordered: [HubDevice] = []
        for existingRoom in groupedRooms {
            reordered.append(contentsOf: existingRoom == room ? roomDevices : devicesIn(room: existingRoom))
        }
        model.devices = reordered

        let ids = reordered.map(\.id)
        Task {
            do {
                try await model.client.saveDeviceOrder(ids)
            } catch {
                errorMessage = "Could not save the device order: \(error.localizedDescription)"
            }
        }
    }

    private func setBrightness(_ device: HubDevice, percent: Int) async {
        pendingCommandIds.insert(device.id)
        defer { pendingCommandIds.remove(device.id) }

        do {
            let result = try await model.client.sendDeviceCommand(id: device.id, brightness: percent)
            apply(result, to: device.id)
            errorMessage = nil
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func apply(_ result: DeviceCommandResult, to id: UUID) {
        guard let index = model.devices.firstIndex(where: { $0.id == id }) else { return }
        model.devices[index].power = result.power
        if let brightness = result.brightness { model.devices[index].brightness = brightness }
    }

    private func delete(_ device: HubDevice) async {
        do {
            try await model.client.deleteDevice(id: device.id)
            await reload()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

// A macOS sheet sizes itself to its content's ideal size, and a List reports essentially none -
// which collapsed the Discover Devices sheet to a bare title bar with no way to start a scan.
// An explicit minimum keeps every Device Hub sheet usable; iPhone sheets size themselves and
// need nothing.
extension View {
    @ViewBuilder
    func deviceHubSheetFrame() -> some View {
        #if os(macOS)
        self.frame(minWidth: 560, minHeight: 560)
        #else
        self
        #endif
    }
}

// One device. The brightness slider is committed on release rather than continuously, so
// dragging it doesn't publish an MQTT message per pixel to a real bulb.
private struct DeviceRow: View {
    let device: HubDevice
    let isBusy: Bool
    let canEdit: Bool
    let onToggle: () -> Void
    let onAction: (String) -> Void
    let onBrightness: (Int) -> Void
    let onEdit: () -> Void
    let onDelete: () -> Void

    @State private var sliderValue: Double = 0
    @State private var isDragging = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 12) {
                Image(systemName: device.systemImage)
                    .foregroundStyle(device.isOn && device.available ? SentinelTheme.green : .secondary)
                    .font(.title3)
                    .frame(width: 28)

                VStack(alignment: .leading, spacing: 2) {
                    Text(device.name)
                    Text(device.statusLabel)
                        .font(.caption)
                        .foregroundStyle(device.statusTint)
                }

                Spacer()

                if isBusy {
                    ProgressView()
                } else if device.commandable {
                    controls
                }

                #if os(macOS)
                // The Mac has no swipe gesture, so the swipe actions that carry Edit and Delete on
                // iPhone are unreachable here - these buttons are the Mac's only route to either.
                // They stay visible while a command is in flight, and a sensor with no controls
                // still gets both.
                if canEdit {
                    Button(action: onEdit) {
                        Image(systemName: "pencil")
                    }
                    .buttonStyle(.borderless)
                    .foregroundStyle(SentinelTheme.cyan)
                    .help("Edit \(device.name)")
                    .accessibilityLabel("Edit \(device.name)")

                    Button(action: onDelete) {
                        Image(systemName: "trash")
                    }
                    .buttonStyle(.borderless)
                    .foregroundStyle(SentinelTheme.red)
                    .help("Delete \(device.name)")
                    .accessibilityLabel("Delete \(device.name)")
                }
                #endif
            }

            // What the device actually reports, under its own keys. Absent for the switches and
            // lamps that report nothing, so the row simply doesn't grow a gap.
            if let measurements = device.measurements, !measurements.isEmpty {
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(measurements) { measurement in
                        HStack {
                            Text(measurement.key)
                                .foregroundStyle(.secondary)
                            Spacer()
                            Text(measurement.formattedValue)
                                .monospacedDigit()
                        }
                        .accessibilityElement(children: .combine)
                    }
                }
                .font(.caption)
                .padding(.leading, 40)
            }

            if device.commandable && device.supportsBrightness {
                HStack(spacing: 8) {
                    Image(systemName: "sun.min").foregroundStyle(.secondary).font(.caption)
                    Slider(
                        value: $sliderValue,
                        in: 0...100,
                        step: 1,
                        onEditingChanged: { editing in
                            isDragging = editing
                            if !editing { onBrightness(Int(sliderValue)) }
                        }
                    )
                    .disabled(!device.enabled)
                    .accessibilityLabel("\(device.name) brightness")
                    Image(systemName: "sun.max").foregroundStyle(.secondary).font(.caption)
                }
            }
        }
        .padding(.vertical, 4)
        .opacity(device.available && device.enabled ? 1 : 0.55)
        .onAppear { sliderValue = Double(device.brightness ?? 100) }
        .onChange(of: device.brightness) { _, newValue in
            // Never yank the slider out from under a finger that's still on it.
            guard !isDragging, let newValue else { return }
            sliderValue = Double(newValue)
        }
    }

    // Per-kind controls: a gate gets Open/Close (and Stop when the motor takes one), a lock gets
    // Lock/Unlock, a momentary device gets a single Press, everything else keeps the switch.
    @ViewBuilder
    private var controls: some View {
        if device.isMomentary {
            Button("Press") { onAction("press") }
                .buttonStyle(.borderedProminent)
                .disabled(!device.enabled)
                .accessibilityLabel("Press \(device.name)")
        } else if device.isCoverLike {
            HStack(spacing: 8) {
                Button("Open") { onAction("open") }.buttonStyle(.bordered)
                if device.hasStop {
                    Button("Stop") { onAction("stop") }.buttonStyle(.bordered)
                }
                Button("Close") { onAction("close") }.buttonStyle(.bordered)
            }
            .disabled(!device.enabled)
        } else if device.isLock {
            HStack(spacing: 8) {
                Button("Unlock") { onAction("unlock") }.buttonStyle(.bordered)
                Button("Lock") { onAction("lock") }.buttonStyle(.bordered)
            }
            .disabled(!device.enabled)
        } else {
            #if os(macOS)
            SlidingPowerSwitch(
                isOn: device.isOn,
                isEnabled: device.enabled,
                accessibilityName: device.name,
                onToggle: onToggle
            )
            #else
            Toggle("", isOn: Binding(get: { device.isOn }, set: { _ in onToggle() }))
                .labelsHidden()
                .toggleStyle(.switch)
                .disabled(!device.enabled)
                .accessibilityLabel("\(device.name) power")
            #endif
        }
    }

}

// The one-line answer to "what is this device doing", shared by the list row and the wall tile.
extension HubDevice {
    var statusLabel: String {
        if !enabled { return "Disabled" }
        if !available { return "Unavailable" }
        if isReporting { return "Reporting" }
        if !commandable { return "Sensor" }
        // A pulse device has no on/off to report - reachable is the whole story.
        if isMomentary { return "Ready" }
        switch power {
        case "on":
            if isCoverLike { return "Open" }
            if isLock { return "Locked" }
            return supportsBrightness && brightness != nil ? "On · \(brightness!)%" : "On"
        case "off":
            if isCoverLike { return "Closed" }
            if isLock { return "Unlocked" }
            return "Off"
        default: return "State unknown"
        }
    }

    var statusTint: Color {
        if !enabled || !available { return SentinelTheme.amber }
        return isOn ? SentinelTheme.green : .secondary
    }
}

#if os(macOS)
// SwiftUI's default macOS Toggle can resolve to the AppKit checkbox style depending on its List
// environment. Draw the familiar sliding control explicitly so this can never regress into a
// checkbox while leaving iPhone's native switch untouched.
private struct SlidingPowerSwitch: View {
    let isOn: Bool
    let isEnabled: Bool
    let accessibilityName: String
    let onToggle: () -> Void

    var body: some View {
        Button(action: onToggle) {
            Capsule()
                .fill(isOn ? SentinelTheme.green : Color.secondary.opacity(0.35))
                .frame(width: 42, height: 24)
                .overlay {
                    Circle()
                        .fill(.white)
                        .shadow(color: .black.opacity(0.22), radius: 1, y: 1)
                        .padding(2)
                        .frame(maxWidth: .infinity, alignment: isOn ? .trailing : .leading)
                }
                .animation(.easeInOut(duration: 0.16), value: isOn)
        }
        .buttonStyle(.plain)
        .disabled(!isEnabled)
        .accessibilityLabel("\(accessibilityName) power")
        .accessibilityValue(isOn ? "On" : "Off")
        .accessibilityAddTraits(.isButton)
    }
}
#endif
