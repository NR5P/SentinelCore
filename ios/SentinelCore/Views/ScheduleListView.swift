import SwiftUI

// Every schedule, with the two things worth knowing at a glance about each - when it next fires
// and how the last firing went - plus the controls that don't need the editor: switch it off,
// run it now, look at its history.
//
// The equivalent screen on the dashboard is the "at a time" pane of the Automations tab.
struct ScheduleListView: View {
    @EnvironmentObject private var model: AppModel

    @State private var schedules: [Schedule] = []
    @State private var metadata = ScheduleMetadata.empty
    @State private var isLoading = true
    @State private var loadError: String?
    @State private var editing: Schedule?
    @State private var creating = false
    @State private var runStatus: [UUID: String] = [:]
    @State private var expandedHistory: UUID?
    @State private var runs: [ScheduleRun] = []

    var body: some View {
        List {
            if let loadError {
                Section {
                    Text(loadError).foregroundStyle(SentinelTheme.red)
                }
            }

            // Worth saying once at the top rather than on every row: without coordinates, a
            // schedule that follows the sun can never work out when to fire.
            if !metadata.locationSet && schedules.contains(where: { $0.followsTheSun }) {
                Section {
                    Text("Some schedules here follow the sun, but no location is set. Add your town under Server Settings → LOCATION, or they will never run.")
                        .foregroundStyle(SentinelTheme.amber)
                }
            }

            if isLoading && schedules.isEmpty {
                Section { Text("Loading schedules…").foregroundStyle(.secondary) }
            } else if schedules.isEmpty {
                Section {
                    Text("No schedules yet. Add one to switch a device, publish MQTT, or call an API endpoint at a time of your choosing — or at sunset, or when it gets dark.")
                        .foregroundStyle(.secondary)
                }
            }

            // One row each, not a Section each. A Section-per-schedule drew them as separate
            // boxes, and on macOS AppKit measured those section rows once - at the height of
            // the "Loading schedules…" row they replaced - and never again, so every schedule
            // showed its name and nothing else: no timing, no next run, and no Run now / Edit /
            // Delete, because the buttons were drawn outside the row and could not be clicked.
            // Resizing the window forced the re-measure and made them appear.
            ForEach(schedules) { schedule in
                row(for: schedule)
            }
        }
        .navigationTitle("Schedules")
        .toolbar {
            if model.isAdmin {
                Button {
                    creating = true
                } label: {
                    Label("Add Schedule", systemImage: "plus")
                }
            }
        }
        .task { await load() }
        .refreshable { await load() }
        .sheet(isPresented: $creating) {
            editorSheet(for: nil)
        }
        .sheet(item: $editing) { schedule in
            editorSheet(for: schedule)
        }
    }

    @ViewBuilder
    private func editorSheet(for schedule: Schedule?) -> some View {
        let editor = NavigationStack {
            ScheduleEditView(schedule: schedule, metadata: metadata) {
                Task { await load() }
            }
        }

        // A Mac sheet collapses around a Form's ideal size without a minimum - see the note above
        // deviceHubSheetFrame(). The minimum has to stay off iPhone, where it is wider than the
        // screen and pushes the whole editor sideways.
        // 580 rather than the 520 this started with: a grouped Form puts the label and the
        // control on opposite ends of the row, and the widest of these rows needs the space.
        #if os(macOS)
        editor.frame(minWidth: 580, minHeight: 620)
        #else
        editor
        #endif
    }

    @ViewBuilder
    private func row(for schedule: Schedule) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text(schedule.name)
                    .font(.headline)
                    .foregroundStyle(schedule.enabled ? .primary : .secondary)
                Spacer()
                if !schedule.enabled {
                    Text("off")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }

            Text(ScheduleSummary.timing(schedule, metadata: metadata))
                .font(.caption)
                .foregroundStyle(.secondary)

            Text(ScheduleSummary.action(schedule, metadata: metadata))
                .font(.caption)
                .foregroundStyle(.secondary)

            Text(nextRunText(for: schedule))
                .font(.caption)
                .foregroundStyle(schedule.followsTheSun && !metadata.locationSet ? SentinelTheme.amber : .secondary)

            if !schedule.lastStatus.isEmpty {
                Text(lastRunText(for: schedule))
                    .font(.caption)
                    .foregroundStyle(schedule.lastStatus == "failed" ? SentinelTheme.red : .secondary)
            }

            if let status = runStatus[schedule.id] {
                Text(status)
                    .font(.caption)
                    .foregroundStyle(status.hasPrefix("failed") ? SentinelTheme.red : SentinelTheme.green)
            }

            if expandedHistory == schedule.id {
                historyView
            }

            if model.isAdmin {
                controls(for: schedule)
            }
        }
        .padding(.vertical, 2)
    }

    @ViewBuilder
    private var historyView: some View {
        if runs.isEmpty {
            Text("No runs recorded yet.")
                .font(.caption)
                .foregroundStyle(.secondary)
        } else {
            VStack(alignment: .leading, spacing: 2) {
                ForEach(runs) { run in
                    Text("\(run.firedAtUtc.formatted(date: .abbreviated, time: .shortened)) · \(run.status)\(run.detail.isEmpty ? "" : " · \(run.detail)")")
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
            }
            .padding(.vertical, 2)
        }
    }

    private func controls(for schedule: Schedule) -> some View {
        HStack(spacing: 12) {
            Button("Run now") { Task { await runNow(schedule) } }
            Button(schedule.enabled ? "Disable" : "Enable") {
                Task { await setEnabled(schedule, to: !schedule.enabled) }
            }
            Button(expandedHistory == schedule.id ? "Hide history" : "History") {
                Task { await toggleHistory(schedule) }
            }
            Button("Edit") { editing = schedule }
            Button("Delete", role: .destructive) { Task { await delete(schedule) } }
        }
        .buttonStyle(.borderless)
        .font(.caption)
    }

    private func nextRunText(for schedule: Schedule) -> String {
        if schedule.followsTheSun && !metadata.locationSet {
            return "No location set — this cannot run"
        }
        guard let next = schedule.nextDueUtc else {
            return schedule.enabled ? "No upcoming run" : "Disabled"
        }
        return "Next: \(next.formatted(date: .abbreviated, time: .shortened))"
    }

    private func lastRunText(for schedule: Schedule) -> String {
        let when = schedule.lastRunUtc.map { " \($0.formatted(date: .abbreviated, time: .shortened))" } ?? ""
        let detail = schedule.lastDetail.isEmpty ? "" : " · \(schedule.lastDetail)"
        return "Last: \(schedule.lastStatus)\(when)\(detail)"
    }

    private func load() async {
        isLoading = true
        defer { isLoading = false }

        do {
            // Metadata carries the device names the rows are described with, so it has to land
            // before the list renders rather than alongside it.
            metadata = try await model.client.scheduleMetadata()
            schedules = try await model.client.schedules()
            loadError = nil
        } catch {
            loadError = "Could not load schedules: \(error.localizedDescription)"
        }
    }

    private func runNow(_ schedule: Schedule) async {
        runStatus[schedule.id] = "running…"
        do {
            let result = try await model.client.runSchedule(id: schedule.id)
            runStatus[schedule.id] = result.fired
                ? "ok: \(result.detail ?? "")"
                : "failed: \(result.error ?? "unknown error")"
        } catch {
            runStatus[schedule.id] = "failed: \(error.localizedDescription)"
        }
        await load()
    }

    private func setEnabled(_ schedule: Schedule, to enabled: Bool) async {
        do {
            _ = try await model.client.setScheduleEnabled(id: schedule.id, enabled: enabled)
            await load()
        } catch {
            runStatus[schedule.id] = "failed: \(error.localizedDescription)"
        }
    }

    private func toggleHistory(_ schedule: Schedule) async {
        if expandedHistory == schedule.id {
            expandedHistory = nil
            return
        }

        expandedHistory = schedule.id
        runs = []
        do {
            runs = try await model.client.scheduleRuns(id: schedule.id)
        } catch {
            runs = []
        }
    }

    private func delete(_ schedule: Schedule) async {
        do {
            try await model.client.deleteSchedule(id: schedule.id)
            await load()
        } catch {
            runStatus[schedule.id] = "failed: \(error.localizedDescription)"
        }
    }
}

// Shared phrasing for a schedule's timing and action, so the list and the editor's summary line
// say the same thing.
enum ScheduleSummary {
    static let dayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"]

    static func timing(_ schedule: Schedule, metadata: ScheduleMetadata) -> String {
        let days = schedule.daysOfWeek.isEmpty
            ? "every day"
            : schedule.daysOfWeek.sorted().map { dayNames[$0] }.joined(separator: ", ")

        switch schedule.kind {
        case "daily":
            return "At \(schedule.timeOfDay), \(days)"
        case "interval":
            let window = schedule.windowStart.isEmpty || schedule.windowEnd.isEmpty
                ? ""
                : ", \(schedule.windowStart)–\(schedule.windowEnd)"
            return "Every \(interval(schedule.intervalSeconds))\(window)"
        case "cron":
            return "Cron: \(schedule.cronExpression)"
        case "once":
            guard let runAt = schedule.runAtUtc else { return "Once, at a time not yet set" }
            return "Once, \(runAt.formatted(date: .abbreviated, time: .shortened))"
        case "solar":
            return solar(schedule, metadata: metadata, days: days)
        default:
            return schedule.kind
        }
    }

    static func interval(_ seconds: Int) -> String {
        if seconds % 3600 == 0 { return "\(seconds / 3600)h" }
        if seconds % 60 == 0 { return "\(seconds / 60)m" }
        return "\(seconds)s"
    }

    static func solar(_ schedule: Schedule, metadata: ScheduleMetadata, days: String) -> String {
        let offset: String
        if schedule.solarOffsetMinutes == 0 {
            offset = "At"
        } else if schedule.solarOffsetMinutes > 0 {
            offset = "\(schedule.solarOffsetMinutes) min after"
        } else {
            offset = "\(abs(schedule.solarOffsetMinutes)) min before"
        }

        guard schedule.solarEvent == "dusk" || schedule.solarEvent == "dawn" else {
            return "\(offset) \(schedule.solarEvent), \(days)"
        }

        let level = metadata.darknessLevels
            .first { abs($0.elevationDegrees - schedule.solarElevationDegrees) < 0.01 }?
            .label.lowercased()
            ?? "the sun \(schedule.solarElevationDegrees)° below the horizon"
        let half = schedule.solarEvent == "dusk" ? "in the evening" : "in the morning"
        return "\(offset) \(level) \(half), \(days)"
    }

    static func action(_ schedule: Schedule, metadata: ScheduleMetadata) -> String {
        switch schedule.action.type {
        case "device":
            let name = metadata.devices.first { $0.id == schedule.action.deviceId }?.name
                ?? "(device no longer in the hub)"
            let brightness = schedule.action.deviceBrightness.map { " at \($0)%" } ?? ""
            return "\(schedule.action.deviceAction.isEmpty ? "set" : schedule.action.deviceAction) \(name)\(brightness)"
        case "mqtt":
            return "MQTT → \(schedule.action.mqttTopic.isEmpty ? "(no topic set)" : schedule.action.mqttTopic)"
        default:
            return "Webhook → \(schedule.action.webhookMethod) \(schedule.action.webhookUrl.isEmpty ? "(no URL set)" : schedule.action.webhookUrl)"
        }
    }
}
