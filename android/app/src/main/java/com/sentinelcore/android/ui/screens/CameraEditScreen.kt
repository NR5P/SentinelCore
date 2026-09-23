package com.sentinelcore.android.ui.screens

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.Error
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.data.Camera
import com.sentinelcore.android.data.CameraInput
import com.sentinelcore.android.data.CameraProbeRequest
import com.sentinelcore.android.data.CameraProbeResult
import com.sentinelcore.android.data.SECRET_MASK
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import kotlinx.coroutines.launch

// `prefillStreamUrl` is used by the discovery flow: tapping a scan result opens this screen
// pre-filled with just the bare IP (per the web behavior) - the user still runs Test Connection
// here to resolve the real, credentialed stream URL before saving.
@Composable
fun CameraEditScreen(
    camera: Camera?,
    prefillStreamUrl: String?,
    model: SentinelViewModel,
    onBack: () -> Unit,
    onConfigureZone: (Camera) -> Unit,
) {
    val isNew = camera == null
    var name by remember { mutableStateOf(camera?.name.orEmpty()) }
    var streamUrl by remember { mutableStateOf(camera?.streamUrl ?: prefillStreamUrl.orEmpty()) }
    // Both of these used to start blank, so every save from this screen sent no credentials and
    // the PUT - which replaces the record rather than merging it - wiped whatever the camera had.
    // `password` is the mask, not the real value; see SECRET_MASK.
    var username by remember { mutableStateOf(camera?.username.orEmpty()) }
    var password by remember { mutableStateOf(camera?.password.orEmpty()) }
    // Until someone presses Show, the box holds the mask and is not editable: leaving the mask in
    // place is what tells the server to keep the stored password, and typing onto the end of it
    // would save "••••••••newpass" as the credential.
    var passwordRevealed by remember { mutableStateOf(isNew) }
    var isRevealing by remember { mutableStateOf(false) }
    var includeAudio by remember { mutableStateOf(camera?.includeAudio ?: false) }
    var ptzEnabled by remember { mutableStateOf(camera?.ptzEnabled ?: false) }
    // Not shown directly - just carries whatever Test Connection last detected (or this camera's
    // existing values, if editing) through to save(), the same way streamUrl/name get silently
    // filled in from a probe result rather than the user typing them.
    var onvifPtzServiceUrl by remember { mutableStateOf(camera?.onvifPtzServiceUrl) }
    var onvifProfileToken by remember { mutableStateOf(camera?.onvifProfileToken) }

    var isTesting by remember { mutableStateOf(false) }
    var isSaving by remember { mutableStateOf(false) }
    var probeResult by remember { mutableStateOf<CameraProbeResult?>(null) }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    fun testConnection() {
        scope.launch {
            isTesting = true
            errorMessage = null
            try {
                // Test Connection dials the camera for real, so the mask would simply fail its
                // login - fetch the stored password for that one call rather than making someone
                // reveal it first.
                val probePassword = when {
                    password != SECRET_MASK -> password.ifBlank { null }
                    camera != null -> model.cameraPassword(camera.id).ifBlank { null }
                    else -> null
                }
                val result = model.probeCamera(
                    CameraProbeRequest(
                        address = streamUrl.trim(),
                        username = username.ifBlank { null },
                        password = probePassword,
                    ),
                )
                probeResult = result
                if (result.status == "Found") {
                    if (!streamUrl.contains("://") && result.streamUrl != null) {
                        streamUrl = result.streamUrl
                    }
                    if (name.isBlank() && result.suggestedName != null) {
                        name = result.suggestedName
                    }
                    // Always carry through the latest detection, but only ever turn the switch
                    // ON here, never off - a user who manually enabled PTZ for a camera this
                    // particular test didn't detect it on shouldn't have that silently undone by
                    // re-testing the connection.
                    onvifPtzServiceUrl = result.onvifPtzServiceUrl
                    onvifProfileToken = result.onvifProfileToken
                    if (result.supportsPtz) ptzEnabled = true
                }
            } catch (error: Exception) {
                errorMessage = error.message ?: "Could not test this connection."
            } finally {
                isTesting = false
            }
        }
    }

    fun save() {
        scope.launch {
            isSaving = true
            errorMessage = null
            val input = CameraInput(
                name = name.trim(),
                streamUrl = streamUrl.trim(),
                username = username.ifBlank { null },
                password = password.ifBlank { null },
                includeAudio = includeAudio,
                ptzEnabled = ptzEnabled,
                onvifPtzServiceUrl = onvifPtzServiceUrl,
                onvifProfileToken = onvifProfileToken,
                isBrowserPublished = camera?.isBrowserPublished == true,
            )
            try {
                if (camera != null) {
                    model.updateCamera(camera.id, input)
                } else {
                    model.createCamera(input)
                }
                onBack()
            } catch (error: Exception) {
                errorMessage = error.message ?: "Could not save this camera."
            } finally {
                isSaving = false
            }
        }
    }

    Column(Modifier.fillMaxSize()) {
        DetailHeader(if (isNew) "Add Camera" else "Edit Camera", onBack) {
            if (isSaving) {
                CircularProgressIndicator(Modifier.size(20.dp))
            } else {
                TextButton(
                    onClick = ::save,
                    enabled = name.isNotBlank() && (streamUrl.isNotBlank() || camera?.isBrowserPublished == true),
                ) {
                    Text("Save")
                }
            }
        }
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            contentPadding = PaddingValues(16.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            item {
                Card(Modifier.fillMaxWidth().widthIn(max = 900.dp)) {
                    Column(
                        modifier = Modifier.padding(14.dp),
                        verticalArrangement = Arrangement.spacedBy(12.dp),
                    ) {
                        OutlinedTextField(
                            value = name,
                            onValueChange = { name = it },
                            label = { Text("Name") },
                            singleLine = true,
                            modifier = Modifier.fillMaxWidth(),
                        )
                        if (camera?.isBrowserPublished == true) {
                            // No address to edit: the device streams to the server, not the other way.
                            Text(
                                "Streams from a device (a phone, a Mac, or a browser's publish page). The device picks this camera when it starts streaming.",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        } else OutlinedTextField(
                            value = streamUrl,
                            onValueChange = { streamUrl = it },
                            label = { Text("Stream URL or address") },
                            singleLine = true,
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                            modifier = Modifier.fillMaxWidth(),
                        )
                        OutlinedTextField(
                            value = username,
                            onValueChange = { username = it },
                            label = { Text("Username (optional)") },
                            singleLine = true,
                            modifier = Modifier.fillMaxWidth(),
                        )
                        OutlinedTextField(
                            value = password,
                            onValueChange = { password = it },
                            label = { Text("Password (optional)") },
                            singleLine = true,
                            readOnly = !passwordRevealed,
                            visualTransformation = if (passwordRevealed) {
                                VisualTransformation.None
                            } else {
                                PasswordVisualTransformation()
                            },
                            modifier = Modifier.fillMaxWidth(),
                        )
                        if (!passwordRevealed) {
                            TextButton(
                                onClick = {
                                    if (camera == null) return@TextButton
                                    scope.launch {
                                        isRevealing = true
                                        try {
                                            password = model.cameraPassword(camera.id)
                                            passwordRevealed = true
                                        } catch (error: Exception) {
                                            errorMessage = error.message
                                                ?: "Could not read the saved password."
                                        } finally {
                                            isRevealing = false
                                        }
                                    }
                                },
                                enabled = !isRevealing && password.isNotEmpty(),
                            ) {
                                Text(if (isRevealing) "Showing…" else "Show password")
                            }
                            Text(
                                if (password.isEmpty()) {
                                    "This camera has no password saved."
                                } else {
                                    "The saved password is kept on the server and isn't sent with " +
                                        "the camera list. Show it to check or change it."
                                },
                                style = MaterialTheme.typography.bodySmall,
                            )
                        }
                        Row(
                            Modifier.fillMaxWidth(),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Text("Include Audio", modifier = Modifier.weight(1f))
                            Switch(checked = includeAudio, onCheckedChange = { includeAudio = it })
                        }
                        Row(
                            Modifier.fillMaxWidth(),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Text("Pan/Tilt/Zoom (PTZ) Joystick", modifier = Modifier.weight(1f))
                            Switch(checked = ptzEnabled, onCheckedChange = { ptzEnabled = it })
                        }
                        Text(
                            "Shows on-screen directional controls in Live view for cameras that can physically move. " +
                                "Turned on automatically when Test Connection detects an ONVIF PTZ service - you can " +
                                "also turn it on by hand, but the controls won't do anything without one.",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }

            item {
                Card(Modifier.fillMaxWidth().widthIn(max = 900.dp)) {
                    Column(
                        modifier = Modifier.padding(14.dp),
                        verticalArrangement = Arrangement.spacedBy(10.dp),
                    ) {
                        OutlinedButton(
                            onClick = ::testConnection,
                            enabled = !isTesting && streamUrl.isNotBlank(),
                            modifier = Modifier.fillMaxWidth(),
                        ) {
                            if (isTesting) {
                                CircularProgressIndicator(Modifier.size(18.dp))
                            } else {
                                Text("Test Connection")
                            }
                        }
                        probeResult?.let { result ->
                            val (icon, color, message) = when (result.status) {
                                "Found" -> Triple(
                                    Icons.Default.CheckCircle,
                                    SentinelGreen,
                                    result.message ?: "Camera found.",
                                )
                                "NeedsCredentials" -> Triple(
                                    Icons.Default.Lock,
                                    SentinelAmber,
                                    "This camera needs credentials. Enter them above and test again.",
                                )
                                else -> Triple(
                                    Icons.Default.Error,
                                    SentinelRed,
                                    result.message ?: "Camera not found.",
                                )
                            }
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Icon(icon, contentDescription = null, tint = color)
                                Text(message, color = color, modifier = Modifier.padding(start = 8.dp))
                            }
                        }
                    }
                }
            }

            errorMessage?.let { message ->
                item {
                    Text(
                        message,
                        color = SentinelRed,
                        modifier = Modifier.fillMaxWidth().widthIn(max = 900.dp),
                    )
                }
            }

            if (camera != null) {
                item {
                    Card(
                        modifier = Modifier.fillMaxWidth().widthIn(max = 900.dp)
                            .clickable { onConfigureZone(camera) },
                    ) {
                        Row(
                            modifier = Modifier.padding(14.dp).fillMaxWidth(),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Text("Configure Trigger Zone", modifier = Modifier.weight(1f))
                            Icon(Icons.Default.ChevronRight, contentDescription = null)
                        }
                    }
                }
            }
        }
    }
}
