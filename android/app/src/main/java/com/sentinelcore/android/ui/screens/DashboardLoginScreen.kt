package com.sentinelcore.android.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
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
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import kotlinx.coroutines.launch

// Whether this server requires a login at all - the web dashboard's LOGIN section. Distinct from
// Manage Accounts, which edits the accounts once login is on: this is the switch itself, and it
// was the one part of that section the apps never had.
//
// Turning it on creates the first admin account in the same step, because a server demanding a
// login nobody could satisfy would be locked shut. Turning it off is a real loss of protection and
// says so rather than just confirming.
@Composable
fun DashboardLoginScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onBack: () -> Unit,
) {
    val scope = rememberCoroutineScope()

    var username by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var confirmPassword by remember { mutableStateOf("") }
    var isWorking by remember { mutableStateOf(false) }
    var confirmingDisable by remember { mutableStateOf(false) }
    var errorMessage by remember { mutableStateOf<String?>(null) }

    val isEnabled = state.authStatus?.enabled == true
    val canEnable = username.isNotBlank() && password.length >= 8 && password == confirmPassword

    Column(Modifier.fillMaxSize()) {
        ScreenHeader("Dashboard Login", state)
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            verticalArrangement = Arrangement.spacedBy(10.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            item {
                TextButton(onClick = onBack, modifier = Modifier.fillMaxWidth()) {
                    Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = null)
                    Text("Back to Settings", modifier = Modifier.weight(1f).padding(start = 8.dp))
                }
            }

            item {
                Card(Modifier.fillMaxWidth().padding(horizontal = 14.dp)) {
                    Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                        Text(
                            if (isEnabled) "Login required" else "No login required",
                            style = MaterialTheme.typography.titleSmall,
                            color = SentinelGreen,
                        )
                        Text(
                            if (isEnabled) {
                                "Everyone signs in to open this dashboard. Sessions live in memory, so a " +
                                    "server restart signs everyone out."
                            } else {
                                "Anyone who can reach this network can open the dashboard and change every " +
                                    "setting. That's a reasonable default on a home network you trust — turn " +
                                    "it on before this is reachable from anywhere else."
                            },
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }

            if (isEnabled) {
                item {
                    OutlinedButton(
                        onClick = { confirmingDisable = true },
                        enabled = !isWorking,
                        modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp),
                    ) { Text("Turn Off Login", color = SentinelRed) }
                }
                item {
                    Text(
                        "Accounts are kept, not deleted — turning login back on later restores them.",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp),
                    )
                }
            } else {
                item {
                    Card(Modifier.fillMaxWidth().padding(horizontal = 14.dp)) {
                        Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                            Text(
                                "Create the First Account",
                                style = MaterialTheme.typography.titleSmall,
                                color = SentinelGreen,
                            )
                            OutlinedTextField(
                                value = username,
                                onValueChange = { username = it },
                                label = { Text("Username") },
                                singleLine = true,
                                modifier = Modifier.fillMaxWidth(),
                            )
                            OutlinedTextField(
                                value = password,
                                onValueChange = { password = it },
                                label = { Text("Password (min. 8 characters)") },
                                singleLine = true,
                                visualTransformation = PasswordVisualTransformation(),
                                modifier = Modifier.fillMaxWidth(),
                            )
                            OutlinedTextField(
                                value = confirmPassword,
                                onValueChange = { confirmPassword = it },
                                label = { Text("Confirm password") },
                                singleLine = true,
                                visualTransformation = PasswordVisualTransformation(),
                                modifier = Modifier.fillMaxWidth(),
                            )
                            if (confirmPassword.isNotEmpty() && password != confirmPassword) {
                                Text("Passwords don't match.", color = SentinelRed)
                            }
                            Button(
                                onClick = {
                                    scope.launch {
                                        isWorking = true
                                        errorMessage = null
                                        try {
                                            model.enableDashboardLogin(username.trim(), password)
                                            password = ""
                                            confirmPassword = ""
                                        } catch (error: Exception) {
                                            errorMessage = error.message ?: "Could not enable login."
                                        } finally {
                                            isWorking = false
                                        }
                                    }
                                },
                                enabled = !isWorking && canEnable,
                                modifier = Modifier.fillMaxWidth(),
                            ) { Text(if (isWorking) "Working…" else "Require Login") }
                        }
                    }
                }
            }

            errorMessage?.let { message ->
                item { Text(message, color = SentinelRed, modifier = Modifier.padding(horizontal = 14.dp)) }
            }
        }
    }

    if (confirmingDisable) {
        AlertDialog(
            onDismissRequest = { confirmingDisable = false },
            title = { Text("Turn off login?") },
            text = {
                Text(
                    "Anyone on this network will then have full access to the dashboard and every setting " +
                        "in it. Accounts are kept, not deleted.",
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    confirmingDisable = false
                    scope.launch {
                        isWorking = true
                        errorMessage = null
                        try {
                            model.disableDashboardLogin()
                        } catch (error: Exception) {
                            errorMessage = error.message ?: "Could not turn off login."
                        } finally {
                            isWorking = false
                        }
                    }
                }) { Text("Turn Off Login", color = SentinelRed) }
            },
            dismissButton = { TextButton(onClick = { confirmingDisable = false }) { Text("Cancel") } },
        )
    }
}
