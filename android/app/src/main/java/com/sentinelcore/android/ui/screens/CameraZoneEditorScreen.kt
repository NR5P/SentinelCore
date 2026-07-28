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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExposedDropdownMenuBox
import androidx.compose.material3.ExposedDropdownMenuDefaults
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
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.data.Camera
import com.sentinelcore.android.data.Point2D
import com.sentinelcore.android.data.PromptProfileName
import com.sentinelcore.android.data.TriggerConfig
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch
import kotlin.math.PI
import kotlin.math.roundToInt
import kotlin.math.sqrt

@Composable
fun CameraZoneEditorScreen(
    camera: Camera,
    model: SentinelViewModel,
    onBack: () -> Unit,
) {
    var referenceBitmap by remember { mutableStateOf<Bitmap?>(null) }
    var loadError by remember { mutableStateOf<String?>(null) }
    var isLoading by remember { mutableStateOf(true) }

    var polygonPoints by remember { mutableStateOf<List<Point2D>>(emptyList()) }
    var minTriggerArea by remember { mutableStateOf(0.02) }
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

    var isSaving by remember { mutableStateOf(false) }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    fun apply(config: TriggerConfig) {
        enabled = config.enabled
        polygonPoints = config.zonePolygon
        minTriggerArea = config.minTriggerArea
        cooldownSeconds = config.cooldownSeconds
        maxTriggersPerMinute = config.maxTriggersPerMinute
        shadowSensitivity = config.shadowSensitivity.toFloat()
        recordClips = config.recordClips
        clipPreSeconds = config.clipPreSeconds
        clipPostSeconds = config.clipPostSeconds
        sendToAlerts = config.sendToAlerts
        alertProfile = config.alertProfile
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

    fun validationError(): String? = when {
        enabled && polygonPoints.size < 3 -> "Draw a zone with at least 3 points, or turn off Enabled."
        recordClips && clipPreSeconds + clipPostSeconds <= 0 ->
            "Set at least one second of pre-roll or post-roll to record clips."
        else -> null
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
                val config = TriggerConfig(
                    enabled = enabled,
                    zonePolygon = polygonPoints,
                    minTriggerArea = minTriggerArea,
                    cooldownSeconds = cooldownSeconds,
                    maxTriggersPerMinute = maxTriggersPerMinute,
                    shadowSensitivity = shadowSensitivity.roundToInt(),
                    recordClips = recordClips,
                    clipPreSeconds = clipPreSeconds,
                    clipPostSeconds = clipPostSeconds,
                    sendToAlerts = sendToAlerts,
                    alertProfile = alertProfile,
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
                            Text("Zone", style = MaterialTheme.typography.titleSmall)
                            PolygonZoneOverlay(
                                bitmap = bitmap,
                                points = polygonPoints,
                                onPointsChange = { polygonPoints = it },
                            )
                            Row(
                                Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceBetween,
                            ) {
                                TextButton(
                                    onClick = { polygonPoints = polygonPoints.dropLast(1) },
                                    enabled = polygonPoints.isNotEmpty(),
                                ) { Text("Undo Last Point") }
                                TextButton(
                                    onClick = { polygonPoints = emptyList() },
                                    enabled = polygonPoints.isNotEmpty(),
                                ) { Text("Clear All", color = SentinelRed) }
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
                            Text("Minimum Trigger Size", style = MaterialTheme.typography.titleSmall)
                            Text(
                                "Drag anywhere on the image to resize the circle to the smallest " +
                                    "object size that should count as motion.",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                            MinAreaCircleOverlay(
                                bitmap = bitmap,
                                minTriggerArea = minTriggerArea,
                                onAreaChange = { minTriggerArea = it },
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

@Composable
private fun PolygonZoneOverlay(
    bitmap: Bitmap,
    points: List<Point2D>,
    onPointsChange: (List<Point2D>) -> Unit,
) {
    // Canvas's draw scope isn't a composable context, so theme-aware colors are resolved here
    // first rather than read live inside the draw block below.
    val polygonColor = SentinelGreen
    // pointerInput(Unit) installs its gesture-detection coroutine once and never restarts it, so
    // the lambda below must not close over `points` directly - that would freeze on whatever
    // value was in scope at first composition (empty), making every tap reset the polygon to a
    // single point instead of appending. rememberUpdatedState keeps the read live.
    val currentPoints = rememberUpdatedState(points)
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
                        onPointsChange(currentPoints.value + Point2D(fractionX.toDouble(), fractionY.toDouble()))
                    }
                },
        ) {
            if (points.isEmpty()) return@Canvas
            val path = Path()
            points.forEachIndexed { index, point ->
                val x = (point.x * size.width).toFloat()
                val y = (point.y * size.height).toFloat()
                if (index == 0) path.moveTo(x, y) else path.lineTo(x, y)
            }
            if (points.size >= 3) path.close()
            drawPath(path, color = polygonColor.copy(alpha = 0.18f))
            drawPath(path, color = polygonColor, style = Stroke(width = 4f))
            points.forEach { point ->
                drawCircle(
                    color = polygonColor,
                    radius = 10f,
                    center = Offset((point.x * size.width).toFloat(), (point.y * size.height).toFloat()),
                )
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
                        val newRadius = sqrt(dx * dx + dy * dy).coerceIn(10f, maxRadius)
                        val area = (PI * newRadius * newRadius) /
                            (size.width.toDouble() * size.height.toDouble())
                        onAreaChange(area.coerceIn(0.0001, 1.0))
                    }
                },
        ) {
            val center = Offset(size.width / 2f, size.height / 2f)
            val radius = sqrt(minTriggerArea * size.width * size.height / PI).toFloat()
            drawCircle(color = circleColor.copy(alpha = 0.15f), radius = radius, center = center)
            drawCircle(color = circleColor, radius = radius, center = center, style = Stroke(width = 4f))
        }
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
