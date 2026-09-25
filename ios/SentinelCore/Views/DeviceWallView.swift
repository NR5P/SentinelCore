import SwiftUI

// Where the wall display's "remembered on this device" flag lives. Outside the iOS-only block
// below because RootView reads it on every platform to decide where the app opens.
enum DeviceWall {
    static let storageKey = "deviceHub.wallDisplay"
}

#if os(iOS)
import UIKit

// The Device Hub for an iPad mounted on a wall. Presented full screen over the hub, so the tab
// bar and toolbar are gone; every switchable device is a big tile you tap, grouped by room, under
// a clock, and the screen is kept awake while it's up. Add, edit, delete and rearrange stay in
// the hub underneath - a wall tablet is for using the house, not configuring it.
//
// Commands go through the closures the hub hands in, so this view has no networking of its own,
// and the hub's five-second state poll keeps running underneath the cover and feeds
// model.devices, which is all this reads.
struct DeviceWallView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss

    let isBusy: (HubDevice) -> Bool
    let errorMessage: String?
    let onToggle: (HubDevice) -> Void
    let onAction: (HubDevice, String) -> Void
    let onBrightness: (HubDevice, Int) -> Void

    @State private var now = Date()
    @State private var idleTimerWasDisabled = false
    private let clock = Timer.publish(every: 10, on: .main, in: .common).autoconnect()

    var body: some View {
        VStack(spacing: 0) {
            header

            if model.devices.isEmpty {
                ContentUnavailableView {
                    Label("No Devices", systemImage: "lightbulb.slash")
                } description: {
                    Text("Tap Exit, then use Discover Devices or Add Device.")
                }
            } else {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 24) {
                        ForEach(rooms, id: \.self) { room in
                            VStack(alignment: .leading, spacing: 10) {
                                Text(room.isEmpty ? "Other" : room)
                                    .font(.caption.weight(.semibold))
                                    .tracking(3)
                                    .textCase(.uppercase)
                                    .foregroundStyle(SentinelTheme.cyan)

                                LazyVGrid(columns: [GridItem(.adaptive(minimum: 170), spacing: 14)], spacing: 14) {
                                    ForEach(devicesIn(room: room)) { device in
                                        DeviceWallTile(
                                            device: device,
                                            isBusy: isBusy(device),
                                            onToggle: { onToggle(device) },
                                            onAction: { onAction(device, $0) },
                                            onBrightness: { onBrightness(device, $0) }
                                        )
                                    }
                                }
                            }
                        }
                    }
                    .padding(18)
                }
            }
        }
        .background(Color(.systemBackground))
        .onReceive(clock) { now = $0 }
        .onAppear {
            // Auto-Lock would turn a wall tablet into a black rectangle two minutes in. Put back
            // the way it was on exit, which leaves it alone if something else (this device
            // streaming as a camera, say) had already switched it off.
            idleTimerWasDisabled = UIApplication.shared.isIdleTimerDisabled
            UIApplication.shared.isIdleTimerDisabled = true
        }
        .onDisappear {
            if !idleTimerWasDisabled { UIApplication.shared.isIdleTimerDisabled = false }
        }
    }

    private var header: some View {
        HStack(alignment: .firstTextBaseline, spacing: 14) {
            Text(now, format: .dateTime.hour().minute())
                .font(.system(size: 34, weight: .bold, design: .rounded))
                .monospacedDigit()
                .foregroundStyle(SentinelTheme.green)
            Text(now, format: .dateTime.weekday(.wide).month(.wide).day())
                .font(.caption)
                .textCase(.uppercase)
                .foregroundStyle(.secondary)

            Spacer()

            if let errorMessage {
                Text(errorMessage)
                    .font(.caption)
                    .foregroundStyle(SentinelTheme.amber)
                    .lineLimit(2)
            }

            // Small and in the corner on purpose, so a stray tap on a tile can't land on it.
            Button("Exit") { dismiss() }
                .buttonStyle(.bordered)
                .controlSize(.large)
                .accessibilityLabel("Leave the wall display")
        }
        .padding(.horizontal, 18)
        .padding(.top, 12)
        .padding(.bottom, 8)
    }

    // Rooms in the order the hub lists them - the arrangement someone dragged into place carries
    // over - with the unnamed ones last, the same as the hub's own grouping.
    private var rooms: [String] {
        var seen: [String] = []
        for device in model.devices where !seen.contains(device.room) {
            seen.append(device.room)
        }
        if let index = seen.firstIndex(of: "") { seen.append(seen.remove(at: index)) }
        return seen
    }

    private func devicesIn(room: String) -> [HubDevice] {
        model.devices.filter { $0.room == room }
    }
}

// One tile. The whole tile is the switch for anything that's simply on or off - a finger on a
// wall wants a target the size of a light switch, not a toggle in a corner. Gates, locks and
// momentary devices get big buttons instead, because a tap would be ambiguous for them. The
// brightness slider is committed on release, as everywhere else, so one drag is one MQTT publish.
private struct DeviceWallTile: View {
    let device: HubDevice
    let isBusy: Bool
    let onToggle: () -> Void
    let onAction: (String) -> Void
    let onBrightness: (Int) -> Void

    @State private var sliderValue: Double = 0
    @State private var isDragging = false

    private var isTappable: Bool {
        device.commandable && !device.isMomentary && !device.isCoverLike && !device.isLock
    }

    private var isLit: Bool { device.isOn && device.available && device.enabled }

    private let shape = RoundedRectangle(cornerRadius: 14, style: .continuous)

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Image(systemName: device.systemImage)
                    .font(.system(size: 30))
                    .foregroundStyle(isLit ? SentinelTheme.green : .secondary)
                Spacer()
                if isBusy {
                    ProgressView()
                } else {
                    Text(device.statusLabel)
                        .font(.caption.weight(.medium))
                        .tracking(1)
                        .textCase(.uppercase)
                        .multilineTextAlignment(.trailing)
                        .foregroundStyle(device.statusTint)
                }
            }

            Text(device.name)
                .font(.title3.weight(.semibold))
                .lineLimit(2)
                .frame(maxWidth: .infinity, alignment: .leading)

            // What a sensor reports, large: on a wall the readings are the whole point of it.
            if let measurements = device.measurements, !measurements.isEmpty {
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(measurements) { measurement in
                        HStack(alignment: .firstTextBaseline) {
                            Text(measurement.key)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                            Spacer()
                            Text(measurement.formattedValue)
                                .font(.title3)
                                .monospacedDigit()
                        }
                        .accessibilityElement(children: .combine)
                    }
                }
            }

            controls
        }
        .padding(16)
        .frame(maxWidth: .infinity, minHeight: 150, alignment: .topLeading)
        .background(shape.fill(isLit ? SentinelTheme.green.opacity(0.12) : Color(.secondarySystemBackground)))
        .overlay(shape.strokeBorder(isLit ? SentinelTheme.green.opacity(0.6) : Color.secondary.opacity(0.25)))
        .contentShape(shape)
        .opacity(device.available && device.enabled ? 1 : 0.55)
        .onTapGesture {
            if isTappable && device.enabled && !isBusy { onToggle() }
        }
        .accessibilityAddTraits(isTappable ? .isButton : [])
        .accessibilityLabel(isTappable ? "\(device.name) power" : device.name)
        .accessibilityValue(isTappable ? (device.isOn ? "On" : "Off") : "")
        .animation(.easeInOut(duration: 0.16), value: isLit)
        .onAppear { sliderValue = Double(device.brightness ?? 100) }
        .onChange(of: device.brightness) { _, newValue in
            // Never yank the slider out from under a finger that's still on it.
            guard !isDragging, let newValue else { return }
            sliderValue = Double(newValue)
        }
    }

    @ViewBuilder
    private var controls: some View {
        if device.commandable && device.isMomentary {
            bigButton("Press") { onAction("press") }
        } else if device.commandable && device.isCoverLike {
            HStack(spacing: 8) {
                bigButton("Open") { onAction("open") }
                if device.hasStop {
                    bigButton("Stop") { onAction("stop") }
                }
                bigButton("Close") { onAction("close") }
            }
        } else if device.commandable && device.isLock {
            HStack(spacing: 8) {
                bigButton("Unlock") { onAction("unlock") }
                bigButton("Lock") { onAction("lock") }
            }
        } else if device.commandable && device.supportsBrightness {
            Slider(
                value: $sliderValue,
                in: 0...100,
                step: 1,
                onEditingChanged: { editing in
                    isDragging = editing
                    if !editing { onBrightness(Int(sliderValue)) }
                }
            )
            .tint(SentinelTheme.green)
            .disabled(!device.enabled)
            .accessibilityLabel("\(device.name) brightness")
        }
    }

    private func bigButton(_ title: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .frame(maxWidth: .infinity, minHeight: 36)
        }
        .buttonStyle(.bordered)
        .disabled(!device.enabled || isBusy)
    }
}
#endif
