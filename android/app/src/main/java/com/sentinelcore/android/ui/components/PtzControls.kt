package com.sentinelcore.android.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.East
import androidx.compose.material.icons.filled.Home
import androidx.compose.material.icons.filled.North
import androidx.compose.material.icons.filled.NorthEast
import androidx.compose.material.icons.filled.NorthWest
import androidx.compose.material.icons.filled.Remove
import androidx.compose.material.icons.filled.South
import androidx.compose.material.icons.filled.SouthEast
import androidx.compose.material.icons.filled.SouthWest
import androidx.compose.material.icons.filled.West
import androidx.compose.material3.Icon
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelGreen

// Continuous-move semantics: pressing a direction starts the camera moving at a fixed moderate
// speed, and it keeps moving until released - there's no ONVIF "move this far and stop
// automatically" in this mode. That makes the release the load-bearing command: a dropped move
// means the camera didn't budge, which is self-evident from the video, but a dropped stop means a
// camera that keeps turning. Ordering, retrying and scoping of the commands therefore live in the
// ViewModel (see enqueuePtz) rather than here.
private const val PTZ_SPEED = 0.5
private val PTZ_BUTTON_SIZE = 48.dp
private val PTZ_BUTTON_GAP = 6.dp

@Composable
fun PtzControls(cameraId: String, model: SentinelViewModel, modifier: Modifier = Modifier) {
    fun move(pan: Double, tilt: Double, zoom: Double) = model.startPtzMove(cameraId, pan, tilt, zoom)

    fun stop() = model.endPtzMove(cameraId)

    fun home() = model.goPtzHome(cameraId)

    // Belt and braces alongside the finally block in PtzButton: if this control leaves the
    // composition while a button is held, send a stop on the way out. A stop with nothing moving
    // is a no-op, so this costs nothing when the press ended normally.
    DisposableEffect(cameraId) {
        onDispose { stop() }
    }

    Row(
        modifier
            .background(Color.Black.copy(alpha = 0.35f), RoundedCornerShape(8.dp))
            .padding(8.dp),
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        // 3x3 compass grid, home button in the center - matches the web dashboard and iOS
        // layout exactly, same directions/icons.
        val directions: List<Triple<Double, Double, ImageVector>?> = listOf(
            Triple(-1.0, 1.0, Icons.Default.NorthWest), Triple(0.0, 1.0, Icons.Default.North), Triple(1.0, 1.0, Icons.Default.NorthEast),
            Triple(-1.0, 0.0, Icons.Default.West), null, Triple(1.0, 0.0, Icons.Default.East),
            Triple(-1.0, -1.0, Icons.Default.SouthWest), Triple(0.0, -1.0, Icons.Default.South), Triple(1.0, -1.0, Icons.Default.SouthEast),
        )
        Column(verticalArrangement = Arrangement.spacedBy(PTZ_BUTTON_GAP)) {
            directions.chunked(3).forEach { row ->
                Row(horizontalArrangement = Arrangement.spacedBy(PTZ_BUTTON_GAP)) {
                    for (dir in row) {
                        if (dir == null) {
                            PtzButton(Icons.Default.Home, tint = SentinelCyan, onPress = ::home, onRelease = {})
                        } else {
                            val (pan, tilt, icon) = dir
                            PtzButton(icon, onPress = { move(pan * PTZ_SPEED, tilt * PTZ_SPEED, 0.0) }, onRelease = ::stop)
                        }
                    }
                }
            }
        }
        Column(verticalArrangement = Arrangement.spacedBy(PTZ_BUTTON_GAP)) {
            PtzButton(Icons.Default.Add, onPress = { move(0.0, 0.0, PTZ_SPEED) }, onRelease = ::stop)
            PtzButton(Icons.Default.Remove, onPress = { move(0.0, 0.0, -PTZ_SPEED) }, onRelease = ::stop)
        }
    }
}

// detectTapGestures' onPress is a suspend callback that runs for the entire press: it fires
// immediately on touch-down, then awaitRelease() suspends until the finger lifts (or the
// gesture is cancelled, e.g. scrolled away) - the finally block is what guarantees onRelease
// fires even on a cancelled gesture, so a camera can't be left moving indefinitely.
@Composable
private fun PtzButton(
    icon: ImageVector,
    tint: Color = SentinelGreen,
    onPress: () -> Unit,
    onRelease: () -> Unit,
) {
    var pressed by remember { mutableStateOf(false) }
    Box(
        modifier = Modifier
            // 48dp is Material's minimum touch target. These were 30dp with 3dp between them, in a
            // 3x3 grid of arrows - small enough that hitting a neighbouring direction was routine.
            .size(PTZ_BUTTON_SIZE)
            .background(
                Color.Black.copy(alpha = if (pressed) 0.85f else 0.55f),
                RoundedCornerShape(6.dp),
            )
            .border(
                if (pressed) 2.dp else 1.dp,
                tint.copy(alpha = if (pressed) 1f else 0.4f),
                RoundedCornerShape(6.dp),
            )
            .pointerInput(Unit) {
                detectTapGestures(
                    onPress = {
                        pressed = true
                        onPress()
                        try {
                            awaitRelease()
                        } finally {
                            pressed = false
                            onRelease()
                        }
                    },
                )
            },
        contentAlignment = Alignment.Center,
    ) {
        Icon(icon, contentDescription = null, tint = tint, modifier = Modifier.size(24.dp))
    }
}
