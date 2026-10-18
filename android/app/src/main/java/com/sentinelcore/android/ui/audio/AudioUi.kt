package com.sentinelcore.android.ui.audio

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.LruCache
import androidx.compose.material3.SnackbarDuration
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.SnackbarResult
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color
import com.sentinelcore.android.audio.AudioApi
import com.sentinelcore.android.audio.AudioItem
import com.sentinelcore.android.audio.AudioRuntime
import com.sentinelcore.android.audio.Episode
import com.sentinelcore.android.audio.PlayContext
import com.sentinelcore.android.audio.Track
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

// Where the audio screens can go. Filled in by SentinelApp from its NavController.
data class AudioNav(
    val openAlbum: (String) -> Unit,
    val openArtist: (String) -> Unit,
    val openPlaylist: (String) -> Unit,
    val openGenre: (String) -> Unit,
    val openDecade: (Int) -> Unit,
    val openShow: (String) -> Unit,
    val openEpisode: (String) -> Unit,
    val openPreview: (feedUrl: String?, itunesId: Long?) -> Unit,
    val open: (String) -> Unit,
    val back: () -> Unit,
)

// What a long-press menu was opened on. `playlistId`/`entryId` add "Remove from this playlist".
data class TrackMenuTarget(val track: Track, val playlistId: String? = null, val entryId: String? = null, val canRemove: Boolean = false)

// The audio screens' shared state: the sheets and menus that any screen can open (they are drawn
// once, over everything, by AudioOverlays), Now Playing, and a snackbar. One per app shell.
@Stable
class AudioUi(
    val runtime: AudioRuntime,
    val scope: CoroutineScope,
    val isAdmin: Boolean,
    val nav: AudioNav,
) {
    val engine get() = runtime.engine
    val api get() = runtime.api

    var nowPlayingOpen by mutableStateOf(false)
    var trackMenu by mutableStateOf<TrackMenuTarget?>(null)
    var episodeMenu by mutableStateOf<Episode?>(null)
    // Track ids waiting for "Add to playlist".
    var addToPlaylist by mutableStateOf<List<String>?>(null)
    var speakerPicker by mutableStateOf(false)
    // Bumped after an edit (a love, a playlist change) so open screens reload.
    var libraryVersion by mutableIntStateOf(0)
    val snackbar = SnackbarHostState()

    fun message(text: String, action: String? = null, onAction: (() -> Unit)? = null) {
        scope.launch {
            snackbar.currentSnackbarData?.dismiss()
            val result = snackbar.showSnackbar(text, actionLabel = action, withDismissAction = action == null, duration = SnackbarDuration.Short)
            if (result == SnackbarResult.ActionPerformed) onAction?.invoke()
        }
    }

    fun failed(error: Throwable, fallback: String) = message(error.message?.takeIf { it.isNotBlank() } ?: fallback)

    // Runs a server call, reporting a failure in the snackbar.
    fun launch(fallback: String = "That didn't work.", block: suspend () -> Unit) {
        scope.launch {
            try {
                block()
            } catch (error: Exception) {
                if (error is kotlinx.coroutines.CancellationException) throw error
                failed(error, fallback)
            }
        }
    }

    fun playTracks(tracks: List<Track>, index: Int = 0, context: PlayContext? = null, shuffle: Boolean = false, openPlayer: Boolean = false) {
        if (tracks.isEmpty()) return message("Nothing to play.")
        engine.playTracks(tracks, index, context, shuffle)
        if (openPlayer) nowPlayingOpen = true
    }

    fun playEpisode(episode: Episode, startAt: Double? = null, context: PlayContext? = null) {
        engine.playEpisodes(listOf(episode), 0, context ?: PlayContext("podcast", episode.podcastId, episode.podcastTitle), startAt)
    }

    fun playNext(tracks: List<Track>) {
        engine.playNext(tracks.map(AudioItem::of))
        message(if (tracks.size == 1) "\"${tracks.first().title}\" plays next" else "${tracks.size} songs play next")
    }

    fun addToQueue(tracks: List<Track>) {
        engine.addToQueue(tracks.map(AudioItem::of))
        message(if (tracks.size == 1) "Added \"${tracks.first().title}\" to the queue" else "Added ${tracks.size} songs to the queue")
    }

    fun loveTrack(track: Track, loved: Boolean = !track.loved, after: (Boolean) -> Unit = {}) = launch("Couldn't save that.") {
        val result = api.love("tracks", track.id, loved)
        engine.updateTrack(track.copy(loved = result))
        after(result)
        libraryVersion++
    }

    fun rateTrack(track: Track, rating: Int) = launch("Couldn't save the rating.") {
        val result = api.rate("tracks", track.id, rating)
        engine.updateTrack(track.copy(rating = result))
        libraryVersion++
        message(if (result == 0) "Rating cleared" else "Rated ${"★".repeat(result)}")
    }

    fun download(tracks: List<Track>) {
        runtime.downloads.downloadTracks(tracks)
        message(
            if (tracks.size == 1) "Downloading \"${tracks.first().title}\"" else "Downloading ${tracks.size} songs",
            action = "View",
        ) { nav.open(AudioRoutes.DOWNLOADS) }
    }
}

val LocalAudioUi = staticCompositionLocalOf<AudioUi> { error("AudioUi not provided") }

object AudioRoutes {
    const val ALBUM = "music/album/{id}"
    const val ARTIST = "music/artist/{id}"
    const val PLAYLIST = "music/playlist/{id}"
    const val GENRE = "music/genre/{name}"
    const val DECADE = "music/decade/{decade}"
    const val FOLDERS = "music/folders"
    const val SMART = "music/smart/{id}"
    const val ADD = "music/add"
    const val ALBUM_DOWNLOADS = "music/album-downloads"
    const val SUGGESTIONS = "music/suggestions"
    const val SHOW = "podcasts/show/{id}"
    const val EPISODE = "podcasts/episode/{id}"
    const val PREVIEW = "podcasts/preview?feed={feed}&itunes={itunes}"
    const val DISCOVER = "podcasts/discover"
    const val UP_NEXT = "podcasts/up-next"
    const val BOOKMARKS = "podcasts/bookmarks"
    const val TRANSCRIPT_SEARCH = "podcasts/transcripts"
    const val CLIPS = "podcasts/clips"
    const val DOWNLOADS = "audio/downloads"
    const val ALARMS = "audio/alarms"
    const val ALARM = "audio/alarm/{id}"
    const val BRIEFING = "audio/briefing"
    const val STATS = "audio/stats"
    const val WRAPPED = "audio/wrapped"
    const val EQUALIZER = "audio/equalizer"
    const val SETTINGS = "audio/settings"
    const val LISTENBRAINZ = "audio/listenbrainz"
    const val SPEAKERS = "audio/speakers"
    const val MUSIC_LIBRARIES = "settings/music-libraries"
}

// Artwork, loaded through the app's own session and kept in a small memory cache so lists don't
// flicker. Downloaded art comes from disk, so offline lists look right.
object ArtCache {
    private val cache = object : LruCache<String, Bitmap>(24 * 1024) {
        override fun sizeOf(key: String, value: Bitmap): Int = (value.byteCount / 1024).coerceAtLeast(1)
    }

    fun peek(path: String?, size: Int): Bitmap? = path?.let { cache.get("$it#$size") }

    suspend fun load(runtime: AudioRuntime, path: String, size: Int): Bitmap? {
        val key = "$path#$size"
        cache.get(key)?.let { return it }
        val local = runtime.downloads.localArt(path)
        val bitmap = if (local != null) {
            withContext(Dispatchers.IO) {
                val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
                BitmapFactory.decodeFile(local.path, bounds)
                var sample = 1
                while (bounds.outWidth / (sample * 2) >= size) sample *= 2
                BitmapFactory.decodeFile(local.path, BitmapFactory.Options().apply { inSampleSize = sample })
            }
        } else {
            val sized = AudioApi.sized(path, size.coerceIn(64, 1200)) ?: return null
            runtime.repository.loadImage(sized, size)
        }
        bitmap?.let { cache.put(key, it) }
        return bitmap
    }

    // A color to wash Now Playing's background with: the art's average, darkened, so white text
    // always reads on it.
    fun moodColor(bitmap: Bitmap): Color {
        val small = Bitmap.createScaledBitmap(bitmap, 12, 12, true)
        var r = 0L
        var g = 0L
        var b = 0L
        var n = 0L
        for (x in 0 until small.width) for (y in 0 until small.height) {
            val pixel = small.getPixel(x, y)
            val pr = (pixel shr 16) and 0xff
            val pg = (pixel shr 8) and 0xff
            val pb = pixel and 0xff
            // Favour colourful pixels over greys, so a mostly-white cover with a red logo goes red.
            val weight = 1 + (maxOf(pr, pg, pb) - minOf(pr, pg, pb)) / 16
            r += pr * weight
            g += pg * weight
            b += pb * weight
            n += weight
        }
        if (n == 0L) return Color(0xFF202428)
        val scale = 0.55f
        return Color((r / n / 255f) * scale, (g / n / 255f) * scale, (b / n / 255f) * scale)
    }
}
