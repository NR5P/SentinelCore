import AppIntents
import Foundation
import OrganizerKit

// The Organizer from Siri, Shortcuts, Spotlight and the Action button: "Hey Siri, add a reminder
// in Sentinel Core". Apple doesn't let an app take over plain "remind me to…" - that stays with
// Apple Reminders - so every phrase names the app.
//
// Siri runs these inside the app, launching it in the background if it isn't open. The item is
// saved to this device's copy first, so it's never lost, and then sent if the server can be
// reached in the few seconds Siri waits. Otherwise it goes the next time the app connects.

struct AddReminderIntent: AppIntent {
    static let title: LocalizedStringResource = "Add Reminder"
    static let description = IntentDescription(
        "Adds a reminder to the Sentinel Core Organizer. Say when in the same breath, like \"call mom tomorrow at 5\", and it gets a due date and an alert.")
    static let openAppWhenRun = false

    @Parameter(title: "Reminder", requestValueDialog: "What's the reminder?")
    var text: String

    static var parameterSummary: some ParameterSummary {
        Summary("Add reminder \(\.$text)")
    }

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog {
        let model = try await VoiceOrganizer.ready()
        let phrase = VoicePhrase.parse(text)
        var fields: [String: JSONValue] = ["title": .string(phrase.title), "completed": false]
        if let due = phrase.dueField() {
            fields["due"] = .string(due)
            fields["allDay"] = .bool(!phrase.hasTime)
        }
        model.organizer.create(.reminder, fields: fields)
        let sent = await VoiceOrganizer.send(model)
        let when = phrase.due.map { " for " + VoiceOrganizer.spoken($0, hasTime: phrase.hasTime) } ?? ""
        return .result(dialog: "\(VoiceOrganizer.confirmation("Added \u{201C}\(phrase.title)\u{201D}\(when).", sent: sent))")
    }
}

struct AddNoteIntent: AppIntent {
    static let title: LocalizedStringResource = "Add Note"
    static let description = IntentDescription("Takes a note in the Sentinel Core Organizer.")
    static let openAppWhenRun = false

    @Parameter(title: "Note", requestValueDialog: "What should the note say?")
    var text: String

    static var parameterSummary: some ParameterSummary {
        Summary("Add note \(\.$text)")
    }

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog {
        let model = try await VoiceOrganizer.ready()
        let spoken = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let title = NoteText.title(for: spoken)
        model.organizer.create(.note, fields: ["title": .string(title), "body": .string(title == spoken ? "" : spoken)])
        let sent = await VoiceOrganizer.send(model)
        return .result(dialog: "\(VoiceOrganizer.confirmation("Saved your note.", sent: sent))")
    }
}

struct AddEventIntent: AppIntent {
    static let title: LocalizedStringResource = "Add Calendar Event"
    static let description = IntentDescription(
        "Adds an event to the Sentinel Core calendar, like \"dentist Friday at 3\". Siri asks when if you don't say.")
    static let openAppWhenRun = false

    @Parameter(title: "Event", requestValueDialog: "What's the event?")
    var text: String

    @Parameter(title: "Starts", requestValueDialog: "When does it start?")
    var start: Date?

    static var parameterSummary: some ParameterSummary {
        Summary("Add event \(\.$text) at \(\.$start)")
    }

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog {
        let model = try await VoiceOrganizer.ready()
        let phrase = VoicePhrase.parse(text)
        let calendar = Calendar.current
        let begins: Date
        let allDay: Bool
        if let start {
            begins = start
            allDay = false
        } else if let due = phrase.due {
            begins = due
            allDay = !phrase.hasTime
        } else {
            throw $start.needsValueError("When is it?")
        }
        // An hour long, or the one day - the same defaults as the event editor. All-day ends are
        // exclusive on the wire.
        let ends = allDay ? calendar.date(byAdding: .day, value: 1, to: calendar.startOfDay(for: begins))! : begins.addingTimeInterval(3600)
        model.organizer.create(.event, fields: [
            "title": .string(phrase.title),
            "allDay": .bool(allDay),
            "start": .string(OrganizerDates.fieldValue(allDay ? calendar.startOfDay(for: begins) : begins, allDay: allDay)),
            "end": .string(OrganizerDates.fieldValue(ends, allDay: allDay)),
            "timeZone": .string(TimeZone.current.identifier),
        ])
        let sent = await VoiceOrganizer.send(model)
        let when = VoiceOrganizer.spoken(begins, hasTime: !allDay)
        return .result(dialog: "\(VoiceOrganizer.confirmation("Added \u{201C}\(phrase.title)\u{201D} \(when).", sent: sent))")
    }
}

struct TodaysRemindersIntent: AppIntent {
    static let title: LocalizedStringResource = "Today's Reminders"
    static let description = IntentDescription("Reads out the Sentinel Core reminders due today, and any that are overdue.")
    static let openAppWhenRun = false

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog {
        let model = try await VoiceOrganizer.ready()
        // Catch what other devices added since this one last synced, if the server answers in time.
        _ = await VoiceOrganizer.send(model)
        let calendar = Calendar.current
        guard let tomorrow = calendar.date(byAdding: .day, value: 1, to: calendar.startOfDay(for: Date())) else {
            return .result(dialog: "Nothing's due today.")
        }
        let due = model.organizer.items(.reminder)
            .filter { $0.parentId == nil && !$0.bool("completed") }
            .compactMap { item -> (item: OrganizerItem, date: Date)? in
                OrganizerDates.parse(item.string("due"), allDay: item.bool("allDay")).map { (item, $0) }
            }
            .filter { $0.date < tomorrow }
            .sorted { $0.date < $1.date }
        guard !due.isEmpty else { return .result(dialog: "Nothing's due today.") }
        let today = calendar.startOfDay(for: Date())
        let overdue = due.filter { $0.item.bool("allDay") ? $0.date < today : $0.date < Date() }.count
        let names = due.prefix(6).map { entry in
            let title = entry.item.title.isEmpty ? "a reminder" : entry.item.title
            guard !entry.item.bool("allDay"), entry.date >= today else { return title }
            return "\(title) at \(entry.date.formatted(date: .omitted, time: .shortened))"
        }
        var answer = due.count == 1 ? "You have one reminder: " : "You have \(due.count) reminders: "
        answer += names.formatted(.list(type: .and))
        if due.count > names.count { answer += ", and more" }
        answer += "."
        if overdue > 0 { answer += overdue == 1 ? " One is overdue." : " \(overdue) are overdue." }
        return .result(dialog: "\(answer)")
    }
}

struct SentinelShortcuts: AppShortcutsProvider {
    // Every phrase must name the app; Siri matches "Sentinel Core", the display name.
    static var appShortcuts: [AppShortcut] {
        AppShortcut(
            intent: AddReminderIntent(),
            phrases: [
                "Add a reminder in \(.applicationName)",
                "Add a \(.applicationName) reminder",
                "New reminder in \(.applicationName)",
                "Remind me in \(.applicationName)",
                "\(.applicationName) remind me",
            ],
            shortTitle: "Add Reminder",
            systemImageName: "checklist")
        AppShortcut(
            intent: AddNoteIntent(),
            phrases: [
                "Take a note in \(.applicationName)",
                "Add a note in \(.applicationName)",
                "Add a \(.applicationName) note",
                "New note in \(.applicationName)",
            ],
            shortTitle: "Add Note",
            systemImageName: "note.text")
        AppShortcut(
            intent: AddEventIntent(),
            phrases: [
                "Add an event in \(.applicationName)",
                "Add an event to \(.applicationName)",
                "Add a \(.applicationName) event",
                "New event in \(.applicationName)",
            ],
            shortTitle: "Add Event",
            systemImageName: "calendar.badge.plus")
        AppShortcut(
            intent: TodaysRemindersIntent(),
            phrases: [
                "What's due in \(.applicationName)",
                "What are my \(.applicationName) reminders",
                "Read my \(.applicationName) reminders",
                "What's on my \(.applicationName) list",
            ],
            shortTitle: "Today's Reminders",
            systemImageName: "checklist.checked")
    }
}

enum VoiceOrganizerError: Error, CustomLocalizedStringResourceConvertible {
    case notSetUp
    case turnedOff
    case notSynced

    var localizedStringResource: LocalizedStringResource {
        switch self {
        case .notSetUp: "Sentinel Core isn't connected to a server yet. Open the app to set it up."
        case .turnedOff: "The Organizer is turned off. It's in the dashboard's Settings, under Organizer."
        case .notSynced: "Sentinel Core hasn't synced your Organizer yet. Open the app while it can reach your server, then try again."
        }
    }
}

@MainActor
enum VoiceOrganizer {
    // Seconds Siri is given for each network step. Siri gives up on an intent after about ten.
    static let connectWait: Double = 5
    static let sendWait: Double = 3

    // The model, connected if the server answers quickly. Being offline is fine, since the item
    // waits on this device, but a server that has the Organizer switched off is not.
    static func ready() async throws -> AppModel {
        let model = AppModel.shared
        guard model.isConfigured else { throw VoiceOrganizerError.notSetUp }
        if !model.organizer.isConnected {
            _ = await finishes(within: connectWait) {
                await model.connect()
                // connect() returns at once when a connection is already under way - the app
                // opening at the same moment Siri asked - so wait for that one to land.
                var polls = 0
                while !model.organizer.isConnected, model.connectionState == .checking, polls < 50 {
                    try? await Task.sleep(for: .milliseconds(200))
                    polls += 1
                }
            }
        }
        if model.organizer.isConnected, model.organizer.status == nil {
            _ = await finishes(within: sendWait) { await model.organizer.refreshStatus() }
        }
        // Refused only when the server has said it's off, now or the last time it was asked. Not
        // knowing - a first launch with no signal - keeps the item to send when it can.
        let enabled = model.organizer.status?.enabled ?? model.organizerEnabledIfKnown
        if enabled == false || model.organizer.syncState.turnedOff { throw VoiceOrganizerError.turnedOff }
        // Until the first sync this device doesn't know the person's own space, and new things
        // would default to Family - where everyone sees them.
        guard model.organizer.knowsSpaces else { throw VoiceOrganizerError.notSynced }
        return model
    }

    // Plans the new item's alert on this device at once, even offline, and then tries to send it.
    // True when nothing is left waiting to go.
    static func send(_ model: AppModel) async -> Bool {
        model.organizer.refreshAlerts()
        guard model.organizer.isConnected else { return false }
        _ = await finishes(within: sendWait) { await model.organizer.sync() }
        return model.organizer.syncState.online && model.organizer.syncState.waitingChanges == 0
    }

    static func confirmation(_ said: String, sent: Bool) -> String {
        sent ? said : said + " It'll reach your other devices when Sentinel Core next connects."
    }

    // "today at 5:00 PM", "tomorrow", "Friday at 9:00 AM", "October 3".
    static func spoken(_ date: Date, hasTime: Bool) -> String {
        let calendar = Calendar.current
        let day: String
        if calendar.isDateInToday(date) {
            day = "today"
        } else if calendar.isDateInTomorrow(date) {
            day = "tomorrow"
        } else if let days = calendar.dateComponents([.day], from: calendar.startOfDay(for: Date()), to: calendar.startOfDay(for: date)).day, days > 0, days < 7 {
            day = date.formatted(.dateTime.weekday(.wide))
        } else {
            day = date.formatted(.dateTime.month(.wide).day())
        }
        return hasTime ? "\(day) at \(date.formatted(date: .omitted, time: .shortened))" : day
    }

    // Runs `operation` and waits at most `seconds` for it. What's still running when time is up
    // carries on in the background - a connection that finishes late still sends the item.
    static func finishes(within seconds: Double, _ operation: @escaping @MainActor () async -> Void) async -> Bool {
        final class Once {
            var continuation: CheckedContinuation<Bool, Never>?
            func resume(_ value: Bool) {
                continuation?.resume(returning: value)
                continuation = nil
            }
        }
        let once = Once()
        return await withCheckedContinuation { continuation in
            once.continuation = continuation
            Task { @MainActor in
                await operation()
                once.resume(true)
            }
            Task { @MainActor in
                try? await Task.sleep(for: .seconds(seconds))
                once.resume(false)
            }
        }
    }
}
