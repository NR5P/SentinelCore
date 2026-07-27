package com.sentinelcore.android.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.LiveTv
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.Radar
import androidx.compose.material.icons.filled.Security
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.Icon
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationRail
import androidx.compose.material3.NavigationRailItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.sentinelcore.android.data.Camera
import com.sentinelcore.android.data.ConnectionState
import com.sentinelcore.android.data.SecurityEvent
import com.sentinelcore.android.data.TriggerEvent
import com.sentinelcore.android.ui.components.ConnectionBadge
import com.sentinelcore.android.ui.screens.EventDetailScreen
import com.sentinelcore.android.ui.screens.EventFeedScreen
import com.sentinelcore.android.ui.screens.LiveCamerasScreen
import com.sentinelcore.android.ui.screens.LivePlayerScreen
import com.sentinelcore.android.ui.screens.RecordingTimelineScreen
import com.sentinelcore.android.ui.screens.LoginScreen
import com.sentinelcore.android.ui.screens.ServerSetupScreen
import com.sentinelcore.android.ui.screens.SettingsScreen
import com.sentinelcore.android.ui.screens.TriggerDetailScreen
import com.sentinelcore.android.ui.screens.TriggerFeedScreen
import com.sentinelcore.android.ui.theme.SentinelBackground
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelPanel
import kotlinx.coroutines.delay

@Composable
fun SentinelApp(model: SentinelViewModel) {
    val state by model.state.collectAsStateWithLifecycle()

    LaunchedEffect(state.serverUrl) {
        if (state.isConfigured) model.connect(resetNetworkSession = true)
    }
    LaunchedEffect(state.serverUrl, state.requiresLogin) {
        while (state.isConfigured && !state.requiresLogin) {
            delay(30_000)
            model.connect()
        }
    }

    Box(
        modifier = Modifier.fillMaxSize().background(SentinelBackground),
    ) {
        when {
            !state.isConfigured -> ServerSetupScreen(
                initialAddress = state.serverUrl,
                onConnect = model::saveServer,
            )
            state.requiresLogin -> LoginScreen(
                state = state,
                onLogin = model::signIn,
            )
            else -> AppShell(state = state, model = model)
        }
    }
}

@Composable
private fun AppShell(
    state: SentinelUiState,
    model: SentinelViewModel,
) {
    var section by rememberSaveable { mutableStateOf(AppSection.FEED) }
    var detail by remember { mutableStateOf<DetailScreen?>(null) }
    BackHandler(detail != null) { detail = null }
    LaunchedEffect(state.pendingPushEventId, state.events) {
        val eventId = state.pendingPushEventId ?: return@LaunchedEffect
        val event = state.events.firstOrNull { it.id == eventId } ?: return@LaunchedEffect
        section = AppSection.FEED
        detail = DetailScreen.Event(event)
        model.consumePushEvent()
    }

    BoxWithConstraints(Modifier.fillMaxSize()) {
        val useRail = maxWidth >= 840.dp
        if (useRail) {
            Row(Modifier.fillMaxSize()) {
                SentinelNavigationRail(
                    selected = section,
                    state = state,
                    onSelected = {
                        section = it
                        detail = null
                    },
                )
                ScreenContent(
                    modifier = Modifier.weight(1f),
                    section = section,
                    detail = detail,
                    state = state,
                    model = model,
                    onDetail = { detail = it },
                    onBack = { detail = null },
                )
            }
        } else {
            Scaffold(
                containerColor = SentinelBackground,
                bottomBar = {
                    NavigationBar(containerColor = SentinelPanel) {
                        AppSection.entries.forEach { item ->
                            NavigationBarItem(
                                selected = section == item,
                                onClick = {
                                    section = item
                                    detail = null
                                },
                                icon = { Icon(item.icon, contentDescription = item.title) },
                                label = { Text(item.title) },
                            )
                        }
                    }
                },
            ) { padding ->
                ScreenContent(
                    modifier = Modifier.padding(padding),
                    section = section,
                    detail = detail,
                    state = state,
                    model = model,
                    onDetail = { detail = it },
                    onBack = { detail = null },
                )
            }
        }
    }
}

@Composable
private fun SentinelNavigationRail(
    selected: AppSection,
    state: SentinelUiState,
    onSelected: (AppSection) -> Unit,
) {
    NavigationRail(
        containerColor = SentinelPanel,
        header = {
            Column(
                modifier = Modifier.padding(horizontal = 12.dp, vertical = 18.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Text(">_", color = SentinelGreen)
                Text("SENTINEL", color = SentinelGreen)
            }
        },
    ) {
        AppSection.entries.forEach { item ->
            NavigationRailItem(
                selected = selected == item,
                onClick = { onSelected(item) },
                icon = { Icon(item.icon, contentDescription = item.title) },
                label = { Text(item.title) },
            )
        }
        Box(Modifier.weight(1f))
        ConnectionBadge(
            state.connectionState,
            Modifier.padding(horizontal = 10.dp, vertical = 18.dp),
        )
    }
}

@Composable
private fun ScreenContent(
    modifier: Modifier,
    section: AppSection,
    detail: DetailScreen?,
    state: SentinelUiState,
    model: SentinelViewModel,
    onDetail: (DetailScreen) -> Unit,
    onBack: () -> Unit,
) {
    Box(modifier.fillMaxSize()) {
        when (val selectedDetail = detail) {
            is DetailScreen.Event -> EventDetailScreen(
                event = selectedDetail.event,
                model = model,
                onBack = onBack,
            )
            is DetailScreen.Trigger -> TriggerDetailScreen(
                trigger = selectedDetail.trigger,
                model = model,
                onBack = onBack,
            )
            is DetailScreen.Camera -> LivePlayerScreen(
                camera = selectedDetail.value,
                model = model,
                onBack = onBack,
            )
            null -> when (section) {
                AppSection.FEED -> EventFeedScreen(
                    state = state,
                    model = model,
                    onEvent = { onDetail(DetailScreen.Event(it)) },
                    onRefresh = { model.connect(resetNetworkSession = true) },
                )
                AppSection.LIVE -> LiveCamerasScreen(
                    state = state,
                    model = model,
                    onCamera = { onDetail(DetailScreen.Camera(it)) },
                    onRefresh = model::connect,
                )
                AppSection.TIMELINE -> RecordingTimelineScreen(state = state, model = model)
                AppSection.TRIGGERS -> TriggerFeedScreen(
                    state = state,
                    model = model,
                    onTrigger = { onDetail(DetailScreen.Trigger(it)) },
                    onRefresh = model::connect,
                )
                AppSection.SETTINGS -> SettingsScreen(state = state, model = model)
            }
        }
    }
}

private enum class AppSection(
    val title: String,
    val icon: ImageVector,
) {
    FEED("FEED", Icons.Default.Security),
    TIMELINE("TIMELINE", Icons.Default.History),
    LIVE("LIVE", Icons.Default.LiveTv),
    TRIGGERS("TRIGGERS", Icons.Default.Radar),
    SETTINGS("SETTINGS", Icons.Default.Settings),
}

private sealed interface DetailScreen {
    data class Event(val event: SecurityEvent) : DetailScreen
    data class Trigger(val trigger: TriggerEvent) : DetailScreen
    data class Camera(val value: com.sentinelcore.android.data.Camera) : DetailScreen
}
