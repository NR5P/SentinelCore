package com.sentinelcore.android.data

import org.json.JSONArray
import org.json.JSONObject
import java.net.URLEncoder

// Movie downloads - "+ Add" on the Movies tab. The contract is core/docs/MOVIE_DOWNLOADS.md: find a
// title on TMDB, pick a release the household's torrent sites offer, and the server downloads it
// through a VPN and files it into the library. As in MediaModels.kt, every field is nullable or
// defaulted so an older or partial server answer never throws its way out of a parse, and the
// parsers are `internal` top-level functions so the JVM unit tests can feed them the contract's
// sample JSON.

data class MediaDownloadVpn(
    // "connected", "connecting", "down", "off" or "unknown".
    val state: String = "unknown",
    val required: Boolean = true,
    val ip: String? = null,
    val location: String? = null,
    val country: String? = null,
    val provider: String? = null,
    val checkedAt: String? = null,
    val error: String? = null,
)

data class MediaDownloadServices(
    val enabled: Boolean = false,
    // "running", "starting", "stopped", "failed", "notInstalled" or "external".
    val services: String = "stopped",
    val idleMinutes: Int = 5,
    val vpn: MediaDownloadVpn = MediaDownloadVpn(),
)

// GET /api/media/downloads/status - decides whether "+ Add" is shown at all.
data class MediaDownloadStatus(
    val enabled: Boolean = false,
    val canDownload: Boolean = false,
    val canManageSites: Boolean = false,
    val tmdbConfigured: Boolean = false,
    // Whether a library on the server's own disks can receive a movie / a TV show.
    val movieLibrary: Boolean = false,
    val showLibrary: Boolean = false,
    val status: MediaDownloadServices? = null,
)

// One TMDB match from /lookup.
data class MediaLookupResult(
    // "movie" or "show".
    val kind: String,
    val tmdbId: Long,
    val title: String,
    val year: Int? = null,
    val overview: String? = null,
    val posterPath: String? = null,
    val posterUrl: String? = null,
    val inLibrary: Boolean = false,
    val itemId: String? = null,
) {
    val isShow: Boolean get() = kind == "show"
}

data class MediaLookup(val tmdbConfigured: Boolean, val results: List<MediaLookupResult> = emptyList())

data class MediaDownloadEpisode(
    val number: Int,
    val name: String? = null,
    val airDate: String? = null,
    // False for an episode that is not out yet; missing reads as out.
    val aired: Boolean = true,
    val inLibrary: Boolean = false,
)

data class MediaDownloadSeason(
    val number: Int,
    val name: String,
    val episodeCount: Int = 0,
    val ownedCount: Int = 0,
    val episodes: List<MediaDownloadEpisode> = emptyList(),
) {
    val isComplete: Boolean get() = episodeCount > 0 && ownedCount >= episodeCount
}

// GET /api/media/downloads/show/{tmdbId}.
data class MediaDownloadShow(
    val tmdbId: Long,
    val title: String,
    val year: Int? = null,
    val overview: String? = null,
    val posterPath: String? = null,
    val posterUrl: String? = null,
    val itemId: String? = null,
    val seasons: List<MediaDownloadSeason> = emptyList(),
)

// The body of POST /api/media/downloads/search. `season` alone asks for a whole season;
// `season` + `episode` for one episode.
data class MediaReleaseSearchInput(
    val kind: String,
    val title: String,
    val year: Int? = null,
    val tmdbId: Long? = null,
    val posterPath: String? = null,
    val season: Int? = null,
    val episode: Int? = null,
) {
    fun toJson(): JSONObject = JSONObject()
        .put("kind", kind)
        .put("title", title)
        .put("year", year ?: JSONObject.NULL)
        .put("tmdbId", tmdbId ?: JSONObject.NULL)
        .put("posterPath", posterPath ?: JSONObject.NULL)
        .put("season", season ?: JSONObject.NULL)
        .put("episode", episode ?: JSONObject.NULL)

    // "Dune: Part Two (2024)", "Bluey (2018) · S02E05", "Bluey (2018) · season 2".
    val label: String
        get() = buildString {
            append(title)
            year?.let { append(" ($it)") }
            if (season != null) {
                append(" · ")
                append(if (episode != null) MediaDownloadFormat.episodeCode(season, episode) else "season $season")
            }
        }
}

data class MediaRelease(
    val id: String,
    val title: String,
    val site: String? = null,
    val sizeBytes: Long? = null,
    val seeders: Int? = null,
    val leechers: Int? = null,
    val publishedAt: String? = null,
    val quality: String? = null,
    val codec: String? = null,
    val source: String? = null,
    val hdr: Boolean = false,
    val season: Int? = null,
    val episode: Int? = null,
    val seasonPack: Boolean = false,
    val matches: Boolean = true,
    val warning: String? = null,
) {
    // The little tags under a release, in the web's order.
    val chips: List<String>
        get() = listOfNotNull(quality, codec, source, "HDR".takeIf { hdr }, "whole season".takeIf { seasonPack })
}

// GET /api/media/downloads/search/{id}, polled every second until done or failed.
data class MediaReleaseSearch(
    val id: String,
    // "connecting", "searching", "done" or "failed".
    val phase: String,
    val message: String? = null,
    val error: String? = null,
    val kind: String? = null,
    val title: String? = null,
    val year: Int? = null,
    val season: Int? = null,
    val episode: Int? = null,
    val results: List<MediaRelease> = emptyList(),
    val others: List<MediaRelease> = emptyList(),
) {
    val isFinished: Boolean get() = phase == "done" || phase == "failed"
}

data class MediaDownload(
    val id: String,
    // "movie" or "episode".
    val kind: String = "movie",
    val title: String,
    val year: Int? = null,
    val tmdbId: Long? = null,
    val season: Int? = null,
    val episode: Int? = null,
    // "S02E05", "Season 2" or null.
    val subtitle: String? = null,
    val posterUrl: String? = null,
    val releaseTitle: String? = null,
    val site: String? = null,
    val sizeBytes: Long? = null,
    // "queued", "downloading", "seeding", "importing", "done", "failed" or "canceled".
    val state: String,
    // 0-1.
    val progress: Double = 0.0,
    val downloadedBytes: Long? = null,
    val speedBytesPerSecond: Long? = null,
    val etaSeconds: Long? = null,
    val peers: Int? = null,
    // On an active download a note ("Waiting for the VPN to connect."), on a failed one the reason.
    val error: String? = null,
    val libraryId: String? = null,
    val itemId: String? = null,
    val requestedBy: String? = null,
    val createdAt: String? = null,
    val completedAt: String? = null,
    val finishedAt: String? = null,
) {
    val isActive: Boolean get() = MediaDownloadFormat.isActive(state)
    val isDone: Boolean get() = state == "done"
    val isFailed: Boolean get() = state == "failed"
}

// GET /api/media/downloads.
data class MediaDownloadList(
    val status: MediaDownloadServices? = null,
    val active: List<MediaDownload> = emptyList(),
    val recent: List<MediaDownload> = emptyList(),
)

// One of the household's torrent sites (Prowlarr indexers). Admin only.
data class MediaDownloadSite(
    val id: String,
    val name: String,
    val enabled: Boolean = true,
    // "public", "semiPrivate" or "private".
    val privacy: String? = null,
    val language: String? = null,
    val description: String? = null,
    val definition: String? = null,
)

data class MediaSiteFieldOption(val value: String, val label: String)

data class MediaSiteField(
    val name: String,
    val label: String,
    // "text", "password", "checkbox", "select" or "number".
    val type: String = "text",
    val help: String? = null,
    // The default, as text ("" for none); checkboxes use `checked`.
    val value: String = "",
    val checked: Boolean = false,
    val options: List<MediaSiteFieldOption> = emptyList(),
)

// A site that can be added, from /sites/catalog.
data class MediaSiteDefinition(
    val definition: String,
    val name: String,
    val description: String? = null,
    val privacy: String? = null,
    val language: String? = null,
    val needsSetup: Boolean = false,
    val fields: List<MediaSiteField> = emptyList(),
)

// A sites route answered 503 { starting: true }: Prowlarr is inside the VPN, which is still
// coming up. The screen shows the message and asks again.
class MediaDownloadsStartingException(message: String) : Exception(message)

// ---------------- Parsing ----------------

private fun JSONObject.dlString(key: String): String? =
    if (!has(key) || isNull(key)) null else optString(key).takeIf { it.isNotBlank() }

private fun JSONObject.dlInt(key: String): Int? =
    if (!has(key) || isNull(key)) null else optInt(key)

private fun JSONObject.dlLong(key: String): Long? =
    if (!has(key) || isNull(key)) null else optLong(key)

private fun JSONObject.dlObject(key: String): JSONObject? =
    if (!has(key) || isNull(key)) null else optJSONObject(key)

private fun JSONObject.dlArray(key: String): JSONArray? =
    if (!has(key) || isNull(key)) null else optJSONArray(key)

private fun <T> JSONArray?.dlObjects(parse: (JSONObject) -> T): List<T> {
    val array = this ?: return emptyList()
    return buildList { for (i in 0 until array.length()) array.optJSONObject(i)?.let { add(parse(it)) } }
}

internal fun parseMediaDownloadVpn(json: JSONObject?): MediaDownloadVpn {
    json ?: return MediaDownloadVpn()
    return MediaDownloadVpn(
        state = json.dlString("state") ?: "unknown",
        required = json.optBoolean("required", true),
        ip = json.dlString("ip"),
        location = json.dlString("location"),
        country = json.dlString("country"),
        provider = json.dlString("provider"),
        checkedAt = json.dlString("checkedAt"),
        error = json.dlString("error"),
    )
}

internal fun parseMediaDownloadServices(json: JSONObject?): MediaDownloadServices? {
    json ?: return null
    return MediaDownloadServices(
        enabled = json.optBoolean("enabled"),
        services = json.dlString("services") ?: "stopped",
        idleMinutes = json.optInt("idleMinutes", 5),
        vpn = parseMediaDownloadVpn(json.dlObject("vpn")),
    )
}

internal fun parseMediaDownloadStatus(json: JSONObject): MediaDownloadStatus = MediaDownloadStatus(
    enabled = json.optBoolean("enabled"),
    canDownload = json.optBoolean("canDownload"),
    canManageSites = json.optBoolean("canManageSites"),
    tmdbConfigured = json.optBoolean("tmdbConfigured"),
    movieLibrary = json.optBoolean("movieLibrary"),
    showLibrary = json.optBoolean("showLibrary"),
    status = parseMediaDownloadServices(json.dlObject("status")),
)

internal fun parseMediaLookupResult(json: JSONObject): MediaLookupResult = MediaLookupResult(
    kind = json.dlString("kind") ?: "movie",
    tmdbId = json.optLong("tmdbId"),
    title = json.optString("title"),
    year = json.dlInt("year"),
    overview = json.dlString("overview"),
    posterPath = json.dlString("posterPath"),
    posterUrl = json.dlString("posterUrl"),
    inLibrary = json.optBoolean("inLibrary"),
    itemId = json.dlString("itemId"),
)

internal fun parseMediaLookup(json: JSONObject): MediaLookup {
    val configured = json.optBoolean("tmdbConfigured", true)
    return MediaLookup(
        tmdbConfigured = configured,
        results = if (configured) json.dlArray("results").dlObjects(::parseMediaLookupResult) else emptyList(),
    )
}

internal fun parseMediaDownloadEpisode(json: JSONObject): MediaDownloadEpisode = MediaDownloadEpisode(
    number = json.optInt("number"),
    name = json.dlString("name"),
    airDate = json.dlString("airDate"),
    aired = json.optBoolean("aired", true),
    inLibrary = json.optBoolean("inLibrary"),
)

internal fun parseMediaDownloadSeason(json: JSONObject): MediaDownloadSeason {
    val number = json.optInt("number")
    val episodes = json.dlArray("episodes").dlObjects(::parseMediaDownloadEpisode)
    return MediaDownloadSeason(
        number = number,
        name = json.dlString("name") ?: if (number == 0) "Specials" else "Season $number",
        episodeCount = json.dlInt("episodeCount") ?: episodes.size,
        ownedCount = json.optInt("ownedCount"),
        episodes = episodes,
    )
}

internal fun parseMediaDownloadShow(json: JSONObject): MediaDownloadShow = MediaDownloadShow(
    tmdbId = json.optLong("tmdbId"),
    title = json.optString("title"),
    year = json.dlInt("year"),
    overview = json.dlString("overview"),
    posterPath = json.dlString("posterPath"),
    posterUrl = json.dlString("posterUrl"),
    itemId = json.dlString("itemId"),
    seasons = json.dlArray("seasons").dlObjects(::parseMediaDownloadSeason),
)

internal fun parseMediaRelease(json: JSONObject): MediaRelease = MediaRelease(
    id = json.optString("id"),
    title = json.optString("title"),
    site = json.dlString("site"),
    sizeBytes = json.dlLong("sizeBytes"),
    seeders = json.dlInt("seeders"),
    leechers = json.dlInt("leechers"),
    publishedAt = json.dlString("publishedAt"),
    quality = json.dlString("quality"),
    codec = json.dlString("codec"),
    source = json.dlString("source"),
    hdr = json.optBoolean("hdr"),
    season = json.dlInt("season"),
    episode = json.dlInt("episode"),
    seasonPack = json.optBoolean("seasonPack"),
    matches = json.optBoolean("matches", true),
    warning = json.dlString("warning"),
)

internal fun parseMediaReleaseSearch(json: JSONObject): MediaReleaseSearch = MediaReleaseSearch(
    id = json.optString("id"),
    phase = json.dlString("phase") ?: "connecting",
    message = json.dlString("message"),
    error = json.dlString("error"),
    kind = json.dlString("kind"),
    title = json.dlString("title"),
    year = json.dlInt("year"),
    season = json.dlInt("season"),
    episode = json.dlInt("episode"),
    results = json.dlArray("results").dlObjects(::parseMediaRelease),
    others = json.dlArray("others").dlObjects(::parseMediaRelease),
)

internal fun parseMediaDownload(json: JSONObject): MediaDownload = MediaDownload(
    id = json.optString("id"),
    kind = json.dlString("kind") ?: "movie",
    title = json.optString("title"),
    year = json.dlInt("year"),
    tmdbId = json.dlLong("tmdbId"),
    season = json.dlInt("season"),
    episode = json.dlInt("episode"),
    subtitle = json.dlString("subtitle"),
    posterUrl = json.dlString("posterUrl"),
    releaseTitle = json.dlString("releaseTitle"),
    site = json.dlString("site"),
    sizeBytes = json.dlLong("sizeBytes"),
    state = json.dlString("state") ?: "queued",
    progress = json.optDouble("progress", 0.0).takeUnless { it.isNaN() } ?: 0.0,
    downloadedBytes = json.dlLong("downloadedBytes"),
    speedBytesPerSecond = json.dlLong("speedBytesPerSecond"),
    etaSeconds = json.dlLong("etaSeconds"),
    peers = json.dlInt("peers"),
    error = json.dlString("error"),
    libraryId = json.dlString("libraryId"),
    itemId = json.dlString("itemId"),
    requestedBy = json.dlString("requestedBy"),
    createdAt = json.dlString("createdAt"),
    completedAt = json.dlString("completedAt"),
    finishedAt = json.dlString("finishedAt"),
)

internal fun parseMediaDownloadList(json: JSONObject): MediaDownloadList = MediaDownloadList(
    status = parseMediaDownloadServices(json.dlObject("status")),
    active = json.dlArray("active").dlObjects(::parseMediaDownload),
    recent = json.dlArray("recent").dlObjects(::parseMediaDownload),
)

internal fun parseMediaDownloadSite(json: JSONObject): MediaDownloadSite = MediaDownloadSite(
    id = json.optString("id"),
    name = json.dlString("name") ?: json.optString("definition"),
    enabled = json.optBoolean("enabled", true),
    privacy = json.dlString("privacy"),
    language = json.dlString("language"),
    description = json.dlString("description"),
    definition = json.dlString("definition"),
)

internal fun parseMediaDownloadSites(array: JSONArray?): List<MediaDownloadSite> = array.dlObjects(::parseMediaDownloadSite)

internal fun parseMediaSiteField(json: JSONObject): MediaSiteField {
    val raw = if (!json.has("value") || json.isNull("value")) null else json.opt("value")
    return MediaSiteField(
        name = json.optString("name"),
        label = json.dlString("label") ?: json.optString("name"),
        type = json.dlString("type") ?: "text",
        help = json.dlString("help"),
        value = when (raw) {
            null, is Boolean -> ""
            else -> raw.toString()
        },
        checked = raw == true,
        options = json.dlArray("options").dlObjects { option ->
            val value = if (option.isNull("value")) "" else option.opt("value")?.toString().orEmpty()
            MediaSiteFieldOption(value = value, label = option.dlString("label") ?: value)
        },
    )
}

internal fun parseMediaSiteDefinition(json: JSONObject): MediaSiteDefinition = MediaSiteDefinition(
    definition = json.optString("definition"),
    name = json.dlString("name") ?: json.optString("definition"),
    description = json.dlString("description"),
    privacy = json.dlString("privacy"),
    language = json.dlString("language"),
    needsSetup = json.optBoolean("needsSetup"),
    fields = json.dlArray("fields").dlObjects(::parseMediaSiteField),
)

internal fun parseMediaSiteCatalog(array: JSONArray?): List<MediaSiteDefinition> = array.dlObjects(::parseMediaSiteDefinition)

// A sites route's 503 body: { error, starting: true } while the VPN comes up.
internal fun parseMediaDownloadsStarting(body: String): MediaDownloadsStartingException? {
    val json = runCatching { JSONObject(body) }.getOrNull() ?: return null
    if (!json.optBoolean("starting")) return null
    return MediaDownloadsStartingException(json.dlString("error") ?: "Connecting to the VPN... try again in a moment.")
}

// ---------------- Requests ----------------

internal object MediaDownloadApi {
    const val STATUS_PATH = "/api/media/downloads/status"
    const val DOWNLOADS_PATH = "/api/media/downloads"
    const val SEARCH_PATH = "/api/media/downloads/search"
    const val SITES_PATH = "/api/media/downloads/sites"

    private fun segment(value: String): String = URLEncoder.encode(value, "UTF-8").replace("+", "%20")
    private fun query(value: String): String = URLEncoder.encode(value, "UTF-8")

    fun lookupPath(query: String, kind: String): String =
        "/api/media/downloads/lookup?q=${query(query.trim())}&kind=${query(kind)}"

    fun showPath(tmdbId: Long): String = "/api/media/downloads/show/$tmdbId"

    fun searchPath(id: String): String = "$SEARCH_PATH/${segment(id)}"

    fun downloadPath(id: String): String = "$DOWNLOADS_PATH/${segment(id)}"

    fun retryPath(id: String): String = downloadPath(id) + "/retry"

    fun sitePath(id: String): String = "$SITES_PATH/${segment(id)}"

    fun catalogPath(query: String): String = "$SITES_PATH/catalog?q=${query(query.trim())}"

    fun grabBody(searchId: String, releaseId: String, libraryId: String? = null): JSONObject = JSONObject()
        .put("searchId", searchId)
        .put("releaseId", releaseId)
        .put("libraryId", libraryId ?: JSONObject.NULL)

    // The site form's answers, typed the way the web sends them: checkboxes as booleans, numbers
    // as numbers (blank = null), a select whose value is a whole number as a number.
    fun siteFieldsJson(fields: List<MediaSiteField>, values: Map<String, String>, checks: Map<String, Boolean>): JSONObject {
        val json = JSONObject()
        for (field in fields) {
            when (field.type) {
                "checkbox" -> json.put(field.name, checks[field.name] ?: field.checked)
                "number" -> {
                    val text = (values[field.name] ?: field.value).trim()
                    json.put(field.name, text.toLongOrNull() ?: text.toDoubleOrNull() ?: JSONObject.NULL)
                }
                "select" -> {
                    val text = values[field.name] ?: field.value
                    json.put(field.name, if (Regex("^-?\\d+$").matches(text)) text.toLong() else text)
                }
                else -> json.put(field.name, values[field.name] ?: field.value)
            }
        }
        return json
    }

    fun addSiteBody(definition: String, fields: JSONObject): JSONObject = JSONObject()
        .put("definition", definition)
        .put("name", JSONObject.NULL)
        .put("fields", fields)
}

// ---------------- Wording ----------------

enum class VpnTone { ON, BUSY, OFF, WARN }

internal object MediaDownloadFormat {
    private val ACTIVE = setOf("queued", "downloading", "seeding", "importing")

    fun isActive(state: String): Boolean = state in ACTIVE

    fun stateWord(state: String): String = when (state) {
        "queued" -> "waiting"
        "downloading" -> "downloading"
        "seeding" -> "sharing back"
        "importing" -> "adding to library"
        "done" -> "ready"
        "failed" -> "failed"
        "canceled" -> "canceled"
        else -> state
    }

    fun episodeCode(season: Int, episode: Int): String = "S%02dE%02d".format(season, episode)

    // "3.1 MB/s" / "640 KB/s", or null when nothing is moving.
    fun speedLabel(bytesPerSecond: Long?): String? {
        val bytes = bytesPerSecond ?: return null
        if (bytes <= 0) return null
        if (bytes >= 1_000_000) return "%.1f MB/s".format(bytes / 1_000_000.0)
        return "${maxOf(1L, Math.round(bytes / 1_000.0))} KB/s"
    }

    fun etaLabel(seconds: Long?): String? {
        val s = seconds ?: return null
        if (s <= 0) return null
        if (s < 90) return "about a minute left"
        if (s < 3600) return "${Math.round(s / 60.0)} min left"
        return "${s / 3600} h ${Math.round((s % 3600) / 60.0)} min left"
    }

    fun sizeLabel(bytes: Long?): String? = bytes?.takeIf { it > 0 }?.let(MediaPlaybackMath::formatSize)

    fun percentLabel(progress: Double): String {
        val tenths = Math.round(progress.coerceIn(0.0, 1.0) * 1000) / 10.0
        return if (tenths % 1.0 == 0.0) "${tenths.toLong()}%" else "$tenths%"
    }

    fun plural(count: Int, noun: String): String = if (count == 1) "1 $noun" else "$count ${noun}s"

    // "Downloading Bluey S02E05 · 42%", or "2 downloads in progress".
    fun bannerText(active: List<MediaDownload>): String? {
        val first = active.firstOrNull() ?: return null
        if (active.size > 1) return "${active.size} downloads in progress"
        val pct = Math.round(first.progress.coerceIn(0.0, 1.0) * 100)
        return "Downloading ${first.title}${first.subtitle?.let { " $it" }.orEmpty()} · $pct%"
    }

    // The VPN line on the Downloads screen, worded as the web's vpnMarkup.
    fun vpnLine(status: MediaDownloadServices?): Pair<VpnTone, String>? {
        status ?: return null
        val vpn = status.vpn
        val services = status.services
        return when {
            services == "notInstalled" -> VpnTone.WARN to
                "The VPN and torrent services are not installed on the server yet. Run core/downloads/install-downloads.sh on it once."
            vpn.state == "connected" -> VpnTone.ON to
                listOfNotNull("Connected", vpn.location, vpn.ip).joinToString(" · ")
            services == "starting" || vpn.state == "connecting" -> VpnTone.BUSY to "Connecting…"
            services == "failed" || vpn.state == "down" -> VpnTone.WARN to
                (vpn.error ?: "The VPN or torrent services failed to start. Check the server's log (journalctl -u sentinelcore-vpn).")
            services == "external" -> VpnTone.OFF to
                if (vpn.required) "qBittorrent and Prowlarr are run outside this server."
                else "qBittorrent and Prowlarr are run outside this server; the VPN is not checked."
            else -> VpnTone.OFF to "Off - starts by itself when someone searches or downloads."
        }
    }

    // The season the picker opens on: the first real season with an aired episode the library
    // lacks, else the first listed.
    fun startingSeason(show: MediaDownloadShow): Int? =
        (show.seasons.firstOrNull { season -> season.number > 0 && season.episodes.any { it.aired && !it.inLibrary } }
            ?: show.seasons.firstOrNull())?.number

    fun seasonTabLabel(season: MediaDownloadSeason): String =
        (if (season.number == 0) "Specials" else "S${season.number}") + if (season.isComplete) " ✓" else ""

    fun privacyLabel(privacy: String?): String? = when (privacy) {
        null -> null
        "public" -> "public"
        "semiPrivate" -> "semi-private"
        "private" -> "private"
        else -> privacy
    }

    // Digits only, like the web's /^\d+$/ check on the season / episode boxes.
    fun wholeNumber(text: String): Int? = text.trim().takeIf { it.matches(Regex("^\\d+$")) }?.toIntOrNull()
}
