package com.sentinelcore.android.audio

import com.sentinelcore.android.data.MediaImportCandidate
import org.json.JSONArray
import org.json.JSONObject

// "Suggested for you" on Music (core/docs/MUSIC_AND_PODCASTS.md, "Suggestions"): albums by
// artists like the ones this person listens to, not in the library yet. Built on the server in the
// background; downloading one is an imported list (POST /api/music/downloads/imports). Every
// field is nullable or defaulted so a partial answer never throws.

data class MusicSuggestionSeed(val artist: String, val reason: String? = null)

data class MusicSuggestedAlbum(
    val key: String,
    val artist: String,
    val title: String,
    val year: Int? = null,
    val mbid: String? = null,
    val artUrl: String? = null,
    val because: List<String> = emptyList(),
    val listens: Long = 0,
    // Already on one of this person's imported lists, or downloading.
    val queued: Boolean = false,
)

data class MusicSuggestions(
    // "ready", "working" or "error".
    val state: String = "ready",
    val builtAt: String? = null,
    val message: String? = null,
    val seeds: List<MusicSuggestionSeed> = emptyList(),
    val albums: List<MusicSuggestedAlbum> = emptyList(),
) {
    val working: Boolean get() = state == "working"
    val failed: Boolean get() = state == "error"
}

private fun JSONObject.sgString(key: String): String? =
    if (!has(key) || isNull(key)) null else optString(key).takeIf { it.isNotBlank() }

private fun JSONObject.sgArray(key: String): JSONArray? = if (!has(key) || isNull(key)) null else optJSONArray(key)

private fun JSONArray?.sgStrings(): List<String> {
    val array = this ?: return emptyList()
    return buildList { for (i in 0 until array.length()) if (!array.isNull(i)) array.optString(i).takeIf { it.isNotBlank() }?.let(::add) }
}

private fun <T> JSONArray?.sgObjects(parse: (JSONObject) -> T?): List<T> {
    val array = this ?: return emptyList()
    return buildList { for (i in 0 until array.length()) array.optJSONObject(i)?.let(parse)?.let(::add) }
}

internal fun parseMusicSuggestedAlbum(json: JSONObject): MusicSuggestedAlbum? {
    val artist = json.sgString("artist") ?: return null
    val title = json.sgString("title") ?: return null
    return MusicSuggestedAlbum(
        key = json.sgString("key") ?: "${artist.lowercase()}|${title.lowercase()}",
        artist = artist,
        title = title,
        year = (if (json.isNull("year")) null else (json.opt("year") as? Number)?.toInt() ?: json.optString("year").toIntOrNull())?.takeIf { it > 0 },
        mbid = json.sgString("mbid"),
        artUrl = json.sgString("artUrl"),
        because = json.sgArray("because").sgStrings(),
        listens = if (json.isNull("listens")) 0 else json.optLong("listens", 0),
        queued = !json.isNull("queued") && json.optBoolean("queued", false),
    )
}

internal fun parseMusicSuggestions(json: JSONObject): MusicSuggestions = MusicSuggestions(
    state = json.sgString("state") ?: "ready",
    builtAt = json.sgString("builtAt"),
    message = json.sgString("message"),
    seeds = json.sgArray("seeds").sgObjects { seed -> seed.sgString("artist")?.let { MusicSuggestionSeed(it, seed.sgString("reason")) } },
    albums = json.sgArray("albums").sgObjects(::parseMusicSuggestedAlbum).distinctBy { it.key },
)

internal object MusicSuggestionsFormat {
    const val LIST_NAME = "Suggested for you"
    const val POLL_MS = 3_000L
    const val EMPTY = "Love some songs or albums, or import your Spotify likes, and suggestions will show up here."

    // "Because you like Fleetwood Mac", "… Fleetwood Mac and Eagles", "… A, B and C".
    fun because(album: MusicSuggestedAlbum): String? {
        val names = album.because.take(3)
        if (names.isEmpty()) return null
        val joined = if (names.size == 1) names[0] else names.dropLast(1).joinToString(", ") + " and " + names.last()
        return "Because you like $joined"
    }

    // "Based on: Fleetwood Mac, Eagles, Steely Dan…" (the first few seeds).
    fun basedOn(seeds: List<MusicSuggestionSeed>, shown: Int = 5): String? {
        if (seeds.isEmpty()) return null
        val names = seeds.take(shown).joinToString(", ") { it.artist }
        return "Based on: $names" + if (seeds.size > shown) "…" else ""
    }

    // Queued on the server, or downloaded from this screen just now.
    fun isQueued(album: MusicSuggestedAlbum, localQueued: Set<String>): Boolean = album.queued || album.key in localQueued

    // The imported-list items for these albums.
    fun importItems(albums: List<MusicSuggestedAlbum>): List<MediaImportCandidate> =
        albums.map { MediaImportCandidate(key = it.key, artist = it.artist, title = it.title, year = it.year) }

    fun startedMessage(count: Int): String =
        if (count == 1) "On its way. Watch it under Downloads." else "$count albums on their way. Watch them under Downloads."

    fun dismissBody(artist: String, title: String?): JSONObject =
        JSONObject().put("artist", artist).apply { title?.let { put("title", it) } }.put("dismissed", true)
}
