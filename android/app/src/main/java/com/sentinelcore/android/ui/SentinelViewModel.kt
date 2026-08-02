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
import com.sentinelcore.android.data.Account
import com.sentinelcore.android.data.AuthStatus
import com.sentinelcore.android.data.Camera
import com.sentinelcore.android.data.CameraInput
import com.sentinelcore.android.data.CameraProbeRequest
import com.sentinelcore.android.data.CameraProbeResult
import com.sentinelcore.android.data.ConnectionState
import com.sentinelcore.android.data.DeviceCandidate
import com.sentinelcore.android.data.DeviceScanSnapshot
import com.sentinelcore.android.data.DeviceScanStartResult
import com.sentinelcore.android.data.DiagnosticEntry
import com.sentinelcore.android.data.DiagnosticLevel
import com.sentinelcore.android.data.HubDevice
import com.sentinelcore.android.data.HubDeviceInput
import com.sentinelcore.android.data.KnownFace
import com.sentinelcore.android.data.LiveCameraOrder
import com.sentinelcore.android.data.SettingsAction
import com.sentinelcore.android.data.SettingsSection
import com.sentinelcore.android.data.PromptProfileName
import com.sentinelcore.android.data.ScanInterface
import com.sentinelcore.android.data.ScanJobSnapshot
import com.sentinelcore.android.data.SecurityEvent
import com.sentinelcore.android.data.SentinelApiException
import com.sentinelcore.android.data.SentinelRepository
import com.sentinelcore.android.data.ServerCompatibility
import com.sentinelcore.android.data.TriggerConfig
import com.sentinelcore.android.data.TriggerEvent
import com.sentinelcore.android.data.PushStatus
import com.sentinelcore.android.data.SystemStatus
import com.sentinelcore.android.data.RecordingTimelineDay
import com.sentinelcore.android.net.DiscoveredServer
import com.sentinelcore.android.net.ServerDiscovery
import com.sentinelcore.android.push.PushNavigation
import com.sentinelcore.android.push.PushTokenStore
import com.sentinelcore.android.push.PushTokenUpdates
import kotlinx.coroutines.CancellationException
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
    // Optional Cloudflare Tunnel (or any other externally-reachable) address, used only when
    // the local address can't be reached - see SentinelViewModel.connect() for the resolution.
    val remoteServerUrl: String = "",
    val activeServerUrl: String = "",
    val isUsingRemote: Boolean = false,
    val connectionState: ConnectionState = ConnectionState.NOT_CONFIGURED,
    // Whether the server this app is talking to is older than it expects. Advisory: nothing in the
    // app behaves differently, it just says so - see ServerCompatibility.
    val serverCompatibility: ServerCompatibility = ServerCompatibility.Unknown,
    val authStatus: AuthStatus? = null,
    val events: List<SecurityEvent> = emptyList(),
    val triggers: List<TriggerEvent> = emptyList(),
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
    private val repository = SentinelRepository(::log)
    private val serverDiscovery = ServerDiscovery(application)
    private val _state = MutableStateFlow(
        SentinelUiState(
            serverUrl = preferences.getString(SERVER_URL_KEY, "").orEmpty(),
            remoteServerUrl = preferences.getString(REMOTE_SERVER_URL_KEY, "").orEmpty(),
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
        if (!current.hasRemoteServerUrl) {
            repository.baseUrl = current.serverUrl
            _state.update { it.copy(activeServerUrl = current.serverUrl, isUsingRemote = false) }
            return
        }
        if (repository.probeReachable(current.serverUrl)) {
            repository.baseUrl = current.serverUrl
            _state.update { it.copy(activeServerUrl = current.serverUrl, isUsingRemote = false) }
        } else {
            log("Local address unreachable; using remote address", DiagnosticLevel.INFO)
            repository.baseUrl = current.remoteServerUrl
            _state.update { it.copy(activeServerUrl = current.remoteServerUrl, isUsingRemote = true) }
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
                // Same treatment, opposite default - see SentinelUiState.isLicensed.
                viewModelScope.async {
                    runCatching { repository.licenseValid() }
                        .onSuccess { value -> _state.update { it.copy(isLicensed = value) } }
                        .onFailure { _state.update { it.copy(isLicensed = true) } }
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

    suspend fun settingsSchema(): List<SettingsSection> = repository.settingsSchema()

    suspend fun settingsSnapshot(): org.json.JSONObject = repository.settingsSnapshot()

    suspend fun saveSettingsSection(endpoint: String, body: org.json.JSONObject): Boolean =
        repository.saveSettingsSection(endpoint, body)

    suspend fun runSettingsAction(action: SettingsAction) = repository.runSettingsAction(action)

    suspend fun restartServer(service: String) = repository.restartServer(service)

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
    suspend fun movePtz(cameraId: String, pan: Double, tilt: Double, zoom: Double) =
        repository.ptzMove(cameraId, pan, tilt, zoom)

    suspend fun stopPtz(cameraId: String) = repository.ptzStop(cameraId)

    suspend fun homePtz(cameraId: String) = repository.ptzHome(cameraId)

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
        private const val REMOTE_SERVER_URL_KEY = "remote_server_url"
        private const val PUSH_ENABLED_KEY = "push_registration_enabled"
    }
}
