import Foundation

// Board automations: "every morning at 7, put 'pick up eggs' in To do", "when a card is moved into
// Done, mark it done". A rule is WHEN (one trigger) + ONLY IF (any number of conditions) + THEN
// (actions, run in order). They run on the server (core OrganizerAutomations.cs), so a card
// scheduled for 7am appears with every device asleep.
//
// Rules are not Organizer items and never sync: they are plain REST under
// /api/organizer/automations, and this device keeps no copy. The dashboard's
// organizer-automations.js is the reference editor; the wording here is a port of its
// ruleSentence(), and the two should read alike.
//
// Every part of a rule is a loose bag of JSON, the same as on the server, so a field or a block
// this build doesn't understand yet goes back exactly as it came.

// One block: the trigger, a condition, an action, or a trigger's schedule.
public struct AutomationBlock: Codable, Hashable, Sendable, Identifiable {
    public var fields: [String: JSONValue]
    // Only so a list of blocks can be shown and reordered; never sent.
    public let id: UUID

    public init(_ fields: [String: JSONValue]) {
        self.fields = fields
        id = UUID()
    }

    public init(from decoder: Decoder) throws {
        fields = try [String: JSONValue](from: decoder)
        id = UUID()
    }

    public func encode(to encoder: Encoder) throws {
        try fields.encode(to: encoder)
    }

    public static func == (lhs: AutomationBlock, rhs: AutomationBlock) -> Bool { lhs.fields == rhs.fields }
    public func hash(into hasher: inout Hasher) { hasher.combine(fields) }

    public var type: String {
        get { fields["type"]?.string ?? "" }
        set { fields["type"] = .string(newValue) }
    }

    // Missing and null read the same: not set.
    public func string(_ key: String) -> String? { fields[key]?.string }
    public func int(_ key: String) -> Int? { fields[key]?.int }
    public func bool(_ key: String) -> Bool? { fields[key]?.bool }
    public func strings(_ key: String) -> [String] { fields[key]?.array?.compactMap(\.string) ?? [] }
    public func ints(_ key: String) -> [Int] { fields[key]?.array?.compactMap(\.int) ?? [] }

    public mutating func set(_ key: String, _ value: JSONValue) { fields[key] = value }

    // A schedule trigger's "when".
    public var schedule: AutomationBlock? {
        get { fields["schedule"]?.object.map(AutomationBlock.init) }
        set { fields["schedule"] = newValue.map { .object($0.fields) } ?? .null }
    }
}

public struct BoardAutomation: Codable, Hashable, Sendable, Identifiable {
    // Everything but the three parts below, exactly as the server sent it.
    public var fields: [String: JSONValue]
    public var trigger: AutomationBlock?
    public var conditions: [AutomationBlock]
    public var actions: [AutomationBlock]

    public init(fields: [String: JSONValue] = [:], trigger: AutomationBlock? = nil, conditions: [AutomationBlock] = [], actions: [AutomationBlock] = []) {
        self.fields = fields
        self.trigger = trigger
        self.conditions = conditions
        self.actions = actions
    }

    public init(from decoder: Decoder) throws {
        var bag = try [String: JSONValue](from: decoder)
        trigger = bag.removeValue(forKey: "trigger")?.object.map(AutomationBlock.init)
        conditions = bag.removeValue(forKey: "conditions")?.array?.compactMap { $0.object.map(AutomationBlock.init) } ?? []
        actions = bag.removeValue(forKey: "actions")?.array?.compactMap { $0.object.map(AutomationBlock.init) } ?? []
        fields = bag
    }

    public func encode(to encoder: Encoder) throws {
        var bag = fields
        bag["trigger"] = trigger.map { .object($0.fields) } ?? .null
        bag["conditions"] = .array(conditions.map { .object($0.fields) })
        bag["actions"] = .array(actions.map { .object($0.fields) })
        try bag.encode(to: encoder)
    }

    // Empty for a rule not saved yet.
    public var id: String {
        get { fields["id"]?.string ?? "" }
        set { fields["id"] = newValue.isEmpty ? nil : .string(newValue) }
    }

    public var name: String {
        get { fields["name"]?.string ?? "" }
        set { fields["name"] = .string(newValue) }
    }

    public var enabled: Bool {
        get { fields["enabled"]?.bool ?? true }
        set { fields["enabled"] = .bool(newValue) }
    }

    public var boardId: String {
        get { fields["boardId"]?.string ?? "" }
        set { fields["boardId"] = .string(newValue) }
    }

    public var isScheduled: Bool { trigger?.type == "schedule" }

    // Kept by the server; whatever a client sends for these is ignored.
    public var nextRunAt: Date? { fields["nextRunAt"]?.string.flatMap(OrganizerDates.parseTimestamp) }
    public var lastRunAt: Date? { fields["lastRunAt"]?.string.flatMap(OrganizerDates.parseTimestamp) }
    public var lastOk: Bool { fields["lastOk"]?.bool ?? false }
    public var lastResult: String { fields["lastResult"]?.string ?? "" }

    // A copy to edit: a saved rule keeps everything it had; a recipe or a blank starts on this board.
    public static func draft(editing rule: BoardAutomation?, recipe: BoardAutomation? = nil, boardId: String) -> BoardAutomation {
        if var rule {
            rule.boardId = boardId
            return rule
        }
        var draft = recipe ?? BoardAutomation(fields: ["name": ""])
        draft.fields["id"] = nil
        draft.enabled = true
        draft.boardId = boardId
        return draft
    }

    // What POST / PUT take: the rule without its id, in the device's time zone.
    public func requestBody(timeZone: String) throws -> Data {
        var body = self
        body.fields["id"] = nil
        body.fields["timeZone"] = .string(timeZone)
        return try JSONEncoder().encode(body)
    }
}

// MARK: - the blocks

public enum AutomationZone: String, CaseIterable, Sendable {
    case trigger, conditions, actions

    public var title: String {
        switch self {
        case .trigger: "WHEN"
        case .conditions: "ONLY IF"
        case .actions: "THEN"
        }
    }
}

// What a block looks like in the palette. `needsCard` blocks act on "the card" - the one that set
// the rule off, or the one the last "Create a card" made.
public struct AutomationBlockKind: Hashable, Sendable, Identifiable {
    public let zone: AutomationZone
    public let type: String
    public let label: String
    public let hint: String?
    public let symbol: String
    public var needsCard = false
    public var needsPeople = false

    public var id: String { "\(zone.rawValue).\(type)" }
}

// What a new block starts out pointing at.
public struct AutomationMakeContext: Sendable {
    public var firstColumnId: String
    public var lastColumnId: String
    public var timeZone: String
    public var people: [OrganizerPerson]

    public init(firstColumnId: String, lastColumnId: String, timeZone: String, people: [OrganizerPerson]) {
        self.firstColumnId = firstColumnId
        self.lastColumnId = lastColumnId
        self.timeZone = timeZone
        self.people = people
    }
}

public struct AutomationRecipe: Sendable, Identifiable {
    public let name: String
    public let why: String
    public let rule: BoardAutomation
    public var id: String { name }
}

public enum BoardAutomations {
    public static let dayShort = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"]
    public static let tokens: [(token: String, what: String)] = [
        ("{date}", "today's date"), ("{weekday}", "day of the week"), ("{month}", "month"),
        ("{card}", "the card's title"), ("{due}", "the card's due date"), ("{board}", "this board's name"),
    ]
    public static let dueOffsets: [(minutes: Int, label: String)] = [
        (0, "at the time it's due"), (15, "15 minutes before"), (60, "1 hour before"), (180, "3 hours before"),
        (1440, "1 day before"), (2880, "2 days before"), (10080, "1 week before"),
    ]
    public static let dueDays: [(days: Int, label: String)] = [
        (0, "today"), (1, "tomorrow"), (2, "in 2 days"), (3, "in 3 days"), (7, "in a week"), (14, "in 2 weeks"), (30, "in 30 days"),
    ]
    public static let scheduleModes: [(mode: String, label: String)] = [
        ("day", "every day"), ("weekdays", "every weekday"), ("weekends", "every weekend day"), ("days", "on certain days"),
        ("monthly", "every month"), ("hourly", "every few hours"), ("once", "just once"),
    ]
    public static let hourChoices = [1, 2, 3, 4, 6, 8, 12]

    public static let triggers: [AutomationBlockKind] = [
        .init(zone: .trigger, type: "schedule", label: "On a schedule", hint: "every morning, every Monday, once…", symbol: "alarm"),
        .init(zone: .trigger, type: "cardAdded", label: "When a card is added", hint: "to a list, or anywhere on the board", symbol: "plus.rectangle"),
        .init(zone: .trigger, type: "cardMoved", label: "When a card is moved", hint: "into a particular list", symbol: "arrow.right.square"),
        .init(zone: .trigger, type: "cardCompleted", label: "When a card is marked done", hint: "ticked off anywhere on the board", symbol: "checkmark.circle"),
        .init(zone: .trigger, type: "checklistCompleted", label: "When a checklist is finished", hint: "every item on a card ticked", symbol: "checklist.checked"),
        .init(zone: .trigger, type: "cardDue", label: "When a card is due", hint: "or a while before - a reminder", symbol: "hourglass"),
        .init(zone: .trigger, type: "labelAdded", label: "When a label is added", hint: "a particular label, or any", symbol: "tag"),
    ]

    public static let conditions: [AutomationBlockKind] = [
        .init(zone: .conditions, type: "titleContains", label: "Title contains…", hint: nil, symbol: "textformat", needsCard: true),
        .init(zone: .conditions, type: "hasLabel", label: "Has a label", hint: nil, symbol: "tag", needsCard: true),
        .init(zone: .conditions, type: "lacksLabel", label: "Doesn't have a label", hint: nil, symbol: "tag.slash", needsCard: true),
        .init(zone: .conditions, type: "inColumn", label: "Is in a list", hint: nil, symbol: "rectangle.split.3x1", needsCard: true),
        .init(zone: .conditions, type: "onDays", label: "Only on certain days", hint: nil, symbol: "calendar"),
    ]

    public static let actions: [AutomationBlockKind] = [
        .init(zone: .actions, type: "createCard", label: "Create a card", hint: "then the steps below act on it", symbol: "plus.rectangle"),
        .init(zone: .actions, type: "moveCard", label: "Move the card", hint: nil, symbol: "arrow.right.square", needsCard: true),
        .init(zone: .actions, type: "addLabel", label: "Add a label", hint: nil, symbol: "tag", needsCard: true),
        .init(zone: .actions, type: "removeLabel", label: "Remove a label", hint: nil, symbol: "tag.slash", needsCard: true),
        .init(zone: .actions, type: "setDue", label: "Set the due date", hint: nil, symbol: "clock", needsCard: true),
        .init(zone: .actions, type: "complete", label: "Mark done / not done", hint: nil, symbol: "checkmark.circle", needsCard: true),
        .init(zone: .actions, type: "addChecklist", label: "Add checklist items", hint: nil, symbol: "checklist", needsCard: true),
        .init(zone: .actions, type: "resetChecklist", label: "Untick the checklist", hint: nil, symbol: "arrow.counterclockwise", needsCard: true),
        .init(zone: .actions, type: "addComment", label: "Add a comment", hint: nil, symbol: "text.bubble", needsCard: true),
        .init(zone: .actions, type: "assign", label: "Assign someone", hint: nil, symbol: "person", needsCard: true, needsPeople: true),
        .init(zone: .actions, type: "deleteCard", label: "Delete the card", hint: nil, symbol: "trash", needsCard: true),
        .init(zone: .actions, type: "notify", label: "Send a notification", hint: "to phones - everyone's, or chosen people", symbol: "bell.badge"),
        .init(zone: .actions, type: "email", label: "Send an email", hint: "to people here, or any address", symbol: "envelope"),
        .init(zone: .actions, type: "moveAllCards", label: "Move every card in a list", hint: nil, symbol: "rectangle.stack"),
        .init(zone: .actions, type: "deleteAllCards", label: "Clear out a list", hint: nil, symbol: "xmark.bin"),
    ]

    public static func kinds(_ zone: AutomationZone) -> [AutomationBlockKind] {
        switch zone {
        case .trigger: triggers
        case .conditions: conditions
        case .actions: actions
        }
    }

    public static func kind(_ zone: AutomationZone, _ type: String) -> AutomationBlockKind? {
        kinds(zone).first { $0.type == type }
    }

    // The palette: "Assign someone" only when there is someone to assign.
    public static func palette(_ zone: AutomationZone, hasPeople: Bool) -> [AutomationBlockKind] {
        kinds(zone).filter { !$0.needsPeople || hasPeople }
    }

    public static func make(_ zone: AutomationZone, _ type: String, context c: AutomationMakeContext) -> AutomationBlock {
        let fields: [String: JSONValue] = switch type {
        case "schedule": ["type": "schedule", "schedule": ["repeat": "daily", "time": "07:00", "days": [], "timeZone": .string(c.timeZone)]]
        case "cardAdded": ["type": "cardAdded", "columnId": ""]
        case "cardMoved": ["type": "cardMoved", "columnId": .string(c.lastColumnId)]
        case "cardDue": ["type": "cardDue", "minutesBefore": 60, "columnId": ""]
        case "labelAdded": ["type": "labelAdded", "label": ""]
        case "titleContains", "hasLabel", "lacksLabel": ["type": .string(type), "text": ""]
        case "inColumn": ["type": "inColumn", "columnId": .string(c.firstColumnId)]
        case "onDays": ["type": "onDays", "days": [1, 2, 3, 4, 5]]
        case "createCard": ["type": "createCard", "columnId": .string(c.firstColumnId), "title": "", "position": "bottom", "skipIfExists": true]
        case "moveCard": ["type": "moveCard", "columnId": .string(c.lastColumnId), "position": "top"]
        case "addLabel": ["type": "addLabel", "name": "", "color": ""]
        case "removeLabel": ["type": "removeLabel", "name": ""]
        case "setDue": ["type": "setDue", "inDays": 1, "time": ""]
        case "complete": ["type": "complete", "done": true]
        case "addChecklist": ["type": "addChecklist", "items": []]
        case "addComment": ["type": "addComment", "text": ""]
        case "assign": ["type": "assign", "username": .string(c.people.first?.username ?? "")]
        case "notify": ["type": "notify", "everyone": .bool(c.people.isEmpty), "people": [], "title": "", "text": ""]
        case "email": ["type": "email", "people": [], "addresses": [], "title": "", "text": ""]
        case "moveAllCards": ["type": "moveAllCards", "columnId": .string(c.lastColumnId), "toColumnId": .string(c.firstColumnId), "onlyCompleted": false]
        case "deleteAllCards": ["type": "deleteAllCards", "columnId": .string(c.lastColumnId), "onlyCompleted": true]
        default: ["type": .string(type)]
        }
        return AutomationBlock(fields)
    }

    // MARK: schedules

    // The editor's choice of how often: the server only knows once/daily/monthly/hourly, and
    // "every weekday" is daily on Mon-Fri.
    public static func scheduleMode(_ schedule: AutomationBlock) -> String {
        let repeatValue = schedule.string("repeat") ?? ""
        guard repeatValue == "daily" else { return repeatValue }
        let days = schedule.ints("days").sorted().map(String.init).joined(separator: ",")
        if days.isEmpty { return "day" }
        if days == "1,2,3,4,5" { return "weekdays" }
        if days == "0,6" { return "weekends" }
        return "days"
    }

    public static func applyScheduleMode(_ mode: String, to s: inout AutomationBlock, now: Date = Date(), calendar: Calendar = .current) {
        s.set("repeat", .string(["monthly", "hourly", "once"].contains(mode) ? mode : "daily"))
        switch mode {
        case "day": s.set("days", [])
        case "weekdays": s.set("days", [1, 2, 3, 4, 5])
        case "weekends": s.set("days", [0, 6])
        case "days": if s.ints("days").isEmpty { s.set("days", [1]) }
        case "monthly":
            s.set("days", [])
            if (s.int("dayOfMonth") ?? 0) == 0 { s.set("dayOfMonth", 1) }
        case "hourly":
            s.set("days", [])
            if (s.int("everyHours") ?? 0) == 0 { s.set("everyHours", 3) }
        case "once":
            s.set("days", [])
            if (s.string("date") ?? "").isEmpty {
                let tomorrow = calendar.date(byAdding: .day, value: 1, to: now) ?? now
                s.set("date", .string(OrganizerDates.ymd(tomorrow, calendar: calendar)))
            }
        default: break
        }
    }

    // Ticks a day on or off, keeping at least one - unticking the last would silently mean every day.
    public static func toggling(_ day: Int, in days: [Int]) -> [Int] {
        let next = days.contains(day) ? days.filter { $0 != day } : (days + [day]).sorted()
        return next.isEmpty ? days : next
    }

    // MARK: checks

    // Whether the rule has a card on hand at step `index`: a card rule starts with one, a schedule
    // only once a "Create a card" step has run - and a "Delete the card" takes it away again.
    public static func cardAvailableBefore(_ index: Int, trigger: AutomationBlock?, actions: [AutomationBlock]) -> Bool {
        let before = actions.prefix(index).map(\.type)
        let lastCreate = before.lastIndex(of: "createCard") ?? -1
        let lastDelete = before.lastIndex(of: "deleteCard") ?? -1
        if trigger?.type == "schedule" { return lastCreate > lastDelete }
        return lastDelete == -1 || lastCreate > lastDelete
    }

    // What to say under a THEN step that would have nothing to act on, or nil.
    public static func actionWarning(_ index: Int, in rule: BoardAutomation) -> String? {
        guard index < rule.actions.count, kind(.actions, rule.actions[index].type)?.needsCard == true,
              !cardAvailableBefore(index, trigger: rule.trigger, actions: rule.actions) else { return nil }
        return rule.isScheduled
            ? "A schedule has no card of its own - put “Create a card” above this step."
            : "The card was deleted by an earlier step."
    }

    public static func conditionWarning(_ index: Int, in rule: BoardAutomation) -> String? {
        guard rule.isScheduled, index < rule.conditions.count, kind(.conditions, rule.conditions[index].type)?.needsCard == true else { return nil }
        return "A schedule has no card to check - this would never be true."
    }

    // The checks made before a rule goes to the server, which checks the rest.
    public static func problems(_ rule: BoardAutomation) -> [String] {
        var errors: [String] = []
        if rule.trigger == nil { errors.append("Add a WHEN block - what starts the rule?") }
        if rule.actions.isEmpty { errors.append("Add at least one THEN block - what should happen?") }
        if rule.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { errors.append("Give the rule a name.") }
        return errors
    }

    // The server's reasons, one per line, for showing as a list.
    public static func messages(for error: Error) -> [String] {
        if case let OrganizerError.server(_, message?) = error {
            let lines = message.split(separator: "\n").map(String.init).filter { !$0.isEmpty }
            if !lines.isEmpty { return lines }
        }
        return [error.localizedDescription]
    }

    public static let offlineMessage = "Automations need the server - they can't be changed offline."

    // MARK: recipes

    public static func recipes(firstColumnId first: String, lastColumnId last: String, lastColumnName: String, timeZone tz: String) -> [AutomationRecipe] {
        func schedule(_ time: String, _ days: [Int]) -> AutomationBlock {
            AutomationBlock(["type": "schedule", "schedule": ["repeat": "daily", "time": .string(time), "days": .array(days.map { .number(Double($0)) }), "timeZone": .string(tz)]])
        }
        func rule(_ name: String, _ trigger: AutomationBlock, _ actions: [[String: JSONValue]]) -> BoardAutomation {
            BoardAutomation(fields: ["name": .string(name)], trigger: trigger, conditions: [], actions: actions.map(AutomationBlock.init))
        }
        return [
            AutomationRecipe(name: "Daily to-do", why: "a card every morning", rule: rule(
                "Pick up eggs every morning", schedule("07:00", []),
                [["type": "createCard", "columnId": .string(first), "title": "pick up eggs", "position": "bottom", "skipIfExists": true]])),
            AutomationRecipe(name: "Weekly chore", why: "every Sunday, with a checklist", rule: rule(
                "Sunday chores", schedule("09:00", [0]),
                [["type": "createCard", "columnId": .string(first), "title": "Chores for {date}", "position": "bottom", "skipIfExists": true,
                  "items": ["take out the trash", "water the plants", "vacuum"]]])),
            AutomationRecipe(name: "Done means done", why: "moving to the last list ticks it off", rule: rule(
                "Moved to \(lastColumnName) → done", AutomationBlock(["type": "cardMoved", "columnId": .string(last)]),
                [["type": "complete", "done": true]])),
            AutomationRecipe(name: "Tidy up weekly", why: "clear finished cards each Monday", rule: rule(
                "Clear \(lastColumnName) on Mondays", schedule("06:00", [1]),
                [["type": "deleteAllCards", "columnId": .string(last), "onlyCompleted": true]])),
            AutomationRecipe(name: "Due tomorrow", why: "new cards get a due date", rule: rule(
                "New cards are due tomorrow", AutomationBlock(["type": "cardAdded", "columnId": .string(first)]),
                [["type": "setDue", "inDays": 1, "time": ""]])),
            AutomationRecipe(name: "Due reminder", why: "a notification an hour before a card is due", rule: rule(
                "Remind me before cards are due", AutomationBlock(["type": "cardDue", "minutesBefore": 60, "columnId": ""]),
                [["type": "notify", "everyone": true, "people": [], "title": "", "text": "{card} is due {due}"]])),
            AutomationRecipe(name: "Bin night", why: "a notification every Sunday evening", rule: rule(
                "Bins out on Sunday", schedule("18:00", [0]),
                [["type": "notify", "everyone": true, "people": [], "title": "", "text": "Take the bins out tonight"]])),
            AutomationRecipe(name: "Urgent to the top", why: "the urgent label jumps the queue", rule: rule(
                "Urgent cards to the top", AutomationBlock(["type": "labelAdded", "label": "urgent"]),
                [["type": "moveCard", "columnId": .string(first), "position": "top"]])),
        ]
    }
}

// MARK: - in plain words

// A rule read back as a sentence: "Every weekday at 7:00 AM, create a card “pick up eggs” in To do
// (unless one is still there)." A port of the dashboard's ruleSentence() and its blocks' words().
public struct AutomationWording {
    // A list's title by id; nil when there's no such list (any more).
    public var columnTitle: (String) -> String?
    public var people: [OrganizerPerson]
    public var locale: Locale
    public var timeZone: TimeZone

    public init(columnTitle: @escaping (String) -> String?, people: [OrganizerPerson] = [], locale: Locale = .current, timeZone: TimeZone = .current) {
        self.columnTitle = columnTitle
        self.people = people
        self.locale = locale
        self.timeZone = timeZone
    }

    public func columnName(_ id: String?) -> String {
        guard let id, !id.isEmpty, let title = columnTitle(id) else { return "(pick a list)" }
        return title.isEmpty ? "list" : title
    }

    private func q(_ text: String?) -> String { "“\(text?.isEmpty == false ? text! : "…")”" }

    private func formatter(_ template: String) -> DateFormatter {
        let formatter = DateFormatter()
        formatter.locale = locale
        formatter.timeZone = timeZone
        formatter.setLocalizedDateFormatFromTemplate(template)
        return formatter
    }

    // Newer ICU puts a narrow no-break space before AM/PM; plain words want a plain one.
    private func plain(_ text: String) -> String { text.replacingOccurrences(of: "\u{202F}", with: " ") }

    // "07:00" -> "7:00 AM" (or "07:00" where the clock is 24-hour).
    public func time12(_ hhmm: String?) -> String {
        let text = hhmm?.isEmpty == false ? hhmm! : "07:00"
        let parts = text.split(separator: ":").map { Int($0) ?? 0 }
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        let date = calendar.date(from: DateComponents(year: 2000, month: 1, day: 1, hour: parts.first ?? 0, minute: parts.count > 1 ? parts[1] : 0)) ?? Date()
        return plain(formatter("jmm").string(from: date))
    }

    // For a list of runs: "Tue, Sep 29, 7:00 AM".
    public func formatWhen(_ date: Date) -> String {
        plain(formatter("EEEMMMdjmm").string(from: date))
    }

    public func dueWords(inDays: Int?, time: String?) -> String {
        guard let n = inDays else { return "no due date" }
        let day = n == 0 ? "today" : n == 1 ? "tomorrow" : "in \(n) days"
        return time?.isEmpty == false ? "\(day) at \(time12(time))" : day
    }

    private func dayList(_ days: [Int]) -> String {
        let names = days.sorted().compactMap { (0...6).contains($0) ? BoardAutomations.dayShort[$0] : nil }
        return names.isEmpty ? "…" : names.joined(separator: ", ")
    }

    public func scheduleWords(_ s: AutomationBlock) -> String {
        let at = time12(s.string("time"))
        switch BoardAutomations.scheduleMode(s) {
        case "day": return "Every day at \(at)"
        case "weekdays": return "Every weekday at \(at)"
        case "weekends": return "Every Saturday and Sunday at \(at)"
        case "days": return "Every \(dayList(s.ints("days"))) at \(at)"
        case "monthly":
            let day = s.int("dayOfMonth") ?? 0
            return "\(day == -1 || day == 0 ? "On the last day" : "On day \(day)") of every month at \(at)"
        case "hourly":
            let every = (s.int("everyHours") ?? 0) == 0 ? 1 : s.int("everyHours")!
            return "Every \(every) hour\(every == 1 ? "" : "s") from \(at) until midnight"
        case "once":
            var calendar = Calendar(identifier: .gregorian)
            calendar.timeZone = timeZone
            let date = s.string("date").flatMap { OrganizerDates.day($0, calendar: calendar) }
            return "Once, on \(date.map { plain(formatter("EEEMMMd").string(from: $0)) } ?? "…") at \(at)"
        default: return "On a schedule"
        }
    }

    // "Nathan", "Nathan and Terry", "Nathan, Terry and Sam" - by display name where there is one.
    public func whoWords(_ usernames: [String], quiet: Bool = false) -> String {
        let names = usernames.map { name in people.first { $0.username == name }?.label ?? name }
        if names.isEmpty { return quiet ? "" : "…" }
        return names.count == 1 ? names[0] : "\(names.dropLast().joined(separator: ", ")) and \(names.last!)"
    }

    public func triggerWords(_ t: AutomationBlock) -> String? {
        let column = t.string("columnId") ?? ""
        switch t.type {
        case "schedule": return scheduleWords(t.schedule ?? AutomationBlock([:]))
        case "cardAdded": return "When a card is added to \(column.isEmpty ? "any list" : columnName(column))"
        case "cardMoved": return "When a card is moved into \(column.isEmpty ? "any list" : columnName(column))"
        case "cardCompleted": return "When a card is marked done"
        case "checklistCompleted": return "When every checklist item on a card is ticked"
        case "cardDue":
            let minutes = t.int("minutesBefore") ?? 0
            let when: String
            if minutes == 0 {
                when = "When a card is due"
            } else if let offset = BoardAutomations.dueOffsets.first(where: { $0.minutes == minutes }) {
                when = "\(offset.label.prefix(1).uppercased())\(offset.label.dropFirst()) a card is due"
            } else {
                when = "\(minutes) minutes before a card is due"
            }
            return "\(when)\(column.isEmpty ? "" : " in \(columnName(column))")"
        case "labelAdded":
            let label = t.string("label") ?? ""
            return "When \(label.isEmpty ? "any label" : "the \(q(label)) label") is added to a card"
        default: return nil
        }
    }

    public func conditionWords(_ c: AutomationBlock) -> String? {
        switch c.type {
        case "titleContains": "the title contains \(q(c.string("text")))"
        case "hasLabel": "it has the \(q(c.string("text"))) label"
        case "lacksLabel": "it doesn't have the \(q(c.string("text"))) label"
        case "inColumn": "it's in \(columnName(c.string("columnId")))"
        case "onDays": "it's \(dayList(c.ints("days")))"
        default: nil
        }
    }

    public func actionWords(_ a: AutomationBlock) -> String? {
        switch a.type {
        case "createCard":
            let due = a.int("inDays").map { ", due \(dueWords(inDays: $0, time: a.string("time")))" } ?? ""
            return "create a card \(q(a.string("title"))) in \(columnName(a.string("columnId")))\(due)\(a.bool("skipIfExists") == true ? " (unless one is still there)" : "")"
        case "moveCard": return "move it to the \(a.string("position") == "bottom" ? "bottom" : "top") of \(columnName(a.string("columnId")))"
        case "addLabel": return "label it \(q(a.string("name")))"
        case "removeLabel": return "take off the \(q(a.string("name"))) label"
        case "setDue": return a.int("inDays") == nil ? "clear its due date" : "make it due \(dueWords(inDays: a.int("inDays"), time: a.string("time")))"
        case "complete": return a.bool("done") == false ? "mark it not done" : "mark it done"
        case "addChecklist":
            let count = a.strings("items").count
            return "add \(count == 0 ? "some" : String(count)) checklist item\(count == 1 ? "" : "s")"
        case "resetChecklist": return "untick its checklist"
        case "addComment": return "comment \(q(a.string("text")))"
        case "assign": return "assign it to \(a.string("username")?.isEmpty == false ? a.string("username")! : "…")"
        case "deleteCard": return "delete it"
        case "notify": return "notify \(a.bool("everyone") == true ? "everyone" : whoWords(a.strings("people"))) \(q(a.string("text")))"
        case "email":
            let to = ([whoWords(a.strings("people"), quiet: true)] + a.strings("addresses")).filter { !$0.isEmpty }.joined(separator: ", ")
            return "email \(to.isEmpty ? "…" : to) \(q(a.string("text")))"
        case "moveAllCards":
            return "move every \(a.bool("onlyCompleted") == true ? "finished " : "")card from \(columnName(a.string("columnId"))) to \(columnName(a.string("toColumnId")))"
        case "deleteAllCards":
            return "delete every \(a.bool("onlyCompleted") == true ? "finished " : "")card in \(columnName(a.string("columnId")))"
        default: return nil
        }
    }

    public func sentence(_ rule: BoardAutomation) -> String {
        let when = rule.trigger.flatMap(triggerWords) ?? "When…"
        let ifs = rule.conditions.map { conditionWords($0) ?? $0.type }
        let thens = rule.actions.map { actionWords($0) ?? $0.type }
        return "\(when)\(ifs.isEmpty ? "" : ", if \(ifs.joined(separator: " and "))"), \(thens.isEmpty ? "do…" : thens.joined(separator: ", then "))."
    }

    // The editor's live line, before there's anything to say.
    public func draftSentence(_ rule: BoardAutomation) -> String {
        rule.trigger == nil && rule.actions.isEmpty ? "Add a WHEN block and a THEN block to get started." : sentence(rule)
    }
}

// MARK: - talking to the server

public struct BoardAutomationList: Sendable {
    public var automations: [BoardAutomation]
    public var timeZone: String?
    // Whether the send blocks can reach anyone on this server yet.
    public var pushReady: Bool
    public var emailReady: Bool
}

public struct BoardAutomationRun: Decodable, Hashable, Sendable, Identifiable {
    public var automationId: String
    public var at: Date
    public var ok: Bool
    public var message: String
    public var id: String { "\(at.timeIntervalSince1970)-\(message)" }
}

struct AutomationListResponse: Decodable {
    var automations: [BoardAutomation]?
    var timeZone: String?
    var pushReady: Bool?
    var emailReady: Bool?
}

struct AutomationRunsResponse: Decodable { var runs: [BoardAutomationRun] }
struct AutomationPreviewResponse: Decodable { var runs: [Date] }

extension OrganizerEngine {
    private static func automationPath(_ id: String, _ suffix: String = "") -> String {
        "/api/organizer/automations/\(id.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? id)\(suffix)"
    }

    public func automations(boardId: String) async throws -> BoardAutomationList {
        let response = try await send("GET", "/api/organizer/automations", query: [URLQueryItem(name: "boardId", value: boardId)])
        let list = try JSONDecoder.organizer.decode(AutomationListResponse.self, from: response.data)
        return BoardAutomationList(automations: list.automations ?? [], timeZone: list.timeZone,
                                   pushReady: list.pushReady != false, emailReady: list.emailReady != false)
    }

    // A new rule when it has no id; the saved rule comes back.
    @discardableResult
    public func saveAutomation(_ rule: BoardAutomation, timeZone: String = TimeZone.current.identifier) async throws -> BoardAutomation {
        let body = try rule.requestBody(timeZone: timeZone)
        let response = rule.id.isEmpty
            ? try await send("POST", "/api/organizer/automations", body: body)
            : try await send("PUT", Self.automationPath(rule.id), body: body)
        return try JSONDecoder.organizer.decode(BoardAutomation.self, from: response.data)
    }

    // The whole rule goes back with only the switch changed, as the dashboard does.
    public func setAutomationEnabled(_ rule: BoardAutomation, _ enabled: Bool) async throws {
        var changed = rule
        changed.enabled = enabled
        _ = try await send("PUT", Self.automationPath(rule.id), body: try JSONEncoder().encode(changed))
    }

    public func deleteAutomation(id: String) async throws {
        _ = try await send("DELETE", Self.automationPath(id))
    }

    // Schedule rules only. Syncs afterwards so a card it made shows straight away.
    public func runAutomation(id: String) async throws -> BoardAutomationRun {
        let response = try await send("POST", Self.automationPath(id, "/run"))
        let run = try JSONDecoder.organizer.decode(BoardAutomationRun.self, from: response.data)
        await sync()
        return run
    }

    public func automationRuns(id: String) async throws -> [BoardAutomationRun] {
        let response = try await send("GET", Self.automationPath(id, "/runs"))
        return try JSONDecoder.organizer.decode(AutomationRunsResponse.self, from: response.data).runs
    }

    // The next few times a schedule would run, worked out by the server so they're what will happen.
    public func previewSchedule(_ schedule: AutomationBlock) async throws -> [Date] {
        let response = try await send("POST", "/api/organizer/automations/preview", body: try JSONEncoder().encode(schedule))
        return try JSONDecoder.organizer.decode(AutomationPreviewResponse.self, from: response.data).runs
    }
}
