package com.sentinelcore.android.audio

import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.content.pm.ServiceInfo
import android.os.Build
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingWorkPolicy
import androidx.work.ForegroundInfo
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import com.sentinelcore.android.R
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.security.MessageDigest

// One thing saved on this phone for offline listening.
data class DownloadEntry(
    val type: String,
    val id: String,
    val title: String,
    val subtitle: String,
    val album: String? = null,
    val albumId: String? = null,
    val podcastId: String? = null,
    val artUrl: String? = null,
    val durationSeconds: Double = 0.0,
    // queued, downloading, done or failed.
    val state: String = "queued",
    val progress: Float = 0f,
    val bytes: Long = 0,
    val error: String? = null,
    val addedAt: Long = System.currentTimeMillis(),
    val replayGain: ReplayGainInfo? = null,
    val trackNumber: Int? = null,
    val discNumber: Int? = null,
) {
    val key: String get() = "$type:$id"
    val done: Boolean get() = state == "done"

    // Enough to play it with no server: a Track or an Episode built from what was saved.
    fun toItem(): AudioItem = if (type == AudioItem.TYPE_EPISODE) {
        AudioItem.of(Episode(id = id, podcastId = podcastId.orEmpty(), podcastTitle = subtitle, title = title, durationSeconds = durationSeconds, artUrl = artUrl))
    } else {
        AudioItem.of(
            Track(
                id = id, title = title, artist = subtitle, album = album.orEmpty(), albumId = albumId, artUrl = artUrl,
                durationSeconds = durationSeconds, replayGain = replayGain, trackNumber = trackNumber, discNumber = discNumber,
            ),
        )
    }

    fun toJson(): JSONObject = JSONObject()
        .put("type", type).put("id", id).put("title", title).put("subtitle", subtitle)
        .putOpt2("album", album).putOpt2("albumId", albumId).putOpt2("podcastId", podcastId).putOpt2("artUrl", artUrl)
        .put("durationSeconds", durationSeconds).put("state", state).put("progress", progress.toDouble())
        .put("bytes", bytes).putOpt2("error", error).put("addedAt", addedAt)
        .putOpt2("trackNumber", trackNumber).putOpt2("discNumber", discNumber)
        .putOpt2(
            "replayGain",
            replayGain?.let { JSONObject().putOpt2("trackGain", it.trackGain).putOpt2("trackPeak", it.trackPeak).putOpt2("albumGain", it.albumGain).putOpt2("albumPeak", it.albumPeak) },
        )

    companion object {
        fun fromJson(json: JSONObject): DownloadEntry = DownloadEntry(
            type = json.text("type", AudioItem.TYPE_TRACK),
            id = json.getString("id"),
            title = json.text("title"),
            subtitle = json.text("subtitle"),
            album = json.str("album"),
            albumId = json.str("albumId"),
            podcastId = json.str("podcastId"),
            artUrl = json.str("artUrl"),
            durationSeconds = json.dbl("durationSeconds") ?: 0.0,
            state = json.str("state") ?: "queued",
            progress = (json.dbl("progress") ?: 0.0).toFloat(),
            bytes = json.long("bytes") ?: 0L,
            error = json.str("error"),
            addedAt = json.long("addedAt") ?: 0L,
            replayGain = parseReplayGain(json.obj("replayGain")),
            trackNumber = json.int("trackNumber"),
            discNumber = json.int("discNumber"),
        )

        fun of(track: Track) = DownloadEntry(
            type = AudioItem.TYPE_TRACK, id = track.id, title = track.title, subtitle = track.artist, album = track.album,
            albumId = track.albumId, artUrl = track.artUrl, durationSeconds = track.durationSeconds, replayGain = track.replayGain,
            trackNumber = track.trackNumber, discNumber = track.discNumber,
        )

        fun of(episode: Episode) = DownloadEntry(
            type = AudioItem.TYPE_EPISODE, id = episode.id, title = episode.title, subtitle = episode.podcastTitle,
            podcastId = episode.podcastId, artUrl = episode.artUrl, durationSeconds = episode.durationSeconds,
        )
    }
}

// Offline downloads (§11): into this app's own storage, one file per track or episode, plus the
// art so the downloads look right in a tunnel. Music comes from /download with this phone's
// codecs; episodes from `audioUrl?download=1`. A WorkManager job does the fetching, so it carries
// on with the app closed and waits for Wi-Fi when the person asked for that.
class AudioDownloads(private val runtime: AudioRuntime) {
    private val root: File get() = File(runtime.context.filesDir, "audio")
    private val mediaDir: File get() = File(root, "media")
    private val artDir: File get() = File(root, "art")
    private val indexFile: File get() = File(root, "downloads.json")

    private val _entries = MutableStateFlow(load())
    val entries: StateFlow<List<DownloadEntry>> = _entries.asStateFlow()

    fun isDownloaded(type: String, id: String): Boolean = _entries.value.any { it.type == type && it.id == id && it.done }

    fun entry(type: String, id: String): DownloadEntry? = _entries.value.firstOrNull { it.type == type && it.id == id }

    fun localFile(type: String, id: String): File? {
        val entry = entry(type, id) ?: return null
        if (!entry.done) return null
        return fileFor(entry).takeIf { it.exists() && it.length() > 0 }
    }

    fun localArt(artUrl: String?): File? {
        val url = artUrl ?: return null
        return artFile(url).takeIf { it.exists() && it.length() > 0 }
    }

    val totalBytes: Long get() = _entries.value.sumOf { it.bytes }

    fun downloadTracks(tracks: List<Track>) = enqueue(tracks.map(DownloadEntry::of))

    fun downloadEpisode(episode: Episode) = enqueue(listOf(DownloadEntry.of(episode)))

    fun retry(entry: DownloadEntry) = enqueue(listOf(entry.copy(state = "queued", error = null, progress = 0f)))

    private fun enqueue(new: List<DownloadEntry>) {
        if (new.isEmpty()) return
        update { list ->
            val keys = new.map { it.key }.toSet()
            list.filterNot { it.key in keys && !it.done } + new.filter { entry -> list.none { it.key == entry.key && it.done } }
        }
        schedule()
    }

    fun remove(type: String, id: String) {
        val entry = entry(type, id) ?: return
        fileFor(entry).delete()
        update { list -> list.filterNot { it.key == entry.key } }
        cleanArt()
        runtime.scope.launch { runtime.engine.refreshSources() }
    }

    fun removeAll(type: String? = null) {
        val gone = _entries.value.filter { type == null || it.type == type }
        gone.forEach { fileFor(it).delete() }
        update { list -> list.filterNot { entry -> gone.any { it.key == entry.key } } }
        cleanArt()
        runtime.scope.launch { runtime.engine.refreshSources() }
    }

    fun schedule() {
        val network = if (runtime.prefs.downloadOnWifiOnly) NetworkType.UNMETERED else NetworkType.CONNECTED
        val request = OneTimeWorkRequestBuilder<AudioDownloadWorker>()
            .setConstraints(Constraints.Builder().setRequiredNetworkType(network).build())
            .build()
        WorkManager.getInstance(runtime.context).enqueueUniqueWork(WORK, ExistingWorkPolicy.APPEND_OR_REPLACE, request)
    }

    // Runs in the worker: each queued entry in turn, re-reading the list so ones added meanwhile
    // are picked up too.
    suspend fun runQueue(onProgress: suspend (String, Int) -> Unit) {
        while (true) {
            val next = _entries.value.firstOrNull { it.state == "queued" || it.state == "downloading" } ?: break
            onProgress(next.title, _entries.value.count { it.state == "queued" || it.state == "downloading" })
            setState(next.key) { it.copy(state = "downloading", progress = 0f, error = null) }
            try {
                val bytes = fetch(next)
                fetchArt(next.artUrl)
                setState(next.key) { it.copy(state = "done", progress = 1f, bytes = bytes) }
                withContext(Dispatchers.Main) { runtime.engine.refreshSources() }
            } catch (error: IOException) {
                Log.w(AudioApi.TAG, "Download of ${next.title} failed: ${error.message}")
                if (error is AudioApiException) {
                    // The server said no: that one won't work on a retry either.
                    setState(next.key) { it.copy(state = "failed", error = error.message ?: "Download failed.") }
                } else {
                    // The connection dropped: back in line for when the network returns.
                    setState(next.key) { it.copy(state = "queued", progress = 0f, error = "Waiting for a connection") }
                    throw error
                }
            }
        }
    }

    private suspend fun fetch(entry: DownloadEntry): Long = withContext(Dispatchers.IO) {
        val path = if (entry.type == AudioItem.TYPE_EPISODE) {
            AudioApi.episodeAudioPath(entry.id, download = true)
        } else {
            AudioApi.trackDownloadPath(entry.id, if (runtime.prefs.dataSaver) AudioRuntime.DATA_SAVER_KBPS else null)
        }
        val connection = runtime.openConnection(path)
        try {
            connection.connect()
            val status = connection.responseCode
            if (status !in 200..299) {
                val body = connection.errorStream?.bufferedReader()?.use { it.readText() }.orEmpty()
                throw AudioApiException(status, AudioApi.errorMessage(body) ?: "The server said HTTP $status.")
            }
            val total = connection.contentLengthLong
            mediaDir.mkdirs()
            val partial = File(mediaDir, fileFor(entry).name + ".part")
            var written = 0L
            var lastReport = 0L
            connection.inputStream.use { input ->
                partial.outputStream().use { output ->
                    val buffer = ByteArray(64 * 1024)
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        output.write(buffer, 0, read)
                        written += read
                        if (total > 0 && written - lastReport > 256 * 1024) {
                            lastReport = written
                            setState(entry.key) { it.copy(progress = (written.toFloat() / total).coerceIn(0f, 1f)) }
                        }
                    }
                }
            }
            if (written == 0L) throw IOException("The server sent an empty file.")
            val target = fileFor(entry)
            target.delete()
            if (!partial.renameTo(target)) throw IOException("Couldn't save the file.")
            written
        } finally {
            connection.disconnect()
        }
    }

    private suspend fun fetchArt(artUrl: String?) = withContext(Dispatchers.IO) {
        val url = artUrl ?: return@withContext
        val target = artFile(url)
        if (target.exists()) return@withContext
        runCatching {
            val connection = runtime.openConnection(AudioApi.sized(url, 600) ?: url)
            try {
                if (connection.responseCode in 200..299) {
                    artDir.mkdirs()
                    connection.inputStream.use { input -> target.outputStream().use { input.copyTo(it) } }
                }
            } finally {
                connection.disconnect()
            }
        }
    }

    private fun cleanArt() {
        val used = _entries.value.mapNotNull { it.artUrl?.let(::artFile)?.name }.toSet()
        artDir.listFiles()?.filter { it.name !in used }?.forEach { it.delete() }
    }

    private fun fileFor(entry: DownloadEntry): File = File(mediaDir, "${entry.type}-${hash(entry.id)}.audio")

    private fun artFile(url: String): File = File(artDir, hash(url) + ".img")

    private fun hash(value: String): String =
        MessageDigest.getInstance("SHA-1").digest(value.toByteArray()).joinToString("") { "%02x".format(it) }.take(24)

    private fun setState(key: String, change: (DownloadEntry) -> DownloadEntry) = update { list -> list.map { if (it.key == key) change(it) else it } }

    @Synchronized
    private fun update(change: (List<DownloadEntry>) -> List<DownloadEntry>) {
        val updated = change(_entries.value)
        _entries.value = updated
        runCatching {
            root.mkdirs()
            val array = JSONArray()
            updated.forEach { array.put(it.toJson()) }
            indexFile.writeText(array.toString())
        }
    }

    private fun load(): List<DownloadEntry> = runCatching {
        if (!indexFile.exists()) emptyList() else JSONArray(indexFile.readText()).objects(DownloadEntry::fromJson)
    }.getOrDefault(emptyList())

    companion object {
        const val WORK = "audio-downloads"
        const val CHANNEL = "audio_downloads"
        const val NOTIFICATION_ID = 4711
    }
}

class AudioDownloadWorker(context: Context, parameters: WorkerParameters) : CoroutineWorker(context, parameters) {
    override suspend fun doWork(): Result {
        val runtime = withContext(Dispatchers.Main) { AudioRuntime.get(applicationContext) }
        return try {
            runtime.downloads.runQueue { title, left ->
                runCatching { setForeground(foregroundInfo(title, left)) }
            }
            Result.success()
        } catch (_: IOException) {
            // The network went: try again when it's back.
            Result.retry()
        }
    }

    override suspend fun getForegroundInfo(): ForegroundInfo = foregroundInfo("Music and podcasts", 1)

    private fun foregroundInfo(title: String, left: Int): ForegroundInfo {
        val manager = applicationContext.getSystemService(NotificationManager::class.java)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && manager.getNotificationChannel(AudioDownloads.CHANNEL) == null) {
            manager.createNotificationChannel(NotificationChannel(AudioDownloads.CHANNEL, "Offline downloads", NotificationManager.IMPORTANCE_LOW))
        }
        val notification = NotificationCompat.Builder(applicationContext, AudioDownloads.CHANNEL)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(if (left > 1) "Downloading $left for offline" else "Downloading for offline")
            .setContentText(title)
            .setOngoing(true)
            .setSilent(true)
            .setProgress(0, 0, true)
            .build()
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            ForegroundInfo(AudioDownloads.NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } else {
            ForegroundInfo(AudioDownloads.NOTIFICATION_ID, notification)
        }
    }
}
