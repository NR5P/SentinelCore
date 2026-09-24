package com.sentinelcore.android.publish

import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import androidx.core.content.ContextCompat
import com.sentinelcore.android.data.DiagnosticLevel
import com.sentinelcore.android.data.SentinelApiException
import com.sentinelcore.android.data.SentinelRepository
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withTimeoutOrNull
import org.webrtc.AudioTrack
import org.webrtc.Camera2Enumerator
import org.webrtc.CameraVideoCapturer
import org.webrtc.DataChannel
import org.webrtc.DefaultVideoDecoderFactory
import org.webrtc.EglBase
import org.webrtc.IceCandidate
import org.webrtc.MediaConstraints
import org.webrtc.MediaStream
import org.webrtc.PeerConnection
import org.webrtc.PeerConnectionFactory
import org.webrtc.RtpParameters
import org.webrtc.RtpSender
import org.webrtc.RtpTransceiver
import org.webrtc.SdpObserver
import org.webrtc.SessionDescription
import org.webrtc.SurfaceTextureHelper
import org.webrtc.SentinelH264EncoderFactory
import org.webrtc.VideoSource
import org.webrtc.VideoTrack
import java.io.IOException
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

// Turns the phone this app runs on into one of the dashboard's cameras: capture its camera and
// push the video to the server over WebRTC, exactly what the web dashboard's publish page
// (wwwroot/publisher.js) does from a browser. Same signaling endpoint, same session cookie, same
// go2rtc sidecar on the other end - so from the server's point of view a phone running this and a
// laptop running the browser page are the same kind of camera, and clips, trigger zones and AI
// analysis all work without knowing which it was.
//
// A process-wide object, not a ViewModel. Streaming has to outlive the screen that started it and
// the Activity the screen was in: an Android phone left in a room can turn its screen off and
// keep the camera running, because DevicePublisherService holds a foreground service of type
// camera (started while the app was in front, which is the rule that makes that legal) and a
// partial wake lock. That service is what keeps this object's process alive; this object is what
// does the work.
//
// The awkward part, as in the browser, is running unattended: reconnecting after a network blip
// or a doze, and telling the truth about whether the server is actually receiving frames, which
// a "connected" peer connection does not establish on its own.
object DevicePublisher {
    data class Target(val id: String, val name: String)

    // A camera on this phone, by Camera2 id - stable across launches, which is what lets the
    // remembered intent name the same camera next time.
    data class CaptureDevice(val id: String, val name: String, val isFront: Boolean)

    enum class Phase { IDLE, PREVIEWING, STARTING, CONNECTING, CONNECTED, STREAMING, RECONNECTING }

    data class State(
        val phase: Phase = Phase.IDLE,
        // The dashboard camera this phone streams into. Only the id and name are kept.
        val target: Target? = null,
        val deviceId: String = "",
        val includeMic: Boolean = false,
        val status: String = "",
        val error: String? = null,
        val devices: List<CaptureDevice> = emptyList(),
    ) {
        // "This phone is meant to be streaming", regardless of whether the connection is up at
        // this instant. What the Live badge, the notification and the auto-resume all key on.
        val isPublishing: Boolean get() = target != null
    }

    private val _state = MutableStateFlow(State())
    val state: StateFlow<State> = _state.asStateFlow()

    // Whatever the camera is currently producing - shown by the screen's preview, streaming or not.
    private val _previewTrack = MutableStateFlow<VideoTrack?>(null)
    val previewTrack: StateFlow<VideoTrack?> = _previewTrack.asStateFlow()

    // Set from the notification: "open the screen for this". Consumed by SentinelApp.
    val openScreenRequested = MutableStateFlow(false)

    private lateinit var appContext: Context
    private var repository: SentinelRepository? = null
    private var logger: (String, DiagnosticLevel) -> Unit = { _, _ -> }
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    // Shared with the preview renderer, so the frames it draws are the ones the encoder gets.
    val eglBase: EglBase by lazy { EglBase.create() }
    private var factory: PeerConnectionFactory? = null
    private var videoSource: VideoSource? = null
    private var videoTrack: VideoTrack? = null
    private var audioTrack: AudioTrack? = null
    private var capturer: CameraVideoCapturer? = null
    private var surfaceHelper: SurfaceTextureHelper? = null
    private var captureRunning = false
    private var peer: PeerConnection? = null

    private var statusJob: Job? = null
    private var retryJob: Job? = null
    private var heartbeatJob: Job? = null

    // Bumped every time a session starts or stops, so a callback from a session that has since
    // been torn down (a slow SDP exchange, a late retry) can tell it is stale and do nothing.
    private var generation = 0

    // Its own file, not the app's main "sentinel_core" one, because backup and device transfer
    // copy whole files and this one must not travel: restored onto a new phone, the intent below
    // made that phone start streaming into the old phone's camera slot after its first sign-in,
    // before it had ever been given camera permission. Excluded in data_extraction_rules.xml and
    // backup_rules.xml.
    private const val PREFS = "sentinel_core_publisher"
    private const val LEGACY_PREFS = "sentinel_core"
    private const val INTENT_ID = "publisher.target.id"
    private const val INTENT_NAME = "publisher.target.name"
    private const val INTENT_DEVICE = "publisher.device"
    private const val INTENT_MIC = "publisher.mic"
    // Which dashboard camera this phone last streamed to - kept across a stop, unlike the intent,
    // so the screen offers the same camera next time rather than creating another.
    private const val LAST_TARGET = "publisher.lastTarget"
    private const val STATUS_POLL_MS = 10_000L
    private const val RETRY_MS = 3_000L
    private const val HEARTBEAT_MS = 15_000L
    private const val SUSPEND_GAP_MS = 3 * 60 * 1000L

    // Called once by SentinelViewModel: the repository is where the session cookie lives, and the
    // context outlives any Activity.
    fun attach(context: Context, repository: SentinelRepository, logger: (String, DiagnosticLevel) -> Unit) {
        appContext = context.applicationContext
        this.repository = repository
        this.logger = logger
        moveLegacyPreferences()
    }

    // Builds before the move kept these keys in the main preferences file.
    private fun moveLegacyPreferences() {
        val keys = listOf(INTENT_ID, INTENT_NAME, INTENT_DEVICE, INTENT_MIC, LAST_TARGET)
        val legacy = appContext.getSharedPreferences(LEGACY_PREFS, Context.MODE_PRIVATE)
        if (keys.none(legacy::contains)) return
        val moved = appContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
        legacy.getString(INTENT_ID, null)?.let { moved.putString(INTENT_ID, it) }
        legacy.getString(INTENT_NAME, null)?.let { moved.putString(INTENT_NAME, it) }
        legacy.getString(INTENT_DEVICE, null)?.let { moved.putString(INTENT_DEVICE, it) }
        if (legacy.contains(INTENT_MIC)) moved.putBoolean(INTENT_MIC, legacy.getBoolean(INTENT_MIC, false))
        legacy.getString(LAST_TARGET, null)?.let { moved.putString(LAST_TARGET, it) }
        moved.apply()
        legacy.edit().apply { keys.forEach(::remove) }.apply()
    }

    val isAttached: Boolean get() = repository != null

    // ---------------- Devices ----------------

    fun refreshDevices() {
        val enumerator = Camera2Enumerator(appContext)
        var back = 0
        var front = 0
        val devices = enumerator.deviceNames.map { name ->
            val isFront = enumerator.isFrontFacing(name)
            val label = when {
                isFront -> "Front camera" + if (++front > 1) " $front" else ""
                enumerator.isBackFacing(name) -> "Back camera" + if (++back > 1) " $back" else ""
                else -> "Camera $name"
            }
            CaptureDevice(name, label, isFront)
        }
        _state.update { it.copy(devices = devices) }
    }

    // A phone left in a room is propped up with its screen facing the room's owner and its back
    // to the room, so the back camera is what "the camera" means here.
    fun defaultDeviceId(): String {
        val devices = _state.value.devices.ifEmpty { refreshDevices(); _state.value.devices }
        return (devices.firstOrNull { !it.isFront } ?: devices.firstOrNull())?.id.orEmpty()
    }

    // ---------------- Preview ----------------

    // Shows the chosen camera without sending it anywhere - what the screen renders while the
    // person decides which camera and whether to start. A page that shows nothing until "start"
    // reads as a page that does nothing.
    fun preview(deviceId: String) {
        if (_state.value.isPublishing) return
        scope.launch {
            _state.update { it.copy(error = null, status = "opening camera…") }
            try {
                ensureCapture(deviceId.ifEmpty { defaultDeviceId() })
                _state.update { it.copy(phase = Phase.PREVIEWING, status = "previewing - nothing is being sent yet") }
            } catch (error: Exception) {
                _state.update { it.copy(phase = Phase.IDLE, status = "", error = error.message ?: "Couldn't open the camera.") }
            }
        }
    }

    // Releases the camera when the screen goes away without streaming. While streaming the
    // capture is the stream and stays.
    fun endPreview() {
        if (_state.value.isPublishing) return
        stopCapture()
        _state.update { it.copy(phase = Phase.IDLE, status = "") }
    }

    // ---------------- Publishing ----------------

    fun start(target: Target, deviceId: String, includeMic: Boolean) {
        scope.launch { startNow(target, deviceId, includeMic) }
    }

    private suspend fun startNow(target: Target, deviceId: String, includeMic: Boolean) {
        retryJob?.cancel()
        statusJob?.cancel()
        closePeer()
        val mine = ++generation

        // Checked before the service exists, not left to ensureCapture: a camera-type foreground
        // service without the camera permission is refused by Android 14+ with an exception that
        // takes the whole app down - and because the remembered intent survived the crash, it did
        // so again on every launch. A permission the person has taken away is also a decision
        // only they can undo, so the intent goes too, rather than retrying on every reconnect.
        missingPermissionMessage(includeMic)?.let { message ->
            forgetIntent()
            _state.update {
                it.copy(target = null, phase = if (captureRunning) Phase.PREVIEWING else Phase.IDLE, status = "", error = message)
            }
            return
        }

        _state.update {
            it.copy(target = target, includeMic = includeMic, error = null, phase = Phase.STARTING, status = "requesting camera…")
        }

        // The service first, while the app is in front: Android only lets a foreground service
        // use the camera if it was started from the foreground, and this is the moment that holds.
        DevicePublisherService.start(appContext)

        try {
            ensureCapture(deviceId.ifEmpty { defaultDeviceId() })
            if (includeMic) ensureMicrophone() else audioTrack = null
        } catch (error: Exception) {
            if (mine != generation) return
            // No stopService here. A service started with startForegroundService that is stopped
            // before it reaches startForeground crashes the app, and this failure can easily come
            // first. Clearing the target is enough: the service calls startForeground, sees
            // nothing is publishing, and stops itself.
            _state.update {
                it.copy(target = null, phase = if (captureRunning) Phase.PREVIEWING else Phase.IDLE, status = "", error = error.message ?: "Couldn't open the camera.")
            }
            return
        }
        if (mine != generation) return

        rememberIntent()
        appContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putString(LAST_TARGET, target.id).apply()
        startHeartbeat()
        connect(mine)
    }

    val lastTargetId: String?
        get() = appContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(LAST_TARGET, null)

    // Ends streaming. A deliberate stop forgets the remembered intent, so the app stops trying to
    // be a camera on its next launch (the only other thing that does is a missing permission - see
    // startNow). The preview stays up so the screen still shows the camera, and endPreview
    // releases it when the screen goes.
    //
    // The service isn't stopped from here: it stops itself once nothing is publishing (see
    // DevicePublisherService), and stopping it directly could land before its startForeground -
    // a Stop tapped in the first moment of a start - which Android punishes by crashing the app.
    fun stop() {
        generation++
        forgetIntent()
        retryJob?.cancel()
        statusJob?.cancel()
        heartbeatJob?.cancel()
        closePeer()
        audioTrack?.dispose()
        audioTrack = null
        _state.update {
            it.copy(
                target = null,
                error = null,
                phase = if (captureRunning) Phase.PREVIEWING else Phase.IDLE,
                status = if (captureRunning) "stopped - previewing only, nothing is being sent" else "",
            )
        }
    }

    // Android wouldn't let DevicePublisherService go into the foreground - see its onStartCommand.
    fun serviceRefused() {
        stop()
        _state.update {
            it.copy(error = "Android didn't allow Sentinel Core to keep the camera running. Check its camera permission under Settings → Apps → Sentinel Core → Permissions, then try again.")
        }
    }

    private suspend fun connect(mine: Int) {
        val repository = repository ?: return
        val target = _state.value.target ?: return
        val factory = try { ensureFactory() } catch (error: Exception) { fail(error.message ?: "The video engine couldn't start.", mine); return }
        val track = videoTrack ?: return
        if (_state.value.phase != Phase.RECONNECTING) _state.update { it.copy(phase = Phase.CONNECTING, status = "connecting…") }

        // No STUN, like the browser page: the server is on the LAN and go2rtc answers with its
        // own host candidates.
        val configuration = PeerConnection.RTCConfiguration(emptyList()).apply {
            sdpSemantics = PeerConnection.SdpSemantics.UNIFIED_PLAN
        }
        val observer = object : PeerConnectionObserverAdapter() {
            override fun onConnectionChange(newState: PeerConnection.PeerConnectionState) {
                scope.launch { connectionStateChanged(newState, mine) }
            }
        }
        val peer = factory.createPeerConnection(configuration, observer)
        if (peer == null) {
            fail("Couldn't create a connection on this phone.", mine)
            return
        }
        this.peer = peer

        // A source, and only a source: saying so keeps the offer from asking for anything back.
        val videoSender = peer.addTrack(track, listOf("sentinelcore"))
        audioTrack?.let { peer.addTrack(it, listOf("sentinelcore")) }
        applyVideoBitrate(videoSender)
        peer.transceivers.forEach { it.direction = RtpTransceiver.RtpTransceiverDirection.SEND_ONLY }

        try {
            val offer = peer.createOfferSuspend()
            peer.setLocalSuspend(offer)
            waitForIceGathering(peer, 1_500L)
            if (mine != generation) return
            val local = peer.localDescription ?: throw IOException("No local description.")

            val answer = repository.publishOffer(target.id, local.description)
            if (mine != generation) return
            peer.setRemoteSuspend(SessionDescription(SessionDescription.Type.ANSWER, answer))
        } catch (error: Exception) {
            if (mine != generation) return
            // The camera is gone from the server - deleted on the dashboard, or this app was
            // pointed at a different server. Retrying would loop on 404 forever; the honest
            // answer is to stop and say so, and to forget the intent so the next launch doesn't
            // start the loop again.
            if (error is SentinelApiException && error.statusCode == 404) {
                val name = target.name
                stop()
                _state.update { it.copy(error = "\"$name\" no longer exists on this server. Pick another camera, or make a new one.") }
                return
            }
            fail("Couldn't start streaming: ${describe(error)}", mine)
            return
        }

        if (mine != generation) return
        _state.update { it.copy(phase = Phase.CONNECTED, status = "connected - waiting for the server to confirm it's receiving video…") }
        pollStatus(mine)
    }

    private fun connectionStateChanged(newState: PeerConnection.PeerConnectionState, mine: Int) {
        if (mine != generation || !_state.value.isPublishing) return
        when (newState) {
            PeerConnection.PeerConnectionState.FAILED,
            PeerConnection.PeerConnectionState.DISCONNECTED -> {
                _state.update { it.copy(phase = Phase.RECONNECTING, status = "connection lost - reconnecting…") }
                scheduleRetry(mine)
            }
            PeerConnection.PeerConnectionState.CONNECTED -> {
                _state.update {
                    if (it.phase == Phase.CONNECTING || it.phase == Phase.RECONNECTING) {
                        it.copy(phase = Phase.CONNECTED, status = "connected - waiting for the server to confirm it's receiving video…")
                    } else {
                        it
                    }
                }
            }
            else -> Unit
        }
    }

    private fun fail(message: String, mine: Int) {
        logger("Device camera: $message", DiagnosticLevel.ERROR)
        _state.update { it.copy(error = message, phase = Phase.RECONNECTING, status = "will retry in a few seconds…") }
        scheduleRetry(mine)
    }

    // A peer connection reporting "connected" only means the handshake completed. Asking the
    // server what it can actually see is the only honest confirmation, and it is what makes a
    // streaming phone trustworthy to walk away from.
    private fun pollStatus(mine: Int) {
        statusJob?.cancel()
        statusJob = scope.launch {
            while (isActive && mine == generation) {
                val repository = repository ?: return@launch
                val target = _state.value.target ?: return@launch
                val result = runCatching { repository.publishStatus(target.id) }.getOrNull()
                if (result != null && mine == generation && _state.value.isPublishing) {
                    _state.update {
                        when {
                            result.publishing -> it.copy(phase = Phase.STREAMING, status = "streaming - the server is receiving video from this phone", error = null)
                            it.phase == Phase.STREAMING || it.phase == Phase.CONNECTED ->
                                it.copy(phase = Phase.CONNECTED, status = "connected, but the server isn't receiving video yet…")
                            else -> it
                        }
                    }
                }
                delay(STATUS_POLL_MS)
            }
        }
    }

    private fun scheduleRetry(mine: Int) {
        if (mine != generation || !_state.value.isPublishing) return
        retryJob?.cancel()
        retryJob = scope.launch {
            delay(RETRY_MS)
            if (mine != generation || !_state.value.isPublishing) return@launch
            reconnect(restartCapture = false)
        }
    }

    // A fresh connection, keeping the target. After a doze the capture itself may have been
    // taken away, so it is restarted too when asked.
    private suspend fun reconnect(restartCapture: Boolean) {
        val current = _state.value
        if (!current.isPublishing) return
        if (restartCapture) stopCapture()
        // Not cancelled: this very often *is* the retry job, and cancelling it here made every
        // suspension below throw - so no reconnect ever finished. A pending retry that isn't the
        // caller is left alone; the generation bump turns it into a no-op when it fires.
        retryJob = null
        statusJob?.cancel()
        closePeer()
        val mine = ++generation
        _state.update { it.copy(phase = Phase.RECONNECTING) }
        try {
            ensureCapture(current.deviceId)
            if (current.includeMic) ensureMicrophone()
        } catch (error: Exception) {
            if (mine != generation) return
            fail("Couldn't reopen the camera: ${describe(error)}", mine)
            return
        }
        if (mine != generation) return
        connect(mine)
    }

    // ---------------- Capture ----------------

    private fun ensureFactory(): PeerConnectionFactory {
        factory?.let { return it }
        PeerConnectionFactory.initialize(
            PeerConnectionFactory.InitializationOptions.builder(appContext).createInitializationOptions(),
        )
        // H.264 only, deliberately - see SentinelH264EncoderFactory for why, and for the software
        // fallback on a phone without a hardware H.264 encoder.
        val h264 = SentinelH264EncoderFactory(eglBase.eglBaseContext)
        if (!h264.hasEncoder) {
            throw IOException("This phone has no H.264 video encoder, which the server needs to record from it.")
        }
        if (h264.usesSoftwareEncoder) {
            logger("Device camera: no hardware H.264 encoder on this phone - using the software one, which costs more battery", DiagnosticLevel.NOTICE)
        }
        val created = PeerConnectionFactory.builder()
            .setVideoEncoderFactory(h264)
            .setVideoDecoderFactory(DefaultVideoDecoderFactory(eglBase.eglBaseContext))
            .createPeerConnectionFactory()
        factory = created
        return created
    }

    private fun granted(permission: String) =
        ContextCompat.checkSelfPermission(appContext, permission) == PackageManager.PERMISSION_GRANTED

    private fun missingPermissionMessage(includeMic: Boolean): String? = when {
        !granted(Manifest.permission.CAMERA) -> CAMERA_DENIED
        includeMic && !granted(Manifest.permission.RECORD_AUDIO) -> MICROPHONE_DENIED
        else -> null
    }

    private const val CAMERA_DENIED =
        "Sentinel Core isn't allowed to use the camera. Allow it under Settings → Apps → Sentinel Core → Permissions, then try again."
    private const val MICROPHONE_DENIED =
        "Sentinel Core isn't allowed to use the microphone. Allow it under Settings → Apps → Sentinel Core → Permissions, or turn the microphone off here."

    private suspend fun ensureCapture(requested: String) {
        if (!granted(Manifest.permission.CAMERA)) throw IOException(CAMERA_DENIED)
        val factory = ensureFactory()
        refreshDevices()
        val devices = _state.value.devices
        val device = devices.firstOrNull { it.id == requested } ?: devices.firstOrNull()
            ?: throw IOException("No camera was found on this phone.")

        if (captureRunning && device.id == _state.value.deviceId) return
        stopCapture()

        val source = videoSource ?: factory.createVideoSource(false).also { created ->
            videoSource = created
            videoTrack = factory.createVideoTrack("sentinelcore-video", created)
        }
        val enumerator = Camera2Enumerator(appContext)
        val capturer = enumerator.createCapturer(device.id, CameraEvents) ?: throw IOException("Couldn't open that camera.")
        val helper = SurfaceTextureHelper.create("SentinelCoreCapture", eglBase.eglBaseContext)
        capturer.initialize(helper, appContext, source.capturerObserver)
        // 720p at up to 30fps, or the nearest the camera offers. Enough for a room; more than the
        // server keeps (its HLS output for a published camera is a 640x480 encode), and the
        // hardware encoder is what keeps it cheap on a phone that has to run all day.
        capturer.startCapture(1280, 720, 30)
        this.capturer = capturer
        surfaceHelper = helper
        captureRunning = true
        _state.update { it.copy(deviceId = device.id) }
        _previewTrack.value = videoTrack
    }

    private fun stopCapture() {
        runCatching { capturer?.stopCapture() }
        capturer?.dispose()
        capturer = null
        surfaceHelper?.dispose()
        surfaceHelper = null
        captureRunning = false
        _previewTrack.value = null
    }

    private fun ensureMicrophone() {
        if (audioTrack != null) return
        if (!granted(Manifest.permission.RECORD_AUDIO)) throw IOException(MICROPHONE_DENIED)
        val factory = ensureFactory()
        // WebRTC's audio processing is tuned for a call, and a camera is not a call: nothing plays
        // out of this phone, so the echo canceller has nothing to cancel and instead eats any sound
        // that resembles what the phone last played; and noise suppression flattens exactly the
        // faint room sounds a security recording is for. Gain control stays.
        val constraints = MediaConstraints().apply {
            mandatory.add(MediaConstraints.KeyValuePair("googEchoCancellation", "false"))
            mandatory.add(MediaConstraints.KeyValuePair("googNoiseSuppression", "false"))
            mandatory.add(MediaConstraints.KeyValuePair("googHighpassFilter", "true"))
            mandatory.add(MediaConstraints.KeyValuePair("googAutoGainControl", "true"))
        }
        val source = factory.createAudioSource(constraints)
        audioTrack = factory.createAudioTrack("sentinelcore-audio", source)
    }

    // go2rtc sends no bandwidth feedback (no REMB, no transport-cc), so the encoder's estimator
    // can sit at its cautious starting point and shrink the picture - seen on the Mac at 480x270,
    // "qualityLimitationReason=bandwidth". A floor on the bitrate keeps the encoder at the
    // capture's resolution; the ceiling keeps a phone honest on Wi-Fi.
    private fun applyVideoBitrate(sender: RtpSender?) {
        val parameters = sender?.parameters ?: return
        parameters.encodings.forEach {
            it.minBitrateBps = 1_500_000
            it.maxBitrateBps = 4_000_000
            it.maxFramerate = 30
        }
        // A security camera would rather drop frames than detail: a face at 15fps is still a
        // face, a face at 480x270 is a smudge.
        parameters.degradationPreference = RtpParameters.DegradationPreference.MAINTAIN_RESOLUTION
        sender.parameters = parameters
    }

    private fun closePeer() {
        peer?.let { runCatching { it.close() } }
        peer = null
    }

    private suspend fun waitForIceGathering(peer: PeerConnection, timeoutMs: Long) {
        withTimeoutOrNull(timeoutMs) {
            while (peer.iceGatheringState() != PeerConnection.IceGatheringState.COMPLETE) delay(100)
        }
    }

    // A camera error mid-stream (another app grabbed it, the OS took it away) is a reason to
    // reconnect with a fresh capture, not to give up.
    private object CameraEvents : CameraVideoCapturer.CameraEventsHandler {
        override fun onCameraError(error: String?) {
            scope.launch {
                if (!_state.value.isPublishing) return@launch
                _state.update { it.copy(status = "camera error (${error ?: "unknown"}) - reopening…") }
                delay(RETRY_MS)
                reconnect(restartCapture = true)
            }
        }
        override fun onCameraDisconnected() {
            scope.launch {
                if (!_state.value.isPublishing) return@launch
                _state.update { it.copy(status = "camera disconnected - reopening…") }
                delay(RETRY_MS)
                reconnect(restartCapture = true)
            }
        }
        override fun onCameraFreezed(error: String?) {}
        override fun onCameraOpening(cameraName: String?) {}
        override fun onFirstFrameAvailable() {}
        override fun onCameraClosed() {}
    }

    // ---------------- Surviving a relaunch, and a doze ----------------

    private fun rememberIntent() {
        val current = _state.value
        val target = current.target ?: return
        appContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .putString(INTENT_ID, target.id)
            .putString(INTENT_NAME, target.name)
            .putString(INTENT_DEVICE, current.deviceId)
            .putBoolean(INTENT_MIC, current.includeMic)
            .apply()
    }

    private fun forgetIntent() {
        appContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .remove(INTENT_ID).remove(INTENT_NAME).remove(INTENT_DEVICE).remove(INTENT_MIC)
            .apply()
    }

    val storedTarget: Target?
        get() {
            val prefs = appContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            val id = prefs.getString(INTENT_ID, null) ?: return null
            return Target(id, prefs.getString(INTENT_NAME, null) ?: "camera")
        }

    // Picks streaming back up after the app was relaunched, if it was streaming when it went. The
    // app is what has to be opened for the camera to run again, but the person shouldn't then also
    // have to find the screen and press start.
    fun resumeIfIntended(): Target? {
        if (_state.value.isPublishing) return null
        val prefs = appContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val target = storedTarget ?: return null
        start(target, prefs.getString(INTENT_DEVICE, null).orEmpty(), prefs.getBoolean(INTENT_MIC, false))
        return target
    }

    // A phone that dozed froze this loop; on waking, the gap between two ticks is however long
    // it was out, while the peer connection may never announce that it died.
    private fun startHeartbeat() {
        heartbeatJob?.cancel()
        heartbeatJob = scope.launch {
            var last = System.currentTimeMillis()
            while (isActive) {
                delay(HEARTBEAT_MS)
                val now = System.currentTimeMillis()
                val gap = now - last
                last = now
                if (!_state.value.isPublishing || gap < SUSPEND_GAP_MS) continue
                val minutes = (gap / 60_000.0).toInt().coerceAtLeast(1)
                _state.update { it.copy(status = "this phone was asleep for about $minutes minute${if (minutes == 1) "" else "s"} - reconnecting…") }
                reconnect(restartCapture = true)
            }
        }
    }

    private fun describe(error: Exception): String = when {
        error is SentinelApiException && error.statusCode == 401 -> "this phone is no longer signed in - sign in again"
        error is SentinelApiException && error.statusCode == 503 -> "the server can't accept published video because go2rtc isn't running on it"
        else -> error.message ?: error.javaClass.simpleName
    }

    // ---------------- WebRTC glue ----------------

    // The one callback this needs, with the rest of the observer's required methods as no-ops:
    // every other one is about receiving media, and this connection only sends.
    private abstract class PeerConnectionObserverAdapter : PeerConnection.Observer {
        override fun onSignalingChange(newState: PeerConnection.SignalingState?) {}
        override fun onIceConnectionChange(newState: PeerConnection.IceConnectionState?) {}
        override fun onIceConnectionReceivingChange(receiving: Boolean) {}
        override fun onIceGatheringChange(newState: PeerConnection.IceGatheringState?) {}
        override fun onIceCandidate(candidate: IceCandidate?) {}
        override fun onIceCandidatesRemoved(candidates: Array<out IceCandidate>?) {}
        override fun onAddStream(stream: MediaStream?) {}
        override fun onRemoveStream(stream: MediaStream?) {}
        override fun onDataChannel(dataChannel: DataChannel?) {}
        override fun onRenegotiationNeeded() {}
    }

    private suspend fun PeerConnection.createOfferSuspend(): SessionDescription =
        suspendCancellableCoroutine { continuation ->
            createOffer(
                object : SdpObserver {
                    override fun onCreateSuccess(sdp: SessionDescription) { continuation.resume(sdp) }
                    override fun onCreateFailure(error: String?) { continuation.resumeWithException(IOException(error ?: "createOffer failed")) }
                    override fun onSetSuccess() {}
                    override fun onSetFailure(error: String?) {}
                },
                MediaConstraints(),
            )
        }

    private suspend fun PeerConnection.setLocalSuspend(sdp: SessionDescription) =
        suspendCancellableCoroutine { continuation ->
            setLocalDescription(
                object : SdpObserver {
                    override fun onCreateSuccess(sdp: SessionDescription?) {}
                    override fun onCreateFailure(error: String?) {}
                    override fun onSetSuccess() { continuation.resume(Unit) }
                    override fun onSetFailure(error: String?) { continuation.resumeWithException(IOException(error ?: "setLocalDescription failed")) }
                },
                sdp,
            )
        }

    private suspend fun PeerConnection.setRemoteSuspend(sdp: SessionDescription) =
        suspendCancellableCoroutine { continuation ->
            setRemoteDescription(
                object : SdpObserver {
                    override fun onCreateSuccess(sdp: SessionDescription?) {}
                    override fun onCreateFailure(error: String?) {}
                    override fun onSetSuccess() { continuation.resume(Unit) }
                    override fun onSetFailure(error: String?) { continuation.resumeWithException(IOException(error ?: "setRemoteDescription failed")) }
                },
                sdp,
            )
        }

    // What the notification's "open" tap does: asks SentinelApp to show the screen.
    fun requestScreen(intent: Intent?) {
        if (intent?.getBooleanExtra(OPEN_SCREEN_EXTRA, false) == true) openScreenRequested.value = true
    }

    const val OPEN_SCREEN_EXTRA = "openDeviceCamera"
}
