package com.sentinelcore.android

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.core.splashscreen.SplashScreen.Companion.installSplashScreen
import androidx.lifecycle.viewmodel.compose.viewModel
import com.sentinelcore.android.ui.AlarmNavigation
import com.sentinelcore.android.ui.SentinelApp
import com.sentinelcore.android.ui.weather.WeatherNavigation
import com.sentinelcore.android.data.WeatherWarning
import java.time.Instant
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.theme.SentinelTheme
import com.sentinelcore.android.publish.DevicePublisher
import com.sentinelcore.android.organizer.OrganizerAlarms
import com.sentinelcore.android.organizer.OrganizerNavigation
import com.sentinelcore.android.widgets.OrganizerWidgets
import com.sentinelcore.android.organizer.VoiceCapture
import com.sentinelcore.android.push.PushNavigation
import com.sentinelcore.android.push.SentinelFirebaseMessagingService

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        installSplashScreen()
        super.onCreate(savedInstanceState)
        // .auto() picks light/dark icon contrast itself from the current configuration, so it
        // stays correct whichever theme SentinelTheme resolves to (system setting or dynamic
        // color) instead of hardcoding dark-style icons regardless of appearance.
        enableEdgeToEdge(
            statusBarStyle = SystemBarStyle.auto(android.graphics.Color.TRANSPARENT, android.graphics.Color.TRANSPARENT),
            navigationBarStyle = SystemBarStyle.auto(android.graphics.Color.TRANSPARENT, android.graphics.Color.TRANSPARENT),
        )
        SentinelFirebaseMessagingService.ensureChannel(this)
        handlePushIntent(intent)
        // Only on a fresh start: a rotation recreates the activity with the same intent, and a
        // voice reminder must not be added twice.
        if (savedInstanceState == null) {
            handleVoiceIntent(intent)
            handleMediaSearch(intent)
        }
        setContent {
            SentinelTheme {
                val model: SentinelViewModel = viewModel()
                SentinelApp(model)
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handlePushIntent(intent)
        handleVoiceIntent(intent)
        handleMediaSearch(intent)
    }

    // "Play Rumours on Sentinel Core" - the voice search every media app must accept.
    private fun handleMediaSearch(intent: Intent?) {
        if (intent?.action != android.provider.MediaStore.INTENT_ACTION_MEDIA_PLAY_FROM_SEARCH) return
        com.sentinelcore.android.audio.AudioRuntime.get(this)
            .playFromSearch(intent.getStringExtra(android.app.SearchManager.QUERY).orEmpty())
    }

    // "Voice reminder", "Voice note" or the Assistant - see organizer/VoiceCapture.
    private fun handleVoiceIntent(intent: Intent?) {
        VoiceCapture.fromIntent(intent)?.let { VoiceCapture.pending.value = it }
    }

    private fun handlePushIntent(intent: Intent?) {
        PushNavigation.open(intent?.getStringExtra(EventIdExtra))
        // An alarm push (see PushNotifications) opens the Alarm screen.
        if (intent?.getBooleanExtra(AlarmExtra, false) == true) AlarmNavigation.open.value = true
        // A reminder or event alert (see OrganizerAlarms) opens the Organizer.
        if (intent?.getStringExtra(OrganizerAlarms.EXTRA_ITEM_ID) != null) OrganizerNavigation.open.value = true
        // An Organizer widget on the home screen was tapped (see widgets/OrganizerWidgets).
        intent?.getStringExtra(OrganizerWidgets.EXTRA_PANE)?.let { pane ->
            OrganizerNavigation.pane.value = pane
            OrganizerNavigation.open.value = true
        }
        DevicePublisher.requestScreen(intent)
        if (intent?.getBooleanExtra(NowPlayingExtra, false) == true) com.sentinelcore.android.ui.audio.AudioNavigation.openNowPlaying.value = true
        if (BuildConfig.DEBUG) handleDebugWeatherWarning(intent)
    }

    // DEBUG builds only: `adb shell am start -n com.sentinelcore.android/.MainActivity
    // --es debugWeatherWarning warning|watch|advisory` adds a fake TEST alert to the weather
    // warning banner (with --ei debugWeatherCount N for several), so it can be looked at without a
    // real storm. "clear" removes them.
    private fun handleDebugWeatherWarning(intent: Intent?) {
        val level = intent?.getStringExtra(DebugWeatherWarningExtra) ?: return
        if (level == "clear") {
            WeatherNavigation.debugAlerts.value = emptyList()
            return
        }
        val count = intent.getIntExtra("debugWeatherCount", 1).coerceIn(1, 5)
        val event = mapOf("warning" to "Tornado Warning", "watch" to "Severe Thunderstorm Watch").getOrDefault(level, "Wind Advisory")
        val stamp = System.currentTimeMillis()
        WeatherNavigation.debugAlerts.value = (1..count).map { n ->
            WeatherWarning(
                id = "debug-$stamp-$n",
                event = if (n == 1) "TEST $event" else "TEST Flood Advisory",
                level = if (n == 1) level else "advisory",
                category = null,
                severity = "Extreme",
                headline = "TEST - $event issued for this area until 45 minutes from now. This is only a test.",
                instruction = null,
                ends = Instant.now().plusSeconds(45 * 60),
                references = emptyList(),
            )
        }
    }

    companion object {
        const val EventIdExtra = "eventId"
        const val AlarmExtra = "alarm"
        // The media notification: opens Now Playing (see audio/AudioPlaybackService).
        const val NowPlayingExtra = "nowPlaying"
        const val DebugWeatherWarningExtra = "debugWeatherWarning"
    }
}
