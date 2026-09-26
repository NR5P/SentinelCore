package com.sentinelcore.android.push

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage
import com.sentinelcore.android.MainActivity
import com.sentinelcore.android.R
import com.sentinelcore.android.widgets.OrganizerBackgroundSync
import kotlinx.coroutines.flow.MutableStateFlow

object PushNavigation {
    val eventId = MutableStateFlow<String?>(null)

    fun open(eventId: String?) {
        if (!eventId.isNullOrBlank()) this.eventId.value = eventId
    }

    fun consumed() {
        eventId.value = null
    }
}

object PushTokenUpdates {
    val token = MutableStateFlow<String?>(null)
}

object PushTokenStore {
    private const val PreferencesName = "sentinel_core"
    private const val TokenKey = "firebase_installation_id"

    fun save(context: Context, token: String) {
        context.getSharedPreferences(PreferencesName, Context.MODE_PRIVATE)
            .edit()
            .putString(TokenKey, token)
            .apply()
        PushTokenUpdates.token.value = token
    }

    fun get(context: Context): String? =
        context.getSharedPreferences(PreferencesName, Context.MODE_PRIVATE)
            .getString(TokenKey, null)

    fun clear(context: Context) {
        context.getSharedPreferences(PreferencesName, Context.MODE_PRIVATE)
            .edit()
            .remove(TokenKey)
            .apply()
        PushTokenUpdates.token.value = null
    }
}

class SentinelFirebaseMessagingService : FirebaseMessagingService() {
    // Firebase can rotate the FCM registration token independently of an explicit register()
    // call (restore, app-data reset, security rotation). Without this callback Sentinel kept
    // sending to the obsolete token until the person manually disabled and re-enabled push.
    @Suppress("OVERRIDE_DEPRECATION")
    override fun onNewToken(token: String) {
        PushTokenStore.save(this, token)
    }

    // Firebase Messaging 25's installation-id API uses these callbacks; keep onNewToken above
    // as the compatibility path for token rotation reported by Android lint/older providers.
    override fun onRegistered(installationId: String) {
        PushTokenStore.save(this, installationId)
    }

    override fun onUnregistered(installationId: String) {
        PushTokenStore.clear(this)
    }

    override fun onMessageReceived(message: RemoteMessage) {
        // The server's silent "the Organizer changed" - sync in the background, show nothing.
        if (message.data["eventType"] == "organizer-changed") {
            OrganizerBackgroundSync.syncSoon(this)
            return
        }
        val title = message.notification?.title
            ?: message.data["title"]
            ?: "Sentinel Core alert"
        val body = message.notification?.body
            ?: message.data["body"]
            ?: "New security camera activity"
        showNotification(
            context = this,
            title = title,
            body = body,
            eventId = message.data["eventId"],
            alarm = message.data["eventType"] == "alarm",
        )
    }

    companion object {
        fun ensureChannel(context: Context) {
            if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
            val channel = NotificationChannel(
                context.getString(R.string.security_alert_channel_id),
                context.getString(R.string.security_alert_channel_name),
                NotificationManager.IMPORTANCE_HIGH,
            ).apply {
                description = context.getString(R.string.security_alert_channel_description)
                enableVibration(true)
            }
            context.getSystemService(NotificationManager::class.java)
                .createNotificationChannel(channel)
        }

        fun showNotification(
            context: Context,
            title: String,
            body: String,
            eventId: String?,
            // The home alarm (triggered, armed, a sensor's battery): tapping opens the Alarm
            // screen rather than the feed.
            alarm: Boolean = false,
        ) {
            ensureChannel(context)
            val intent = Intent(context, MainActivity::class.java).apply {
                flags = Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP
                putExtra(MainActivity.EventIdExtra, eventId)
                if (alarm) putExtra(MainActivity.AlarmExtra, true)
            }
            val pendingIntent = PendingIntent.getActivity(
                context,
                eventId?.hashCode() ?: 0,
                intent,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
            )
            val notification = NotificationCompat.Builder(
                context,
                context.getString(R.string.security_alert_channel_id),
            )
                .setSmallIcon(R.drawable.ic_notification)
                .setContentTitle(title)
                .setContentText(body)
                .setStyle(NotificationCompat.BigTextStyle().bigText(body))
                .setPriority(NotificationCompat.PRIORITY_HIGH)
                .setCategory(NotificationCompat.CATEGORY_ALARM)
                .setAutoCancel(true)
                .setContentIntent(pendingIntent)
                .build()

            try {
                NotificationManagerCompat.from(context)
                    .notify(eventId?.hashCode() ?: body.hashCode(), notification)
            } catch (_: SecurityException) {
                // Android 13+ blocks this when the person has not granted notification access.
            }
        }
    }
}
