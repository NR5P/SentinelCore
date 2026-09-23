package com.sentinelcore.android.organizer

import java.time.Instant
import java.time.OffsetDateTime
import java.time.format.DateTimeParseException
import org.json.JSONArray
import org.json.JSONObject

// The Organizer's data as this app holds it - notes, events, reminders, boards and everything
// hanging off them. The wire protocol is core/docs/ORGANIZER_SYNC.md; the iOS/Mac/TV apps' copy of
// this is OrganizerKit, and the two should read alike.
//
// Fields are kept as plain JSON values (String, Double, Boolean, List, Map, null) rather than
// decoded into fixed classes. A field this build doesn't know about - one a newer app added - still
// has to be stored and sent back untouched, or an older phone would quietly erase it.

object OrganizerKinds {
    const val NOTE = "note"
    const val EVENT = "event"
    const val REMINDER = "reminder"
    const val BOARD = "board"
    const val COLUMN = "column"
    const val CARD = "card"
    const val ATTACHMENT = "attachment"
    const val COMMENT = "comment"
}

// Comments on a board card: each is its own item under the card, with `body` and, once its author
// changes it, `editedAt`. The author is `createdBy`; only they may edit it (the server enforces
// that), while anyone who can see the card may delete it.
object OrganizerComments {
    // Oldest first. Ones not yet stamped by the server still carry the local creation time.
    fun sorted(comments: List<OrganizerItem>): List<OrganizerItem> =
        comments.sortedWith(compareBy({ it.createdAt ?: Instant.MAX }, { it.id }))

    // With login off (no username) or an author-less comment, everyone may edit.
    fun canEdit(comment: OrganizerItem, username: String?): Boolean {
        val author = comment.createdBy
        return username.isNullOrBlank() || author.isNullOrBlank() || author.equals(username, ignoreCase = true)
    }

    // Null when there's no author: login is off, one shared household, so there is no name to show.
    fun authorName(createdBy: String?, people: List<OrganizerPerson>): String? {
        if (createdBy.isNullOrBlank()) return null
        return people.firstOrNull { it.username.equals(createdBy, ignoreCase = true) }?.label ?: createdBy
    }

    // The fields an edit sends, or nothing when the text is blank or unchanged.
    fun editFields(comment: OrganizerItem, newBody: String, now: Instant = Instant.now()): Map<String, Any?>? {
        val body = newBody.trim()
        if (body.isEmpty() || body == comment.string("body")) return null
        return mapOf("body" to body, "editedAt" to now.toString())
    }
}

data class OrganizerItem(
    val id: String,
    val kind: String,
    val spaceId: String,
    val parentId: String? = null,
    val deleted: Boolean = false,
    val fields: Map<String, Any?> = emptyMap(),
    val clocks: Map<String, String> = emptyMap(),
    val seq: Long? = null,
    val createdAt: Instant? = null,
    val createdBy: String? = null,
    val updatedAt: Instant? = null,
    val updatedBy: String? = null,
) {
    fun string(name: String): String? = fields[name] as? String
    fun bool(name: String): Boolean = fields[name] as? Boolean ?: false
    fun number(name: String): Double? = (fields[name] as? Number)?.toDouble()
    @Suppress("UNCHECKED_CAST")
    fun list(name: String): List<Any?> = fields[name] as? List<Any?> ?: emptyList()

    val title: String get() = string("title").orEmpty()

    // Items without a position sort after those with one, by when they were made.
    val sortPosition: Double get() = number("position") ?: (createdAt?.toEpochMilli()?.toDouble() ?: 0.0)
}

enum class OrganizerSpaceKind(val wire: String) {
    FAMILY("Family"), PERSONAL("Personal"), GROUP("Group");

    companion object {
        fun from(value: String?) = entries.firstOrNull { it.wire == value } ?: FAMILY
    }
}

data class OrganizerSpace(
    val id: String,
    val kind: OrganizerSpaceKind,
    val name: String,
    val members: List<String> = emptyList(),
    val color: String? = null,
    val createdBy: String? = null,
) {
    companion object {
        val FAMILY = OrganizerSpace("family", OrganizerSpaceKind.FAMILY, "Family")
    }
}

// Edits to one item not yet acknowledged by the server. A second edit to the same field replaces
// the first; the first base clock is kept, since that's where the whole run of edits started.
data class OrganizerChange(
    val id: String,
    val kind: String,
    val fields: Map<String, Any?> = emptyMap(),
    val clocks: Map<String, String> = emptyMap(),
    val baseClocks: Map<String, String>? = null,
) {
    fun merging(newer: OrganizerChange): OrganizerChange = copy(
        fields = fields + newer.fields,
        clocks = clocks + newer.clocks,
        baseClocks = when {
            newer.baseClocks == null -> baseClocks
            baseClocks == null -> newer.baseClocks
            else -> newer.baseClocks + baseClocks
        },
    )
}

data class OrganizerStatus(
    val enabled: Boolean,
    val serverId: String,
    val username: String?,
    val displayName: String?,
    val personalSpaces: Boolean,
    val itemCount: Int?,
    val maxAttachmentBytes: Long?,
)

data class OrganizerPerson(val username: String, val displayName: String?) {
    val label: String get() = displayName?.takeIf { it.isNotBlank() } ?: username
}

// JSON in and out, in the exact shapes the server speaks.
object OrganizerJson {
    fun toKotlin(value: Any?): Any? = when (value) {
        null, JSONObject.NULL -> null
        is JSONObject -> map(value)
        is JSONArray -> (0 until value.length()).map { toKotlin(value.opt(it)) }
        is Number -> value.toDouble()
        else -> value
    }

    fun map(json: JSONObject): Map<String, Any?> {
        val out = LinkedHashMap<String, Any?>()
        val keys = json.keys()
        while (keys.hasNext()) {
            val key = keys.next()
            out[key] = toKotlin(json.opt(key))
        }
        return out
    }

    fun toJson(value: Any?): Any = when (value) {
        null -> JSONObject.NULL
        is Map<*, *> -> JSONObject().also { json -> value.forEach { (k, v) -> json.put(k.toString(), toJson(v)) } }
        is List<*> -> JSONArray().also { array -> value.forEach { array.put(toJson(it)) } }
        // Whole numbers go out without a fraction so a priority of 2 reads back as 2 everywhere.
        is Double -> if (value == Math.rint(value) && Math.abs(value) < 9.007199254740992E15) value.toLong() else value
        is Float -> toJson(value.toDouble())
        is Int -> value.toLong()
        else -> value
    }

    fun stringMap(json: JSONObject?): Map<String, String> {
        if (json == null) return emptyMap()
        val out = LinkedHashMap<String, String>()
        json.keys().forEach { key -> json.optString(key).takeIf { it.isNotEmpty() }?.let { out[key] = it } }
        return out
    }

    fun instant(value: String?): Instant? {
        if (value.isNullOrBlank() || value == "null") return null
        return try {
            OffsetDateTime.parse(value).toInstant()
        } catch (_: DateTimeParseException) {
            runCatching { Instant.parse(value) }.getOrNull()
        }
    }

    fun item(json: JSONObject): OrganizerItem = OrganizerItem(
        id = json.getString("id"),
        kind = json.getString("kind"),
        spaceId = json.optString("spaceId"),
        parentId = json.optString("parentId").takeIf { it.isNotEmpty() && it != "null" },
        deleted = json.optBoolean("deleted"),
        fields = json.optJSONObject("fields")?.let(::map) ?: emptyMap(),
        clocks = stringMap(json.optJSONObject("clocks")),
        seq = if (json.has("seq") && !json.isNull("seq")) json.getLong("seq") else null,
        createdAt = instant(json.optString("createdAt")),
        createdBy = json.optString("createdBy").takeIf { it.isNotEmpty() && it != "null" },
        updatedAt = instant(json.optString("updatedAt")),
        updatedBy = json.optString("updatedBy").takeIf { it.isNotEmpty() && it != "null" },
    )

    fun itemJson(item: OrganizerItem): JSONObject = JSONObject().apply {
        put("id", item.id)
        put("kind", item.kind)
        put("spaceId", item.spaceId)
        put("parentId", item.parentId ?: JSONObject.NULL)
        put("deleted", item.deleted)
        put("fields", toJson(item.fields))
        put("clocks", JSONObject(item.clocks))
        put("seq", item.seq ?: JSONObject.NULL)
        put("createdAt", item.createdAt?.toString() ?: JSONObject.NULL)
        put("createdBy", item.createdBy ?: JSONObject.NULL)
        put("updatedAt", item.updatedAt?.toString() ?: JSONObject.NULL)
        put("updatedBy", item.updatedBy ?: JSONObject.NULL)
    }

    fun change(json: JSONObject): OrganizerChange = OrganizerChange(
        id = json.getString("id"),
        kind = json.getString("kind"),
        fields = json.optJSONObject("fields")?.let(::map) ?: emptyMap(),
        clocks = stringMap(json.optJSONObject("clocks")),
        baseClocks = json.optJSONObject("baseClocks")?.let(::stringMap),
    )

    fun changeJson(change: OrganizerChange): JSONObject = JSONObject().apply {
        put("id", change.id)
        put("kind", change.kind)
        put("fields", toJson(change.fields))
        put("clocks", JSONObject(change.clocks))
        change.baseClocks?.let { put("baseClocks", JSONObject(it)) }
    }

    fun space(json: JSONObject): OrganizerSpace = OrganizerSpace(
        id = json.getString("id"),
        kind = OrganizerSpaceKind.from(json.optString("kind")),
        name = json.optString("name"),
        members = json.optJSONArray("members")?.let { array -> (0 until array.length()).map { array.getString(it) } } ?: emptyList(),
        color = json.optString("color").takeIf { it.isNotEmpty() && it != "null" },
        createdBy = json.optString("createdBy").takeIf { it.isNotEmpty() && it != "null" },
    )

    fun spaceJson(space: OrganizerSpace): JSONObject = JSONObject().apply {
        put("id", space.id)
        put("kind", space.kind.wire)
        put("name", space.name)
        put("members", JSONArray(space.members))
        put("color", space.color ?: JSONObject.NULL)
        put("createdBy", space.createdBy ?: JSONObject.NULL)
    }

    fun status(json: JSONObject) = OrganizerStatus(
        enabled = json.optBoolean("enabled"),
        serverId = json.optString("serverId"),
        username = json.optString("username").takeIf { it.isNotEmpty() && it != "null" },
        displayName = json.optString("displayName").takeIf { it.isNotEmpty() && it != "null" },
        personalSpaces = json.optBoolean("personalSpaces"),
        itemCount = if (json.has("itemCount")) json.optInt("itemCount") else null,
        maxAttachmentBytes = if (json.has("maxAttachmentBytes")) json.optLong("maxAttachmentBytes") else null,
    )

    fun errorMessage(body: String): String? = runCatching {
        val json = JSONObject(body)
        json.optString("error").takeIf { it.isNotEmpty() }
            ?: json.optJSONArray("errors")?.let { array -> (0 until array.length()).joinToString("\n") { array.getString(it) } }
    }.getOrNull()
}
