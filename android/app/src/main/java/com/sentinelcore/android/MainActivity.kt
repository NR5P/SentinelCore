package com.sentinelcore.android

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.core.splashscreen.SplashScreen.Companion.installSplashScreen
import androidx.lifecycle.viewmodel.compose.viewModel
import com.sentinelcore.android.ui.SentinelApp
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.theme.SentinelTheme
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
    }

    private fun handlePushIntent(intent: Intent?) {
        PushNavigation.open(intent?.getStringExtra(EventIdExtra))
    }

    companion object {
        const val EventIdExtra = "eventId"
    }
}
