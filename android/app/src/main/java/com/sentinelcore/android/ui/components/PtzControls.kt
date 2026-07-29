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
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelGreen
import kotlinx.coroutines.launch

// Continuous-move semantics: pressing a direction starts the camera moving at a fixed moderate
// speed, and it keeps moving until released - there's no ONVIF "move this far and stop
// automatically" in this mode. Errors are swallowed on purpose (fire-and-forget) - a dropped PTZ
// command just means the camera didn't move that instant, which is self-evident from the video
// itself, not something worth surfacing as an error over.
private const val PTZ_SPEED = 0.5

@Composable
fun PtzControls(cameraId: String, model: SentinelViewModel, modifier: Modifier = Modifier) {
    val scope = rememberCoroutineScope()

    fun move(pan: Double, tilt: Double, zoom: Double) {
        scope.launch { runCatching { model.movePtz(cameraId, pan, tilt, zoom) } }
    }

    fun stop() {
        scope.launch { runCatching { model.stopPtz(cameraId) } }
    }

    fun home() {
        scope.launch { runCatching { model.homePtz(cameraId) } }
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
        Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
            directions.chunked(3).forEach { row ->
                Row(horizontalArrangement = Arrangement.spacedBy(3.dp)) {
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
        Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
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
    Box(
        modifier = Modifier
            .size(30.dp)
            .background(Color.Black.copy(alpha = 0.55f), RoundedCornerShape(4.dp))
            .border(1.dp, tint.copy(alpha = 0.4f), RoundedCornerShape(4.dp))
            .pointerInput(Unit) {
                detectTapGestures(
                    onPress = {
                        onPress()
                        try {
                            awaitRelease()
                        } finally {
                            onRelease()
                        }
                    },
                )
            },
        contentAlignment = Alignment.Center,
    ) {
        Icon(icon, contentDescription = null, tint = tint, modifier = Modifier.size(16.dp))
    }
}
