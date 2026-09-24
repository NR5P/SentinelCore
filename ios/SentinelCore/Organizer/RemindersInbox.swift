@preconcurrency import EventKit
import Foundation
import Observation
import OrganizerKit

// Apple Reminders as an inbox for the Organizer. The person picks one Reminders list - ideally
// their default list, so plain "Hey Siri, remind me…" lands in it - and a space. Whatever appears
// in that list is brought into the space and taken out of Apple Reminders. One way only: it's a
// way in, not a mirror.
//
// It runs when the app opens, whenever Reminders changes while the app is running (on a Mac left
// open, within seconds of iCloud delivering it), and in the iPhone's background refresh. Until
// then the reminder sits in Apple Reminders, which still alerts for it, so nothing is missed.
//
// A per-device setting. With iCloud the same list reaches the iPhone and the Mac; if both have it
// on, each Apple reminder becomes the same Organizer item on both (AppleReminderImport.itemId).
@MainActor
@Observable
final class RemindersInbox {
    static let shared = RemindersInbox()

    enum Access { case notAsked, granted, denied }

    struct ListChoice: Identifiable, Hashable {
        let id: String
        let title: String
        let account: String
    }

    private(set) var enabled: Bool {
        didSet { UserDefaults.standard.set(enabled, forKey: "remindersInboxEnabled") }
    }
    var listId: String? {
        didSet {
            UserDefaults.standard.set(listId, forKey: "remindersInboxListId")
            if listId != oldValue { Task { await bringIn() } }
        }
    }
    // Where brought-in reminders go. Chosen when it's switched on - this person's own space - and
    // only ever changed by them.
    var spaceId: String? {
        didSet { UserDefaults.standard.set(spaceId, forKey: "remindersInboxSpaceId") }
    }
    private(set) var access: Access = .notAsked
    private(set) var lists: [ListChoice] = []
    private(set) var lastRun: Date?
    // The last pass that actually brought something in - most passes find nothing, and "0 brought
    // in" right after it moved three reads as though it hadn't worked.
    private(set) var lastBrought: (count: Int, at: Date)?
    // "Milk - alerts by location": what was left in Apple Reminders last time, and why.
    private(set) var leftBehind: [String] = []
    private(set) var problem: String?

    private let store = EKEventStore()
    private var engine: OrganizerEngine?
    private var organizerIsOn: () -> Bool = { false }
    private var log: (String) -> Void = { _ in }
    private var observer: NSObjectProtocol?
    private var debounce: Task<Void, Never>?
    private var running = false
    private var runAgain = false

    static let listName = "Sentinel Core"

    private init() {
        enabled = UserDefaults.standard.bool(forKey: "remindersInboxEnabled")
        listId = UserDefaults.standard.string(forKey: "remindersInboxListId")
        spaceId = UserDefaults.standard.string(forKey: "remindersInboxSpaceId")
        access = Self.currentAccess()
    }

    // From AppModel at launch.
    func attach(_ engine: OrganizerEngine, organizerIsOn: @escaping () -> Bool, log: @escaping (String) -> Void) {
        self.engine = engine
        self.organizerIsOn = organizerIsOn
        self.log = log
        guard enabled, access == .granted else { return }
        startWatching()
        loadLists()
        Task { await bringIn() }
    }

    func setEnabled(_ on: Bool) async {
        problem = nil
        guard on else {
            enabled = false
            stopWatching()
            return
        }
        if access == .notAsked {
            _ = try? await store.requestFullAccessToReminders()
            access = Self.currentAccess()
        }
        enabled = true
        guard access == .granted else { return }
        loadLists()
        if listId == nil || !lists.contains(where: { $0.id == listId }) {
            listId = store.defaultCalendarForNewReminders()?.calendarIdentifier ?? lists.first?.id
        }
        if spaceId == nil, let engine, engine.knowsSpaces { spaceId = engine.defaultSpaceId }
        startWatching()
        await bringIn()
    }

    // A list of its own, so turning this on never empties a list someone was using for something
    // else. Made in the same account as the default list - iCloud, normally - so the Mac sees it too.
    func createList() {
        let calendar = EKCalendar(for: .reminder, eventStore: store)
        calendar.title = Self.listName
        guard let source = store.defaultCalendarForNewReminders()?.source
                ?? store.sources.first(where: { $0.sourceType == .calDAV || $0.sourceType == .local }) else {
            problem = "There's no Reminders account on this device to add a list to."
            return
        }
        calendar.source = source
        do {
            try store.saveCalendar(calendar, commit: true)
            loadLists()
            listId = calendar.calendarIdentifier
        } catch {
            problem = "Couldn't create the list: \(error.localizedDescription)"
        }
    }

    // Runs one pass now. A call while one is running makes it go round again afterwards, so a
    // reminder added mid-pass isn't left until next time.
    func bringIn() async {
        guard enabled else { return }
        if running {
            runAgain = true
            return
        }
        running = true
        defer { running = false }
        repeat {
            runAgain = false
            await onePass()
        } while runAgain
    }

    private func onePass() async {
        access = Self.currentAccess()
        guard access == .granted, let engine else { return }
        // Nothing moves out of Apple Reminders unless there's somewhere private and known to put it.
        guard organizerIsOn() else {
            problem = "The Organizer is turned off on the server, so nothing is being brought in."
            return
        }
        guard engine.knowsSpaces else {
            problem = "Waiting for the first sync with the server."
            return
        }
        guard let spaceId, engine.spaces.contains(where: { $0.id == spaceId }) else {
            problem = "Choose where brought-in reminders should go."
            return
        }
        guard let listId, let list = store.calendar(withIdentifier: listId) else {
            problem = "Choose which Reminders list to bring in."
            return
        }
        problem = nil

        let found = await incompleteReminders(in: list)
        var brought = 0
        var left: [String] = []
        for candidate in found {
            switch AppleReminderImport.outcome(for: candidate.reminder) {
            case let .bring(id, fields):
                // Already here - brought in by this device before Apple's removal reached
                // iCloud, or by the other device - is fine: just finish taking it out of Apple.
                if !engine.knows(id) {
                    engine.create(.reminder, fields: fields, spaceId: spaceId, id: id)
                    brought += 1
                }
                if let reminder = store.calendarItem(withIdentifier: candidate.itemIdentifier) as? EKReminder {
                    do {
                        try store.remove(reminder, commit: false)
                    } catch {
                        log("Couldn't remove \"\(candidate.reminder.title)\" from Apple Reminders: \(error.localizedDescription)")
                    }
                }
            case let .leave(reason):
                left.append("\(candidate.reminder.title) - \(reason)")
            }
        }
        do {
            try store.commit()
        } catch {
            problem = "Brought reminders in, but couldn't take them out of Apple Reminders: \(error.localizedDescription)"
            store.reset()
        }
        if brought > 0 {
            // Its alerts, now: local creates otherwise wait for the next sync to be planned.
            engine.refreshAlerts()
            log("Brought in \(brought) reminder\(brought == 1 ? "" : "s") from Apple Reminders")
        }
        lastRun = Date()
        if brought > 0 { lastBrought = (brought, Date()) }
        leftBehind = left
    }

    private struct Candidate: Sendable {
        let itemIdentifier: String
        let reminder: AppleReminder
    }

    private func incompleteReminders(in list: EKCalendar) async -> [Candidate] {
        let predicate = store.predicateForIncompleteReminders(withDueDateStarting: nil, ending: nil, calendars: [list])
        return await withCheckedContinuation { continuation in
            // EventKit calls back on its own queue. @Sendable keeps Swift from treating this as
            // main-actor code, which would trap there; the work it does is nonisolated.
            store.fetchReminders(matching: predicate) { @Sendable reminders in
                continuation.resume(returning: (reminders ?? []).map(Self.candidate))
            }
        }
    }

    private nonisolated static func candidate(_ reminder: EKReminder) -> Candidate {
        let alarms = reminder.alarms ?? []
        let rules = reminder.recurrenceRules ?? []
        let rule = rules.count == 1 ? recurrence(rules[0], due: reminder.dueDateComponents) : nil
        return Candidate(
            itemIdentifier: reminder.calendarItemIdentifier,
            reminder: AppleReminder(
                externalId: reminder.calendarItemExternalIdentifier ?? reminder.calendarItemIdentifier,
                title: reminder.title ?? "",
                notes: reminder.notes,
                url: reminder.url,
                due: reminder.dueDateComponents,
                priority: reminder.priority,
                relativeAlarms: alarms.filter { $0.absoluteDate == nil && $0.structuredLocation == nil }.map(\.relativeOffset),
                absoluteAlarms: alarms.compactMap(\.absoluteDate),
                hasLocationAlarm: alarms.contains { $0.structuredLocation != nil || $0.proximity != .none },
                recurrence: rule,
                hasUnsupportedRecurrence: rules.count > 1 || (rules.count == 1 && rule == nil)))
    }

    // Apple's repeat as the Organizer's, or nil when it can't say it: "the second Tuesday", a
    // chosen week of the year, and the like.
    private nonisolated static func recurrence(_ rule: EKRecurrenceRule, due: DateComponents?) -> RecurrenceRule? {
        if !(rule.setPositions ?? []).isEmpty || !(rule.weeksOfTheYear ?? []).isEmpty || !(rule.daysOfTheYear ?? []).isEmpty { return nil }
        let days = rule.daysOfTheWeek ?? []
        if days.contains(where: { $0.weekNumber != 0 }) { return nil }
        // Apple fills in the month for "every year"; it's only a problem when it isn't the due month.
        if let months = rule.monthsOfTheYear, !months.isEmpty,
           rule.frequency != .yearly || months.map(\.intValue) != [due?.month].compactMap({ $0 }) { return nil }
        let frequency: RecurrenceRule.Frequency = switch rule.frequency {
        case .daily: .daily
        case .weekly: .weekly
        case .monthly: .monthly
        case .yearly: .yearly
        @unknown default: .daily
        }
        let count = rule.recurrenceEnd.map(\.occurrenceCount).flatMap { $0 > 0 ? $0 : nil }
        return RecurrenceRule(
            frequency: frequency, interval: rule.interval, count: count, until: rule.recurrenceEnd?.endDate,
            byWeekday: days.isEmpty ? nil : days.map { $0.dayOfTheWeek.rawValue - 1 },
            byMonthDay: rule.daysOfTheMonth.flatMap { $0.isEmpty ? nil : $0.map(\.intValue) })
    }

    func loadLists() {
        lists = store.calendars(for: .reminder)
            .map { ListChoice(id: $0.calendarIdentifier, title: $0.title, account: $0.source.title) }
            .sorted { $0.title.localizedStandardCompare($1.title) == .orderedAscending }
    }

    private func startWatching() {
        guard observer == nil else { return }
        observer = NotificationCenter.default.addObserver(forName: .EKEventStoreChanged, object: store, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                // Changes arrive in bursts (iCloud, and our own removals); wait for the end of one.
                self.debounce?.cancel()
                self.debounce = Task {
                    try? await Task.sleep(for: .seconds(2))
                    guard !Task.isCancelled else { return }
                    self.loadLists()
                    await self.bringIn()
                }
            }
        }
    }

    private func stopWatching() {
        if let observer { NotificationCenter.default.removeObserver(observer) }
        observer = nil
        debounce?.cancel()
    }

    private static func currentAccess() -> Access {
        switch EKEventStore.authorizationStatus(for: .reminder) {
        case .fullAccess: .granted
        case .notDetermined: .notAsked
        default: .denied
        }
    }
}
