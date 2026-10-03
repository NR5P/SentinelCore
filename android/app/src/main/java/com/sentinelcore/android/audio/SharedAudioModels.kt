package com.sentinelcore.android.audio

import org.json.JSONArray
import org.json.JSONObject

// What the two tabs share - core/docs/MUSIC_AND_PODCASTS.md §1, §7-§9.

data class AudioEnabled(val music: Boolean, val podcasts: Boolean) {
    val any: Boolean get() = music || podcasts

    companion object {
        val OFF = AudioEnabled(music = false, podcasts = false)
    }
}

// One entry in a queue as the server stores it: "track" or "episode" plus its id.
data class QueueRef(val type: String, val id: String) {
    fun toJson(): JSONObject = JSONObject().put("type", type).put("id", id)
}

data class PlayContext(val type: String, val id: String? = null, val name: String? = null) {
    fun toJson(): JSONObject = JSONObject().put("type", type).putOpt2("id", id).putOpt2("name", name)
}

data class NowPlaying(
    val deviceId: String,
    val deviceName: String,
    // music or podcast.
    val kind: String,
    val queue: List<QueueRef>,
    val index: Int,
    val positionSeconds: Double,
    val playing: Boolean,
    val shuffle: Boolean = false,
    val repeat: String = "off",
    val speed: Double = 1.0,
    val context: PlayContext? = null,
    val updatedAt: String? = null,
) {
    fun toJson(): JSONObject = JSONObject()
        .put("deviceId", deviceId)
        .put("deviceName", deviceName)
        .put("kind", kind)
        .put("queue", JSONArray().apply { queue.forEach { put(it.toJson()) } })
        .put("index", index)
        .put("positionSeconds", positionSeconds)
        .put("playing", playing)
        .put("shuffle", shuffle)
        .put("repeat", repeat)
        .put("speed", speed)
        .putOpt2("context", context?.toJson())
}

data class NowPlayingResolved(val nowPlaying: NowPlaying, val tracks: List<Track>, val episodes: List<Episode>)

data class SpeakerState(
    val playing: Boolean,
    val title: String?,
    val subtitle: String?,
    val artUrl: String?,
    val positionSeconds: Double,
    val durationSeconds: Double?,
    val volume: Double,
    val kind: String?,
    val itemId: String?,
) {
    fun toJson(): JSONObject = JSONObject()
        .put("playing", playing)
        .putOpt2("title", title)
        .putOpt2("subtitle", subtitle)
        .putOpt2("artUrl", artUrl)
        .put("positionSeconds", positionSeconds)
        .putOpt2("durationSeconds", durationSeconds)
        .put("volume", volume)
        .putOpt2("kind", kind)
        .putOpt2("itemId", itemId)
}

data class Speaker(
    val deviceId: String,
    val name: String,
    val kind: String,
    val online: Boolean,
    val allowAnnouncements: Boolean,
    val volume: Double,
    val lastSeenAt: String?,
    val state: SpeakerState?,
)

data class SpeakerCommand(
    val seq: Long = 0,
    val at: String? = null,
    val from: String? = null,
    val action: String,
    val queue: List<QueueRef> = emptyList(),
    val index: Int = 0,
    val positionSeconds: Double = 0.0,
    val shuffle: Boolean = false,
    val volume: Double? = null,
    val rampSeconds: Double = 0.0,
    val text: String? = null,
    val context: PlayContext? = null,
) {
    fun toJson(): JSONObject = JSONObject()
        .put("action", action)
        .apply {
            if (queue.isNotEmpty()) put("queue", JSONArray().apply { queue.forEach { put(it.toJson()) } })
            if (action == "play" || action == "seek") put("positionSeconds", positionSeconds)
            if (action == "play") {
                put("index", index)
                put("shuffle", shuffle)
            }
            volume?.let { put("volume", it) }
            if (rampSeconds > 0) put("rampSeconds", rampSeconds)
            text?.let { put("text", it) }
            context?.let { put("context", it.toJson()) }
        }
}

data class CommandBatch(val commands: List<SpeakerCommand>, val latestSeq: Long?)

data class AlarmSource(val type: String, val id: String? = null, val name: String? = null) {
    fun toJson(): JSONObject = JSONObject().put("type", type).putOpt2("id", id).putOpt2("name", name)
}

data class WakeAlarm(
    val id: String? = null,
    val name: String = "Alarm",
    // HH:mm, the server's local time.
    val time: String = "07:00",
    val days: List<String> = emptyList(),
    val speakerId: String? = null,
    val source: AlarmSource = AlarmSource("shuffle"),
    val volume: Double = 0.5,
    val rampSeconds: Int = 60,
    val enabled: Boolean = true,
    val nextFireAt: String? = null,
    val lastFiredAt: String? = null,
) {
    fun toJson(): JSONObject = JSONObject()
        .put("name", name)
        .put("time", time)
        .put("days", JSONArray(days))
        .putOpt2("speakerId", speakerId)
        .put("source", source.toJson())
        .put("volume", volume)
        .put("rampSeconds", rampSeconds)
        .put("enabled", enabled)

    companion object {
        val WEEKDAYS = listOf("mon", "tue", "wed", "thu", "fri", "sat", "sun")
    }
}

data class Briefing(val greeting: String, val speech: List<String>, val episodes: List<Episode>)

data class ArtistStat(val artist: Artist, val plays: Int, val minutes: Double)
data class AlbumStat(val album: Album, val plays: Int)
data class TrackStat(val track: Track, val plays: Int)
data class GenreStat(val genre: String, val minutes: Double)
data class ShowStat(val podcast: Podcast, val minutes: Double, val episodes: Int)

data class MusicStats(
    val minutes: Double = 0.0,
    val plays: Int = 0,
    val skips: Int = 0,
    val distinctTracks: Int = 0,
    val distinctArtists: Int = 0,
    val newArtists: Int = 0,
    val topArtists: List<ArtistStat> = emptyList(),
    val topAlbums: List<AlbumStat> = emptyList(),
    val topTracks: List<TrackStat> = emptyList(),
    val topGenres: List<GenreStat> = emptyList(),
    val byHour: List<Double> = emptyList(),
    val byWeekday: List<Double> = emptyList(),
    val longestStreakDays: Int = 0,
    val firstPlay: Pair<Track, String?>? = null,
)

data class PodcastStats(
    val minutes: Double = 0.0,
    val episodesFinished: Int = 0,
    val timeSavedMinutes: Double = 0.0,
    val topShows: List<ShowStat> = emptyList(),
    val byHour: List<Double> = emptyList(),
    val byWeekday: List<Double> = emptyList(),
)

data class ListeningStats(
    val period: String,
    val from: String?,
    val to: String?,
    val music: MusicStats?,
    val podcasts: PodcastStats?,
)

data class ListenBrainzStatus(val connected: Boolean, val username: String?)

// ---------------- Parsing ----------------

internal fun parseAudioEnabled(json: JSONObject): AudioEnabled =
    AudioEnabled(json.bool("music"), json.bool("podcasts"))

internal fun parseQueueRef(json: JSONObject): QueueRef = QueueRef(json.text("type", "track"), json.getString("id"))

internal fun parsePlayContext(json: JSONObject): PlayContext =
    PlayContext(json.text("type", "queue"), json.str("id"), json.str("name"))

internal fun parseNowPlaying(json: JSONObject): NowPlaying = NowPlaying(
    deviceId = json.text("deviceId"),
    deviceName = json.text("deviceName", "Another device"),
    kind = json.text("kind", "music"),
    queue = json.list("queue", ::parseQueueRef),
    index = json.int("index") ?: 0,
    positionSeconds = json.dbl("positionSeconds") ?: 0.0,
    playing = json.bool("playing"),
    shuffle = json.bool("shuffle"),
    repeat = json.str("repeat") ?: "off",
    speed = json.dbl("speed") ?: 1.0,
    context = json.obj("context")?.let(::parsePlayContext),
    updatedAt = json.str("updatedAt"),
)

internal fun parseNowPlayingResolved(json: JSONObject): NowPlayingResolved = NowPlayingResolved(
    nowPlaying = parseNowPlaying(json.obj("nowPlaying") ?: JSONObject()),
    tracks = json.list("tracks", ::parseTrack),
    episodes = json.list("episodes", ::parseEpisode),
)

internal fun parseSpeakerState(json: JSONObject): SpeakerState = SpeakerState(
    playing = json.bool("playing"),
    title = json.str("title"),
    subtitle = json.str("subtitle"),
    artUrl = json.str("artUrl"),
    positionSeconds = json.dbl("positionSeconds") ?: 0.0,
    durationSeconds = json.dbl("durationSeconds"),
    volume = json.dbl("volume") ?: 1.0,
    kind = json.str("kind"),
    itemId = json.str("itemId"),
)

internal fun parseSpeaker(json: JSONObject): Speaker = Speaker(
    deviceId = json.getString("deviceId"),
    name = json.text("name", "Speaker"),
    kind = json.text("kind", "web"),
    online = json.bool("online"),
    allowAnnouncements = json.bool("allowAnnouncements"),
    volume = json.dbl("volume") ?: 1.0,
    lastSeenAt = json.str("lastSeenAt"),
    state = json.obj("state")?.let(::parseSpeakerState),
)

internal fun parseSpeakerCommand(json: JSONObject): SpeakerCommand = SpeakerCommand(
    seq = json.long("seq") ?: 0L,
    at = json.str("at"),
    from = json.str("from"),
    action = json.text("action"),
    queue = json.list("queue", ::parseQueueRef),
    index = json.int("index") ?: 0,
    positionSeconds = json.dbl("positionSeconds") ?: 0.0,
    shuffle = json.bool("shuffle"),
    volume = json.dbl("volume"),
    rampSeconds = json.dbl("rampSeconds") ?: 0.0,
    text = json.str("text"),
    context = json.obj("context")?.let(::parsePlayContext),
)

internal fun parseCommandBatch(json: JSONObject): CommandBatch =
    CommandBatch(json.list("commands", ::parseSpeakerCommand), json.long("latestSeq"))

internal fun parseWakeAlarm(json: JSONObject): WakeAlarm = WakeAlarm(
    id = json.str("id"),
    name = json.text("name", "Alarm"),
    time = json.text("time", "07:00"),
    days = json.strings("days"),
    speakerId = json.str("speakerId"),
    source = json.obj("source")?.let { AlarmSource(it.text("type", "shuffle"), it.str("id"), it.str("name")) } ?: AlarmSource("shuffle"),
    volume = json.dbl("volume") ?: 0.5,
    rampSeconds = json.int("rampSeconds") ?: 0,
    enabled = json.bool("enabled", true),
    nextFireAt = json.str("nextFireAt"),
    lastFiredAt = json.str("lastFiredAt"),
)

internal fun parseBriefing(json: JSONObject): Briefing =
    Briefing(json.text("greeting"), json.strings("speech"), json.list("episodes", ::parseEpisode))

internal fun parseStats(json: JSONObject): ListeningStats {
    val music = json.obj("music")?.let { m ->
        MusicStats(
            minutes = m.dbl("minutes") ?: 0.0,
            plays = m.int("plays") ?: 0,
            skips = m.int("skips") ?: 0,
            distinctTracks = m.int("distinctTracks") ?: 0,
            distinctArtists = m.int("distinctArtists") ?: 0,
            newArtists = m.int("newArtists") ?: 0,
            topArtists = m.list("topArtists") { ArtistStat(parseArtist(it.getJSONObject("artist")), it.int("plays") ?: 0, it.dbl("minutes") ?: 0.0) },
            topAlbums = m.list("topAlbums") { AlbumStat(parseAlbum(it.getJSONObject("album")), it.int("plays") ?: 0) },
            topTracks = m.list("topTracks") { TrackStat(parseTrack(it.getJSONObject("track")), it.int("plays") ?: 0) },
            topGenres = m.list("topGenres") { GenreStat(it.text("genre"), it.dbl("minutes") ?: 0.0) },
            byHour = m.doubles("byHour"),
            byWeekday = m.doubles("byWeekday"),
            longestStreakDays = m.int("longestStreakDays") ?: 0,
            firstPlay = m.obj("firstPlay")?.let { first ->
                first.obj("track")?.let { parseTrack(it) to first.str("playedAt") }
            },
        )
    }
    val podcasts = json.obj("podcasts")?.let { p ->
        PodcastStats(
            minutes = p.dbl("minutes") ?: 0.0,
            episodesFinished = p.int("episodesFinished") ?: 0,
            timeSavedMinutes = p.dbl("timeSavedMinutes") ?: 0.0,
            topShows = p.list("topShows") { ShowStat(parsePodcast(it.getJSONObject("podcast")), it.dbl("minutes") ?: 0.0, it.int("episodes") ?: 0) },
            byHour = p.doubles("byHour"),
            byWeekday = p.doubles("byWeekday"),
        )
    }
    return ListeningStats(json.text("period", "month"), json.str("from"), json.str("to"), music, podcasts)
}
