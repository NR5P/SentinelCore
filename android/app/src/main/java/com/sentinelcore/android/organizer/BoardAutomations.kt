package com.sentinelcore.android.organizer

import java.net.URLEncoder
import java.time.Instant
import java.time.LocalDate
import java.time.LocalTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.util.Locale
import org.json.JSONArray
import org.json.JSONObject

// Board automations: "every morning at 7, put 'pick up eggs' in New items", "when a card is moved
// into Done, mark it complete". The rules run on the server (OrganizerAutomationEngine), so a
// scheduled card appears at 7 with every phone asleep; this is the phone's builder for them. The
// dashboard's organizer-automations.js is the reference - the wording here is a port of its
// words() functions and should read the same, word for word.
//
// Rules are not Organizer items and never sync: they are plain REST, and need the server.
//
// Every block (the trigger, each condition, each action) is the server's loose bag of properties,
// kept as a map rather than a class per block type - so a field or a whole block this phone
// doesn't know about survives an edit and a save untouched.

data class RuleBlock(val fields: Map<String, Any?>) {
    constructor(vararg pairs: Pair<String, Any?>) : this(linkedMapOf(*pairs))

    val type: String get() = fields["type"] as? String ?: ""

    fun string(key: String): String? = fields[key] as? String

    // JSON numbers come back as doubles; a new block holds ints.
    fun int(key: String): Int? = (fields[key] as? Number)?.toInt()

    fun bool(key: String, default: Boolean = false): Boolean = fields[key] as? Boolean ?: default

    fun strings(key: String): List<String> = (fields[key] as? List<*>)?.mapNotNull { it as? String } ?: emptyList()

    fun ints(key: String): List<Int> = (fields[key] as? List<*>)?.mapNotNull { (it as? Number)?.toInt() } ?: emptyList()

    fun with(key: String, value: Any?): RuleBlock = RuleBlock(LinkedHashMap(fields).apply { put(key, value) })

    // The schedule inside a schedule trigger.
    @Suppress("UNCHECKED_CAST")
    val schedule: RuleBlock get() = RuleBlock((fields["schedule"] as? Map<String, Any?>) ?: emptyMap())

    fun withSchedule(schedule: RuleBlock): RuleBlock = with("schedule", schedule.fields)

    fun toJson(): JSONObject = OrganizerJson.toJson(fields) as JSONObject

    companion object {
        fun from(json: JSONObject): RuleBlock = RuleBlock(OrganizerJson.map(json))
    }
}

// One rule, in the server's shape. What the engine keeps (next run, last result, who made it)
// rides along in `extra` untouched, and so does anything a newer server adds.
data class AutomationRule(
    val id: String? = null,
    val name: String = "",
    val enabled: Boolean = true,
    val boardId: String = "",
    val timeZone: String? = null,
    val trigger: RuleBlock? = null,
    val conditions: List<RuleBlock> = emptyList(),
    val actions: List<RuleBlock> = emptyList(),
    val extra: Map<String, Any?> = emptyMap(),
) {
    val isScheduled: Boolean get() = trigger?.type == "schedule"
    val nextRunAt: Instant? get() = OrganizerJson.instant(extra["nextRunAt"] as? String)
    val lastRunAt: Instant? get() = OrganizerJson.instant(extra["lastRunAt"] as? String)
    val lastOk: Boolean? get() = extra["lastOk"] as? Boolean
    val lastResult: String? get() = extra["lastResult"] as? String

    fun toJson(includeId: Boolean = true): JSONObject {
        val json = OrganizerJson.toJson(extra) as JSONObject
        if (includeId) json.put("id", id ?: JSONObject.NULL) else json.remove("id")
        json.put("name", name)
        json.put("enabled", enabled)
        json.put("boardId", boardId)
        json.put("timeZone", timeZone ?: JSONObject.NULL)
        json.put("trigger", trigger?.toJson() ?: JSONObject.NULL)
        json.put("conditions", JSONArray(conditions.map { it.toJson() }))
        json.put("actions", JSONArray(actions.map { it.toJson() }))
        return json
    }

    companion object {
        private val known = setOf("id", "name", "enabled", "boardId", "timeZone", "trigger", "conditions", "actions")

        fun from(json: JSONObject): AutomationRule {
            fun blocks(name: String): List<RuleBlock> {
                val array = json.optJSONArray(name) ?: return emptyList()
                return (0 until array.length()).mapNotNull { array.optJSONObject(it)?.let(RuleBlock::from) }
            }
            return AutomationRule(
                id = json.optString("id").takeIf { it.isNotEmpty() && !json.isNull("id") },
                name = json.optString("name"),
                enabled = json.optBoolean("enabled", true),
                boardId = json.optString("boardId"),
                timeZone = if (json.isNull("timeZone")) null else json.optString("timeZone").takeIf { it.isNotEmpty() },
                trigger = json.optJSONObject("trigger")?.let(RuleBlock::from),
                conditions = blocks("conditions"),
                actions = blocks("actions"),
                extra = OrganizerJson.map(json).filterKeys { it !in known },
            )
        }
    }
}

data class AutomationRun(val automationId: String, val at: Instant?, val ok: Boolean, val message: String) {
    companion object {
        fun from(json: JSONObject) = AutomationRun(
            automationId = json.optString("automationId"),
            at = OrganizerJson.instant(json.optString("at")),
            ok = json.optBoolean("ok"),
            message = json.optString("message"),
        )
    }
}

// A board's rules, and whether the send blocks can work on this server.
data class AutomationList(val rules: List<AutomationRule>, val timeZone: String?, val pushReady: Boolean, val emailReady: Boolean)

// /api/organizer/automations. `send` is the engine's, so it shares the signed-in transport and
// throws OrganizerException - a 400's errors[] arrive as the Server exception's message, one per line.
class BoardAutomationClient(
    private val send: suspend (method: String, path: String, query: Map<String, String>, body: ByteArray?) -> OrganizerResponse,
) {
    private fun path(id: String, tail: String = "") = "/api/organizer/automations/${URLEncoder.encode(id, "UTF-8")}$tail"

    suspend fun list(boardId: String): AutomationList {
        val json = JSONObject(send("GET", "/api/organizer/automations", mapOf("boardId" to boardId), null).text)
        val array = json.optJSONArray("automations") ?: JSONArray()
        return AutomationList(
            rules = (0 until array.length()).map { AutomationRule.from(array.getJSONObject(it)) },
            timeZone = json.optString("timeZone").takeIf { it.isNotEmpty() && !json.isNull("timeZone") },
            // Missing means an older server that doesn't say - don't nag.
            pushReady = json.optBoolean("pushReady", true),
            emailReady = json.optBoolean("emailReady", true),
        )
    }

    // A new rule when it has no id yet, else a change to it. Returns what the server kept.
    suspend fun save(rule: AutomationRule): AutomationRule {
        val body = rule.toJson(includeId = false).toString().toByteArray()
        val response = if (rule.id == null) send("POST", "/api/organizer/automations", emptyMap(), body) else send("PUT", path(rule.id), emptyMap(), body)
        return AutomationRule.from(JSONObject(response.text))
    }

    suspend fun delete(id: String) {
        send("DELETE", path(id), emptyMap(), null)
    }

    suspend fun run(id: String): AutomationRun = AutomationRun.from(JSONObject(send("POST", path(id, "/run"), emptyMap(), null).text))

    suspend fun runs(id: String): List<AutomationRun> {
        val array = JSONObject(send("GET", path(id, "/runs"), emptyMap(), null).text).optJSONArray("runs") ?: return emptyList()
        return (0 until array.length()).map { AutomationRun.from(array.getJSONObject(it)) }
    }

    // When a schedule would next run, worked out by the server so it matches what will happen.
    suspend fun preview(schedule: RuleBlock): List<Instant> {
        val array = JSONObject(send("POST", "/api/organizer/automations/preview", emptyMap(), schedule.toJson().toString().toByteArray()).text)
            .optJSONArray("runs") ?: return emptyList()
        return (0 until array.length()).mapNotNull { OrganizerJson.instant(array.optString(it)) }
    }

    companion object {
        // A save the server refused: its reasons, one per line.
        fun errors(error: Throwable): List<String> = when (error) {
            is OrganizerException.Server -> (error.message ?: "").split("\n").filter { it.isNotBlank() }
                .ifEmpty { listOf("The server said ${error.status}.") }
            is OrganizerException.SignInRequired, is OrganizerException.TurnedOff -> listOf(error.message ?: "")
            else -> listOf(BoardAutomations.OFFLINE)
        }
    }
}

// ---- the blocks --------------------------------------------------------------------------------

enum class RuleZone { TRIGGER, CONDITIONS, ACTIONS }

// What a block looks like in the palette. `needsCard` blocks act on "the card".
data class RuleBlockKind(
    val zone: RuleZone,
    val type: String,
    val icon: String,
    val label: String,
    val hint: String? = null,
    val needsCard: Boolean = false,
    val needsPeople: Boolean = false,
)

// What a new block starts from: this board's first and last list, who there is, the phone's zone.
data class RuleContext(
    val firstColumnId: String = "",
    val lastColumnId: String = "",
    val people: List<OrganizerPerson> = emptyList(),
    val timeZone: String = ZoneId.systemDefault().id,
)

object BoardAutomations {
    const val OFFLINE = "Automations need the server - they can't be changed offline."
    const val DELETE_NOTE = "Cards it already made stay where they are."

    val DAY_SHORT = listOf("Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat")
    val TOKENS = listOf("{date}" to "today's date", "{weekday}" to "day of the week", "{month}" to "month",
        "{card}" to "the card's title", "{due}" to "the card's due date", "{board}" to "this board's name")
    val DUE_OFFSETS = listOf(0 to "at the time it's due", 15 to "15 minutes before", 60 to "1 hour before", 180 to "3 hours before",
        1440 to "1 day before", 2880 to "2 days before", 10080 to "1 week before")
    val DUE_DAYS = listOf(0 to "today", 1 to "tomorrow", 2 to "in 2 days", 3 to "in 3 days", 7 to "in a week", 14 to "in 2 weeks", 30 to "in 30 days")
    val SCHEDULE_MODES = listOf("day" to "every day", "weekdays" to "every weekday", "weekends" to "every weekend day",
        "days" to "on certain days", "monthly" to "every month", "hourly" to "every few hours", "once" to "just once")
    val EVERY_HOURS = listOf(1, 2, 3, 4, 6, 8, 12)

    // Fields a {token} can go into, per action type; the first is where a token goes when no
    // field has been touched yet.
    val TOKEN_FIELDS = mapOf("createCard" to listOf("title"), "addComment" to listOf("text"),
        "notify" to listOf("text", "title"), "email" to listOf("text", "title"))

    val TRIGGERS = listOf(
        RuleBlockKind(RuleZone.TRIGGER, "schedule", "⏰", "On a schedule", "every morning, every Monday, once…"),
        RuleBlockKind(RuleZone.TRIGGER, "cardAdded", "＋", "When a card is added", "to a list, or anywhere on the board"),
        RuleBlockKind(RuleZone.TRIGGER, "cardMoved", "⇢", "When a card is moved", "into a particular list"),
        RuleBlockKind(RuleZone.TRIGGER, "cardCompleted", "✓", "When a card is marked done", "ticked off anywhere on the board"),
        RuleBlockKind(RuleZone.TRIGGER, "checklistCompleted", "☑", "When a checklist is finished", "every item on a card ticked"),
        RuleBlockKind(RuleZone.TRIGGER, "cardDue", "⏳", "When a card is due", "or a while before - a reminder"),
        RuleBlockKind(RuleZone.TRIGGER, "labelAdded", "🏷", "When a label is added", "a particular label, or any"),
    )

    val CONDITIONS = listOf(
        RuleBlockKind(RuleZone.CONDITIONS, "titleContains", "Aa", "Title contains…", needsCard = true),
        RuleBlockKind(RuleZone.CONDITIONS, "hasLabel", "🏷", "Has a label", needsCard = true),
        RuleBlockKind(RuleZone.CONDITIONS, "lacksLabel", "⊘", "Doesn't have a label", needsCard = true),
        RuleBlockKind(RuleZone.CONDITIONS, "inColumn", "▤", "Is in a list", needsCard = true),
        RuleBlockKind(RuleZone.CONDITIONS, "onDays", "📅", "Only on certain days"),
    )

    val ACTIONS = listOf(
        RuleBlockKind(RuleZone.ACTIONS, "createCard", "＋", "Create a card", "then the steps below act on it"),
        RuleBlockKind(RuleZone.ACTIONS, "moveCard", "⇢", "Move the card", needsCard = true),
        RuleBlockKind(RuleZone.ACTIONS, "addLabel", "🏷", "Add a label", needsCard = true),
        RuleBlockKind(RuleZone.ACTIONS, "removeLabel", "⊘", "Remove a label", needsCard = true),
        RuleBlockKind(RuleZone.ACTIONS, "setDue", "🕑", "Set the due date", needsCard = true),
        RuleBlockKind(RuleZone.ACTIONS, "complete", "✓", "Mark done / not done", needsCard = true),
        RuleBlockKind(RuleZone.ACTIONS, "addChecklist", "☑", "Add checklist items", needsCard = true),
        RuleBlockKind(RuleZone.ACTIONS, "resetChecklist", "↺", "Untick the checklist", needsCard = true),
        RuleBlockKind(RuleZone.ACTIONS, "addComment", "💬", "Add a comment", needsCard = true),
        RuleBlockKind(RuleZone.ACTIONS, "assign", "👤", "Assign someone", needsCard = true, needsPeople = true),
        RuleBlockKind(RuleZone.ACTIONS, "deleteCard", "🗑", "Delete the card", needsCard = true),
        RuleBlockKind(RuleZone.ACTIONS, "notify", "📣", "Send a notification", "to phones - everyone's, or chosen people"),
        RuleBlockKind(RuleZone.ACTIONS, "email", "✉", "Send an email", "to people here, or any address"),
        RuleBlockKind(RuleZone.ACTIONS, "moveAllCards", "⇶", "Move every card in a list"),
        RuleBlockKind(RuleZone.ACTIONS, "deleteAllCards", "🧹", "Clear out a list"),
    )

    fun kinds(zone: RuleZone): List<RuleBlockKind> = when (zone) {
        RuleZone.TRIGGER -> TRIGGERS
        RuleZone.CONDITIONS -> CONDITIONS
        RuleZone.ACTIONS -> ACTIONS
    }

    fun kind(zone: RuleZone, type: String): RuleBlockKind? = kinds(zone).firstOrNull { it.type == type }

    // The palette: "Assign someone" only when there's someone to assign.
    fun palette(zone: RuleZone, people: List<OrganizerPerson>): List<RuleBlockKind> =
        kinds(zone).filter { !it.needsPeople || people.isNotEmpty() }

    // A fresh block of a type, as the dashboard's make() builds it.
    fun make(zone: RuleZone, type: String, context: RuleContext): RuleBlock? {
        val first = context.firstColumnId
        val last = context.lastColumnId
        return when (zone) {
            RuleZone.TRIGGER -> when (type) {
                "schedule" -> RuleBlock("type" to type, "schedule" to linkedMapOf("repeat" to "daily", "time" to "07:00", "days" to emptyList<Int>(), "timeZone" to context.timeZone))
                "cardAdded" -> RuleBlock("type" to type, "columnId" to "")
                "cardMoved" -> RuleBlock("type" to type, "columnId" to last)
                "cardCompleted", "checklistCompleted" -> RuleBlock("type" to type)
                "cardDue" -> RuleBlock("type" to type, "minutesBefore" to 60, "columnId" to "")
                "labelAdded" -> RuleBlock("type" to type, "label" to "")
                else -> null
            }
            RuleZone.CONDITIONS -> when (type) {
                "titleContains", "hasLabel", "lacksLabel" -> RuleBlock("type" to type, "text" to "")
                "inColumn" -> RuleBlock("type" to type, "columnId" to first)
                "onDays" -> RuleBlock("type" to type, "days" to listOf(1, 2, 3, 4, 5))
                else -> null
            }
            RuleZone.ACTIONS -> when (type) {
                "createCard" -> RuleBlock("type" to type, "columnId" to first, "title" to "", "position" to "bottom", "skipIfExists" to true)
                "moveCard" -> RuleBlock("type" to type, "columnId" to last, "position" to "top")
                "addLabel" -> RuleBlock("type" to type, "name" to "", "color" to "")
                "removeLabel" -> RuleBlock("type" to type, "name" to "")
                "setDue" -> RuleBlock("type" to type, "inDays" to 1, "time" to "")
                "complete" -> RuleBlock("type" to type, "done" to true)
                "addChecklist" -> RuleBlock("type" to type, "items" to emptyList<String>())
                "resetChecklist", "deleteCard" -> RuleBlock("type" to type)
                "addComment" -> RuleBlock("type" to type, "text" to "")
                "assign" -> RuleBlock("type" to type, "username" to (context.people.firstOrNull()?.username ?: ""))
                "notify" -> RuleBlock("type" to type, "everyone" to context.people.isEmpty(), "people" to emptyList<String>(), "title" to "", "text" to "")
                "email" -> RuleBlock("type" to type, "people" to emptyList<String>(), "addresses" to emptyList<String>(), "title" to "", "text" to "")
                "moveAllCards" -> RuleBlock("type" to type, "columnId" to last, "toColumnId" to first, "onlyCompleted" to false)
                "deleteAllCards" -> RuleBlock("type" to type, "columnId" to last, "onlyCompleted" to true)
                else -> null
            }
        }
    }

    // ---- schedules ------------------------------------------------------------------------------

    // How the schedule menu reads a schedule: the server only knows daily/monthly/hourly/once, and
    // "every weekday" is a daily one with Monday to Friday.
    fun scheduleMode(schedule: RuleBlock): String {
        val repeat = schedule.string("repeat") ?: ""
        if (repeat != "daily") return repeat
        val days = schedule.ints("days").sorted().joinToString(",")
        return when (days) {
            "" -> "day"
            "1,2,3,4,5" -> "weekdays"
            "0,6" -> "weekends"
            else -> "days"
        }
    }

    // A choice from the schedule menu applied to a schedule; `today` for "just once"'s first date.
    fun applyScheduleMode(schedule: RuleBlock, choice: String, today: LocalDate): RuleBlock {
        var s = schedule.with("repeat", if (choice in listOf("monthly", "hourly", "once")) choice else "daily")
        when (choice) {
            "day" -> s = s.with("days", emptyList<Int>())
            "weekdays" -> s = s.with("days", listOf(1, 2, 3, 4, 5))
            "weekends" -> s = s.with("days", listOf(0, 6))
            "days" -> s = s.with("days", s.ints("days").ifEmpty { listOf(1) })
            "monthly" -> s = s.with("days", emptyList<Int>()).with("dayOfMonth", s.int("dayOfMonth")?.takeIf { it != 0 } ?: 1)
            "hourly" -> s = s.with("days", emptyList<Int>()).with("everyHours", s.int("everyHours")?.takeIf { it != 0 } ?: 3)
            "once" -> {
                s = s.with("days", emptyList<Int>())
                if (s.string("date").isNullOrEmpty()) s = s.with("date", today.plusDays(1).toString())
            }
        }
        return s
    }

    // A day chip tapped. Unticking the last day would silently mean "every day", so that's refused (null).
    fun toggleDay(days: List<Int>, day: Int): List<Int>? {
        val next = if (day in days) days.filter { it != day } else (days + day).sorted()
        return next.ifEmpty { null }
    }

    // ---- what the rule has to work with ------------------------------------------------------------

    // Whether there's a card for the step at `index` to act on: a card rule starts with one, a
    // schedule only once a "create a card" step has run - and a "delete the card" step ends it.
    fun cardAvailableBefore(trigger: RuleBlock?, actions: List<RuleBlock>, index: Int): Boolean {
        val before = actions.take(index).map { it.type }
        val lastCreate = before.lastIndexOf("createCard")
        val lastDelete = before.lastIndexOf("deleteCard")
        if (trigger?.type == "schedule") return lastCreate > lastDelete
        return lastDelete == -1 || lastCreate > lastDelete
    }

    // The in-place warning for a step, or null.
    fun actionWarning(trigger: RuleBlock?, actions: List<RuleBlock>, index: Int): String? {
        val kind = kind(RuleZone.ACTIONS, actions[index].type) ?: return null
        if (!kind.needsCard || cardAvailableBefore(trigger, actions, index)) return null
        return if (trigger?.type == "schedule") "A schedule has no card of its own - put “Create a card” above this step."
        else "The card was deleted by an earlier step."
    }

    fun conditionWarning(trigger: RuleBlock?, condition: RuleBlock): String? =
        if (trigger?.type == "schedule" && kind(RuleZone.CONDITIONS, condition.type)?.needsCard == true)
            "A schedule has no card to check - this would never be true." else null

    // What the Save button checks before the server does.
    fun localErrors(rule: AutomationRule): List<String> = buildList {
        if (rule.trigger == null) add("Add a WHEN block - what starts the rule?")
        if (rule.actions.isEmpty()) add("Add at least one THEN block - what should happen?")
        if (rule.name.isBlank()) add("Give the rule a name.")
    }

    // What goes to the server on Save: trimmed, stamped with this phone's zone.
    fun forSaving(rule: AutomationRule, zone: String): AutomationRule = rule.copy(name = rule.name.trim(), timeZone = zone)

    // Moves the action at `from` to be at `to` in the finished list.
    fun <T> moved(list: List<T>, from: Int, to: Int): List<T> {
        if (from !in list.indices) return list
        val out = list.toMutableList()
        val item = out.removeAt(from)
        out.add(to.coerceIn(0, out.size), item)
        return out
    }

    // ---- recipes -----------------------------------------------------------------------------------

    data class Recipe(val name: String, val why: String, val rule: AutomationRule)

    fun recipes(context: RuleContext, words: AutomationWords): List<Recipe> {
        val first = context.firstColumnId
        val last = context.lastColumnId
        val tz = context.timeZone
        fun daily(time: String, days: List<Int>) =
            RuleBlock("type" to "schedule", "schedule" to linkedMapOf("repeat" to "daily", "time" to time, "days" to days, "timeZone" to tz))
        fun rule(name: String, trigger: RuleBlock, vararg actions: RuleBlock) =
            AutomationRule(name = name, trigger = trigger, actions = actions.toList())
        return listOf(
            Recipe("Daily to-do", "a card every morning", rule("Pick up eggs every morning", daily("07:00", emptyList()),
                RuleBlock("type" to "createCard", "columnId" to first, "title" to "pick up eggs", "position" to "bottom", "skipIfExists" to true))),
            Recipe("Weekly chore", "every Sunday, with a checklist", rule("Sunday chores", daily("09:00", listOf(0)),
                RuleBlock("type" to "createCard", "columnId" to first, "title" to "Chores for {date}", "position" to "bottom", "skipIfExists" to true,
                    "items" to listOf("take out the trash", "water the plants", "vacuum")))),
            Recipe("Done means done", "moving to the last list ticks it off", rule("Moved to ${words.columnName(last)} → done",
                RuleBlock("type" to "cardMoved", "columnId" to last), RuleBlock("type" to "complete", "done" to true))),
            Recipe("Tidy up weekly", "clear finished cards each Monday", rule("Clear ${words.columnName(last)} on Mondays", daily("06:00", listOf(1)),
                RuleBlock("type" to "deleteAllCards", "columnId" to last, "onlyCompleted" to true))),
            Recipe("Due tomorrow", "new cards get a due date", rule("New cards are due tomorrow",
                RuleBlock("type" to "cardAdded", "columnId" to first), RuleBlock("type" to "setDue", "inDays" to 1, "time" to ""))),
            Recipe("Due reminder", "a notification an hour before a card is due", rule("Remind me before cards are due",
                RuleBlock("type" to "cardDue", "minutesBefore" to 60, "columnId" to ""),
                RuleBlock("type" to "notify", "everyone" to true, "people" to emptyList<String>(), "title" to "", "text" to "{card} is due {due}"))),
            Recipe("Bin night", "a notification every Sunday evening", rule("Bins out on Sunday", daily("18:00", listOf(0)),
                RuleBlock("type" to "notify", "everyone" to true, "people" to emptyList<String>(), "title" to "", "text" to "Take the bins out tonight"))),
            Recipe("Urgent to the top", "the urgent label jumps the queue", rule("Urgent cards to the top",
                RuleBlock("type" to "labelAdded", "label" to "urgent"), RuleBlock("type" to "moveCard", "columnId" to first, "position" to "top"))),
        )
    }
}

// ---- in plain words ----------------------------------------------------------------------------

// A rule read back as a sentence: "Every weekday at 7:00 AM, create a card “pick up eggs” in To do."
// `columnTitle` finds a list's title anywhere (null when it's gone); `time` and `date` format for
// the phone's locale, and the tests pin them.
class AutomationWords(
    private val columnTitle: (String) -> String?,
    private val people: List<OrganizerPerson> = emptyList(),
    private val time: (LocalTime) -> String = { it.format(DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT)).replace(' ', ' ') },
    private val date: (LocalDate) -> String = { it.format(DateTimeFormatter.ofPattern("EEE, MMM d", Locale.getDefault())) },
) {
    fun columnName(id: String?): String {
        if (id.isNullOrEmpty()) return "(pick a list)"
        return columnTitle(id)?.ifEmpty { "list" } ?: "(pick a list)"
    }

    private fun listOrAny(id: String?) = if (id.isNullOrEmpty()) "any list" else columnName(id)

    fun time12(hhmm: String?): String {
        val parts = (hhmm?.ifEmpty { null } ?: "07:00").split(":")
        val hour = parts.getOrNull(0)?.toIntOrNull() ?: 0
        val minute = parts.getOrNull(1)?.toIntOrNull() ?: 0
        return time(LocalTime.of(hour.coerceIn(0, 23), minute.coerceIn(0, 59)))
    }

    fun dueWords(inDays: Int?, time: String?): String {
        if (inDays == null) return "no due date"
        val day = when (inDays) { 0 -> "today"; 1 -> "tomorrow"; else -> "in $inDays days" }
        return if (!time.isNullOrEmpty()) "$day at ${time12(time)}" else day
    }

    private fun q(text: String?) = "“${text?.ifEmpty { null } ?: "…"}”"

    private fun days(list: List<Int>) = list.sorted().mapNotNull { BoardAutomations.DAY_SHORT.getOrNull(it) }.joinToString(", ").ifEmpty { "…" }

    fun scheduleWords(s: RuleBlock): String {
        val at = time12(s.string("time"))
        return when (BoardAutomations.scheduleMode(s)) {
            "day" -> "Every day at $at"
            "weekdays" -> "Every weekday at $at"
            "weekends" -> "Every Saturday and Sunday at $at"
            "days" -> "Every ${days(s.ints("days"))} at $at"
            "monthly" -> {
                val day = s.int("dayOfMonth")
                "${if (day == null || day == -1 || day == 0) "On the last day" else "On day $day"} of every month at $at"
            }
            "hourly" -> {
                val every = s.int("everyHours")?.takeIf { it != 0 } ?: 1
                "Every $every hour${if (every == 1) "" else "s"} from $at until midnight"
            }
            "once" -> {
                val on = s.string("date")?.let { runCatching { LocalDate.parse(it) }.getOrNull() }?.let(date) ?: "…"
                "Once, on $on at $at"
            }
            else -> "On a schedule"
        }
    }

    fun whoWords(usernames: List<String>, quiet: Boolean = false): String {
        val names = usernames.map { u -> people.firstOrNull { it.username == u }?.label ?: u }
        if (names.isEmpty()) return if (quiet) "" else "…"
        return if (names.size == 1) names[0] else "${names.dropLast(1).joinToString(", ")} and ${names.last()}"
    }

    // Null when this phone doesn't know the block's type.
    fun trigger(t: RuleBlock): String? = when (t.type) {
        "schedule" -> scheduleWords(t.schedule)
        "cardAdded" -> "When a card is added to ${listOrAny(t.string("columnId"))}"
        "cardMoved" -> "When a card is moved into ${listOrAny(t.string("columnId"))}"
        "cardCompleted" -> "When a card is marked done"
        "checklistCompleted" -> "When every checklist item on a card is ticked"
        "cardDue" -> {
            val minutes = t.int("minutesBefore") ?: 0
            val offset = BoardAutomations.DUE_OFFSETS.firstOrNull { it.first == minutes }
            val start = if (minutes == 0) "When a card is due"
                else "${offset?.second?.replaceFirstChar { it.uppercase() } ?: "$minutes minutes before"} a card is due"
            val column = t.string("columnId")
            "$start${if (!column.isNullOrEmpty()) " in ${columnName(column)}" else ""}"
        }
        "labelAdded" -> {
            val label = t.string("label")
            "When ${if (!label.isNullOrEmpty()) "the ${q(label)} label" else "any label"} is added to a card"
        }
        else -> null
    }

    fun condition(c: RuleBlock): String? = when (c.type) {
        "titleContains" -> "the title contains ${q(c.string("text"))}"
        "hasLabel" -> "it has the ${q(c.string("text"))} label"
        "lacksLabel" -> "it doesn't have the ${q(c.string("text"))} label"
        "inColumn" -> "it's in ${columnName(c.string("columnId"))}"
        "onDays" -> "it's ${days(c.ints("days"))}"
        else -> null
    }

    fun action(a: RuleBlock): String? {
        val inDays = a.int("inDays")
        val finished = if (a.bool("onlyCompleted")) "finished " else ""
        return when (a.type) {
            "createCard" -> "create a card ${q(a.string("title"))} in ${columnName(a.string("columnId"))}" +
                (if (inDays != null) ", due ${dueWords(inDays, a.string("time"))}" else "") +
                (if (a.bool("skipIfExists")) " (unless one is still there)" else "")
            "moveCard" -> "move it to the ${if (a.string("position") == "bottom") "bottom" else "top"} of ${columnName(a.string("columnId"))}"
            "addLabel" -> "label it ${q(a.string("name"))}"
            "removeLabel" -> "take off the ${q(a.string("name"))} label"
            "setDue" -> if (inDays == null) "clear its due date" else "make it due ${dueWords(inDays, a.string("time"))}"
            "complete" -> if (a.bool("done", default = true)) "mark it done" else "mark it not done"
            "addChecklist" -> {
                val count = a.strings("items").size
                "add ${if (count == 0) "some" else count.toString()} checklist item${if (count == 1) "" else "s"}"
            }
            "resetChecklist" -> "untick its checklist"
            "addComment" -> "comment ${q(a.string("text"))}"
            "assign" -> "assign it to ${a.string("username")?.ifEmpty { null } ?: "…"}"
            "deleteCard" -> "delete it"
            "notify" -> "notify ${if (a.bool("everyone")) "everyone" else whoWords(a.strings("people"))} ${q(a.string("text"))}"
            "email" -> {
                val to = (listOf(whoWords(a.strings("people"), quiet = true)) + a.strings("addresses")).filter { it.isNotEmpty() }
                "email ${to.joinToString(", ").ifEmpty { "…" }} ${q(a.string("text"))}"
            }
            "moveAllCards" -> "move every ${finished}card from ${columnName(a.string("columnId"))} to ${columnName(a.string("toColumnId"))}"
            "deleteAllCards" -> "delete every ${finished}card in ${columnName(a.string("columnId"))}"
            else -> null
        }
    }

    fun sentence(rule: AutomationRule): String {
        val start = rule.trigger?.let(::trigger) ?: "When…"
        val ifs = rule.conditions.map { condition(it) ?: it.type }
        val thens = rule.actions.map { action(it) ?: it.type }
        return "$start${if (ifs.isNotEmpty()) ", if ${ifs.joinToString(" and ")}" else ""}, ${if (thens.isNotEmpty()) thens.joinToString(", then ") else "do…"}."
    }

    // The editor's live line, before there's enough to make a sentence.
    fun draftSentence(rule: AutomationRule): String =
        if (rule.trigger != null || rule.actions.isNotEmpty()) sentence(rule)
        else "add a WHEN block and a THEN block to the rule to get started."
}
