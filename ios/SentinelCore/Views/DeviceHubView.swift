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
        .sheet(isPresented: $showingAddSheet) {
            NavigationStack {
                DeviceEditView(device: nil) { Task { await reload() } }
            }
        }
        .sheet(item: $editingDevice) { device in
            NavigationStack {
                DeviceEditView(device: device) { Task { await reload() } }
            }
        }
        .sheet(isPresented: $showingDiscoverySheet) {
            NavigationStack {
                DeviceDiscoveryView { Task { await reload() } }
            }
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
                            onToggle: { Task { await toggle(device) } },
                            onBrightness: { percent in Task { await setBrightness(device, percent: percent) } }
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
            }
            errorMessage = snapshot.connected ? nil : "Not connected to the MQTT broker."
        } catch {
            // A dropped poll isn't worth surfacing - the next one is five seconds away.
        }
    }

    private func toggle(_ device: HubDevice) async {
        pendingCommandIds.insert(device.id)
        defer { pendingCommandIds.remove(device.id) }

        do {
            let result = try await model.client.sendDeviceCommand(id: device.id, action: "toggle")
            apply(result, to: device.id)
            errorMessage = result.acknowledged ? nil : "\(device.name) did not confirm the change."
        } catch {
            errorMessage = error.localizedDescription
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

// One device. The brightness slider is committed on release rather than continuously, so
// dragging it doesn't publish an MQTT message per pixel to a real bulb.
private struct DeviceRow: View {
    let device: HubDevice
    let isBusy: Bool
    let onToggle: () -> Void
    let onBrightness: (Int) -> Void

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
                    Text(statusText)
                        .font(.caption)
                        .foregroundStyle(statusColor)
                }

                Spacer()

                if isBusy {
                    ProgressView()
                } else if device.commandable {
                    Toggle("", isOn: Binding(get: { device.isOn }, set: { _ in onToggle() }))
                        .labelsHidden()
                        .disabled(!device.enabled)
                        .accessibilityLabel("\(device.name) power")
                }
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

    private var statusText: String {
        if !device.enabled { return "Disabled" }
        if !device.available { return "Unavailable" }
        if !device.commandable { return "Sensor" }
        switch device.power {
        case "on": return device.supportsBrightness && device.brightness != nil ? "On · \(device.brightness!)%" : "On"
        case "off": return "Off"
        default: return "State unknown"
        }
    }

    private var statusColor: Color {
        if !device.enabled || !device.available { return SentinelTheme.amber }
        return device.isOn ? SentinelTheme.green : .secondary
    }
}
