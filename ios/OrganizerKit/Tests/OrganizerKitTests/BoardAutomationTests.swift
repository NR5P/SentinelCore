import Foundation
import XCTest
@testable import OrganizerKit

// Board automations: the plain-words sentence (a port of the dashboard's ruleSentence, so each
// block's wording is pinned here), the rule's JSON surviving a round trip untouched, and the
// "is there a card to act on" check behind the editor's warnings.
final class BoardAutomationTests: XCTestCase {
    private let columns = ["todo": "To do", "done": "Done", "blank": ""]
    private let people = [
        OrganizerPerson(username: "morgan", displayName: "Morgan", hasEmail: true),
        OrganizerPerson(username: "terry", displayName: "Terry", hasEmail: false),
        OrganizerPerson(username: "pat", displayName: nil),
    ]

    private var words: AutomationWording {
        let columns = self.columns
        return AutomationWording(columnTitle: { columns[$0] }, people: people,
                                 locale: Locale(identifier: "en_US"), timeZone: TimeZone(identifier: "America/Chicago")!)
    }

    private func block(_ fields: [String: JSONValue]) -> AutomationBlock { AutomationBlock(fields) }

    private func schedule(_ fields: [String: JSONValue]) -> AutomationBlock {
        block(["type": "schedule", "schedule": .object(fields)])
    }

    // MARK: - triggers

    func testScheduleWordsForEveryMode() {
        XCTAssertEqual(words.triggerWords(schedule(["repeat": "daily", "time": "07:00", "days": []])), "Every day at 7:00 AM")
        XCTAssertEqual(words.triggerWords(schedule(["repeat": "daily", "time": "07:30", "days": [5, 1, 2, 3, 4]])), "Every weekday at 7:30 AM")
        XCTAssertEqual(words.triggerWords(schedule(["repeat": "daily", "time": "18:00", "days": [6, 0]])), "Every Saturday and Sunday at 6:00 PM")
        XCTAssertEqual(words.triggerWords(schedule(["repeat": "daily", "time": "09:00", "days": [3, 1]])), "Every Mon, Wed at 9:00 AM")
        XCTAssertEqual(words.triggerWords(schedule(["repeat": "monthly", "time": "08:00", "dayOfMonth": 15])), "On day 15 of every month at 8:00 AM")
        XCTAssertEqual(words.triggerWords(schedule(["repeat": "monthly", "time": "08:00", "dayOfMonth": -1])), "On the last day of every month at 8:00 AM")
        XCTAssertEqual(words.triggerWords(schedule(["repeat": "monthly", "time": "08:00", "dayOfMonth": nil])), "On the last day of every month at 8:00 AM")
        XCTAssertEqual(words.triggerWords(schedule(["repeat": "hourly", "time": "06:00", "everyHours": 3])), "Every 3 hours from 6:00 AM until midnight")
        XCTAssertEqual(words.triggerWords(schedule(["repeat": "hourly", "time": "06:00", "everyHours": nil])), "Every 1 hour from 6:00 AM until midnight")
        XCTAssertEqual(words.triggerWords(schedule(["repeat": "once", "time": "12:00", "date": "2026-09-29"])), "Once, on Tue, Sep 29 at 12:00 PM")
        XCTAssertEqual(words.triggerWords(schedule(["repeat": "once", "time": "", "date": nil])), "Once, on … at 7:00 AM")
        XCTAssertEqual(words.triggerWords(schedule(["repeat": "weekly"])), "On a schedule")
    }

    func testCardTriggerWords() {
        XCTAssertEqual(words.triggerWords(block(["type": "cardAdded", "columnId": ""])), "When a card is added to any list")
        XCTAssertEqual(words.triggerWords(block(["type": "cardAdded", "columnId": "todo"])), "When a card is added to To do")
        XCTAssertEqual(words.triggerWords(block(["type": "cardMoved", "columnId": nil])), "When a card is moved into any list")
        XCTAssertEqual(words.triggerWords(block(["type": "cardMoved", "columnId": "gone"])), "When a card is moved into (pick a list)")
        XCTAssertEqual(words.triggerWords(block(["type": "cardMoved", "columnId": "blank"])), "When a card is moved into list")
        XCTAssertEqual(words.triggerWords(block(["type": "cardCompleted"])), "When a card is marked done")
        XCTAssertEqual(words.triggerWords(block(["type": "checklistCompleted"])), "When every checklist item on a card is ticked")
        XCTAssertEqual(words.triggerWords(block(["type": "cardDue", "minutesBefore": 0, "columnId": ""])), "When a card is due")
        XCTAssertEqual(words.triggerWords(block(["type": "cardDue", "minutesBefore": 60, "columnId": "todo"])), "1 hour before a card is due in To do")
        XCTAssertEqual(words.triggerWords(block(["type": "cardDue", "minutesBefore": 45])), "45 minutes before a card is due")
        XCTAssertEqual(words.triggerWords(block(["type": "labelAdded", "label": ""])), "When any label is added to a card")
        XCTAssertEqual(words.triggerWords(block(["type": "labelAdded", "label": "urgent"])), "When the “urgent” label is added to a card")
        XCTAssertNil(words.triggerWords(block(["type": "somethingNew"])))
    }

    // MARK: - conditions

    func testConditionWords() {
        XCTAssertEqual(words.conditionWords(block(["type": "titleContains", "text": "asap"])), "the title contains “asap”")
        XCTAssertEqual(words.conditionWords(block(["type": "titleContains", "text": ""])), "the title contains “…”")
        XCTAssertEqual(words.conditionWords(block(["type": "hasLabel", "text": "home"])), "it has the “home” label")
        XCTAssertEqual(words.conditionWords(block(["type": "lacksLabel", "text": "home"])), "it doesn't have the “home” label")
        XCTAssertEqual(words.conditionWords(block(["type": "inColumn", "columnId": "done"])), "it's in Done")
        XCTAssertEqual(words.conditionWords(block(["type": "onDays", "days": [5, 1]])), "it's Mon, Fri")
        XCTAssertEqual(words.conditionWords(block(["type": "onDays", "days": []])), "it's …")
    }

    // MARK: - actions

    func testActionWords() {
        XCTAssertEqual(words.actionWords(block(["type": "createCard", "columnId": "todo", "title": "pick up eggs", "skipIfExists": true, "inDays": nil])),
                       "create a card “pick up eggs” in To do (unless one is still there)")
        XCTAssertEqual(words.actionWords(block(["type": "createCard", "columnId": "todo", "title": "bins", "skipIfExists": false, "inDays": 1, "time": "19:00"])),
                       "create a card “bins” in To do, due tomorrow at 7:00 PM")
        XCTAssertEqual(words.actionWords(block(["type": "moveCard", "columnId": "done", "position": "top"])), "move it to the top of Done")
        XCTAssertEqual(words.actionWords(block(["type": "moveCard", "columnId": "done", "position": "bottom"])), "move it to the bottom of Done")
        XCTAssertEqual(words.actionWords(block(["type": "addLabel", "name": "urgent", "color": "#ff0000"])), "label it “urgent”")
        XCTAssertEqual(words.actionWords(block(["type": "removeLabel", "name": "urgent"])), "take off the “urgent” label")
        XCTAssertEqual(words.actionWords(block(["type": "setDue", "inDays": 0, "time": ""])), "make it due today")
        XCTAssertEqual(words.actionWords(block(["type": "setDue", "inDays": 3, "time": "09:15"])), "make it due in 3 days at 9:15 AM")
        XCTAssertEqual(words.actionWords(block(["type": "setDue", "inDays": nil, "time": ""])), "clear its due date")
        XCTAssertEqual(words.actionWords(block(["type": "complete", "done": true])), "mark it done")
        XCTAssertEqual(words.actionWords(block(["type": "complete", "done": false])), "mark it not done")
        XCTAssertEqual(words.actionWords(block(["type": "addChecklist", "items": []])), "add some checklist items")
        XCTAssertEqual(words.actionWords(block(["type": "addChecklist", "items": ["a"]])), "add 1 checklist item")
        XCTAssertEqual(words.actionWords(block(["type": "addChecklist", "items": ["a", "b"]])), "add 2 checklist items")
        XCTAssertEqual(words.actionWords(block(["type": "resetChecklist"])), "untick its checklist")
        XCTAssertEqual(words.actionWords(block(["type": "addComment", "text": "finished on {date}"])), "comment “finished on {date}”")
        XCTAssertEqual(words.actionWords(block(["type": "assign", "username": "terry"])), "assign it to terry")
        XCTAssertEqual(words.actionWords(block(["type": "assign", "username": ""])), "assign it to …")
        XCTAssertEqual(words.actionWords(block(["type": "deleteCard"])), "delete it")
        XCTAssertEqual(words.actionWords(block(["type": "notify", "everyone": true, "people": [], "text": "bins"])), "notify everyone “bins”")
        XCTAssertEqual(words.actionWords(block(["type": "notify", "everyone": false, "people": ["morgan", "terry", "pat"], "text": ""])),
                       "notify Morgan, Terry and pat “…”")
        XCTAssertEqual(words.actionWords(block(["type": "notify", "everyone": false, "people": [], "text": "x"])), "notify … “x”")
        XCTAssertEqual(words.actionWords(block(["type": "email", "people": ["morgan"], "addresses": ["a@b.com"], "text": "hi"])), "email Morgan, a@b.com “hi”")
        XCTAssertEqual(words.actionWords(block(["type": "email", "people": [], "addresses": [], "text": ""])), "email … “…”")
        XCTAssertEqual(words.actionWords(block(["type": "moveAllCards", "columnId": "done", "toColumnId": "todo", "onlyCompleted": true])),
                       "move every finished card from Done to To do")
        XCTAssertEqual(words.actionWords(block(["type": "moveAllCards", "columnId": "done", "toColumnId": "", "onlyCompleted": false])),
                       "move every card from Done to (pick a list)")
        XCTAssertEqual(words.actionWords(block(["type": "deleteAllCards", "columnId": "done", "onlyCompleted": true])), "delete every finished card in Done")
        XCTAssertEqual(words.actionWords(block(["type": "deleteAllCards", "columnId": "done", "onlyCompleted": false])), "delete every card in Done")
        XCTAssertNil(words.actionWords(block(["type": "teleport"])))
    }

    func testWholeSentence() {
        let rule = BoardAutomation(
            fields: ["name": "Eggs"],
            trigger: schedule(["repeat": "daily", "time": "07:00", "days": [1, 2, 3, 4, 5]]),
            conditions: [block(["type": "onDays", "days": [1]]), block(["type": "mystery"])],
            actions: [block(["type": "createCard", "columnId": "todo", "title": "eggs", "skipIfExists": true]), block(["type": "complete", "done": true]), block(["type": "teleport"])])
        XCTAssertEqual(words.sentence(rule),
                       "Every weekday at 7:00 AM, if it's Mon and mystery, create a card “eggs” in To do (unless one is still there), then mark it done, then teleport.")
        XCTAssertEqual(words.sentence(BoardAutomation(trigger: nil, actions: [])), "When…, do….")
        XCTAssertEqual(words.draftSentence(BoardAutomation()), "Add a WHEN block and a THEN block to get started.")
        XCTAssertEqual(words.draftSentence(BoardAutomation(actions: [block(["type": "deleteCard"])])), "When…, delete it.")
    }

    // MARK: - JSON

    // A rule as the server sends it: camelCase, nulls written out, server-kept fields, and a
    // field and a block this build has never heard of. All of it has to go back unchanged.
    func testRuleSurvivesARoundTripIncludingNullsAndUnknowns() throws {
        let json = """
        {"id":"abc","name":"Eggs","enabled":false,"boardId":"b1","timeZone":null,"runAs":"morgan","createdBy":"morgan",
         "createdAt":"2026-09-24T12:00:00+00:00","updatedAt":"2026-09-24T12:00:00+00:00",
         "nextRunAt":"2026-09-30T12:00:00+00:00","lastRunAt":"2026-09-29T12:00:00.1234567+00:00","lastOk":true,"lastResult":"made 1 card","runCount":3,
         "futureField":{"x":[1,2.5,null]},
         "trigger":{"type":"schedule","columnId":null,"minutesBefore":null,"label":null,
                    "schedule":{"repeat":"daily","time":"07:00","days":[1,2,3,4,5],"dayOfMonth":null,"everyHours":null,"date":null,"timeZone":"America/Chicago"}},
         "conditions":[{"type":"onDays","text":null,"columnId":null,"days":[1]}],
         "actions":[{"type":"createCard","columnId":"c1","toColumnId":null,"title":"eggs","inDays":null,"time":null,"skipIfExists":true,"onlyCompleted":false,"done":true,"everyone":false},
                    {"type":"teleport","where":"moon"}]}
        """
        let original = try JSONDecoder().decode([String: JSONValue].self, from: Data(json.utf8))
        let rule = try JSONDecoder.organizer.decode(BoardAutomation.self, from: Data(json.utf8))
        XCTAssertEqual(rule.id, "abc")
        XCTAssertEqual(rule.name, "Eggs")
        XCTAssertFalse(rule.enabled)
        XCTAssertTrue(rule.isScheduled)
        XCTAssertEqual(rule.trigger?.schedule?.ints("days"), [1, 2, 3, 4, 5])
        XCTAssertNil(rule.actions[0].int("inDays"))
        XCTAssertEqual(rule.actions[1].type, "teleport")
        XCTAssertNotNil(rule.nextRunAt)
        XCTAssertNotNil(rule.lastRunAt)
        XCTAssertTrue(rule.lastOk)
        XCTAssertEqual(rule.lastResult, "made 1 card")

        let back = try JSONDecoder().decode([String: JSONValue].self, from: JSONEncoder().encode(rule))
        XCTAssertEqual(back, original)

        // An edit changes only what was edited.
        var edited = rule
        edited.actions[0].set("inDays", 2)
        edited.trigger?.schedule?.set("time", "08:00")
        let editedBack = try JSONDecoder().decode([String: JSONValue].self, from: JSONEncoder().encode(edited))
        XCTAssertEqual(editedBack["futureField"], original["futureField"])
        XCTAssertEqual(editedBack["actions"]?.array?[0].object?["inDays"], 2)
        XCTAssertEqual(editedBack["actions"]?.array?[1], original["actions"]?.array?[1])
        XCTAssertEqual(editedBack["trigger"]?.object?["schedule"]?.object?["time"], "08:00")
        XCTAssertEqual(editedBack["trigger"]?.object?["schedule"]?.object?["timeZone"], "America/Chicago")
    }

    func testRequestBodyDropsTheIdAndCarriesTheTimeZone() throws {
        var rule = BoardAutomation(fields: ["id": "abc", "name": "x", "enabled": true, "boardId": "b1"], trigger: block(["type": "cardCompleted"]),
                                   actions: [block(["type": "setDue", "inDays": nil])])
        let body = try JSONDecoder().decode([String: JSONValue].self, from: rule.requestBody(timeZone: "Europe/London"))
        XCTAssertNil(body["id"])
        XCTAssertEqual(body["timeZone"], "Europe/London")
        XCTAssertEqual(body["conditions"], [])
        // setDue with inDays null clears the due date, so the null has to be sent, not dropped.
        XCTAssertEqual(body["actions"]?.array?[0].object?["inDays"], JSONValue.null)
        rule.id = ""
        XCTAssertNil(rule.fields["id"])
    }

    func testDraftsFromARecipeAreNewAndOnThisBoard() {
        let recipe = BoardAutomations.recipes(firstColumnId: "todo", lastColumnId: "done", lastColumnName: "Done", timeZone: "America/Chicago")[0]
        let draft = BoardAutomation.draft(editing: nil, recipe: recipe.rule, boardId: "b1")
        XCTAssertEqual(draft.id, "")
        XCTAssertTrue(draft.enabled)
        XCTAssertEqual(draft.boardId, "b1")
        XCTAssertEqual(draft.name, "Pick up eggs every morning")

        let saved = BoardAutomation(fields: ["id": "r1", "name": "Mine", "enabled": false, "boardId": "b1", "runCount": 4])
        let editing = BoardAutomation.draft(editing: saved, boardId: "b1")
        XCTAssertEqual(editing.id, "r1")
        XCTAssertFalse(editing.enabled)
        XCTAssertEqual(editing.fields["runCount"], 4)
    }

    // MARK: - card available

    func testCardAvailableBefore() {
        let create = block(["type": "createCard"]), delete = block(["type": "deleteCard"]), label = block(["type": "addLabel"])
        let scheduled = schedule(["repeat": "daily"]), moved = block(["type": "cardMoved"])

        // A schedule has no card until it makes one, and none again once it deletes it.
        XCTAssertFalse(BoardAutomations.cardAvailableBefore(0, trigger: scheduled, actions: [label]))
        XCTAssertTrue(BoardAutomations.cardAvailableBefore(1, trigger: scheduled, actions: [create, label]))
        XCTAssertFalse(BoardAutomations.cardAvailableBefore(2, trigger: scheduled, actions: [create, delete, label]))
        XCTAssertTrue(BoardAutomations.cardAvailableBefore(3, trigger: scheduled, actions: [create, delete, create, label]))

        // A card rule starts with one.
        XCTAssertTrue(BoardAutomations.cardAvailableBefore(0, trigger: moved, actions: [label]))
        XCTAssertFalse(BoardAutomations.cardAvailableBefore(1, trigger: moved, actions: [delete, label]))
        XCTAssertTrue(BoardAutomations.cardAvailableBefore(2, trigger: moved, actions: [delete, create, label]))
        XCTAssertTrue(BoardAutomations.cardAvailableBefore(0, trigger: nil, actions: [label]))

        let rule = BoardAutomation(trigger: scheduled, conditions: [block(["type": "hasLabel"]), block(["type": "onDays"])],
                                   actions: [label, create, block(["type": "notify"])])
        XCTAssertEqual(BoardAutomations.actionWarning(0, in: rule), "A schedule has no card of its own - put “Create a card” above this step.")
        XCTAssertNil(BoardAutomations.actionWarning(1, in: rule))
        XCTAssertNil(BoardAutomations.actionWarning(2, in: rule))
        XCTAssertNotNil(BoardAutomations.conditionWarning(0, in: rule))
        XCTAssertNil(BoardAutomations.conditionWarning(1, in: rule))

        let deleted = BoardAutomation(trigger: moved, actions: [delete, label])
        XCTAssertEqual(BoardAutomations.actionWarning(1, in: deleted), "The card was deleted by an earlier step.")
    }

    // MARK: - schedule mode

    func testScheduleModes() {
        func mode(_ fields: [String: JSONValue]) -> String { BoardAutomations.scheduleMode(block(fields)) }
        XCTAssertEqual(mode(["repeat": "daily", "days": []]), "day")
        XCTAssertEqual(mode(["repeat": "daily"]), "day")
        XCTAssertEqual(mode(["repeat": "daily", "days": [5, 4, 3, 2, 1]]), "weekdays")
        XCTAssertEqual(mode(["repeat": "daily", "days": [6, 0]]), "weekends")
        XCTAssertEqual(mode(["repeat": "daily", "days": [2]]), "days")
        XCTAssertEqual(mode(["repeat": "monthly"]), "monthly")
        XCTAssertEqual(mode(["repeat": "hourly"]), "hourly")
        XCTAssertEqual(mode(["repeat": "once"]), "once")

        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "America/Chicago")!
        let now = calendar.date(from: DateComponents(year: 2026, month: 12, day: 31, hour: 10))!
        var s = block(["repeat": "daily", "time": "07:00", "days": []])
        BoardAutomations.applyScheduleMode("once", to: &s, now: now, calendar: calendar)
        XCTAssertEqual(s.string("repeat"), "once")
        XCTAssertEqual(s.string("date"), "2027-01-01")
        BoardAutomations.applyScheduleMode("hourly", to: &s, now: now, calendar: calendar)
        XCTAssertEqual(s.int("everyHours"), 3)
        BoardAutomations.applyScheduleMode("monthly", to: &s, now: now, calendar: calendar)
        XCTAssertEqual(s.string("repeat"), "monthly")
        XCTAssertEqual(s.int("dayOfMonth"), 1)
        BoardAutomations.applyScheduleMode("weekends", to: &s, now: now, calendar: calendar)
        XCTAssertEqual(s.string("repeat"), "daily")
        XCTAssertEqual(BoardAutomations.scheduleMode(s), "weekends")
        BoardAutomations.applyScheduleMode("days", to: &s, now: now, calendar: calendar)
        XCTAssertEqual(s.ints("days"), [0, 6])
        BoardAutomations.applyScheduleMode("day", to: &s, now: now, calendar: calendar)
        XCTAssertEqual(BoardAutomations.scheduleMode(s), "day")
        BoardAutomations.applyScheduleMode("days", to: &s, now: now, calendar: calendar)
        XCTAssertEqual(s.ints("days"), [1])

        XCTAssertEqual(BoardAutomations.toggling(3, in: [1]), [1, 3])
        XCTAssertEqual(BoardAutomations.toggling(1, in: [1, 3]), [3])
        XCTAssertEqual(BoardAutomations.toggling(3, in: [3]), [3])
    }

    // MARK: - blocks, recipes, checks

    func testNewBlocksStartWhereTheDashboardsDo() {
        let context = AutomationMakeContext(firstColumnId: "todo", lastColumnId: "done", timeZone: "America/Chicago", people: [])
        let s = BoardAutomations.make(.trigger, "schedule", context: context)
        XCTAssertEqual(s.schedule?.string("timeZone"), "America/Chicago")
        XCTAssertEqual(BoardAutomations.make(.trigger, "cardMoved", context: context).string("columnId"), "done")
        XCTAssertEqual(BoardAutomations.make(.conditions, "inColumn", context: context).string("columnId"), "todo")
        XCTAssertEqual(BoardAutomations.make(.actions, "notify", context: context).bool("everyone"), true)
        let withPeople = AutomationMakeContext(firstColumnId: "todo", lastColumnId: "done", timeZone: "UTC", people: people)
        XCTAssertEqual(BoardAutomations.make(.actions, "notify", context: withPeople).bool("everyone"), false)
        XCTAssertEqual(BoardAutomations.make(.actions, "assign", context: withPeople).string("username"), "morgan")

        XCTAssertEqual(BoardAutomations.triggers.count, 7)
        XCTAssertEqual(BoardAutomations.conditions.count, 5)
        XCTAssertEqual(BoardAutomations.actions.count, 15)
        // Every block has words of its own once made.
        for kind in BoardAutomations.triggers { XCTAssertNotNil(words.triggerWords(BoardAutomations.make(.trigger, kind.type, context: context)), kind.type) }
        for kind in BoardAutomations.conditions { XCTAssertNotNil(words.conditionWords(BoardAutomations.make(.conditions, kind.type, context: context)), kind.type) }
        for kind in BoardAutomations.actions { XCTAssertNotNil(words.actionWords(BoardAutomations.make(.actions, kind.type, context: context)), kind.type) }
        XCTAssertFalse(BoardAutomations.palette(.actions, hasPeople: false).contains { $0.type == "assign" })
        XCTAssertTrue(BoardAutomations.palette(.actions, hasPeople: true).contains { $0.type == "assign" })
    }

    func testRecipes() {
        let recipes = BoardAutomations.recipes(firstColumnId: "todo", lastColumnId: "done", lastColumnName: "Done", timeZone: "America/Chicago")
        XCTAssertEqual(recipes.map(\.name), ["Daily to-do", "Weekly chore", "Done means done", "Tidy up weekly", "Due tomorrow", "Due reminder", "Bin night", "Urgent to the top"])
        XCTAssertEqual(recipes.map { words.sentence($0.rule) }, [
            "Every day at 7:00 AM, create a card “pick up eggs” in To do (unless one is still there).",
            "Every Sun at 9:00 AM, create a card “Chores for {date}” in To do (unless one is still there).",
            "When a card is moved into Done, mark it done.",
            "Every Mon at 6:00 AM, delete every finished card in Done.",
            "When a card is added to To do, make it due tomorrow.",
            "1 hour before a card is due, notify everyone “{card} is due {due}”.",
            "Every Sun at 6:00 PM, notify everyone “Take the bins out tonight”.",
            "When the “urgent” label is added to a card, move it to the top of To do.",
        ])
        XCTAssertEqual(recipes[2].rule.name, "Moved to Done → done")
        for recipe in recipes { XCTAssertEqual(BoardAutomations.problems(recipe.rule), [], recipe.name) }
    }

    func testClientSideChecks() {
        XCTAssertEqual(BoardAutomations.problems(BoardAutomation(fields: ["name": "  "])), [
            "Add a WHEN block - what starts the rule?", "Add at least one THEN block - what should happen?", "Give the rule a name.",
        ])
        XCTAssertEqual(BoardAutomations.messages(for: OrganizerError.server(status: 400, message: "Pick a list.\nThe comment is empty.")),
                       ["Pick a list.", "The comment is empty."])
        XCTAssertEqual(BoardAutomations.messages(for: OrganizerError.offline), ["Can't reach the server."])
    }

    // MARK: - the REST calls

    @MainActor
    func testTheEngineTalksToTheAutomationEndpoints() async throws {
        let engine = try OrganizerEngine(directory: nil, deviceId: "phone", policy: .computerDefault)
        let transport = ScriptedTransport { request in
            switch (request.method, request.path) {
            case (_, "/api/organizer/status"): return ScriptedTransport.status(username: nil)
            case (_, "/api/organizer/sync"): return ScriptedTransport.reply()
            case ("GET", "/api/organizer/automations"):
                return ScriptedTransport.json(["automations": [["id": "r1", "name": "Eggs", "enabled": true, "boardId": "b1",
                                                                 "trigger": ["type": "cardCompleted"], "conditions": [], "actions": [["type": "deleteCard"]]]],
                                               "timeZone": "America/Chicago", "pushReady": false, "emailReady": true])
            case ("POST", "/api/organizer/automations"):
                return ScriptedTransport.json(["errors": ["Give the rule a name.", "Pick a list for \"create a card\"."]], status: 400)
            case ("POST", "/api/organizer/automations/r1/run"):
                return ScriptedTransport.json(["automationId": "r1", "at": "2026-09-29T12:00:00.123+00:00", "ok": true, "message": "Made 1 card."])
            case ("POST", "/api/organizer/automations/preview"):
                return ScriptedTransport.json(["runs": ["2026-09-30T12:00:00+00:00", "2026-10-01T12:00:00+00:00"]])
            default: return ScriptedTransport.json([:], status: 404)
            }
        }
        engine.connect(transport)
        try? await Task.sleep(for: .milliseconds(50))

        let list = try await engine.automations(boardId: "b1")
        XCTAssertEqual(list.automations.map(\.name), ["Eggs"])
        XCTAssertFalse(list.pushReady)
        XCTAssertTrue(list.emailReady)

        do {
            try await engine.saveAutomation(BoardAutomation(fields: ["name": ""]), timeZone: "America/Chicago")
            XCTFail("expected the server's errors")
        } catch {
            XCTAssertEqual(BoardAutomations.messages(for: error), ["Give the rule a name.", "Pick a list for \"create a card\"."])
        }
        let posted = try XCTUnwrap(transport.requests.last { $0.method == "POST" && $0.path == "/api/organizer/automations" }?.body)
        let body = try JSONDecoder().decode([String: JSONValue].self, from: posted)
        XCTAssertEqual(body["timeZone"], "America/Chicago")

        let run = try await engine.runAutomation(id: "r1")
        XCTAssertTrue(run.ok)
        XCTAssertEqual(run.message, "Made 1 card.")
        let runIndex = try XCTUnwrap(transport.requests.lastIndex { $0.path == "/api/organizer/automations/r1/run" })
        XCTAssertTrue(transport.requests[(runIndex + 1)...].contains { $0.path == "/api/organizer/sync" }, "a run should be followed by a sync")

        let next = try await engine.previewSchedule(AutomationBlock(["repeat": "daily", "time": "07:00", "days": []]))
        XCTAssertEqual(next.count, 2)
        engine.disconnect()
    }
}
