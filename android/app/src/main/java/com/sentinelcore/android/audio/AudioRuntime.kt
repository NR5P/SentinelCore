package com.sentinelcore.android.audio

import android.content.ComponentName
import android.content.Context
import android.content.res.Configuration
import android.net.ConnectivityManager
import android.os.Build
import android.provider.Settings
import android.util.Log
import androidx.core.content.ContextCompat
import androidx.media3.session.MediaController
import androidx.media3.session.SessionToken
import com.google.common.util.concurrent.ListenableFuture
import com.sentinelcore.android.data.Reachability
import com.sentinelcore.android.data.SentinelRepository
import com.sentinelcore.android.data.ServerAddress
import com.sentinelcore.android.data.ServerIdentity
import com.sentinelcore.android.ui.SentinelViewModel
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import java.net.HttpURLConnection
import java.net.URL
import java.util.UUID

// The one audio stack in this process: the API, the player, offline downloads, the report queue
// and the speaker. Like OrganizerRuntime it belongs to the process rather than a screen, because
// playback outlives the Activity (the MediaLibraryService keeps the process alive) and Android
// Auto can start the service with no Activity at all.
class AudioRuntime private constructor(context: Context) {
    val context: Context = context.applicationContext
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    val prefs = AudioPrefs(this.context)

    // The ViewModel's repository once it exists - same address choice (local/remote/HTTP
    // fallback) and session cookie as the rest of the app. Until then (a cold start from Android
    // Auto) one of our own, pointed the same way OrganizerBackgroundSync points its own.
    @Volatile private var attached: SentinelRepository? = null
    private val fallback: SentinelRepository by lazy {
        SentinelRepository(this.context) { message, _ -> Log.d(AudioApi.TAG, message) }.also { repo ->
            val preferences = this.context.getSharedPreferences("sentinel_core", Context.MODE_PRIVATE)
            repo.baseUrl = preferences.getString(SentinelViewModel.SERVER_URL_KEY, "").orEmpty()
        }
    }
    private var fallbackResolved = false

    val repository: SentinelRepository get() = attached ?: fallback

    val api = AudioApi { method, path, query, body, contentType, timeout ->
        ensureAddress()
        repository.organizerRequest(method, path, query, body, contentType, timeout)
    }

    // What the server last said about the two tabs; remembered so the tabs don't blink in on
    // every launch.
    private val _enabled = MutableStateFlow(prefs.lastEnabled)
    val enabled: StateFlow<AudioEnabled> = _enabled.asStateFlow()

    val reports = ReportQueue(this)
    val downloads = AudioDownloads(this)
    val engine: AudioEngine by lazy { AudioEngine(this) }
    val speaker = SpeakerHost(this)
    val speech = SpeechVoice(this.context)

    val deviceId: String = prefs.deviceId
    val deviceName: String get() = prefs.speakerName.ifBlank { defaultDeviceName(context) }
    val deviceKind: String =
        if (context.resources.configuration.smallestScreenWidthDp >= 600) "tablet" else "phone"

    fun attach(repository: SentinelRepository) {
        attached = repository
    }

    fun setEnabled(value: AudioEnabled) {
        prefs.lastEnabled = value
        _enabled.value = value
    }

    // The connection came up: send anything queued while offline, and start the speaker if this
    // device lets others play here.
    fun onConnected() {
        scope.launch { reports.flush() }
        speaker.refresh()
    }

    private suspend fun ensureAddress() {
        if (attached != null || fallbackResolved) return
        fallbackResolved = true
        val preferences = context.getSharedPreferences("sentinel_core", Context.MODE_PRIVATE)
        val local = preferences.getString(SentinelViewModel.SERVER_URL_KEY, "").orEmpty()
        val remote = preferences.getString(SentinelViewModel.REMOTE_SERVER_URL_KEY, "").orEmpty()
        if (local.isBlank()) return
        when (fallback.probe(local)) {
            Reachability.REACHABLE -> fallback.baseUrl = local
            // Only to a server that has already proved it's this one - see ServerIdentity. With
            // nobody watching there's no first-use exception; that waits for the app to be opened.
            Reachability.CERTIFICATE_UNTRUSTED ->
                if (ServerIdentity(context).check(local) == ServerIdentity.Check.Verified) {
                    ServerAddress.httpFallback(local)?.let { fallback.baseUrl = it }
                }
            Reachability.UNREACHABLE -> if (remote.isNotBlank()) fallback.baseUrl = remote
        }
    }

    // Absolute URL and session headers for the player, downloads and artwork.
    fun absoluteUrl(path: String): String? =
        if (path.startsWith("http://", true) || path.startsWith("https://", true)) path else repository.absoluteUrl(path)

    fun headers(): Map<String, String> = repository.mediaHeaders()

    fun openConnection(path: String): HttpURLConnection {
        val url = absoluteUrl(path) ?: throw java.io.IOException("No server address.")
        val connection = URL(url).openConnection() as HttpURLConnection
        connection.connectTimeout = 15_000
        connection.readTimeout = 60_000
        connection.instanceFollowRedirects = true
        headers().forEach { (key, value) -> connection.setRequestProperty(key, value) }
        return connection
    }

    fun isMetered(): Boolean = runCatching {
        ContextCompat.getSystemService(context, ConnectivityManager::class.java)?.isActiveNetworkMetered == true
    }.getOrDefault(false)

    // Data saver: a lower bitrate on mobile data when the person asked for it.
    fun streamBitrateCap(): Int? = if (prefs.dataSaver && isMetered()) DATA_SAVER_KBPS else null

    // Connecting a controller binds the playback service, which creates the session around the
    // engine's player; from then on the service puts itself in the foreground when playback
    // starts. Kept for the life of the process.
    private var controllerFuture: ListenableFuture<MediaController>? = null

    fun ensureService() {
        if (controllerFuture != null) return
        val token = SessionToken(context, ComponentName(context, AudioPlaybackService::class.java))
        controllerFuture = MediaController.Builder(context, token).buildAsync()
    }

    companion object {
        const val DATA_SAVER_KBPS = 128

        @Volatile private var instance: AudioRuntime? = null

        fun get(context: Context): AudioRuntime =
            instance ?: synchronized(this) {
                instance ?: AudioRuntime(context.applicationContext).also { instance = it }
            }

        fun defaultDeviceName(context: Context): String {
            val named = runCatching { Settings.Global.getString(context.contentResolver, Settings.Global.DEVICE_NAME) }.getOrNull()
            return named?.takeIf { it.isNotBlank() } ?: Build.MODEL ?: "Android"
        }
    }
}

// This device's audio preferences. Plain SharedPreferences: they are this phone's, not the
// person's (those live on the server: playlists, per-show speed, loves).
class AudioPrefs(context: Context) {
    private val prefs = context.getSharedPreferences("sentinel_audio", Context.MODE_PRIVATE)

    val deviceId: String = prefs.getString("deviceId", null) ?: ("android-" + UUID.randomUUID().toString()).also {
        prefs.edit().putString("deviceId", it).apply()
    }

    var lastEnabled: AudioEnabled
        get() = AudioEnabled(prefs.getBoolean("musicEnabled", false), prefs.getBoolean("podcastsEnabled", false))
        set(value) = prefs.edit().putBoolean("musicEnabled", value.music).putBoolean("podcastsEnabled", value.podcasts).apply()

    var speakerName: String
        get() = prefs.getString("speakerName", "").orEmpty()
        set(value) = prefs.edit().putString("speakerName", value).apply()

    var letOthersPlay: Boolean
        get() = prefs.getBoolean("letOthersPlay", false)
        set(value) = prefs.edit().putBoolean("letOthersPlay", value).apply()

    var allowAnnouncements: Boolean
        get() = prefs.getBoolean("allowAnnouncements", true)
        set(value) = prefs.edit().putBoolean("allowAnnouncements", value).apply()

    var soundCheck: Boolean
        get() = prefs.getBoolean("soundCheck", true)
        set(value) = prefs.edit().putBoolean("soundCheck", value).apply()

    var dataSaver: Boolean
        get() = prefs.getBoolean("dataSaver", false)
        set(value) = prefs.edit().putBoolean("dataSaver", value).apply()

    var downloadOnWifiOnly: Boolean
        get() = prefs.getBoolean("downloadWifiOnly", true)
        set(value) = prefs.edit().putBoolean("downloadWifiOnly", value).apply()

    // The speed podcasts play at when the show has none of its own.
    var podcastSpeed: Float
        get() = prefs.getFloat("podcastSpeed", 1.0f)
        set(value) = prefs.edit().putFloat("podcastSpeed", value).apply()

    var musicSpeed: Float
        get() = prefs.getFloat("musicSpeed", 1.0f)
        set(value) = prefs.edit().putFloat("musicSpeed", value).apply()

    var skipBackSeconds: Int
        get() = prefs.getInt("skipBack", 15)
        set(value) = prefs.edit().putInt("skipBack", value).apply()

    var skipForwardSeconds: Int
        get() = prefs.getInt("skipForward", 30)
        set(value) = prefs.edit().putInt("skipForward", value).apply()

    var equalizerEnabled: Boolean
        get() = prefs.getBoolean("eqEnabled", false)
        set(value) = prefs.edit().putBoolean("eqEnabled", value).apply()

    // -1 = custom (the band levels below).
    var equalizerPreset: Int
        get() = prefs.getInt("eqPreset", -1)
        set(value) = prefs.edit().putInt("eqPreset", value).apply()

    var equalizerBands: List<Int>
        get() = prefs.getString("eqBands", "").orEmpty().split(",").mapNotNull { it.toIntOrNull() }
        set(value) = prefs.edit().putString("eqBands", value.joinToString(",")).apply()

    var bassBoost: Int
        get() = prefs.getInt("bassBoost", 0)
        set(value) = prefs.edit().putInt("bassBoost", value).apply()

    var lastSpeakerSeq: Long
        get() = prefs.getLong("speakerSeq", -1L)
        set(value) = prefs.edit().putLong("speakerSeq", value).apply()

    // The banner for another device's queue is dismissed per device until it plays something new.
    var dismissedContinue: String
        get() = prefs.getString("dismissedContinue", "").orEmpty()
        set(value) = prefs.edit().putString("dismissedContinue", value).apply()

    var lastMusicPane: Int
        get() = prefs.getInt("musicPane", 0)
        set(value) = prefs.edit().putInt("musicPane", value).apply()

    var lastPodcastPane: Int
        get() = prefs.getInt("podcastPane", 0)
        set(value) = prefs.edit().putInt("podcastPane", value).apply()
}

private fun Configuration.isTablet(): Boolean = smallestScreenWidthDp >= 600
