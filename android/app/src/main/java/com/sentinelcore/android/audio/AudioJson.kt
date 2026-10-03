package com.sentinelcore.android.audio

import org.json.JSONArray
import org.json.JSONObject

// Small, null-safe readers for the audio API's JSON (core/docs/MUSIC_AND_PODCASTS.md). Android's
// org.json hands back the string "null" for optString on a JSON null, and 0 for a missing number,
// both of which would leak into the UI as real values - so every optional field goes through here.

internal fun JSONObject.str(key: String): String? =
    if (!has(key) || isNull(key)) null else optString(key)

internal fun JSONObject.text(key: String, fallback: String = ""): String = str(key) ?: fallback

internal fun JSONObject.dbl(key: String): Double? =
    if (!has(key) || isNull(key)) null else optDouble(key).takeIf { !it.isNaN() }

internal fun JSONObject.int(key: String): Int? =
    if (!has(key) || isNull(key)) null else (opt(key) as? Number)?.toInt() ?: optString(key).toIntOrNull()

internal fun JSONObject.long(key: String): Long? =
    if (!has(key) || isNull(key)) null else (opt(key) as? Number)?.toLong() ?: optString(key).toLongOrNull()

internal fun JSONObject.bool(key: String, fallback: Boolean = false): Boolean =
    if (!has(key) || isNull(key)) fallback else optBoolean(key, fallback)

internal fun JSONObject.obj(key: String): JSONObject? =
    if (!has(key) || isNull(key)) null else optJSONObject(key)

internal fun JSONObject.strings(key: String): List<String> {
    val array = if (!has(key) || isNull(key)) return emptyList() else optJSONArray(key) ?: return emptyList()
    return buildList {
        for (index in 0 until array.length()) {
            if (!array.isNull(index)) array.optString(index).takeIf { it.isNotBlank() }?.let(::add)
        }
    }
}

internal fun JSONObject.doubles(key: String): List<Double> {
    val array = if (!has(key) || isNull(key)) return emptyList() else optJSONArray(key) ?: return emptyList()
    return buildList { for (index in 0 until array.length()) add(array.optDouble(index, 0.0)) }
}

internal fun JSONObject.ints(key: String): List<Int> {
    val array = if (!has(key) || isNull(key)) return emptyList() else optJSONArray(key) ?: return emptyList()
    return buildList { for (index in 0 until array.length()) add(array.optInt(index)) }
}

internal inline fun <T> JSONObject.list(key: String, parse: (JSONObject) -> T): List<T> {
    val array = if (!has(key) || isNull(key)) return emptyList() else optJSONArray(key) ?: return emptyList()
    return array.objects(parse)
}

internal inline fun <T> JSONArray.objects(parse: (JSONObject) -> T): List<T> = buildList {
    for (index in 0 until length()) {
        val item = optJSONObject(index) ?: continue
        runCatching { parse(item) }.getOrNull()?.let(::add)
    }
}

internal fun JSONObject.putOpt2(key: String, value: Any?): JSONObject = put(key, value ?: JSONObject.NULL)
