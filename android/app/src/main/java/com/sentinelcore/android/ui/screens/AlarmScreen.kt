package com.sentinelcore.android.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.Checkbox
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.data.AlarmConfig
import com.sentinelcore.android.data.AlarmEvent
import com.sentinelcore.android.data.AlarmModes
import com.sentinelcore.android.data.AlarmSensor
import com.sentinelcore.android.data.AlarmSensorInput
import com.sentinelcore.android.data.AlarmStatus
import com.sentinelcore.android.data.AlarmZones
import com.sentinelcore.android.data.SentinelApiException
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.components.EmptyState
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelTextDim
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

// The home alarm: arm and disarm the house, see every sensor, and (for admins) set it up. The
// state machine and the sensor subscription live on the server (see AlarmEngine.cs); this screen
// polls /api/alarm/status while it is open and counts the exit and entry delays down from the
// deadlines the server hands it.
@Composable
fun AlarmScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onAddSensor: () -> Unit,
    onEditSensor: (AlarmSensor) -> Unit,
    onSettings: () -> Unit,
    onEvents: () -> Unit,
) {
    var isLoading by remember { mutableStateOf(false) }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    var clockOffsetMs by remember { mutableLongStateOf(0L) }
    var nowMs by remember { mutableLongStateOf(System.currentTimeMillis()) }
    var pinFor by remember { mutableStateOf<String?>(null) }
    var pinError by remember { mutableStateOf<String?>(null) }
    var openConfirm by remember { mutableStateOf<Pair<String, String>?>(null) }
    var confirmPanic by remember { mutableStateOf(false) }
    var sensorToDelete by remember { mutableStateOf<AlarmSensor?>(null) }
    val scope = rememberCoroutineScope()
    val status = state.alarmStatus
    val isAdmin = state.isAdmin

    suspend fun refresh() {
        try {
            val fresh = model.refreshAlarm()
            fresh.now?.let { clockOffsetMs = it.toEpochMilli() - System.currentTimeMillis() }
            if (pinFor == null) errorMessage = null
        } catch (error: Exception) {
            errorMessage = error.message ?: "Could not reach the alarm."
        }
    }

    LaunchedEffect(Unit) {
        isLoading = true
        refresh()
        isLoading = false
    }

    // Polls only while this destination is composed; the half-second tick keeps the countdown
    // moving between polls.
    LaunchedEffect(Unit) {
        var ticks = 0
        while (true) {
            delay(500)
            nowMs = System.currentTimeMillis() + clockOffsetMs
            if (++ticks % 4 == 0) refresh()
        }
    }

    // Arm or disarm; the server says no with a 4xx and an explanation, and two of those
    // answers get their own dialog: a wrong or missing PIN, and something being open.
    fun change(mode: String, force: Boolean, pin: String?) {
        scope.launch {
            try {
                val reply = if (mode == "disarmed") model.disarmAlarm(pin) else model.armAlarm(mode, force, pin)
                errorMessage = reply.note
                pinFor = null
            } catch (error: SentinelApiException) {
                when {
                    error.statusCode == 403 && status?.pinSet == true -> {
                        pinError = if (pin == null) null else error.message
                        pinFor = mode
                    }
                    error.statusCode == 409 && mode != "disarmed" && error.message.contains("open") -> openConfirm = mode to error.message
                    else -> errorMessage = error.message
                }
            } catch (error: Exception) {
                errorMessage = error.message ?: "Request failed."
            }
        }
    }

    Column(Modifier.fillMaxSize()) {
        ScreenHeader("Alarm", state, onRefresh = { scope.launch { refresh() } }) {
            IconButton(onClick = onEvents) { Icon(Icons.Default.History, contentDescription = "Activity") }
            if (isAdmin && status?.enabled == true) {
                IconButton(onClick = onSettings) { Icon(Icons.Default.Settings, contentDescription = "Alarm settings") }
                IconButton(onClick = onAddSensor) { Icon(Icons.Default.Add, contentDescription = "Add sensor") }
            }
        }

        if (isLoading) LinearProgressIndicator(Modifier.fillMaxWidth(), color = SentinelGreen)

        when {
            status == null -> Spacer(Modifier)
            !status.enabled -> EmptyState("Alarm is off", "The alarm is turned off in the server's Settings.")
            else -> LazyColumn(
                contentPadding = PaddingValues(16.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                item { AlarmStatusCard(status, nowMs, ::change) { confirmPanic = true } }
                errorMessage?.let { message -> item { Text(message, color = SentinelAmber, style = MaterialTheme.typography.bodySmall) } }
                if (status.sensors.isEmpty()) {
                    item {
                        Text(
                            when {
                                !status.hubEnabled -> "The alarm listens to Device Hub sensors, and the Device Hub is off. Turn on MQTT and the Device Hub in the server's Settings, add your sensors there, then come back here."
                                isAdmin -> "No sensors yet. Add a Device Hub sensor - a door contact, a motion sensor, a leak sensor - and say what it guards."
                                else -> "No sensors yet. An admin account can add them."
                            },
                            color = SentinelTextDim,
                            modifier = Modifier.padding(8.dp),
                        )
                    }
                } else {
                    item { Text("Sensors", style = MaterialTheme.typography.titleMedium) }
                    items(status.sensors, key = { it.id }) { sensor ->
                        AlarmSensorCard(
                            sensor = sensor,
                            status = status,
                            nowMs = nowMs,
                            canEdit = isAdmin,
                            onEdit = { onEditSensor(sensor) },
                            onDelete = { sensorToDelete = sensor },
                        )
                    }
                }
            }
        }
    }

    pinFor?.let { mode ->
        var pin by remember(mode) { mutableStateOf("") }
        AlertDialog(
            onDismissRequest = { pinFor = null },
            title = { Text(if (mode == "disarmed") "PIN to disarm" else "PIN to arm ${AlarmModes.label(mode).lowercase()}") },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    pinError?.let { Text(it, color = SentinelRed) }
                    OutlinedTextField(
                        value = pin,
                        onValueChange = { pin = it.filter(Char::isDigit).take(8) },
                        label = { Text("PIN") },
                        singleLine = true,
                        visualTransformation = PasswordVisualTransformation(),
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword, imeAction = ImeAction.Done),
                    )
                }
            },
            confirmButton = { TextButton(onClick = { change(mode, false, pin) }, enabled = pin.isNotEmpty()) { Text("OK") } },
            dismissButton = { TextButton(onClick = { pinFor = null }) { Text("Cancel") } },
        )
    }

    openConfirm?.let { (mode, message) ->
        AlertDialog(
            onDismissRequest = { openConfirm = null },
            title = { Text("Something is open") },
            text = { Text(message) },
            confirmButton = { TextButton(onClick = { openConfirm = null; change(mode, true, null) }) { Text("Arm anyway") } },
            dismissButton = { TextButton(onClick = { openConfirm = null }) { Text("Cancel") } },
        )
    }

    if (confirmPanic) {
        AlertDialog(
            onDismissRequest = { confirmPanic = false },
            title = { Text("Set the alarm off?") },
            text = { Text("The siren, everyone's phones and the cameras - right now.") },
            confirmButton = {
                TextButton(onClick = {
                    confirmPanic = false
                    scope.launch { runCatching { model.alarmPanic() }.onFailure { errorMessage = it.message } }
                }) { Text("Set it off", color = SentinelRed) }
            },
            dismissButton = { TextButton(onClick = { confirmPanic = false }) { Text("Cancel") } },
        )
    }

    sensorToDelete?.let { sensor ->
        AlertDialog(
            onDismissRequest = { sensorToDelete = null },
            title = { Text("Remove ${sensor.name}?") },
            text = { Text("It will no longer be part of the alarm. Its Device Hub device is not touched.") },
            confirmButton = {
                TextButton(onClick = {
                    sensorToDelete = null
                    scope.launch { runCatching { model.deleteAlarmSensor(sensor.id) }.onFailure { errorMessage = it.message } }
                }) { Text("Remove", color = SentinelRed) }
            },
            dismissButton = { TextButton(onClick = { sensorToDelete = null }) { Text("Cancel") } },
        )
    }
}

@Composable
private fun AlarmStatusCard(
    status: AlarmStatus,
    nowMs: Long,
    onChange: (mode: String, force: Boolean, pin: String?) -> Unit,
    onPanic: () -> Unit,
) {
    fun secondsLeft(until: Instant?): Long = until?.let { ((it.toEpochMilli() - nowMs + 999) / 1000).coerceAtLeast(0) } ?: 0
    val headline = when (status.status) {
        "arming" -> "Arming away · ${secondsLeft(status.exitUntil)}s to leave"
        "pending" -> "Entry · ${secondsLeft(status.pendingUntil)}s to disarm"
        "triggered" -> "🚨 Alarm"
        "armed" -> "Armed ${AlarmModes.label(status.mode).lowercase()}"
        else -> "Disarmed"
    }
    val headlineColor = when (status.status) {
        "armed" -> SentinelGreen
        "arming", "pending" -> SentinelAmber
        "triggered" -> SentinelRed
        else -> SentinelTextDim
    }
    val by = status.changedBy?.let { " by $it" } ?: ""
    val detail = when (status.status) {
        "triggered" -> "${status.triggeredBy ?: "Alarm"}${if (status.sirenOn) " · siren on" else ""}. Disarm to silence it."
        "disarmed" -> status.changedAt?.let { "Disarmed$by ${formatWhen(it)}" } ?: "Never armed"
        else -> "${AlarmModes.label(status.mode)}$by${status.changedAt?.let { " since ${formatWhen(it)}" } ?: ""}"
    }

    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text(headline, style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold, color = headlineColor)
            Text(detail, style = MaterialTheme.typography.bodyMedium, color = SentinelTextDim)
            if (status.status == "disarmed") {
                Text(
                    if (status.ready) "Ready to arm - every sensor is closed and reporting" else "Not ready",
                    style = MaterialTheme.typography.bodySmall,
                    color = if (status.ready) SentinelGreen else SentinelAmber,
                )
            }
            status.issues.forEach { issue ->
                Text("• $issue", style = MaterialTheme.typography.bodySmall, color = SentinelAmber)
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                ModeButton("Disarm", "disarmed", status, Modifier.weight(1f)) { onChange("disarmed", false, null) }
                ModeButton("Home", "home", status, Modifier.weight(1f)) { onChange("home", false, null) }
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                ModeButton("Night", "night", status, Modifier.weight(1f)) { onChange("night", false, null) }
                ModeButton("Away", "away", status, Modifier.weight(1f)) { onChange("away", false, null) }
            }
            OutlinedButton(
                onClick = onPanic,
                enabled = !status.isTriggered,
                modifier = Modifier.fillMaxWidth(),
                colors = ButtonDefaults.outlinedButtonColors(contentColor = SentinelRed),
            ) { Text("Panic") }
        }
    }
}

@Composable
private fun ModeButton(label: String, mode: String, status: AlarmStatus, modifier: Modifier, onClick: () -> Unit) {
    val selected = status.mode == mode
    Button(
        onClick = onClick,
        enabled = !selected || status.isTriggered,
        modifier = modifier,
        // The current mode is disabled (nothing to switch to) unless the alarm is going off, so the
        // disabled colours need the green too - otherwise the mode in force reads as greyed out.
        colors = if (selected) ButtonDefaults.buttonColors(
            containerColor = SentinelGreen, contentColor = Color.Black,
            disabledContainerColor = SentinelGreen, disabledContentColor = Color.Black,
        ) else ButtonDefaults.buttonColors(),
    ) { Text(label) }
}

@Composable
private fun AlarmSensorCard(
    sensor: AlarmSensor,
    status: AlarmStatus,
    nowMs: Long,
    canEdit: Boolean,
    onEdit: () -> Unit,
    onDelete: () -> Unit,
) {
    val stateColor = when (sensor.state) {
        "open" -> SentinelAmber
        "closed" -> SentinelGreen
        else -> SentinelTextDim
    }
    val flags = buildList {
        if (sensor.deviceMissing) add("device missing" to SentinelRed) else if (sensor.offline) add("offline" to SentinelRed)
        if (sensor.bypassed) add("skipped until disarm" to SentinelAmber)
        if (sensor.lowBattery) add("low battery" to SentinelAmber)
        if (!sensor.enabled) add("disabled" to SentinelTextDim)
        else if (!sensor.watched && status.status != "disarmed") add("not watched in ${AlarmModes.label(status.mode).lowercase()}" to SentinelTextDim)
    }
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(sensor.name, style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f))
                Text(sensor.stateLabel, style = MaterialTheme.typography.titleSmall, color = stateColor)
            }
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Text(sensor.zoneLabel, style = MaterialTheme.typography.bodySmall, color = SentinelTextDim)
                sensor.battery?.let {
                    Text("🔋 ${it.toInt()}%", style = MaterialTheme.typography.bodySmall, color = if (sensor.lowBattery) SentinelAmber else SentinelTextDim)
                }
                sensor.linkQuality?.let { Text("📶 ${it.toInt()}", style = MaterialTheme.typography.bodySmall, color = SentinelTextDim) }
            }
            val meta = buildList {
                add(sensor.lastSeen?.let { "heard ${ago(it, nowMs)}" } ?: "never heard")
                sensor.lastChanged?.let { add("changed ${ago(it, nowMs)}") }
                sensor.deviceName?.let { add(sensor.room?.let { room -> "$it · $room" } ?: it) }
            }
            Text(meta.joinToString(" · "), style = MaterialTheme.typography.bodySmall, color = SentinelTextDim)
            if (flags.isNotEmpty()) {
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    flags.forEach { (text, color) -> Text(text.uppercase(), style = MaterialTheme.typography.labelSmall, color = color) }
                }
            }
            if (canEdit) {
                Row(horizontalArrangement = Arrangement.End, modifier = Modifier.fillMaxWidth()) {
                    IconButton(onClick = onEdit) { Icon(Icons.Default.Edit, contentDescription = "Edit ${sensor.name}") }
                    IconButton(onClick = onDelete) { Icon(Icons.Default.Delete, contentDescription = "Remove ${sensor.name}") }
                }
            }
        }
    }
}

private val whenFormatter: DateTimeFormatter = DateTimeFormatter.ofPattern("MMM d, h:mm a")

private fun formatWhen(instant: Instant): String = whenFormatter.format(instant.atZone(ZoneId.systemDefault()))

private fun ago(instant: Instant, nowMs: Long): String {
    val seconds = ((nowMs - instant.toEpochMilli()) / 1000).coerceAtLeast(0)
    return when {
        seconds < 60 -> "${seconds}s ago"
        seconds < 3600 -> "${seconds / 60}m ago"
        seconds < 86400 -> "${seconds / 3600}h ago"
        else -> "${seconds / 86400}d ago"
    }
}

// Add or edit one sensor: which Device Hub device it is, what it guards, when it is watched.
@Composable
fun AlarmSensorEditScreen(
    model: SentinelViewModel,
    state: SentinelUiState,
    sensorId: String?,
    onBack: () -> Unit,
) {
    val existing = sensorId?.let { model.alarmSensor(it) }
    val taken = state.alarmStatus?.sensors.orEmpty().filter { it.id != sensorId }.map { it.deviceId }.toSet()
    val candidates = remember(state.devices, existing) {
        state.devices.filter { it.kind == "sensor" && it.id !in taken }
    }
    var input by remember {
        mutableStateOf(
            existing?.let { AlarmSensorInput.from(it) }
                ?: AlarmSensorInput(deviceId = candidates.firstOrNull()?.id ?: "", name = candidates.firstOrNull()?.name ?: ""),
        )
    }
    var isSaving by remember { mutableStateOf(false) }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    LaunchedEffect(Unit) { runCatching { model.refreshDevices() } }

    Column(Modifier.fillMaxSize()) {
        DetailHeader(if (sensorId == null) "Add Sensor" else "Edit Sensor", onBack)
        Column(
            Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            errorMessage?.let { Text(it, color = SentinelRed) }

            Text("Device Hub device", style = MaterialTheme.typography.labelLarge)
            if (candidates.isEmpty() && existing == null) {
                Text(
                    "No unused sensor devices in the Device Hub. Add the sensor on the Device Hub tab first - Discover finds Zigbee2MQTT ones by itself.",
                    color = SentinelTextDim,
                )
            }
            val choices = if (existing != null && candidates.none { it.id == existing.deviceId }) {
                listOf(existing.deviceId to (existing.deviceName ?: "(missing device)")) + candidates.map { it.id to it.name }
            } else {
                candidates.map { it.id to it.name }
            }
            Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                choices.forEach { (id, name) ->
                    FilterChip(
                        selected = input.deviceId == id,
                        onClick = {
                            input = input.copy(deviceId = id, name = input.name.ifBlank { name })
                        },
                        label = { Text(name) },
                    )
                }
            }

            OutlinedTextField(
                value = input.name,
                onValueChange = { input = input.copy(name = it) },
                label = { Text("Name") },
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )

            Text("What it guards", style = MaterialTheme.typography.labelLarge)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                AlarmZones.all.forEach { zone ->
                    FilterChip(
                        selected = input.zone == zone,
                        onClick = {
                            input = if (existing == null) {
                                input.copy(zone = zone, armedHome = zone != "interior", armedNight = zone != "interior", armedAway = true)
                            } else {
                                input.copy(zone = zone)
                            }
                        },
                        label = { Text(AlarmZones.label(zone)) },
                    )
                }
            }
            Text(AlarmZones.explanation(input.zone), style = MaterialTheme.typography.bodySmall, color = SentinelTextDim)

            if (input.zone != "always") {
                Text("Watched in", style = MaterialTheme.typography.labelLarge)
                AlarmSwitchRow("Home", input.armedHome) { input = input.copy(armedHome = it) }
                AlarmSwitchRow("Night", input.armedNight) { input = input.copy(armedNight = it) }
                AlarmSwitchRow("Away", input.armedAway) { input = input.copy(armedAway = it) }
            }
            AlarmSwitchRow("Reads the other way round", input.inverted) { input = input.copy(inverted = it) }
            Text(
                "Most sensors say ON for open, motion or wet. If this one shows Open while the door is shut, turn this on.",
                style = MaterialTheme.typography.bodySmall,
                color = SentinelTextDim,
            )
            AlarmSwitchRow("Enabled", input.enabled) { input = input.copy(enabled = it) }

            Button(
                onClick = {
                    scope.launch {
                        isSaving = true
                        try {
                            model.saveAlarmSensor(sensorId, input)
                            onBack()
                        } catch (error: Exception) {
                            errorMessage = error.message ?: "Could not save."
                        } finally {
                            isSaving = false
                        }
                    }
                },
                enabled = !isSaving && input.deviceId.isNotBlank(),
                modifier = Modifier.fillMaxWidth(),
            ) { Text(if (isSaving) "Saving…" else "Save") }
        }
    }
}

// Delays, sirens, cameras, notifications and the PIN.
@Composable
fun AlarmSettingsScreen(
    model: SentinelViewModel,
    state: SentinelUiState,
    onBack: () -> Unit,
) {
    val status = state.alarmStatus
    var config by remember { mutableStateOf(status?.config ?: AlarmConfig()) }
    var newPin by remember { mutableStateOf("") }
    var removePin by remember { mutableStateOf(false) }
    var isSaving by remember { mutableStateOf(false) }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()
    val switchable = state.devices.filter { it.kind !in listOf("sensor", "button", "cover", "gate", "garage", "lock") }

    LaunchedEffect(Unit) { runCatching { model.refreshDevices() } }

    Column(Modifier.fillMaxSize()) {
        DetailHeader("Alarm Settings", onBack)
        Column(
            Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            errorMessage?.let { Text(it, color = SentinelRed) }

            NumberField("Exit delay (seconds)", config.exitDelaySeconds) { config = config.copy(exitDelaySeconds = it) }
            Text("How long you have to leave after arming Away. Home and Night arm at once.", style = MaterialTheme.typography.bodySmall, color = SentinelTextDim)
            NumberField("Entry delay (seconds)", config.entryDelaySeconds) { config = config.copy(entryDelaySeconds = it) }
            Text("How long an entry door may be open before the siren, so whoever came in can disarm.", style = MaterialTheme.typography.bodySmall, color = SentinelTextDim)
            NumberField("Siren time (seconds)", config.sirenSeconds) { config = config.copy(sirenSeconds = it) }

            Text("Switch on when triggered", style = MaterialTheme.typography.labelLarge)
            if (switchable.isEmpty()) {
                Text("No switchable Device Hub devices yet. A siren, a strobe or a plug with a lamp on it can be switched on when the alarm goes off.", style = MaterialTheme.typography.bodySmall, color = SentinelTextDim)
            }
            switchable.forEach { device ->
                CheckRow(if (device.room.isBlank()) device.name else "${device.name} (${device.room})", device.id in config.sirenDeviceIds) { on ->
                    config = config.copy(sirenDeviceIds = if (on) config.sirenDeviceIds + device.id else config.sirenDeviceIds - device.id)
                }
            }

            Text("Cameras to snapshot", style = MaterialTheme.typography.labelLarge)
            Text(if (state.cameras.isEmpty()) "No cameras yet." else "None ticked means all of them.", style = MaterialTheme.typography.bodySmall, color = SentinelTextDim)
            state.cameras.forEach { camera ->
                CheckRow(camera.name, camera.id in config.cameraIds) { on ->
                    config = config.copy(cameraIds = if (on) config.cameraIds + camera.id else config.cameraIds - camera.id)
                }
            }

            Text("Notifications", style = MaterialTheme.typography.labelLarge)
            AlarmSwitchRow("Notify everyone's phones", config.notifyPush) { config = config.copy(notifyPush = it) }
            AlarmSwitchRow("Also on every arm and disarm", config.notifyArmDisarm) { config = config.copy(notifyArmDisarm = it) }
            NumberField("Low battery at (%)", config.lowBatteryPercent) { config = config.copy(lowBatteryPercent = it) }
            NumberField("Offline after (hours)", config.offlineAfterHours) { config = config.copy(offlineAfterHours = it) }

            Text("PIN", style = MaterialTheme.typography.labelLarge)
            AlarmSwitchRow("Ask for the PIN to arm as well", config.requirePinToArm) { config = config.copy(requirePinToArm = it) }
            OutlinedTextField(
                value = newPin,
                onValueChange = { newPin = it.filter(Char::isDigit).take(8) },
                label = { Text(if (status?.pinSet == true) "New PIN (leave blank to keep)" else "PIN (4-8 digits)") },
                singleLine = true,
                visualTransformation = PasswordVisualTransformation(),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
                modifier = Modifier.fillMaxWidth(),
            )
            if (status?.pinSet == true) AlarmSwitchRow("Remove the PIN", removePin) { removePin = it }
            Text(
                if (status?.pinSet == true) "Disarming needs the PIN." else "No PIN: anyone signed in can disarm.",
                style = MaterialTheme.typography.bodySmall,
                color = SentinelTextDim,
            )

            Button(
                onClick = {
                    scope.launch {
                        isSaving = true
                        try {
                            model.saveAlarmConfig(config, newPin, removePin)
                            onBack()
                        } catch (error: Exception) {
                            errorMessage = error.message ?: "Could not save."
                        } finally {
                            isSaving = false
                        }
                    }
                },
                enabled = !isSaving,
                modifier = Modifier.fillMaxWidth(),
            ) { Text(if (isSaving) "Saving…" else "Save") }
        }
    }
}

// Every arm, disarm, trigger, skipped sensor, low battery and outage, newest first.
@Composable
fun AlarmEventsScreen(model: SentinelViewModel, onBack: () -> Unit) {
    var events by remember { mutableStateOf<List<AlarmEvent>>(emptyList()) }
    var loaded by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        events = runCatching { model.alarmEvents() }.getOrDefault(emptyList())
        loaded = true
    }
    Column(Modifier.fillMaxSize()) {
        DetailHeader("Alarm Activity", onBack)
        if (loaded && events.isEmpty()) {
            EmptyState("Nothing yet", "Arms, disarms, triggers and sensor problems will be listed here.")
        } else {
            LazyColumn(contentPadding = PaddingValues(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                items(events, key = { it.id }) { event ->
                    val color = when (event.kind) {
                        "triggered", "panic" -> SentinelRed
                        "pending", "bypassed", "batteryLow", "offline", "pinRejected" -> SentinelAmber
                        "armed", "arming" -> SentinelGreen
                        else -> MaterialTheme.colorScheme.onSurface
                    }
                    Column {
                        Text(event.message, color = color)
                        Text(formatWhen(event.at), style = MaterialTheme.typography.bodySmall, color = SentinelTextDim)
                    }
                }
            }
        }
    }
}

@Composable
private fun AlarmSwitchRow(label: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text(label, modifier = Modifier.weight(1f))
        Switch(checked = checked, onCheckedChange = onChange)
    }
}

@Composable
private fun CheckRow(label: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Checkbox(checked = checked, onCheckedChange = onChange)
        Spacer(Modifier.width(4.dp))
        Text(label)
    }
}

@Composable
private fun NumberField(label: String, value: Int, onChange: (Int) -> Unit) {
    OutlinedTextField(
        value = value.toString(),
        onValueChange = { text -> text.filter(Char::isDigit).toIntOrNull()?.let(onChange) ?: if (text.isEmpty()) onChange(0) else Unit },
        label = { Text(label) },
        singleLine = true,
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
        modifier = Modifier.fillMaxWidth(),
    )
}
