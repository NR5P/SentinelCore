package com.sentinelcore.android.data

import org.json.JSONArray
import org.json.JSONObject
import java.net.URLEncoder

// The Movies & TV library - see core/docs/MOVIES.md, which is the contract these follow. Every
// field the contract marks optional is nullable or defaulted here, so an older or partial server
// answer never throws its way out of a parse. The parse functions are `internal` top-level
// functions rather than repository privates so the JVM unit tests can feed them the contract's
// sample JSON directly.

data class MediaLibrary(
    val id: String,
    val name: String,
    // "movies" or "shows".
    val kind: String,
    val path: String?,
    val itemCount: Int = 0,
    val lastScanFinishedAt: String? = null,
    val lastScanError: String? = null,
)

// This account's progress on one item. Null on a summary means never played.
data class MediaProgress(
    val positionSeconds: Double,
    val durationSeconds: Double?,
    // 0-100.
    val percent: Double,
    val watched: Boolean,
    val updatedAt: String? = null,
)

data class MediaItemSummary(
    val id: String,
    // "movie", "show" or "episode".
    val kind: String,
    val libraryId: String?,
    val title: String,
    val sortTitle: String? = null,
    val year: Int? = null,
    val genres: List<String> = emptyList(),
    val rating: Double? = null,
    val certification: String? = null,
    val runtimeMinutes: Int? = null,
    val durationSeconds: Double? = null,
    val posterUrl: String? = null,
    val backdropUrl: String? = null,
    val thumbUrl: String? = null,
    val addedAt: String? = null,
    val progress: MediaProgress? = null,
    // Episodes only.
    val showId: String? = null,
    val showTitle: String? = null,
    val seasonNumber: Int? = null,
    val episodeNumber: Int? = null,
    val airDate: String? = null,
    // Shows only.
    val seasonCount: Int? = null,
    val episodeCount: Int? = null,
    val watchedEpisodeCount: Int? = null,
    val nextEpisodeId: String? = null,
) {
    val isMovie: Boolean get() = kind == "movie"
    val isShow: Boolean get() = kind == "show"
    val isEpisode: Boolean get() = kind == "episode"

    // "S1 · E3" - the episode's place in its show, or null for anything that isn't one.
    val episodeCode: String?
        get() = if (isEpisode && seasonNumber != null && episodeNumber != null) "S$seasonNumber · E$episodeNumber" else null

    // Episodes a show still has to offer this account; null when the server didn't say.
    val unwatchedEpisodeCount: Int?
        get() {
            val total = episodeCount ?: return null
            return (total - (watchedEpisodeCount ?: 0)).coerceAtLeast(0)
        }

    // A show counts as watched once every episode is; anything else by its own progress.
    val isWatched: Boolean
        get() = if (isShow) {
            val total = episodeCount ?: 0
            total > 0 && (watchedEpisodeCount ?: 0) >= total
        } else {
            progress?.watched == true
        }

    // 0-1 for a progress bar, or null when there is nothing part-way to show.
    val progressFraction: Float?
        get() = progress
            ?.takeIf { !it.watched && it.percent > 0.0 }
            ?.let { (it.percent / 100.0).coerceIn(0.0, 1.0).toFloat() }
}

data class MediaCastMember(val name: String, val character: String? = null)

data class MediaFileInfo(
    val container: String? = null,
    val videoCodec: String? = null,
    val videoProfile: String? = null,
    val width: Int? = null,
    val height: Int? = null,
    val audioCodec: String? = null,
    val audioChannels: Int? = null,
    val sizeBytes: Long? = null,
    val bitrate: Long? = null,
    // Admin only; null for everyone else.
    val path: String? = null,
)

data class MediaAudioTrack(
    val index: Int,
    val codec: String? = null,
    val channels: Int? = null,
    val language: String? = null,
    val label: String? = null,
    val isDefault: Boolean = false,
)

data class MediaSubtitle(
    val index: Int,
    val language: String? = null,
    val label: String? = null,
    val url: String,
    val forced: Boolean = false,
    val external: Boolean = false,
)

data class MediaSeason(
    val number: Int,
    val name: String,
    val episodeCount: Int = 0,
    val watchedCount: Int = 0,
    val posterUrl: String? = null,
    val episodes: List<MediaItemSummary> = emptyList(),
)

data class MediaItemDetail(
    val summary: MediaItemSummary,
    val overview: String? = null,
    val tagline: String? = null,
    val releaseDate: String? = null,
    val cast: List<MediaCastMember> = emptyList(),
    val tmdbId: Long? = null,
    val tmdbUrl: String? = null,
    // Null for shows.
    val file: MediaFileInfo? = null,
    val audioTracks: List<MediaAudioTrack> = emptyList(),
    val subtitles: List<MediaSubtitle> = emptyList(),
    // Null for movies and episodes.
    val seasons: List<MediaSeason>? = null,
)

data class MediaHome(
    val continueWatching: List<MediaItemSummary> = emptyList(),
    val recentMovies: List<MediaItemSummary> = emptyList(),
    val recentShows: List<MediaItemSummary> = emptyList(),
    val recentEpisodes: List<MediaItemSummary> = emptyList(),
    val featured: MediaItemSummary? = null,
    val movieCount: Int = 0,
    val showCount: Int = 0,
    val libraries: List<MediaLibrary> = emptyList(),
)

data class MediaItemsPage(
    val items: List<MediaItemSummary>,
    val total: Int,
    val genres: List<String> = emptyList(),
)

data class MediaPlayback(
    val itemId: String,
    val title: String,
    val subtitle: String? = null,
    val durationSeconds: Double? = null,
    // Where the stream begins. In hls mode the playlist's clock starts here - see MediaPlaybackMath.
    val startSeconds: Double = 0.0,
    val resumeSeconds: Double? = null,
    // "direct" or "hls".
    val mode: String,
    val url: String,
    val mimeType: String? = null,
    val video: String? = null,
    val audio: String? = null,
    val subtitles: List<MediaSubtitle> = emptyList(),
    val nextEpisodeId: String? = null,
    val nextEpisodeTitle: String? = null,
    val backdropUrl: String? = null,
    val posterUrl: String? = null,
) {
    val isHls: Boolean get() = mode == "hls"
}

data class MediaScan(
    val phase: String,
    val libraryName: String? = null,
    val processed: Int = 0,
    val total: Int = 0,
    val startedAt: String? = null,
)

data class MediaStatus(
    val enabled: Boolean,
    val ffmpegAvailable: Boolean = false,
    val tmdbConfigured: Boolean = false,
    val scanning: Boolean = false,
    val scan: MediaScan? = null,
    val libraries: List<MediaLibrary> = emptyList(),
    val movieCount: Int = 0,
    val showCount: Int = 0,
    val episodeCount: Int = 0,
    val lastScanFinishedAt: String? = null,
)

// ---------------- Parsing ----------------

private fun JSONObject.stringOrNull(key: String): String? =
    if (!has(key) || isNull(key)) null else optString(key).takeIf { it.isNotBlank() }

private fun JSONObject.intOrNull(key: String): Int? =
    if (!has(key) || isNull(key)) null else optInt(key)

private fun JSONObject.longOrNull(key: String): Long? =
    if (!has(key) || isNull(key)) null else optLong(key)

private fun JSONObject.doubleOrNull(key: String): Double? =
    if (!has(key) || isNull(key)) null else optDouble(key).takeUnless { it.isNaN() }

private fun JSONObject.objectOrNull(key: String): JSONObject? =
    if (!has(key) || isNull(key)) null else optJSONObject(key)

private fun JSONObject.arrayOrNull(key: String): JSONArray? =
    if (!has(key) || isNull(key)) null else optJSONArray(key)

private fun JSONArray?.strings(): List<String> {
    val array = this ?: return emptyList()
    return buildList { for (i in 0 until array.length()) array.optString(i).takeIf { it.isNotBlank() }?.let(::add) }
}

private fun <T> JSONArray?.objects(parse: (JSONObject) -> T): List<T> {
    val array = this ?: return emptyList()
    return buildList { for (i in 0 until array.length()) array.optJSONObject(i)?.let { add(parse(it)) } }
}

internal fun parseMediaLibrary(json: JSONObject): MediaLibrary = MediaLibrary(
    id = json.optString("id"),
    name = json.optString("name"),
    kind = json.optString("kind"),
    path = json.stringOrNull("path"),
    itemCount = json.optInt("itemCount"),
    lastScanFinishedAt = json.stringOrNull("lastScanFinishedAt"),
    lastScanError = json.stringOrNull("lastScanError"),
)

internal fun parseMediaProgress(json: JSONObject?): MediaProgress? {
    json ?: return null
    return MediaProgress(
        positionSeconds = json.optDouble("positionSeconds", 0.0),
        durationSeconds = json.doubleOrNull("durationSeconds"),
        percent = json.optDouble("percent", 0.0),
        watched = json.optBoolean("watched"),
        updatedAt = json.stringOrNull("updatedAt"),
    )
}

internal fun parseMediaItem(json: JSONObject): MediaItemSummary = MediaItemSummary(
    id = json.optString("id"),
    kind = json.optString("kind"),
    libraryId = json.stringOrNull("libraryId"),
    title = json.optString("title"),
    sortTitle = json.stringOrNull("sortTitle"),
    year = json.intOrNull("year"),
    genres = json.arrayOrNull("genres").strings(),
    rating = json.doubleOrNull("rating"),
    certification = json.stringOrNull("certification"),
    runtimeMinutes = json.intOrNull("runtimeMinutes"),
    durationSeconds = json.doubleOrNull("durationSeconds"),
    posterUrl = json.stringOrNull("posterUrl"),
    backdropUrl = json.stringOrNull("backdropUrl"),
    thumbUrl = json.stringOrNull("thumbUrl"),
    addedAt = json.stringOrNull("addedAt"),
    progress = parseMediaProgress(json.objectOrNull("progress")),
    showId = json.stringOrNull("showId"),
    showTitle = json.stringOrNull("showTitle"),
    seasonNumber = json.intOrNull("seasonNumber"),
    episodeNumber = json.intOrNull("episodeNumber"),
    airDate = json.stringOrNull("airDate"),
    seasonCount = json.intOrNull("seasonCount"),
    episodeCount = json.intOrNull("episodeCount"),
    watchedEpisodeCount = json.intOrNull("watchedEpisodeCount"),
    nextEpisodeId = json.stringOrNull("nextEpisodeId"),
)

internal fun parseMediaItems(array: JSONArray?): List<MediaItemSummary> = array.objects(::parseMediaItem)

internal fun parseMediaFileInfo(json: JSONObject?): MediaFileInfo? {
    json ?: return null
    return MediaFileInfo(
        container = json.stringOrNull("container"),
        videoCodec = json.stringOrNull("videoCodec"),
        videoProfile = json.stringOrNull("videoProfile"),
        width = json.intOrNull("width"),
        height = json.intOrNull("height"),
        audioCodec = json.stringOrNull("audioCodec"),
        audioChannels = json.intOrNull("audioChannels"),
        sizeBytes = json.longOrNull("sizeBytes"),
        bitrate = json.longOrNull("bitrate"),
        path = json.stringOrNull("path"),
    )
}

internal fun parseMediaSubtitle(json: JSONObject): MediaSubtitle = MediaSubtitle(
    index = json.optInt("index"),
    language = json.stringOrNull("language"),
    label = json.stringOrNull("label"),
    url = json.optString("url"),
    forced = json.optBoolean("forced"),
    external = json.optBoolean("external"),
)

internal fun parseMediaSeason(json: JSONObject): MediaSeason = MediaSeason(
    number = json.optInt("number"),
    name = json.stringOrNull("name") ?: "Season ${json.optInt("number")}",
    episodeCount = json.optInt("episodeCount"),
    watchedCount = json.optInt("watchedCount"),
    posterUrl = json.stringOrNull("posterUrl"),
    episodes = parseMediaItems(json.arrayOrNull("episodes")),
)

internal fun parseMediaItemDetail(json: JSONObject): MediaItemDetail = MediaItemDetail(
    summary = parseMediaItem(json),
    overview = json.stringOrNull("overview"),
    tagline = json.stringOrNull("tagline"),
    releaseDate = json.stringOrNull("releaseDate"),
    cast = json.arrayOrNull("cast").objects { member ->
        MediaCastMember(name = member.optString("name"), character = member.stringOrNull("character"))
    },
    tmdbId = json.longOrNull("tmdbId"),
    tmdbUrl = json.stringOrNull("tmdbUrl"),
    file = parseMediaFileInfo(json.objectOrNull("file")),
    audioTracks = json.arrayOrNull("audioTracks").objects { track ->
        MediaAudioTrack(
            index = track.optInt("index"),
            codec = track.stringOrNull("codec"),
            channels = track.intOrNull("channels"),
            language = track.stringOrNull("language"),
            label = track.stringOrNull("label"),
            isDefault = track.optBoolean("isDefault"),
        )
    },
    subtitles = json.arrayOrNull("subtitles").objects(::parseMediaSubtitle),
    seasons = json.arrayOrNull("seasons")?.objects(::parseMediaSeason),
)

internal fun parseMediaHome(json: JSONObject): MediaHome = MediaHome(
    continueWatching = parseMediaItems(json.arrayOrNull("continueWatching")),
    recentMovies = parseMediaItems(json.arrayOrNull("recentMovies")),
    recentShows = parseMediaItems(json.arrayOrNull("recentShows")),
    recentEpisodes = parseMediaItems(json.arrayOrNull("recentEpisodes")),
    featured = json.objectOrNull("featured")?.let(::parseMediaItem),
    movieCount = json.optInt("movieCount"),
    showCount = json.optInt("showCount"),
    libraries = json.arrayOrNull("libraries").objects(::parseMediaLibrary),
)

internal fun parseMediaItemsPage(json: JSONObject): MediaItemsPage = MediaItemsPage(
    items = parseMediaItems(json.arrayOrNull("items")),
    total = json.optInt("total"),
    genres = json.arrayOrNull("genres").strings(),
)

internal fun parseMediaPlayback(json: JSONObject): MediaPlayback = MediaPlayback(
    itemId = json.optString("itemId"),
    title = json.optString("title"),
    subtitle = json.stringOrNull("subtitle"),
    durationSeconds = json.doubleOrNull("durationSeconds"),
    startSeconds = json.optDouble("startSeconds", 0.0).takeUnless { it.isNaN() } ?: 0.0,
    resumeSeconds = json.doubleOrNull("resumeSeconds"),
    mode = json.optString("mode", "direct"),
    url = json.optString("url"),
    mimeType = json.stringOrNull("mimeType"),
    video = json.stringOrNull("video"),
    audio = json.stringOrNull("audio"),
    subtitles = json.arrayOrNull("subtitles").objects(::parseMediaSubtitle),
    nextEpisodeId = json.stringOrNull("nextEpisodeId"),
    nextEpisodeTitle = json.stringOrNull("nextEpisodeTitle"),
    backdropUrl = json.stringOrNull("backdropUrl"),
    posterUrl = json.stringOrNull("posterUrl"),
)

internal fun parseMediaStatus(json: JSONObject): MediaStatus = MediaStatus(
    enabled = json.optBoolean("enabled"),
    ffmpegAvailable = json.optBoolean("ffmpegAvailable"),
    tmdbConfigured = json.optBoolean("tmdbConfigured"),
    scanning = json.optBoolean("scanning"),
    scan = json.objectOrNull("scan")?.let { scan ->
        MediaScan(
            phase = scan.optString("phase", "idle"),
            libraryName = scan.stringOrNull("libraryName"),
            processed = scan.optInt("processed"),
            total = scan.optInt("total"),
            startedAt = scan.stringOrNull("startedAt"),
        )
    },
    libraries = json.arrayOrNull("libraries").objects(::parseMediaLibrary),
    movieCount = json.optInt("movieCount"),
    showCount = json.optInt("showCount"),
    episodeCount = json.optInt("episodeCount"),
    lastScanFinishedAt = json.stringOrNull("lastScanFinishedAt"),
)

// The arithmetic the player and the detail screen share, kept free of Android so it is testable.
internal object MediaPlaybackMath {
    // Below this the server saves nothing anyway, so there is nothing worth offering to resume.
    const val MIN_RESUME_SECONDS = 30.0

    // The saved point to offer as "Resume", or null when the item should simply play from the
    // start: never played, finished (watched clears the resume point), or barely begun.
    fun resumeSeconds(progress: MediaProgress?): Double? {
        progress ?: return null
        if (progress.watched) return null
        return progress.positionSeconds.takeIf { it >= MIN_RESUME_SECONDS }
    }

    // What the player's own clock should be set to once the stream is prepared. A direct file is
    // the whole movie, so the player seeks to the start point itself; an HLS playlist already
    // begins at startSeconds, so its clock starts at zero.
    fun initialSeekMillis(mode: String, startSeconds: Double): Long =
        if (mode == "hls") 0L else (startSeconds * 1000).toLong().coerceAtLeast(0L)

    // The position in the movie, given the player's own position. The HLS playlist's clock runs
    // from startSeconds, not from the beginning of the file - see MOVIES.md.
    fun mediaPositionSeconds(mode: String, startSeconds: Double, playerPositionMillis: Long): Double {
        val playerSeconds = playerPositionMillis.coerceAtLeast(0L) / 1000.0
        return if (mode == "hls") startSeconds + playerSeconds else playerSeconds
    }

    // Whether a reported position is worth sending at all: the server discards anything under
    // 30 seconds, and a position with no duration can't become a percentage.
    fun worthReporting(positionSeconds: Double, durationSeconds: Double?): Boolean =
        positionSeconds >= MIN_RESUME_SECONDS && (durationSeconds ?: 0.0) > 0.0

    // "1:23:45" or "23:45".
    fun formatClock(seconds: Double): String {
        val total = seconds.toLong().coerceAtLeast(0L)
        val hours = total / 3600
        val minutes = (total % 3600) / 60
        val secs = total % 60
        return if (hours > 0) "%d:%02d:%02d".format(hours, minutes, secs) else "%d:%02d".format(minutes, secs)
    }

    // "1h 42m" for a runtime in minutes.
    fun formatRuntime(minutes: Int): String {
        val hours = minutes / 60
        val rest = minutes % 60
        return when {
            hours > 0 && rest > 0 -> "${hours}h ${rest}m"
            hours > 0 -> "${hours}h"
            else -> "${rest}m"
        }
    }

    // "1.7 GB" / "640 MB" for a file size.
    fun formatSize(bytes: Long): String {
        val gb = bytes / 1_000_000_000.0
        return if (gb >= 1.0) "%.1f GB".format(gb) else "%d MB".format(bytes / 1_000_000)
    }

    // The largest power-of-two downsample that still leaves the image at least targetWidth wide,
    // so a 2000px TMDB poster decodes at a size a 140dp grid cell can use without wasting memory.
    fun imageSampleSize(sourceWidth: Int, targetWidth: Int): Int {
        if (sourceWidth <= 0 || targetWidth <= 0) return 1
        var sample = 1
        while (sourceWidth / (sample * 2) >= targetWidth) sample *= 2
        return sample
    }
}

// The request side of the contract: paths and query strings, kept pure so they're testable.
internal object MediaApi {
    const val PAGE_SIZE = 60

    // Contract defaults for Android (MOVIES.md, Playback). The app narrows these to what this
    // particular phone has decoders for - see mediaCodecs.
    const val VIDEO_CODECS = "h264,hevc"
    const val AUDIO_CODECS = "aac,ac3,eac3,mp3"

    fun segment(value: String): String = URLEncoder.encode(value, "UTF-8").replace("+", "%20")

    private fun query(value: String): String = URLEncoder.encode(value, "UTF-8")

    // Title reads A to Z; everything else reads newest / highest first.
    fun defaultOrder(sort: String): String = if (sort == "title") "asc" else "desc"

    fun itemsPath(
        kind: String?,
        query: String? = null,
        genre: String? = null,
        sort: String = "title",
        order: String = defaultOrder(sort),
        unwatched: Boolean = false,
        offset: Int = 0,
        limit: Int = PAGE_SIZE,
    ): String {
        val parts = buildList {
            kind?.let { add("kind=${query(it)}") }
            query?.trim()?.takeIf { it.isNotEmpty() }?.let { add("q=${query(it)}") }
            genre?.takeIf { it.isNotBlank() }?.let { add("genre=${query(it)}") }
            add("sort=${query(sort)}")
            add("order=${query(order)}")
            if (unwatched) add("unwatched=true")
            add("offset=$offset")
            add("limit=$limit")
        }
        return "/api/media/items?" + parts.joinToString("&")
    }

    fun itemPath(id: String): String = "/api/media/items/${segment(id)}"

    // start null resumes from the saved progress (the server's own rule); 0 starts over.
    fun playbackPath(
        id: String,
        start: Double?,
        video: String = VIDEO_CODECS,
        audio: String = AUDIO_CODECS,
    ): String {
        val parts = buildList {
            if (start != null) add("start=${formatSeconds(start.coerceAtLeast(0.0))}")
            add("video=$video")
            add("audio=$audio")
        }
        return itemPath(id) + "/playback?" + parts.joinToString("&")
    }

    fun seasonWatchedPath(showId: String, season: Int): String = itemPath(showId) + "/seasons/$season/watched"

    fun formatSeconds(seconds: Double): String =
        if (seconds % 1.0 == 0.0) seconds.toLong().toString() else "%.1f".format(java.util.Locale.ROOT, seconds)

    // The codec lists for this phone: the contract's Android defaults minus any decoder the phone
    // turns out not to have. HEVC and the Dolby codecs are the ones phones vary on; a phone that
    // claims AC-3 and can't decode it plays in silence, so it is better to have the server convert.
    fun mediaCodecs(hasHevc: Boolean, hasAc3: Boolean, hasEac3: Boolean): Pair<String, String> {
        val video = if (hasHevc) VIDEO_CODECS else "h264"
        val audio = listOfNotNull("aac", "ac3".takeIf { hasAc3 }, "eac3".takeIf { hasEac3 }, "mp3").joinToString(",")
        return video to audio
    }
}
