package com.sentinelcore.android.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Lan
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.components.ConnectionBadge
import com.sentinelcore.android.ui.components.SentinelPanel
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelBackground
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelText
import com.sentinelcore.android.ui.theme.SentinelTextDim

@Composable
fun ServerSetupScreen(
    initialAddress: String,
    onConnect: (String) -> Unit,
) {
    var address by rememberSaveable(initialAddress) { mutableStateOf(initialAddress) }
    val canConnect = address.isNotBlank()

    Box(
        modifier = Modifier.fillMaxSize().background(SentinelBackground).padding(24.dp),
        contentAlignment = Alignment.Center,
    ) {
        SentinelPanel(Modifier.fillMaxWidth().widthIn(max = 560.dp)) {
            Column(
                verticalArrangement = Arrangement.spacedBy(16.dp),
                horizontalAlignment = Alignment.Start,
            ) {
                Icon(Icons.Default.Lan, contentDescription = null, tint = SentinelGreen)
                Text(
                    ">_ SENTINEL_CORE",
                    color = SentinelGreen,
                    style = MaterialTheme.typography.headlineSmall,
                    fontWeight = FontWeight.Bold,
                )
                Text(
                    "Connect this device to the Sentinel Core server on your local network.",
                    color = SentinelText,
                )
                OutlinedTextField(
                    value = address,
                    onValueChange = { address = it },
                    modifier = Modifier.fillMaxWidth(),
                    label = { Text("SERVER ADDRESS") },
                    placeholder = { Text("http://192.168.50.8:3320") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(
                        keyboardType = KeyboardType.Uri,
                        imeAction = ImeAction.Go,
                    ),
                    keyboardActions = KeyboardActions(
                        onGo = { if (canConnect) onConnect(address) },
                    ),
                )
                Text(
                    "Your Android device and server must be on the same network.",
                    color = SentinelTextDim,
                    style = MaterialTheme.typography.bodySmall,
                )
                Button(
                    onClick = { onConnect(address) },
                    modifier = Modifier.fillMaxWidth(),
                    enabled = canConnect,
                ) {
                    Text("ESTABLISH LINK")
                }
            }
        }
    }
}

@Composable
fun LoginScreen(
    state: SentinelUiState,
    onLogin: (String, String, (String?) -> Unit) -> Unit,
) {
    var username by rememberSaveable { mutableStateOf("") }
    var password by rememberSaveable { mutableStateOf("") }
    var localError by remember { mutableStateOf<String?>(null) }
    val canSubmit = username.isNotBlank() && password.isNotBlank() && !state.isLoading

    fun submit() {
        if (!canSubmit) return
        localError = null
        onLogin(username, password) { localError = it }
    }

    Box(
        modifier = Modifier.fillMaxSize().background(SentinelBackground).padding(24.dp),
        contentAlignment = Alignment.Center,
    ) {
        SentinelPanel(Modifier.fillMaxWidth().widthIn(max = 520.dp)) {
            Column(verticalArrangement = Arrangement.spacedBy(14.dp)) {
                Icon(Icons.Default.Lock, contentDescription = null, tint = SentinelGreen)
                Text(
                    "AUTHENTICATION_REQUIRED",
                    color = SentinelGreen,
                    style = MaterialTheme.typography.titleLarge,
                )
                ConnectionBadge(state.connectionState)
                OutlinedTextField(
                    value = username,
                    onValueChange = { username = it },
                    modifier = Modifier.fillMaxWidth(),
                    label = { Text("USERNAME") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(imeAction = ImeAction.Next),
                )
                OutlinedTextField(
                    value = password,
                    onValueChange = { password = it },
                    modifier = Modifier.fillMaxWidth(),
                    label = { Text("PASSWORD") },
                    singleLine = true,
                    visualTransformation = PasswordVisualTransformation(),
                    keyboardOptions = KeyboardOptions(
                        keyboardType = KeyboardType.Password,
                        imeAction = ImeAction.Go,
                    ),
                    keyboardActions = KeyboardActions(onGo = { submit() }),
                )
                (localError ?: state.lastError)?.let { message ->
                    Text(message, color = SentinelAmber, style = MaterialTheme.typography.bodySmall)
                }
                Button(
                    onClick = { submit() },
                    enabled = canSubmit,
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    if (state.isLoading) {
                        CircularProgressIndicator(
                            modifier = Modifier.height(18.dp),
                            color = Color.Black,
                            strokeWidth = 2.dp,
                        )
                        Spacer(Modifier.height(4.dp))
                    }
                    Text("SIGN IN")
                }
            }
        }
    }
}
