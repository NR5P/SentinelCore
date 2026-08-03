package com.sentinelcore.android.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.gestures.detectDragGesturesAfterLongPress
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.Fence
import androidx.compose.material.icons.filled.RadioButtonChecked
import androidx.compose.material.icons.filled.Lightbulb
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.Power
import androidx.compose.material.icons.filled.Sensors
import androidx.compose.material.icons.filled.Toys
import androidx.compose.material.icons.filled.Wifi
import androidx.compose.material.icons.filled.WindPower
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.zIndex
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.data.HubDevice
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.components.EmptyState
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelTextDim
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

// Lights, plugs, and anything else on the MQTT broker, switchable from the phone. Any signed-in
// account can switch a device; adding, editing, deleting, and discovery are Admin-only
// server-side, so those controls are hidden for a Viewer rather than left to fail with a 403.
@Composable
fun DeviceHubScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onAddDevice: () -> Unit,
    onEditDevice: (HubDevice) -> Unit,
    onDiscover: () -> Unit,
) {
    var isLoading by remember { mutableStateOf(false) }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    var busyDeviceId by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()
    val isAdmin = state.authStatus?.role?.equals("Admin", ignoreCase = true) != false

    suspend fun load() {
        isLoading = true
        try {
            model.refreshDevices()
            errorMessage = null
        } catch (error: Exception) {
            errorMessage = error.message ?: "Could not load devices."
        } finally {
            isLoading = false
        }
    }

    LaunchedEffect(Unit) { load() }

    // Polls only while this destination is composed - leaving the screen cancels it, so nothing
    // keeps hitting the server in the background.
    LaunchedEffect(Unit) {
        while (true) {
            delay(5_000)
            runCatching { model.refreshDeviceStates() }
        }
    }

    Column(Modifier.fillMaxSize()) {
        ScreenHeader("Device Hub", state, onRefresh = { scope.launch { load() } }) {
            if (isAdmin) {
                IconButton(onClick = onDiscover) {
                    Icon(Icons.Default.Wifi, contentDescription = "Discover devices")
                }
                IconButton(onClick = onAddDevice) {
                    Icon(Icons.Default.Add, contentDescription = "Add device")
                }
            }
        }

        if (isLoading) {
            LinearProgressIndicator(Modifier.fillMaxWidth(), color = SentinelGreen)
        }

        if (!state.deviceBrokerConnected) {
            Text(
                "Not connected to the MQTT broker - commands won't reach anything.",
                color = SentinelAmber,
                style = MaterialTheme.typography.bodySmall,
                modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
            )
        }

        errorMessage?.let {
            Text(it, color = SentinelRed, modifier = Modifier.padding(16.dp))
        }

        if (state.devices.isEmpty() && !isLoading) {
            EmptyState(
                "No devices",
                if (isAdmin) {
                    "Use discover to find what's already on your MQTT broker, or add one by hand."
                } else {
                    "No devices have been added yet. An admin account can add them."
                },
                Modifier.fillMaxSize(),
            )
        } else {
            // Long-press-and-drag to reorder (admins only - the order is saved server-side).
            // Incremental swap: once the dragged card travels past most of a neighbour, the two
            // trade places and the accumulated offset resets, so the card follows the finger.
            val listState = rememberLazyListState()
            var draggingId by remember { mutableStateOf<String?>(null) }
            var dragOffset by remember { mutableStateOf(0f) }

            LazyColumn(
                state = listState,
                contentPadding = PaddingValues(16.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                itemsIndexed(state.devices, key = { _, device -> device.id }) { _, device ->
                    val isDragging = draggingId == device.id
                    val dragModifier = Modifier
                        .zIndex(if (isDragging) 1f else 0f)
                        .graphicsLayer { translationY = if (isDragging) dragOffset else 0f }
                        .pointerInput(device.id, isAdmin) {
                            if (!isAdmin) return@pointerInput
                            detectDragGesturesAfterLongPress(
                                onDragStart = {
                                    draggingId = device.id
                                    dragOffset = 0f
                                },
                                onDrag = { change, amount ->
                                    change.consume()
                                    dragOffset += amount.y
                                    val current = state.devices.indexOfFirst { it.id == device.id }
                                    if (current < 0) return@detectDragGesturesAfterLongPress
                                    val itemHeight = listState.layoutInfo.visibleItemsInfo
                                        .firstOrNull { it.key == device.id }?.size ?: return@detectDragGesturesAfterLongPress
                                    val step = itemHeight * 0.66f
                                    if (dragOffset > step && current < state.devices.lastIndex) {
                                        model.moveDevice(current, current + 1)
                                        dragOffset -= itemHeight
                                    } else if (dragOffset < -step && current > 0) {
                                        model.moveDevice(current, current - 1)
                                        dragOffset += itemHeight
                                    }
                                },
                                onDragEnd = {
                                    draggingId = null
                                    dragOffset = 0f
                                },
                                onDragCancel = {
                                    draggingId = null
                                    dragOffset = 0f
                                },
                            )
                        }

                    DeviceCard(
                        modifier = dragModifier,
                        device = device,
                        isBusy = busyDeviceId == device.id,
                        isAdmin = isAdmin,
                        onToggle = {
                            scope.launch {
                                busyDeviceId = device.id
                                try {
                                    errorMessage = model.sendDeviceCommand(device.id, action = "toggle")
                                } catch (error: Exception) {
                                    errorMessage = error.message ?: "Could not switch ${device.name}."
                                } finally {
                                    busyDeviceId = null
                                }
                            }
                        },
                        onAction = { action ->
                            scope.launch {
                                busyDeviceId = device.id
                                try {
                                    val warning = model.sendDeviceCommand(device.id, action = action)
                                    // A momentary press has no echoed state to wait for, so
                                    // "unconfirmed" is its normal successful outcome.
                                    errorMessage = if (device.isMomentary) null else warning
                                } catch (error: Exception) {
                                    errorMessage = error.message ?: "Could not command ${device.name}."
                                } finally {
                                    busyDeviceId = null
                                }
                            }
                        },
                        onBrightness = { percent ->
                            scope.launch {
                                busyDeviceId = device.id
                                try {
                                    errorMessage = model.sendDeviceCommand(device.id, brightness = percent)
                                } catch (error: Exception) {
                                    errorMessage = error.message ?: "Could not dim ${device.name}."
                                } finally {
                                    busyDeviceId = null
                                }
                            }
                        },
                        onEdit = { onEditDevice(device) },
                        onDelete = {
                            scope.launch {
                                try {
                                    model.deleteDevice(device)
                                } catch (error: Exception) {
                                    errorMessage = error.message ?: "Could not remove ${device.name}."
                                }
                            }
                        },
                    )
                }
            }
        }
    }
}

@Composable
private fun DeviceCard(
    modifier: Modifier = Modifier,
    device: HubDevice,
    isBusy: Boolean,
    isAdmin: Boolean,
    onToggle: () -> Unit,
    onAction: (String) -> Unit,
    onBrightness: (Int) -> Unit,
    onEdit: () -> Unit,
    onDelete: () -> Unit,
) {
    // Local so dragging is smooth and doesn't fight the five-second state poll; committed to the
    // device only on release, so one drag isn't dozens of MQTT publishes to a real bulb.
    var sliderValue by remember(device.id) { mutableStateOf((device.brightness ?: 100).toFloat()) }
    var isDragging by remember(device.id) { mutableStateOf(false) }

    LaunchedEffect(device.brightness) {
        if (!isDragging) device.brightness?.let { sliderValue = it.toFloat() }
    }

    Card(modifier.fillMaxWidth()) {
        Column(
            Modifier.padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(
                    imageVector = device.kindIcon(),
                    contentDescription = null,
                    tint = if (device.isOn && device.available) SentinelGreen else SentinelTextDim,
                    modifier = Modifier.size(28.dp),
                )
                Spacer(Modifier.size(12.dp))
                Column(Modifier.weight(1f)) {
                    Text(
                        device.name,
                        style = MaterialTheme.typography.titleSmall,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    Text(
                        device.statusText(),
                        style = MaterialTheme.typography.bodySmall,
                        color = device.statusColor(),
                    )
                }
                if (isBusy) {
                    CircularProgressIndicator(Modifier.size(24.dp), color = SentinelGreen)
                } else if (device.commandable && !device.isMomentary && !device.isCoverLike && !device.isLock) {
                    Switch(
                        checked = device.isOn,
                        onCheckedChange = { onToggle() },
                        enabled = device.enabled,
                        modifier = Modifier.semantics { contentDescription = "${device.name} power" },
                    )
                }
            }

            // Kind-flavoured controls: a gate gets Open/Close (and Stop when the motor takes
            // one), a lock gets Lock/Unlock, a momentary device gets a single Press.
            if (!isBusy && device.commandable && (device.isMomentary || device.isCoverLike || device.isLock)) {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    if (device.isMomentary) {
                        Button(onClick = { onAction("press") }, enabled = device.enabled) { Text("Press") }
                    } else if (device.isCoverLike) {
                        OutlinedButton(onClick = { onAction("open") }, enabled = device.enabled) { Text("Open") }
                        if (device.hasStop) {
                            OutlinedButton(onClick = { onAction("stop") }, enabled = device.enabled) { Text("Stop") }
                        }
                        OutlinedButton(onClick = { onAction("close") }, enabled = device.enabled) { Text("Close") }
                    } else {
                        OutlinedButton(onClick = { onAction("unlock") }, enabled = device.enabled) { Text("Unlock") }
                        OutlinedButton(onClick = { onAction("lock") }, enabled = device.enabled) { Text("Lock") }
                    }
                }
            }

            if (device.room.isNotBlank()) {
                Text(
                    device.room,
                    style = MaterialTheme.typography.labelSmall,
                    color = SentinelTextDim,
                )
            }

            if (device.commandable && device.supportsBrightness) {
                Slider(
                    value = sliderValue,
                    onValueChange = {
                        isDragging = true
                        sliderValue = it
                    },
                    onValueChangeFinished = {
                        isDragging = false
                        onBrightness(sliderValue.toInt())
                    },
                    valueRange = 0f..100f,
                    enabled = device.enabled,
                    modifier = Modifier.semantics { contentDescription = "${device.name} brightness" },
                )
            }

            if (isAdmin) {
                Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                    IconButton(onClick = onEdit) {
                        Icon(Icons.Default.Edit, contentDescription = "Edit ${device.name}")
                    }
                    IconButton(onClick = onDelete) {
                        Icon(Icons.Default.Delete, contentDescription = "Remove ${device.name}", tint = SentinelRed)
                    }
                }
            }
        }
    }
}

private fun HubDevice.kindIcon(): ImageVector = when (kind) {
    "light" -> Icons.Default.Lightbulb
    "switch", "outlet" -> Icons.Default.Power
    "fan" -> Icons.Default.WindPower
    "cover" -> Icons.Default.Toys
    "gate", "garage" -> Icons.Default.Fence
    "lock" -> Icons.Default.Lock
    "button" -> Icons.Default.RadioButtonChecked
    else -> Icons.Default.Sensors
}

private fun HubDevice.statusText(): String = when {
    !enabled -> "Disabled"
    !available -> "Unavailable"
    !commandable -> "Sensor"
    // A pulse device has no on/off to report - reachable is the whole story.
    isMomentary -> "Ready"
    power == "on" && isCoverLike -> "Open"
    power == "off" && isCoverLike -> "Closed"
    power == "on" && isLock -> "Locked"
    power == "off" && isLock -> "Unlocked"
    power == "on" && supportsBrightness && brightness != null -> "On · $brightness%"
    power == "on" -> "On"
    power == "off" -> "Off"
    else -> "State unknown"
}

@Composable
private fun HubDevice.statusColor() = when {
    !enabled || !available -> SentinelAmber
    isOn -> SentinelGreen
    else -> SentinelTextDim
}
