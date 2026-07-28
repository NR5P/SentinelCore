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
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.data.Camera
import com.sentinelcore.android.data.CameraInput
import com.sentinelcore.android.data.CameraProbeRequest
import com.sentinelcore.android.data.CameraProbeResult
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
    var username by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var includeAudio by remember { mutableStateOf(camera?.includeAudio ?: false) }

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
                val result = model.probeCamera(
                    CameraProbeRequest(
                        address = streamUrl.trim(),
                        username = username.ifBlank { null },
                        password = password.ifBlank { null },
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
                    enabled = name.isNotBlank() && streamUrl.isNotBlank(),
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
                        OutlinedTextField(
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
                            visualTransformation = PasswordVisualTransformation(),
                            modifier = Modifier.fillMaxWidth(),
                        )
                        Row(
                            Modifier.fillMaxWidth(),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Text("Include Audio", modifier = Modifier.weight(1f))
                            Switch(checked = includeAudio, onCheckedChange = { includeAudio = it })
                        }
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
