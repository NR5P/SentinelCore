package com.sentinelcore.android

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.lifecycle.viewmodel.compose.viewModel
import com.sentinelcore.android.ui.SentinelApp
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.theme.SentinelTheme
import com.sentinelcore.android.push.PushNavigation
import com.sentinelcore.android.push.SentinelFirebaseMessagingService

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge(
            statusBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT),
            navigationBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT),
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
