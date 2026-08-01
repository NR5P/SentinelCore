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
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.BuildConfig
import com.sentinelcore.android.data.DiagnosticEntry
import com.sentinelcore.android.data.DiagnosticLevel
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.components.ConnectionBadge
import com.sentinelcore.android.ui.components.shortTime
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import kotlinx.coroutines.launch

@Composable
fun SettingsScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onManageCameras: () -> Unit,
    onManageAccounts: () -> Unit,
    onManageKnownFaces: () -> Unit,
    onServerSettings: () -> Unit,
) {
    var address by rememberSaveable { mutableStateOf(state.serverUrl) }
    var remoteAddress by rememberSaveable { mutableStateOf(state.remoteServerUrl) }
    var showDiagnostics by rememberSaveable { mutableStateOf(false) }
    var showChangePassword by rememberSaveable { mutableStateOf(false) }
    val notificationPermissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) {
        model.refreshNotificationPermission()
        if (it) model.enablePushNotifications()
    }
    LaunchedEffect(state.serverUrl) { address = state.serverUrl }
    LaunchedEffect(state.remoteServerUrl) { remoteAddress = state.remoteServerUrl }
    LaunchedEffect(Unit) { model.refreshNotificationPermission() }
    LaunchedEffect(Unit) { model.refreshSystemStatus() }

    Column(Modifier.fillMaxSize()) {
        ScreenHeader("Settings", state)
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            item {
                SettingsSection("Server") {
                    OutlinedTextField(
                        value = address,
                        onValueChange = { address = it },
                        modifier = Modifier.fillMaxWidth(),
                        label = { Text("Dashboard address") },
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                        singleLine = true,
                    )
                    OutlinedTextField(
                        value = remoteAddress,
                        onValueChange = { remoteAddress = it },
                        modifier = Modifier.fillMaxWidth(),
                        label = { Text("Remote address (optional)") },
                        placeholder = { Text("https://your-tunnel.example.com") },
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                        singleLine = true,
                    )
                    Row(
                        Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Text("Status")
                        Spacer(Modifier.weight(1f))
                        if (state.hasRemoteServerUrl) {
                            Text(
                                if (state.isUsingRemote) "via Remote" else "via Local",
                                style = MaterialTheme.typography.labelSmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier = Modifier.padding(end = 8.dp),
                            )
                        }
                        ConnectionBadge(state.connectionState)
                    }
                    Text(
                        "A Cloudflare Tunnel (or any other externally-reachable) address to use " +
                            "when the local address can't be reached, such as away from home on cellular. " +
                            "Point it at the server's Internet Exposed port, not the normal dashboard port — " +
                            "that port always requires a login, while the normal one trusts the local network.",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    Button(
                        onClick = { model.saveServers(address, remoteAddress) },
                        enabled = address.isNotBlank() && !state.isLoading,
                        modifier = Modifier.fillMaxWidth(),
                    ) {
                        Text("Save and Reconnect")
                    }
                }
            }

            item {
                SettingsSection("Account") {
                    state.authStatus?.username?.let {
                        SettingsRow("Signed in as", it)
                    }
                    state.authStatus?.role?.let {
                        SettingsRow("Role", it.replaceFirstChar(Char::uppercase))
                    }
                    if (state.authStatus?.enabled == true) {
                        OutlinedButton(
                            onClick = { showChangePassword = true },
                            modifier = Modifier.fillMaxWidth(),
                        ) {
                            Text("Change Password")
                        }
                        OutlinedButton(
                            onClick = model::signOut,
                            modifier = Modifier.fillMaxWidth(),
                        ) {
                            Text("Sign Out", color = SentinelRed)
                        }
                    } else {
                        Text(
                            "Dashboard login is not enabled.",
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }

            // Camera CRUD, discovery, and trigger-zone configuration are all AdminOnly on the
            // server - hidden entirely for a Viewer rather than shown-disabled, same convention
            // as the Send Test Notification button below.
            if (state.isAdmin) {
                item {
                    SettingsSection("Cameras") {
                        TextButton(
                            onClick = onManageCameras,
                            modifier = Modifier.fillMaxWidth(),
                        ) {
                            Text("Manage Cameras", color = SentinelGreen, modifier = Modifier.weight(1f))
                        }
                    }
                }
                item {
                    SettingsSection("Accounts") {
                        TextButton(
                            onClick = onManageAccounts,
                            modifier = Modifier.fillMaxWidth(),
                        ) {
                            Text("Manage Accounts", color = SentinelGreen, modifier = Modifier.weight(1f))
                        }
                    }
                }
                item {
                    SettingsSection("Known Faces") {
                        TextButton(
                            onClick = onManageKnownFaces,
                            modifier = Modifier.fillMaxWidth(),
                        ) {
                            Text("Manage Known Faces", color = SentinelGreen, modifier = Modifier.weight(1f))
                        }
                    }
                }
                item {
                    SettingsSection("Server Configuration") {
                        TextButton(
                            onClick = onServerSettings,
                            modifier = Modifier.fillMaxWidth(),
                        ) {
                            Text("Server Settings", color = SentinelGreen, modifier = Modifier.weight(1f))
                        }
                        Text(
                            "Every server setting the web dashboard has — alerts, AI backends, MQTT, Device Hub, and more.",
                            style = MaterialTheme.typography.labelSmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }

            item {
                SettingsSection("Server health") {
                    state.systemStatus?.let { status ->
                        SettingsRow(
                            "CPU",
                            "${status.cpuUsagePercent.toInt()}% of ${status.processorCount} core${if (status.processorCount == 1) "" else "s"}",
                        )
                        UsageBar(status.cpuUsagePercent / 100.0)
                        SettingsRow(
                            "Memory",
                            "${formatBytes(status.memoryUsedBytes)} / ${formatBytes(status.memoryTotalBytes)}",
                        )
                        UsageBar(status.memoryUsedFraction)
                        SettingsRow(
                            "Disk",
                            "${formatBytes(status.diskUsedBytes)} / ${formatBytes(status.diskTotalBytes)}",
                        )
                        UsageBar(status.diskUsedFraction)
                        SettingsRow("Analysis queue", "${status.queuedAlerts} pending")
                        SettingsRow(
                            "Vision backend",
                            if (status.visionBackendHealthy) "Reachable" else "Unreachable",
                        )
                        SettingsRow("Uptime", formatUptime(status.uptimeSeconds))
                    } ?: Text("Could not load server status.", color = MaterialTheme.colorScheme.onSurfaceVariant)
                    OutlinedButton(
                        onClick = model::refreshSystemStatus,
                        modifier = Modifier.fillMaxWidth(),
                    ) {
                        Text("Refresh")
                    }
                }
            }

            item {
                SettingsSection("System") {
                    SettingsRow("Events loaded", state.events.size.toString())
                    SettingsRow("Cameras", state.cameras.size.toString())
                    SettingsRow("Motion triggers", state.triggers.size.toString())
                    SettingsRow("App version", BuildConfig.VERSION_NAME)
                    state.lastError?.let { Text(it, color = SentinelRed) }
                }
            }

            item {
                SettingsSection("Push notifications") {
                    SettingsRow("Permission", state.pushPermission.lowercase().replaceFirstChar(Char::uppercase))
                    state.pushStatus?.let { status ->
                        SettingsRow("Core", if (status.enabled) "Enabled" else "Disabled")
                        SettingsRow(
                            "Firebase provider",
                            if (status.firebaseConfigured) "Configured" else "Not configured",
                        )
                        SettingsRow("Registered devices", status.registeredDevices.toString())
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
                                "Refresh Registration"
                            } else {
                                "Enable Notifications"
                            },
                        )
                    }
                    if (state.pushRegistrationEnabled) {
                        OutlinedButton(
                            onClick = model::disablePushNotifications,
                            modifier = Modifier.fillMaxWidth(),
                        ) {
                            Text("Disable on This Device")
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
                            Text("Send Test Notification")
                        }
                    }
                    state.pushMessage?.let {
                        Text(it, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }

            item {
                SettingsSection("Diagnostics") {
                    TextButton(
                        onClick = { showDiagnostics = !showDiagnostics },
                        modifier = Modifier.fillMaxWidth(),
                    ) {
                        Text(
                            "Connection Log",
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
                                Text(
                                    "No requests recorded yet.",
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            } else {
                                state.diagnostics.take(30).forEach { DiagnosticRow(it) }
                            }
                            TextButton(onClick = model::clearDiagnostics) {
                                Text("Clear Log")
                            }
                        }
                    }
                    Text(
                        "Request bodies, passwords, cookies, and server response contents are never written here.",
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }

            item {
                Text(
                    "This native client manages its connection and push registration locally. " +
                        "Full server configuration remains in the Core dashboard.",
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(horizontal = 24.dp, vertical = 8.dp)
                        .fillMaxWidth()
                        .widthIn(max = 900.dp),
                )
            }
        }
    }

    if (showChangePassword) {
        ChangePasswordDialog(model = model, onDismiss = { showChangePassword = false })
    }
}

@Composable
private fun ChangePasswordDialog(model: SentinelViewModel, onDismiss: () -> Unit) {
    var currentPassword by rememberSaveable { mutableStateOf("") }
    var newPassword by rememberSaveable { mutableStateOf("") }
    var confirmPassword by rememberSaveable { mutableStateOf("") }
    var isSaving by remember { mutableStateOf(false) }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    val mismatch = confirmPassword.isNotEmpty() && newPassword != confirmPassword
    val canSave = currentPassword.isNotEmpty() && newPassword.length >= 8 && newPassword == confirmPassword

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Change Password") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                OutlinedTextField(
                    value = currentPassword,
                    onValueChange = { currentPassword = it },
                    label = { Text("Current password") },
                    visualTransformation = PasswordVisualTransformation(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = newPassword,
                    onValueChange = { newPassword = it },
                    label = { Text("New password (min. 8 characters)") },
                    visualTransformation = PasswordVisualTransformation(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = confirmPassword,
                    onValueChange = { confirmPassword = it },
                    label = { Text("Confirm new password") },
                    visualTransformation = PasswordVisualTransformation(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                if (mismatch) {
                    Text("New passwords don't match.", color = SentinelRed)
                }
                errorMessage?.let { Text(it, color = SentinelRed) }
            }
        },
        confirmButton = {
            TextButton(
                enabled = canSave && !isSaving,
                onClick = {
                    scope.launch {
                        isSaving = true
                        errorMessage = null
                        try {
                            model.changeMyPassword(currentPassword, newPassword)
                            onDismiss()
                        } catch (error: Exception) {
                            errorMessage = error.message ?: "Could not change the password."
                        } finally {
                            isSaving = false
                        }
                    }
                },
            ) {
                Text(if (isSaving) "Saving…" else "Save")
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text("Cancel") }
        },
    )
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
        Card(Modifier.fillMaxWidth()) {
            Column(
                modifier = Modifier.padding(14.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                content()
            }
        }
    }
}

@Composable
private fun SettingsRow(label: String, value: String) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        Text(label, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.weight(1f))
        Text(value)
    }
}

@Composable
private fun UsageBar(fraction: Double) {
    val clamped = fraction.coerceIn(0.0, 1.0)
    val color = when {
        clamped >= 0.9 -> SentinelRed
        clamped >= 0.7 -> SentinelAmber
        else -> SentinelGreen
    }
    LinearProgressIndicator(
        progress = { clamped.toFloat() },
        modifier = Modifier.fillMaxWidth(),
        color = color,
    )
}

private fun formatBytes(bytes: Long): String {
    if (bytes <= 0) return "0 B"
    val units = arrayOf("B", "KB", "MB", "GB", "TB")
    var value = bytes.toDouble()
    var unit = 0
    while (value >= 1024 && unit < units.size - 1) {
        value /= 1024
        unit++
    }
    return if (unit == 0) "${value.toInt()} ${units[unit]}" else "%.1f %s".format(value, units[unit])
}

private fun formatUptime(seconds: Double): String {
    val totalMinutes = (seconds / 60).toInt()
    val days = totalMinutes / 1440
    val hours = (totalMinutes % 1440) / 60
    val minutes = totalMinutes % 60
    return when {
        days > 0 -> "${days}d ${hours}h"
        hours > 0 -> "${hours}h ${minutes}m"
        else -> "${minutes}m"
    }
}

// Monospace is kept here on purpose - this is genuinely tabular/log content, unlike the rest of
// the screen's now-system-font text (see Theme.kt for why the app no longer forces it globally).
@Composable
private fun DiagnosticRow(entry: DiagnosticEntry) {
    Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
        Row {
            Text(
                entry.level.name,
                color = if (entry.level == DiagnosticLevel.ERROR) SentinelRed else SentinelCyan,
                fontFamily = FontFamily.Monospace,
            )
            Spacer(Modifier.weight(1f))
            Text(
                entry.timestamp.shortTime(),
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                fontFamily = FontFamily.Monospace,
            )
        }
        SelectionContainer {
            Text(entry.message, fontFamily = FontFamily.Monospace)
        }
        HorizontalDivider(color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.25f))
    }
}
