import SwiftUI

// tvOS port of ios/SentinelCore/Views/AlarmView.swift, control-only like the Device Hub tab:
// arm, disarm (with the PIN if there is one) and see the sensors. Sensors and settings are set
// up on the dashboard, iPhone, Mac or Android - not with the Siri remote.
struct AlarmView: View {
    @EnvironmentObject private var model: AppModel

    let isActive: Bool

    @State private var status: AlarmStatus?
    @State private var errorMessage: String?
    @State private var clockOffset: TimeInterval = 0
    @State private var pinFor: String?
    @State private var pinText = ""
    @State private var confirmOpen: (mode: String, message: String)?

    private static let pollInterval = Duration.seconds(2)

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 36) {
                if let status, status.enabled {
                    statusCard(status)
                    sensorGrid(status.sensors ?? [])
                } else if status == nil {
                    ProgressView()
                } else {
                    Text("The alarm is turned off in the server's Settings.").foregroundStyle(.secondary)
                }
            }
            .padding(.horizontal, 60)
            .padding(.bottom, 60)
        }
        .overlay(alignment: .bottom) {
            if let errorMessage {
                Text(errorMessage)
                    .foregroundStyle(SentinelTheme.amber)
                    .padding()
                    .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 12))
                    .padding(.bottom, 40)
            }
        }
        .alert("Alarm PIN", isPresented: Binding(get: { pinFor != nil }, set: { if !$0 { pinFor = nil } })) {
            TextField("PIN", text: $pinText)
            Button("OK") {
                let mode = pinFor ?? "disarmed"
                let pin = pinText
                pinText = ""
                Task { await change(to: mode, force: false, pin: pin) }
            }
            Button("Cancel", role: .cancel) { pinText = "" }
        }
        .alert("Something is open", isPresented: Binding(get: { confirmOpen != nil }, set: { if !$0 { confirmOpen = nil } })) {
            Button("Arm Anyway") {
                if let confirm = confirmOpen { Task { await change(to: confirm.mode, force: true, pin: nil) } }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(confirmOpen?.message ?? "")
        }
        .task(id: isActive) {
            guard isActive else { return }
            await refresh()
            while !Task.isCancelled {
                try? await Task.sleep(for: Self.pollInterval)
                if Task.isCancelled { return }
                await refresh()
            }
        }
    }

    private var serverNow: Date { Date().addingTimeInterval(clockOffset) }

    private func statusCard(_ status: AlarmStatus) -> some View {
        VStack(alignment: .leading, spacing: 18) {
            TimelineView(.periodic(from: .now, by: 0.5)) { _ in
                Text(headline(status))
                    .font(.system(size: 56, weight: .heavy, design: .rounded))
                    .foregroundStyle(headlineColor(status))
            }
            Text(detail(status)).font(.title3).foregroundStyle(.secondary)
            if let issues = status.issues, !issues.isEmpty, status.status == "disarmed" {
                Text(issues.joined(separator: " · ")).font(.callout).foregroundStyle(SentinelTheme.amber)
            }
            HStack(spacing: 20) {
                modeButton("Disarm", mode: "disarmed", status: status, systemImage: "lock.open.fill")
                modeButton("Home", mode: "home", status: status, systemImage: "house.fill")
                modeButton("Night", mode: "night", status: status, systemImage: "moon.fill")
                modeButton("Away", mode: "away", status: status, systemImage: "figure.walk")
            }
        }
        .padding(32)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 20))
    }

    private func modeButton(_ label: String, mode: String, status: AlarmStatus, systemImage: String) -> some View {
        let selected = (status.mode ?? "disarmed") == mode
        return Button {
            Task { await change(to: mode, force: false, pin: nil) }
        } label: {
            Label(label, systemImage: systemImage)
                .frame(minWidth: 200)
                // The current mode's button is filled green and disabled; without this the
                // disabled label is dimmed to the same green and vanishes.
                .foregroundStyle(selected ? Color.black : Color.primary)
        }
        .tint(selected ? SentinelTheme.green : nil)
        .disabled(selected && !status.isTriggered)
    }

    private func sensorGrid(_ sensors: [AlarmSensor]) -> some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 380), spacing: 36)], spacing: 36) {
            ForEach(sensors) { sensor in
                VStack(alignment: .leading, spacing: 8) {
                    HStack {
                        Text(sensor.name).font(.headline)
                        Spacer()
                        Text(sensor.stateLabel)
                            .font(.headline)
                            .foregroundStyle(sensor.isOpen ? SentinelTheme.amber : sensor.state == "closed" ? SentinelTheme.green : .secondary)
                    }
                    HStack(spacing: 14) {
                        Text(sensor.zoneLabel)
                        if let battery = sensor.battery {
                            Label("\(Int(battery.rounded()))%", systemImage: sensor.lowBattery ? "battery.25percent" : "battery.100percent")
                                .foregroundStyle(sensor.lowBattery ? SentinelTheme.amber : .secondary)
                        }
                    }
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    if sensor.offline || sensor.deviceMissing || sensor.bypassed || !sensor.enabled {
                        Text(sensor.deviceMissing ? "Device missing" : sensor.offline ? "Offline" : sensor.bypassed ? "Skipped until disarm" : "Disabled")
                            .font(.callout)
                            .foregroundStyle(sensor.bypassed || !sensor.enabled ? .secondary : SentinelTheme.red)
                    }
                }
                .padding(24)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 16))
                .opacity(sensor.enabled ? 1 : 0.6)
            }
        }
    }

    private func headline(_ status: AlarmStatus) -> String {
        switch status.status {
        case "arming": "Arming away · \(secondsLeft(status.exitUntil))s to leave"
        case "pending": "Entry · \(secondsLeft(status.pendingUntil))s to disarm"
        case "triggered": "🚨 Alarm"
        case "armed": "Armed \((status.mode ?? "").capitalized)"
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
            return "\(status.triggeredBy ?? "Alarm")\(status.sirenOn == true ? " · siren on" : ""). Disarm to silence it."
        case "disarmed":
            guard let at = status.changedAt else { return "Never armed" }
            return "Disarmed\(by) \(at.formatted(date: .abbreviated, time: .shortened))"
        default:
            let since = status.changedAt.map { " since \($0.formatted(date: .omitted, time: .shortened))" } ?? ""
            return "\((status.mode ?? "").capitalized)\(by)\(since)"
        }
    }

    private func secondsLeft(_ until: Date?) -> Int {
        guard let until else { return 0 }
        return max(0, Int(until.timeIntervalSince(serverNow).rounded(.up)))
    }

    private func refresh() async {
        do {
            let fresh = try await model.client.alarmStatus()
            if let now = fresh.now { clockOffset = now.timeIntervalSinceNow }
            status = fresh
        } catch {
            if status == nil { status = AlarmStatus(enabled: false) }
            errorMessage = error.localizedDescription
        }
    }

    private func change(to mode: String, force: Bool, pin: String?) async {
        do {
            if mode == "disarmed" {
                _ = try await model.client.disarmAlarm(pin: pin)
            } else {
                let reply = try await model.client.armAlarm(mode: mode, force: force, pin: pin)
                errorMessage = reply.note
            }
            if pin != nil || mode == "disarmed" { errorMessage = nil }
            await refresh()
        } catch let ClientError.server(code, message) {
            if code == 403, status?.pinSet == true {
                errorMessage = pin == nil ? nil : message
                pinFor = mode
            } else if code == 409, let message, message.contains("open"), mode != "disarmed" {
                confirmOpen = (mode, message)
            } else {
                errorMessage = message ?? "Request failed (HTTP \(code))."
            }
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}
