package com.sentinelcore.android.ui.screens

import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.PowerManager
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Info
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.sentinelcore.android.data.Camera
import com.sentinelcore.android.data.CameraInput
import com.sentinelcore.android.publish.DevicePublisher
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import kotlinx.coroutines.launch
import org.webrtc.RendererCommon
import org.webrtc.SurfaceViewRenderer
import org.webrtc.VideoTrack

// "Use this phone as a camera": the phone this app is on becomes one of the dashboard's cameras,
// in a few taps - pick (or create) the camera it should be, check the preview, start. The web
// dashboard's publish page does the same from a browser; this is the native version, with the
// things a browser can't do: the phone can turn its screen off and keep streaming (see
// DevicePublisherService), and streaming resumes on its own after the app is relaunched.
//
// The streaming itself lives in DevicePublisher and outlives this screen. Leaving this screen is
// not stopping; the stop button is.
@Composable
fun DeviceCameraScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onBack: () -> Unit,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val publisher by DevicePublisher.state.collectAsStateWithLifecycle()
    val previewTrack by DevicePublisher.previewTrack.collectAsStateWithLifecycle()

    val targets = state.cameras.filter { it.isBrowserPublished }
    var selectedTargetId by rememberSaveable { mutableStateOf<String?>(publisher.target?.id) }
    // The selection as far as the server is concerned: a remembered or resumed camera that this
    // server doesn't have (the app was pointed at another server, the camera was deleted) reads
    // as "New camera…" rather than as no chip selected and a hidden name field.
    val effectiveTargetId = selectedTargetId?.takeIf { id -> targets.any { it.id == id } }
    var newName by rememberSaveable { mutableStateOf(deviceName()) }
    var selectedDeviceId by rememberSaveable { mutableStateOf(publisher.deviceId) }
    var includeMic by rememberSaveable { mutableStateOf(publisher.includeMic) }
    var isBusy by remember { mutableStateOf(false) }
    var localError by remember { mutableStateOf<String?>(null) }
    var cameraGranted by remember {
        mutableStateOf(ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED)
    }
    var pendingStart by remember { mutableStateOf(false) }

    fun beginPreview() {
        DevicePublisher.refreshDevices()
        if (!publisher.isPublishing) {
            if (selectedDeviceId.isEmpty()) selectedDeviceId = DevicePublisher.defaultDeviceId()
            DevicePublisher.preview(selectedDeviceId)
        }
    }

    // The camera's own "include audio" setting is what makes viewers ask for the audio track, so
    // it has to follow the microphone switch here - otherwise a mic turned on later streams sound
    // that nothing plays. Best effort: a failure to save it must not stop the stream.
    suspend fun syncIncludeAudio(target: DevicePublisher.Target) {
        val camera = targets.firstOrNull { it.id == target.id } ?: return
        if (camera.includeAudio == includeMic) return
        try {
            model.updateCamera(
                camera.id,
                CameraInput(
                    name = camera.name,
                    streamUrl = "",
                    username = camera.username,
                    password = camera.password,
                    includeAudio = includeMic,
                    ptzEnabled = false,
                    onvifPtzServiceUrl = camera.onvifPtzServiceUrl,
                    onvifProfileToken = camera.onvifProfileToken,
                    isBrowserPublished = true,
                ),
            )
        } catch (error: Exception) {
            localError = "The microphone streams, but the camera's audio setting couldn't be saved: ${error.message}"
        }
    }

    suspend fun startStreaming() {
        isBusy = true
        localError = null
        try {
            val target = targets.firstOrNull { it.id == effectiveTargetId }?.let { DevicePublisher.Target(it.id, it.name) }
                ?: run {
                    val created = model.createCamera(
                        CameraInput(
                            name = newName.trim(),
                            streamUrl = "",
                            username = null,
                            password = null,
                            includeAudio = includeMic,
                            ptzEnabled = false,
                            onvifPtzServiceUrl = null,
                            onvifProfileToken = null,
                            isBrowserPublished = true,
                        ),
                    )
                    selectedTargetId = created.id
                    DevicePublisher.Target(created.id, created.name)
                }
            syncIncludeAudio(target)
            DevicePublisher.start(target, selectedDeviceId, includeMic)
        } catch (error: Exception) {
            localError = error.message ?: "Could not create the camera."
        } finally {
            isBusy = false
        }
    }

    val permissionLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { granted ->
        cameraGranted = granted[Manifest.permission.CAMERA] == true ||
            ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
        if (cameraGranted) {
            if (pendingStart) {
                pendingStart = false
                if (granted[Manifest.permission.RECORD_AUDIO] == false) includeMic = false
                scope.launch { startStreaming() }
            } else {
                beginPreview()
            }
        } else {
            localError = "Sentinel Core needs the camera permission to stream from this phone."
        }
    }

    fun neededPermissions(forStart: Boolean): List<String> = buildList {
        if (!cameraGranted) add(Manifest.permission.CAMERA)
        if (forStart && includeMic &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED
        ) add(Manifest.permission.RECORD_AUDIO)
        if (forStart && Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) add(Manifest.permission.POST_NOTIFICATIONS)
    }

    LaunchedEffect(Unit) {
        model.refreshCameras()
        if (cameraGranted) beginPreview() else permissionLauncher.launch(neededPermissions(forStart = false).toTypedArray())
    }
    LaunchedEffect(publisher.target) {
        publisher.target?.let { selectedTargetId = it.id }
        if (publisher.isPublishing) {
            selectedDeviceId = publisher.deviceId
            includeMic = publisher.includeMic
        }
    }
    LaunchedEffect(targets.size) {
        // The camera this phone streamed to last time, if it still exists; otherwise "New
        // camera…". Never "the only one there is": on a first run that one is usually another
        // device's (a laptop on the browser page), and Start must not take it over.
        if (selectedTargetId == null) {
            val last = DevicePublisher.lastTargetId
            if (last != null && targets.any { it.id == last }) selectedTargetId = last
        }
    }
    DisposableEffect(Unit) { onDispose { DevicePublisher.endPreview() } }

    // The screen stays on while this is in front and streaming - and it is fine to turn it off:
    // the foreground service keeps the camera going with the screen dark.
    val view = LocalView.current
    DisposableEffect(publisher.isPublishing) {
        view.keepScreenOn = publisher.isPublishing
        onDispose { view.keepScreenOn = false }
    }

    Column(Modifier.fillMaxSize()) {
        DetailHeader("This Phone as a Camera", onBack)
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            contentPadding = PaddingValues(16.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            item {
                Box(
                    Modifier.fillMaxWidth().widthIn(max = 900.dp).aspectRatio(4f / 3f)
                        .clip(RoundedCornerShape(12.dp)).background(Color.Black),
                    contentAlignment = Alignment.Center,
                ) {
                    PublisherPreview(previewTrack, mirror = publisher.devices.firstOrNull { it.id == publisher.deviceId }?.isFront == true)
                    if (previewTrack == null) {
                        Text("no camera yet", color = Color.White.copy(alpha = 0.5f), fontFamily = FontFamily.Monospace)
                    }
                }
            }

            if (!state.isAdmin) {
                item {
                    Card(Modifier.fillMaxWidth().widthIn(max = 900.dp)) {
                        Text(
                            "Only an Admin account can add a phone as a camera. Sign in as an Admin on this phone to use it here.",
                            modifier = Modifier.padding(14.dp),
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
                return@LazyColumn
            }

            item {
                Card(Modifier.fillMaxWidth().widthIn(max = 900.dp)) {
                    Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        Text("Dashboard camera", style = MaterialTheme.typography.titleSmall)
                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            targets.forEach { camera ->
                                FilterChip(
                                    selected = effectiveTargetId == camera.id,
                                    onClick = { selectedTargetId = camera.id },
                                    label = { Text(camera.name) },
                                    enabled = !publisher.isPublishing,
                                )
                            }
                            FilterChip(
                                selected = effectiveTargetId == null,
                                onClick = { selectedTargetId = null },
                                label = { Text("New camera…") },
                                enabled = !publisher.isPublishing,
                            )
                        }
                        if (effectiveTargetId == null) {
                            OutlinedTextField(
                                value = newName,
                                onValueChange = { newName = it },
                                label = { Text("Name on the dashboard") },
                                singleLine = true,
                                enabled = !publisher.isPublishing,
                                modifier = Modifier.fillMaxWidth(),
                            )
                        }
                        Text(
                            if (targets.isEmpty()) {
                                "A new camera is created for this phone the moment you start. It appears on the Live tab and records, fires trigger zones and gets analyzed like any other."
                            } else {
                                "Pick the dashboard camera this phone should be, or make a new one. Each device streams to its own camera."
                            },
                            style = MaterialTheme.typography.labelSmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }

            item {
                Card(Modifier.fillMaxWidth().widthIn(max = 900.dp)) {
                    Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        Text("This phone", style = MaterialTheme.typography.titleSmall)
                        if (publisher.devices.isEmpty()) {
                            Text("No camera found on this phone.", color = MaterialTheme.colorScheme.onSurfaceVariant)
                        } else {
                            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                publisher.devices.forEach { device ->
                                    FilterChip(
                                        selected = selectedDeviceId == device.id,
                                        onClick = {
                                            if (selectedDeviceId == device.id) return@FilterChip
                                            selectedDeviceId = device.id
                                            // Changing camera mid-stream restarts it - a second of
                                            // video, not a lost session.
                                            if (publisher.isPublishing) {
                                                publisher.target?.let { DevicePublisher.start(it, device.id, includeMic) }
                                            } else {
                                                DevicePublisher.preview(device.id)
                                            }
                                        },
                                        label = { Text(device.name) },
                                    )
                                }
                            }
                        }
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text("Include this phone's microphone", Modifier.weight(1f))
                            Switch(
                                checked = includeMic,
                                onCheckedChange = { wanted ->
                                    includeMic = wanted
                                    if (publisher.isPublishing) {
                                        if (wanted && ContextCompat.checkSelfPermission(context, Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
                                            pendingStart = true
                                            permissionLauncher.launch(arrayOf(Manifest.permission.RECORD_AUDIO))
                                        } else {
                                            publisher.target?.let { target ->
                                                scope.launch {
                                                    syncIncludeAudio(target)
                                                    DevicePublisher.start(target, selectedDeviceId, wanted)
                                                }
                                            }
                                        }
                                    }
                                },
                            )
                        }
                    }
                }
            }

            item {
                Column(Modifier.fillMaxWidth().widthIn(max = 900.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(
                        onClick = {
                            if (publisher.isPublishing) {
                                DevicePublisher.stop()
                            } else {
                                val needed = neededPermissions(forStart = true)
                                if (needed.isEmpty()) {
                                    scope.launch { startStreaming() }
                                } else {
                                    pendingStart = true
                                    permissionLauncher.launch(needed.toTypedArray())
                                }
                            }
                        },
                        // No camera means nothing to start - and starting would still create the
                        // dashboard camera first, leaving an empty one behind.
                        enabled = !isBusy && (publisher.isPublishing ||
                            (publisher.devices.isNotEmpty() && (effectiveTargetId != null || newName.isNotBlank()))),
                        colors = ButtonDefaults.buttonColors(containerColor = if (publisher.isPublishing) SentinelRed else SentinelGreen),
                        modifier = Modifier.fillMaxWidth(),
                    ) {
                        if (isBusy) CircularProgressIndicator(Modifier.size(18.dp), color = Color.White)
                        else Text(if (publisher.isPublishing) "Stop streaming" else "Start streaming")
                    }
                    if (publisher.status.isNotBlank()) {
                        Text(
                            publisher.status,
                            fontFamily = FontFamily.Monospace,
                            style = MaterialTheme.typography.bodySmall,
                            color = when (publisher.phase) {
                                DevicePublisher.Phase.STREAMING -> SentinelGreen
                                DevicePublisher.Phase.RECONNECTING -> SentinelAmber
                                else -> MaterialTheme.colorScheme.onSurfaceVariant
                            },
                        )
                    }
                    (publisher.error ?: localError)?.let {
                        Text(it, color = SentinelRed, style = MaterialTheme.typography.bodySmall)
                    }
                }
            }

            item {
                KeepAwakeCard(context, publisher.isPublishing)
            }
        }
    }
}

// Everything the app does itself to keep this phone streaming unattended, and the two things it
// can't do for you. Android is the platform where the least is asked: with the foreground
// service holding the camera, the screen can simply be turned off.
@Composable
private fun KeepAwakeCard(context: Context, isPublishing: Boolean) {
    val power = context.getSystemService(Context.POWER_SERVICE) as PowerManager
    var unrestricted by remember { mutableStateOf(power.isIgnoringBatteryOptimizations(context.packageName)) }
    LaunchedEffect(isPublishing) { unrestricted = power.isIgnoringBatteryOptimizations(context.packageName) }

    Card(Modifier.fillMaxWidth().widthIn(max = 900.dp)) {
        Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text("Keeping it going", style = MaterialTheme.typography.titleSmall)
            Advice(true, "The screen can be turned off. A foreground service keeps the camera and the connection running with the screen dark, and holds the phone awake - the notification is that service.")
            Advice(true, "If the app is closed or the phone restarts, streaming resumes on its own the next time the app is opened.")
            Advice(true, "The connection recovers on its own after a network blip, a doze, or a server restart. If the server stops receiving video for two minutes it sends you a message, and another when it is back.")
            Advice(false, "Plug it in. A phone streaming video all day will not last on its battery.")
            Advice(false, "Don't swipe Sentinel Core away from the recent apps - on many phones that ends the service with it.")
            Advice(
                unrestricted,
                if (unrestricted) "Battery use is unrestricted for Sentinel Core, so the phone won't put the stream to sleep to save power."
                else "Set battery use to Unrestricted for Sentinel Core, so the phone doesn't put the stream to sleep to save power.",
            )
            if (!unrestricted) {
                OutlinedButton(onClick = {
                    runCatching {
                        context.startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                    }
                }) { Text("Open battery settings") }
            }
        }
    }
}

@Composable
private fun Advice(handled: Boolean, text: String) {
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.Top) {
        Icon(
            if (handled) Icons.Default.CheckCircle else Icons.Default.Info,
            contentDescription = null,
            tint = if (handled) SentinelGreen else SentinelAmber,
            modifier = Modifier.size(18.dp),
        )
        Text(text, style = MaterialTheme.typography.bodySmall, modifier = Modifier.weight(1f))
    }
}

// The live picture from this phone's own camera, drawn by WebRTC's renderer straight from the
// track being (or about to be) streamed - so what is on screen is exactly what the server gets.
@Composable
private fun PublisherPreview(track: VideoTrack?, mirror: Boolean) {
    AndroidView(
        modifier = Modifier.fillMaxSize(),
        factory = { context ->
            SurfaceViewRenderer(context).apply {
                init(DevicePublisher.eglBase.eglBaseContext, null)
                setScalingType(RendererCommon.ScalingType.SCALE_ASPECT_FIT)
                setEnableHardwareScaler(true)
                tag = null
            }
        },
        update = { renderer ->
            val attached = renderer.tag as? VideoTrack
            if (attached !== track) {
                attached?.removeSink(renderer)
                track?.addSink(renderer)
                renderer.tag = track
            }
            renderer.setMirror(mirror)
        },
        onRelease = { renderer ->
            (renderer.tag as? VideoTrack)?.removeSink(renderer)
            renderer.release()
        },
    )
}

private fun deviceName(): String {
    val model = Build.MODEL.orEmpty()
    val manufacturer = Build.MANUFACTURER.orEmpty()
    return if (model.startsWith(manufacturer, ignoreCase = true) || manufacturer.isBlank()) model.ifBlank { "Phone" }
    else "${manufacturer.replaceFirstChar { it.uppercase() }} $model"
}
