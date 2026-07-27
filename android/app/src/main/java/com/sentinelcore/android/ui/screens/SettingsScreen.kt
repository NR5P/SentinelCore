package com.sentinelcore.android.ui.screens

import android.Manifest
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ExpandLess
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material3.Button
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.BuildConfig
import com.sentinelcore.android.data.DiagnosticEntry
import com.sentinelcore.android.data.DiagnosticLevel
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.components.ConnectionBadge
import com.sentinelcore.android.ui.components.SentinelPanel
import com.sentinelcore.android.ui.components.shortTime
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelText
import com.sentinelcore.android.ui.theme.SentinelTextDim

@Composable
fun SettingsScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
) {
    var address by rememberSaveable { mutableStateOf(state.serverUrl) }
    var showDiagnostics by rememberSaveable { mutableStateOf(false) }
    val notificationPermissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) {
        model.refreshNotificationPermission()
        if (it) model.enablePushNotifications()
    }
    LaunchedEffect(state.serverUrl) { address = state.serverUrl }
    LaunchedEffect(Unit) { model.refreshNotificationPermission() }

    Column(Modifier.fillMaxSize()) {
        ScreenHeader("SETTINGS", state)
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            item {
                SettingsSection("SERVER") {
                    OutlinedTextField(
                        value = address,
                        onValueChange = { address = it },
                        modifier = Modifier.fillMaxWidth(),
                        label = { Text("DASHBOARD ADDRESS") },
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                        singleLine = true,
                    )
                    Row(
                        Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Text("STATUS", color = SentinelTextDim)
                        Spacer(Modifier.weight(1f))
                        ConnectionBadge(state.connectionState)
                    }
                    Button(
                        onClick = { model.saveServer(address) },
                        enabled = address.isNotBlank() && !state.isLoading,
                        modifier = Modifier.fillMaxWidth(),
                    ) {
                        Text("SAVE AND RECONNECT")
                    }
                }
            }

            item {
                SettingsSection("ACCOUNT") {
                    state.authStatus?.username?.let {
                        SettingsRow("SIGNED IN AS", it)
                    }
                    state.authStatus?.role?.let {
                        SettingsRow("ROLE", it.uppercase())
                    }
                    if (state.authStatus?.enabled == true) {
                        OutlinedButton(
                            onClick = model::signOut,
                            modifier = Modifier.fillMaxWidth(),
                        ) {
                            Text("SIGN OUT", color = SentinelRed)
                        }
                    } else {
                        Text(
                            "Dashboard login is not enabled.",
                            color = SentinelTextDim,
                        )
                    }
                }
            }

            item {
                SettingsSection("SYSTEM") {
                    SettingsRow("EVENTS LOADED", state.events.size.toString())
                    SettingsRow("CAMERAS", state.cameras.size.toString())
                    SettingsRow("MOTION TRIGGERS", state.triggers.size.toString())
                    SettingsRow("APP VERSION", BuildConfig.VERSION_NAME)
                    state.lastError?.let { Text(it, color = SentinelRed) }
                }
            }

            item {
                SettingsSection("PUSH NOTIFICATIONS") {
                    SettingsRow("PERMISSION", state.pushPermission)
                    state.pushStatus?.let { status ->
                        SettingsRow("CORE", if (status.enabled) "ENABLED" else "DISABLED")
                        SettingsRow(
                            "FIREBASE PROVIDER",
                            if (status.firebaseConfigured) "CONFIGURED" else "NOT CONFIGURED",
                        )
                        SettingsRow("REGISTERED DEVICES", status.registeredDevices.toString())
                    }
                    Button(
                        onClick = {
                            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                                notificationPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
                            } else {
                                model.enablePushNotifications()
                            }
                        },
                        modifier = Modifier.fillMaxWidth(),
                    ) {
                        Text(
                            if (state.pushRegistrationEnabled) {
                                "REFRESH REGISTRATION"
                            } else {
                                "ENABLE NOTIFICATIONS"
                            },
                        )
                    }
                    if (state.pushRegistrationEnabled) {
                        OutlinedButton(
                            onClick = model::disablePushNotifications,
                            modifier = Modifier.fillMaxWidth(),
                        ) {
                            Text("DISABLE ON THIS DEVICE")
                        }
                    }
                    // Admin-only on the server (it messages every registered device, not just
                    // this one) - hidden rather than disabled for a Viewer, since there's
                    // nothing they could do to enable it.
                    if (state.isAdmin) {
                        OutlinedButton(
                            onClick = model::sendTestPushNotification,
                            enabled = state.pushStatus?.firebaseConfigured == true &&
                                state.pushPermission == "ENABLED" &&
                                state.pushRegistrationEnabled,
                            modifier = Modifier.fillMaxWidth(),
                        ) {
                            Text("SEND TEST NOTIFICATION")
                        }
                    }
                    state.pushMessage?.let { Text(it, color = SentinelTextDim) }
                }
            }

            item {
                SettingsSection("DIAGNOSTICS") {
                    TextButton(
                        onClick = { showDiagnostics = !showDiagnostics },
                        modifier = Modifier.fillMaxWidth(),
                    ) {
                        Text(
                            "CONNECTION LOG",
                            color = SentinelGreen,
                            modifier = Modifier.weight(1f),
                        )
                        Icon(
                            if (showDiagnostics) Icons.Default.ExpandLess else Icons.Default.ExpandMore,
                            contentDescription = if (showDiagnostics) "Collapse log" else "Expand log",
                        )
                    }
                    AnimatedVisibility(showDiagnostics) {
                        Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                            if (state.diagnostics.isEmpty()) {
                                Text("No requests recorded yet.", color = SentinelTextDim)
                            } else {
                                state.diagnostics.take(30).forEach { DiagnosticRow(it) }
                            }
                            TextButton(onClick = model::clearDiagnostics) {
                                Text("CLEAR LOG")
                            }
                        }
                    }
                    Text(
                        "Request bodies, passwords, cookies, and server response contents are never written here.",
                        color = SentinelTextDim,
                    )
                }
            }

            item {
                Text(
                    "This native client manages its connection and push registration locally. " +
                        "Full server configuration, camera editing, motion-zone drawing, and " +
                        "account management remain in the Core dashboard.",
                    color = SentinelTextDim,
                    modifier = Modifier.padding(horizontal = 24.dp, vertical = 8.dp)
                        .fillMaxWidth()
                        .widthIn(max = 900.dp),
                )
            }
        }
    }
}

@Composable
private fun SettingsSection(
    title: String,
    content: @Composable () -> Unit,
) {
    Column(
        modifier = Modifier.padding(horizontal = 16.dp).fillMaxWidth().widthIn(max = 900.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Text(title, color = SentinelCyan)
        SentinelPanel(Modifier.fillMaxWidth()) {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                content()
            }
        }
    }
}

@Composable
private fun SettingsRow(label: String, value: String) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        Text(label, color = SentinelTextDim)
        Spacer(Modifier.weight(1f))
        Text(value, color = SentinelText)
    }
}

@Composable
private fun DiagnosticRow(entry: DiagnosticEntry) {
    Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
        Row {
            Text(
                entry.level.name,
                color = if (entry.level == DiagnosticLevel.ERROR) SentinelRed else SentinelCyan,
            )
            Spacer(Modifier.weight(1f))
            Text(entry.timestamp.shortTime(), color = SentinelTextDim)
        }
        SelectionContainer {
            Text(entry.message, color = SentinelText)
        }
        HorizontalDivider(color = SentinelTextDim.copy(alpha = 0.25f))
    }
}
