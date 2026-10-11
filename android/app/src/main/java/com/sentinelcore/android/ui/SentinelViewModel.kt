package com.sentinelcore.android.ui

import android.Manifest
import android.app.Application
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Build
import com.sentinelcore.android.data.MediaApi
import com.sentinelcore.android.audio.AudioEnabled
import com.sentinelcore.android.audio.AudioRuntime
import com.sentinelcore.android.data.WeatherDetails
import com.sentinelcore.android.data.WeatherMapInfo
import com.sentinelcore.android.data.WeatherReport
import com.sentinelcore.android.data.WeatherStorms
import com.sentinelcore.android.data.MediaBrowseListing
import com.sentinelcore.android.data.MediaDiscoveredComputer
import com.sentinelcore.android.data.MediaLibrary
import com.sentinelcore.android.data.MediaLibraryInput
import com.sentinelcore.android.data.MediaNetworkComputer
import com.sentinelcore.android.data.MediaNetworkSignInInput
import com.sentinelcore.android.data.MediaNetworkSignInResult
import com.sentinelcore.android.data.MediaHome
import com.sentinelcore.android.data.MediaItemDetail
import com.sentinelcore.android.data.MediaItemSummary
import com.sentinelcore.android.data.MediaItemsPage
import com.sentinelcore.android.data.MediaPlayback
import com.sentinelcore.android.data.MediaProgress
import com.sentinelcore.android.data.MediaStatus
import androidx.core.content.ContextCompat
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.sentinelcore.android.organizer.AppOrganizerTransport
import com.sentinelcore.android.organizer.OrganizerAlarms
import com.sentinelcore.android.organizer.OrganizerDevice
import com.sentinelcore.android.organizer.OrganizerRuntime
import com.sentinelcore.android.organizer.OrganizerKinds
import com.sentinelcore.android.organizer.VoiceCapture
import com.google.firebase.FirebaseApp
import com.google.firebase.messaging.FirebaseMessaging
import com.sentinelcore.android.data.Account
import com.sentinelcore.android.data.ActivityItem
import com.sentinelcore.android.data.AuthStatus
import com.sentinelcore.android.data.PtzCommandQueue
import com.sentinelcore.android.data.Camera
import com.sentinelcore.android.data.CameraInput
import com.sentinelcore.android.data.CameraProbeRequest
import com.sentinelcore.android.data.CameraProbeResult
import com.sentinelcore.android.data.SECRET_MASK
import com.sentinelcore.android.data.ServerIdentity
import com.sentinelcore.android.data.ConnectionState
import com.sentinelcore.android.data.DeviceCandidate
import com.sentinelcore.android.data.DeviceScanSnapshot
import com.sentinelcore.android.data.DeviceScanStartResult
import com.sentinelcore.android.data.DiagnosticEntry
import com.sentinelcore.android.data.DiagnosticLevel
import com.sentinelcore.android.data.FamilyDeleteResult
import com.sentinelcore.android.data.FamilyDrive
import com.sentinelcore.android.data.FamilyDriveInput
import com.sentinelcore.android.data.FamilyDriveTestResult
import com.sentinelcore.android.data.FamilyDuplicateReport
import com.sentinelcore.android.data.FamilyFileEntry
import com.sentinelcore.android.data.FamilyFilesStatus
import com.sentinelcore.android.data.FamilyFolderListing
import com.sentinelcore.android.data.FamilyJob
import com.sentinelcore.android.data.FamilyRestoreResult
import com.sentinelcore.android.data.FamilyTrashBatch
import com.sentinelcore.android.data.FamilyUploadNaming
import com.sentinelcore.android.data.FamilyUploadProgress
import com.sentinelcore.android.data.FamilyUploadState
import android.net.Uri
import java.io.File
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import com.sentinelcore.android.data.HomeKitSettings
import com.sentinelcore.android.data.BackupRestoreSummary
import com.sentinelcore.android.data.HomeKitStatus
import com.sentinelcore.android.data.AlarmCommandReply
import com.sentinelcore.android.data.AlarmConfig
import com.sentinelcore.android.data.AlarmEvent
import com.sentinelcore.android.data.AlarmSensor
import com.sentinelcore.android.data.AlarmSensorInput
import com.sentinelcore.android.data.AlarmStatus
import com.sentinelcore.android.data.HubDevice
import com.sentinelcore.android.widgets.AlarmWidgetSnapshot
import com.sentinelcore.android.data.HubDeviceInput
import com.sentinelcore.android.data.KnownFace
import com.sentinelcore.android.data.KnownVehicle
import com.sentinelcore.android.data.LiveCameraOrder
import com.sentinelcore.android.data.PromptProfileName
import com.sentinelcore.android.data.PushStatus
import com.sentinelcore.android.data.Reachability
import com.sentinelcore.android.data.RecordingTimelineDay
import com.sentinelcore.android.data.Schedule
import com.sentinelcore.android.data.ScheduleInput
import com.sentinelcore.android.data.ScheduleMetadata
import com.sentinelcore.android.data.SchedulePreview
import com.sentinelcore.android.data.ScheduleRun
import com.sentinelcore.android.data.ScheduleRunResult
import com.sentinelcore.android.data.ScanInterface
import com.sentinelcore.android.data.ScanJobSnapshot
import com.sentinelcore.android.data.SecurityEvent
import com.sentinelcore.android.data.SentinelApiException
import com.sentinelcore.android.data.SentinelRepository
import com.sentinelcore.android.data.ServerAddress
import com.sentinelcore.android.data.ServerCompatibility
import com.sentinelcore.android.data.SettingsAction
import com.sentinelcore.android.data.SettingsFieldOption
import com.sentinelcore.android.data.SettingsLookup
import com.sentinelcore.android.data.SettingsLookupCandidate
import com.sentinelcore.android.data.SettingsOptionsSource
import com.sentinelcore.android.data.SettingsSection
import com.sentinelcore.android.data.SystemStatus
import com.sentinelcore.android.data.TriggerConfig
import com.sentinelcore.android.net.DiscoveredServer
import com.sentinelcore.android.net.ServerDiscovery
import com.sentinelcore.android.publish.DevicePublisher
import com.sentinelcore.android.push.PushNavigation
import com.sentinelcore.android.push.PushTokenStore
import com.sentinelcore.android.push.PushTokenUpdates
import java.net.URI
import java.time.Instant
import java.time.LocalDate
import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class SentinelUiState(
    val serverUrl: String = "",
    // Optional Cloudflare Tunnel (or any other externally-reachable) address, used only when
    // the local address can't be reached - see SentinelViewModel.connect() for the resolution.
    val remoteServerUrl: String = "",
    val activeServerUrl: String = "",
    val isUsingRemote: Boolean = false,
    // Whether the local address was an https:// one this device wouldn't trust, and the app
    // retried on the server's plain-HTTP port. Surfaced in Settings: the connection works, but
    // it is unencrypted, and that shouldn't be silent.
    val isUsingHttpFallback: Boolean = false,
    val connectionState: ConnectionState = ConnectionState.NOT_CONFIGURED,
    // Whether the server has the Organizer switched on - its own answer once it has given one
    // this launch, otherwise what it said last time, so the section is there offline too.
    val organizerEnabled: Boolean = false,
    // Whether the server this app is talking to is older than it expects. Advisory: nothing in the
    // app behaves differently, it just says so - see ServerCompatibility.
    val serverCompatibility: ServerCompatibility = ServerCompatibility.Unknown,
    val authStatus: AuthStatus? = null,
    // The single Events list: motion and the AI's read of it, already joined server-side into one
    // row per moment. This replaced two separately-fetched lists (events and triggers), which had
    // the app showing the same moment twice whenever a camera forwarded to AI analysis.
    val activity: List<ActivityItem> = emptyList(),
    // "Show hidden" - motion the AI rejected as nothing, plus anything still awaiting a verdict.
    // Not persisted: the confirmed-only list is the one to be looking at day to day.
    val showHiddenActivity: Boolean = false,
    // An alert opened from a push notification that isn't in `activity` - too old to be on the
    // current page, or hidden by the confirmed-only filter. Fetched on its own so the banner
    // always opens something. See resolvePendingPushEvent.
    val pushedEvent: SecurityEvent? = null,
    val cameras: List<Camera> = emptyList(),
    // How this account has arranged the Live grid, as last known from the server. Kept apart from
    // `cameras` on purpose: that list stays the canonical, order-agnostic one every other screen
    // reads, and only the Live grid cares where the tiles sit - see liveCameras below.
    val liveOrder: List<String> = emptyList(),
    val accounts: List<Account> = emptyList(),
    val knownFaces: List<KnownFace> = emptyList(),
    // Whether the server has the Device Hub switched on - drives whether its bottom-bar
    // destination exists at all.
    val deviceHubEnabled: Boolean = false,
    // Whether the server has Movies & TV switched on - the MOVIES tab exists only then. Starts
    // from what the server said last time, like organizerEnabled.
    val moviesEnabled: Boolean = false,
    // Whether the server has the Alarm switched on - drives whether its destination exists.
    val alarmEnabled: Boolean = false,
    // Whether the server has the Weather tab - every server since it was added does, whatever
    // the location setting; only an older server lacks it. Starts from what the server said last
    // time, like moviesEnabled, so the tab doesn't blink in on every launch.
    val weatherEnabled: Boolean = false,
    // Music & Podcasts (core/docs/MUSIC_AND_PODCASTS.md): each tab exists only while the server
    // has it switched on. Starts from what the server said last time.
    val musicEnabled: Boolean = false,
    val podcastsEnabled: Boolean = false,
    // The alarm as last polled - see AlarmScreen. Null until the screen has asked.
    val alarmStatus: AlarmStatus? = null,
    // Whether the server has the Files library switched on - same treatment as the Device Hub.
    val familyFilesEnabled: Boolean = false,
    // Drives the UNLICENSED watermark over camera images and live video. Starts out - and stays -
    // true whenever the check can't be made: an older server has no such endpoint, and a request
    // that simply failed says nothing about the licence. Accusing a paying customer over a dropped
    // request would be a worse bug than missing the nag.
    val isLicensed: Boolean = true,
    val devices: List<HubDevice> = emptyList(),
    val deviceBrokerConnected: Boolean = true,
    val isLoading: Boolean = false,
    val lastError: String? = null,
    val diagnostics: List<DiagnosticEntry> = emptyList(),
    val lastUpdated: Instant? = null,
    val pushStatus: PushStatus? = null,
    val systemStatus: SystemStatus? = null,
    val pushPermission: String = "NOT REQUESTED",
    val pushRegistrationEnabled: Boolean = false,
    val pushMessage: String? = null,
    val pendingPushEventId: String? = null,
    val timelineDay: RecordingTimelineDay? = null,
    val timelineLoading: Boolean = false,
    val timelineError: String? = null,
    val timelineCameras: List<String> = emptyList(),
    val discoveredServers: List<DiscoveredServer> = emptyList(),
) {
    // A push notification names an alert, and alerts are the analyzed half of the Events list -
    // derived rather than fetched separately, so there's still exactly one request behind the
    // list. Motion rows have no alert id and correctly never match a notification.
    val events: List<SecurityEvent>
        get() = (listOfNotNull(pushedEvent) + activity.mapNotNull { it.asSecurityEvent() })
            .distinctBy { it.id }

    // The cameras as this account arranged them - what the Live grid renders. Every other screen
    // keeps reading `cameras`, which stays in the server's own order.
    val liveCameras: List<Camera>
        get() = LiveCameraOrder.arrange(liveOrder, cameras)

    val isConfigured: Boolean
        get() = runCatching {
            val uri = URI(serverUrl)
            uri.scheme in setOf("http", "https") && !uri.host.isNullOrBlank()
        }.getOrDefault(false)

    val hasRemoteServerUrl: Boolean
        get() = runCatching {
            val uri = URI(remoteServerUrl)
            uri.scheme in setOf("http", "https") && !uri.host.isNullOrBlank()
        }.getOrDefault(false)

    val requiresLogin: Boolean
        get() = authStatus?.let { it.enabled && !it.authenticated } == true

    // Absent role (older server, or login not required) reads as Admin - matches the server's
    // own AuthGateMiddleware treatment of "no login required" as full access for everyone.
    val isAdmin: Boolean
        get() = (authStatus?.role ?: "Admin") == "Admin"
}

class SentinelViewModel(application: Application) : AndroidViewModel(application) {
    private val preferences =
        application.getSharedPreferences("sentinel_core", Application.MODE_PRIVATE)
    private val logSequence = AtomicLong()
    private val repository = SentinelRepository(application, ::log)
    private val serverIdentity = ServerIdentity(application)

    // Set by resolveActiveServerUrl when something at the local address failed the identity check,
    // so the connection that follows reports that instead of a bare certificate error.
    private var identityRefusal: String? = null

    // The shared player, downloads and speaker - process-wide, like the Organizer's runtime.
    val audio: AudioRuntime = AudioRuntime.get(application)
    private val serverDiscovery = ServerDiscovery(application)
    private val _state = MutableStateFlow(
        SentinelUiState(
            serverUrl = preferences.getString(SERVER_URL_KEY, "").orEmpty(),
            remoteServerUrl = preferences.getString(REMOTE_SERVER_URL_KEY, "").orEmpty(),
            pushRegistrationEnabled = preferences.getBoolean(PUSH_ENABLED_KEY, false),
            organizerEnabled = OrganizerDevice.lastKnownEnabled(application),
            moviesEnabled = preferences.getBoolean(MOVIES_ENABLED_KEY, false),
            weatherEnabled = preferences.getBoolean(WEATHER_ENABLED_KEY, false),
            musicEnabled = AudioRuntime.get(application).enabled.value.music,
            podcastsEnabled = AudioRuntime.get(application).enabled.value.podcasts,
        ),
    )

    // Notes, calendar, reminders and boards - see organizer/OrganizerEngine and OrganizerRuntime,
    // which owns it: the same engine serves background syncs when no screen is open. Created up front
    // and independent of the connection: this device's copy works offline, before the server has
    // been reached this launch. Its own scope on IO, since a sync applies server replies to the
    // local SQLite copy and that belongs off the main thread.
    private val organizerRuntime = OrganizerRuntime.get(application)
    private val organizerScope = organizerRuntime.scope
    val organizer = organizerRuntime.engine
    private var organizerActive = true
    val state: StateFlow<SentinelUiState> = _state.asStateFlow()

    private var connectionJob: Job? = null
    private var timelineJob: Job? = null

    init {
        repository.baseUrl = _state.value.serverUrl
        // The device-camera engine shares this repository - same server, same session cookie -
        // and outlives this ViewModel, since a foreground service keeps it streaming after the
        // Activity is gone. See DevicePublisher.
        DevicePublisher.attach(application, repository, ::log)
        // Music & Podcasts share this repository too, and outlive this ViewModel the same way:
        // the playback service keeps the process alive. See audio/AudioRuntime.
        audio.attach(repository)
        refreshNotificationPermission()
        // The engine follows the connection: running while online and in the foreground.
        viewModelScope.launch {
            state.map { it.connectionState }.distinctUntilChanged().collect { updateOrganizerConnection() }
        }
        viewModelScope.launch {
            organizer.status.collect { status ->
                if (status != null) {
                    organizerRuntime.setEnabled(application, status.enabled)
                    _state.update { it.copy(organizerEnabled = status.enabled) }
                }
            }
        }
        viewModelScope.launch {
            PushTokenUpdates.token.collect { token ->
                if (!token.isNullOrBlank()) syncPushRegistration(token)
            }
        }
        viewModelScope.launch {
            PushNavigation.eventId.collect { eventId ->
                if (!eventId.isNullOrBlank()) openPushEvent(eventId)
            }
        }
        viewModelScope.launch {
            serverDiscovery.servers.collect { servers ->
                _state.update { it.copy(discoveredServers = servers) }
            }
        }
    }

    // Local-network discovery is only useful before a server is configured - see ServerDiscovery
    // and SentinelApp's LaunchedEffect(state.isConfigured) that drives these.
    fun startDiscovery() = serverDiscovery.start()

    fun stopDiscovery() = serverDiscovery.stop()

    override fun onCleared() {
        super.onCleared()
        serverDiscovery.stop()
        // The engine and its scope belong to the process (OrganizerRuntime), not this screen.
        organizer.disconnect()
    }

    private fun updateOrganizerConnection() {
        val online = _state.value.connectionState == ConnectionState.ONLINE
        if (online && organizerActive) {
            if (!organizer.isConnected) organizer.connect(AppOrganizerTransport(repository))
        } else if (organizer.isConnected) {
            organizer.disconnect()
        }
    }

    // A reminder or note said to the phone (organizer/VoiceCapture). Saved to this phone's copy
    // at once, alert and all; the engine sends it as soon as it's connected. Returns what to tell
    // the person, and whether it was added.
    fun addFromVoice(kind: String, text: String, subject: String? = null, shared: Boolean = false): Pair<Boolean, String> {
        // Refused only when the server has said it's off, now or the last time it was asked. Not
        // knowing - a first launch with no signal - keeps the item to send when it can.
        val enabled = organizer.status.value?.enabled ?: OrganizerDevice.knownEnabled(getApplication<Application>())
        if (enabled == false) {
            return false to "The Organizer is turned off. It's in the dashboard's Settings, under Organizer."
        }
        // Until the first sync this phone doesn't know the person's own space, and new things
        // would default to Family - where everyone sees them.
        if (organizer.spaces.value.isEmpty()) {
            return false to "Sentinel Core hasn't synced your Organizer yet. Try again once it has connected to your server."
        }
        return if (shared) {
            organizer.create(OrganizerKinds.NOTE, VoiceCapture.sharedNote(text, subject))
            true to "Saved to your notes."
        } else if (kind == OrganizerKinds.NOTE) {
            organizer.create(OrganizerKinds.NOTE, VoiceCapture.note(text))
            true to "Saved your note."
        } else {
            val (fields, said) = VoiceCapture.reminder(text)
            organizer.create(OrganizerKinds.REMINDER, fields)
            true to said
        }
    }

    // From the activity's lifecycle: a backgrounded app shouldn't hold a long poll open.
    fun setOrganizerActive(active: Boolean) {
        organizerActive = active
        if (active) organizerRuntime.widgets.appBecameActive()
        if (active && organizer.isConnected) organizerScope.launch { organizer.sync() }
        updateOrganizerConnection()
    }

    fun connect(resetNetworkSession: Boolean = false) {
        if (!_state.value.isConfigured) {
            _state.update { it.copy(connectionState = ConnectionState.NOT_CONFIGURED) }
            return
        }
        if (connectionJob?.isActive == true) {
            log("Connection request skipped because one is already active", DiagnosticLevel.NOTICE)
            return
        }

        connectionJob = viewModelScope.launch {
            val wasOffline = _state.value.connectionState == ConnectionState.OFFLINE
            if (_state.value.connectionState != ConnectionState.ONLINE) {
                _state.update { it.copy(connectionState = ConnectionState.CHECKING) }
            }
            if (resetNetworkSession || wasOffline) repository.resetSession()
            log("Connecting to ${_state.value.serverUrl}", DiagnosticLevel.INFO)
            identityRefusal = null
            resolveActiveServerUrl()

            try {
                val auth = repository.authStatus()
                _state.update { it.copy(authStatus = auth) }
                // Before the authentication return below, so the advisory is already known at the
                // login screen. Never throws - see checkServerCompatibility.
                checkServerCompatibility()
                if (auth.enabled && !auth.authenticated) {
                    log("Server requires dashboard authentication", DiagnosticLevel.INFO)
                    _state.update {
                        it.copy(connectionState = ConnectionState.AUTHENTICATION_REQUIRED)
                    }
                    return@launch
                }
                refreshAll()
                _state.update { it.copy(connectionState = ConnectionState.ONLINE) }
                log(
                    "Connection established; loaded ${_state.value.activity.size} events",
                    DiagnosticLevel.INFO,
                )
                resolvePendingPushEvent()
                audio.onConnected()
                syncPushRegistration()
                resolvePendingPushEvent()
            } catch (error: Exception) {
                _state.update {
                    it.copy(
                        connectionState = if (error is SentinelApiException && error.statusCode == 401) {
                            ConnectionState.AUTHENTICATION_REQUIRED
                        } else {
                            ConnectionState.OFFLINE
                        },
                        lastError = identityRefusal ?: readableError(error),
                    )
                }
                log("Connection failed: ${readableError(error)}", DiagnosticLevel.ERROR)
            }
        }
    }

    // Deliberately swallows every failure. This is a diagnostic nicety layered on top of a
    // connection that has already succeeded - failing the connect() that called it, over a server
    // that merely declined to describe itself, would turn a cosmetic feature into an outage.
    // Cancellation is rethrown rather than reported: it means the connect job itself was torn
    // down, which is the app working correctly and not something to log as a version problem.
    private suspend fun checkServerCompatibility() {
        val compatibility = try {
            ServerCompatibility.evaluate(repository.serverHealth())
        } catch (cancellation: CancellationException) {
            throw cancellation
        } catch (error: Exception) {
            log("Could not read the server's version: ${readableError(error)}", DiagnosticLevel.INFO)
            ServerCompatibility.Unknown
        }
        _state.update { it.copy(serverCompatibility = compatibility) }
        compatibility.advisory?.let { log(it, DiagnosticLevel.NOTICE) }
    }

    // Prefers the local address whenever it's reachable at all (any HTTP response counts, not
    // just 2xx) and only falls back to the remote address on an actual network failure - this
    // runs at the top of every connect(), so moving between home and away self-heals on the
    // next reconnect without any WiFi/SSID detection.
    private suspend fun resolveActiveServerUrl() {
        val current = _state.value
        val httpFallback = ServerAddress.httpFallback(current.serverUrl)

        // Probing costs a round trip, and is only worth it when there is somewhere else to go.
        if (!current.hasRemoteServerUrl && httpFallback == null) {
            useServerUrl(current.serverUrl, isRemote = false, isHttpFallback = false)
            return
        }

        when (repository.probe(current.serverUrl)) {
            Reachability.REACHABLE -> {
                useServerUrl(current.serverUrl, isRemote = false, isHttpFallback = false)
                return
            }
            // Tried before the remote address, not after: an untrusted local certificate says
            // nothing about which network this device is on, and the server's own plain-HTTP port
            // is a better answer than sending home traffic out over the Internet and back.
            //
            // The fallback is derived from the local address only. Quietly dropping encryption on
            // the remote address would be doing it across the open Internet, which is a different
            // proposition entirely - and, when that address is fronted by a TLS-terminating proxy,
            // lands on a Secure session cookie this app cannot return anyway.
            //
            // And only to a server that proves it is this one (see ServerIdentity). A server that
            // hasn't yet - the first fallback, before its authority is remembered - gets none of
            // the existing session: it is dropped, and the sign-in screen asks for the password.
            Reachability.CERTIFICATE_UNTRUSTED -> if (httpFallback != null) {
                val identity = serverIdentity.check(current.serverUrl)
                when (identity) {
                    ServerIdentity.Check.Mismatch -> {
                        log(
                            "The server at ${current.serverUrl} presented a certificate its own " +
                                "authority didn't sign - not falling back to plain HTTP",
                            DiagnosticLevel.ERROR,
                        )
                        identityRefusal = IMPOSTOR_MESSAGE
                    }
                    ServerIdentity.Check.Unavailable -> log(
                        "Couldn't check the certificate at ${current.serverUrl} before falling " +
                            "back to plain HTTP",
                        DiagnosticLevel.NOTICE,
                    )
                    ServerIdentity.Check.Verified, is ServerIdentity.Check.Unknown -> {
                        if (identity is ServerIdentity.Check.Unknown) repository.forgetSession(httpFallback)
                        if (useHttpFallback(current.serverUrl, httpFallback, identity)) return
                    }
                }
            }
            Reachability.UNREACHABLE -> Unit
        }

        if (!current.hasRemoteServerUrl) {
            // Nothing left to try. Go with the configured address anyway, so the connection that
            // follows reports the server's own error rather than a probe's silent verdict.
            useServerUrl(current.serverUrl, isRemote = false, isHttpFallback = false)
            return
        }
        log("Local address unreachable; using remote address", DiagnosticLevel.INFO)
        useServerUrl(current.remoteServerUrl, isRemote = true, isHttpFallback = false)
    }

    // The plain-HTTP rung of resolveActiveServerUrl, once the identity check has allowed it. True
    // when it was taken.
    private suspend fun useHttpFallback(
        httpsAddress: String,
        httpFallback: String,
        identity: ServerIdentity.Check,
    ): Boolean {
        log(
            "This device does not trust the server's HTTPS certificate; trying the " +
                "plain-HTTP port at $httpFallback",
            DiagnosticLevel.NOTICE,
        )
        if (repository.probe(httpFallback) == Reachability.REACHABLE) {
            if (identity is ServerIdentity.Check.Unknown) {
                rememberServerIdentity(identity, httpsAddress, httpFallback)
            }
            log(
                "Using $httpFallback. To connect over HTTPS instead, install the server's " +
                    "certificate authority (ca.crt) on this device - see DEPLOYMENT.md " +
                    "section 14a.",
                DiagnosticLevel.NOTICE,
            )
            useServerUrl(httpFallback, isRemote = false, isHttpFallback = true)
            return true
        }
        log("The plain-HTTP port at $httpFallback did not answer either", DiagnosticLevel.NOTICE)
        return false
    }

    // Trust on first use - see ServerIdentity. Best effort: failing to remember just means the
    // next fallback is treated as a first one again.
    private suspend fun rememberServerIdentity(
        identity: ServerIdentity.Check.Unknown,
        httpsAddress: String,
        httpAddress: String,
    ) {
        val authority = ServerIdentity.fetchAuthority(httpAddress)
        when {
            authority == null ->
                log("Couldn't download the server's certificate authority to remember it", DiagnosticLevel.NOTICE)
            serverIdentity.remember(authority, identity.leaf, httpsAddress) ->
                log(
                    "Remembered this server's certificate authority; plain-HTTP fallback now only " +
                        "goes to this server",
                    DiagnosticLevel.NOTICE,
                )
            else -> log(
                "The certificate authority the server offered didn't sign its HTTPS certificate - " +
                    "not remembering it",
                DiagnosticLevel.NOTICE,
            )
        }
    }

    private fun useServerUrl(url: String, isRemote: Boolean, isHttpFallback: Boolean) {
        repository.baseUrl = url
        _state.update {
            it.copy(
                activeServerUrl = url,
                isUsingRemote = isRemote,
                isUsingHttpFallback = isHttpFallback,
            )
        }
    }

    // Changes what the server is asked for, not how the loaded list is filtered, so it reloads.
    fun toggleShowHidden() {
        _state.update { it.copy(showHiddenActivity = !it.showHiddenActivity) }
        connect()
    }

    suspend fun refreshAll() {
        _state.update { it.copy(isLoading = true) }
        try {
            val currentErrors = mutableListOf<Throwable>()
            val requests = listOf(
                viewModelScope.async {
                    runCatching { repository.activity(_state.value.showHiddenActivity) }
                        .onSuccess { value -> _state.update { it.copy(activity = value) } }
                        .onFailure(currentErrors::add)
                },
                viewModelScope.async {
                    runCatching { repository.cameras() }
                        .onSuccess { value -> _state.update { it.copy(cameras = value) } }
                        .onFailure(currentErrors::add)
                },
                // Same treatment as the two below: a server older than this feature has no such
                // endpoint, and the Live grid falling back to the camera list's own order is a
                // fine outcome, not a failed connection.
                viewModelScope.async {
                    runCatching { repository.liveOrder() }
                        .onSuccess { value -> _state.update { it.copy(liveOrder = value) } }
                },
                // Deliberately not added to currentErrors: an older server has no such
                // endpoint, and a 404 there means "no Device Hub", not a failed connection.
                viewModelScope.async {
                    runCatching { repository.deviceHubEnabled() }
                        .onSuccess { value -> _state.update { it.copy(deviceHubEnabled = value) } }
                        .onFailure { _state.update { it.copy(deviceHubEnabled = false) } }
                },
                // Same treatment: a 404 from a server older than Movies & TV means "off".
                viewModelScope.async {
                    runCatching { repository.mediaEnabled() }
                        .onSuccess(::setMoviesEnabled)
                        .onFailure { setMoviesEnabled(false) }
                },
                // Music & Podcasts: /api/audio/enabled always answers; an older server 404s,
                // which the API reads as both off. A failed request keeps what was known.
                viewModelScope.async {
                    runCatching { audio.api.enabled() }
                        .onSuccess(::setAudioEnabled)
                },
                // Same again for the weather: only an older server lacks it.
                viewModelScope.async {
                    runCatching { repository.weatherAvailable() }
                        .onSuccess(::setWeatherEnabled)
                        .onFailure { setWeatherEnabled(false) }
                },
                // Same again for the alarm: an older server has no such endpoint.
                viewModelScope.async {
                    runCatching { repository.alarmStatus() }
                        .onSuccess { value ->
                            _state.update { it.copy(alarmEnabled = value.enabled, alarmStatus = value) }
                            AlarmWidgetSnapshot.write(getApplication(), value)
                        }
                        .onFailure { _state.update { it.copy(alarmEnabled = false) } }
                },
                // Same again: /status answers enabled:false when the feature is off, and an
                // older server has no such route at all.
                viewModelScope.async {
                    runCatching { repository.familyFilesEnabled() }
                        .onSuccess { value -> _state.update { it.copy(familyFilesEnabled = value) } }
                        .onFailure { _state.update { it.copy(familyFilesEnabled = false) } }
                },
                // Same treatment, opposite default - see SentinelUiState.isLicensed.
                viewModelScope.async {
                    runCatching { repository.licenseValid() }
                        .onSuccess { value -> _state.update { it.copy(isLicensed = value) } }
                        .onFailure { _state.update { it.copy(isLicensed = true) } }
                },
            )
            requests.awaitAll()

            if (_state.value.deviceHubEnabled) {
                runCatching {
                    val devices = repository.devices()
                    _state.update { it.copy(devices = devices) }
                    refreshDeviceStates()
                }.onFailure { error ->
                    log("Could not prime Device Hub state during startup: ${readableError(error)}", DiagnosticLevel.NOTICE)
                }
            }

            val firstError = currentErrors.firstOrNull()
            _state.update {
                it.copy(
                    lastError = firstError?.let(::readableError),
                    lastUpdated = Instant.now(),
                )
            }
            if (firstError != null && _state.value.activity.isEmpty()) throw firstError
            resolvePendingPushEvent()
        } finally {
            _state.update { it.copy(isLoading = false) }
        }
    }

    fun saveServer(address: String) {
        val normalized = SentinelRepository.normalizeAddress(address)
        connectionJob?.cancel()
        repository.clearAuthenticationSession()
        // Saving the address is the way past a server whose certificate authority changed (a
        // reinstall) - see ServerIdentity.
        serverIdentity.forget(normalized)
        repository.baseUrl = normalized
        preferences.edit().putString(SERVER_URL_KEY, normalized).apply()
        _state.update {
            SentinelUiState(
                serverUrl = normalized,
                remoteServerUrl = it.remoteServerUrl,
                diagnostics = it.diagnostics,
                connectionState = if (normalized.isBlank()) {
                    ConnectionState.NOT_CONFIGURED
                } else {
                    ConnectionState.CHECKING
                },
            )
        }
        connect(resetNetworkSession = true)
    }

    // Saves both addresses and reconnects exactly once - saveServer()/a hypothetical
    // saveRemoteServer() called back-to-back would each independently trigger connect(),
    // racing against connect()'s own "already active" guard.
    fun saveServers(localAddress: String, remoteAddress: String) {
        val normalizedLocal = SentinelRepository.normalizeAddress(localAddress)
        val normalizedRemote = if (remoteAddress.isBlank()) "" else SentinelRepository.normalizeAddress(remoteAddress, defaultScheme = "https")
        connectionJob?.cancel()
        repository.clearAuthenticationSession()
        serverIdentity.forget(normalizedLocal)
        repository.baseUrl = normalizedLocal
        preferences.edit()
            .putString(SERVER_URL_KEY, normalizedLocal)
            .putString(REMOTE_SERVER_URL_KEY, normalizedRemote)
            .apply()
        _state.update {
            SentinelUiState(
                serverUrl = normalizedLocal,
                remoteServerUrl = normalizedRemote,
                diagnostics = it.diagnostics,
                connectionState = if (normalizedLocal.isBlank()) {
                    ConnectionState.NOT_CONFIGURED
                } else {
                    ConnectionState.CHECKING
                },
            )
        }
        connect(resetNetworkSession = true)
    }

    fun signIn(username: String, password: String, onFinished: (String?) -> Unit) {
        viewModelScope.launch {
            _state.update { it.copy(isLoading = true, lastError = null) }
            try {
                repository.login(username, password)
                val auth = repository.authStatus()
                _state.update { it.copy(authStatus = auth) }
                refreshAll()
                _state.update { it.copy(connectionState = ConnectionState.ONLINE) }
                refreshPushStatus()
                syncPushRegistration()
                onFinished(null)
            } catch (error: Exception) {
                val message = readableError(error)
                _state.update { it.copy(lastError = message) }
                log("Sign in failed: $message", DiagnosticLevel.ERROR)
                onFinished(message)
            } finally {
                _state.update { it.copy(isLoading = false) }
            }
        }
    }

    fun signOut() {
        viewModelScope.launch {
            runCatching { repository.logout() }
            repository.clearAuthenticationSession()
            val auth = runCatching { repository.authStatus() }.getOrNull()
            _state.update {
                it.copy(
                    authStatus = auth,
                    connectionState = if (auth?.enabled == true) {
                        ConnectionState.AUTHENTICATION_REQUIRED
                    } else {
                        ConnectionState.ONLINE
                    },
                )
            }
        }
    }

    suspend fun loadImage(path: String): Bitmap? = repository.loadImage(path)

    fun absoluteUrl(path: String): String? = repository.absoluteUrl(path)

    fun mediaHeaders(): Map<String, String> = repository.mediaHeaders()

    // ---------------- Music & Podcasts ----------------

    private fun setAudioEnabled(enabled: AudioEnabled) {
        audio.setEnabled(enabled)
        _state.update { it.copy(musicEnabled = enabled.music, podcastsEnabled = enabled.podcasts) }
    }

    // Settings saved a section that might have switched a tab on or off.
    fun refreshAudioEnabled() {
        viewModelScope.launch { runCatching { audio.api.enabled() }.onSuccess(::setAudioEnabled) }
    }

    // ---------------- Weather ----------------

    private fun setWeatherEnabled(enabled: Boolean) {
        preferences.edit().putBoolean(WEATHER_ENABLED_KEY, enabled).apply()
        _state.update { it.copy(weatherEnabled = enabled) }
    }

    suspend fun weatherReport(): WeatherReport = repository.weatherReport()

    suspend fun weatherDetails(): WeatherDetails = repository.weatherDetails()

    suspend fun weatherMap(station: String?): WeatherMapInfo = repository.weatherMap(station)

    suspend fun weatherStorms(path: String): WeatherStorms = repository.weatherStorms(path)

    suspend fun weatherBytes(path: String): ByteArray? = repository.weatherBytes(path)

    // ---------------- Movies & TV ----------------

    private fun setMoviesEnabled(enabled: Boolean) {
        preferences.edit().putBoolean(MOVIES_ENABLED_KEY, enabled).apply()
        _state.update { it.copy(moviesEnabled = enabled) }
    }

    // What this phone can decode, asked of MediaCodecList once: the contract's Android lists
    // minus HEVC / AC-3 / E-AC-3 when there is no decoder for them, so the server converts
    // rather than sending a stream that plays silent or black.
    private val mediaCodecs: Pair<String, String> by lazy {
        runCatching {
            val decoders = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.filterNot { it.isEncoder }
            fun has(mime: String) = decoders.any { info -> info.supportedTypes.any { it.equals(mime, ignoreCase = true) } }
            MediaApi.mediaCodecs(
                hasHevc = has(MediaFormat.MIMETYPE_VIDEO_HEVC),
                hasAc3 = has(MediaFormat.MIMETYPE_AUDIO_AC3),
                hasEac3 = has(MediaFormat.MIMETYPE_AUDIO_EAC3),
            )
        }.getOrDefault(MediaApi.VIDEO_CODECS to MediaApi.AUDIO_CODECS)
    }

    suspend fun loadImage(path: String, maxWidth: Int): Bitmap? = repository.loadImage(path, maxWidth)

    suspend fun mediaStatus(): MediaStatus = repository.mediaStatus()

    suspend fun mediaHome(): MediaHome = repository.mediaHome()

    suspend fun mediaItems(
        kind: String?,
        query: String?,
        genre: String?,
        sort: String,
        order: String,
        unwatched: Boolean,
        offset: Int,
        limit: Int = MediaApi.PAGE_SIZE,
        list: Boolean = false,
    ): MediaItemsPage = repository.mediaItems(kind, query, genre, sort, order, unwatched, offset, limit, list)

    suspend fun mediaRandom(): MediaItemSummary = repository.mediaRandom()

    suspend fun mediaItem(id: String): MediaItemDetail = repository.mediaItem(id)

    // Bumped when a title leaves the library, so the Movies tab's Home and grids load again and
    // its poster is gone when the user lands back there.
    private val _mediaLibraryVersion = MutableStateFlow(0)
    val mediaLibraryVersion: StateFlow<Int> = _mediaLibraryVersion.asStateFlow()

    suspend fun deleteMediaItem(id: String) {
        repository.deleteMediaItem(id)
        _mediaLibraryVersion.value++
    }

    suspend fun mediaPlayback(id: String, start: Double?): MediaPlayback =
        repository.mediaPlayback(id, start, mediaCodecs.first, mediaCodecs.second)

    suspend fun reportMediaProgress(id: String, positionSeconds: Double, durationSeconds: Double?): MediaProgress? =
        repository.reportMediaProgress(id, positionSeconds, durationSeconds)

    // Fire and forget, on this ViewModel's scope rather than the player's, so the report made as
    // the player closes still reaches the server after the player's own scope is gone.
    fun reportMediaProgressInBackground(id: String, positionSeconds: Double, durationSeconds: Double?) {
        viewModelScope.launch {
            runCatching { repository.reportMediaProgress(id, positionSeconds, durationSeconds) }
                .onFailure { log("Could not save playback progress: ${readableError(it)}", DiagnosticLevel.NOTICE) }
        }
    }

    suspend fun setMediaWatched(id: String, watched: Boolean): MediaProgress? = repository.setMediaWatched(id, watched)

    suspend fun setMediaInList(id: String, inList: Boolean): Boolean = repository.setMediaInList(id, inList)

    suspend fun setMediaSeasonWatched(showId: String, season: Int, watched: Boolean) =
        repository.setMediaSeasonWatched(showId, season, watched)

    // Library admin - Settings -> Movies & TV libraries.
    suspend fun mediaLibraries(): List<MediaLibrary> = repository.mediaLibraries()

    suspend fun saveMediaLibrary(id: String?, input: MediaLibraryInput): MediaLibrary = repository.saveMediaLibrary(id, input)

    suspend fun deleteMediaLibrary(id: String) = repository.deleteMediaLibrary(id)

    suspend fun scanMediaLibrary(id: String) = repository.scanMediaLibrary(id)

    suspend fun scanAllMedia() = repository.scanAllMedia()

    // `base` is /api/media for Movies & TV and /api/audio for Music & Podcasts: the same picker
    // and the same saved sign-ins behind both (MUSIC_AND_PODCASTS.md §1).
    suspend fun mediaBrowse(path: String?, base: String = MediaApi.MEDIA_ROUTES): MediaBrowseListing =
        repository.mediaBrowse(path, base)

    suspend fun mediaCreateFolder(parent: String, name: String, base: String = MediaApi.MEDIA_ROUTES): MediaBrowseListing =
        repository.mediaCreateFolder(parent, name, base)

    suspend fun mediaNetworkComputers(base: String = MediaApi.MEDIA_ROUTES): List<MediaNetworkComputer> =
        repository.mediaNetworkComputers(base)

    suspend fun saveMediaNetworkComputer(id: String?, input: MediaNetworkSignInInput, base: String = MediaApi.MEDIA_ROUTES): MediaNetworkSignInResult =
        repository.saveMediaNetworkComputer(id, input, base)

    suspend fun deleteMediaNetworkComputer(id: String, base: String = MediaApi.MEDIA_ROUTES) =
        repository.deleteMediaNetworkComputer(id, base)

    suspend fun discoverMediaNetworkComputers(base: String = MediaApi.MEDIA_ROUTES): List<MediaDiscoveredComputer> =
        repository.discoverMediaNetworkComputers(base)

    // Movie downloads ("+ Add" on the Movies tab) - core/docs/MOVIE_DOWNLOADS.md. Null status
    // means the feature is off or the server predates it.
    suspend fun mediaDownloadStatus(): com.sentinelcore.android.data.MediaDownloadStatus? = repository.mediaDownloadStatus()

    suspend fun mediaDownloadLookup(query: String, kind: String) = repository.mediaDownloadLookup(query, kind)

    suspend fun mediaDownloadShow(tmdbId: Long) = repository.mediaDownloadShow(tmdbId)

    suspend fun startMediaReleaseSearch(input: com.sentinelcore.android.data.MediaReleaseSearchInput) =
        repository.startMediaReleaseSearch(input)

    suspend fun mediaReleaseSearch(id: String) = repository.mediaReleaseSearch(id)

    suspend fun startMediaDownload(searchId: String, releaseId: String, replace: Boolean? = null) =
        repository.startMediaDownload(searchId, releaseId, replace = replace)

    suspend fun mediaDownloads() = repository.mediaDownloads()

    suspend fun mediaDownload(id: String) = repository.mediaDownload(id)

    suspend fun deleteMediaDownload(id: String) = repository.deleteMediaDownload(id)

    suspend fun retryMediaDownload(id: String) = repository.retryMediaDownload(id)

    suspend fun testMediaDownloadVpn() = repository.testMediaDownloadVpn()

    suspend fun mediaDownloadSites() = repository.mediaDownloadSites()

    suspend fun mediaDownloadSiteCatalog(query: String) = repository.mediaDownloadSiteCatalog(query)

    suspend fun addMediaDownloadSite(definition: String, fields: org.json.JSONObject) =
        repository.addMediaDownloadSite(definition, fields)

    suspend fun setMediaDownloadSiteEnabled(id: String, enabled: Boolean) = repository.setMediaDownloadSiteEnabled(id, enabled)

    suspend fun deleteMediaDownloadSite(id: String) = repository.deleteMediaDownloadSite(id)

    // A live tile was dragged onto another one. Applied on screen immediately and saved in the
    // background: the arrangement is what the person just asked for, and a round trip to the
    // server is no reason to make them watch a tile snap back before it moves. A failed save is
    // logged rather than rolled back - the grid they are looking at stays as they left it, and the
    // next refresh simply comes back with the last order the server did store.
    fun moveLiveCamera(movingId: String, targetId: String) {
        val current = _state.value
        val moved = LiveCameraOrder.move(current.liveCameras.map(Camera::id), movingId, targetId)
        if (moved == current.liveOrder) return

        _state.update { it.copy(liveOrder = moved) }
        viewModelScope.launch {
            runCatching { repository.saveLiveOrder(moved) }
                .onFailure { error -> log("Could not save the live camera order: ${readableError(error)}", DiagnosticLevel.ERROR) }
        }
    }

    suspend fun refreshCameras() {
        val value = repository.cameras()
        _state.update { it.copy(cameras = value) }
    }

    // ---------------- Alarm ----------------

    suspend fun refreshAlarm(): AlarmStatus {
        val value = repository.alarmStatus()
        _state.update { it.copy(alarmEnabled = value.enabled, alarmStatus = value) }
        AlarmWidgetSnapshot.write(getApplication(), value)
        return value
    }

    suspend fun armAlarm(mode: String, force: Boolean, pin: String?): AlarmCommandReply =
        repository.armAlarm(mode, force, pin).also { refreshAlarm() }

    suspend fun disarmAlarm(pin: String?): AlarmCommandReply = repository.disarmAlarm(pin).also { refreshAlarm() }

    suspend fun alarmPanic(): AlarmCommandReply = repository.alarmPanic().also { refreshAlarm() }

    suspend fun alarmTest(): AlarmCommandReply = repository.alarmTest()

    suspend fun alarmEvents(): List<AlarmEvent> = repository.alarmEvents()

    fun alarmSensor(id: String): AlarmSensor? = _state.value.alarmStatus?.sensors?.firstOrNull { it.id == id }

    suspend fun saveAlarmSensor(id: String?, input: AlarmSensorInput) {
        if (id == null) repository.createAlarmSensor(input) else repository.updateAlarmSensor(id, input)
        refreshAlarm()
    }

    suspend fun deleteAlarmSensor(id: String) {
        repository.deleteAlarmSensor(id)
        refreshAlarm()
    }

    suspend fun saveAlarmConfig(config: AlarmConfig, newPin: String?, removePin: Boolean) {
        repository.saveAlarmConfig(config)
        if (removePin) repository.setAlarmPin("", null) else if (!newPin.isNullOrBlank()) repository.setAlarmPin(newPin.trim(), null)
        refreshAlarm()
    }

    // ---------------- Device Hub ----------------

    suspend fun refreshDevices() {
        val value = repository.devices()
        _state.update { it.copy(devices = value) }
    }

    // The light poll while the Device Hub screen is open - only the live fields change, so the
    // rest of each device is left alone rather than re-fetched.
    suspend fun refreshDeviceStates() {
        val snapshot = repository.deviceStates()
        _state.update { current ->
            current.copy(
                deviceBrokerConnected = snapshot.connected,
                devices = current.devices.map { device ->
                    snapshot.devices.firstOrNull { it.id == device.id }?.let { state ->
                        device.copy(
                            power = state.power,
                            brightness = state.brightness,
                            available = state.available,
                            measurements = state.measurements,
                        )
                    } ?: device
                },
            )
        }
    }

    /** Returns null when the device confirmed the change, or a message when it didn't. */
    suspend fun sendDeviceCommand(id: String, action: String? = null, brightness: Int? = null): String? {
        val result = repository.sendDeviceCommand(id, action, brightness)
        _state.update { current ->
            current.copy(
                devices = current.devices.map { device ->
                    if (device.id == id) {
                        device.copy(power = result.power, brightness = result.brightness ?: device.brightness)
                    } else {
                        device
                    }
                },
            )
        }
        return if (result.acknowledged) null else "The device did not confirm the change."
    }

    suspend fun createDevice(input: HubDeviceInput): HubDevice {
        val created = repository.createDevice(input)
        _state.update { it.copy(devices = it.devices + created) }
        return created
    }

    suspend fun updateDevice(id: String, input: HubDeviceInput): HubDevice {
        val updated = repository.updateDevice(id, input)
        _state.update { current ->
            current.copy(devices = current.devices.map { if (it.id == id) updated else it })
        }
        return updated
    }

    // Shown immediately, saved in the background with rollback - a round trip to the server is
    // no reason to make the dragged card snap back before it moves.
    fun moveDevice(fromIndex: Int, toIndex: Int) {
        val current = _state.value.devices.toMutableList()
        if (fromIndex !in current.indices || toIndex !in current.indices || fromIndex == toIndex) return
        val previous = _state.value.devices
        current.add(toIndex, current.removeAt(fromIndex))
        _state.update { it.copy(devices = current) }
        viewModelScope.launch {
            try {
                repository.saveDeviceOrder(current.map { it.id })
            } catch (_: Exception) {
                _state.update { it.copy(devices = previous) }
            }
        }
    }

    // Optimistic remove with rollback on failure, matching deleteCamera below.
    suspend fun deleteDevice(device: HubDevice) {
        val previous = _state.value.devices
        _state.update { it.copy(devices = it.devices.filterNot { existing -> existing.id == device.id }) }
        try {
            repository.deleteDevice(device.id)
        } catch (error: Exception) {
            _state.update { it.copy(devices = previous) }
            throw error
        }
    }

    // Schedules keep their state in the screen rather than in SentinelUiState, the same way the
    // server-settings screen does - nothing outside that screen needs them, and they would
    // otherwise be re-fetched on every poll of the shared state.
    suspend fun schedules(): List<Schedule> = repository.schedules()

    suspend fun scheduleMetadata(): ScheduleMetadata = repository.scheduleMetadata()

    suspend fun scheduleRuns(id: String): List<ScheduleRun> = repository.scheduleRuns(id)

    suspend fun previewSchedule(input: ScheduleInput): SchedulePreview = repository.previewSchedule(input)

    suspend fun createSchedule(input: ScheduleInput): Schedule = repository.createSchedule(input)

    suspend fun updateSchedule(id: String, input: ScheduleInput): Schedule = repository.updateSchedule(id, input)

    suspend fun deleteSchedule(id: String) = repository.deleteSchedule(id)

    suspend fun setScheduleEnabled(id: String, enabled: Boolean): Schedule =
        repository.setScheduleEnabled(id, enabled)

    suspend fun runSchedule(id: String): ScheduleRunResult = repository.runSchedule(id)

    suspend fun settingsSchema(): List<SettingsSection> = repository.settingsSchema()

    suspend fun settingsSnapshot(): org.json.JSONObject = repository.settingsSnapshot()

    suspend fun settingsSectionData(endpoint: String): org.json.JSONObject =
        repository.settingsSectionData(endpoint)

    // Whether the server settings list shows advanced sections. Remembered per device, like the
    // web dashboard's own toggle - and purely a display preference: every setting is reachable
    // either way, and searching ignores it entirely.
    var showAdvancedSettings: Boolean
        get() = preferences.getBoolean(ADVANCED_SETTINGS_KEY, false)
        set(value) {
            preferences.edit().putBoolean(ADVANCED_SETTINGS_KEY, value).apply()
        }

    suspend fun saveSettingsSection(endpoint: String, body: org.json.JSONObject): Boolean =
        repository.saveSettingsSection(endpoint, body).also {
            // MUSIC and PODCASTS each carry an `enabled` switch that adds or removes a tab.
            if (endpoint.endsWith("/music") || endpoint.endsWith("/podcasts")) refreshAudioEnabled()
        }

    suspend fun settingsLookup(lookup: SettingsLookup, query: String): List<SettingsLookupCandidate> =
        repository.settingsLookup(lookup, query)

    suspend fun settingsOptions(source: SettingsOptionsSource): List<SettingsFieldOption> =
        repository.settingsOptions(source)

    // String? rather than an inferred type: what an action reports back is part of this screen's
    // contract now that the test sends have something to say, and the neighbours here spell
    // theirs out for the same reason.
    suspend fun runSettingsAction(action: SettingsAction): String? = repository.runSettingsAction(action)

    suspend fun licenseStatus(): Pair<Boolean, String?> = repository.licenseStatus()

    // Also updates the watermark's own flag, so installing a licence clears it straight away
    // rather than at the next full refresh.
    suspend fun installLicense(contents: String): Pair<Boolean, String?> {
        val result = repository.installLicense(contents)
        _state.update { it.copy(isLicensed = result.first) }
        return result
    }

    suspend fun promptProfiles(): org.json.JSONObject = repository.promptProfiles()

    suspend fun savePromptProfiles(profiles: org.json.JSONObject) = repository.savePromptProfiles(profiles)

    suspend fun exportBackup(): String = repository.exportBackup()

    // Refreshes afterwards: a restore changes cameras, accounts and settings underneath the
    // running app, so what's on screen is stale the moment it succeeds.
    suspend fun importBackup(contents: String): BackupRestoreSummary {
        val restored = repository.importBackup(contents)
        refreshAll()
        return restored
    }

    suspend fun homeKitStatus(): HomeKitStatus = repository.homeKitStatus()

    suspend fun homeKitSettings(): HomeKitSettings = repository.homeKitSettings()

    suspend fun saveHomeKitSettings(settings: HomeKitSettings) = repository.saveHomeKitSettings(settings)

    suspend fun resetHomeKitPairing() = repository.resetHomeKitPairing()

    suspend fun knownVehicles(): List<KnownVehicle> = repository.knownVehicles()

    suspend fun saveKnownVehicles(vehicles: List<KnownVehicle>) = repository.saveKnownVehicles(vehicles)

    // ---------------- Files library ----------------
    //
    // Screen-owned data, like Known Vehicles: the screens under ui/files load what they show and
    // these are plain passthroughs. Uploads are the exception, below - they outlive the screen.

    suspend fun familyFilesStatus(): FamilyFilesStatus = repository.familyFilesStatus()

    suspend fun familyFilesJob(): FamilyJob? = repository.familyFilesJob()

    suspend fun familyFilesList(path: String): FamilyFolderListing = repository.familyFilesList(path)

    suspend fun familyFilesSearch(query: String): List<FamilyFileEntry> = repository.familyFilesSearch(query)

    suspend fun familyFilesDownload(path: String, destination: File) = repository.familyFilesDownload(path, destination)

    suspend fun familyFilesThumbnail(path: String): Bitmap? = repository.familyFilesThumbnail(path)

    suspend fun familyFilesCreateFolder(path: String): String = repository.familyFilesCreateFolder(path)

    suspend fun familyFilesMove(from: String, to: String): String = repository.familyFilesMove(from, to)

    suspend fun familyFilesDelete(path: String): FamilyDeleteResult = repository.familyFilesDelete(path)

    suspend fun familyFilesTrash(): List<FamilyTrashBatch> = repository.familyFilesTrash()

    suspend fun familyFilesRestoreTrash(batchId: String, path: String? = null): FamilyRestoreResult =
        repository.familyFilesRestoreTrash(batchId, path)

    suspend fun familyFilesEmptyTrash(): String = repository.familyFilesEmptyTrash()

    suspend fun familyDrives(): List<FamilyDrive> = repository.familyDrives()

    suspend fun saveFamilyDrive(id: String?, input: FamilyDriveInput): FamilyDrive = repository.saveFamilyDrive(id, input)

    suspend fun deleteFamilyDrive(id: String): String = repository.deleteFamilyDrive(id)

    suspend fun testFamilyDrive(input: FamilyDriveInput): FamilyDriveTestResult = repository.testFamilyDrive(input)

    suspend fun syncFamilyDrive(id: String): String = repository.syncFamilyDrive(id)

    suspend fun restoreFamilyDrive(id: String): String = repository.restoreFamilyDrive(id)

    suspend fun syncAllFamilyDrives(): String = repository.syncAllFamilyDrives()

    suspend fun checkFamilyDuplicates(remove: Boolean): String = repository.checkFamilyDuplicates(remove)

    suspend fun familyDuplicatesReport(): FamilyDuplicateReport? = repository.familyDuplicatesReport()

    suspend fun runFamilyNightly(): String = repository.runFamilyNightly()

    // The upload queue lives here rather than in the Files screen so that switching tabs
    // mid-upload doesn't cancel a half-sent video. One batch at a time; a second pick while one
    // is running waits its turn. The screen shows `familyUpload` as its banner and reloads the
    // folder whenever `finished` ticks.
    private val _familyUpload = MutableStateFlow(FamilyUploadState())
    val familyUpload: StateFlow<FamilyUploadState> = _familyUpload.asStateFlow()
    private val familyUploadLock = Mutex()

    fun uploadFamilyFiles(uris: List<Uri>, folderPath: String) {
        if (uris.isEmpty()) return
        viewModelScope.launch {
            familyUploadLock.withLock {
                val resolver = getApplication<Application>().contentResolver
                val failures = mutableListOf<String>()
                var alreadyThere = 0
                uris.forEachIndexed { index, uri ->
                    val name = FamilyUploadNaming.displayName(resolver, uri)
                    _familyUpload.update {
                        it.copy(current = FamilyUploadProgress(index + 1, uris.size, name, 0L, null))
                    }
                    runCatching {
                        repository.familyFilesUpload(uri, folderPath, name, FamilyUploadNaming.modified(resolver, uri)) { sent, total ->
                            _familyUpload.update { state ->
                                state.copy(current = state.current?.copy(sentBytes = sent, totalBytes = total))
                            }
                        }
                    }.onSuccess { if (it.alreadyThere) alreadyThere++ }
                        .onFailure { error ->
                            if (error is CancellationException) throw error
                            failures.add("$name: ${readableError(error)}")
                            log("Files upload of $name failed: ${readableError(error)}", DiagnosticLevel.NOTICE)
                        }
                }
                _familyUpload.update {
                    it.copy(
                        current = null,
                        finished = it.finished + 1,
                        lastUploaded = uris.size - failures.size,
                        lastAlreadyThere = alreadyThere,
                        lastFailures = failures,
                    )
                }
            }
        }
    }

    // Both of these change whether the whole app needs a login, so each re-reads the auth status
    // rather than assuming it landed - the sign-in gate reads the same field.
    suspend fun enableDashboardLogin(username: String, password: String) {
        repository.enableDashboardLogin(username, password)
        refreshAuthStatus()
    }

    suspend fun disableDashboardLogin() {
        repository.disableDashboardLogin()
        refreshAuthStatus()
    }

    suspend fun refreshAuthStatus() {
        val auth = runCatching { repository.authStatus() }.getOrNull()
        _state.update { it.copy(authStatus = auth) }
    }

    suspend fun restartServer(service: String) = repository.restartServer(service)

    suspend fun hostRebootProbe() = repository.hostRebootProbe()

    suspend fun rebootHost() = repository.rebootHost()

    suspend fun cancelHostReboot() = repository.cancelHostReboot()

    suspend fun startDeviceScan(): DeviceScanStartResult = repository.startDeviceScan()

    suspend fun deviceScanStatus(scanId: String): DeviceScanSnapshot = repository.deviceScanStatus(scanId)

    suspend fun addDiscoveredDevice(candidate: DeviceCandidate): HubDevice =
        createDevice(HubDeviceInput.from(candidate))

    // Handed from the discovery screen to the edit screen. Kept here rather than serialised into
    // the navigation route because a command topic contains slashes, which would break the path.
    private var stagedDevice: HubDeviceInput? = null

    fun stageDiscoveredDevice(candidate: DeviceCandidate) {
        stagedDevice = HubDeviceInput.from(candidate)
    }

    fun stagedDiscoveredDevice(): HubDeviceInput? = stagedDevice

    fun device(id: String): HubDevice? = _state.value.devices.firstOrNull { it.id == id }

    suspend fun createCamera(input: CameraInput): Camera {
        val created = repository.createCamera(input)
        _state.update { it.copy(cameras = it.cameras + created) }
        return created
    }

    suspend fun updateCamera(id: String, input: CameraInput) {
        repository.updateCamera(id, input)
        _state.update { current ->
            current.copy(
                cameras = current.cameras.map {
                    if (it.id == id) {
                        it.copy(
                            name = input.name,
                            streamUrl = input.streamUrl,
                            includeAudio = input.includeAudio,
                            ptzEnabled = input.ptzEnabled,
                            onvifPtzServiceUrl = input.onvifPtzServiceUrl,
                            onvifProfileToken = input.onvifProfileToken,
                            username = input.username,
                            // Back to the mask, never the revealed value: this list is what the
                            // next edit loads from, and it should hold no more than the server
                            // just sent.
                            password = if (input.password.isNullOrEmpty()) null else SECRET_MASK,
                        )
                    } else {
                        it
                    }
                },
            )
        }
    }

    // Continuous-move semantics: the camera keeps moving at this velocity until stopPtz is
    // called - there's no "move by this much and stop automatically" in this mode. Fire-and-
    // forget by design (see the joystick UI, which swallows these exceptions) - a dropped PTZ
    // command just means the camera didn't move that instant, not something worth an error over.
    // PTZ commands run one at a time, in the order they were issued, and from the ViewModel's
    // scope rather than a composable's.
    //
    // Both properties matter and neither was true before. Ordering: a press and a release used to
    // launch independent coroutines, so on a quick tap the two HTTP requests raced, and whenever
    // the stop won the camera was sent stop-then-move and kept turning - the intermittent
    // "sticks and spins" this was reported for. Scope: the release used to be launched from
    // rememberCoroutineScope, which is cancelled when the composable leaves, so navigating away
    // mid-press could cancel the very stop that press depended on.
    private val ptzCommands = PtzCommandQueue(viewModelScope)

    private fun enqueuePtz(block: suspend () -> Unit) = ptzCommands.enqueue(block)

    fun startPtzMove(cameraId: String, pan: Double, tilt: Double, zoom: Double) =
        enqueuePtz { repository.ptzMove(cameraId, pan, tilt, zoom) }

    fun endPtzMove(cameraId: String) = enqueuePtz { stopPtzWithRetry(cameraId) }

    fun goPtzHome(cameraId: String) = enqueuePtz { repository.ptzHome(cameraId) }

    // A failed stop is retried; a failed move is not. Every other PTZ command is fire-and-forget
    // because a command that didn't land shows up as a camera that didn't move, which the video
    // makes obvious. A stop is the exception: when it doesn't land the camera keeps going, and
    // nothing about watching the picture fixes that.
    private suspend fun stopPtzWithRetry(cameraId: String) {
        repeat(PTZ_STOP_ATTEMPTS) { attempt ->
            if (runCatching { repository.ptzStop(cameraId) }.isSuccess) return
            // Short and doubling - the whole sequence is under a second, because a camera that is
            // still turning is not something to be patient about.
            if (attempt < PTZ_STOP_ATTEMPTS - 1) delay(PTZ_STOP_BACKOFF_MS shl attempt)
        }
    }

    // Optimistic remove with rollback on failure, rather than a full refetch.
    suspend fun deleteCamera(camera: Camera) {
        val previous = _state.value.cameras
        _state.update { it.copy(cameras = it.cameras.filterNot { existing -> existing.id == camera.id }) }
        try {
            repository.deleteCamera(camera.id)
        } catch (error: Exception) {
            _state.update { it.copy(cameras = previous) }
            throw error
        }
    }

    suspend fun cameraPassword(id: String): String = repository.cameraPassword(id)

    suspend fun probeCamera(request: CameraProbeRequest): CameraProbeResult =
        repository.probeCamera(request)

    suspend fun refreshKnownFaces() {
        val value = repository.knownFaces()
        _state.update { it.copy(knownFaces = value) }
    }

    suspend fun createKnownFace(label: String, imageBytes: ByteArray): KnownFace {
        val created = repository.createKnownFace(label, imageBytes)
        _state.update { it.copy(knownFaces = it.knownFaces + created) }
        return created
    }

    suspend fun addKnownFaceEnrollment(faceId: String, imageBytes: ByteArray): KnownFace {
        val updated = repository.addKnownFaceEnrollment(faceId, imageBytes)
        _state.update { current ->
            current.copy(knownFaces = current.knownFaces.map { if (it.id == faceId) updated else it })
        }
        return updated
    }

    suspend fun deleteKnownFaceEnrollment(face: KnownFace, enrollmentId: String) {
        val previous = _state.value.knownFaces
        _state.update { current ->
            current.copy(
                knownFaces = current.knownFaces.map {
                    if (it.id == face.id) it.copy(enrollments = it.enrollments.filterNot { e -> e.id == enrollmentId }) else it
                },
            )
        }
        try {
            repository.deleteKnownFaceEnrollment(face.id, enrollmentId)
        } catch (error: Exception) {
            _state.update { it.copy(knownFaces = previous) }
            throw error
        }
    }

    // Optimistic remove with rollback on failure, rather than a full refetch.
    suspend fun deleteKnownFace(face: KnownFace) {
        val previous = _state.value.knownFaces
        _state.update { it.copy(knownFaces = it.knownFaces.filterNot { existing -> existing.id == face.id }) }
        try {
            repository.deleteKnownFace(face.id)
        } catch (error: Exception) {
            _state.update { it.copy(knownFaces = previous) }
            throw error
        }
    }

    suspend fun refreshAccounts() {
        val value = repository.accounts()
        _state.update { it.copy(accounts = value) }
    }

    suspend fun createAccount(username: String, password: String, role: String, displayName: String?): Account {
        val created = repository.createAccount(username, password, role, displayName)
        _state.update { it.copy(accounts = it.accounts + created) }
        return created
    }

    suspend fun updateAccountRole(account: Account, role: String) {
        repository.updateAccountRole(account.id, role)
        _state.update { current ->
            current.copy(accounts = current.accounts.map { if (it.id == account.id) it.copy(role = role) else it })
        }
    }

    suspend fun resetAccountPassword(account: Account, newPassword: String) {
        repository.resetAccountPassword(account.id, newPassword)
    }

    // Optimistic remove with rollback on failure, rather than a full refetch - same pattern as
    // deleteCamera above. A failure here is expected and common (server refuses to delete the
    // last remaining admin account), so the rollback matters more than it does for cameras.
    suspend fun deleteAccount(account: Account) {
        val previous = _state.value.accounts
        _state.update { it.copy(accounts = it.accounts.filterNot { existing -> existing.id == account.id }) }
        try {
            repository.deleteAccount(account.id)
        } catch (error: Exception) {
            _state.update { it.copy(accounts = previous) }
            throw error
        }
    }

    suspend fun changeMyPassword(currentPassword: String, newPassword: String) {
        repository.changeMyPassword(currentPassword, newPassword)
    }

    suspend fun fetchScanInterfaces(): List<ScanInterface> = repository.fetchScanInterfaces()

    suspend fun startScan(interfaceAddresses: List<String>): String =
        repository.startScan(interfaceAddresses)

    suspend fun fetchScanStatus(scanId: String): ScanJobSnapshot = repository.fetchScanStatus(scanId)

    suspend fun fetchTriggerConfig(cameraId: String): TriggerConfig =
        repository.fetchTriggerConfig(cameraId)

    suspend fun saveTriggerConfig(cameraId: String, config: TriggerConfig): TriggerConfig =
        repository.saveTriggerConfig(cameraId, config)

    // Live ffmpeg snapshot, never cached - fetched fresh every time the zone editor opens.
    suspend fun fetchReferenceFrame(cameraId: String): ByteArray =
        repository.fetchReferenceFrame(cameraId)

    suspend fun fetchPromptProfileNames(): List<PromptProfileName> =
        repository.fetchPromptProfileNames()

    fun loadTimeline(camera: String, date: LocalDate) {
        if (camera.isBlank()) return
        timelineJob?.cancel()
        timelineJob = viewModelScope.launch {
            _state.update {
                it.copy(timelineDay = null, timelineLoading = true, timelineError = null)
            }
            try {
                val value = repository.timeline(camera, date.toString())
                _state.update {
                    it.copy(timelineDay = value, timelineLoading = false, timelineError = null)
                }
            } catch (cancellation: CancellationException) {
                // The cancel() above can't interrupt the blocking socket read inside the
                // repository, so a superseded request keeps running and only observes its own
                // cancellation once the server answers - by which point the request that replaced
                // it may already have stored its day. Reporting that as a failure would blank the
                // timeline the user is looking at, which is how switching camera or date used to
                // leave the screen stuck on an empty day.
                throw cancellation
            } catch (error: Exception) {
                val message = readableError(error)
                _state.update {
                    it.copy(timelineDay = null, timelineLoading = false, timelineError = message)
                }
                log("Timeline request failed: $message", DiagnosticLevel.ERROR)
            }
        }
    }

    fun loadTimelineCameras() {
        viewModelScope.launch {
            runCatching { repository.timelineCameras() }
                .onSuccess { value -> _state.update { it.copy(timelineCameras = value) } }
                .onFailure { error ->
                    log("Timeline camera list failed: ${readableError(error)}", DiagnosticLevel.NOTICE)
                }
        }
    }

    fun setTimelineEventReviewed(eventId: String, reviewed: Boolean) {
        viewModelScope.launch {
            try {
                repository.setReviewed(eventId, reviewed)
                _state.update { current ->
                    current.copy(
                        timelineDay = current.timelineDay?.copy(
                            events = current.timelineDay.events.map {
                                if (it.id == eventId) it.copy(reviewed = reviewed) else it
                            },
                        ),
                    )
                }
            } catch (error: Exception) {
                _state.update { it.copy(timelineError = readableError(error)) }
            }
        }
    }

    fun clearDiagnostics() {
        _state.update { it.copy(diagnostics = emptyList()) }
    }

    fun refreshNotificationPermission() {
        val application = getApplication<Application>()
        val granted = Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU ||
            ContextCompat.checkSelfPermission(
                application,
                Manifest.permission.POST_NOTIFICATIONS,
            ) == PackageManager.PERMISSION_GRANTED
        _state.update {
            it.copy(
                pushPermission = when {
                    granted -> "ENABLED"
                    Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU -> "NOT ALLOWED"
                    else -> "ENABLED"
                },
            )
        }
    }

    fun enablePushNotifications() {
        refreshNotificationPermission()
        if (_state.value.pushPermission != "ENABLED") {
            _state.update {
                it.copy(pushMessage = "Notification permission was not granted.")
            }
            return
        }

        val application = getApplication<Application>()
        if (FirebaseApp.getApps(application).isEmpty()) {
            _state.update {
                it.copy(
                    pushMessage = "Firebase is not configured. Add app/google-services.json and rebuild the app.",
                )
            }
            return
        }

        _state.update {
            it.copy(
                pushRegistrationEnabled = true,
                pushMessage = "Registering this app installation with Firebase…",
            )
        }
        preferences.edit().putBoolean(PUSH_ENABLED_KEY, true).apply()
        val messaging = FirebaseMessaging.getInstance()
        messaging.isAutoInitEnabled = true
        messaging.register().addOnCompleteListener { task ->
            if (!task.isSuccessful) {
                val message = task.exception?.message ?: "Firebase registration failed."
                preferences.edit().putBoolean(PUSH_ENABLED_KEY, false).apply()
                _state.update {
                    it.copy(pushRegistrationEnabled = false, pushMessage = message)
                }
                log("Firebase registration failed: $message", DiagnosticLevel.ERROR)
                return@addOnCompleteListener
            }
        }
    }

    fun disablePushNotifications() {
        viewModelScope.launch {
            val application = getApplication<Application>()
            val installationId = PushTokenStore.get(application)
            if (!installationId.isNullOrBlank() &&
                _state.value.connectionState == ConnectionState.ONLINE &&
                !_state.value.requiresLogin) {
                runCatching { repository.unregisterPushDevice(installationId) }
                    .onFailure {
                        log(
                            "Could not unregister this device from Core: ${readableError(it)}",
                            DiagnosticLevel.NOTICE,
                        )
                    }
            }
            if (FirebaseApp.getApps(application).isNotEmpty()) {
                FirebaseMessaging.getInstance().apply {
                    isAutoInitEnabled = false
                    unregister()
                }
            }
            PushTokenStore.clear(application)
            preferences.edit().putBoolean(PUSH_ENABLED_KEY, false).apply()
            _state.update {
                it.copy(
                    pushRegistrationEnabled = false,
                    pushMessage = "Push notifications are disabled for this installation.",
                )
            }
            refreshPushStatus()
        }
    }

    fun sendTestPushNotification() {
        viewModelScope.launch {
            try {
                val report = repository.testPushNotification()
                _state.update {
                    it.copy(
                        pushMessage = if (report.delivered > 0) {
                            "Test delivered to ${report.delivered} device(s)."
                        } else {
                            "Core attempted ${report.attempted} delivery(s), but none succeeded."
                        },
                    )
                }
            } catch (error: Exception) {
                val message = readableError(error)
                _state.update { it.copy(pushMessage = message) }
                log("Test push failed: $message", DiagnosticLevel.ERROR)
            }
        }
    }

    fun consumePushEvent() {
        PushNavigation.consumed()
        _state.update { it.copy(pendingPushEventId = null) }
    }

    private suspend fun refreshPushStatus() {
        runCatching { repository.pushStatus() }
            .onSuccess { status -> _state.update { it.copy(pushStatus = status) } }
    }

    // Public (unlike refreshPushStatus above) - called directly from SettingsScreen's
    // LaunchedEffect since there's no other flow that already touches system status.
    fun refreshSystemStatus() {
        viewModelScope.launch {
            runCatching { repository.systemStatus() }
                .onSuccess { status -> _state.update { it.copy(systemStatus = status) } }
        }
    }

    private suspend fun syncPushRegistration(
        token: String? = PushTokenStore.get(getApplication()),
    ) {
        if (token.isNullOrBlank() ||
            !_state.value.pushRegistrationEnabled ||
            _state.value.connectionState != ConnectionState.ONLINE ||
            _state.value.requiresLogin) {
            return
        }
        try {
            repository.registerPushDevice(token, Build.MODEL.ifBlank { "Android device" })
            refreshPushStatus()
            _state.update {
                it.copy(pushMessage = "This device is registered with Sentinel Core.")
            }
            log("Registered this Android device for push notifications", DiagnosticLevel.INFO)
        } catch (error: Exception) {
            val message = readableError(error)
            _state.update { it.copy(pushMessage = message) }
            log("Push token registration failed: $message", DiagnosticLevel.ERROR)
        }
    }

    private suspend fun openPushEvent(eventId: String) {
        _state.update { it.copy(pendingPushEventId = eventId) }
        resolvePendingPushEvent()
    }

    private suspend fun resolvePendingPushEvent() {
        val eventId = _state.value.pendingPushEventId ?: return
        if (_state.value.events.any { it.id == eventId }) return
        if (_state.value.connectionState != ConnectionState.ONLINE || _state.value.requiresLogin) return

        runCatching { repository.event(eventId) }
            .onSuccess { event -> _state.update { it.copy(pushedEvent = event) } }
            .onFailure { error ->
                log("Could not open pushed event: ${readableError(error)}", DiagnosticLevel.NOTICE)
            }
    }

    private fun log(message: String, level: DiagnosticLevel) {
        val entry = DiagnosticEntry(
            id = logSequence.incrementAndGet(),
            timestamp = Instant.now(),
            level = level,
            message = message,
        )
        _state.update { current ->
            current.copy(diagnostics = (listOf(entry) + current.diagnostics).take(100))
        }
        android.util.Log.d("SentinelCore", "[$level] $message")
    }

    private fun readableError(error: Throwable): String =
        error.message?.takeIf(String::isNotBlank) ?: "Could not connect to Sentinel Core."

    companion object {
        private const val PTZ_STOP_ATTEMPTS = 3
        private const val PTZ_STOP_BACKOFF_MS = 150L

        const val IMPOSTOR_MESSAGE =
            "Something at this address isn't your Sentinel Core server - its certificate wasn't " +
                "issued by your server. Nothing was sent to it. If you're away from home, that's " +
                "expected: use the remote address. If you reinstalled the server, save its address " +
                "again in Settings."

        // Read by widgets/OrganizerBackgroundSync too, which syncs with no ViewModel around.
        const val SERVER_URL_KEY = "server_url"
        const val REMOTE_SERVER_URL_KEY = "remote_server_url"
        private const val PUSH_ENABLED_KEY = "push_registration_enabled"
        private const val MOVIES_ENABLED_KEY = "movies_enabled_last_known"
        private const val WEATHER_ENABLED_KEY = "weather_enabled_last_known"
        private const val ADVANCED_SETTINGS_KEY = "settings_show_advanced"
    }
}
