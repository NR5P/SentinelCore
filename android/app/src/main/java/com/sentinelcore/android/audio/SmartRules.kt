package com.sentinelcore.android.audio

import org.json.JSONArray
import org.json.JSONObject

// A smart playlist's rules (MUSIC_AND_PODCASTS.md §3). The builder screen edits these; the field
// and op tables below are the contract's, so the builder can only offer combinations the server
// accepts.
data class SmartRule(val field: String, val op: String, val value: String)

data class SmartRules(
    val match: String = "all",
    val rules: List<SmartRule> = emptyList(),
    val sort: String = "random",
    val order: String = "desc",
    val limit: Int = 100,
) {
    fun toJson(): JSONObject = JSONObject()
        .put("match", match)
        .put(
            "rules",
            JSONArray().apply {
                for (rule in rules) {
                    put(JSONObject().put("field", rule.field).put("op", rule.op).put("value", SmartFields.wireValue(rule.field, rule.value)))
                }
            },
        )
        .put("sort", sort)
        .put("order", order)
        .put("limit", limit.coerceIn(0, SmartFields.MAX_LIMIT))

    // A rule with nothing typed in is left out rather than sent as "title is ''".
    fun withoutBlankRules(): SmartRules = copy(rules = rules.filter { it.value.isNotBlank() })

    // Plain words for a playlist card: "Genre is Rock, rating at least 4".
    fun summary(): String {
        if (rules.isEmpty()) return "Every song"
        val joiner = if (match == "any") " or " else ", "
        return rules.joinToString(joiner) { rule ->
            val field = SmartFields.label(rule.field).lowercase()
            "$field ${SmartFields.opLabel(rule.op).lowercase()} ${SmartFields.valueLabel(rule.field, rule.value)}"
        }.replaceFirstChar(Char::uppercase)
    }

    companion object {
        fun fromJson(json: JSONObject): SmartRules {
            val rules = buildList {
                val array = json.optJSONArray("rules") ?: JSONArray()
                for (index in 0 until array.length()) {
                    val item = array.optJSONObject(index) ?: continue
                    val raw = item.opt("value")
                    val value = when (raw) {
                        null, JSONObject.NULL -> ""
                        is Double -> if (raw == raw.toLong().toDouble()) raw.toLong().toString() else raw.toString()
                        else -> raw.toString()
                    }
                    add(SmartRule(item.optString("field"), item.optString("op"), value))
                }
            }
            return SmartRules(
                match = json.str("match") ?: "all",
                rules = rules,
                sort = json.str("sort") ?: "random",
                order = json.str("order") ?: "desc",
                limit = json.int("limit") ?: 100,
            )
        }
    }
}

enum class SmartFieldKind { TEXT, NUMBER, BOOL, DAYS, CODEC, LIBRARY }

object SmartFields {
    const val MAX_LIMIT = 5000

    // In the order the builder lists them.
    val fields: List<Pair<String, String>> = listOf(
        "genre" to "Genre",
        "artist" to "Artist",
        "album" to "Album",
        "title" to "Title",
        "composer" to "Composer",
        "year" to "Year",
        "rating" to "Rating",
        "loved" to "Loved",
        "playCount" to "Play count",
        "lastPlayed" to "Last played",
        "added" to "Added",
        "durationSeconds" to "Length (seconds)",
        "bpm" to "BPM",
        "codec" to "Format",
        "library" to "Library",
    )

    val sorts: List<Pair<String, String>> = listOf(
        "random" to "Random",
        "title" to "Title",
        "artist" to "Artist",
        "album" to "Album",
        "year" to "Year",
        "added" to "Date added",
        "plays" to "Most played",
        "lastPlayed" to "Last played",
        "rating" to "Rating",
    )

    val codecs = listOf("flac", "alac", "mp3", "aac", "opus", "vorbis", "wav", "aiff", "wma")

    fun kind(field: String): SmartFieldKind = when (field) {
        "title", "artist", "album", "genre", "composer" -> SmartFieldKind.TEXT
        "year", "rating", "playCount", "durationSeconds", "bpm" -> SmartFieldKind.NUMBER
        "loved" -> SmartFieldKind.BOOL
        "added", "lastPlayed" -> SmartFieldKind.DAYS
        "codec" -> SmartFieldKind.CODEC
        "library" -> SmartFieldKind.LIBRARY
        else -> SmartFieldKind.TEXT
    }

    fun ops(field: String): List<String> = when (kind(field)) {
        SmartFieldKind.TEXT -> listOf("is", "isNot", "contains", "notContains")
        SmartFieldKind.NUMBER -> listOf("is", "gte", "lte")
        SmartFieldKind.BOOL -> listOf("is")
        SmartFieldKind.DAYS -> listOf("inLast", "notInLast")
        SmartFieldKind.CODEC, SmartFieldKind.LIBRARY -> listOf("is", "isNot")
    }

    fun label(field: String): String = fields.firstOrNull { it.first == field }?.second ?: field

    fun opLabel(op: String): String = when (op) {
        "is" -> "Is"
        "isNot" -> "Is not"
        "contains" -> "Contains"
        "notContains" -> "Doesn't contain"
        "gte" -> "At least"
        "lte" -> "At most"
        "inLast" -> "In the last (days)"
        "notInLast" -> "Not in the last (days)"
        else -> op
    }

    fun valueLabel(field: String, value: String): String = when (kind(field)) {
        SmartFieldKind.BOOL -> if (value == "true") "yes" else "no"
        SmartFieldKind.DAYS -> "$value days"
        SmartFieldKind.TEXT -> "\"$value\""
        else -> value
    }

    // A field changing kind starts its rule over with an op and value that make sense for it.
    fun defaultRule(field: String): SmartRule = when (kind(field)) {
        SmartFieldKind.TEXT -> SmartRule(field, "is", "")
        SmartFieldKind.NUMBER -> SmartRule(field, if (field == "rating") "gte" else "is", if (field == "rating") "4" else "")
        SmartFieldKind.BOOL -> SmartRule(field, "is", "true")
        SmartFieldKind.DAYS -> SmartRule(field, "inLast", "30")
        SmartFieldKind.CODEC -> SmartRule(field, "is", "flac")
        SmartFieldKind.LIBRARY -> SmartRule(field, "is", "")
    }

    // What the server expects on the wire: numbers for the numeric fields, a boolean for loved,
    // a string for everything else. Unparseable numbers go as 0 rather than failing the save.
    fun wireValue(field: String, value: String): Any = when (kind(field)) {
        SmartFieldKind.NUMBER, SmartFieldKind.DAYS -> {
            val number = value.trim().toDoubleOrNull() ?: 0.0
            if (number == number.toLong().toDouble()) number.toLong() else number
        }
        SmartFieldKind.BOOL -> value.trim().equals("true", ignoreCase = true)
        else -> value.trim()
    }
}
