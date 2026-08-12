package com.sentinelcore.android.ui.screens

import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import kotlinx.coroutines.launch
import java.nio.charset.StandardCharsets

// Installing the .lic file emailed after purchase, and saying whether one is installed - the web
// dashboard's LICENSE section, which had no counterpart on Android until now.
//
// Nothing here is urgent by design: an unlicensed install keeps every feature and only shows a
// watermark, so this screen states the position rather than nagging.
@Composable
fun LicenseAdminScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onBack: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    val context = LocalContext.current

    var licensed by remember { mutableStateOf<Boolean?>(null) }
    var customerName by remember { mutableStateOf<String?>(null) }
    var statusMessage by remember { mutableStateOf<String?>(null) }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    var isInstalling by remember { mutableStateOf(false) }

    suspend fun refresh() {
        runCatching { model.licenseStatus() }
            .onSuccess { (valid, name) ->
                licensed = valid
                customerName = name
            }
            .onFailure { errorMessage = it.message ?: "Could not check the license." }
    }

    LaunchedEffect(Unit) { refresh() }

    // Any file type: a .lic arriving through Gmail or Drive often has no declared MIME type at
    // all, and filtering would grey out the very file the user came here to pick.
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri: Uri? ->
        if (uri == null) return@rememberLauncherForActivityResult
        scope.launch {
            statusMessage = null
            errorMessage = null
            isInstalling = true
            try {
                val contents = context.contentResolver.openInputStream(uri)?.use {
                    it.readBytes().toString(StandardCharsets.UTF_8)
                }
                if (contents.isNullOrBlank()) {
                    errorMessage = "That file is empty or could not be read."
                } else {
                    val (valid, name) = model.installLicense(contents)
                    licensed = valid
                    customerName = name
                    statusMessage = name?.let { "Licensed to $it." } ?: "License installed."
                }
            } catch (error: Exception) {
                errorMessage = error.message ?: "Could not install that file."
            } finally {
                isInstalling = false
            }
        }
    }

    Column(Modifier.fillMaxSize()) {
        ScreenHeader("License", state)
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
                            when (licensed) {
                                null -> "Checking license…"
                                true -> "Licensed"
                                false -> "Unlicensed"
                            },
                            style = MaterialTheme.typography.titleSmall,
                            color = SentinelGreen,
                        )
                        customerName?.let { Text("Licensed to $it", style = MaterialTheme.typography.bodyMedium) }
                        Text(
                            "Installs the licence file you were emailed after purchase. Missing or invalid " +
                                "shows a notice and a watermark, and nothing else — every feature keeps working " +
                                "either way, so this is never urgent.",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }

            item {
                Button(
                    onClick = { picker.launch(arrayOf("*/*")) },
                    enabled = !isInstalling,
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp),
                ) {
                    Text(if (isInstalling) "Installing…" else "Choose License File…")
                }
            }

            statusMessage?.let { message ->
                item {
                    Text(
                        message,
                        color = SentinelGreen,
                        modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp),
                    )
                }
            }
            errorMessage?.let { message ->
                item {
                    Text(
                        message,
                        color = SentinelRed,
                        modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp),
                    )
                }
            }
        }
    }
}
