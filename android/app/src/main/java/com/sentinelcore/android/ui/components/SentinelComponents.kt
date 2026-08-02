package com.sentinelcore.android.ui.components

import android.graphics.Bitmap
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.BrokenImage
import androidx.compose.material.icons.filled.Shield
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.data.ConnectionState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelBackground
import com.sentinelcore.android.ui.theme.SentinelCard
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelGreenDim
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelTextDim
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.util.Locale

// Reserved for surfaces that should still look distinctly "Sentinel" rather than a generic
// Material container - today just the setup/login card. Everywhere else that used to reach for
// this (event rows, grid tiles, settings sections) now uses a plain Card instead.
@Composable
fun BrandedCard(
    modifier: Modifier = Modifier,
    content: @Composable () -> Unit,
) {
    Box(
        modifier = modifier
            .clip(RoundedCornerShape(8.dp))
            .background(SentinelCard)
            .border(1.dp, SentinelGreenDim.copy(alpha = 0.45f), RoundedCornerShape(8.dp))
            .padding(14.dp),
    ) {
        content()
    }
}

@Composable
fun ConnectionBadge(
    state: ConnectionState,
    modifier: Modifier = Modifier,
) {
    val color = when (state) {
        ConnectionState.ONLINE -> SentinelGreen
        ConnectionState.CHECKING,
        ConnectionState.AUTHENTICATION_REQUIRED,
        -> SentinelAmber
        ConnectionState.OFFLINE -> SentinelRed
        ConnectionState.NOT_CONFIGURED -> SentinelTextDim
    }
    Row(
        modifier = modifier.semantics {
            contentDescription = "Server status: ${state.label}"
        },
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(8.dp).background(color, CircleShape))
        Text(
            text = state.label,
            color = SentinelTextDim,
            style = MaterialTheme.typography.labelSmall,
            maxLines = 1,
        )
    }
}

@Composable
fun StatusChip(
    text: String,
    color: Color,
    modifier: Modifier = Modifier,
) {
    Box(
        modifier = modifier
            .clip(CircleShape)
            .background(color.copy(alpha = 0.1f))
            .border(1.dp, color.copy(alpha = 0.4f), CircleShape)
            .padding(horizontal = 8.dp, vertical = 5.dp),
    ) {
        Text(
            text = text,
            color = color,
            style = MaterialTheme.typography.labelSmall,
            fontWeight = FontWeight.Bold,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

@Composable
fun SentinelRemoteImage(
    model: SentinelViewModel,
    path: String?,
    modifier: Modifier = Modifier,
    contentScale: ContentScale = ContentScale.Crop,
    description: String? = null,
) {
    var bitmap by remember(path) { mutableStateOf<Bitmap?>(null) }
    var finished by remember(path) { mutableStateOf(path == null) }

    LaunchedEffect(path) {
        bitmap = path?.let { model.loadImage(it) }
        finished = true
    }

    Box(
        modifier = modifier
            .background(SentinelBackground)
            .semantics { contentDescription = description ?: "Security camera image" },
        contentAlignment = Alignment.Center,
    ) {
        when {
            bitmap != null -> Image(
                bitmap = bitmap!!.asImageBitmap(),
                contentDescription = description,
                modifier = Modifier.fillMaxSize(),
                contentScale = contentScale,
            )
            !finished -> CircularProgressIndicator(
                modifier = Modifier.size(28.dp),
                color = SentinelGreen,
                strokeWidth = 2.dp,
            )
            else -> Icon(
                imageVector = Icons.Default.BrokenImage,
                contentDescription = "Image unavailable",
                tint = SentinelTextDim,
            )
        }
        // Every camera still on any screen in this app goes through here - the alert feed, alert
        // detail, the trigger feed, the recording timeline - so this is the one place the mark
        // needs applying to cover all of them.
        UnlicensedWatermark()
    }
}

@Composable
fun EmptyState(
    title: String,
    message: String,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier.padding(32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Icon(
            imageVector = Icons.Default.Shield,
            contentDescription = null,
            tint = SentinelTextDim,
            modifier = Modifier.size(42.dp),
        )
        Text(title, color = SentinelTextDim, style = MaterialTheme.typography.titleMedium)
        Text(
            message,
            color = SentinelTextDim,
            style = MaterialTheme.typography.bodySmall,
        )
    }
}

fun Instant.relativeTime(now: Instant = Instant.now()): String {
    val duration = Duration.between(this, now)
    val seconds = duration.seconds.coerceAtLeast(0)
    return when {
        seconds < 60 -> "now"
        seconds < 3_600 -> "${seconds / 60}m ago"
        seconds < 86_400 -> "${seconds / 3_600}h ago"
        seconds < 604_800 -> "${seconds / 86_400}d ago"
        else -> DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM)
            .withLocale(Locale.getDefault())
            .withZone(ZoneId.systemDefault())
            .format(this)
    }
}

fun Instant.fullDateTime(): String =
    DateTimeFormatter.ofLocalizedDateTime(FormatStyle.FULL, FormatStyle.MEDIUM)
        .withLocale(Locale.getDefault())
        .withZone(ZoneId.systemDefault())
        .format(this)

fun Instant.shortTime(): String =
    DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT)
        .withLocale(Locale.getDefault())
        .withZone(ZoneId.systemDefault())
        .format(this)

