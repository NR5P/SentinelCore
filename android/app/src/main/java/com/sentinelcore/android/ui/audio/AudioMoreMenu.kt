package com.sentinelcore.android.ui.audio

import androidx.compose.foundation.layout.Box
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Alarm
import androidx.compose.material.icons.filled.AutoAwesome
import androidx.compose.material.icons.filled.BarChart
import androidx.compose.material.icons.filled.Cast
import androidx.compose.material.icons.filled.DownloadForOffline
import androidx.compose.material.icons.filled.Equalizer
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.WbTwilight
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.vector.ImageVector

// The ⋮ in the Music and Podcasts headers: the features the two tabs share.
@Composable
fun AudioMoreMenu(extra: @Composable (close: () -> Unit) -> Unit = {}) {
    val ui = LocalAudioUi.current
    var open by remember { mutableStateOf(false) }
    Box {
        IconButton(onClick = { open = true }) { Icon(Icons.Default.MoreVert, contentDescription = "More") }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            val close = { open = false }
            extra(close)
            Item(Icons.Default.DownloadForOffline, "Downloads") { close(); ui.nav.open(AudioRoutes.DOWNLOADS) }
            Item(Icons.Default.Cast, "Play on…") { close(); ui.speakerPicker = true }
            Item(Icons.Default.WbTwilight, "Morning Briefing") { close(); ui.nav.open(AudioRoutes.BRIEFING) }
            Item(Icons.Default.Alarm, "Wake-up alarms") { close(); ui.nav.open(AudioRoutes.ALARMS) }
            HorizontalDivider()
            Item(Icons.Default.BarChart, "Listening stats") { close(); ui.nav.open(AudioRoutes.STATS) }
            Item(Icons.Default.AutoAwesome, "Your Wrapped") { close(); ui.nav.open(AudioRoutes.WRAPPED) }
            Item(Icons.Default.Equalizer, "Equalizer") { close(); ui.nav.open(AudioRoutes.EQUALIZER) }
            Item(Icons.Default.Settings, "Music & Podcasts settings") { close(); ui.nav.open(AudioRoutes.SETTINGS) }
        }
    }
}

@Composable
fun Item(icon: ImageVector, label: String, onClick: () -> Unit) {
    DropdownMenuItem(text = { Text(label) }, leadingIcon = { Icon(icon, contentDescription = null) }, onClick = onClick)
}
