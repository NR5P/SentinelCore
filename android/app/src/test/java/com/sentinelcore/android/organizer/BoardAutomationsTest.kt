package com.sentinelcore.android.organizer

import java.time.LocalDate
import java.time.format.DateTimeFormatter
import java.util.Locale
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

// The board-automation builder's logic: the plain-words sentence (a port of the dashboard's
// organizer-automations.js, which should read word for word the same), the JSON the server
// speaks, and which steps have a card to act on.
class BoardAutomationsTest {
    private val columns = mapOf("todo" to "To do", "done" to "Done", "blank" to "")
    private val people = listOf(OrganizerPerson("ann", "Ann"), OrganizerPerson("bob", null), OrganizerPerson("cy", "Cy", hasEmail = false))
    private val words = AutomationWords(
        columnTitle = { columns[it] },
        people = people,
        time = { it.format(DateTimeFormatter.ofPattern("h:mm a", Locale.US)) },
        date = { it.format(DateTimeFormatter.ofPattern("EEE, MMM d", Locale.US)) },
    )

    private fun block(vararg pairs: Pair<String, Any?>) = RuleBlock(*pairs)
    private fun schedule(vararg pairs: Pair<String, Any?>) = block("type" to "schedule", "schedule" to linkedMapOf(*pairs))

    @Test
    fun triggerWords() {
        assertEquals("Every day at 7:00 AM", words.trigger(schedule("repeat" to "daily", "time" to "07:00", "days" to emptyList<Int>())))
        assertEquals("Every weekday at 6:30 PM", words.trigger(schedule("repeat" to "daily", "time" to "18:30", "days" to listOf(5, 4, 3, 2, 1))))
        assertEquals("Every Saturday and Sunday at 9:00 AM", words.trigger(schedule("repeat" to "daily", "time" to "09:00", "days" to listOf(6, 0))))
        assertEquals("Every Sun, Wed at 9:00 AM", words.trigger(schedule("repeat" to "daily", "time" to "09:00", "days" to listOf(3, 0))))
        assertEquals("On day 15 of every month at 7:00 AM", words.trigger(schedule("repeat" to "monthly", "time" to "07:00", "dayOfMonth" to 15)))
        assertEquals("On the last day of every month at 7:00 AM", words.trigger(schedule("repeat" to "monthly", "time" to "07:00", "dayOfMonth" to -1)))
        assertEquals("On the last day of every month at 7:00 AM", words.trigger(schedule("repeat" to "monthly", "time" to "07:00")))
        assertEquals("Every 3 hours from 8:00 AM until midnight", words.trigger(schedule("repeat" to "hourly", "time" to "08:00", "everyHours" to 3)))
        assertEquals("Every 1 hour from 8:00 AM until midnight", words.trigger(schedule("repeat" to "hourly", "time" to "08:00")))
        assertEquals("Once, on Sat, Oct 3 at 7:00 AM", words.trigger(schedule("repeat" to "once", "time" to "07:00", "date" to "2026-10-03")))
        assertEquals("Once, on … at 7:00 AM", words.trigger(schedule("repeat" to "once", "time" to "")))
        assertEquals("On a schedule", words.trigger(schedule("repeat" to "yearly", "time" to "07:00")))

        assertEquals("When a card is added to any list", words.trigger(block("type" to "cardAdded", "columnId" to "")))
        assertEquals("When a card is added to To do", words.trigger(block("type" to "cardAdded", "columnId" to "todo")))
        assertEquals("When a card is moved into Done", words.trigger(block("type" to "cardMoved", "columnId" to "done")))
        assertEquals("When a card is moved into any list", words.trigger(block("type" to "cardMoved", "columnId" to null)))
        assertEquals("When a card is marked done", words.trigger(block("type" to "cardCompleted")))
        assertEquals("When every checklist item on a card is ticked", words.trigger(block("type" to "checklistCompleted")))
        assertEquals("When a card is due", words.trigger(block("type" to "cardDue", "minutesBefore" to 0, "columnId" to "")))
        assertEquals("1 hour before a card is due in To do", words.trigger(block("type" to "cardDue", "minutesBefore" to 60.0, "columnId" to "todo")))
        assertEquals("1 week before a card is due", words.trigger(block("type" to "cardDue", "minutesBefore" to 10080)))
        assertEquals("45 minutes before a card is due", words.trigger(block("type" to "cardDue", "minutesBefore" to 45)))
        assertEquals("When any label is added to a card", words.trigger(block("type" to "labelAdded", "label" to "")))
        assertEquals("When the “urgent” label is added to a card", words.trigger(block("type" to "labelAdded", "label" to "urgent")))
        assertNull(words.trigger(block("type" to "somethingNew")))
    }

    @Test
    fun conditionWords() {
        assertEquals("the title contains “asap”", words.condition(block("type" to "titleContains", "text" to "asap")))
        assertEquals("the title contains “…”", words.condition(block("type" to "titleContains", "text" to "")))
        assertEquals("it has the “red” label", words.condition(block("type" to "hasLabel", "text" to "red")))
        assertEquals("it doesn't have the “red” label", words.condition(block("type" to "lacksLabel", "text" to "red")))
        assertEquals("it's in To do", words.condition(block("type" to "inColumn", "columnId" to "todo")))
        assertEquals("it's in list", words.condition(block("type" to "inColumn", "columnId" to "blank")))
        assertEquals("it's in (pick a list)", words.condition(block("type" to "inColumn", "columnId" to "gone")))
        assertEquals("it's Mon, Tue, Wed, Thu, Fri", words.condition(block("type" to "onDays", "days" to listOf(1, 2, 3, 4, 5))))
        assertEquals("it's …", words.condition(block("type" to "onDays", "days" to emptyList<Int>())))
    }

    @Test
    fun actionWords() {
        assertEquals("create a card “eggs” in To do (unless one is still there)",
            words.action(block("type" to "createCard", "columnId" to "todo", "title" to "eggs", "skipIfExists" to true)))
        assertEquals("create a card “eggs” in To do, due tomorrow at 5:00 PM",
            words.action(block("type" to "createCard", "columnId" to "todo", "title" to "eggs", "inDays" to 1, "time" to "17:00")))
        assertEquals("create a card “…” in (pick a list), due today",
            words.action(block("type" to "createCard", "columnId" to "", "title" to "", "inDays" to 0, "time" to "")))
        assertEquals("move it to the top of Done", words.action(block("type" to "moveCard", "columnId" to "done", "position" to "top")))
        assertEquals("move it to the bottom of Done", words.action(block("type" to "moveCard", "columnId" to "done", "position" to "bottom")))
        assertEquals("label it “urgent”", words.action(block("type" to "addLabel", "name" to "urgent")))
        assertEquals("take off the “urgent” label", words.action(block("type" to "removeLabel", "name" to "urgent")))
        assertEquals("make it due in 7 days", words.action(block("type" to "setDue", "inDays" to 7, "time" to "")))
        assertEquals("clear its due date", words.action(block("type" to "setDue", "inDays" to null)))
        assertEquals("mark it done", words.action(block("type" to "complete", "done" to true)))
        assertEquals("mark it done", words.action(block("type" to "complete")))
        assertEquals("mark it not done", words.action(block("type" to "complete", "done" to false)))
        assertEquals("add some checklist items", words.action(block("type" to "addChecklist", "items" to emptyList<String>())))
        assertEquals("add 1 checklist item", words.action(block("type" to "addChecklist", "items" to listOf("a"))))
        assertEquals("add 2 checklist items", words.action(block("type" to "addChecklist", "items" to listOf("a", "b"))))
        assertEquals("untick its checklist", words.action(block("type" to "resetChecklist")))
        assertEquals("comment “done on {date}”", words.action(block("type" to "addComment", "text" to "done on {date}")))
        assertEquals("assign it to ann", words.action(block("type" to "assign", "username" to "ann")))
        assertEquals("assign it to …", words.action(block("type" to "assign", "username" to "")))
        assertEquals("delete it", words.action(block("type" to "deleteCard")))
        assertEquals("notify everyone “bins”", words.action(block("type" to "notify", "everyone" to true, "text" to "bins")))
        assertEquals("notify Ann “…”", words.action(block("type" to "notify", "everyone" to false, "people" to listOf("ann"))))
        assertEquals("notify Ann, bob and Cy “hi”", words.action(block("type" to "notify", "people" to listOf("ann", "bob", "cy"), "text" to "hi")))
        assertEquals("notify … “hi”", words.action(block("type" to "notify", "people" to emptyList<String>(), "text" to "hi")))
        assertEquals("email Ann and bob, x@y.com “hi”", words.action(block("type" to "email", "people" to listOf("ann", "bob"), "addresses" to listOf("x@y.com"), "text" to "hi")))
        assertEquals("email x@y.com “hi”", words.action(block("type" to "email", "people" to emptyList<String>(), "addresses" to listOf("x@y.com"), "text" to "hi")))
        assertEquals("email … “…”", words.action(block("type" to "email")))
        assertEquals("move every card from Done to To do", words.action(block("type" to "moveAllCards", "columnId" to "done", "toColumnId" to "todo")))
        assertEquals("move every finished card from Done to To do",
            words.action(block("type" to "moveAllCards", "columnId" to "done", "toColumnId" to "todo", "onlyCompleted" to true)))
        assertEquals("delete every finished card in Done", words.action(block("type" to "deleteAllCards", "columnId" to "done", "onlyCompleted" to true)))
        assertEquals("delete every card in Done", words.action(block("type" to "deleteAllCards", "columnId" to "done", "onlyCompleted" to false)))
        assertNull(words.action(block("type" to "teleport")))
    }

    @Test
    fun sentences() {
        val rule = AutomationRule(
            name = "Eggs",
            trigger = schedule("repeat" to "daily", "time" to "07:00", "days" to listOf(1, 2, 3, 4, 5)),
            conditions = listOf(block("type" to "onDays", "days" to listOf(1)), block("type" to "mystery")),
            actions = listOf(block("type" to "createCard", "columnId" to "todo", "title" to "eggs"), block("type" to "complete", "done" to true), block("type" to "teleport")),
        )
        assertEquals("Every weekday at 7:00 AM, if it's Mon and mystery, create a card “eggs” in To do, then mark it done, then teleport.", words.sentence(rule))
        assertEquals("When…, do….", words.sentence(AutomationRule(trigger = block("type" to ""))))
        assertEquals("When a card is marked done, do….", words.sentence(AutomationRule(trigger = block("type" to "cardCompleted"))))
        assertEquals("add a WHEN block and a THEN block to the rule to get started.", words.draftSentence(AutomationRule()))
        assertEquals("When…, delete it.", words.draftSentence(AutomationRule(actions = listOf(block("type" to "deleteCard")))))
    }

    @Test
    fun cardAvailableBefore() {
        val schedule = schedule("repeat" to "daily", "time" to "07:00")
        val moved = block("type" to "cardMoved", "columnId" to "")
        val create = block("type" to "createCard")
        val delete = block("type" to "deleteCard")
        val label = block("type" to "addLabel", "name" to "x")
        val notify = block("type" to "notify")

        // A schedule has no card until one is made.
        assertFalse(BoardAutomations.cardAvailableBefore(schedule, listOf(label), 0))
        assertTrue(BoardAutomations.cardAvailableBefore(schedule, listOf(create, label), 1))
        assertFalse(BoardAutomations.cardAvailableBefore(schedule, listOf(create, delete, label), 2))
        assertTrue(BoardAutomations.cardAvailableBefore(schedule, listOf(create, delete, create, label), 3))
        // A card rule has one from the start, until a step deletes it.
        assertTrue(BoardAutomations.cardAvailableBefore(moved, listOf(label), 0))
        assertFalse(BoardAutomations.cardAvailableBefore(moved, listOf(delete, label), 1))
        assertTrue(BoardAutomations.cardAvailableBefore(moved, listOf(delete, create, label), 2))
        // No trigger yet reads like a card rule.
        assertTrue(BoardAutomations.cardAvailableBefore(null, listOf(label), 0))

        assertEquals("A schedule or weather rule has no card of its own - put “Create a card” above this step.",
            BoardAutomations.actionWarning(schedule, listOf(label), 0))
        assertEquals("The card was deleted by an earlier step.", BoardAutomations.actionWarning(moved, listOf(delete, label), 1))
        // Send steps and board-wide steps never need a card.
        assertNull(BoardAutomations.actionWarning(schedule, listOf(notify), 0))
        assertNull(BoardAutomations.actionWarning(schedule, listOf(block("type" to "deleteAllCards")), 0))

        assertEquals("A schedule or weather rule has no card to check - this would never be true.",
            BoardAutomations.conditionWarning(schedule, block("type" to "hasLabel")))
        // A weather rule has no card either.
        val weather = block("type" to "weather", "weather" to BoardAutomations.newWeatherTest("lowNext").fields)
        assertFalse(BoardAutomations.cardAvailableBefore(weather, listOf(label), 0))
        assertTrue(BoardAutomations.cardAvailableBefore(weather, listOf(create, label), 1))
        assertEquals("A schedule or weather rule has no card to check - this would never be true.",
            BoardAutomations.conditionWarning(weather, block("type" to "inColumn")))
        assertNull(BoardAutomations.actionWarning(weather, listOf(block("type" to "createReminder")), 0))
        assertNull(BoardAutomations.conditionWarning(schedule, block("type" to "onDays")))
        assertNull(BoardAutomations.conditionWarning(moved, block("type" to "hasLabel")))
    }

    @Test
    fun jsonRoundTripKeepsNullsAndUnknowns() {
        val wire = """
            {"id":"abc","name":"Eggs","enabled":false,"boardId":"b1","timeZone":null,
             "trigger":{"type":"schedule","columnId":null,"minutesBefore":null,"label":null,
               "schedule":{"repeat":"daily","time":"07:00","days":[1,2],"dayOfMonth":null,"everyHours":null,"date":null,"timeZone":"America/Chicago"}},
             "conditions":[{"type":"onDays","text":null,"columnId":null,"days":[1]}],
             "actions":[{"type":"createCard","columnId":"c1","title":"eggs","inDays":null,"skipIfExists":true,"done":true,"future":{"a":[1,"x"]}},
                        {"type":"teleport","where":"moon"}],
             "runAs":"ann","createdBy":"ann","createdAt":"2026-09-01T12:00:00+00:00","updatedAt":"2026-09-02T12:00:00+00:00",
             "nextRunAt":"2026-09-30T12:00:00+00:00","lastRunAt":null,"lastOk":true,"lastResult":"made a card","runCount":3,"somethingNew":7}
        """.trimIndent()
        val rule = AutomationRule.from(JSONObject(wire))
        assertEquals("abc", rule.id)
        assertFalse(rule.enabled)
        assertNull(rule.timeZone)
        assertTrue(rule.isScheduled)
        assertEquals(listOf(1, 2), rule.trigger!!.schedule.ints("days"))
        assertNull(rule.actions[0].int("inDays"))
        assertEquals(java.time.Instant.parse("2026-09-30T12:00:00Z"), rule.nextRunAt)
        assertNull(rule.lastRunAt)
        assertEquals(true, rule.lastOk)
        assertEquals("made a card", rule.lastResult)

        val back = rule.toJson()
        // Everything that came in goes back out, nulls and all - JSONObject.similar compares by value.
        assertTrue(back.toString(), JSONObject(wire).similar(back))
        assertTrue(back.getJSONObject("trigger").has("columnId"))
        assertTrue(back.getJSONObject("trigger").isNull("columnId"))
        assertEquals("moon", back.getJSONArray("actions").getJSONObject(1).getString("where"))
        assertEquals(3, back.getInt("runCount"))

        // A save leaves the id out of the body.
        assertFalse(rule.toJson(includeId = false).has("id"))
        // And a second trip is the same as the first.
        assertTrue(AutomationRule.from(back).toJson().similar(back))
    }

    @Test
    fun newRuleJson() {
        val context = RuleContext(firstColumnId = "todo", lastColumnId = "done", timeZone = "America/Chicago")
        val rule = BoardAutomations.forSaving(
            AutomationRule(name = "  Eggs  ", boardId = "b1",
                trigger = BoardAutomations.make(RuleZone.TRIGGER, "schedule", context),
                actions = listOf(BoardAutomations.make(RuleZone.ACTIONS, "createCard", context)!!.with("title", "eggs"))),
            "Europe/London",
        )
        val json = rule.toJson(includeId = false)
        assertEquals("Eggs", json.getString("name"))
        assertEquals("Europe/London", json.getString("timeZone"))
        assertTrue(json.getBoolean("enabled"))
        val schedule = json.getJSONObject("trigger").getJSONObject("schedule")
        assertEquals("daily", schedule.getString("repeat"))
        assertEquals("America/Chicago", schedule.getString("timeZone"))
        assertEquals(0, schedule.getJSONArray("days").length())
        val action = json.getJSONArray("actions").getJSONObject(0)
        assertEquals("todo", action.getString("columnId"))
        assertEquals("bottom", action.getString("position"))
        assertTrue(action.getBoolean("skipIfExists"))
    }

    @Test
    fun makeEveryBlock() {
        val context = RuleContext(firstColumnId = "todo", lastColumnId = "done", people = people.take(1))
        for (zone in RuleZone.values()) {
            for (kind in BoardAutomations.kinds(zone)) {
                val made = BoardAutomations.make(zone, kind.type, context)
                assertEquals(kind.type, made?.type)
            }
        }
        assertEquals(8, BoardAutomations.TRIGGERS.size)
        assertEquals(6, BoardAutomations.CONDITIONS.size)
        assertEquals(17, BoardAutomations.ACTIONS.size)
        assertEquals("done", BoardAutomations.make(RuleZone.TRIGGER, "cardMoved", context)!!.string("columnId"))
        assertEquals("ann", BoardAutomations.make(RuleZone.ACTIONS, "assign", context)!!.string("username"))
        // With nobody to choose, a notification goes to everyone; "Assign someone" isn't offered.
        assertTrue(BoardAutomations.make(RuleZone.ACTIONS, "notify", RuleContext())!!.bool("everyone"))
        assertFalse(BoardAutomations.make(RuleZone.ACTIONS, "notify", context)!!.bool("everyone"))
        assertFalse(BoardAutomations.palette(RuleZone.ACTIONS, emptyList()).any { it.type == "assign" })
        assertTrue(BoardAutomations.palette(RuleZone.ACTIONS, people).any { it.type == "assign" })
    }

    @Test
    fun scheduleModes() {
        val base = RuleBlock("repeat" to "daily", "time" to "07:00", "days" to emptyList<Int>())
        val today = LocalDate.of(2026, 9, 29)
        assertEquals("day", BoardAutomations.scheduleMode(base))
        val weekdays = BoardAutomations.applyScheduleMode(base, "weekdays", today)
        assertEquals("weekdays", BoardAutomations.scheduleMode(weekdays))
        assertEquals("weekends", BoardAutomations.scheduleMode(BoardAutomations.applyScheduleMode(base, "weekends", today)))
        val days = BoardAutomations.applyScheduleMode(base, "days", today)
        assertEquals(listOf(1), days.ints("days"))
        assertEquals("weekdays", BoardAutomations.scheduleMode(BoardAutomations.applyScheduleMode(weekdays, "days", today)))
        val monthly = BoardAutomations.applyScheduleMode(weekdays, "monthly", today)
        assertEquals("monthly", monthly.string("repeat"))
        assertEquals(1, monthly.int("dayOfMonth"))
        assertTrue(monthly.ints("days").isEmpty())
        val hourly = BoardAutomations.applyScheduleMode(base, "hourly", today)
        assertEquals(3, hourly.int("everyHours"))
        val once = BoardAutomations.applyScheduleMode(base, "once", today)
        assertEquals("2026-09-30", once.string("date"))
        assertEquals("once", BoardAutomations.scheduleMode(once))
        // The zone the schedule was made with stays.
        assertEquals("X/Y", BoardAutomations.applyScheduleMode(base.with("timeZone", "X/Y"), "weekends", today).string("timeZone"))

        assertEquals(listOf(1, 3), BoardAutomations.toggleDay(listOf(3), 1))
        assertEquals(listOf(3), BoardAutomations.toggleDay(listOf(1, 3), 1))
        assertNull(BoardAutomations.toggleDay(listOf(1), 1))
    }

    @Test
    fun recipesAndChecks() {
        val context = RuleContext(firstColumnId = "todo", lastColumnId = "done", timeZone = "America/Chicago")
        val recipes = BoardAutomations.recipes(context, words)
        assertEquals(8, recipes.size)
        assertEquals("Moved to Done → done", recipes[2].rule.name)
        assertEquals("Clear Done on Mondays", recipes[3].rule.name)
        assertEquals("Every Sun at 9:00 AM, create a card “Chores for {date}” in To do (unless one is still there).", words.sentence(recipes[1].rule))
        assertEquals("1 hour before a card is due, notify everyone “{card} is due {due}”.", words.sentence(recipes[5].rule))
        assertEquals("America/Chicago", recipes[0].rule.trigger!!.schedule.string("timeZone"))
        for (recipe in recipes) assertTrue(recipe.name, BoardAutomations.localErrors(recipe.rule).isEmpty())

        assertEquals(listOf("Add a WHEN block - what starts the rule?", "Add at least one THEN block - what should happen?", "Give the rule a name."),
            BoardAutomations.localErrors(AutomationRule(name = "  ")))
        assertEquals(listOf("b", "c", "a"), BoardAutomations.moved(listOf("a", "b", "c"), 0, 2))
        assertEquals(listOf("c", "a", "b"), BoardAutomations.moved(listOf("a", "b", "c"), 2, 0))
    }

    // The client against a fake server: the paths, the bodies, and a 400's reasons.
    @Test
    fun clientSpeaksTheServersShapes() = runTest {
        val calls = mutableListOf<Triple<String, String, String?>>()
        var reply: OrganizerResponse = OrganizerResponse(200, "{}".toByteArray())
        val client = BoardAutomationClient { method, path, query, body ->
            calls += Triple(method, path + (if (query.isEmpty()) "" else "?" + query.entries.joinToString("&") { "${it.key}=${it.value}" }), body?.let { String(it) })
            reply
        }
        reply = OrganizerResponse(200, """{"automations":[{"id":"r1","name":"A","boardId":"b","trigger":{"type":"cardCompleted"},"conditions":[],"actions":[]}],"timeZone":"UTC","pushReady":false}""".toByteArray())
        val list = client.list("b 1")
        assertEquals("GET" to "/api/organizer/automations?boardId=b 1", calls.last().first to calls.last().second)
        assertEquals("r1", list.rules.single().id)
        assertFalse(list.pushReady)
        assertTrue(list.emailReady)

        reply = OrganizerResponse(200, """{"id":"new","name":"A","boardId":"b"}""".toByteArray())
        client.save(AutomationRule(name = "A", boardId = "b"))
        assertEquals("POST", calls.last().first)
        assertFalse(JSONObject(calls.last().third!!).has("id"))
        client.save(AutomationRule(id = "r1", name = "A", boardId = "b"))
        assertEquals("PUT" to "/api/organizer/automations/r1", calls.last().first to calls.last().second)

        reply = OrganizerResponse(200, """{"automationId":"r1","at":"2026-09-29T12:00:00Z","ok":true,"message":"Made 1 card."}""".toByteArray())
        val run = client.run("r1")
        assertEquals("/api/organizer/automations/r1/run", calls.last().second)
        assertEquals("Made 1 card.", run.message)
        assertTrue(run.ok)

        reply = OrganizerResponse(200, """{"runs":["2026-09-30T12:00:00+00:00","2026-10-01T12:00:00+00:00"]}""".toByteArray())
        assertEquals(2, client.preview(RuleBlock("repeat" to "daily", "time" to "07:00")).size)
        assertEquals("07:00", JSONObject(calls.last().third!!).getString("time"))

        assertEquals(listOf("Give the rule a name.", "Pick a list for \"move the card\"."),
            BoardAutomationClient.errors(OrganizerException.Server(400, "Give the rule a name.\nPick a list for \"move the card\".")))
        assertEquals(listOf(BoardAutomations.OFFLINE), BoardAutomationClient.errors(OrganizerException.Offline()))
    }

    // ---- reminders-and-calendar ("planner") rules and the weather ------------------------------

    private fun test(metric: String, op: String, value: Number, window: Int) =
        block("type" to "weather", "metric" to metric, "op" to op, "value" to value, "window" to window)

    @Test
    fun weatherWordsForEveryMetric() {
        assertEquals("1 inch of rain or less has fallen in the last 7 days", words.weatherWords(test("rainPast", "atMost", 1, 7)))
        assertEquals("1 inch of rain or less has fallen in the last 7 days", words.weatherWords(test("rainPast", "atMost", 1.0, 7)))
        assertEquals("0.5 inches of rain or more has fallen today", words.weatherWords(test("rainPast", "atLeast", 0.5, 1)))
        assertEquals("0.5 inches of rain or more is forecast in the next 2 days", words.weatherWords(test("rainNext", "atLeast", 0.5, 2)))
        assertEquals("1.2 inches of rain or less is forecast in the next day", words.weatherWords(test("rainNext", "atMost", 1.2, 1)))
        assertEquals("2 inches of snow or more is forecast in the next 3 days", words.weatherWords(test("snowNext", "atLeast", 2.0, 3)))
        assertEquals("it gets down to 32°F or colder in the next 7 days", words.weatherWords(test("lowNext", "atMost", 32, 7)))
        assertEquals("it stays at 40°F or warmer in the next day", words.weatherWords(test("lowNext", "atLeast", 40, 1)))
        assertEquals("it stays at 80°F or cooler in the next 5 days", words.weatherWords(test("highNext", "atMost", 80, 5)))
        assertEquals("it gets up to 95°F or hotter in the next 3 days", words.weatherWords(test("highNext", "atLeast", 95.0, 3)))
        assertEquals("the chance of rain stays at 20% or less in the next day", words.weatherWords(test("rainChanceNext", "atMost", 20, 1)))
        assertEquals("there's a 60% chance of rain or more in the next day", words.weatherWords(test("rainChanceNext", "atLeast", 60, 1)))
        assertEquals("it's 32°F or colder right now", words.weatherWords(test("tempNow", "atMost", 32, 1)))
        assertEquals("it's -5°F or warmer right now", words.weatherWords(test("tempNow", "atLeast", -5, 1)))
        assertEquals("the weather…", words.weatherWords(test("hail", "atLeast", 1, 1)))
        // No number yet reads as 0; no window as one day.
        assertEquals("0 inches of rain or more is forecast in the next day", words.weatherWords(block("type" to "weather", "metric" to "rainNext")))

        assertEquals("When it gets down to 32°F or colder in the next 7 days",
            words.trigger(block("type" to "weather", "weather" to test("lowNext", "atMost", 32, 7).fields)))
        assertEquals("1 inch of rain or less has fallen in the last 7 days", words.condition(test("rainPast", "atMost", 1, 7)))
        assertEquals("1", jsNumber(1.0))
        assertEquals("0.5", jsNumber(0.5))
        assertEquals("-3", jsNumber(-3.0))
    }

    @Test
    fun reminderAndEventWords() {
        fun reminder(vararg pairs: Pair<String, Any?>) = block("type" to "createReminder", *pairs)
        assertEquals("", words.reminderWhen(reminder("inDays" to null)))
        assertEquals("today", words.reminderWhen(reminder("inDays" to 0, "time" to "")))
        assertEquals("tomorrow at 7:00 AM", words.reminderWhen(reminder("inDays" to 1, "time" to "07:00")))
        assertEquals("on the day of the weather", words.reminderWhen(reminder("inDays" to 0, "fromWeatherDay" to true)))
        assertEquals("the day before the weather", words.reminderWhen(reminder("inDays" to -1.0, "fromWeatherDay" to true)))
        assertEquals("2 days before the weather at 6:00 PM", words.reminderWhen(reminder("inDays" to -2, "fromWeatherDay" to true, "time" to "18:00")))
        assertEquals("1 days after the weather", words.reminderWhen(reminder("inDays" to 1, "fromWeatherDay" to true)))

        assertEquals("add the reminder “Water the plants”, due today (unless it's still there)",
            words.action(reminder("title" to "Water the plants", "inDays" to 0, "time" to "", "skipIfExists" to true)))
        assertEquals("add the reminder “…”", words.action(reminder("title" to "", "inDays" to null)))
        assertEquals("add the reminder “Check the pipes”, due the day before the weather",
            words.action(reminder("title" to "Check the pipes", "inDays" to -1, "fromWeatherDay" to true)))
        assertEquals("put “Bin day” on the calendar all day tomorrow",
            words.action(block("type" to "createEvent", "title" to "Bin day", "inDays" to 1, "time" to "")))
        assertEquals("put “Dentist” on the calendar in 3 days at 9:30 AM",
            words.action(block("type" to "createEvent", "title" to "Dentist", "inDays" to 3, "time" to "09:30")))
        // An event with no day yet is today.
        assertEquals("put “…” on the calendar all day today", words.action(block("type" to "createEvent")))
        assertEquals("put “Cover the plants” on the calendar all day on the day of the weather",
            words.action(block("type" to "createEvent", "title" to "Cover the plants", "inDays" to 0, "fromWeatherDay" to true)))
    }

    @Test
    fun weatherAndPlannerBlocks() {
        val context = RuleContext(timeZone = "America/Chicago")
        val weatherTrigger = BoardAutomations.make(RuleZone.TRIGGER, "weather", context)!!
        assertEquals(mapOf("type" to "weather", "metric" to "lowNext", "op" to "atMost", "value" to 32, "window" to 7), weatherTrigger.weather.fields)
        assertEquals(mapOf("type" to "weather", "metric" to "rainPast", "op" to "atMost", "value" to 1, "window" to 7),
            BoardAutomations.make(RuleZone.CONDITIONS, "weather", context)!!.fields)
        // A reminder added to a weather rule counts from the weather's day; on a schedule it doesn't.
        assertTrue(BoardAutomations.make(RuleZone.ACTIONS, "createReminder", context, weatherTrigger)!!.bool("fromWeatherDay"))
        val reminder = BoardAutomations.make(RuleZone.ACTIONS, "createReminder", context)!!
        assertFalse(reminder.bool("fromWeatherDay"))
        assertEquals(0, reminder.int("inDays"))
        assertTrue(reminder.bool("skipIfExists"))
        assertTrue(reminder.fields.containsKey("alertMinutes"))
        val event = BoardAutomations.make(RuleZone.ACTIONS, "createEvent", context, weatherTrigger)!!
        assertEquals(60, event.int("durationMinutes"))
        assertFalse(event.bool("fromWeatherDay"))

        // A new measure starts from its own numbers; the rest of the block stays.
        val changed = BoardAutomations.withMetric(test("lowNext", "atMost", 28, 3).with("extra", "kept"), "rainChanceNext")
        assertEquals("atLeast", changed.string("op"))
        assertEquals(60, changed.int("value"))
        assertEquals(1, changed.int("window"))
        assertEquals("kept", changed.string("extra"))
        val past = BoardAutomations.metric("rainPast")!!
        assertEquals("today", past.windowLabel(1))
        assertEquals("the last 14 days", past.windowLabel(14))
        assertEquals("the next day", BoardAutomations.metric("snowNext")!!.windowLabel(1))
        assertEquals("the next 5 days", BoardAutomations.metric("snowNext")!!.windowLabel(5))
        assertTrue(BoardAutomations.metric("tempNow")!!.source.startsWith("From the nearest weather station"))
        assertEquals(7, BoardAutomations.METRICS.size)

        assertTrue(BoardAutomations.weatherInRule(weatherTrigger, emptyList()))
        assertTrue(BoardAutomations.weatherInRule(null, listOf(test("rainPast", "atMost", 1, 7))))
        assertFalse(BoardAutomations.weatherInRule(block("type" to "schedule"), listOf(block("type" to "onDays"))))
        assertTrue(AutomationRule(trigger = weatherTrigger).startsWithoutCard)
        assertFalse(AutomationRule(trigger = block("type" to "cardMoved")).startsWithoutCard)
    }

    @Test
    fun plannerPalette() {
        fun types(zone: RuleZone) = BoardAutomations.palette(zone, people, planner = true).map { it.type }
        assertEquals(listOf("schedule", "weather"), types(RuleZone.TRIGGER))
        assertEquals(listOf("onDays", "weather"), types(RuleZone.CONDITIONS))
        assertEquals(listOf("createReminder", "createEvent", "notify", "email"), types(RuleZone.ACTIONS))
        // A board offers the weather and the reminder/event blocks too.
        val board = BoardAutomations.palette(RuleZone.ACTIONS, people).map { it.type }
        assertTrue(board.containsAll(listOf("createReminder", "createEvent", "createCard")))
        assertTrue(BoardAutomations.palette(RuleZone.TRIGGER, people).any { it.type == "weather" })
        assertTrue(BoardAutomations.palette(RuleZone.CONDITIONS, people).any { it.type == "weather" })

        assertEquals(listOf("{date}", "{weekday}", "{month}", "{weather}", "{weatherDay}"), BoardAutomations.tokens(planner = true).map { it.first })
        assertEquals(8, BoardAutomations.tokens(planner = false).size)
        assertEquals(listOf("title", "description"), BoardAutomations.TOKEN_FIELDS["createReminder"])
        assertEquals(listOf("title", "description"), BoardAutomations.TOKEN_FIELDS["createEvent"])
    }

    private val me = OrganizerSpace("personal:ann", OrganizerSpaceKind.PERSONAL, "Ann")
    private val admins = OrganizerSpace("group:admins", OrganizerSpaceKind.GROUP, "Admins")
    private val garden = OrganizerSpace("group:garden", OrganizerSpaceKind.GROUP, "Garden club")

    @Test
    fun plannerDraftAndSpaces() {
        val spaces = listOf(me, OrganizerSpace.FAMILY, admins)
        // A planner rule keeps a space this person can see, else it's for everyone.
        assertEquals("group:admins", BoardAutomations.draft(AutomationRule(spaceId = "group:admins"), null, spaces).spaceId)
        assertEquals("family", BoardAutomations.draft(AutomationRule(spaceId = "group:gone"), null, spaces).spaceId)
        assertEquals("family", BoardAutomations.draft(AutomationRule(), null, spaces).spaceId)
        assertEquals("family", BoardAutomations.draft(AutomationRule(spaceId = "group:admins"), null, emptyList()).spaceId)
        val planner = BoardAutomations.draft(AutomationRule(id = "r1", boardId = "stale", spaceId = "personal:ann"), null, spaces)
        assertEquals("", planner.boardId)
        assertEquals("r1", planner.id)
        assertTrue(planner.isPlanner)
        // A board rule has no space.
        val board = BoardAutomations.draft(AutomationRule(spaceId = "family"), "b1", spaces)
        assertEquals("b1", board.boardId)
        assertNull(board.spaceId)
        assertFalse(board.toJson().has("spaceId"))
        assertEquals("personal:ann", planner.toJson(includeId = false).getString("spaceId"))
        assertEquals("", planner.toJson().getString("boardId"))

        assertEquals("just me", BoardAutomations.spaceWords(me))
        assertEquals("everyone", BoardAutomations.spaceWords(OrganizerSpace.FAMILY))
        assertEquals("the admins", BoardAutomations.spaceWords(admins))
        assertEquals("Garden club", BoardAutomations.spaceWords(garden))
        assertEquals("…", BoardAutomations.spaceWords(null))
        assertEquals("admins", BoardAutomations.audienceName(admins))
        assertEquals("just me", BoardAutomations.audienceName(me))
        assertEquals(listOf(OrganizerSpace.FAMILY), BoardAutomations.plannerSpaces(emptyList()))
    }

    @Test
    fun plannerProblems() {
        val rule = AutomationRule(name = "Water", spaceId = "", trigger = block("type" to "schedule"), actions = listOf(block("type" to "createReminder")))
        assertEquals(listOf(BoardAutomations.NEEDS_SPACE), BoardAutomations.localErrors(rule, planner = true))
        assertTrue(BoardAutomations.localErrors(rule).isEmpty())
        // After the WHEN one, before the THEN one.
        assertEquals(listOf("Add a WHEN block - what starts the rule?", "Pick who the reminders and events are for.",
            "Add at least one THEN block - what should happen?", "Give the rule a name."),
            BoardAutomations.localErrors(AutomationRule(), planner = true))
    }

    @Test
    fun plannerRecipes() {
        val recipes = BoardAutomations.plannerRecipes(listOf(me, OrganizerSpace.FAMILY, admins), "America/Chicago")
        assertEquals(listOf("Water the plants", "Freeze coming", "Rain tomorrow", "Heat wave", "Snow coming", "Bin day"), recipes.map { it.name })
        for (recipe in recipes) assertTrue(recipe.name, BoardAutomations.localErrors(recipe.rule, planner = true).isEmpty())
        assertEquals(listOf("family", "group:admins", "family", "family", "family", "family"), recipes.map { it.rule.spaceId })
        // With no Admins space the freeze reminder is for everyone.
        assertEquals("family", BoardAutomations.plannerRecipes(listOf(me, OrganizerSpace.FAMILY), "UTC")[1].rule.spaceId)

        assertEquals("Every Sat at 8:00 AM, if 1 inch of rain or less has fallen in the last 7 days, add the reminder “Water the plants”, due today (unless it's still there).",
            words.sentence(recipes[0].rule))
        assertEquals("When it gets down to 32°F or colder in the next 7 days, add the reminder “Check the pipes - {weather} {weatherDay}”, due the day before the weather (unless it's still there), " +
            "then notify everyone “Down to {weather} {weatherDay} - drip the taps and cover the plants.”.", words.sentence(recipes[1].rule))
        assertEquals("When there's a 70% chance of rain or more in the next day, add the reminder “Bring the patio cushions in”, due today (unless it's still there).",
            words.sentence(recipes[2].rule))
        assertEquals("When it gets up to 95°F or hotter in the next 3 days, add the reminder “Hot {weatherDay} ({weather}) - water the garden early”, due on the day of the weather at 7:00 AM (unless it's still there).",
            words.sentence(recipes[3].rule))
        assertEquals("When 2 inches of snow or more is forecast in the next 3 days, add the reminder “Salt the steps - {weather} of snow {weatherDay}”, due the day before the weather (unless it's still there).",
            words.sentence(recipes[4].rule))
        assertEquals("Every Sun at 6:00 AM, put “Bin day” on the calendar all day tomorrow.", words.sentence(recipes[5].rule))
        assertEquals("America/Chicago", recipes[5].rule.trigger!!.schedule.string("timeZone"))
        assertEquals(3, recipes[1].rule.actions[0].int("priority"))
    }

    @Test
    fun plannerListAndJson() = runTest {
        val calls = mutableListOf<String>()
        val client = BoardAutomationClient { method, path, query, _ ->
            calls += "$method $path?" + query.entries.joinToString("&") { "${it.key}=${it.value}" }
            OrganizerResponse(200, """{"automations":[{"id":"p1","name":"Water","boardId":"","spaceId":"family","trigger":{"type":"weather","weather":{"type":"weather","metric":"lowNext","op":"atMost","value":32.0,"window":7}},"conditions":[],"actions":[],"weatherHeld":false}]}""".toByteArray())
        }
        val list = client.list(null)
        assertEquals("GET /api/organizer/automations?planner=true", calls.last())
        val rule = list.rules.single()
        assertTrue(rule.isPlanner)
        assertEquals("family", rule.spaceId)
        assertEquals("When it gets down to 32°F or colder in the next 7 days, do….", words.sentence(rule))
        // The engine's weather bookkeeping rides along untouched.
        assertEquals(false, rule.toJson().getBoolean("weatherHeld"))
        client.list("b1")
        assertEquals("GET /api/organizer/automations?boardId=b1", calls.last())
    }
}
