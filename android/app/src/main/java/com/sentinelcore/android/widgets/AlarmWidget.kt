package com.sentinelcore.android.widgets

import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.GlanceTheme
import androidx.glance.LocalContext
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.action.actionStartActivity
import androidx.glance.appwidget.cornerRadius
import androidx.glance.appwidget.provideContent
import androidx.glance.appwidget.updateAll
import androidx.glance.background
import androidx.glance.color.ColorProvider
import androidx.glance.layout.Column
import androidx.glance.layout.Spacer
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.height
import androidx.glance.layout.padding
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextStyle
import com.sentinelcore.android.MainActivity
import com.sentinelcore.android.data.AlarmModes
import com.sentinelcore.android.data.AlarmStatus
import java.io.File
import org.json.JSONObject

// The alarm at a glance: armed or not, and what's open. Draws the last status the app saw
// (written by AlarmWidgetSnapshot.write whenever the app reads /api/alarm/status); tapping it
// opens the Alarm screen, where arming and disarming live. Same as the iOS widget, and for the
// same reason: a widget has no login of its own to arm with.
class AlarmWidget : GlanceAppWidget() {
    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val snapshot = AlarmWidgetSnapshot.read(context)
        provideContent { AlarmWidgetContent(snapshot) }
    }
}

class AlarmWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget: GlanceAppWidget = AlarmWidget()
}

data class AlarmWidgetSnapshot(
    val enabled: Boolean,
    val mode: String = "disarmed",
    val status: String = "disarmed",
    val exitUntil: Long? = null,
    val pendingUntil: Long? = null,
    val triggeredBy: String? = null,
    val sensorCount: Int = 0,
    val openCount: Int = 0,
    val problemCount: Int = 0,
    val updatedAt: Long = 0,
) {
    // Past a deadline the widget assumes the server moved on the way it always does.
    fun statusAt(now: Long): String = when {
        status == "arming" && exitUntil != null && exitUntil <= now -> "armed"
        status == "pending" && pendingUntil != null && pendingUntil <= now -> "triggered"
        else -> status
    }

    fun toJson(): JSONObject = JSONObject().apply {
        put("enabled", enabled); put("mode", mode); put("status", status)
        put("exitUntil", exitUntil ?: JSONObject.NULL); put("pendingUntil", pendingUntil ?: JSONObject.NULL)
        put("triggeredBy", triggeredBy ?: JSONObject.NULL)
        put("sensorCount", sensorCount); put("openCount", openCount); put("problemCount", problemCount)
        put("updatedAt", updatedAt)
    }

    companion object {
        private fun file(context: Context) = File(File(context.filesDir, "widgets"), "alarm-widget.json")

        fun read(context: Context): AlarmWidgetSnapshot = runCatching {
            val json = JSONObject(file(context).readText())
            AlarmWidgetSnapshot(
                enabled = json.optBoolean("enabled"),
                mode = json.optString("mode", "disarmed"),
                status = json.optString("status", "disarmed"),
                exitUntil = if (json.isNull("exitUntil")) null else json.optLong("exitUntil"),
                pendingUntil = if (json.isNull("pendingUntil")) null else json.optLong("pendingUntil"),
                triggeredBy = if (json.isNull("triggeredBy")) null else json.optString("triggeredBy"),
                sensorCount = json.optInt("sensorCount"),
                openCount = json.optInt("openCount"),
                problemCount = json.optInt("problemCount"),
                updatedAt = json.optLong("updatedAt"),
            )
        }.getOrDefault(AlarmWidgetSnapshot(enabled = false))

        private var last: AlarmWidgetSnapshot? = null

        // Called by the app whenever it reads the alarm's status. Only a change redraws the
        // widget; this runs on every poll while the Alarm screen is open.
        suspend fun write(context: Context, status: AlarmStatus) {
            val sensors = status.sensors.filter { it.enabled }
            val snapshot = AlarmWidgetSnapshot(
                enabled = status.enabled,
                mode = status.mode,
                status = status.status,
                exitUntil = status.exitUntil?.toEpochMilli(),
                pendingUntil = status.pendingUntil?.toEpochMilli(),
                triggeredBy = status.triggeredBy,
                sensorCount = sensors.size,
                openCount = sensors.count { it.isOpen },
                problemCount = sensors.count { it.offline || it.deviceMissing || it.lowBattery },
                updatedAt = System.currentTimeMillis(),
            )
            if (last?.copy(updatedAt = 0) == snapshot.copy(updatedAt = 0)) return
            last = snapshot
            runCatching {
                val target = file(context)
                target.parentFile?.mkdirs()
                val temporary = File(target.parentFile, "${target.name}.tmp")
                temporary.writeText(snapshot.toJson().toString())
                temporary.renameTo(target)
            }
            runCatching { AlarmWidget().updateAll(context) }
        }
    }
}

private object AlarmWidgetColors {
    val green = ColorProvider(day = Color(0xFF0E8C4E), night = Color(0xFF38FF9C))
    val amber = ColorProvider(day = Color(0xFF7A5200), night = Color(0xFFFFB545))
    val red = ColorProvider(day = Color(0xFFA3111F), night = Color(0xFFFF4D5E))
}

@Composable
private fun AlarmWidgetContent(snapshot: AlarmWidgetSnapshot) {
    val context = LocalContext.current
    val now = System.currentTimeMillis()
    val status = snapshot.statusAt(now)
    val headline = when {
        !snapshot.enabled -> "Alarm"
        status == "arming" -> "Arming"
        status == "pending" -> "Entry delay"
        status == "triggered" -> "Alarm!"
        status == "armed" -> "Armed ${AlarmModes.label(snapshot.mode).lowercase()}"
        else -> "Disarmed"
    }
    val color = when (status) {
        "armed" -> AlarmWidgetColors.green
        "arming", "pending" -> AlarmWidgetColors.amber
        "triggered" -> AlarmWidgetColors.red
        else -> GlanceTheme.colors.onSurfaceVariant
    }
    val detail = when {
        !snapshot.enabled -> "Open Sentinel Core once to show the alarm here."
        status == "triggered" && snapshot.triggeredBy != null -> snapshot.triggeredBy
        else -> buildList {
            if (snapshot.openCount > 0) add("${snapshot.openCount} open")
            if (snapshot.problemCount > 0) add("${snapshot.problemCount} need attention")
            if (isEmpty()) add(if (snapshot.sensorCount == 0) "No sensors yet" else "All ${snapshot.sensorCount} sensors closed")
        }.joinToString(" · ")
    }
    val open = actionStartActivity(
        Intent(context, MainActivity::class.java).apply {
            data = Uri.parse("sentinelcore://alarm")
            flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
            putExtra(MainActivity.AlarmExtra, true)
        },
    )
    GlanceTheme {
        Column(
            GlanceModifier.fillMaxSize().cornerRadius(16.dp)
                .background(GlanceTheme.colors.widgetBackground).padding(12.dp)
                .clickable(open),
        ) {
            Text("ALARM", style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 11.sp, fontWeight = FontWeight.Bold))
            Spacer(GlanceModifier.height(6.dp))
            Text(headline, style = TextStyle(color = color, fontSize = 20.sp, fontWeight = FontWeight.Bold), maxLines = 2)
            Spacer(GlanceModifier.height(4.dp))
            Text(detail, style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 12.sp), maxLines = 2)
        }
    }
}
