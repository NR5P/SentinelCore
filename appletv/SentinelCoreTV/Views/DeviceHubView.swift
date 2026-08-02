import SwiftUI

// tvOS port of ios/SentinelCore/Views/DeviceHubView.swift, deliberately control-only: devices are
// added and configured on the web dashboard, iPhone, Mac, or Android, because typing an MQTT topic
// like "zigbee2mqtt/kitchen/set" with the Siri remote's on-screen keyboard is miserable. Here the
// remote does what it's good at - pick a card, click to switch it.
struct DeviceHubView: View {
    @EnvironmentObject private var model: AppModel

    let isActive: Bool

    @State private var errorMessage: String?
    @State private var pendingCommandIds: Set<UUID> = []

    private static let statePollInterval = Duration.seconds(5)

    var body: some View {
        ScrollView {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 380), spacing: 36)], spacing: 36) {
                ForEach(model.devices) { device in
                    DeviceCard(
                        device: device,
                        isBusy: pendingCommandIds.contains(device.id),
                        onToggle: { Task { await toggle(device) } },
                        onBrightnessStep: { delta in Task { await step(device, by: delta) } }
                    )
                }
            }
            .padding(.horizontal, 60)
            .padding(.bottom, 60)
        }
        .toolbar {
            ToolbarItem {
                Button { Task { await reload() } } label: {
                    Image(systemName: "arrow.clockwise").accessibilityLabel("Refresh")
                }
                .disabled(model.isLoading)
            }
        }
        .overlay {
            if let errorMessage {
                Text(errorMessage)
                    .foregroundStyle(SentinelTheme.amber)
                    .padding()
                    .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 12))
                    .frame(maxHeight: .infinity, alignment: .bottom)
                    .padding(.bottom, 40)
            }

            if model.devices.isEmpty && !model.isLoading {
                ContentUnavailableView(
                    "No devices",
                    systemImage: "lightbulb.slash",
                    description: Text("Add devices from the Sentinel Core dashboard, the iPhone or Mac app, or Android. They'll appear here to switch on and off.")
                )
                .foregroundStyle(.secondary)
            }
        }
        .task(id: isActive) {
            guard isActive else { return }
            await reload()
            while !Task.isCancelled {
                try? await Task.sleep(for: Self.statePollInterval)
                if Task.isCancelled { return }
                await refreshStates()
            }
        }
    }

    private func reload() async {
        do {
            model.devices = try await model.client.devices()
            errorMessage = nil
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

    // There's no slider worth dragging with a remote, so brightness moves in fixed steps that
    // the left/right buttons on a focused card map onto.
    private func step(_ device: HubDevice, by delta: Int) async {
        pendingCommandIds.insert(device.id)
        defer { pendingCommandIds.remove(device.id) }

        let target = max(0, min(100, (device.brightness ?? 100) + delta))
        do {
            let result = try await model.client.sendDeviceCommand(id: device.id, brightness: target)
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
}

private struct DeviceCard: View {
    let device: HubDevice
    let isBusy: Bool
    let onToggle: () -> Void
    let onBrightnessStep: (Int) -> Void

    var body: some View {
        Button(action: onToggle) {
            VStack(alignment: .leading, spacing: 16) {
                HStack {
                    Image(systemName: device.systemImage)
                        .font(.system(size: 44))
                        .foregroundStyle(device.isOn && device.available ? SentinelTheme.green : .secondary)
                    Spacer()
                    if isBusy {
                        ProgressView()
                    } else {
                        Text(statusText)
                            .font(.caption)
                            .foregroundStyle(statusColor)
                    }
                }

                VStack(alignment: .leading, spacing: 4) {
                    Text(device.name).bold().lineLimit(2)
                    if !device.room.isEmpty {
                        Text(device.room)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }

                if device.supportsBrightness, let brightness = device.brightness {
                    // A read-only bar: the actual adjustment is the left/right remote buttons
                    // below, since a draggable slider isn't a thing the Siri remote does well.
                    ProgressView(value: Double(brightness), total: 100)
                        .tint(SentinelTheme.green)
                    Text("Brightness \(brightness)% · swipe left or right to adjust")
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(24)
        }
        .buttonStyle(.card)
        .disabled(!device.enabled || !device.commandable)
        .opacity(device.available && device.enabled ? 1 : 0.55)
        .accessibilityLabel("\(device.name), \(statusText)")
        // Only meaningful while this card holds focus, which is exactly when the user's swipe is
        // aimed at it - tvOS routes these to the focused view.
        .onMoveCommand { direction in
            guard device.supportsBrightness, device.enabled else { return }
            switch direction {
            case .left: onBrightnessStep(-10)
            case .right: onBrightnessStep(10)
            default: break
            }
        }
    }

    private var statusText: String {
        if !device.enabled { return "Disabled" }
        if !device.available { return "Unavailable" }
        if !device.commandable { return "Sensor" }
        switch device.power {
        case "on": return "On"
        case "off": return "Off"
        default: return "Unknown"
        }
    }

    private var statusColor: Color {
        if !device.enabled || !device.available { return SentinelTheme.amber }
        return device.isOn ? SentinelTheme.green : .secondary
    }
}
