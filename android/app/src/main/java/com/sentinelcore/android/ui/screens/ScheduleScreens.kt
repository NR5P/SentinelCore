package com.sentinelcore.android.ui.screens

import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
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
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.data.Schedule
import com.sentinelcore.android.data.ScheduleInput
import com.sentinelcore.android.data.ScheduleMetadata
import com.sentinelcore.android.data.ScheduleRun
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelTextDim
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import kotlinx.coroutines.launch

private val DAY_NAMES = listOf("Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat")
private val FULL_DAY_NAMES =
    listOf("Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday")

private val TIMESTAMP: DateTimeFormatter =
    DateTimeFormatter.ofPattern("EEE d MMM, HH:mm").withZone(ZoneId.systemDefault())

// Every schedule, with the two things worth knowing at a glance about each - when it next fires
// and how the last firing went - plus the controls that don't need the editor.
//
// State lives here rather than in SentinelUiState: nothing outside this screen reads schedules,
// and putting them in the shared state would re-fetch them on every poll.
@Composable
fun ScheduleListScreen(
    model: SentinelViewModel,
    isAdmin: Boolean,
    onBack: () -> Unit,
    onAddSchedule: () -> Unit,
    onEditSchedule: (Schedule) -> Unit,
) {
    val scope = rememberCoroutineScope()
    var schedules by remember { mutableStateOf<List<Schedule>>(emptyList()) }
    var metadata by remember { mutableStateOf(ScheduleMetadata()) }
    var loadError by remember { mutableStateOf<String?>(null) }
    var isLoading by remember { mutableStateOf(true) }
    var runStatus by remember { mutableStateOf<Map<String, String>>(emptyMap()) }
    var expandedHistory by remember { mutableStateOf<String?>(null) }
    var runs by remember { mutableStateOf<List<ScheduleRun>>(emptyList()) }

    suspend fun reload() {
        isLoading = true
        try {
            // Metadata carries the device names the rows are described with, so it has to land
            // before the list renders rather than alongside it.
            metadata = model.scheduleMetadata()
            schedules = model.schedules()
            loadError = null
        } catch (error: Exception) {
            loadError = "Could not load schedules: ${error.message}"
        } finally {
            isLoading = false
        }
    }

    LaunchedEffect(Unit) { reload() }

    Column(modifier = Modifier.fillMaxSize().padding(16.dp)) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            TextButton(onClick = onBack) { Text("Back", color = SentinelGreen) }
            Text("SCHEDULES", style = MaterialTheme.typography.titleMedium)
            if (isAdmin) {
                TextButton(onClick = onAddSchedule) { Text("Add", color = SentinelGreen) }
            } else {
                Text("")
            }
        }

        loadError?.let {
            Text(it, color = SentinelRed, style = MaterialTheme.typography.bodySmall)
        }

        // Worth saying once at the top rather than on every row: without coordinates, a schedule
        // that follows the sun can never work out when to fire.
        if (!metadata.locationSet && schedules.any { it.followsTheSun }) {
            Text(
                "Some schedules here follow the sun, but no location is set. Add your town under Server Settings → LOCATION, or they will never run.",
                color = SentinelAmber,
                style = MaterialTheme.typography.bodySmall,
                modifier = Modifier.padding(vertical = 8.dp),
            )
        }

        if (isLoading && schedules.isEmpty()) {
            Text("Loading schedules…", color = SentinelTextDim)
        } else if (schedules.isEmpty()) {
            Text(
                "No schedules yet. Add one to switch a device, publish MQTT, or call an API endpoint at a time of your choosing — or at sunset, or when it gets dark.",
                color = SentinelTextDim,
                style = MaterialTheme.typography.bodySmall,
            )
        }

        LazyColumn(modifier = Modifier.fillMaxSize()) {
            items(schedules, key = { it.id }) { schedule ->
                Column(modifier = Modifier.fillMaxWidth().padding(vertical = 8.dp)) {
                    Text(
                        schedule.name + if (schedule.enabled) "" else " (off)",
                        style = MaterialTheme.typography.titleSmall,
                    )
                    Text(
                        describeTiming(schedule, metadata),
                        style = MaterialTheme.typography.bodySmall,
                        color = SentinelTextDim,
                    )
                    Text(
                        describeAction(schedule, metadata),
                        style = MaterialTheme.typography.bodySmall,
                        color = SentinelTextDim,
                    )
                    Text(
                        nextRunText(schedule, metadata),
                        style = MaterialTheme.typography.bodySmall,
                        color = if (schedule.followsTheSun && !metadata.locationSet) SentinelAmber else SentinelTextDim,
                    )
                    if (schedule.lastStatus.isNotEmpty()) {
                        Text(
                            lastRunText(schedule),
                            style = MaterialTheme.typography.bodySmall,
                            color = if (schedule.lastStatus == "failed") SentinelRed else SentinelTextDim,
                        )
                    }
                    runStatus[schedule.id]?.let {
                        Text(
                            it,
                            style = MaterialTheme.typography.bodySmall,
                            color = if (it.startsWith("failed")) SentinelRed else SentinelGreen,
                        )
                    }

                    if (expandedHistory == schedule.id) {
                        if (runs.isEmpty()) {
                            Text("No runs recorded yet.", style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
                        } else {
                            runs.forEach { run ->
                                Text(
                                    "${TIMESTAMP.format(run.firedAtUtc)} · ${run.status}" +
                                        if (run.detail.isEmpty()) "" else " · ${run.detail}",
                                    style = MaterialTheme.typography.labelSmall,
                                    color = SentinelTextDim,
                                )
                            }
                        }
                    }

                    if (isAdmin) {
                        Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                            TextButton(onClick = {
                                scope.launch {
                                    runStatus = runStatus + (schedule.id to "running…")
                                    runStatus = try {
                                        val result = model.runSchedule(schedule.id)
                                        runStatus + (schedule.id to (if (result.fired) "ok: " else "failed: ") + result.detail)
                                    } catch (error: Exception) {
                                        runStatus + (schedule.id to "failed: ${error.message}")
                                    }
                                    reload()
                                }
                            }) { Text("Run now", color = SentinelGreen) }

                            TextButton(onClick = {
                                scope.launch {
                                    runCatching { model.setScheduleEnabled(schedule.id, !schedule.enabled) }
                                    reload()
                                }
                            }) { Text(if (schedule.enabled) "Disable" else "Enable", color = SentinelGreen) }

                            TextButton(onClick = {
                                scope.launch {
                                    if (expandedHistory == schedule.id) {
                                        expandedHistory = null
                                    } else {
                                        expandedHistory = schedule.id
                                        runs = runCatching { model.scheduleRuns(schedule.id) }.getOrDefault(emptyList())
                                    }
                                }
                            }) { Text("History", color = SentinelGreen) }

                            TextButton(onClick = { onEditSchedule(schedule) }) {
                                Text("Edit", color = SentinelGreen)
                            }

                            TextButton(onClick = {
                                scope.launch {
                                    runCatching { model.deleteSchedule(schedule.id) }
                                    reload()
                                }
                            }) { Text("Delete", color = SentinelRed) }
                        }
                    }

                    HorizontalDivider()
                }
            }
        }
    }
}

// Add or edit one schedule. Only the fields belonging to the chosen timing and action are shown -
// a daily schedule has no cron box, a webhook has no device picker.
//
// The live preview at the bottom is doing real work: a cron expression or a darkness level is
// close to unverifiable by reading it, and seeing the next few firings is what makes either safe
// to commit to.
@Composable
fun ScheduleEditScreen(
    model: SentinelViewModel,
    scheduleId: String?,
    onBack: () -> Unit,
    onSaved: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    var metadata by remember { mutableStateOf(ScheduleMetadata()) }
    var loaded by remember { mutableStateOf(false) }

    var name by remember { mutableStateOf("") }
    var enabled by remember { mutableStateOf(true) }
    var kind by remember { mutableStateOf("daily") }
    var timeOfDay by remember { mutableStateOf("18:00") }
    var days by remember { mutableStateOf<Set<Int>>(emptySet()) }
    var intervalMinutes by remember { mutableStateOf("15") }
    var windowStart by remember { mutableStateOf("") }
    var windowEnd by remember { mutableStateOf("") }
    var cronExpression by remember { mutableStateOf("0 6 * * 1-5") }
    var runAt by remember { mutableStateOf("") }
    var solarEvent by remember { mutableStateOf("dusk") }
    var solarElevation by remember { mutableStateOf(-6.0) }
    var solarOffset by remember { mutableStateOf("0") }

    var actionType by remember { mutableStateOf("device") }
    var deviceId by remember { mutableStateOf<String?>(null) }
    var deviceAction by remember { mutableStateOf("on") }
    var useBrightness by remember { mutableStateOf(false) }
    var brightness by remember { mutableStateOf("100") }
    var mqttTopic by remember { mutableStateOf("") }
    var mqttPayload by remember { mutableStateOf("") }
    var mqttRetain by remember { mutableStateOf(false) }
    var webhookUrl by remember { mutableStateOf("") }
    var webhookMethod by remember { mutableStateOf("POST") }
    var webhookHeaders by remember { mutableStateOf("") }
    var webhookBody by remember { mutableStateOf("") }
    var webhookContentType by remember { mutableStateOf("application/json") }
    var catchUpPolicy by remember { mutableStateOf("skip") }

    var previewText by remember { mutableStateOf("") }
    var errors by remember { mutableStateOf<List<String>>(emptyList()) }
    var isSaving by remember { mutableStateOf(false) }

    fun buildInput() = ScheduleInput(
        name = name.trim(),
        enabled = enabled,
        kind = kind,
        timeOfDay = timeOfDay.trim(),
        // Cron carries its own day-of-week field and a one-off is a single instant, so sending
        // days for either would be ignored at best and misleading at worst.
        daysOfWeek = if (kind == "daily" || kind == "solar") days.sorted() else emptyList(),
        intervalSeconds = (intervalMinutes.toIntOrNull() ?: 15) * 60,
        windowStart = windowStart.trim(),
        windowEnd = windowEnd.trim(),
        cronExpression = cronExpression.trim(),
        runAtUtc = if (kind == "once") runCatching { Instant.parse(runAt.trim()) }.getOrNull() else null,
        solarEvent = solarEvent,
        solarOffsetMinutes = solarOffset.toIntOrNull() ?: 0,
        solarElevationDegrees = solarElevation,
        actionType = actionType,
        deviceId = if (actionType == "device") deviceId else null,
        deviceAction = if (actionType == "device") deviceAction else "",
        deviceBrightness = if (actionType == "device" && useBrightness) brightness.toIntOrNull() else null,
        mqttTopic = mqttTopic.trim(),
        mqttPayload = mqttPayload,
        mqttRetain = mqttRetain,
        webhookUrl = webhookUrl.trim(),
        webhookMethod = webhookMethod,
        webhookHeaders = webhookHeaders,
        webhookBody = webhookBody,
        webhookContentType = webhookContentType.trim(),
        catchUpPolicy = catchUpPolicy,
    )

    LaunchedEffect(scheduleId) {
        metadata = runCatching { model.scheduleMetadata() }.getOrDefault(ScheduleMetadata())
        deviceId = deviceId ?: metadata.devices.firstOrNull()?.id

        if (scheduleId != null) {
            val existing = runCatching { model.schedules().firstOrNull { it.id == scheduleId } }.getOrNull()
            if (existing != null) {
                name = existing.name
                enabled = existing.enabled
                kind = existing.kind
                timeOfDay = existing.timeOfDay.ifEmpty { "18:00" }
                days = existing.daysOfWeek.toSet()
                intervalMinutes = (existing.intervalSeconds / 60).coerceAtLeast(1).toString()
                windowStart = existing.windowStart
                windowEnd = existing.windowEnd
                cronExpression = existing.cronExpression.ifEmpty { cronExpression }
                runAt = existing.runAtUtc?.toString() ?: ""
                solarEvent = existing.solarEvent
                solarElevation = existing.solarElevationDegrees
                solarOffset = existing.solarOffsetMinutes.toString()
                actionType = existing.action.type
                deviceId = existing.action.deviceId ?: metadata.devices.firstOrNull()?.id
                deviceAction = existing.action.deviceAction.ifEmpty { "on" }
                useBrightness = existing.action.deviceBrightness != null
                brightness = (existing.action.deviceBrightness ?: 100).toString()
                mqttTopic = existing.action.mqttTopic
                mqttPayload = existing.action.mqttPayload
                mqttRetain = existing.action.mqttRetain
                webhookUrl = existing.action.webhookUrl
                webhookMethod = existing.action.webhookMethod
                webhookHeaders = existing.action.webhookHeaders
                webhookBody = existing.action.webhookBody
                webhookContentType = existing.action.webhookContentType
                catchUpPolicy = existing.catchUpPolicy
            }
        }
        loaded = true
    }

    // Re-runs whenever anything the firing times depend on changes, and not when the name or the
    // action does.
    LaunchedEffect(loaded, kind, timeOfDay, days, intervalMinutes, windowStart, windowEnd,
        cronExpression, runAt, solarEvent, solarElevation, solarOffset) {
        if (!loaded) return@LaunchedEffect

        // The server rejects a nameless schedule and the preview shares that validation, so it
        // gets a placeholder rather than reporting an error about a field this box is not about.
        val input = buildInput().let { if (it.name.isEmpty()) it.copy(name = "Preview") else it }
        previewText = runCatching {
            val preview = model.previewSchedule(input)
            if (preview.occurrences.isEmpty()) {
                "This never fires as configured."
            } else {
                preview.explanation + "  Next: " + preview.occurrences.joinToString("  •  ")
            }
        }.getOrDefault("Fill in the timing above to see the next runs.")
    }

    Column(
        modifier = Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Text(if (scheduleId == null) "NEW SCHEDULE" else "EDIT SCHEDULE", style = MaterialTheme.typography.titleMedium)

        OutlinedTextField(name, { name = it }, label = { Text("Name") }, modifier = Modifier.fillMaxWidth())
        SwitchRow("Enabled", enabled) { enabled = it }

        Text("WHEN", style = MaterialTheme.typography.labelLarge, color = SentinelGreen)
        ChipRow(
            listOf(
                "daily" to "Time of day",
                "solar" to "Sun / darkness",
                "interval" to "Every so often",
                "once" to "Once",
                "cron" to "Cron",
            ),
            kind,
        ) { kind = it }

        when (kind) {
            "daily" -> OutlinedTextField(
                timeOfDay, { timeOfDay = it },
                label = { Text("Time (HH:mm)") },
                modifier = Modifier.fillMaxWidth(),
            )

            "interval" -> {
                OutlinedTextField(
                    intervalMinutes, { intervalMinutes = it },
                    label = { Text("Every (minutes)") },
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    windowStart, { windowStart = it },
                    label = { Text("Only from (HH:mm, optional)") },
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    windowEnd, { windowEnd = it },
                    label = { Text("Until (HH:mm, optional)") },
                    modifier = Modifier.fillMaxWidth(),
                )
            }

            "cron" -> {
                OutlinedTextField(
                    cronExpression, { cronExpression = it },
                    label = { Text("Cron expression") },
                    modifier = Modifier.fillMaxWidth(),
                )
                Text(
                    "Five fields: minute hour day-of-month month day-of-week. \"0 6 * * 1-5\" is 6am on weekdays.",
                    style = MaterialTheme.typography.labelSmall, color = SentinelTextDim,
                )
            }

            "once" -> OutlinedTextField(
                runAt, { runAt = it },
                label = { Text("Date and time (2026-08-05T15:00:00Z)") },
                modifier = Modifier.fillMaxWidth(),
            )

            "solar" -> {
                ChipRow(
                    listOf(
                        "dusk" to "Gets dark (evening)",
                        "dawn" to "Gets light (morning)",
                        "sunset" to "Sunset",
                        "sunrise" to "Sunrise",
                    ),
                    solarEvent,
                ) { solarEvent = it }

                if (solarEvent == "dusk" || solarEvent == "dawn") {
                    Text("How dark", style = MaterialTheme.typography.labelMedium)
                    ChipRow(
                        metadata.darknessLevels.map { it.elevationDegrees.toString() to it.label },
                        solarElevation.toString(),
                    ) { solarElevation = it.toDoubleOrNull() ?: -6.0 }

                    metadata.darknessLevels
                        .firstOrNull { kotlin.math.abs(it.elevationDegrees - solarElevation) < 0.01 }
                        ?.let {
                            Text(it.description, style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
                        }
                }

                OutlinedTextField(
                    solarOffset, { solarOffset = it },
                    label = { Text("Offset (minutes, negative for before)") },
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    modifier = Modifier.fillMaxWidth(),
                )

                // The clock time this resolves to moves through the year on its own, which is
                // the entire reason to pick this over a daily time - worth saying, since it
                // isn't obvious.
                Text(
                    "The clock time follows the seasons by itself — nothing to adjust twice a year.",
                    style = MaterialTheme.typography.labelSmall, color = SentinelTextDim,
                )

                if (!metadata.locationSet) {
                    Text(
                        "No location is set, so sunrise and sunset cannot be worked out. Add your town under Server Settings → LOCATION first.",
                        style = MaterialTheme.typography.labelSmall, color = SentinelAmber,
                    )
                }
            }
        }

        if (kind == "daily" || kind == "solar") {
            Text("Days (none selected = every day)", style = MaterialTheme.typography.labelMedium)
            ChipRow(
                FULL_DAY_NAMES.mapIndexed { index, label -> index.toString() to DAY_NAMES[index] },
                selected = null,
                isSelected = { days.contains(it.toInt()) },
            ) { value ->
                val day = value.toInt()
                days = if (days.contains(day)) days - day else days + day
            }
        }

        Text("THEN", style = MaterialTheme.typography.labelLarge, color = SentinelGreen)
        ChipRow(
            listOf("device" to "Switch a device", "mqtt" to "Publish MQTT", "webhook" to "Call an endpoint"),
            actionType,
        ) { actionType = it }

        when (actionType) {
            "device" -> {
                if (metadata.devices.isEmpty()) {
                    Text(
                        "No devices in the Device Hub yet. Add one there first, or choose a different action.",
                        style = MaterialTheme.typography.labelSmall, color = SentinelTextDim,
                    )
                } else {
                    ChipRow(
                        metadata.devices.map { it.id to it.name },
                        deviceId ?: "",
                    ) { deviceId = it }

                    val device = metadata.devices.firstOrNull { it.id == deviceId }
                    ChipRow(availableDeviceActions(device, metadata).map { it to it }, deviceAction) {
                        deviceAction = it
                    }

                    // Only offered for devices that can actually dim - on anything else it would
                    // produce nothing but a save error.
                    if (device?.supportsBrightness == true) {
                        SwitchRow("Also set brightness", useBrightness) { useBrightness = it }
                        if (useBrightness) {
                            OutlinedTextField(
                                brightness, { brightness = it },
                                label = { Text("Brightness %") },
                                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                                modifier = Modifier.fillMaxWidth(),
                            )
                        }
                    }
                }
            }

            "mqtt" -> {
                OutlinedTextField(mqttTopic, { mqttTopic = it }, label = { Text("Topic") }, modifier = Modifier.fillMaxWidth())
                OutlinedTextField(mqttPayload, { mqttPayload = it }, label = { Text("Payload") }, modifier = Modifier.fillMaxWidth())
                SwitchRow("Retain", mqttRetain) { mqttRetain = it }
            }

            else -> {
                OutlinedTextField(webhookUrl, { webhookUrl = it }, label = { Text("URL") }, modifier = Modifier.fillMaxWidth())
                ChipRow(listOf("POST", "PUT", "PATCH", "GET", "DELETE").map { it to it }, webhookMethod) {
                    webhookMethod = it
                }
                OutlinedTextField(webhookContentType, { webhookContentType = it }, label = { Text("Content-Type") }, modifier = Modifier.fillMaxWidth())
                OutlinedTextField(webhookHeaders, { webhookHeaders = it }, label = { Text("Extra headers (one per line)") }, modifier = Modifier.fillMaxWidth())
                OutlinedTextField(webhookBody, { webhookBody = it }, label = { Text("Body") }, modifier = Modifier.fillMaxWidth())
            }
        }

        Text("If the server was off when it was due", style = MaterialTheme.typography.labelMedium)
        ChipRow(
            listOf("skip" to "Skip it", "runOnce" to "Run once on the way back up"),
            catchUpPolicy,
        ) { catchUpPolicy = it }

        Text("NEXT RUNS", style = MaterialTheme.typography.labelLarge, color = SentinelGreen)
        Text(previewText, style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)

        errors.forEach { Text(it, color = SentinelRed, style = MaterialTheme.typography.bodySmall) }

        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Button(
                enabled = !isSaving,
                onClick = {
                    scope.launch {
                        isSaving = true
                        errors = emptyList()
                        try {
                            if (scheduleId == null) model.createSchedule(buildInput())
                            else model.updateSchedule(scheduleId, buildInput())
                            onSaved()
                        } catch (error: Exception) {
                            errors = listOf(error.message ?: "Save failed.")
                        } finally {
                            isSaving = false
                        }
                    }
                },
            ) { Text("Save") }

            TextButton(onClick = onBack) { Text("Cancel", color = SentinelGreen) }
        }
    }
}

// A cover-like device speaks open/close, a momentary one only presses - offering the whole
// vocabulary for every device would mostly offer nonsense.
private fun availableDeviceActions(
    device: com.sentinelcore.android.data.ScheduleDeviceOption?,
    metadata: ScheduleMetadata,
): List<String> {
    if (device == null) return metadata.deviceActions
    if (device.momentary) return listOf("press")
    if (device.coverLike) return if (device.hasStop) listOf("open", "close", "stop") else listOf("open", "close")
    if (device.kind == "lock") return listOf("lock", "unlock")
    return listOf("on", "off", "toggle")
}

@Composable
private fun SwitchRow(label: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(label, style = MaterialTheme.typography.bodyMedium)
        Switch(checked = checked, onCheckedChange = onChange)
    }
}

// One row of choices. `selected` covers the usual single-choice case; `isSelected` is for the
// day picker, where several can be on at once.
@Composable
private fun ChipRow(
    options: List<Pair<String, String>>,
    selected: String?,
    isSelected: ((String) -> Boolean)? = null,
    onSelect: (String) -> Unit,
) {
    Row(
        modifier = Modifier.fillMaxWidth().horizontalScrollIfNeeded(),
        horizontalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        options.forEach { (value, label) ->
            FilterChip(
                selected = isSelected?.invoke(value) ?: (value == selected),
                onClick = { onSelect(value) },
                label = { Text(label, style = MaterialTheme.typography.labelSmall) },
            )
        }
    }
}

// Chip rows can hold more than fits on a phone - a device list especially - so they scroll
// sideways rather than being clipped.
@Composable
private fun Modifier.horizontalScrollIfNeeded(): Modifier =
    this.then(Modifier.horizontalScroll(rememberScrollState()))

private fun describeTiming(schedule: Schedule, metadata: ScheduleMetadata): String {
    val days = if (schedule.daysOfWeek.isEmpty()) {
        "every day"
    } else {
        schedule.daysOfWeek.sorted().joinToString(", ") { DAY_NAMES[it] }
    }

    return when (schedule.kind) {
        "daily" -> "At ${schedule.timeOfDay}, $days"
        "interval" -> {
            val window = if (schedule.windowStart.isEmpty() || schedule.windowEnd.isEmpty()) {
                ""
            } else {
                ", ${schedule.windowStart}–${schedule.windowEnd}"
            }
            "Every ${describeInterval(schedule.intervalSeconds)}$window"
        }
        "cron" -> "Cron: ${schedule.cronExpression}"
        "once" -> schedule.runAtUtc?.let { "Once, ${TIMESTAMP.format(it)}" } ?: "Once, at a time not yet set"
        "solar" -> describeSolar(schedule, metadata, days)
        else -> schedule.kind
    }
}

private fun describeInterval(seconds: Int): String = when {
    seconds % 3600 == 0 -> "${seconds / 3600}h"
    seconds % 60 == 0 -> "${seconds / 60}m"
    else -> "${seconds}s"
}

private fun describeSolar(schedule: Schedule, metadata: ScheduleMetadata, days: String): String {
    val offset = when {
        schedule.solarOffsetMinutes == 0 -> "At"
        schedule.solarOffsetMinutes > 0 -> "${schedule.solarOffsetMinutes} min after"
        else -> "${-schedule.solarOffsetMinutes} min before"
    }

    if (schedule.solarEvent != "dusk" && schedule.solarEvent != "dawn") {
        return "$offset ${schedule.solarEvent}, $days"
    }

    val level = metadata.darknessLevels
        .firstOrNull { kotlin.math.abs(it.elevationDegrees - schedule.solarElevationDegrees) < 0.01 }
        ?.label?.lowercase()
        ?: "the sun ${schedule.solarElevationDegrees}° below the horizon"
    val half = if (schedule.solarEvent == "dusk") "in the evening" else "in the morning"
    return "$offset $level $half, $days"
}

private fun describeAction(schedule: Schedule, metadata: ScheduleMetadata): String = when (schedule.action.type) {
    "device" -> {
        val name = metadata.devices.firstOrNull { it.id == schedule.action.deviceId }?.name
            ?: "(device no longer in the hub)"
        val brightness = schedule.action.deviceBrightness?.let { " at $it%" } ?: ""
        "${schedule.action.deviceAction.ifEmpty { "set" }} $name$brightness"
    }
    "mqtt" -> "MQTT → ${schedule.action.mqttTopic.ifEmpty { "(no topic set)" }}"
    else -> "Webhook → ${schedule.action.webhookMethod} ${schedule.action.webhookUrl.ifEmpty { "(no URL set)" }}"
}

private fun nextRunText(schedule: Schedule, metadata: ScheduleMetadata): String {
    if (schedule.followsTheSun && !metadata.locationSet) return "No location set — this cannot run"
    val next = schedule.nextDueUtc ?: return if (schedule.enabled) "No upcoming run" else "Disabled"
    return "Next: ${TIMESTAMP.format(next)}"
}

private fun lastRunText(schedule: Schedule): String {
    val when_ = schedule.lastRunUtc?.let { " ${TIMESTAMP.format(it)}" } ?: ""
    val detail = if (schedule.lastDetail.isEmpty()) "" else " · ${schedule.lastDetail}"
    return "Last: ${schedule.lastStatus}$when_$detail"
}
