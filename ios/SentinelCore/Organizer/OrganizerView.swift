import OrganizerKit
import SwiftUI

// The Organizer section: calendar, reminders, notes and boards over this device's copy (see
// OrganizerKit), with a filter for which space to show. Every screen here reads the engine
// directly, so an edit from another device appears as soon as it syncs in.
struct OrganizerView: View {
    enum Pane: String, CaseIterable, Identifiable {
        case calendar, reminders, notes, boards
        var id: Self { self }
        var title: String { rawValue.capitalized }
        var icon: String {
            switch self {
            case .calendar: "calendar"
            case .reminders: "checklist"
            case .notes: "note.text"
            case .boards: "rectangle.split.3x1"
            }
        }
    }

    @EnvironmentObject private var model: AppModel
    @Environment(OrganizerEngine.self) private var engine
    @AppStorage("organizerPane") private var pane: Pane = .calendar
    @AppStorage("organizerSpaceFilter") private var spaceFilter = "all"
    @State private var showingGroups = false
    @State private var showingSubscribe = false

    var body: some View {
        VStack(spacing: 0) {
            Picker("Show", selection: $pane) {
                ForEach(Pane.allCases) { pane in
                    Label(pane.title, systemImage: pane.icon).tag(pane)
                }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .padding(.horizontal)
            .padding(.vertical, 8)

            OrganizerSyncBanner()

            Group {
                switch pane {
                case .calendar: OrganizerCalendarView(spaceFilter: effectiveFilter)
                case .reminders: OrganizerRemindersView(spaceFilter: effectiveFilter)
                case .notes: OrganizerNotesView(spaceFilter: effectiveFilter)
                case .boards: OrganizerBoardsView(spaceFilter: effectiveFilter)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .navigationTitle("Organizer")
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Menu {
                    Picker("Show", selection: $spaceFilter) {
                        Label("Everything", systemImage: "square.stack.3d.up").tag("all")
                        ForEach(engine.visibleSpaces) { space in
                            Text(space.name).tag(space.id)
                        }
                    }
                    Divider()
                    Button("Groups…", systemImage: "person.3") { showingGroups = true }
                    Button("Subscribe in Another Calendar…", systemImage: "calendar.badge.plus") { showingSubscribe = true }
                    Button("Sync Now", systemImage: "arrow.triangle.2.circlepath") { Task { await engine.sync() } }
                } label: {
                    Label(filterLabel, systemImage: "line.3.horizontal.decrease.circle")
                }
            }
        }
        .sheet(isPresented: $showingGroups) {
            NavigationStack { OrganizerGroupsView() }
                .environment(engine)
                #if os(macOS)
                .frame(minWidth: 460, minHeight: 480)
                #endif
        }
        .sheet(isPresented: $showingSubscribe) {
            NavigationStack { OrganizerSubscribeView() }
                .environment(engine)
                .environmentObject(model)
                #if os(macOS)
                .frame(minWidth: 460, minHeight: 360)
                #endif
        }
    }

    // A filter naming a space this person can no longer see shows everything instead.
    private var effectiveFilter: String? {
        spaceFilter == "all" || engine.space(spaceFilter) == nil ? nil : spaceFilter
    }

    private var filterLabel: String {
        effectiveFilter.map { engine.spaceName($0) } ?? "Everything"
    }
}

// One line under the tabs, only when there's something to say: offline with edits waiting, or a
// sync problem. Quiet when everything is up to date.
struct OrganizerSyncBanner: View {
    @Environment(OrganizerEngine.self) private var engine

    var body: some View {
        let state = engine.syncState
        if let message = message(state) {
            HStack(spacing: 8) {
                Image(systemName: state.online ? "exclamationmark.triangle" : "icloud.slash")
                Text(message).font(.caption)
                Spacer()
            }
            .foregroundStyle(state.online ? SentinelTheme.red : SentinelTheme.amber)
            .padding(.horizontal)
            .padding(.vertical, 6)
            .background((state.online ? SentinelTheme.red : SentinelTheme.amber).opacity(0.1))
        }
    }

    private func message(_ state: OrganizerEngine.SyncState) -> String? {
        if state.needsSignIn { return "Sign in again to sync the Organizer." }
        if !state.online, engine.isConnected || state.waitingChanges > 0 {
            return state.waitingChanges > 0
                ? "Offline - \(state.waitingChanges) change\(state.waitingChanges == 1 ? "" : "s") saved on this device, will sync when the server is back"
                : "Offline - showing this device's copy"
        }
        if let error = state.lastError { return error }
        return nil
    }
}

// MARK: - calendar

struct OrganizerCalendarView: View {
    @Environment(OrganizerEngine.self) private var engine
    let spaceFilter: String?

    @State private var month = Calendar.current.date(from: Calendar.current.dateComponents([.year, .month], from: Date()))!
    @State private var selectedDay = Calendar.current.startOfDay(for: Date())
    @State private var editing: EventEditorTarget?
    @State private var editingReminder: OrganizerItem?
    @State private var viewingHoliday: Holiday?
    @State private var showingHolidaySettings = false
    @State private var archiveLoaded = false

    private var calendar: Calendar { .current }

    var body: some View {
        let gridStart = calendar.date(byAdding: .day, value: -(calendar.component(.weekday, from: month) - 1), to: month)!
        let gridEnd = calendar.date(byAdding: .day, value: 42, to: gridStart)!
        let entries = CalendarEntry.between(gridStart, gridEnd, engine: engine, spaceFilter: spaceFilter)
        let byDay = Dictionary(grouping: entries.flatMap { entry in entry.days(calendar).map { ($0, entry) } }, by: \.0)
            .mapValues { $0.map(\.1) }

        ScrollView {
            VStack(spacing: 12) {
                header
                // Plain rows, not a LazyVGrid: inside a ScrollView the lazy grid sized its rows
                // from an estimate, hid the first week and let the day list below draw over the
                // grid (seen on the iPhone simulator).
                VStack(spacing: 0) {
                    HStack(spacing: 0) {
                        ForEach(Array(calendar.veryShortWeekdaySymbols.enumerated()), id: \.offset) { _, symbol in
                            Text(symbol).font(.caption2).foregroundStyle(.secondary).frame(maxWidth: .infinity).padding(.vertical, 4)
                        }
                    }
                    ForEach(0..<6, id: \.self) { week in
                        HStack(spacing: 0) {
                            ForEach(0..<7, id: \.self) { weekday in
                                let day = calendar.date(byAdding: .day, value: week * 7 + weekday, to: gridStart)!
                                dayCell(day, entries: byDay[day] ?? [])
                            }
                        }
                    }
                }
                .clipShape(RoundedRectangle(cornerRadius: 8))
                .overlay { RoundedRectangle(cornerRadius: 8).stroke(.quaternary) }

                dayDetail(entries: (byDay[selectedDay] ?? []).sorted { $0.start < $1.start })
            }
            .padding()
        }
        .sheet(item: $editing) { target in
            NavigationStack { EventEditor(target: target) }
                .environment(engine)
                #if os(macOS)
                .frame(minWidth: 520, minHeight: 620)
                #endif
        }
        .sheet(item: $editingReminder) { reminder in
            NavigationStack { ReminderEditor(reminder: reminder, parentId: nil) }
                .environment(engine)
                #if os(macOS)
                .frame(minWidth: 480, minHeight: 560)
                #endif
        }
        .sheet(item: $viewingHoliday) { holiday in
            NavigationStack { HolidayDetail(holiday: holiday, showSettings: {
                viewingHoliday = nil
                showingHolidaySettings = true
            }) }
                .environment(engine)
                #if os(macOS)
                .frame(minWidth: 380, minHeight: 260)
                #endif
        }
        .sheet(isPresented: $showingHolidaySettings) {
            NavigationStack { HolidaySettingsView() }
                .environment(engine)
                #if os(macOS)
                .frame(minWidth: 460, minHeight: 560)
                #endif
        }
        .task(id: month) { await loadArchiveIfNeeded() }
    }

    private var header: some View {
        HStack {
            Button { shiftMonth(-1) } label: { Image(systemName: "chevron.left") }
            Text(month.formatted(.dateTime.month(.wide).year()))
                .font(.headline)
                .lineLimit(1)
                .minimumScaleFactor(0.8)
            Button { shiftMonth(1) } label: { Image(systemName: "chevron.right") }
            Button("Today") {
                month = calendar.date(from: calendar.dateComponents([.year, .month], from: Date()))!
                selectedDay = calendar.startOfDay(for: Date())
            }
            .buttonStyle(.bordered)
            Spacer()
            Button {
                showingHolidaySettings = true
            } label: {
                Label("Holidays", systemImage: engine.holidaySets.isEmpty ? "star" : "star.fill")
                    .labelStyle(.iconOnly)
            }
            .accessibilityLabel("Holiday calendars")
            .help("Holiday calendars")
            Button {
                editing = .new(day: selectedDay)
            } label: {
                Label("New Event", systemImage: "plus")
                    .labelStyle(.iconOnly)
            }
            .buttonStyle(.sentinelProminent)
            .accessibilityLabel("New event")
        }
        .buttonStyle(.borderless)
    }

    private func dayCell(_ day: Date, entries: [CalendarEntry]) -> some View {
        let inMonth = calendar.isDate(day, equalTo: month, toGranularity: .month)
        let isToday = calendar.isDateInToday(day)
        let isSelected = calendar.isDate(day, inSameDayAs: selectedDay)
        return VStack(alignment: .leading, spacing: 2) {
            Text("\(calendar.component(.day, from: day))")
                .font(.caption.weight(isToday ? .bold : .regular))
                .foregroundStyle(isToday ? Color.black : (inMonth ? .primary : .secondary))
                .padding(.horizontal, 5)
                .background { if isToday { Capsule().fill(SentinelTheme.green) } }
            ForEach(entries.prefix(3)) { entry in
                Text(entry.title)
                    .font(.system(size: 10))
                    .lineLimit(1)
                    .padding(.horizontal, 3)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .italic(entry.isHoliday)
                    .background(entry.color(engine).opacity(0.18), in: RoundedRectangle(cornerRadius: 3))
            }
            if entries.count > 3 {
                Text("+\(entries.count - 3)").font(.system(size: 9)).foregroundStyle(.secondary)
            }
            Spacer(minLength: 0)
        }
        .padding(3)
        .frame(maxWidth: .infinity, minHeight: 68, alignment: .topLeading)
        .background(isSelected ? SentinelTheme.green.opacity(0.12) : (inMonth ? Color.clear : Color.primary.opacity(0.03)))
        .overlay { Rectangle().stroke(.quaternary, lineWidth: 0.5) }
        .contentShape(Rectangle())
        .onTapGesture(count: 2) { editing = .new(day: day) }
        .onTapGesture { selectedDay = day }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(day.formatted(date: .complete, time: .omitted)), \(entries.count) item\(entries.count == 1 ? "" : "s")")
        .accessibilityAddTraits(.isButton)
    }

    private func dayDetail(entries: [CalendarEntry]) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text(selectedDay.formatted(date: .complete, time: .omitted)).font(.headline).foregroundStyle(SentinelTheme.cyan)
                Spacer()
                Button("Add Event", systemImage: "plus") { editing = .new(day: selectedDay) }
                    .buttonStyle(.borderless)
            }
            if entries.isEmpty {
                Text("Nothing on this day").foregroundStyle(.secondary).font(.callout)
            }
            ForEach(entries) { entry in
                Button {
                    switch entry.kind {
                    case let .event(occurrence): editing = .existing(occurrence)
                    case let .reminder(reminder): editingReminder = reminder
                    case let .holiday(holiday): viewingHoliday = holiday
                    }
                } label: {
                    HStack(alignment: .top, spacing: 10) {
                        RoundedRectangle(cornerRadius: 2).fill(entry.color(engine)).frame(width: 4)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(entry.title).font(.body)
                            Text(entry.timeText).font(.caption).foregroundStyle(.secondary)
                            if let location = entry.location, !location.isEmpty {
                                Label(location, systemImage: "mappin").font(.caption).foregroundStyle(.secondary)
                            }
                        }
                        Spacer()
                        if case let .holiday(holiday) = entry.kind {
                            HolidayTag(setId: holiday.setId)
                        } else if let spaceId = entry.spaceId {
                            SpaceTag(spaceId: spaceId)
                        }
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                Divider()
            }
        }
        .sentinelCard()
    }

    private func shiftMonth(_ by: Int) {
        month = calendar.date(byAdding: .month, value: by, to: month) ?? month
    }

    // A light copy drops events older than its window; looking back past it fetches the rest
    // from the server for this session.
    private func loadArchiveIfNeeded() async {
        guard !archiveLoaded, engine.policy.mode != .full,
              let window = engine.policy.windowDays,
              let cutoff = calendar.date(byAdding: .day, value: -window, to: Date()),
              month < cutoff else { return }
        archiveLoaded = (try? await engine.loadArchive(.event)) != nil
    }
}

// An event occurrence, a dated reminder or a holiday, as the calendar shows it.
struct CalendarEntry: Identifiable {
    enum Kind { case event(Occurrence), reminder(OrganizerItem), holiday(Holiday) }
    let kind: Kind
    let start: Date
    let end: Date

    var id: String {
        switch kind {
        case let .event(occurrence): "e:\(occurrence.item.id):\(occurrence.start.timeIntervalSince1970)"
        case let .reminder(reminder): "r:\(reminder.id)"
        case let .holiday(holiday): holiday.id
        }
    }

    // The Organizer item behind it; holidays aren't items.
    var item: OrganizerItem? {
        switch kind {
        case let .event(occurrence): occurrence.item
        case let .reminder(reminder): reminder
        case .holiday: nil
        }
    }

    var isHoliday: Bool {
        if case .holiday = kind { return true }
        return false
    }

    var title: String {
        switch kind {
        case let .event(occurrence): occurrence.item.title.isEmpty ? "Untitled" : occurrence.item.title
        case let .reminder(reminder): "☐ " + (reminder.title.isEmpty ? "Reminder" : reminder.title)
        case let .holiday(holiday): holiday.name
        }
    }

    var spaceId: String? { item?.spaceId }
    var location: String? { item?.string("location") }
    var allDay: Bool { isHoliday || item?.bool("allDay") == true }

    var timeText: String {
        if case let .holiday(holiday) = kind { return holiday.note ?? (holiday.dayCount > 1 ? "\(holiday.dayCount) days" : "All day") }
        if allDay { return "All day" }
        if case .reminder = kind { return "Due \(start.formatted(date: .omitted, time: .shortened))" }
        return "\(start.formatted(date: .omitted, time: .shortened)) – \(end.formatted(date: .omitted, time: .shortened))"
    }

    @MainActor
    func color(_ engine: OrganizerEngine) -> Color {
        switch kind {
        case .reminder: return SentinelTheme.amber
        case let .holiday(holiday): return Color(hex: HolidayColors.hex(holiday.setId)) ?? .secondary
        case let .event(occurrence):
            if let hex = occurrence.item.string("color"), let color = Color(hex: hex) { return color }
            return engine.color(forSpace: occurrence.item.spaceId)
        }
    }

    // Every day this entry covers, as start-of-day dates.
    func days(_ calendar: Calendar) -> [Date] {
        var day = calendar.startOfDay(for: start)
        let last = end > start ? end.addingTimeInterval(-1) : start
        var out: [Date] = []
        while day <= last && out.count < 60 {
            out.append(day)
            day = calendar.date(byAdding: .day, value: 1, to: day)!
        }
        return out.isEmpty ? [calendar.startOfDay(for: start)] : out
    }

    @MainActor
    static func between(_ from: Date, _ to: Date, engine: OrganizerEngine, spaceFilter: String?) -> [CalendarEntry] {
        var entries: [CalendarEntry] = []
        // Holidays first on each day, and whatever space is picked - they belong to none.
        for (holiday, start, end) in engine.holidays(from: from, to: to) {
            entries.append(CalendarEntry(kind: .holiday(holiday), start: start, end: end))
        }
        for event in engine.items(.event) where spaceFilter == nil || event.spaceId == spaceFilter {
            for occurrence in Recurrence.occurrences(of: event, from: from, to: to) {
                entries.append(CalendarEntry(kind: .event(occurrence), start: occurrence.start, end: occurrence.end))
            }
        }
        for reminder in engine.items(.reminder) where reminder.parentId == nil && !reminder.bool("completed")
            && (spaceFilter == nil || reminder.spaceId == spaceFilter) {
            guard let due = OrganizerDates.parse(reminder.string("due"), allDay: reminder.bool("allDay")), due >= from, due < to else { continue }
            entries.append(CalendarEntry(kind: .reminder(reminder), start: due, end: due))
        }
        return entries.sorted { ($0.start, $0.isHoliday ? 0 : 1) < ($1.start, $1.isHoliday ? 0 : 1) }
    }
}

// MARK: - holiday calendars

struct HolidayTag: View {
    @Environment(OrganizerEngine.self) private var engine
    let setId: String

    var body: some View {
        HStack(spacing: 4) {
            Circle().fill(Color(hex: HolidayColors.hex(setId)) ?? .secondary).frame(width: 7, height: 7)
            Text(engine.holidaySetName(setId))
        }
        .font(.caption2)
        .foregroundStyle(.secondary)
    }
}

// Which holiday calendars to show. All off until someone turns one on; the choice follows the
// account, so it applies on every device.
struct HolidaySettingsView: View {
    @Environment(OrganizerEngine.self) private var engine
    @Environment(\.dismiss) private var dismiss
    @State private var loading = false

    var body: some View {
        Form {
            Section {
                if let catalog = engine.holidayState.catalog {
                    ForEach(catalog) { set in
                        Toggle(isOn: binding(for: set.id)) {
                            HStack(alignment: .top, spacing: 10) {
                                Circle().fill(Color(hex: HolidayColors.hex(set.id)) ?? .secondary)
                                    .frame(width: 10, height: 10)
                                    .padding(.top, 5)
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(set.name)
                                    Text(set.description).font(.caption).foregroundStyle(.secondary)
                                }
                            }
                        }
                    }
                } else if loading {
                    ProgressView()
                } else {
                    Text("Connect to the server once to see the holiday calendars.").foregroundStyle(.secondary)
                }
            } footer: {
                Text("Holidays show on the calendar here and on your other devices, and in the calendar subscription link. They can't be edited.")
            }
        }
        .formStyle(.grouped)
        .navigationTitle("Holidays")
        .toolbar {
            ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } }
        }
        .task {
            guard engine.holidayState.catalog == nil else { return }
            loading = true
            await engine.refreshHolidays(force: true)
            loading = false
        }
    }

    private func binding(for id: String) -> Binding<Bool> {
        Binding(
            get: { engine.holidaySets.contains(id) },
            set: { on in
                var sets = engine.holidaySets.filter { $0 != id }
                if on { sets.append(id) }
                engine.setHolidaySets(sets)
            })
    }
}

struct HolidayDetail: View {
    @Environment(OrganizerEngine.self) private var engine
    @Environment(\.dismiss) private var dismiss
    let holiday: Holiday
    let showSettings: () -> Void

    var body: some View {
        Form {
            Section {
                LabeledContent("Date", value: dates)
                if let note = holiday.note { Text(note) }
                HolidayTag(setId: holiday.setId)
            } footer: {
                Text("Holidays can't be edited. Turn the whole calendar off in holiday settings if you don't want it.")
            }
            Button("Holiday Settings", action: showSettings)
        }
        .formStyle(.grouped)
        .navigationTitle(holiday.name)
        .toolbar {
            ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } }
        }
    }

    private var dates: String {
        guard let span = holiday.span(in: .current) else { return holiday.date }
        let first = span.start.formatted(date: .abbreviated, time: .omitted)
        guard holiday.dayCount > 1, let last = Calendar.current.date(byAdding: .day, value: -1, to: span.end) else { return first }
        return "\(first) – \(last.formatted(date: .abbreviated, time: .omitted))"
    }
}
