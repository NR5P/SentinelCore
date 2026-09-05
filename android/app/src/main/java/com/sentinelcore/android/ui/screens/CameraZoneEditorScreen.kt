package com.sentinelcore.android.ui.screens

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExposedDropdownMenuBox
import androidx.compose.material3.ExposedDropdownMenuDefaults
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.MenuAnchorType
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.drawText
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.data.Camera
import com.sentinelcore.android.data.Point2D
import com.sentinelcore.android.data.PromptProfileName
import com.sentinelcore.android.data.TriggerConfig
import com.sentinelcore.android.data.TriggerZone
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch
import kotlin.math.PI
import kotlin.math.ln
import kotlin.math.pow
import kotlin.math.roundToInt
import kotlin.math.sqrt

// The detector works on a fixed 320x180 grid (MotionDetector.GridWidth/GridHeight), so a size
// expressed as a fraction of the frame turns into this many pixels - the number the trigger
// threshold is actually compared against, and far easier to reason about than "0.0015". For
// reference, a person standing at a gate roughly 30 feet away measures around 160.
private const val DETECTOR_PIXELS = 320.0 * 180.0

// The smallest size a zone can be set to. Four detector pixels is about as small as the grid can
// meaningfully express - below a couple of pixels a blob is indistinguishable from sensor noise -
// and it is far below what this editor used to allow. The old drag-only circle clamped its radius
// to 10 *pixels of the pane*, so the floor depended on how big the pane happened to be: on a phone
// that bottomed out around 250 detector pixels, roughly a person at 40 feet. Anything smaller or
// further away simply couldn't be asked for.
private const val MIN_DETECTOR_PIXELS = 4.0

private fun detectorPixelCount(minTriggerArea: Double): Int =
    (minTriggerArea * DETECTOR_PIXELS).roundToInt()

// The server rejects anything outside (0, 1], and everything that writes minTriggerArea goes
// through here so neither end can be reached by accident.
private fun triggerAreaOf(detectorPixels: Double): Double =
    detectorPixels.coerceIn(MIN_DETECTOR_PIXELS, DETECTOR_PIXELS) / DETECTOR_PIXELS

// The slider is logarithmic. A linear one over a range this wide (4 to 57600) would put every
// useful small size inside its first millimetre of travel, which is exactly the end of the range
// this control exists to reach.
private fun sliderPositionOf(area: Double): Float {
    val pixels = (area * DETECTOR_PIXELS).coerceIn(MIN_DETECTOR_PIXELS, DETECTOR_PIXELS)
    return (ln(pixels / MIN_DETECTOR_PIXELS) / ln(DETECTOR_PIXELS / MIN_DETECTOR_PIXELS)).toFloat()
}

private fun triggerAreaAtSlider(position: Float): Double {
    val clamped = position.coerceIn(0f, 1f).toDouble()
    val pixels = MIN_DETECTOR_PIXELS * (DETECTOR_PIXELS / MIN_DETECTOR_PIXELS).pow(clamped)
    return triggerAreaOf(pixels.roundToInt().toDouble())
}

// A single detector pixel at the small end, 5% further up. Even a log slider can't be dragged to
// an exact value of 6 with a fingertip, and 6 versus 7 is a real difference down there.
private fun nudgedArea(area: Double, direction: Int): Double {
    val pixels = (area * DETECTOR_PIXELS).roundToInt().toDouble()
    val step = if (pixels < 50) 1.0 else (pixels * 0.05).roundToInt().toDouble()
    return triggerAreaOf(pixels + direction * step)
}

// Whole hours on a 24-hour clock, which is how the server stores and reads them. Formatted here
// rather than through the device's time format because these are bare hour numbers with no date
// behind them, and a locale that shows 21 as "9 PM" would still have to round-trip back to 21.
private fun hourLabel(hour: Int): String = "%02d:00".format(hour)

// Length of the pause, wrapping past midnight exactly as TriggerNightWindow.IsActive does: 21 -> 7
// is ten hours, not minus fourteen. Zero means start == end, the zero-width window the server
// refuses to save while the feature is on.
private fun nightWindowHours(start: Int, end: Int): Int {
    val span = end - start
    return if (span > 0) span else span + 24
}

@Composable
fun CameraZoneEditorScreen(
    camera: Camera,
    model: SentinelViewModel,
    onBack: () -> Unit,
) {
    var referenceBitmap by remember { mutableStateOf<Bitmap?>(null) }
    var loadError by remember { mutableStateOf<String?>(null) }
    var isLoading by remember { mutableStateOf(true) }

    // Always at least one entry - see TriggerConfig.resolvedZones. A camera with no zone at all
    // has nothing to mirror into the legacy zonePolygon/minTriggerArea fields.
    var zones by remember {
        mutableStateOf(listOf(TriggerZone(TriggerZone.DEFAULT_NAME, emptyList(), 0.02)))
    }
    var activeZone by remember { mutableIntStateOf(0) }
    var enabled by remember { mutableStateOf(false) }
    var cooldownSeconds by remember { mutableStateOf(5) }
    var maxTriggersPerMinute by remember { mutableStateOf(6) }
    var shadowSensitivity by remember { mutableStateOf(50f) }
    var recordClips by remember { mutableStateOf(false) }
    var clipPreSeconds by remember { mutableStateOf(5) }
    var clipPostSeconds by remember { mutableStateOf(10) }
    var sendToAlerts by remember { mutableStateOf(false) }
    var alertProfile by remember { mutableStateOf("default") }
    var profileNames by remember { mutableStateOf<List<PromptProfileName>>(emptyList()) }

    // Whole-camera, not per-zone: the server holds one window per trigger config. Defaults match
    // TriggerConfig's - off, 21:00-07:00 - so a server that doesn't know about the feature shows
    // the same thing a freshly migrated one does.
    var bypassFastLocal by remember { mutableStateOf(false) }
    var keepOnlyAlertable by remember { mutableStateOf(false) }
    var nightWindowEnabled by remember { mutableStateOf(false) }
    var nightStartHour by remember { mutableIntStateOf(21) }
    var nightEndHour by remember { mutableIntStateOf(7) }

    // Not editable here, only carried: the sensor binding is set up on the web dashboard, and the
    // PUT overwrites the whole record, so dropping these would unbind the camera's sensor every
    // time someone nudged a polygon from their phone.
    var sensorDeviceId by remember { mutableStateOf("") }
    var sensorTriggerOn by remember { mutableStateOf("on") }
    var sensorCooldownSeconds by remember { mutableStateOf(30) }

    var renameTarget by remember { mutableStateOf<Int?>(null) }
    var renameText by remember { mutableStateOf("") }

    var isSaving by remember { mutableStateOf(false) }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    fun mutateActiveZone(mutate: (TriggerZone) -> TriggerZone) {
        val index = activeZone
        if (index !in zones.indices) return
        zones = zones.mapIndexed { i, zone -> if (i == index) mutate(zone) else zone }
    }

    fun apply(config: TriggerConfig) {
        enabled = config.enabled
        // resolvedZones, not zones: a server predating multi-zone sends no list at all, and this
        // editor needs one zone to work with either way.
        zones = config.resolvedZones()
        activeZone = activeZone.coerceIn(0, zones.lastIndex)
        cooldownSeconds = config.cooldownSeconds
        maxTriggersPerMinute = config.maxTriggersPerMinute
        shadowSensitivity = config.shadowSensitivity.toFloat()
        recordClips = config.recordClips
        clipPreSeconds = config.clipPreSeconds
        clipPostSeconds = config.clipPostSeconds
        sendToAlerts = config.sendToAlerts
        alertProfile = config.alertProfile
        sensorDeviceId = config.sensorDeviceId
        sensorTriggerOn = config.sensorTriggerOn
        sensorCooldownSeconds = config.sensorCooldownSeconds
        // resolvedNightWindow(), not the raw nullables: a server predating the feature sends none
        // of the three, and the editor still has to show a window.
        val nightWindow = config.resolvedNightWindow()
        nightWindowEnabled = nightWindow.enabled
        nightStartHour = nightWindow.startHour
        nightEndHour = nightWindow.endHour
        // Resolved, not raw: a server older than either column sends neither key, and the two
        // switches still have to show something.
        bypassFastLocal = config.resolvedBypassFastLocal()
        keepOnlyAlertable = config.resolvedKeepOnlyAlertable()
    }

    suspend fun load() {
        isLoading = true
        loadError = null
        try {
            coroutineScope {
                val imageDeferred = async { model.fetchReferenceFrame(camera.id) }
                val configDeferred = async { model.fetchTriggerConfig(camera.id) }
                val profilesDeferred = async { model.fetchPromptProfileNames() }
                val imageBytes = imageDeferred.await()
                val config = configDeferred.await()
                val profiles = profilesDeferred.await()
                val bitmap = BitmapFactory.decodeByteArray(imageBytes, 0, imageBytes.size)
                if (bitmap == null) {
                    loadError = "The camera's reference frame couldn't be decoded."
                } else {
                    profileNames = profiles
                    apply(config)
                    referenceBitmap = bitmap
                }
            }
        } catch (error: Exception) {
            loadError = error.message ?: "Could not load the reference frame."
        } finally {
            isLoading = false
        }
    }

    LaunchedEffect(camera.id) { load() }

    // Mirrors the server's own validation so the common mistakes are caught before a round-trip,
    // named per zone - a message about the fourth zone shouldn't read as though it were about the
    // one on screen.
    fun validationError(): String? {
        if (enabled) {
            zones.firstOrNull { it.polygon.size < 3 }?.let {
                return "\"${it.name}\" needs at least 3 points, or turn off Enabled."
            }
        }
        if (recordClips && clipPreSeconds + clipPostSeconds <= 0) {
            return "Set at least one second of pre-roll or post-roll to record clips."
        }
        // The server refuses this combination outright rather than reading it as "all day" - see
        // TriggerNightWindow. Catching it here says so next to the two steppers that caused it.
        if (nightWindowEnabled && nightStartHour == nightEndHour) {
            return "The overnight pause can't start and end at the same hour - that window is zero hours long."
        }
        return null
    }

    fun save() {
        val validation = validationError()
        if (validation != null) {
            errorMessage = validation
            return
        }
        scope.launch {
            isSaving = true
            errorMessage = null
            try {
                // zonePolygon/minTriggerArea go out as well as zones, mirroring the primary: the
                // server and any other client still read those two, and the server treats zones as
                // authoritative when it's present. Sending the full list every time is the
                // contract for an editor that understands zones - see
                // core/docs/MULTI_ZONE_CLIENT_HANDOFF.md.
                val primary = zones.firstOrNull()
                    ?: TriggerZone(TriggerZone.DEFAULT_NAME, emptyList(), 0.02)
                val config = TriggerConfig(
                    enabled = enabled,
                    zonePolygon = primary.polygon,
                    minTriggerArea = primary.minTriggerArea,
                    zones = zones,
                    cooldownSeconds = cooldownSeconds,
                    maxTriggersPerMinute = maxTriggersPerMinute,
                    shadowSensitivity = shadowSensitivity.roundToInt(),
                    recordClips = recordClips,
                    clipPreSeconds = clipPreSeconds,
                    clipPostSeconds = clipPostSeconds,
                    sendToAlerts = sendToAlerts,
                    alertProfile = alertProfile,
                    sensorDeviceId = sensorDeviceId,
                    sensorTriggerOn = sensorTriggerOn,
                    sensorCooldownSeconds = sensorCooldownSeconds,
                    // All three, always, and never null from here: the server merges each key it
                    // doesn't receive against the stored row, so sending a partial set would save
                    // half of what's on screen. See core/docs/NIGHT_WINDOW_HANDOFF.md.
                    nightWindowEnabled = nightWindowEnabled,
                    nightStartHour = nightStartHour,
                    nightEndHour = nightEndHour,
                    // Never null from here either, and for the same reason: this editor knows
                    // about both switches, so it owns their value.
                    bypassFastLocal = bypassFastLocal,
                    keepOnlyAlertable = keepOnlyAlertable,
                )
                val saved = model.saveTriggerConfig(camera.id, config)
                apply(saved)
            } catch (error: Exception) {
                errorMessage = error.message ?: "Could not save this trigger zone."
            } finally {
                isSaving = false
            }
        }
    }

    // Falls back to alertProfile appearing as its own option if it's no longer in the fetched
    // list (a renamed/deleted profile) - otherwise saving would silently revert it to "default."
    val profileOptions = if (alertProfile.isNotEmpty() && profileNames.none { it.key == alertProfile }) {
        profileNames + PromptProfileName(alertProfile, alertProfile)
    } else {
        profileNames
    }

    renameTarget?.let { index ->
        AlertDialog(
            onDismissRequest = { renameTarget = null },
            title = { Text("Rename Zone") },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(
                        "The name appears on trigger cards, so you can tell which area fired.",
                        style = MaterialTheme.typography.bodySmall,
                    )
                    OutlinedTextField(
                        value = renameText,
                        onValueChange = { renameText = it },
                        singleLine = true,
                        label = { Text("Zone name") },
                    )
                }
            },
            confirmButton = {
                TextButton(onClick = {
                    // 50 characters is the server's limit; trimming to it here turns a
                    // save-time rejection into something the user never sees.
                    val trimmed = renameText.trim().take(50)
                    if (trimmed.isNotEmpty() && index in zones.indices) {
                        zones = zones.mapIndexed { i, zone ->
                            if (i == index) zone.copy(name = trimmed) else zone
                        }
                    }
                    renameTarget = null
                }) { Text("Rename") }
            },
            dismissButton = {
                TextButton(onClick = { renameTarget = null }) { Text("Cancel") }
            },
        )
    }

    Column(Modifier.fillMaxSize()) {
        DetailHeader("Trigger Zone", onBack) {
            if (isSaving) {
                CircularProgressIndicator(Modifier.size(20.dp))
            } else {
                TextButton(onClick = ::save) { Text("Save") }
            }
        }

        val bitmap = referenceBitmap
        when {
            bitmap != null -> LazyColumn(
                modifier = Modifier.fillMaxSize(),
                contentPadding = PaddingValues(16.dp),
                verticalArrangement = Arrangement.spacedBy(14.dp),
            ) {
                item {
                    Card(Modifier.fillMaxWidth().widthIn(max = 900.dp)) {
                        Column(
                            modifier = Modifier.padding(14.dp),
                            verticalArrangement = Arrangement.spacedBy(10.dp),
                        ) {
                            Text("Zones", style = MaterialTheme.typography.titleSmall)
                            Text(
                                "Each zone has its own minimum object size. Draw one zone per " +
                                    "distance: something far from the camera covers far fewer " +
                                    "pixels than the same thing up close, so a single size can't " +
                                    "work for the whole view.",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                            LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                itemsIndexed(zones) { index, zone ->
                                    FilterChip(
                                        selected = index == activeZone,
                                        onClick = { activeZone = index },
                                        label = {
                                            Text("${zone.name} · ${detectorPixelCount(zone.minTriggerArea)}px")
                                        },
                                    )
                                }
                            }
                            PolygonZoneOverlay(
                                bitmap = bitmap,
                                zones = zones,
                                activeZone = activeZone,
                                onAddPoint = { point ->
                                    mutateActiveZone { it.copy(polygon = it.polygon + point) }
                                },
                            )
                            Row(
                                Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceBetween,
                            ) {
                                TextButton(
                                    onClick = {
                                        mutateActiveZone { it.copy(polygon = it.polygon.dropLast(1)) }
                                    },
                                    enabled = zones.getOrNull(activeZone)?.polygon?.isNotEmpty() == true,
                                ) { Text("Undo Last Point") }
                                TextButton(
                                    onClick = {
                                        mutateActiveZone { it.copy(polygon = emptyList()) }
                                    },
                                    enabled = zones.getOrNull(activeZone)?.polygon?.isNotEmpty() == true,
                                ) { Text("Clear All", color = SentinelRed) }
                            }
                            // A row of its own rather than a third button beside Undo/Clear:
                            // those three titles together overflow the row on a narrow phone,
                            // and Compose would ellipsize the ends.
                            TextButton(
                                onClick = {
                                    mutateActiveZone {
                                        it.copy(polygon = TriggerZone.WHOLE_FRAME_POLYGON)
                                    }
                                },
                                enabled = zones.getOrNull(activeZone)?.polygon !=
                                    TriggerZone.WHOLE_FRAME_POLYGON,
                            ) { Text("Cover Everything") }
                            Row(
                                Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceBetween,
                            ) {
                                // A new zone starts empty and selected, and inherits the current
                                // zone's size as a starting point - you're usually adding a zone
                                // for a different distance, so it's a more useful default than the
                                // stock 2% would be.
                                TextButton(onClick = {
                                    val inherited = zones.getOrNull(activeZone)?.minTriggerArea ?: 0.02
                                    zones = zones + TriggerZone("Zone ${zones.size + 1}", emptyList(), inherited)
                                    activeZone = zones.lastIndex
                                    errorMessage = null
                                }) { Text("Add Zone") }
                                TextButton(onClick = {
                                    renameText = zones.getOrNull(activeZone)?.name.orEmpty()
                                    renameTarget = activeZone
                                }) { Text("Rename") }
                                // A camera always keeps at least one zone: the legacy
                                // zonePolygon/minTriggerArea fields are mirrored from the primary,
                                // so there has to be a primary to mirror.
                                TextButton(
                                    onClick = {
                                        if (zones.size <= 1) {
                                            errorMessage =
                                                "A camera needs at least one zone - clear its points instead."
                                            return@TextButton
                                        }
                                        zones = zones.filterIndexed { i, _ -> i != activeZone }
                                        activeZone = (activeZone - 1).coerceAtLeast(0)
                                        errorMessage = null
                                    },
                                    enabled = zones.size > 1,
                                ) { Text("Delete Zone", color = SentinelRed) }
                            }
                            Row(
                                Modifier.fillMaxWidth(),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                Text("Enabled", modifier = Modifier.weight(1f))
                                Switch(checked = enabled, onCheckedChange = { enabled = it })
                            }
                        }
                    }
                }

                item {
                    Card(Modifier.fillMaxWidth().widthIn(max = 900.dp)) {
                        Column(
                            modifier = Modifier.padding(14.dp),
                            verticalArrangement = Arrangement.spacedBy(10.dp),
                        ) {
                            val zone = zones.getOrNull(activeZone)
                            Text("Minimum Trigger Size", style = MaterialTheme.typography.titleSmall)
                            Text(
                                "The circle is the smallest object size that should count as " +
                                    "motion in ${zone?.name ?: TriggerZone.DEFAULT_NAME}. Set it " +
                                    "with the slider below, or drag on the image for a rough size.",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                            if (zone != null) {
                                MinAreaCircleOverlay(
                                    bitmap = bitmap,
                                    minTriggerArea = zone.minTriggerArea,
                                    onAreaChange = { area ->
                                        mutateActiveZone { it.copy(minTriggerArea = area) }
                                    },
                                )
                                MinAreaSlider(
                                    minTriggerArea = zone.minTriggerArea,
                                    onAreaChange = { area ->
                                        mutateActiveZone { it.copy(minTriggerArea = area) }
                                    },
                                )
                                Text(
                                    "Ignores anything under ${detectorPixelCount(zone.minTriggerArea)} " +
                                        "detector pixels. Sizes in the low tens will also pick up " +
                                        "rain, insects and camera noise.",
                                    style = MaterialTheme.typography.labelSmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                        }
                    }
                }

                item {
                    Card(Modifier.fillMaxWidth().widthIn(max = 900.dp)) {
                        Column(
                            modifier = Modifier.padding(14.dp),
                            verticalArrangement = Arrangement.spacedBy(12.dp),
                        ) {
                            Text("Detection", style = MaterialTheme.typography.titleSmall)
                            NumberStepper(
                                label = "Cooldown",
                                value = cooldownSeconds,
                                range = 0..300,
                                suffix = "s",
                                onValueChange = { cooldownSeconds = it },
                            )
                            NumberStepper(
                                label = "Max Triggers / Minute",
                                value = maxTriggersPerMinute,
                                range = 1..60,
                                onValueChange = { maxTriggersPerMinute = it },
                            )
                            Column {
                                Text("Shadow Sensitivity: ${shadowSensitivity.roundToInt()}")
                                Slider(
                                    value = shadowSensitivity,
                                    onValueChange = { shadowSensitivity = it },
                                    valueRange = 0f..100f,
                                )
                            }
                        }
                    }
                }

                item {
                    // Both switches only do anything while Send to Alerts is on - nothing
                    // analyses the frame otherwise - and both are worth turning on for very few
                    // cameras, so they get their own card rather than lengthening the list above.
                    Card(Modifier.fillMaxWidth().widthIn(max = 900.dp)) {
                        Column(
                            modifier = Modifier.padding(14.dp),
                            verticalArrangement = Arrangement.spacedBy(12.dp),
                        ) {
                            Text("AI Routing", style = MaterialTheme.typography.titleSmall)
                            Row(
                                Modifier.fillMaxWidth(),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                Text("Always Use the Full AI", modifier = Modifier.weight(1f))
                                Switch(
                                    checked = bypassFastLocal,
                                    onCheckedChange = { bypassFastLocal = it },
                                )
                            }
                            Text(
                                "Skips the fast local detector and sends every trigger from this " +
                                    "camera straight to the full AI. Only matters while Send to " +
                                    "Alerts is on, and only when Fast Local Detection is the " +
                                    "primary backend. The detector is trained on a fixed class " +
                                    "list with no raccoon, fox, opossum or weasel in it, so at a " +
                                    "chicken coop it clears those frames as empty and the model " +
                                    "that could name the predator never sees them. The cost is " +
                                    "real: every trigger becomes a full analysis of around two " +
                                    "minutes, so raise the cooldown above to match or the queue " +
                                    "backs up and starts dropping frames unanalysed.",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                            Row(
                                Modifier.fillMaxWidth(),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                Text(
                                    "Only Keep What the AI Would Alert On",
                                    modifier = Modifier.weight(1f),
                                )
                                Switch(
                                    checked = keepOnlyAlertable,
                                    onCheckedChange = { keepOnlyAlertable = it },
                                )
                            }
                            Text(
                                "Judges this camera's triggers by whether the AI would have " +
                                    "alerted on the frame, instead of by whether anything was in " +
                                    "it. The normal bar is \"a person, vehicle, package or animal " +
                                    "is there\", which is right for a driveway but wrong at a " +
                                    "chicken coop: the chickens are animals, they are always in " +
                                    "shot, and they confirm every trigger. With this on, ordinary " +
                                    "chicken activity is rejected - no event, no video kept, no " +
                                    "alert - while a predator still gets all three. The snapshot " +
                                    "is kept either way, so Show Hidden still shows everything " +
                                    "that fired. Nothing is rejected because the AI was slow, " +
                                    "unreachable or errored.",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }
                }

                item {
                    Card(Modifier.fillMaxWidth().widthIn(max = 900.dp)) {
                        Column(
                            modifier = Modifier.padding(14.dp),
                            verticalArrangement = Arrangement.spacedBy(12.dp),
                        ) {
                            Text("Overnight Pause", style = MaterialTheme.typography.titleSmall)
                            Row(
                                Modifier.fillMaxWidth(),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                Text("Pause Triggers Overnight", modifier = Modifier.weight(1f))
                                Switch(
                                    checked = nightWindowEnabled,
                                    onCheckedChange = { nightWindowEnabled = it },
                                )
                            }
                            Text(
                                "Holds back every motion trigger between the hours below. Meant " +
                                    "for a camera whose IR light picks up a spider web, insects " +
                                    "or condensation close to the lens after dark: those blobs " +
                                    "measure the same on screen as a person further away, so no " +
                                    "minimum size can tell them apart - but the time of day can. " +
                                    "Nothing is captured, recorded or analysed while paused. A " +
                                    "sensor binding still fires normally.",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                            if (nightWindowEnabled) {
                                HourStepper(
                                    label = "Pause From",
                                    hour = nightStartHour,
                                    onHourChange = { nightStartHour = it },
                                )
                                HourStepper(
                                    label = "Resume At",
                                    hour = nightEndHour,
                                    onHourChange = { nightEndHour = it },
                                )
                                // Spelling the window back out is the point of this line: 21 -> 7
                                // wraps past midnight, and "10 hours a night" is the sanity check
                                // on whether the two numbers say what was meant. The hours are the
                                // server's clock, not this device's - a phone in another timezone
                                // would otherwise read them as its own.
                                val zeroWidth = nightStartHour == nightEndHour
                                val hours = nightWindowHours(nightStartHour, nightEndHour)
                                Text(
                                    if (zeroWidth) {
                                        "Start and end are the same hour, which is a window zero " +
                                            "hours long - triggers would never pause. Pick " +
                                            "different hours."
                                    } else {
                                        "Triggers pause at ${hourLabel(nightStartHour)} and " +
                                            "resume at ${hourLabel(nightEndHour)} - $hours " +
                                            "hour${if (hours == 1) "" else "s"} a night, on the " +
                                            "server's clock."
                                    },
                                    style = MaterialTheme.typography.labelSmall,
                                    color = if (zeroWidth) SentinelRed else MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                        }
                    }
                }

                item {
                    Card(Modifier.fillMaxWidth().widthIn(max = 900.dp)) {
                        Column(
                            modifier = Modifier.padding(14.dp),
                            verticalArrangement = Arrangement.spacedBy(12.dp),
                        ) {
                            Text("Clips & Alerts", style = MaterialTheme.typography.titleSmall)
                            Row(
                                Modifier.fillMaxWidth(),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                Text("Record Clips", modifier = Modifier.weight(1f))
                                Switch(checked = recordClips, onCheckedChange = { recordClips = it })
                            }
                            if (recordClips) {
                                NumberStepper(
                                    label = "Pre-roll",
                                    value = clipPreSeconds,
                                    range = 0..60,
                                    suffix = "s",
                                    onValueChange = { clipPreSeconds = it },
                                )
                                NumberStepper(
                                    label = "Post-roll",
                                    value = clipPostSeconds,
                                    range = 0..120,
                                    suffix = "s",
                                    onValueChange = { clipPostSeconds = it },
                                )
                            }
                            Row(
                                Modifier.fillMaxWidth(),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                Text("Send to Alerts", modifier = Modifier.weight(1f))
                                Switch(checked = sendToAlerts, onCheckedChange = { sendToAlerts = it })
                            }
                            AlertProfileDropdown(
                                options = profileOptions,
                                selected = alertProfile,
                                onSelected = { alertProfile = it },
                            )
                        }
                    }
                }

                errorMessage?.let { message ->
                    item { Text(message, color = SentinelRed) }
                }
            }
            isLoading -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                CircularProgressIndicator(color = SentinelGreen)
            }
            else -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Column(
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.spacedBy(12.dp),
                ) {
                    Text("Couldn't load reference frame", style = MaterialTheme.typography.titleMedium)
                    Text(
                        loadError ?: "Unknown error",
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    Button(onClick = { scope.launch { load() } }) { Text("Retry") }
                }
            }
        }
    }
}

// Every zone is drawn, but only the selected one is solid and shows its vertices - the others are
// dashed and faint so you can see how they line up without mistaking them for the shape your taps
// are going into. The selected zone is drawn last so it sits on top.
@Composable
private fun PolygonZoneOverlay(
    bitmap: Bitmap,
    zones: List<TriggerZone>,
    activeZone: Int,
    onAddPoint: (Point2D) -> Unit,
) {
    // Canvas's draw scope isn't a composable context, so theme-aware colors are resolved here
    // first rather than read live inside the draw block below.
    val polygonColor = SentinelGreen
    // pointerInput(Unit) installs its gesture-detection coroutine once and never restarts it, so
    // the lambda below must not close over `onAddPoint` directly - the caller's lambda captures
    // `activeZone`, and freezing on the first composition's value would send every tap into
    // whichever zone was selected when the screen opened. rememberUpdatedState keeps the read live.
    val currentOnAddPoint = rememberUpdatedState(onAddPoint)
    val textMeasurer = rememberTextMeasurer()
    val labelStyle = MaterialTheme.typography.labelSmall
    Box(
        Modifier.fillMaxWidth()
            .aspectRatio(bitmap.width.toFloat() / bitmap.height.toFloat())
            .clip(RoundedCornerShape(8.dp)),
    ) {
        Image(
            bitmap = bitmap.asImageBitmap(),
            contentDescription = null,
            modifier = Modifier.fillMaxSize(),
            contentScale = ContentScale.Fit,
        )
        Canvas(
            modifier = Modifier.fillMaxSize()
                .pointerInput(Unit) {
                    detectTapGestures { offset ->
                        val fractionX = (offset.x / size.width).coerceIn(0f, 1f)
                        val fractionY = (offset.y / size.height).coerceIn(0f, 1f)
                        currentOnAddPoint.value(Point2D(fractionX.toDouble(), fractionY.toDouble()))
                    }
                },
        ) {
            val order = zones.indices.filter { it != activeZone } +
                listOfNotNull(activeZone.takeIf { it in zones.indices })
            for (index in order) {
                val zone = zones[index]
                if (zone.polygon.isEmpty()) continue
                val isActive = index == activeZone

                val path = Path()
                zone.polygon.forEachIndexed { pointIndex, point ->
                    val x = (point.x * size.width).toFloat()
                    val y = (point.y * size.height).toFloat()
                    if (pointIndex == 0) path.moveTo(x, y) else path.lineTo(x, y)
                }
                if (zone.polygon.size >= 3) path.close()

                drawPath(path, color = polygonColor.copy(alpha = if (isActive) 0.18f else 0.06f))
                drawPath(
                    path,
                    color = if (isActive) polygonColor else polygonColor.copy(alpha = 0.45f),
                    style = Stroke(
                        width = 4f,
                        pathEffect = if (isActive) null else PathEffect.dashPathEffect(floatArrayOf(12f, 9f)),
                    ),
                )

                // Named at its first vertex, like the web build, so the dimmed shapes are still
                // tellable apart while editing another one.
                zone.polygon.firstOrNull()?.let { first ->
                    drawText(
                        textMeasurer = textMeasurer,
                        text = zone.name,
                        topLeft = Offset(
                            (first.x * size.width).toFloat() + 8f,
                            (first.y * size.height).toFloat() - 40f,
                        ),
                        style = labelStyle.copy(
                            color = if (isActive) polygonColor else polygonColor.copy(alpha = 0.5f),
                        ),
                    )
                }

                if (!isActive) continue
                zone.polygon.forEach { point ->
                    drawCircle(
                        color = polygonColor,
                        radius = 10f,
                        center = Offset((point.x * size.width).toFloat(), (point.y * size.height).toFloat()),
                    )
                }
            }
        }
    }
}

@Composable
private fun MinAreaCircleOverlay(
    bitmap: Bitmap,
    minTriggerArea: Double,
    onAreaChange: (Double) -> Unit,
) {
    val circleColor = SentinelCyan
    // Same pointerInput(Unit) trap as PolygonZoneOverlay: the caller's lambda captures the active
    // zone index, so a frozen capture would keep resizing whichever zone was selected at first
    // composition no matter which tab is active now.
    val currentOnAreaChange = rememberUpdatedState(onAreaChange)
    Box(
        Modifier.fillMaxWidth()
            .aspectRatio(bitmap.width.toFloat() / bitmap.height.toFloat())
            .clip(RoundedCornerShape(8.dp)),
    ) {
        Image(
            bitmap = bitmap.asImageBitmap(),
            contentDescription = null,
            modifier = Modifier.fillMaxSize(),
            contentScale = ContentScale.Fit,
        )
        Canvas(
            // Dragging anywhere in this pane resizes the circle - simpler and more reliable on
            // touch than requiring a precise grab on a small handle, and this control has no
            // other interactive purpose.
            modifier = Modifier.fillMaxSize()
                .pointerInput(Unit) {
                    detectDragGestures { change, _ ->
                        change.consume()
                        val center = Offset(size.width / 2f, size.height / 2f)
                        val dx = change.position.x - center.x
                        val dy = change.position.y - center.y
                        val maxRadius = minOf(size.width, size.height) / 2f
                        // No floor on the radius any more, only the domain floor triggerAreaOf
                        // applies: a 10-pixel minimum here is what made small sizes unreachable,
                        // and it did it differently on every screen size.
                        val newRadius = sqrt(dx * dx + dy * dy).coerceIn(0f, maxRadius)
                        val area = (PI * newRadius * newRadius) /
                            (size.width.toDouble() * size.height.toDouble())
                        currentOnAreaChange.value(triggerAreaOf(area * DETECTOR_PIXELS))
                    }
                },
        ) {
            val center = Offset(size.width / 2f, size.height / 2f)
            val radius = sqrt(minTriggerArea * size.width * size.height / PI).toFloat()
            drawCircle(color = circleColor.copy(alpha = 0.15f), radius = radius, center = center)
            drawCircle(color = circleColor, radius = radius, center = center, style = Stroke(width = 4f))
            // At the small end of the range the circle is a couple of pixels across and
            // effectively invisible against a busy frame. The dashed ring says "the size you set
            // is in here" without pretending to be the size itself.
            if (radius < LOCATOR_RADIUS_PX) {
                drawCircle(
                    color = circleColor.copy(alpha = 0.5f),
                    radius = LOCATOR_RADIUS_PX,
                    center = center,
                    style = Stroke(
                        width = 2f,
                        pathEffect = PathEffect.dashPathEffect(floatArrayOf(6f, 6f)),
                    ),
                )
            }
        }
    }
}

// Radius of the dashed "the circle is in here" ring drawn when the real circle is too small to
// find by eye.
private const val LOCATOR_RADIUS_PX = 18f

// Why this exists alongside the drag-on-the-image control: on a phone the circle is the thing you
// are aiming at and your fingertip is sitting on top of it, so the smaller it gets the less you
// can see of what you are setting - and small is where this control now spends most of its range.
// The slider is off to one side of the picture, and the -/+ buttons land on an exact value.
@Composable
private fun MinAreaSlider(
    minTriggerArea: Double,
    onAreaChange: (Double) -> Unit,
) {
    Row(
        Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        OutlinedButton(
            onClick = { onAreaChange(nudgedArea(minTriggerArea, -1)) },
            enabled = detectorPixelCount(minTriggerArea) > MIN_DETECTOR_PIXELS,
        ) { Text("−") }
        Slider(
            value = sliderPositionOf(minTriggerArea),
            onValueChange = { onAreaChange(triggerAreaAtSlider(it)) },
            valueRange = 0f..1f,
            modifier = Modifier.weight(1f).padding(horizontal = 12.dp),
        )
        OutlinedButton(
            onClick = { onAreaChange(nudgedArea(minTriggerArea, 1)) },
            enabled = minTriggerArea < 1.0,
        ) { Text("+") }
    }
}

@Composable
private fun NumberStepper(
    label: String,
    value: Int,
    range: IntRange,
    onValueChange: (Int) -> Unit,
    suffix: String = "",
) {
    Row(
        Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text("$label: $value$suffix", modifier = Modifier.weight(1f))
        OutlinedButton(
            onClick = { onValueChange((value - 1).coerceIn(range)) },
            enabled = value > range.first,
        ) { Text("−") }
        OutlinedButton(
            onClick = { onValueChange((value + 1).coerceIn(range)) },
            enabled = value < range.last,
            modifier = Modifier.padding(start = 8.dp),
        ) { Text("+") }
    }
}

// A stepper rather than a text field: there are 24 legal values and no reason to let someone type
// 47 and find out at save time. It wraps rather than clamping, because hours are a cycle and the
// hour someone wants next to 00:00 is 23:00 - the same reason the window itself wraps past
// midnight.
@Composable
private fun HourStepper(
    label: String,
    hour: Int,
    onHourChange: (Int) -> Unit,
) {
    Row(
        Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text("$label: ${hourLabel(hour)}", modifier = Modifier.weight(1f))
        OutlinedButton(onClick = { onHourChange((hour + 23) % 24) }) { Text("−") }
        OutlinedButton(
            onClick = { onHourChange((hour + 1) % 24) },
            modifier = Modifier.padding(start = 8.dp),
        ) { Text("+") }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun AlertProfileDropdown(
    options: List<PromptProfileName>,
    selected: String,
    onSelected: (String) -> Unit,
) {
    var expanded by remember { mutableStateOf(false) }
    val selectedLabel = options.firstOrNull { it.key == selected }?.displayName ?: selected
    ExposedDropdownMenuBox(expanded = expanded, onExpandedChange = { expanded = it }) {
        OutlinedTextField(
            value = selectedLabel,
            onValueChange = {},
            readOnly = true,
            label = { Text("Alert Profile") },
            trailingIcon = { ExposedDropdownMenuDefaults.TrailingIcon(expanded = expanded) },
            modifier = Modifier.fillMaxWidth().menuAnchor(MenuAnchorType.PrimaryEditable, true),
        )
        ExposedDropdownMenu(
            expanded = expanded,
            onDismissRequest = { expanded = false },
        ) {
            options.forEach { option ->
                DropdownMenuItem(
                    text = { Text(option.displayName) },
                    onClick = {
                        onSelected(option.key)
                        expanded = false
                    },
                )
            }
        }
    }
}
