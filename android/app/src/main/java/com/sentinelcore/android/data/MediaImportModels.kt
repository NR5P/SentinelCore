package com.sentinelcore.android.data

import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.net.URLEncoder
import kotlin.math.roundToInt

// "Import a list" on Music and Movies (core/docs/MOVIE_DOWNLOADS.md, "Import a list"): the user
// picks CSV/JSON files exported from Spotify, Letterboxd, IMDb and the like; the server parses
// them, the app shows what was found, and the server then works through the chosen albums or films
// in the background. The client only reads each file as text - every format is recognised on the
// server. As elsewhere every field is nullable or defaulted, and the parsers are `internal`
// top-level functions so the JVM unit tests can feed them the contract's sample JSON.
// The web version this mirrors is core/SentinelCore.Web/wwwroot/media-import.js.

// One chosen file, read as UTF-8 text.
data class MediaImportFile(val name: String, val text: String)

data class MediaImportAlbum(
    val artist: String = "",
    val title: String = "",
    val year: Int? = null,
    val trackCount: Int = 0,
    val tracks: List<String> = emptyList(),
    // A one-song release named after the song (rarely shared on its own).
    val single: Boolean = false,
    val inLibrary: Boolean = false,
)

data class MediaImportMovie(
    val title: String = "",
    val year: Int? = null,
    val imdbId: String? = null,
    val inLibrary: Boolean = false,
)

// POST {base}/import/preview. Nothing is saved yet.
data class MediaImportPreview(
    val kind: String = "music",
    val sources: List<String> = emptyList(),
    val tracks: Int = 0,
    val noAlbum: Int = 0,
    val shows: Int = 0,
    val unreadable: Int = 0,
    val problems: List<String> = emptyList(),
    val albums: List<MediaImportAlbum> = emptyList(),
    val movies: List<MediaImportMovie> = emptyList(),
)

// One row of the preview's checklist, the same shape for an album and a film.
data class MediaImportCandidate(
    val key: String,
    val artist: String? = null,
    val title: String,
    val year: Int? = null,
    val trackCount: Int = 0,
    val single: Boolean = false,
    val inLibrary: Boolean = false,
)

data class MediaImportCounts(
    val total: Int = 0,
    val waiting: Int = 0,
    val downloading: Int = 0,
    val done: Int = 0,
    val inLibrary: Int = 0,
    val notFound: Int = 0,
    val failed: Int = 0,
    val canceled: Int = 0,
) {
    val settled: Int get() = done + inLibrary + notFound + failed + canceled
    val missing: Int get() = notFound + failed
}

data class MediaImportItem(
    val id: String = "",
    val artist: String? = null,
    val title: String = "",
    val year: Int? = null,
    val trackCount: Int = 0,
    // waiting|searching|queued|downloading|seeding|importing|done|inLibrary|notFound|failed|canceled
    val state: String = "waiting",
    val message: String? = null,
    val progress: Double? = null,
    val downloadId: String? = null,
    val releaseTitle: String? = null,
)

// An imported list ("Import" in the contract). `items` is empty in the GET {base}/imports listing.
data class MediaImportJob(
    val id: String = "",
    val kind: String = "music",
    val name: String = "",
    val requestedBy: String? = null,
    val createdAt: String? = null,
    val paused: Boolean = false,
    val message: String? = null,
    val finished: Boolean = false,
    val counts: MediaImportCounts = MediaImportCounts(),
    val items: List<MediaImportItem> = emptyList(),
)

// ---------------- Parsing ----------------

private fun JSONObject.miString(key: String): String? =
    if (!has(key) || isNull(key)) null else optString(key).takeIf { it.isNotBlank() }

private fun JSONObject.miInt(key: String): Int? =
    if (!has(key) || isNull(key)) null else (opt(key) as? Number)?.toInt() ?: optString(key).toIntOrNull()

private fun JSONObject.miDouble(key: String): Double? =
    if (!has(key) || isNull(key)) null else (opt(key) as? Number)?.toDouble() ?: optString(key).toDoubleOrNull()

private fun JSONObject.miBool(key: String): Boolean =
    has(key) && !isNull(key) && optBoolean(key, false)

private fun JSONArray?.miStrings(): List<String> {
    val array = this ?: return emptyList()
    return buildList {
        for (i in 0 until array.length()) {
            if (!array.isNull(i)) array.optString(i).takeIf { it.isNotBlank() }?.let(::add)
        }
    }
}

private fun <T> JSONArray?.miObjects(parse: (JSONObject) -> T): List<T> {
    val array = this ?: return emptyList()
    return buildList { for (i in 0 until array.length()) array.optJSONObject(i)?.let { add(parse(it)) } }
}

private fun JSONObject.miArray(key: String): JSONArray? = if (!has(key) || isNull(key)) null else optJSONArray(key)

internal fun parseMediaImportAlbum(json: JSONObject): MediaImportAlbum = MediaImportAlbum(
    artist = json.miString("artist").orEmpty(),
    title = json.miString("title").orEmpty(),
    year = json.miInt("year")?.takeIf { it > 0 },
    trackCount = json.miInt("trackCount") ?: 0,
    tracks = json.miArray("tracks").miStrings(),
    single = json.miBool("single"),
    inLibrary = json.miBool("inLibrary"),
)

internal fun parseMediaImportMovie(json: JSONObject): MediaImportMovie = MediaImportMovie(
    title = json.miString("title").orEmpty(),
    year = json.miInt("year")?.takeIf { it > 0 },
    imdbId = json.miString("imdbId"),
    inLibrary = json.miBool("inLibrary"),
)

internal fun parseMediaImportPreview(json: JSONObject): MediaImportPreview = MediaImportPreview(
    kind = json.miString("kind") ?: "music",
    sources = json.miArray("sources").miStrings(),
    tracks = json.miInt("tracks") ?: 0,
    noAlbum = json.miInt("noAlbum") ?: 0,
    shows = json.miInt("shows") ?: 0,
    unreadable = json.miInt("unreadable") ?: 0,
    problems = json.miArray("problems").miStrings(),
    albums = json.miArray("albums").miObjects(::parseMediaImportAlbum).filter { it.title.isNotBlank() },
    movies = json.miArray("movies").miObjects(::parseMediaImportMovie).filter { it.title.isNotBlank() },
)

internal fun parseMediaImportCounts(json: JSONObject?): MediaImportCounts {
    json ?: return MediaImportCounts()
    return MediaImportCounts(
        total = json.miInt("total") ?: 0,
        waiting = json.miInt("waiting") ?: 0,
        downloading = json.miInt("downloading") ?: 0,
        done = json.miInt("done") ?: 0,
        inLibrary = json.miInt("inLibrary") ?: 0,
        notFound = json.miInt("notFound") ?: 0,
        failed = json.miInt("failed") ?: 0,
        canceled = json.miInt("canceled") ?: 0,
    )
}

internal fun parseMediaImportItem(json: JSONObject): MediaImportItem = MediaImportItem(
    id = json.miString("id").orEmpty(),
    artist = json.miString("artist"),
    title = json.miString("title").orEmpty(),
    year = json.miInt("year")?.takeIf { it > 0 },
    trackCount = json.miInt("trackCount") ?: 0,
    state = json.miString("state") ?: "waiting",
    message = json.miString("message"),
    progress = json.miDouble("progress"),
    downloadId = json.miString("downloadId"),
    releaseTitle = json.miString("releaseTitle"),
)

internal fun parseMediaImportJob(json: JSONObject): MediaImportJob {
    val items = json.miArray("items").miObjects(::parseMediaImportItem)
    val counts = parseMediaImportCounts(if (json.isNull("counts")) null else json.optJSONObject("counts"))
    return MediaImportJob(
        id = json.miString("id").orEmpty(),
        kind = json.miString("kind") ?: "music",
        name = json.miString("name") ?: "Imported list",
        requestedBy = json.miString("requestedBy"),
        createdAt = json.miString("createdAt"),
        paused = json.miBool("paused"),
        message = json.miString("message"),
        finished = json.miBool("finished"),
        // An older answer without counts still shows the right total.
        counts = if (counts.total == 0 && items.isNotEmpty()) counts.copy(total = items.size) else counts,
        items = items,
    )
}

// GET {base}/imports → { imports: [...] }; a bare array is read too.
internal fun parseMediaImportJobs(text: String): List<MediaImportJob> {
    val trimmed = text.trim()
    if (trimmed.isEmpty()) return emptyList()
    val array = if (trimmed.startsWith("[")) JSONArray(trimmed) else JSONObject(trimmed).miArray("imports")
    return array.miObjects(::parseMediaImportJob).filter { it.id.isNotBlank() }
}

// ---------------- Requests ----------------

// The same transport the audio and organizer routes use (SentinelRepository.organizerRequest):
// method, path, query, body, content type, read timeout → (status, body bytes).
typealias MediaImportTransport = suspend (method: String, path: String, query: Map<String, String>, body: ByteArray?, contentType: String?, timeoutMillis: Int?) -> Pair<Int, ByteArray>

class MediaImportException(val status: Int, message: String) : IOException(message)

// `kind` is "music" or "movies"; each has its own base path but the same routes.
class MediaImportApi(val kind: String, private val transport: MediaImportTransport) {
    val base: String = basePath(kind)

    private suspend fun call(method: String, path: String, body: JSONObject? = null, timeoutMillis: Int? = null): String {
        val bytes = body?.toString()?.toByteArray(Charsets.UTF_8)
        val (status, response) = transport(method, path, emptyMap(), bytes, if (bytes != null) "application/json" else null, timeoutMillis)
        val text = String(response, Charsets.UTF_8)
        if (status !in 200..299) throw MediaImportException(status, MediaImportFormat.errorMessage(status, text))
        return text
    }

    private fun job(text: String): MediaImportJob = parseMediaImportJob(JSONObject(text))

    // The server parses the files; a big Spotify export can take a moment.
    suspend fun preview(files: List<MediaImportFile>): MediaImportPreview =
        parseMediaImportPreview(JSONObject(call("POST", "$base/import/preview", previewBody(files), timeoutMillis = 120_000)))

    suspend fun start(name: String, items: List<MediaImportCandidate>): MediaImportJob =
        job(call("POST", "$base/imports", startBody(name, items), timeoutMillis = 60_000))

    // Null when the server has no imports here (404: downloads off or an older server).
    suspend fun list(): List<MediaImportJob>? = try {
        parseMediaImportJobs(call("GET", "$base/imports"))
    } catch (error: MediaImportException) {
        if (error.status == 404) null else throw error
    }

    suspend fun get(id: String): MediaImportJob = job(call("GET", jobPath(id)))

    suspend fun pause(id: String, paused: Boolean): MediaImportJob =
        job(call("POST", jobPath(id) + "/pause", JSONObject().put("paused", paused)))

    suspend fun retry(id: String): MediaImportJob = job(call("POST", jobPath(id) + "/retry"))

    suspend fun skip(id: String, itemId: String): MediaImportJob =
        job(call("DELETE", jobPath(id) + "/items/" + segment(itemId)))

    suspend fun remove(id: String) {
        call("DELETE", jobPath(id))
    }

    fun jobPath(id: String): String = "$base/imports/${segment(id)}"

    companion object {
        fun basePath(kind: String): String = if (kind == "movies") "/api/media/downloads" else "/api/music/downloads"

        private fun segment(value: String): String = URLEncoder.encode(value, "UTF-8").replace("+", "%20")

        fun previewBody(files: List<MediaImportFile>): JSONObject = JSONObject().put(
            "files",
            JSONArray().apply { files.forEach { put(JSONObject().put("name", it.name).put("text", it.text)) } },
        )

        fun startBody(name: String, items: List<MediaImportCandidate>): JSONObject = JSONObject()
            .put("name", name.trim())
            .put(
                "items",
                JSONArray().apply {
                    items.forEach { item ->
                        put(
                            JSONObject()
                                .put("artist", item.artist?.takeIf { it.isNotBlank() } ?: JSONObject.NULL)
                                .put("title", item.title)
                                .put("year", item.year ?: JSONObject.NULL)
                                .put("trackCount", item.trackCount),
                        )
                    }
                },
            )
    }
}

// ---------------- Wording (media-import.js) ----------------

internal object MediaImportFormat {
    // The server refuses more than this in one preview.
    const val MAX_TOTAL_CHARS = 25_000_000

    // How many checklist rows are drawn at once; the rest stay included.
    const val SHOWN_AT_ONCE = 400

    // What the file picker offers. Many apps label a CSV oddly (or as a spreadsheet), so */* is
    // there too: the server says when a file is not a list.
    val PICKER_TYPES = arrayOf(
        "text/csv", "text/comma-separated-values", "text/tab-separated-values", "application/csv",
        "application/json", "text/json", "text/plain", "application/vnd.ms-excel", "*/*",
    )

    data class Words(val thing: String, val things: String, val tab: String)

    fun words(kind: String): Words =
        if (kind == "movies") Words("movie", "movies", "Movies") else Words("album", "albums", "Music")

    fun plural(n: Int, word: String, words: String = word + "s"): String = "$n ${if (n == 1) word else words}"

    fun errorMessage(status: Int, text: String): String {
        val fromServer = runCatching {
            val json = JSONObject(text)
            json.miString("error") ?: json.optJSONArray("errors")?.miStrings()?.joinToString(" ")?.takeIf { it.isNotBlank() }
        }.getOrNull()
        return fromServer ?: when (status) {
            401 -> "This server requires you to sign in."
            403 -> "Only an admin can do that."
            404 -> "Not found."
            413 -> "That file is too big to be a list."
            else -> "The server answered $status."
        }
    }

    fun candidates(kind: String, preview: MediaImportPreview?): List<MediaImportCandidate> {
        preview ?: return emptyList()
        return if (kind == "movies") {
            preview.movies.map { MediaImportCandidate("${it.title}\u0001${it.year ?: ""}", null, it.title, it.year, 0, false, it.inLibrary) }
        } else {
            preview.albums.map { MediaImportCandidate("${it.artist}\u0001${it.title}", it.artist, it.title, it.year, it.trackCount, it.single, it.inLibrary) }
        }.distinctBy { it.key }
    }

    // The checklist starts with everything not already in the library ticked.
    fun initialChecked(items: List<MediaImportCandidate>): Set<String> = items.filter { !it.inLibrary }.map { it.key }.toSet()

    fun matches(item: MediaImportCandidate, filter: String): Boolean {
        val q = filter.trim().lowercase()
        if (q.isEmpty()) return true
        return "${item.artist.orEmpty()} ${item.title} ${item.year ?: ""}".lowercase().contains(q)
    }

    // "Liked_Songs.csv" → "Liked Songs"; several files → "Music import".
    fun defaultName(kind: String, fileNames: List<String>): String =
        if (fileNames.size == 1) {
            fileNames[0].replace(Regex("\\.(csv|json|txt|tsv)$", RegexOption.IGNORE_CASE), "").replace(Regex("_+"), " ").trim()
                .ifBlank { "${words(kind).tab} import" }
        } else "${words(kind).tab} import"

    // "From Exportify", "312 songs → 120 albums", "4 already in the library".
    fun facts(kind: String, preview: MediaImportPreview): List<String> {
        val items = candidates(kind, preview)
        val have = items.count { it.inLibrary }
        return buildList {
            if (preview.sources.isNotEmpty()) add("From ${preview.sources.joinToString(" + ")}")
            if (kind == "movies") add(plural(items.size, "movie"))
            else add("${plural(preview.tracks, "song")} → ${plural(items.size, "album")}")
            if (have > 0) add("$have already in the library")
        }
    }

    fun notes(kind: String, preview: MediaImportPreview): List<String> {
        val items = candidates(kind, preview)
        return buildList {
            if (preview.noAlbum > 0) {
                add("${plural(preview.noAlbum, "song")} had no album, so ${if (preview.noAlbum == 1) "it was" else "they were"} left out.")
            }
            if (preview.shows > 0) {
                add("${plural(preview.shows, "TV show")} left out: add ${if (preview.shows == 1) "it" else "them"} with + Add to pick seasons.")
            }
            if (preview.unreadable > 0) {
                add("${preview.unreadable} row${if (preview.unreadable == 1) "" else "s"} could not be read (podcast episodes, local files, blank titles).")
            }
            addAll(preview.problems)
            val singles = items.count { it.single && !it.inLibrary }
            if (singles > 0) {
                add("${plural(singles, "single")}: one-song releases are rarely shared on their own, so expect some of these to come back not found.")
            }
        }
    }

    fun startLabel(kind: String, count: Int, busy: Boolean): String {
        if (busy) return "Starting…"
        val w = words(kind)
        return "Download ${plural(count, w.thing, w.things)}"
    }

    fun startedMessage(kind: String, count: Int): String {
        val w = words(kind)
        return "Working through ${plural(count, w.thing, w.things)}. Watch it under Downloads."
    }

    fun stateWord(state: String): String = when (state) {
        "waiting" -> "waiting"
        "searching" -> "searching…"
        "queued" -> "queued"
        "downloading" -> "downloading"
        "seeding" -> "sharing back"
        "importing" -> "adding to library"
        "done" -> "in the library"
        "inLibrary" -> "already had it"
        "notFound" -> "not found"
        "failed" -> "failed"
        "canceled" -> "left out"
        else -> state
    }

    // A downloading item shows its percent; everything else its state.
    fun itemStateLabel(item: MediaImportItem): String =
        if (item.state == "downloading" && item.progress != null) "${(item.progress * 100).roundToInt()}%" else stateWord(item.state)

    // Only these can be left out (the server refuses the rest).
    fun canSkip(state: String): Boolean = state in setOf("waiting", "searching", "notFound", "failed")

    fun status(job: MediaImportJob): String = when {
        job.paused -> "paused"
        job.finished -> "finished"
        else -> "working"
    }

    // "120 downloaded · 3 downloading · 50 to search · 4 already had · 2 not found".
    fun summaryBits(counts: MediaImportCounts): List<String> = buildList {
        if (counts.done > 0) add("${counts.done} downloaded")
        if (counts.downloading > 0) add("${counts.downloading} downloading")
        if (counts.waiting > 0) add("${counts.waiting} to search")
        if (counts.inLibrary > 0) add("${counts.inLibrary} already had")
        if (counts.notFound > 0) add("${counts.notFound} not found")
        if (counts.failed > 0) add("${counts.failed} failed")
    }

    // How much of the list is settled, 0..1.
    fun settledFraction(counts: MediaImportCounts): Float =
        if (counts.total <= 0) 1f else (counts.settled.toFloat() / counts.total).coerceIn(0f, 1f)

    // Pause/resume shows while the list still has work (or is paused).
    fun canPause(job: MediaImportJob): Boolean = !job.finished || job.paused

    fun canRetry(job: MediaImportJob): Boolean = job.counts.missing > 0

    // The details screen's filters: all / in progress / done / not found.
    val DETAIL_FILTERS = listOf("all" to "All", "active" to "In progress", "done" to "Done", "missing" to "Not found")

    fun inFilter(filter: String, state: String): Boolean = when (filter) {
        "active" -> state in setOf("waiting", "searching", "queued", "downloading", "seeding", "importing")
        "done" -> state == "done" || state == "inLibrary"
        "missing" -> state == "notFound" || state == "failed"
        else -> true
    }

    // Polling: quickly while something is moving.
    fun anyBusy(jobs: List<MediaImportJob>): Boolean = jobs.any { !it.finished && !it.paused }
}
