package com.sentinelcore.android.ui

import android.Manifest
import android.app.Application
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.os.Build
import androidx.core.content.ContextCompat
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.google.firebase.FirebaseApp
import com.google.firebase.messaging.FirebaseMessaging
import com.sentinelcore.android.data.AuthStatus
import com.sentinelcore.android.data.Camera
import com.sentinelcore.android.data.ConnectionState
import com.sentinelcore.android.data.DiagnosticEntry
import com.sentinelcore.android.data.DiagnosticLevel
import com.sentinelcore.android.data.SecurityEvent
import com.sentinelcore.android.data.SentinelApiException
import com.sentinelcore.android.data.SentinelRepository
import com.sentinelcore.android.data.TriggerEvent
import com.sentinelcore.android.data.PushStatus
import com.sentinelcore.android.data.RecordingTimelineDay
import com.sentinelcore.android.push.PushNavigation
import com.sentinelcore.android.push.PushTokenStore
import com.sentinelcore.android.push.PushTokenUpdates
import kotlinx.coroutines.Job
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.net.URI
import java.time.Instant
import java.time.LocalDate
import java.util.concurrent.atomic.AtomicLong

data class SentinelUiState(
    val serverUrl: String = "",
    val connectionState: ConnectionState = ConnectionState.NOT_CONFIGURED,
    val authStatus: AuthStatus? = null,
    val events: List<SecurityEvent> = emptyList(),
    val triggers: List<TriggerEvent> = emptyList(),
    val cameras: List<Camera> = emptyList(),
    val isLoading: Boolean = false,
    val lastError: String? = null,
    val diagnostics: List<DiagnosticEntry> = emptyList(),
    val lastUpdated: Instant? = null,
    val pushStatus: PushStatus? = null,
    val pushPermission: String = "NOT REQUESTED",
    val pushRegistrationEnabled: Boolean = false,
    val pushMessage: String? = null,
    val pendingPushEventId: String? = null,
    val timelineDay: RecordingTimelineDay? = null,
    val timelineLoading: Boolean = false,
    val timelineError: String? = null,
    val timelineCameras: List<String> = emptyList(),
) {
    val isConfigured: Boolean
        get() = runCatching {
            val uri = URI(serverUrl)
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
    private val repository = SentinelRepository(::log)
    private val _state = MutableStateFlow(
        SentinelUiState(
            serverUrl = preferences.getString(SERVER_URL_KEY, "").orEmpty(),
            pushRegistrationEnabled = preferences.getBoolean(PUSH_ENABLED_KEY, false),
        ),
    )
    val state: StateFlow<SentinelUiState> = _state.asStateFlow()

    private var connectionJob: Job? = null
    private var timelineJob: Job? = null

    init {
        repository.baseUrl = _state.value.serverUrl
        refreshNotificationPermission()
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

            try {
                val auth = repository.authStatus()
                _state.update { it.copy(authStatus = auth) }
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
                    "Connection established; loaded ${_state.value.events.size} events",
                    DiagnosticLevel.INFO,
                )
                refreshPushStatus()
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
                        lastError = readableError(error),
                    )
                }
                log("Connection failed: ${readableError(error)}", DiagnosticLevel.ERROR)
            }
        }
    }

    suspend fun refreshAll() {
        _state.update { it.copy(isLoading = true) }
        try {
            val currentErrors = mutableListOf<Throwable>()
            val requests = listOf(
                viewModelScope.async {
                    runCatching { repository.events() }
                        .onSuccess { value -> _state.update { it.copy(events = value) } }
                        .onFailure(currentErrors::add)
                },
                viewModelScope.async {
                    runCatching { repository.triggers() }
                        .onSuccess { value -> _state.update { it.copy(triggers = value) } }
                        .onFailure(currentErrors::add)
                },
                viewModelScope.async {
                    runCatching { repository.cameras() }
                        .onSuccess { value -> _state.update { it.copy(cameras = value) } }
                        .onFailure(currentErrors::add)
                },
            )
            requests.awaitAll()

            val firstError = currentErrors.firstOrNull()
            _state.update {
                it.copy(
                    lastError = firstError?.let(::readableError),
                    lastUpdated = Instant.now(),
                )
            }
            if (firstError != null && _state.value.events.isEmpty()) throw firstError
            resolvePendingPushEvent()
        } finally {
            _state.update { it.copy(isLoading = false) }
        }
    }

    fun saveServer(address: String) {
        val normalized = SentinelRepository.normalizeAddress(address)
        connectionJob?.cancel()
        repository.clearAuthenticationSession()
        repository.baseUrl = normalized
        preferences.edit().putString(SERVER_URL_KEY, normalized).apply()
        _state.update {
            SentinelUiState(
                serverUrl = normalized,
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
            .onSuccess { event ->
                _state.update { current ->
                    current.copy(events = listOf(event) + current.events.filterNot { it.id == event.id })
                }
            }
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
        private const val SERVER_URL_KEY = "server_url"
        private const val PUSH_ENABLED_KEY = "push_registration_enabled"
    }
}
