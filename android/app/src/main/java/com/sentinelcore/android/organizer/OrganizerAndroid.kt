package com.sentinelcore.android.organizer

import android.Manifest
import android.app.AlarmManager
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import com.sentinelcore.android.MainActivity
import com.sentinelcore.android.R
import com.sentinelcore.android.data.SentinelRepository
import java.io.File
import java.io.IOException
import java.util.UUID
import kotlinx.coroutines.CancellationException
import org.json.JSONArray
import org.json.JSONObject

// The app's side of the Organizer: how the engine reaches the server, where it keeps its copy,
// which offline policy this device uses, and turning planned alerts into notifications.

class AppOrganizerTransport(private val repository: SentinelRepository) : OrganizerTransport {
    override suspend fun send(method: String, path: String, query: Map<String, String>, body: ByteArray?, contentType: String?, timeoutMillis: Int?): OrganizerResponse =
        try {
            val (status, bytes) = repository.organizerRequest(method, path, query, body, contentType, timeoutMillis)
            OrganizerResponse(status, bytes)
        } catch (error: CancellationException) {
            throw error
        } catch (_: IOException) {
            throw OrganizerException.Offline()
        }
}

object OrganizerDevice {
    private const val PREFS = "sentinel_core"

    // Stable for the life of this install; the tiebreak inside every clock this device writes.
    fun id(context: Context): String {
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        prefs.getString("organizer_device_id", null)?.let { return it }
        val id = "android-" + UUID.randomUUID().toString().take(8)
        prefs.edit().putString("organizer_device_id", id).apply()
        return id
    }

    // Not backed up: the copy is rebuilt from the server whenever it's missing.
    fun directory(context: Context) = File(context.noBackupFilesDir, "organizer").apply { mkdirs() }

    // Phones and tablets keep a light copy by default; see OfflinePolicy.
    fun policy(context: Context): OfflinePolicy =
        OfflinePolicy.fromJson(context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString("organizer_policy", null))
            ?: OfflinePolicy.PHONE_DEFAULT

    fun savePolicy(context: Context, policy: OfflinePolicy) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putString("organizer_policy", policy.toJson()).apply()
    }

    fun lastKnownEnabled(context: Context) =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean("organizer_enabled", false)

    fun saveLastKnownEnabled(context: Context, enabled: Boolean) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean("organizer_enabled", enabled).apply()
    }
}

// Reminder and event alerts as local alarms: they fire with no signal and with the server down,
// which is why they aren't pushed. The planned list is kept in preferences too, so a reboot -
// which clears every alarm - can put them back without starting the whole app.
object OrganizerAlarms {
    const val CHANNEL_ID = "organizer_alerts"
    const val EXTRA_ITEM_ID = "organizerItemId"
    private const val PREFS = "organizer_alarms"
    private const val EXTRA_TITLE = "title"
    private const val EXTRA_BODY = "body"
    private const val EXTRA_ALERT_ID = "alertId"

    fun ensureChannel(context: Context) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val manager = context.getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "Reminders and events", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "Alerts for Organizer reminders and calendar events, raised by this phone so they work offline."
            },
        )
    }

    fun schedule(context: Context, alerts: List<PlannedAlert>) {
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val alarmManager = context.getSystemService(AlarmManager::class.java)
        val previous = prefs.getStringSet("ids", emptySet()).orEmpty()
        val wanted = alerts.map { it.id }.toSet()
        for (id in previous - wanted) alarmManager.cancel(pendingIntent(context, id, null))
        for (alert in alerts) {
            val intent = pendingIntent(context, alert.id, alert)
            // Exact when the person has allowed it (Settings → Alarms & reminders); otherwise
            // within a few minutes, which Doze permits without any special permission.
            if (Build.VERSION.SDK_INT < Build.VERSION_CODES.S || alarmManager.canScheduleExactAlarms()) {
                alarmManager.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, alert.fireAtMillis, intent)
            } else {
                alarmManager.setWindow(AlarmManager.RTC_WAKEUP, alert.fireAtMillis, 5 * 60_000L, intent)
            }
        }
        prefs.edit()
            .putStringSet("ids", wanted)
            .putString("alerts", JSONArray(alerts.map { alert ->
                JSONObject().put("id", alert.id).put("itemId", alert.itemId).put("at", alert.fireAtMillis)
                    .put("title", alert.title).put("body", alert.body)
            }).toString())
            .apply()
    }

    fun rescheduleSaved(context: Context) {
        val text = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString("alerts", null) ?: return
        val now = System.currentTimeMillis()
        val alerts = runCatching {
            val array = JSONArray(text)
            (0 until array.length()).map { i ->
                val json = array.getJSONObject(i)
                PlannedAlert(json.getString("id"), json.getString("itemId"), json.getLong("at"), json.getString("title"), json.getString("body"))
            }
        }.getOrDefault(emptyList()).filter { it.fireAtMillis > now }
        schedule(context, alerts)
    }

    private fun pendingIntent(context: Context, alertId: String, alert: PlannedAlert?): PendingIntent {
        val intent = Intent(context, OrganizerAlarmReceiver::class.java).apply {
            action = "com.sentinelcore.android.ORGANIZER_ALERT.$alertId"
            putExtra(EXTRA_ALERT_ID, alertId)
            alert?.let {
                putExtra(EXTRA_ITEM_ID, it.itemId)
                putExtra(EXTRA_TITLE, it.title)
                putExtra(EXTRA_BODY, it.body)
            }
        }
        return PendingIntent.getBroadcast(context, alertId.hashCode(), intent, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
    }

    internal fun show(context: Context, intent: Intent) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) return
        ensureChannel(context)
        val itemId = intent.getStringExtra(EXTRA_ITEM_ID)
        val open = PendingIntent.getActivity(
            context, (itemId ?: "").hashCode(),
            Intent(context, MainActivity::class.java).apply {
                flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
                putExtra(EXTRA_ITEM_ID, itemId)
            },
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val notification = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(intent.getStringExtra(EXTRA_TITLE) ?: "Reminder")
            .setContentText(intent.getStringExtra(EXTRA_BODY))
            .setAutoCancel(true)
            .setContentIntent(open)
            .setCategory(NotificationCompat.CATEGORY_REMINDER)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .build()
        NotificationManagerCompat.from(context).notify((intent.getStringExtra(EXTRA_ALERT_ID) ?: "").hashCode(), notification)
    }
}

class OrganizerAlarmReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) = OrganizerAlarms.show(context, intent)
}

// Set when an alert notification is tapped; the app shell navigates to the Organizer.
object OrganizerNavigation {
    val open = kotlinx.coroutines.flow.MutableStateFlow(false)
}

// Alarms don't survive a reboot; this puts the planned ones back.
class OrganizerBootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action == Intent.ACTION_BOOT_COMPLETED || intent.action == Intent.ACTION_MY_PACKAGE_REPLACED) {
            OrganizerAlarms.rescheduleSaved(context)
        }
    }
}
