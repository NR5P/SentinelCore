package com.sentinelcore.android.audio

import org.json.JSONObject

// Podcast objects - core/docs/MUSIC_AND_PODCASTS.md §5-§6.

data class PodcastSettings(
    val autoDownload: Boolean = true,
    val keepEpisodes: Int = 10,
    val voiceBoost: Boolean = false,
    val trimSilence: Boolean = false,
    val transcribe: Boolean = false,
) {
    fun toJson(): JSONObject = JSONObject()
        .put("autoDownload", autoDownload)
        .put("keepEpisodes", keepEpisodes)
        .put("voiceBoost", voiceBoost)
        .put("trimSilence", trimSilence)
        .put("transcribe", transcribe)
}

data class PodcastMySettings(
    // 0.5-3.0; null = the app's default podcast speed.
    val playbackSpeed: Double? = null,
    val skipIntroSeconds: Int = 0,
    val skipOutroSeconds: Int = 0,
    val notify: Boolean = true,
    val autoQueue: Boolean = false,
    val inBriefing: Boolean = false,
    val sort: String = "newest",
) {
    fun toJson(): JSONObject = JSONObject()
        .putOpt2("playbackSpeed", playbackSpeed)
        .put("skipIntroSeconds", skipIntroSeconds)
        .put("skipOutroSeconds", skipOutroSeconds)
        .put("notify", notify)
        .put("autoQueue", autoQueue)
        .put("inBriefing", inBriefing)
        .put("sort", sort)
}

data class Funding(val url: String, val title: String)

data class Podcast(
    val id: String,
    val feedUrl: String = "",
    val title: String,
    val author: String = "",
    val description: String = "",
    val artUrl: String? = null,
    val link: String? = null,
    val categories: List<String> = emptyList(),
    val language: String? = null,
    val explicit: Boolean = false,
    val episodeCount: Int = 0,
    val unplayedCount: Int = 0,
    val newestEpisodeAt: String? = null,
    val lastRefreshedAt: String? = null,
    val refreshError: String? = null,
    val subscribed: Boolean = false,
    val subscriberCount: Int = 0,
    val funding: List<Funding> = emptyList(),
    val settings: PodcastSettings? = null,
    val mySettings: PodcastMySettings? = null,
)

data class EpisodeDownload(
    // none, queued, downloading, done, failed.
    val state: String = "none",
    val progress: Double = 0.0,
    val error: String? = null,
    val enhanced: Boolean = false,
    val timeSavedSeconds: Double = 0.0,
)

data class EpisodeProgress(
    val positionSeconds: Double = 0.0,
    val durationSeconds: Double? = null,
    val percent: Double = 0.0,
    val played: Boolean = false,
    val updatedAt: String? = null,
)

data class Episode(
    val id: String,
    val podcastId: String,
    val podcastTitle: String = "",
    val title: String,
    val summary: String = "",
    val publishedAt: String? = null,
    val durationSeconds: Double = 0.0,
    val season: Int? = null,
    val episode: Int? = null,
    val episodeType: String = "full",
    val explicit: Boolean = false,
    val link: String? = null,
    val artUrl: String? = null,
    val audioUrl: String? = null,
    val mimeType: String? = null,
    val sizeBytes: Long? = null,
    val download: EpisodeDownload? = null,
    val archived: Boolean = false,
    val hasChapters: Boolean = false,
    val hasTranscript: Boolean = false,
    val transcriptSource: String? = null,
    val progress: EpisodeProgress? = null,
    val inQueue: Boolean = false,
    val bookmarkCount: Int = 0,
    // Only on GET /api/podcasts/episodes/{id}.
    val description: String? = null,
    val descriptionText: String? = null,
) {
    val played: Boolean get() = progress?.played == true

    val inProgress: Boolean get() = progress != null && !played && progress.positionSeconds > 0

    // What is left to hear, for "32 min left" and Fill my drive.
    val remainingSeconds: Double
        get() {
            val total = progress?.durationSeconds?.takeIf { it > 0 } ?: durationSeconds
            return if (played) 0.0 else (total - (progress?.positionSeconds ?: 0.0)).coerceAtLeast(0.0)
        }

    // The point to start from: where this person left off, unless they finished it.
    val resumeSeconds: Double
        get() = if (played) 0.0 else progress?.positionSeconds ?: 0.0
}

data class EpisodePage(val items: List<Episode>, val total: Int, val seasons: List<Int>)

data class DirectoryShow(
    val itunesId: Long?,
    val title: String,
    val author: String,
    val artUrl: String?,
    val feedUrl: String?,
    val genre: String?,
    val episodeCount: Int?,
    val podcastId: String?,
    val subscribed: Boolean,
)

data class DirectoryGenre(val id: Long, val name: String)

data class PodcastPreview(val podcast: Podcast, val episodes: List<Episode>)

data class Bookmark(
    val id: String,
    val episodeId: String,
    val episodeTitle: String,
    val podcastId: String?,
    val podcastTitle: String,
    val positionSeconds: Double,
    val note: String,
    val transcriptSnippet: String?,
    val createdAt: String?,
    val artUrl: String?,
)

data class PodcastHome(
    val upNext: List<Episode> = emptyList(),
    val continueListening: List<Episode> = emptyList(),
    val newEpisodes: List<Episode> = emptyList(),
    val shortOnes: List<Episode> = emptyList(),
    val downloaded: List<Episode> = emptyList(),
    val subscriptions: List<Podcast> = emptyList(),
    val bookmarks: List<Bookmark> = emptyList(),
    val timeSavedSeconds: Double = 0.0,
)

data class Chapter(
    val startSeconds: Double,
    val endSeconds: Double?,
    val title: String,
    val imageUrl: String?,
    val url: String?,
)

data class TranscriptSegment(val start: Double, val end: Double, val speaker: String?, val text: String)

data class Transcript(val source: String?, val language: String?, val segments: List<TranscriptSegment>)

data class TranscriptHit(
    val episodeId: String,
    val episodeTitle: String,
    val podcastId: String?,
    val podcastTitle: String,
    val startSeconds: Double,
    val snippet: String,
)

data class Clip(val id: String, val url: String, val durationSeconds: Double, val title: String? = null, val createdAt: String? = null)

data class OpmlFailure(val feedUrl: String, val error: String)

data class OpmlImportResult(val added: Int, val existing: Int, val failed: List<OpmlFailure>)

data class FillResult(val episodes: List<Episode>, val totalSeconds: Double)

data class PodcastStatus(
    val enabled: Boolean,
    val ffmpegAvailable: Boolean,
    val refreshing: Boolean,
    val lastRefreshAt: String?,
    val downloadFolder: String?,
    val storageBytes: Long,
    val maxStorageBytes: Long,
    val downloadsActive: Int,
    val downloadsQueued: Int,
    val transcriptsEnabled: Boolean,
    val transcriptsAvailable: Boolean,
    val transcriptsError: String?,
    val transcriptsQueued: Int,
    val podcastCount: Int,
    val episodeCount: Int,
)

// ---------------- Parsing ----------------

internal fun parsePodcastSettings(json: JSONObject): PodcastSettings = PodcastSettings(
    autoDownload = json.bool("autoDownload", true),
    keepEpisodes = json.int("keepEpisodes") ?: 10,
    voiceBoost = json.bool("voiceBoost"),
    trimSilence = json.bool("trimSilence"),
    transcribe = json.bool("transcribe"),
)

internal fun parsePodcastMySettings(json: JSONObject): PodcastMySettings = PodcastMySettings(
    playbackSpeed = json.dbl("playbackSpeed"),
    skipIntroSeconds = json.int("skipIntroSeconds") ?: 0,
    skipOutroSeconds = json.int("skipOutroSeconds") ?: 0,
    notify = json.bool("notify", true),
    autoQueue = json.bool("autoQueue"),
    inBriefing = json.bool("inBriefing"),
    sort = json.str("sort") ?: "newest",
)

// The preview route's podcast has no id (§6): it is keyed by its feed instead.
internal fun parsePodcast(json: JSONObject): Podcast = Podcast(
    id = json.str("id") ?: json.text("feedUrl"),
    feedUrl = json.text("feedUrl"),
    title = json.text("title", "Podcast"),
    author = json.text("author"),
    description = json.text("description"),
    artUrl = json.str("artUrl"),
    link = json.str("link"),
    categories = json.strings("categories"),
    language = json.str("language"),
    explicit = json.bool("explicit"),
    episodeCount = json.int("episodeCount") ?: 0,
    unplayedCount = json.int("unplayedCount") ?: 0,
    newestEpisodeAt = json.str("newestEpisodeAt"),
    lastRefreshedAt = json.str("lastRefreshedAt"),
    refreshError = json.str("refreshError"),
    subscribed = json.bool("subscribed"),
    subscriberCount = json.int("subscriberCount") ?: 0,
    funding = json.list("funding") { Funding(it.text("url"), it.str("title") ?: "Support the show") }.filter { it.url.isNotBlank() },
    settings = json.obj("settings")?.let(::parsePodcastSettings),
    mySettings = json.obj("mySettings")?.let(::parsePodcastMySettings),
)

internal fun parseEpisodeProgress(json: JSONObject): EpisodeProgress = EpisodeProgress(
    positionSeconds = json.dbl("positionSeconds") ?: 0.0,
    durationSeconds = json.dbl("durationSeconds"),
    percent = json.dbl("percent") ?: 0.0,
    played = json.bool("played"),
    updatedAt = json.str("updatedAt"),
)

internal fun parseEpisode(json: JSONObject): Episode = Episode(
    id = json.getString("id"),
    podcastId = json.text("podcastId"),
    podcastTitle = json.text("podcastTitle"),
    title = json.text("title", "Episode"),
    summary = json.text("summary"),
    publishedAt = json.str("publishedAt"),
    durationSeconds = json.dbl("durationSeconds") ?: 0.0,
    season = json.int("season"),
    episode = json.int("episode"),
    episodeType = json.str("episodeType") ?: "full",
    explicit = json.bool("explicit"),
    link = json.str("link"),
    artUrl = json.str("artUrl"),
    audioUrl = json.str("audioUrl"),
    mimeType = json.str("mimeType"),
    sizeBytes = json.long("sizeBytes"),
    download = json.obj("download")?.let {
        EpisodeDownload(
            state = it.str("state") ?: "none",
            progress = it.dbl("progress") ?: 0.0,
            error = it.str("error"),
            enhanced = it.bool("enhanced"),
            timeSavedSeconds = it.dbl("timeSavedSeconds") ?: 0.0,
        )
    },
    archived = json.bool("archived"),
    hasChapters = json.bool("hasChapters"),
    hasTranscript = json.bool("hasTranscript"),
    transcriptSource = json.str("transcriptSource"),
    progress = json.obj("progress")?.let(::parseEpisodeProgress),
    inQueue = json.bool("inQueue"),
    bookmarkCount = json.int("bookmarkCount") ?: 0,
    description = json.str("description"),
    descriptionText = json.str("descriptionText"),
)

internal fun parseEpisodePage(json: JSONObject): EpisodePage {
    val items = json.list("items", ::parseEpisode)
    return EpisodePage(items, json.int("total") ?: items.size, json.ints("seasons"))
}

internal fun parseDirectoryShow(json: JSONObject): DirectoryShow = DirectoryShow(
    itunesId = json.long("itunesId"),
    title = json.text("title", "Podcast"),
    author = json.text("author"),
    artUrl = json.str("artUrl"),
    feedUrl = json.str("feedUrl"),
    genre = json.str("genre"),
    episodeCount = json.int("episodeCount"),
    podcastId = json.str("podcastId"),
    subscribed = json.bool("subscribed"),
)

internal fun parseBookmark(json: JSONObject): Bookmark = Bookmark(
    id = json.getString("id"),
    episodeId = json.text("episodeId"),
    episodeTitle = json.text("episodeTitle"),
    podcastId = json.str("podcastId"),
    podcastTitle = json.text("podcastTitle"),
    positionSeconds = json.dbl("positionSeconds") ?: 0.0,
    note = json.text("note"),
    transcriptSnippet = json.str("transcriptSnippet"),
    createdAt = json.str("createdAt"),
    artUrl = json.str("artUrl"),
)

internal fun parsePodcastHome(json: JSONObject): PodcastHome = PodcastHome(
    upNext = json.list("upNext", ::parseEpisode),
    continueListening = json.list("continueListening", ::parseEpisode),
    newEpisodes = json.list("newEpisodes", ::parseEpisode),
    shortOnes = json.list("shortOnes", ::parseEpisode),
    downloaded = json.list("downloaded", ::parseEpisode),
    subscriptions = json.list("subscriptions", ::parsePodcast),
    bookmarks = json.list("bookmarks", ::parseBookmark),
    timeSavedSeconds = json.dbl("timeSavedSeconds") ?: 0.0,
)

internal fun parseChapter(json: JSONObject): Chapter = Chapter(
    startSeconds = json.dbl("startSeconds") ?: 0.0,
    endSeconds = json.dbl("endSeconds"),
    title = json.text("title", "Chapter"),
    imageUrl = json.str("imageUrl"),
    url = json.str("url"),
)

internal fun parseTranscript(json: JSONObject): Transcript = Transcript(
    source = json.str("source"),
    language = json.str("language"),
    segments = json.list("segments") {
        TranscriptSegment(it.dbl("start") ?: 0.0, it.dbl("end") ?: 0.0, it.str("speaker"), it.text("text"))
    },
)

internal fun parseTranscriptHit(json: JSONObject): TranscriptHit = TranscriptHit(
    episodeId = json.text("episodeId"),
    episodeTitle = json.text("episodeTitle"),
    podcastId = json.str("podcastId"),
    podcastTitle = json.text("podcastTitle"),
    startSeconds = json.dbl("startSeconds") ?: 0.0,
    snippet = json.text("snippet"),
)

internal fun parseClip(json: JSONObject): Clip = Clip(
    id = json.text("id"),
    url = json.text("url"),
    durationSeconds = json.dbl("durationSeconds") ?: 0.0,
    title = json.str("title"),
    createdAt = json.str("createdAt"),
)

internal fun parsePodcastStatus(json: JSONObject): PodcastStatus {
    val transcripts = json.obj("transcripts")
    return PodcastStatus(
        enabled = json.bool("enabled", true),
        ffmpegAvailable = json.bool("ffmpegAvailable", true),
        refreshing = json.bool("refreshing"),
        lastRefreshAt = json.str("lastRefreshAt"),
        downloadFolder = json.str("downloadFolder"),
        storageBytes = json.long("storageBytes") ?: 0L,
        maxStorageBytes = json.long("maxStorageBytes") ?: 0L,
        downloadsActive = json.int("downloadsActive") ?: 0,
        downloadsQueued = json.int("downloadsQueued") ?: 0,
        transcriptsEnabled = transcripts?.bool("enabled") ?: false,
        transcriptsAvailable = transcripts?.bool("available") ?: false,
        transcriptsError = transcripts?.str("error"),
        transcriptsQueued = transcripts?.int("queued") ?: 0,
        podcastCount = json.int("podcastCount") ?: 0,
        episodeCount = json.int("episodeCount") ?: 0,
    )
}
