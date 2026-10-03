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
        if (savedInstanceState == null) handleVoiceIntent(intent)
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
    }

    companion object {
        const val EventIdExtra = "eventId"
        const val AlarmExtra = "alarm"
        // The media notification: opens Now Playing (see audio/AudioPlaybackService).
        const val NowPlayingExtra = "nowPlaying"
    }
}
