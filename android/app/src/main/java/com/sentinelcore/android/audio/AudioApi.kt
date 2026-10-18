package com.sentinelcore.android.audio

import android.util.Log
import com.sentinelcore.android.data.MediaDownload
import com.sentinelcore.android.data.MediaDownloadList
import com.sentinelcore.android.data.parseMediaDownload
import com.sentinelcore.android.data.parseMediaDownloadList
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.net.URLEncoder

// A non-2xx answer from the audio routes, with the server's own `error` / `errors` as the message.
class AudioApiException(val status: Int, message: String) : IOException(message)

// The transport: method, path, query, body, content type, read timeout → (status, body bytes).
// The app hands in SentinelRepository.organizerRequest, which carries the same address and session
// cookie as every other request; the unit tests hand in a fake.
typealias AudioTransport = suspend (method: String, path: String, query: Map<String, String>, body: ByteArray?, contentType: String?, timeoutMillis: Int?) -> Pair<Int, ByteArray>

// Every route in core/docs/MUSIC_AND_PODCASTS.md, as suspend functions. Nothing here holds state.
class AudioApi(private val transport: AudioTransport) {

    // ---------------- plumbing ----------------

    private suspend fun call(
        method: String,
        path: String,
        query: Map<String, String> = emptyMap(),
        body: Any? = null,
        timeoutMillis: Int? = null,
        contentType: String = "application/json",
    ): String {
        val bytes = when (body) {
            null -> null
            is ByteArray -> body
            is String -> body.toByteArray()
            else -> body.toString().toByteArray()
        }
        val (status, response) = transport(method, path, query.filterValues { it.isNotEmpty() }, bytes, if (bytes != null) contentType else null, timeoutMillis)
        val text = String(response, Charsets.UTF_8)
        if (status !in 200..299) {
            val message = errorMessage(text) ?: when (status) {
                401 -> "This server requires you to sign in."
                403 -> "Only an admin can do that."
                404 -> "Not found."
                else -> "Server request failed (HTTP $status)."
            }
            if (status != 404) runCatching { Log.d(TAG, "$method $path -> $status $message") }
            throw AudioApiException(status, message)
        }
        return text
    }

    private suspend fun obj(method: String, path: String, query: Map<String, String> = emptyMap(), body: Any? = null, timeoutMillis: Int? = null): JSONObject {
        val text = call(method, path, query, body, timeoutMillis)
        return if (text.isBlank()) JSONObject() else JSONObject(text)
    }

    private suspend fun arr(path: String, query: Map<String, String> = emptyMap(), method: String = "GET", body: Any? = null): JSONArray {
        val text = call(method, path, query, body)
        return if (text.isBlank()) JSONArray() else JSONArray(text)
    }

    private suspend fun send(method: String, path: String, body: Any? = null, query: Map<String, String> = emptyMap()) {
        call(method, path, query, body)
    }

    // ---------------- §1 flags, folders ----------------

    // A 404 from an older server means both are off.
    suspend fun enabled(): AudioEnabled = try {
        parseAudioEnabled(obj("GET", "/api/audio/enabled"))
    } catch (error: AudioApiException) {
        if (error.status == 404) AudioEnabled.OFF else throw error
    }

    // ---------------- §2 libraries ----------------

    suspend fun musicStatus(): MusicStatus = parseMusicStatus(obj("GET", "/api/music/status"))

    suspend fun musicLibraries(): List<MusicLibrary> = arr("/api/music/libraries").objects(::parseMusicLibrary)

    suspend fun saveMusicLibrary(id: String?, name: String, path: String): MusicLibrary {
        val body = JSONObject().put("name", name).put("path", path)
        return parseMusicLibrary(
            if (id == null) obj("POST", "/api/music/libraries", body = body) else obj("PUT", "/api/music/libraries/${seg(id)}", body = body),
        )
    }

    suspend fun deleteMusicLibrary(id: String) = send("DELETE", "/api/music/libraries/${seg(id)}")

    suspend fun scanMusicLibrary(id: String?) =
        send("POST", if (id == null) "/api/music/scan" else "/api/music/libraries/${seg(id)}/scan")

    // ---------------- §4 music ----------------

    suspend fun musicHome(): MusicHome = parseMusicHome(obj("GET", "/api/music/home"))

    suspend fun artists(q: String = "", genre: String = "", sort: String = "name", order: String = "", albumArtistsOnly: Boolean = false, offset: Int = 0, limit: Int = PAGE): Page<Artist> =
        parsePage(
            obj("GET", "/api/music/artists", mapOf("q" to q, "genre" to genre, "sort" to sort, "order" to order, "albumArtistsOnly" to if (albumArtistsOnly) "true" else "", "offset" to "$offset", "limit" to "$limit")),
            ::parseArtist,
        )

    suspend fun albums(q: String = "", artistId: String = "", genre: String = "", year: Int? = null, decade: Int? = null, sort: String = "title", order: String = "", offset: Int = 0, limit: Int = PAGE): Page<Album> =
        parsePage(
            obj(
                "GET", "/api/music/albums",
                mapOf("q" to q, "artistId" to artistId, "genre" to genre, "year" to (year?.toString() ?: ""), "decade" to (decade?.toString() ?: ""), "sort" to sort, "order" to order, "offset" to "$offset", "limit" to "$limit"),
            ),
            ::parseAlbum,
        )

    suspend fun tracks(
        q: String = "", artistId: String = "", albumId: String = "", genre: String = "", decade: Int? = null,
        loved: Boolean = false, minRating: Int? = null, sort: String = "title", order: String = "", offset: Int = 0, limit: Int = PAGE,
    ): Page<Track> = parsePage(
        obj(
            "GET", "/api/music/tracks",
            mapOf(
                "q" to q, "artistId" to artistId, "albumId" to albumId, "genre" to genre, "decade" to (decade?.toString() ?: ""),
                "loved" to if (loved) "true" else "", "minRating" to (minRating?.toString() ?: ""), "sort" to sort, "order" to order,
                "offset" to "$offset", "limit" to "$limit",
            ),
        ),
        ::parseTrack,
    )

    suspend fun genres(): List<Genre> = arr("/api/music/genres").objects(::parseGenre)

    suspend fun artist(id: String): ArtistDetail = parseArtistDetail(obj("GET", "/api/music/artists/${seg(id)}"))

    suspend fun album(id: String): AlbumDetail = parseAlbumDetail(obj("GET", "/api/music/albums/${seg(id)}"))

    suspend fun search(q: String): MusicSearch = parseMusicSearch(obj("GET", "/api/music/search", mapOf("q" to q)))

    suspend fun folders(libraryId: String?, path: String?): FolderListing =
        parseFolderListing(obj("GET", "/api/music/folders", mapOf("libraryId" to (libraryId ?: ""), "path" to (path ?: ""))))

    suspend fun lyrics(trackId: String): Lyrics? = try {
        parseLyrics(obj("GET", "/api/music/tracks/${seg(trackId)}/lyrics"))
    } catch (error: AudioApiException) {
        if (error.status == 404) null else throw error
    }

    suspend fun reportPlayed(trackId: String, secondsListened: Double, completed: Boolean, skipped: Boolean, playedAt: String?, deviceName: String) =
        send(
            "POST", "/api/music/tracks/${seg(trackId)}/played",
            JSONObject().put("secondsListened", secondsListened).put("completed", completed).put("skipped", skipped)
                .apply { playedAt?.let { put("playedAt", it) } }.put("deviceName", deviceName),
        )

    suspend fun nowPlayingTrack(trackId: String) = send("POST", "/api/music/now-playing-track", JSONObject().put("trackId", trackId))

    // kind: tracks, albums or artists.
    suspend fun love(kind: String, id: String, loved: Boolean): Boolean =
        obj("POST", "/api/music/$kind/${seg(id)}/love", body = JSONObject().put("loved", loved)).bool("loved", loved)

    // kind: tracks or albums.
    suspend fun rate(kind: String, id: String, rating: Int): Int =
        obj("POST", "/api/music/$kind/${seg(id)}/rating", body = JSONObject().put("rating", rating.coerceIn(0, 5))).int("rating") ?: rating

    suspend fun mix(seedType: String, seedId: String, limit: Int = 50, excludeIds: List<String> = emptyList()): MixResult =
        parseMixResult(obj("GET", "/api/music/mix", mapOf("seedType" to seedType, "seedId" to seedId, "limit" to "$limit", "excludeIds" to excludeIds.joinToString(","))))

    suspend fun namedMix(mixId: String, excludeIds: List<String> = emptyList()): MixResult =
        parseMixResult(obj("GET", "/api/music/mix", mapOf("mix" to mixId, "excludeIds" to excludeIds.joinToString(","))))

    suspend fun shuffleAll(limit: Int = 200): List<Track> {
        val text = call("GET", "/api/music/shuffle", mapOf("limit" to "$limit"))
        // The contract shows a queue; accept either a bare array or {tracks}/{items}.
        val trimmed = text.trim()
        return if (trimmed.startsWith("[")) JSONArray(trimmed).objects(::parseTrack)
        else JSONObject(trimmed).let { it.list("tracks", ::parseTrack).ifEmpty { it.list("items", ::parseTrack) } }
    }

    // ---------------- playlists ----------------

    suspend fun playlists(): List<Playlist> = arr("/api/music/playlists").objects(::parsePlaylist)

    suspend fun playlist(id: String): PlaylistDetail = parsePlaylistDetail(obj("GET", "/api/music/playlists/${seg(id)}"))

    suspend fun createPlaylist(name: String, description: String = "", shared: Boolean = false, collaborative: Boolean = false, rules: SmartRules? = null, trackIds: List<String> = emptyList()): Playlist =
        parsePlaylist(
            obj(
                "POST", "/api/music/playlists",
                body = JSONObject().put("name", name).put("description", description).put("shared", shared).put("collaborative", collaborative)
                    .apply {
                        rules?.let { put("rules", it.toJson()) }
                        if (trackIds.isNotEmpty()) put("trackIds", JSONArray(trackIds))
                    },
            ),
        )

    suspend fun updatePlaylist(id: String, name: String, description: String, shared: Boolean, collaborative: Boolean, rules: SmartRules?): Playlist =
        parsePlaylist(
            obj(
                "PUT", "/api/music/playlists/${seg(id)}",
                body = JSONObject().put("name", name).put("description", description).put("shared", shared).put("collaborative", collaborative)
                    .putOpt2("rules", rules?.toJson()),
            ),
        )

    suspend fun deletePlaylist(id: String) = send("DELETE", "/api/music/playlists/${seg(id)}")

    suspend fun addToPlaylist(id: String, trackIds: List<String>, position: Int? = null): Playlist =
        parsePlaylist(obj("POST", "/api/music/playlists/${seg(id)}/tracks", body = JSONObject().put("trackIds", JSONArray(trackIds)).putOpt2("position", position)))

    suspend fun removeFromPlaylist(id: String, entryId: String) = send("DELETE", "/api/music/playlists/${seg(id)}/tracks/${seg(entryId)}")

    suspend fun reorderPlaylist(id: String, entryIds: List<String>) =
        send("PUT", "/api/music/playlists/${seg(id)}/order", JSONObject().put("entryIds", JSONArray(entryIds)))

    suspend fun pinPlaylist(id: String, pinned: Boolean) = send("POST", "/api/music/playlists/${seg(id)}/pin", JSONObject().put("pinned", pinned))

    suspend fun duplicatePlaylist(id: String): Playlist = parsePlaylist(obj("POST", "/api/music/playlists/${seg(id)}/duplicate"))

    suspend fun playlistM3u(id: String): String = call("GET", "/api/music/playlists/${seg(id)}/m3u")

    // ---------------- + Add (album downloads) ----------------

    // Null when the feature is not there at all (404: Music off or an older server).
    suspend fun musicDownloadStatus(): MusicDownloadStatus? = try {
        parseMusicDownloadStatus(obj("GET", "/api/music/downloads/status"))
    } catch (error: AudioApiException) {
        if (error.status == 404) null else throw error
    }

    suspend fun musicLookup(q: String): List<MusicLookup> =
        arr("/api/music/downloads/lookup", mapOf("q" to q)).objects(::parseMusicLookup)

    suspend fun startMusicSearch(artist: String, album: String, year: Int?, mbid: String?, artUrl: String?): MusicReleaseSearch =
        parseMusicReleaseSearch(
            obj(
                "POST", "/api/music/downloads/search",
                body = JSONObject().put("artist", artist).put("album", album).putOpt2("year", year)
                    .apply {
                        mbid?.takeIf { it.isNotBlank() }?.let { put("mbid", it) }
                        artUrl?.takeIf { it.isNotBlank() }?.let { put("artUrl", it) }
                    },
                timeoutMillis = 60_000,
            ),
        )

    suspend fun musicSearch(id: String): MusicReleaseSearch = parseMusicReleaseSearch(obj("GET", "/api/music/downloads/search/${seg(id)}"))

    suspend fun grabMusic(searchId: String, releaseId: String, libraryId: String? = null): MediaDownload =
        parseMediaDownload(
            obj("POST", "/api/music/downloads", body = JSONObject().put("searchId", searchId).put("releaseId", releaseId).apply { libraryId?.let { put("libraryId", it) } }),
        )

    suspend fun musicDownloads(): MediaDownloadList = parseMediaDownloadList(obj("GET", "/api/music/downloads"))

    suspend fun deleteMusicDownload(id: String) = send("DELETE", "/api/music/downloads/${seg(id)}")

    suspend fun retryMusicDownload(id: String): MediaDownload = parseMediaDownload(obj("POST", "/api/music/downloads/${seg(id)}/retry"))

    // "Suggested for you". `refresh` starts a new build; the server waits up to 2 s for it.
    suspend fun musicSuggestions(refresh: Boolean = false): MusicSuggestions =
        parseMusicSuggestions(obj("GET", "/api/music/downloads/suggestions", mapOf("refresh" to if (refresh) "true" else ""), timeoutMillis = 30_000))

    // "Not interested": with `title` that one album, without it the whole artist.
    suspend fun dismissMusicSuggestion(artist: String, title: String? = null) =
        send("POST", "/api/music/downloads/suggestions/dismiss", MusicSuggestionsFormat.dismissBody(artist, title))

    // ---------------- §6 podcasts ----------------

    suspend fun directorySearch(q: String): List<DirectoryShow> = arr("/api/podcasts/directory/search", mapOf("q" to q)).objects(::parseDirectoryShow)

    suspend fun directoryTop(genre: Long?): List<DirectoryShow> =
        arr("/api/podcasts/directory/top", mapOf("genre" to (genre?.toString() ?: ""))).objects(::parseDirectoryShow)

    suspend fun directoryGenres(): List<DirectoryGenre> =
        arr("/api/podcasts/directory/genres").objects { DirectoryGenre(it.long("id") ?: 0L, it.text("name")) }

    suspend fun preview(feedUrl: String?, itunesId: Long?): PodcastPreview {
        val json = obj("GET", "/api/podcasts/preview", mapOf("feedUrl" to (feedUrl ?: ""), "itunesId" to (itunesId?.toString() ?: "")), timeoutMillis = 60_000)
        // Preview episodes are "Episode-lite" (§6): nothing is stored yet, so they carry no id, and
        // parseEpisode needs one - without this every row was dropped and the page read "0 episodes".
        val episodes = json.optJSONArray("episodes") ?: JSONArray()
        for (index in 0 until episodes.length()) {
            val episode = episodes.optJSONObject(index) ?: continue
            if (episode.str("id") == null) episode.put("id", "preview-$index")
        }
        return PodcastPreview(parsePodcast(json.obj("podcast") ?: JSONObject()), json.list("episodes", ::parseEpisode))
    }

    suspend fun recommended(): List<DirectoryShow> = arr("/api/podcasts/recommended").objects(::parseDirectoryShow)

    suspend fun podcasts(all: Boolean = false): List<Podcast> =
        arr("/api/podcasts", if (all) mapOf("all" to "true") else emptyMap()).objects(::parsePodcast)

    suspend fun subscribe(feedUrl: String?, itunesId: Long?): Podcast = parsePodcast(
        obj(
            "POST", "/api/podcasts/subscriptions",
            body = if (feedUrl != null) JSONObject().put("feedUrl", feedUrl) else JSONObject().put("itunesId", itunesId),
            timeoutMillis = 60_000,
        ),
    )

    suspend fun unsubscribe(id: String) = send("DELETE", "/api/podcasts/${seg(id)}/subscription")

    suspend fun deletePodcast(id: String) = send("DELETE", "/api/podcasts/${seg(id)}")

    suspend fun podcast(id: String): Podcast = parsePodcast(obj("GET", "/api/podcasts/${seg(id)}"))

    suspend fun savePodcastSettings(id: String, settings: PodcastSettings): Podcast =
        parsePodcast(obj("PUT", "/api/podcasts/${seg(id)}/settings", body = settings.toJson()))

    suspend fun saveMySettings(id: String, settings: PodcastMySettings): Podcast =
        parsePodcast(obj("PUT", "/api/podcasts/${seg(id)}/my-settings", body = settings.toJson()))

    suspend fun refreshPodcast(id: String): Podcast = parsePodcast(obj("POST", "/api/podcasts/${seg(id)}/refresh", timeoutMillis = 60_000))

    suspend fun refreshAllPodcasts() = send("POST", "/api/podcasts/refresh")

    suspend fun episodes(podcastId: String, sort: String = "newest", filter: String = "all", q: String = "", season: Int? = null, offset: Int = 0, limit: Int = PAGE): EpisodePage =
        parseEpisodePage(
            obj(
                "GET", "/api/podcasts/${seg(podcastId)}/episodes",
                mapOf("sort" to sort, "filter" to filter, "q" to q, "season" to (season?.toString() ?: ""), "offset" to "$offset", "limit" to "$limit"),
            ),
        )

    suspend fun markAllPlayed(podcastId: String, played: Boolean, before: String? = null) =
        send("POST", "/api/podcasts/${seg(podcastId)}/played", JSONObject().put("played", played).apply { before?.let { put("before", it) } })

    suspend fun opmlExport(): String = call("GET", "/api/podcasts/opml")

    suspend fun opmlImport(xml: String): OpmlImportResult {
        val json = JSONObject(call("POST", "/api/podcasts/opml", body = xml, contentType = "text/xml", timeoutMillis = 60_000))
        return OpmlImportResult(
            json.int("added") ?: 0,
            json.int("existing") ?: 0,
            json.list("failed") { OpmlFailure(it.text("feedUrl"), it.text("error")) },
        )
    }

    suspend fun podcastHome(): PodcastHome = parsePodcastHome(obj("GET", "/api/podcasts/home"))

    suspend fun queue(): List<Episode> = arr("/api/podcasts/queue").objects(::parseEpisode)

    suspend fun enqueue(episodeId: String, position: String): List<Episode> =
        arr("/api/podcasts/queue", method = "POST", body = JSONObject().put("episodeId", episodeId).put("position", position)).objects(::parseEpisode)

    suspend fun setQueue(episodeIds: List<String>): List<Episode> =
        arr("/api/podcasts/queue", method = "PUT", body = JSONObject().put("episodeIds", JSONArray(episodeIds))).objects(::parseEpisode)

    suspend fun dequeue(episodeId: String): List<Episode> =
        arr("/api/podcasts/queue/${seg(episodeId)}", method = "DELETE").objects(::parseEpisode)

    suspend fun episode(id: String): Episode = parseEpisode(obj("GET", "/api/podcasts/episodes/${seg(id)}"))

    suspend fun reportProgress(episodeId: String, positionSeconds: Double, durationSeconds: Double, speed: Double, deviceName: String): EpisodeProgress? {
        val text = call(
            "POST", "/api/podcasts/episodes/${seg(episodeId)}/progress",
            body = JSONObject().put("positionSeconds", positionSeconds).put("durationSeconds", durationSeconds).put("speed", speed).put("deviceName", deviceName),
        )
        return runCatching { parseEpisodeProgress(JSONObject(text)) }.getOrNull()
    }

    suspend fun setEpisodePlayed(episodeId: String, played: Boolean): EpisodeProgress? {
        val text = call("POST", "/api/podcasts/episodes/${seg(episodeId)}/played", body = JSONObject().put("played", played))
        return runCatching { parseEpisodeProgress(JSONObject(text)) }.getOrNull()
    }

    suspend fun downloadEpisodeOnServer(id: String): Episode = parseEpisode(obj("POST", "/api/podcasts/episodes/${seg(id)}/download"))

    suspend fun deleteEpisodeDownloadOnServer(id: String): Episode = parseEpisode(obj("DELETE", "/api/podcasts/episodes/${seg(id)}/download"))

    suspend fun chapters(id: String): List<Chapter> = arr("/api/podcasts/episodes/${seg(id)}/chapters").objects(::parseChapter)

    suspend fun fill(minutes: Int): FillResult {
        val json = obj("GET", "/api/podcasts/fill", mapOf("minutes" to "$minutes"))
        return FillResult(json.list("episodes", ::parseEpisode), json.dbl("totalSeconds") ?: 0.0)
    }

    suspend fun transcript(id: String): Transcript? = try {
        parseTranscript(obj("GET", "/api/podcasts/episodes/${seg(id)}/transcript"))
    } catch (error: AudioApiException) {
        if (error.status == 404) null else throw error
    }

    suspend fun transcribe(id: String) = send("POST", "/api/podcasts/episodes/${seg(id)}/transcribe")

    suspend fun searchTranscripts(q: String): List<TranscriptHit> =
        arr("/api/podcasts/transcripts/search", mapOf("q" to q)).objects(::parseTranscriptHit)

    suspend fun bookmarks(episodeId: String? = null): List<Bookmark> =
        arr("/api/podcasts/bookmarks", mapOf("episodeId" to (episodeId ?: ""))).objects(::parseBookmark)

    suspend fun addBookmark(episodeId: String, positionSeconds: Double, note: String): Bookmark =
        parseBookmark(obj("POST", "/api/podcasts/episodes/${seg(episodeId)}/bookmarks", body = JSONObject().put("positionSeconds", positionSeconds).put("note", note)))

    suspend fun updateBookmark(id: String, note: String): Bookmark =
        parseBookmark(obj("PUT", "/api/podcasts/bookmarks/${seg(id)}", body = JSONObject().put("note", note)))

    suspend fun deleteBookmark(id: String) = send("DELETE", "/api/podcasts/bookmarks/${seg(id)}")

    suspend fun bookmarkToNote(id: String): String? = obj("POST", "/api/podcasts/bookmarks/${seg(id)}/to-note").str("noteId")

    suspend fun makeClip(episodeId: String, startSeconds: Double, durationSeconds: Double, title: String): Clip =
        parseClip(
            obj(
                "POST", "/api/podcasts/episodes/${seg(episodeId)}/clips",
                body = JSONObject().put("startSeconds", startSeconds).put("durationSeconds", durationSeconds).put("title", title),
                timeoutMillis = 120_000,
            ),
        )

    suspend fun clips(): List<Clip> = arr("/api/podcasts/clips").objects(::parseClip)

    suspend fun deleteClip(id: String) = send("DELETE", "/api/podcasts/clips/${seg(id)}")

    suspend fun podcastStatus(): PodcastStatus = parsePodcastStatus(obj("GET", "/api/podcasts/status"))

    // ---------------- §7 now playing ----------------

    suspend fun putNowPlaying(nowPlaying: NowPlaying) = send("PUT", "/api/audio/now-playing", nowPlaying.toJson())

    suspend fun nowPlaying(): List<NowPlaying> = arr("/api/audio/now-playing").objects(::parseNowPlaying)

    suspend fun deleteNowPlaying(deviceId: String) = send("DELETE", "/api/audio/now-playing/${seg(deviceId)}")

    suspend fun resolveNowPlaying(deviceId: String): NowPlayingResolved =
        parseNowPlayingResolved(obj("GET", "/api/audio/now-playing/${seg(deviceId)}/resolve"))

    // ---------------- §8 speakers ----------------

    suspend fun speakerHeartbeat(deviceId: String, name: String, kind: String, allowAnnouncements: Boolean, volume: Double) =
        send(
            "PUT", "/api/audio/speakers/${seg(deviceId)}",
            JSONObject().put("name", name).put("kind", kind).put("allowAnnouncements", allowAnnouncements).put("volume", volume),
        )

    suspend fun removeSpeaker(deviceId: String) = send("DELETE", "/api/audio/speakers/${seg(deviceId)}")

    suspend fun speakers(): List<Speaker> = arr("/api/audio/speakers").objects(::parseSpeaker)

    suspend fun putSpeakerState(deviceId: String, state: SpeakerState) = send("PUT", "/api/audio/speakers/${seg(deviceId)}/state", state.toJson())

    suspend fun sendSpeakerCommand(deviceId: String, command: SpeakerCommand): Long? =
        obj("POST", "/api/audio/speakers/${seg(deviceId)}/commands", body = command.toJson()).long("seq")

    suspend fun waitForCommands(deviceId: String, after: Long, waitSeconds: Int): CommandBatch =
        parseCommandBatch(
            obj(
                "GET", "/api/audio/speakers/${seg(deviceId)}/commands",
                mapOf("after" to "$after", "wait" to "$waitSeconds"),
                timeoutMillis = (waitSeconds + 15) * 1000,
            ),
        )

    // ---------------- §9 alarms, briefing, stats, ListenBrainz ----------------

    suspend fun alarms(): List<WakeAlarm> = arr("/api/audio/alarms").objects(::parseWakeAlarm)

    suspend fun saveAlarm(alarm: WakeAlarm): WakeAlarm = parseWakeAlarm(
        if (alarm.id == null) obj("POST", "/api/audio/alarms", body = alarm.toJson())
        else obj("PUT", "/api/audio/alarms/${seg(alarm.id)}", body = alarm.toJson()),
    )

    suspend fun deleteAlarm(id: String) = send("DELETE", "/api/audio/alarms/${seg(id)}")

    suspend fun testAlarm(id: String) = send("POST", "/api/audio/alarms/${seg(id)}/test")

    suspend fun briefing(): Briefing = parseBriefing(obj("GET", "/api/audio/briefing", timeoutMillis = 45_000))

    suspend fun stats(period: String, year: Int? = null): ListeningStats =
        parseStats(obj("GET", "/api/audio/stats", mapOf("period" to period, "year" to (year?.toString() ?: ""))))

    suspend fun listenBrainz(): ListenBrainzStatus =
        obj("GET", "/api/audio/me/listenbrainz").let { ListenBrainzStatus(it.bool("connected"), it.str("username")) }

    suspend fun setListenBrainz(token: String): ListenBrainzStatus =
        obj("PUT", "/api/audio/me/listenbrainz", body = JSONObject().put("token", token), timeoutMillis = 30_000)
            .let { ListenBrainzStatus(it.bool("connected"), it.str("username")) }

    companion object {
        const val TAG = "SentinelAudio"
        const val PAGE = 100

        // What this app's player decodes natively (§4 Streaming, Android row).
        const val CODECS = "mp3,aac,flac,alac,opus,vorbis,wav"

        fun seg(value: String): String = URLEncoder.encode(value, "UTF-8").replace("+", "%20")

        fun errorMessage(body: String): String? {
            if (body.isBlank()) return null
            return runCatching {
                val json = JSONObject(body)
                json.str("error") ?: json.optJSONArray("errors")?.let { array ->
                    (0 until array.length()).map { array.optString(it) }.filter { it.isNotBlank() }.joinToString("\n").ifBlank { null }
                }
            }.getOrNull()
        }

        // Paths for the player and downloads: server-relative, resolved against the current
        // address by the player's data source (see AudioDataSources).
        fun trackStreamPath(trackId: String, maxBitrate: Int? = null): String =
            "/api/music/tracks/${seg(trackId)}/stream?codecs=$CODECS" + (maxBitrate?.let { "&maxBitrate=$it" } ?: "")

        fun trackDownloadPath(trackId: String, maxBitrate: Int? = null): String =
            "/api/music/tracks/${seg(trackId)}/download?codecs=$CODECS" + (maxBitrate?.let { "&maxBitrate=$it" } ?: "")

        fun episodeAudioPath(episodeId: String, download: Boolean = false): String =
            "/api/podcasts/episodes/${seg(episodeId)}/audio" + if (download) "?download=1" else ""

        // Art with a size, for the list rows (small) and Now Playing (large).
        fun sized(artUrl: String?, size: Int): String? {
            val url = artUrl?.takeIf { it.isNotBlank() } ?: return null
            if (url.startsWith("http", ignoreCase = true)) return url
            return url + (if (url.contains('?')) "&" else "?") + "size=$size"
        }
    }
}
