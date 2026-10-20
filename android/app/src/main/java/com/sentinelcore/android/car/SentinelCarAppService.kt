package com.sentinelcore.android.car

import android.app.PendingIntent
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.ApplicationInfo
import androidx.car.app.CarAppService
import androidx.car.app.CarContext
import androidx.car.app.Screen
import androidx.car.app.ScreenManager
import androidx.car.app.Session
import androidx.car.app.SessionInfo
import androidx.car.app.notification.CarAppExtender
import androidx.car.app.notification.CarPendingIntent
import androidx.car.app.validation.HostValidator
import androidx.core.app.NotificationManagerCompat
import androidx.core.net.toUri
import com.sentinelcore.android.R
import com.sentinelcore.android.audio.AudioRuntime
import com.sentinelcore.android.data.SentinelRepository

// Android Auto's IoT category (Car App Quality IT-1): what may be done while driving is looking
// at device state, one-tap on/off/open/close and scenes, and being told about security events.
// So the car gets the alarm (status + arm, never disarm - that needs the PIN and the car has no
// safe way to type one), the Device Hub's switches, covers and buttons, the schedules that act
// on devices, and the recent alerts. Nothing here sets anything up; that stays on the phone.
//
// Music is not here: it already reaches the car through audio/AudioPlaybackService.
class SentinelCarAppService : CarAppService() {
    override fun createHostValidator(): HostValidator =
        if (applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE != 0) {
            // The Desktop Head Unit and sideloaded test hosts.
            HostValidator.ALLOW_ALL_HOSTS_VALIDATOR
        } else {
            HostValidator.Builder(applicationContext)
                .addAllowedHosts(R.array.car_hosts_allowlist)
                .build()
        }

    override fun onCreateSession(sessionInfo: SessionInfo): Session = SentinelCarSession()
}

class SentinelCarSession : Session() {
    override fun onCreateScreen(intent: Intent): Screen {
        val home = CarHomeScreen(carContext)
        val deep = screenFor(intent) ?: return home
        carContext.getCarService(ScreenManager::class.java).push(home)
        return deep
    }

    override fun onNewIntent(intent: Intent) {
        val screen = screenFor(intent) ?: return
        val manager = carContext.getCarService(ScreenManager::class.java)
        manager.popToRoot()
        manager.push(screen)
    }

    private fun screenFor(intent: Intent): Screen? = when (intent.data?.host) {
        ALERTS -> CarAlertsScreen(carContext)
        ALARM -> CarAlarmScreen(carContext)
        else -> null
    }

    companion object {
        private const val SCHEME = "sentinelcar"
        private const val ALERTS = "alerts"
        private const val ALARM = "alarm"

        // Added to every security notification. Shown on the car screen as a heads-up; tapping
        // it opens the Alerts list (or the Alarm, for alarm notifications) in the car app.
        fun carExtender(context: Context, alarm: Boolean): CarAppExtender {
            val builder = CarAppExtender.Builder()
                .setImportance(NotificationManagerCompat.IMPORTANCE_HIGH)
            runCatching {
                val intent = Intent(Intent.ACTION_VIEW)
                    .setComponent(ComponentName(context, SentinelCarAppService::class.java))
                    .setData("$SCHEME://${if (alarm) ALARM else ALERTS}".toUri())
                builder.setContentIntent(
                    CarPendingIntent.getCarApp(context, if (alarm) 2 else 1, intent, PendingIntent.FLAG_UPDATE_CURRENT),
                )
            }
            return builder.build()
        }
    }
}

// The same repository the phone UI and the music service use (session cookie, local/remote
// address choice). The car app can be the first thing to run after a reboot, so it may have to
// work out the address itself; AudioRuntime already knows how.
internal suspend fun CarContext.repository(): SentinelRepository {
    val repository = AudioRuntime.get(this).readyRepository()
    if (repository.baseUrl.isBlank()) throw IllegalStateException(NOT_SET_UP)
    return repository
}

// Every server call the car makes: capped at 20 seconds (a request's own read timeout is 60,
// and a car screen shouldn't spin that long), and re-checking home against remote if the address
// in use stopped answering - see AudioRuntime.withAddressRecovery.
internal suspend fun <T> CarContext.withServer(block: suspend (SentinelRepository) -> T): T {
    val runtime = AudioRuntime.get(this)
    return com.sentinelcore.android.audio.withHardTimeout(CAR_REQUEST_LIMIT_MS) {
        repository()
        runtime.withAddressRecovery { block(runtime.repository) }
    }
}

internal const val CAR_REQUEST_LIMIT_MS = 20_000L

// VI-1: anything that sends the driver to the phone says to look only when it's safe.
internal const val NOT_SET_UP = "Sentinel Core isn't set up yet. When it's safe, open it on your phone."
internal const val PHONE_ONLY = "When it's safe, use Sentinel Core on your phone."

internal fun Throwable.carMessage(): String = when (this) {
    is com.sentinelcore.android.data.SentinelApiException -> message.ifBlank { "The server said no ($statusCode)." }
    is IllegalStateException -> message ?: "Something went wrong."
    is java.io.IOException -> "Can't reach your Sentinel Core server."
    else -> message ?: "Something went wrong."
}
