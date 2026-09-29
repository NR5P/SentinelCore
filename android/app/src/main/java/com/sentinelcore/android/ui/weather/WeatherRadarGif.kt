package com.sentinelcore.android.ui.weather

import android.graphics.BitmapFactory
import android.graphics.ImageDecoder
import android.graphics.drawable.AnimatedImageDrawable
import android.graphics.drawable.BitmapDrawable
import android.graphics.drawable.Drawable
import android.os.Build
import android.widget.ImageView
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import com.sentinelcore.android.ui.theme.SentinelTextDim
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import java.nio.ByteBuffer

// The animated NWS radar picture, for a server from before the radar map (no /api/weather/map).
// The same local and national loops the dashboard's Weather tab first shipped with, refreshed
// every two minutes. Animated on Android 9+ (AnimatedImageDrawable); older phones get the loop's
// first frame, which is still the latest picture of the radar.
@Composable
internal fun RadarGifPanel(wx: WeatherScreenModel) {
    var scope by rememberSaveable { mutableStateOf("local") }
    var drawable by remember { mutableStateOf<Drawable?>(null) }
    var failed by remember { mutableStateOf(false) }

    LaunchedEffect(scope) {
        drawable = null
        failed = false
        while (true) {
            val bytes = wx.radarGif(scope)
            val decoded = bytes?.let { withContext(Dispatchers.Default) { decodeGif(it) } }
            if (decoded != null) {
                drawable = decoded
                failed = false
            } else if (drawable == null) {
                failed = true
            }
            delay(RADAR_REFRESH_MS)
        }
    }

    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text("RADAR", style = MaterialTheme.typography.labelLarge, fontWeight = FontWeight.Bold)
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            FilterChip(scope == "local", { scope = "local" }, label = { Text("Local") })
            FilterChip(scope == "national", { scope = "national" }, label = { Text("National") })
        }
        Box(Modifier.fillMaxWidth().aspectRatio(600f / 550f), contentAlignment = Alignment.Center) {
            val current = drawable
            when {
                current != null -> {
                    AndroidView(
                        factory = { ImageView(it).apply { scaleType = ImageView.ScaleType.FIT_CENTER; adjustViewBounds = true } },
                        update = { view ->
                            if (view.drawable !== current) {
                                view.setImageDrawable(current)
                                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P && current is AnimatedImageDrawable) current.start()
                            }
                        },
                        modifier = Modifier.fillMaxWidth(),
                    )
                    DisposableEffect(current) {
                        onDispose {
                            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P && current is AnimatedImageDrawable) current.stop()
                        }
                    }
                }
                failed -> Text("The radar picture is unavailable right now.", color = SentinelTextDim)
                else -> CircularProgressIndicator()
            }
        }
        Text(
            if (scope == "local") "The nearest National Weather Service radar's last hour or so, looping."
            else "The whole country's radar mosaic, looping.",
            style = MaterialTheme.typography.bodySmall,
            color = SentinelTextDim,
        )
    }
}

private fun decodeGif(bytes: ByteArray): Drawable? = runCatching {
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
        ImageDecoder.decodeDrawable(ImageDecoder.createSource(ByteBuffer.wrap(bytes)))
    } else {
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size)?.let { BitmapDrawable(null, it) }
    }
}.getOrNull()
