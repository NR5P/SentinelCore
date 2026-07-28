package com.sentinelcore.android.ui.screens

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.data.ScanInterface
import com.sentinelcore.android.data.ScanResultCamera
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

@Composable
fun CameraDiscoveryScreen(
    model: SentinelViewModel,
    onBack: () -> Unit,
    onResultSelected: (ScanResultCamera) -> Unit,
) {
    var interfaces by remember { mutableStateOf<List<ScanInterface>>(emptyList()) }
    var selectedAddresses by remember { mutableStateOf<Set<String>>(emptySet()) }
    var isScanning by remember { mutableStateOf(false) }
    var results by remember { mutableStateOf<List<ScanResultCamera>>(emptyList()) }
    var scanStatus by remember { mutableStateOf("Running") }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    var pollJob by remember { mutableStateOf<Job?>(null) }
    val scope = rememberCoroutineScope()

    LaunchedEffect(Unit) {
        try {
            interfaces = model.fetchScanInterfaces()
            selectedAddresses = interfaces.filterNot { it.looksVirtual }.map { it.address }.toSet()
        } catch (error: Exception) {
            errorMessage = error.message ?: "Could not load network interfaces."
        }
    }

    // The scan job only lives 5 minutes server-side - stop polling once we leave this screen
    // rather than leaking a loop that outlives it (LaunchedEffect/DisposableEffect handle the
    // cancellation automatically here, unlike iOS which needs an explicit onDisappear cancel).
    DisposableEffect(Unit) {
        onDispose { pollJob?.cancel() }
    }

    fun startScan() {
        errorMessage = null
        scope.launch {
            try {
                val scanId = model.startScan(selectedAddresses.toList())
                isScanning = true
                results = emptyList()
                scanStatus = "Running"
                pollJob?.cancel()
                pollJob = scope.launch {
                    while (true) {
                        try {
                            val snapshot = model.fetchScanStatus(scanId)
                            results = snapshot.results
                            scanStatus = snapshot.status
                            if (snapshot.status == "Completed") break
                        } catch (error: Exception) {
                            errorMessage = error.message ?: "Scan failed."
                            break
                        }
                        delay(1_000)
                    }
                }
            } catch (error: Exception) {
                errorMessage = error.message ?: "Could not start a scan."
            }
        }
    }

    Column(Modifier.fillMaxSize()) {
        DetailHeader("Discover Cameras", onBack)
        errorMessage?.let {
            Text(it, color = SentinelRed, modifier = Modifier.padding(16.dp))
        }
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            contentPadding = PaddingValues(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            if (!isScanning) {
                items(interfaces, key = ScanInterface::address) { iface ->
                    Card(
                        Modifier.fillMaxWidth().clickable {
                            selectedAddresses = if (iface.address in selectedAddresses) {
                                selectedAddresses - iface.address
                            } else {
                                selectedAddresses + iface.address
                            }
                        },
                    ) {
                        Row(
                            modifier = Modifier.padding(14.dp).fillMaxWidth(),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Checkbox(
                                checked = iface.address in selectedAddresses,
                                onCheckedChange = {
                                    selectedAddresses = if (it) {
                                        selectedAddresses + iface.address
                                    } else {
                                        selectedAddresses - iface.address
                                    }
                                },
                            )
                            Column(Modifier.weight(1f)) {
                                Text(iface.name)
                                Text(
                                    iface.description,
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                        }
                    }
                }
                item {
                    Button(
                        onClick = ::startScan,
                        enabled = selectedAddresses.isNotEmpty(),
                        modifier = Modifier.fillMaxWidth(),
                    ) {
                        Text("Scan")
                    }
                }
            } else {
                item {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        CircularProgressIndicator(Modifier.size(20.dp))
                        Spacer(Modifier.width(12.dp))
                        Text(if (scanStatus == "Completed") "Scan complete" else "Scanning…")
                    }
                }
                if (results.isEmpty()) {
                    item {
                        Text(
                            if (scanStatus == "Completed") "No cameras found." else "Looking for cameras…",
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
                items(results, key = ScanResultCamera::ipAddress) { result ->
                    Card(
                        modifier = Modifier.fillMaxWidth()
                            .let { base -> if (result.alreadySaved) base else base.clickable { onResultSelected(result) } },
                    ) {
                        Row(
                            modifier = Modifier.padding(14.dp).fillMaxWidth(),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Column(Modifier.weight(1f)) {
                                Row(verticalAlignment = Alignment.CenterVertically) {
                                    Text(result.suggestedName, style = MaterialTheme.typography.titleSmall)
                                    if (result.needsCredentials) {
                                        Text(
                                            "  NEEDS CREDENTIALS",
                                            color = SentinelAmber,
                                            style = MaterialTheme.typography.labelSmall,
                                        )
                                    }
                                }
                                Text(
                                    listOfNotNull(result.ipAddress, result.manufacturer, result.model)
                                        .joinToString(" · "),
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                            if (result.alreadySaved) {
                                Icon(Icons.Default.CheckCircle, contentDescription = "Already saved", tint = SentinelGreen)
                            }
                        }
                    }
                }
            }
        }
    }
}
