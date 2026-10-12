package com.sentinelcore.android.ui.weather

import android.content.Context
import android.provider.Settings
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.lerp
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.ViewModel
import com.sentinelcore.android.data.WeatherWarning
import com.sentinelcore.android.data.WeatherWarningLogic
import com.sentinelcore.android.data.WeatherWarningSeen
import com.sentinelcore.android.ui.SentinelViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import java.time.Instant
import java.time.ZoneId

// The app-wide NWS warning banner - the phone/tablet counterpart of the wall kiosk's
// core/SentinelCore.Web/wwwroot/kiosk-weather-alert.js. A tornado warning, severe thunderstorm
// watch or any other alert the server's weather-warning settings ask for gets a flashing banner
// across the top of every tab. It floats over the app without pushing anything down; tapping it
// opens the Weather tab's radar full screen, × just dismisses it. Either way it counts as seen and
// goes away, and the Weather tab keeps a coloured dot until the alert ends.

object WeatherNavigation {
    // Set by the banner; the Weather screen opens its radar full screen and clears it.
    val openRadar = MutableStateFlow(false)

    // Whether the Weather screen's radar is full screen right now: then the banner's tap just says
    // "got it", there being nothing more to open.
    val radarShowing = MutableStateFlow(false)

    // DEBUG builds only (see MainActivity): fake alerts merged into the server's, so the banner
    // can be looked at without a real storm.
    val debugAlerts = MutableStateFlow<List<WeatherWarning>>(emptyList())
}

class WeatherWarningsModel(
    private val app: SentinelViewModel,
    context: Context,
) : ViewModel() {
    private val preferences = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
    private val seen = WeatherWarningSeen.decode(preferences.getString(SEEN_KEY, null)).also {
        it.prune(Instant.now())
    }
    private var fromServer: List<WeatherWarning> = emptyList()

    // Every alert in force, seen or not - the Weather tab's dot. Worst first, as the server sorts.
    var active by mutableStateOf<List<WeatherWarning>>(emptyList())
        private set

    // What the banner shows.
    var unseen by mutableStateOf<List<WeatherWarning>>(emptyList())
        private set

    suspend fun poll() {
        // Null is any failure, a 404 from an older server included: keep what's showing.
        app.weatherWarnings()?.let { fromServer = it }
        update()
    }

    fun update() {
        val now = Instant.now()
        val before = seen.entries
        seen.prune(now)
        active = WeatherWarningLogic.active(WeatherNavigation.debugAlerts.value + fromServer, now)
        unseen = active.filter { !seen.isSeen(it, now) }
        if (seen.entries != before) save()
    }

    fun markSeen(alerts: List<WeatherWarning>) {
        seen.markSeen(alerts, Instant.now())
        save()
        update()
    }

    private fun save() {
        preferences.edit().putString(SEEN_KEY, seen.encode()).apply()
    }

    companion object {
        const val POLL_MS = 60_000L
        private const val PREFS = "sentinel_weather_warnings"
        private const val SEEN_KEY = "seen"
    }
}

// Bright and dark of each level, between which the banner pulses - the kiosk's colours.
data class WarningColors(val bright: Color, val dark: Color)

fun warningColors(level: String?): WarningColors = when (level) {
    "watch" -> WarningColors(Color(0xFFFF8A2A), Color(0xFF6E3300))
    "advisory" -> WarningColors(Color(0xFFFFB545), Color(0xFF5C4100))
    else -> WarningColors(Color(0xFFFF4D5E), Color(0xFF7A0F1A))
}

@Composable
fun WeatherWarningBanner(
    alerts: List<WeatherWarning>,
    onOpen: () -> Unit,
    onDismiss: () -> Unit,
    onRadar: Boolean,
    modifier: Modifier = Modifier,
) {
    val top = alerts.firstOrNull() ?: return
    val context = LocalContext.current
    val colors = warningColors(top.level)
    // "Remove animations" in Accessibility sets the animator scale to 0: hold the dark colour.
    val animate = remember {
        Settings.Global.getFloat(context.contentResolver, Settings.Global.ANIMATOR_DURATION_SCALE, 1f) > 0f
    }
    val pulse = if (animate) {
        rememberInfiniteTransition(label = "weather-warning").animateFloat(
            initialValue = 0f,
            targetValue = 1f,
            animationSpec = infiniteRepeatable(tween(1100, easing = FastOutSlowInEasing), RepeatMode.Reverse),
            label = "pulse",
        ).value
    } else {
        0f
    }
    val is24Hour = android.text.format.DateFormat.is24HourFormat(context)
    val until = WeatherWarningLogic.untilText(top.ends, Instant.now(), ZoneId.systemDefault(), is24Hour)
    val more = if (alerts.size > 1) "+${alerts.size - 1} more" else ""
    val shape = RoundedCornerShape(12.dp)

    BoxWithConstraints(modifier.fillMaxWidth().statusBarsPadding().padding(top = 10.dp, start = 16.dp, end = 16.dp), contentAlignment = Alignment.TopCenter) {
        // The kiosk's "tap for radar" chip, where there's room for it.
        val showHint = maxWidth >= 520.dp
        Row(
            Modifier
                .widthIn(max = 760.dp)
                .fillMaxWidth()
                .height(IntrinsicSize.Min)
                .shadow(12.dp + 10.dp * pulse, shape, ambientColor = colors.bright, spotColor = colors.bright)
                .clip(shape)
                .background(lerp(colors.dark, colors.bright, pulse))
                .border(2.dp, colors.bright, shape)
                .semantics { liveRegion = LiveRegionMode.Assertive },
        ) {
            Row(
                Modifier
                    .weight(1f)
                    .heightIn(min = 56.dp)
                    .clickable(onClickLabel = if (onRadar) "mark as seen" else "open the radar", onClick = onOpen)
                    .padding(horizontal = 14.dp, vertical = 8.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                Icon(Icons.Filled.Warning, contentDescription = null, tint = Color.White, modifier = Modifier.size(30.dp))
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Row(verticalAlignment = Alignment.Bottom, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        Text(
                            top.event.uppercase(),
                            color = Color.White,
                            fontSize = 16.sp,
                            fontWeight = FontWeight.Bold,
                            letterSpacing = 0.5.sp,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            modifier = Modifier.weight(1f, fill = false),
                        )
                        // On a phone the event name comes first: "until" moves down a line.
                        if (showHint && until.isNotEmpty()) Text(until, color = Color.White, fontSize = 13.sp, maxLines = 1)
                        if (more.isNotEmpty()) Text(more, color = Color.White.copy(alpha = 0.85f), fontSize = 13.sp, maxLines = 1)
                    }
                    val second = if (showHint) top.headline else listOfNotNull(until.ifEmpty { null }, top.headline).joinToString(" · ")
                    second?.takeIf { it.isNotEmpty() }?.let {
                        Text(it, color = Color.White.copy(alpha = 0.92f), fontSize = 13.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                }
                if (showHint) {
                    Text(
                        if (onRadar) "GOT IT" else "TAP FOR RADAR",
                        color = Color.White,
                        fontSize = 12.sp,
                        letterSpacing = 1.sp,
                        maxLines = 1,
                        modifier = Modifier
                            .border(1.dp, Color.White.copy(alpha = 0.6f), RoundedCornerShape(8.dp))
                            .padding(horizontal = 10.dp, vertical = 6.dp),
                    )
                }
            }
            Box(
                Modifier
                    .width(1.dp)
                    .fillMaxHeight()
                    .background(Color.White.copy(alpha = 0.35f)),
            )
            Box(
                Modifier
                    .width(52.dp)
                    .fillMaxHeight()
                    .background(Color.Black.copy(alpha = 0.25f))
                    .clickable(onClickLabel = "dismiss", onClick = onDismiss),
                contentAlignment = Alignment.Center,
            ) {
                Icon(Icons.Filled.Close, contentDescription = "Dismiss weather warning", tint = Color.White, modifier = Modifier.size(26.dp))
            }
        }
    }
}

// The Weather tab's dot while an alert is in force, coloured by the worst one.
@Composable
fun WeatherAlertDot(level: String, modifier: Modifier = Modifier) {
    val color = warningColors(level).bright
    Box(
        modifier
            .size(10.dp)
            .shadow(4.dp, CircleShape, ambientColor = color, spotColor = color)
            .clip(CircleShape)
            .background(color),
    )
}
