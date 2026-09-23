package com.sentinelcore.android.publish

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import com.sentinelcore.android.MainActivity
import com.sentinelcore.android.R
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch

// What lets a phone keep being a camera with its screen off. Android only allows camera use from
// an app that is either in front or holding a foreground service of the camera type - one that
// was started while the app was in front - and it only keeps the CPU up for a process that holds
// a wake lock. This service is both, and nothing else: DevicePublisher does the streaming.
//
// The notification it shows is required for a foreground service, and is put to use: it says
// which camera this phone is, whether the server is receiving, and has a Stop button, so the
// stream can be ended without finding the screen.
class DevicePublisherService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var wakeLock: PowerManager.WakeLock? = null
    private var watcher: Job? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            DevicePublisher.stop()
            stopSelf()
            return START_NOT_STICKY
        }

        ensureChannel(this)
        val state = DevicePublisher.state.value
        val micGranted = ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED
        var type = 0
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            type = ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA
            // Only claimed when it can be honoured: on Android 14 a microphone-type service
            // without the permission is refused outright rather than started without audio.
            if (state.includeMic && micGranted) type = type or ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE
        }
        ServiceCompat.startForeground(this, NOTIFICATION_ID, buildNotification(state), type)

        if (wakeLock == null) {
            val power = getSystemService(Context.POWER_SERVICE) as PowerManager
            wakeLock = power.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "SentinelCore:DeviceCamera").apply {
                setReferenceCounted(false)
                acquire()
            }
        }

        watcher?.cancel()
        watcher = scope.launch {
            DevicePublisher.state
                .map { Triple(it.target, it.phase, it.status) }
                .distinctUntilChanged()
                .collect {
                    val current = DevicePublisher.state.value
                    if (!current.isPublishing) {
                        stopSelf()
                    } else {
                        getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, buildNotification(current))
                    }
                }
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        watcher?.cancel()
        scope.cancel()
        wakeLock?.let { if (it.isHeld) it.release() }
        wakeLock = null
        super.onDestroy()
    }

    private fun buildNotification(state: DevicePublisher.State): Notification {
        val open = PendingIntent.getActivity(
            this,
            1,
            Intent(this, MainActivity::class.java).putExtra(DevicePublisher.OPEN_SCREEN_EXTRA, true),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val stop = PendingIntent.getService(
            this,
            2,
            Intent(this, DevicePublisherService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val title = "Streaming as \"${state.target?.name ?: "camera"}\""
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(title)
            .setContentText(state.status.ifBlank { "this phone is one of your cameras" })
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setSilent(true)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .setContentIntent(open)
            .addAction(0, "Stop streaming", stop)
            .build()
    }

    companion object {
        private const val CHANNEL_ID = "device_camera"
        private const val NOTIFICATION_ID = 4101
        private const val ACTION_STOP = "com.sentinelcore.android.publish.STOP"

        fun start(context: Context) {
            ContextCompat.startForegroundService(context, Intent(context, DevicePublisherService::class.java))
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, DevicePublisherService::class.java))
        }

        private fun ensureChannel(context: Context) {
            val manager = context.getSystemService(NotificationManager::class.java)
            if (manager.getNotificationChannel(CHANNEL_ID) != null) return
            manager.createNotificationChannel(
                NotificationChannel(CHANNEL_ID, context.getString(R.string.device_camera_channel_name), NotificationManager.IMPORTANCE_LOW).apply {
                    description = context.getString(R.string.device_camera_channel_description)
                    setShowBadge(false)
                },
            )
        }
    }
}
