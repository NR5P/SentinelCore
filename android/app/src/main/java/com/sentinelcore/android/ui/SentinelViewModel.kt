package com.sentinelcore.android.ui

import android.app.Application
import android.graphics.Bitmap
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.sentinelcore.android.data.AuthStatus
import com.sentinelcore.android.data.Camera
import com.sentinelcore.android.data.ConnectionState
import com.sentinelcore.android.data.DiagnosticEntry
import com.sentinelcore.android.data.DiagnosticLevel
import com.sentinelcore.android.data.SecurityEvent
import com.sentinelcore.android.data.SentinelApiException
import com.sentinelcore.android.data.SentinelRepository
import com.sentinelcore.android.data.TriggerEvent
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
) {
    val isConfigured: Boolean
        get() = runCatching {
            val uri = URI(serverUrl)
            uri.scheme in setOf("http", "https") && !uri.host.isNullOrBlank()
        }.getOrDefault(false)

    val requiresLogin: Boolean
        get() = authStatus?.let { it.enabled && !it.authenticated } == true
}

class SentinelViewModel(application: Application) : AndroidViewModel(application) {
    private val preferences =
        application.getSharedPreferences("sentinel_core", Application.MODE_PRIVATE)
    private val logSequence = AtomicLong()
    private val repository = SentinelRepository(::log)
    private val _state = MutableStateFlow(
        SentinelUiState(
            serverUrl = preferences.getString(SERVER_URL_KEY, "").orEmpty(),
        ),
    )
    val state: StateFlow<SentinelUiState> = _state.asStateFlow()

    private var connectionJob: Job? = null

    init {
        repository.baseUrl = _state.value.serverUrl
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

    fun clearDiagnostics() {
        _state.update { it.copy(diagnostics = emptyList()) }
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
    }
}
