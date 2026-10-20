package com.sentinelcore.android.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowForward
import androidx.compose.material.icons.filled.Lan
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.net.DiscoveredServer
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.components.BrandedCard
import com.sentinelcore.android.ui.components.ConnectionBadge
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelBackground
import com.sentinelcore.android.ui.theme.SentinelGreen

@Composable
fun ServerSetupScreen(
    initialAddress: String,
    discoveredServers: List<DiscoveredServer> = emptyList(),
    onConnect: (String) -> Unit,
) {
    var address by rememberSaveable(initialAddress) { mutableStateOf(initialAddress) }
    val canConnect = address.isNotBlank()
    val uriHandler = LocalUriHandler.current

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(SentinelBackground)
            .verticalScroll(rememberScrollState())
            .padding(24.dp),
        contentAlignment = Alignment.Center,
    ) {
        BrandedCard(Modifier.fillMaxWidth().widthIn(max = 560.dp)) {
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
                    fontFamily = FontFamily.Monospace,
                )
                // What someone who just found the app in a store needs before an address field
                // means anything. The link is to the project's site (downloads and docs, no
                // prices) - never a purchase.
                Text("Welcome to Sentinel Core", color = SentinelGreen, fontWeight = FontWeight.Bold)
                Text(
                    "This app connects to Sentinel Core, a home hub that runs on a computer in your own " +
                        "home: AI camera alerts, the family organizer, the alarm, movies, music and weather. " +
                        "Your footage and data stay on that computer; this app is how you reach it.",
                )
                Text(
                    "Don't have a server yet? Install it on a Linux PC first, then come back here.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                TextButton(onClick = { uriHandler.openUri("https://nr5p.com") }) {
                    Text("Set up a server at nr5p.com", color = SentinelGreen)
                }
                Text("Connect to your server", color = SentinelGreen, fontWeight = FontWeight.Bold)

                // An assist on top of manual entry below, which always stays visible - discovery
                // can legitimately find nothing (client/AP isolation, a different VLAN, ...), and
                // that must never block getting connected by typing the address instead.
                if (discoveredServers.isEmpty()) {
                    Text(
                        "Searching your local network for a server…",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                } else {
                    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text(
                            "Found on your network",
                            style = MaterialTheme.typography.labelLarge,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                        discoveredServers.forEach { server ->
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clip(RoundedCornerShape(8.dp))
                                    .background(SentinelGreen.copy(alpha = 0.08f))
                                    .clickable {
                                        address = server.addressString
                                        onConnect(server.addressString)
                                    }
                                    .padding(10.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                Column {
                                    Text(server.name)
                                    Text(
                                        server.addressString,
                                        style = MaterialTheme.typography.bodySmall,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                                    )
                                }
                                Icon(
                                    Icons.AutoMirrored.Filled.ArrowForward,
                                    contentDescription = null,
                                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                        }
                    }
                }

                Text(
                    "Or enter its address: the one on your home network, like http://192.168.1.50:3320, " +
                        "or a web address such as https://home.example.com.",
                )
                OutlinedTextField(
                    value = address,
                    onValueChange = { address = it },
                    modifier = Modifier.fillMaxWidth(),
                    label = { Text("Server address") },
                    placeholder = { Text("http://192.168.1.50:3320") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(
                        keyboardType = KeyboardType.Uri,
                        imeAction = ImeAction.Go,
                    ),
                    keyboardActions = KeyboardActions(
                        onGo = { if (canConnect) onConnect(address) },
                    ),
                )
                Button(
                    onClick = { onConnect(address) },
                    modifier = Modifier.fillMaxWidth(),
                    enabled = canConnect,
                ) {
                    Text("Connect")
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
        // Username trimmed, password never: a phone keyboard that leaves a trailing space behind
        // shouldn't come back as "invalid username or password". The server trims too, but this
        // also covers a phone talking to a server that hasn't been updated yet.
        onLogin(username.trim(), password) { localError = it }
    }

    Box(
        modifier = Modifier.fillMaxSize().background(SentinelBackground).padding(24.dp),
        contentAlignment = Alignment.Center,
    ) {
        BrandedCard(Modifier.fillMaxWidth().widthIn(max = 520.dp)) {
            Column(verticalArrangement = Arrangement.spacedBy(14.dp)) {
                Icon(Icons.Default.Lock, contentDescription = null, tint = SentinelGreen)
                Text(
                    "Sign in required",
                    color = SentinelGreen,
                    style = MaterialTheme.typography.titleLarge,
                )
                ConnectionBadge(state.connectionState)
                OutlinedTextField(
                    value = username,
                    onValueChange = { username = it },
                    modifier = Modifier.fillMaxWidth(),
                    label = { Text("Username") },
                    singleLine = true,
                    // No autocapitalize and no autocorrect - a username is not a sentence, and
                    // letting the IME "help" here is how a correct username turns into a
                    // rejected one.
                    keyboardOptions = KeyboardOptions(
                        capitalization = KeyboardCapitalization.None,
                        autoCorrectEnabled = false,
                        imeAction = ImeAction.Next,
                    ),
                )
                OutlinedTextField(
                    value = password,
                    onValueChange = { password = it },
                    modifier = Modifier.fillMaxWidth(),
                    label = { Text("Password") },
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
                            color = MaterialTheme.colorScheme.onPrimary,
                            strokeWidth = 2.dp,
                        )
                        Spacer(Modifier.height(4.dp))
                    }
                    Text("Sign In")
                }
            }
        }
    }
}
