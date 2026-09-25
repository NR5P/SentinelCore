package com.sentinelcore.android.ui.screens

import android.content.Context
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
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
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.sentinelcore.android.data.HubDevice
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.components.EmptyState
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelBackground
import com.sentinelcore.android.ui.theme.SentinelCard
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelTextDim
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.text.DateFormat
import java.util.Date

// The Device Hub for a tablet mounted on a wall. Its own route so the navigation bar and rail
// are gone (AppShell hides them for it); every switchable device is a big tile you tap, grouped
// by room, under a clock, and the screen is kept awake while it's up. Add, edit, delete and
// rearrange stay on the Device Hub screen - a wall tablet is for using the house, not
// configuring it.
internal const val DEVICE_WALL_ROUTE = "devices/wall"

// Remembered on this device, so the app opens straight onto the wall display next time (see
// AppShell) rather than onto Events. Cleared by the wall display's own Exit. The same
// preferences file as the tab bar layout.
internal object DeviceWallPreference {
    private const val FILE = "sentinel_core"
    private const val KEY = "device_hub_wall_display"

    fun isOn(context: Context): Boolean =
        context.getSharedPreferences(FILE, Context.MODE_PRIVATE).getBoolean(KEY, false)

    fun set(context: Context, on: Boolean) {
        context.getSharedPreferences(FILE, Context.MODE_PRIVATE).edit().putBoolean(KEY, on).apply()
    }
}

@Composable
fun DeviceWallScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onExit: () -> Unit,
) {
    var errorMessage by remember { mutableStateOf<String?>(null) }
    var busyDeviceId by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    // Polls only while this destination is composed, the same as the Device Hub screen - leaving
    // cancels it.
    LaunchedEffect(Unit) {
        runCatching { model.refreshDevices() }
            .onFailure { errorMessage = it.message ?: "Could not load devices." }
        while (true) {
            delay(5_000)
            runCatching { model.refreshDeviceStates() }
        }
    }

    // A tablet that sleeps after two minutes is a black rectangle on the wall.
    val view = LocalView.current
    DisposableEffect(view) {
        view.keepScreenOn = true
        onDispose { view.keepScreenOn = false }
    }

    var now by remember { mutableStateOf(Date()) }
    LaunchedEffect(Unit) {
        while (true) {
            now = Date()
            delay(10_000)
        }
    }
    val context = LocalContext.current
    // Android's own formatter, so the clock follows the tablet's 12/24-hour setting.
    val timeFormat = remember(context) { android.text.format.DateFormat.getTimeFormat(context) }
    val dateFormat = remember { DateFormat.getDateInstance(DateFormat.FULL) }

    // The system back gesture leaves the wall display the same way Exit does.
    BackHandler(onBack = onExit)

    fun command(device: HubDevice, action: String? = null, brightness: Int? = null) {
        scope.launch {
            busyDeviceId = device.id
            try {
                val warning = model.sendDeviceCommand(device.id, action = action, brightness = brightness)
                // A momentary press has no echoed state to wait for, so "unconfirmed" is its
                // normal successful outcome.
                errorMessage = if (device.isMomentary) null else warning
            } catch (error: Exception) {
                errorMessage = error.message ?: "Could not command ${device.name}."
            } finally {
                busyDeviceId = null
            }
        }
    }

    Column(
        Modifier
            .fillMaxSize()
            .background(SentinelBackground)
            .systemBarsPadding(),
    ) {
        Row(
            Modifier
                .fillMaxWidth()
                .padding(horizontal = 18.dp, vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            Text(
                timeFormat.format(now),
                style = MaterialTheme.typography.headlineLarge,
                fontWeight = FontWeight.Bold,
                color = SentinelGreen,
            )
            Text(
                dateFormat.format(now).uppercase(),
                style = MaterialTheme.typography.labelSmall,
                color = SentinelTextDim,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )
            val warning = errorMessage
                ?: if (!state.deviceBrokerConnected) "Not connected to the MQTT broker." else null
            if (warning != null) {
                Text(
                    warning,
                    color = SentinelAmber,
                    style = MaterialTheme.typography.bodySmall,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f),
                )
            }
            // Small and in the corner on purpose, so a stray tap on a tile can't land on it.
            OutlinedButton(
                onClick = onExit,
                modifier = Modifier
                    .heightIn(min = 48.dp)
                    .semantics { contentDescription = "Leave the wall display" },
            ) { Text("Exit") }
        }

        if (state.devices.isEmpty()) {
            EmptyState(
                "No devices",
                "Tap Exit, then use discover to find what's on your broker, or add one by hand.",
                Modifier.fillMaxSize(),
            )
        } else {
            // Rooms in the order the hub lists them - the arrangement someone dragged into place
            // carries over - with the unnamed ones last.
            val rooms = remember(state.devices) {
                val seen = state.devices.map { it.room.trim() }.distinct().toMutableList()
                if (seen.remove("")) seen.add("")
                seen
            }
            LazyVerticalGrid(
                columns = GridCells.Adaptive(170.dp),
                contentPadding = PaddingValues(16.dp),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
                modifier = Modifier.fillMaxSize(),
            ) {
                rooms.forEach { room ->
                    item(key = "room:$room", span = { GridItemSpan(maxLineSpan) }) {
                        Text(
                            room.ifBlank { "Other" }.uppercase(),
                            style = MaterialTheme.typography.labelMedium,
                            color = SentinelCyan,
                            letterSpacing = 3.sp,
                            modifier = Modifier.padding(top = 8.dp),
                        )
                    }
                    items(state.devices.filter { it.room.trim() == room }, key = { it.id }) { device ->
                        WallTile(
                            device = device,
                            busy = busyDeviceId == device.id,
                            onToggle = { command(device, action = "toggle") },
                            onAction = { command(device, action = it) },
                            onBrightness = { command(device, brightness = it) },
                        )
                    }
                }
            }
        }
    }
}

// One tile. The whole tile is the switch for anything that's simply on or off - a finger on a
// wall wants a target the size of a light switch, not a toggle in a corner. Gates, locks and
// momentary devices get big buttons instead, because a tap would be ambiguous for them. The
// brightness slider is committed on release, as everywhere else, so one drag is one MQTT publish.
@Composable
private fun WallTile(
    device: HubDevice,
    busy: Boolean,
    onToggle: () -> Unit,
    onAction: (String) -> Unit,
    onBrightness: (Int) -> Unit,
) {
    val tappable = device.commandable && !device.isMomentary && !device.isCoverLike && !device.isLock
    val lit = device.isOn && device.available && device.enabled
    var sliderValue by remember(device.id) { mutableStateOf((device.brightness ?: 100).toFloat()) }
    var isDragging by remember(device.id) { mutableStateOf(false) }

    LaunchedEffect(device.brightness) {
        // Never yank the slider out from under a finger that's still on it.
        if (!isDragging) device.brightness?.let { sliderValue = it.toFloat() }
    }

    val shape = RoundedCornerShape(14.dp)
    val green = SentinelGreen
    val dim = SentinelTextDim

    Column(
        Modifier
            .fillMaxWidth()
            .heightIn(min = 150.dp)
            .clip(shape)
            .background(if (lit) green.copy(alpha = 0.12f) else SentinelCard, shape)
            .border(1.dp, if (lit) green.copy(alpha = 0.6f) else dim.copy(alpha = 0.3f), shape)
            .then(
                if (tappable) {
                    Modifier.clickable(
                        enabled = device.enabled && !busy,
                        onClickLabel = "Switch ${device.name}",
                        role = Role.Switch,
                        onClick = onToggle,
                    )
                } else {
                    Modifier
                },
            )
            .alpha(if (device.available && device.enabled) 1f else 0.55f)
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(
                imageVector = device.kindIcon(),
                contentDescription = null,
                tint = if (lit) green else dim,
                modifier = Modifier.size(32.dp),
            )
            Spacer(Modifier.weight(1f))
            if (busy) {
                CircularProgressIndicator(Modifier.size(20.dp), color = green, strokeWidth = 2.dp)
            } else {
                Text(
                    device.statusText().uppercase(),
                    style = MaterialTheme.typography.labelSmall,
                    color = device.statusColor(),
                    letterSpacing = 1.sp,
                )
            }
        }

        Text(
            device.name,
            style = MaterialTheme.typography.titleMedium,
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
        )

        // What a sensor reports, large: on a wall the readings are the whole point of it.
        if (device.measurements.isNotEmpty()) {
            Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                device.measurements.forEach { measurement ->
                    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.Bottom) {
                        Text(
                            measurement.key,
                            style = MaterialTheme.typography.bodySmall,
                            color = dim,
                            modifier = Modifier.weight(1f),
                        )
                        Text(measurement.formattedValue, style = MaterialTheme.typography.titleMedium)
                    }
                }
            }
        }

        when {
            device.commandable && device.isMomentary -> {
                Row { BigButton("Press", device.enabled && !busy) { onAction("press") } }
            }
            device.commandable && device.isCoverLike -> {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    BigButton("Open", device.enabled && !busy) { onAction("open") }
                    if (device.hasStop) BigButton("Stop", device.enabled && !busy) { onAction("stop") }
                    BigButton("Close", device.enabled && !busy) { onAction("close") }
                }
            }
            device.commandable && device.isLock -> {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    BigButton("Unlock", device.enabled && !busy) { onAction("unlock") }
                    BigButton("Lock", device.enabled && !busy) { onAction("lock") }
                }
            }
            device.commandable && device.supportsBrightness -> {
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
                    colors = SliderDefaults.colors(thumbColor = green, activeTrackColor = green),
                    modifier = Modifier.semantics { contentDescription = "${device.name} brightness" },
                )
            }
        }
    }
}

@Composable
private fun RowScope.BigButton(label: String, enabled: Boolean, onClick: () -> Unit) {
    OutlinedButton(
        onClick = onClick,
        enabled = enabled,
        modifier = Modifier
            .weight(1f)
            .heightIn(min = 52.dp),
    ) { Text(label) }
}
