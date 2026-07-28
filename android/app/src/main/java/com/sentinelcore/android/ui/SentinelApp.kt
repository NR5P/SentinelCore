package com.sentinelcore.android.ui

import android.net.Uri
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
import androidx.compose.material3.CircularProgressIndicator
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
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.navigation.NavHostController
import androidx.navigation.NavType
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import androidx.navigation.navArgument
import com.sentinelcore.android.ui.components.ConnectionBadge
import com.sentinelcore.android.ui.screens.CameraAdminScreen
import com.sentinelcore.android.ui.screens.CameraDiscoveryScreen
import com.sentinelcore.android.ui.screens.CameraEditScreen
import com.sentinelcore.android.ui.screens.CameraZoneEditorScreen
import com.sentinelcore.android.ui.screens.DetailHeader
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
    val navController = rememberNavController()
    val backStackEntry by navController.currentBackStackEntryAsState()
    val currentRoute = backStackEntry?.destination?.route
    // Detail routes are nested under their section ("feed/event/{eventId}"), so a route match
    // has to check for that prefix too - otherwise the tab bar would deselect itself the moment
    // a detail screen opens.
    val selectedSection = AppSection.entries.firstOrNull { section ->
        currentRoute == section.route || currentRoute?.startsWith("${section.route}/") == true
    } ?: AppSection.FEED

    // Navigates straight to the pushed event without waiting for state.events to already contain
    // it - the detail route itself shows a brief loading state if the list hasn't caught up yet,
    // which is simpler and more robust than the old approach of dropping the deep link entirely
    // when the event wasn't loaded yet.
    LaunchedEffect(state.pendingPushEventId) {
        val eventId = state.pendingPushEventId ?: return@LaunchedEffect
        navController.navigate("feed/event/$eventId") { launchSingleTop = true }
        model.consumePushEvent()
    }

    val onSelectSection: (AppSection) -> Unit = { section ->
        // Every tab switch becomes a real back-stack entry (no popUpTo/saveState) so system back
        // actually walks backward through tab history instead of exiting immediately - guarded
        // here so re-tapping the already-selected tab doesn't push a duplicate entry.
        if (currentRoute != section.route) {
            navController.navigate(section.route) { launchSingleTop = true }
        }
    }

    BoxWithConstraints(Modifier.fillMaxSize()) {
        val useRail = maxWidth >= 840.dp
        if (useRail) {
            Row(Modifier.fillMaxSize()) {
                SentinelNavigationRail(
                    selected = selectedSection,
                    state = state,
                    onSelected = onSelectSection,
                )
                SentinelNavHost(
                    navController = navController,
                    state = state,
                    model = model,
                    modifier = Modifier.weight(1f),
                )
            }
        } else {
            Scaffold(
                containerColor = SentinelBackground,
                bottomBar = {
                    NavigationBar(containerColor = SentinelPanel) {
                        AppSection.entries.forEach { item ->
                            NavigationBarItem(
                                selected = selectedSection == item,
                                onClick = { onSelectSection(item) },
                                icon = { Icon(item.icon, contentDescription = item.title) },
                                label = { Text(item.title) },
                            )
                        }
                    }
                },
            ) { padding ->
                SentinelNavHost(
                    navController = navController,
                    state = state,
                    model = model,
                    modifier = Modifier.padding(padding),
                )
            }
        }
    }
}

@Composable
private fun SentinelNavHost(
    navController: NavHostController,
    state: SentinelUiState,
    model: SentinelViewModel,
    modifier: Modifier = Modifier,
) {
    NavHost(navController = navController, startDestination = AppSection.FEED.route, modifier = modifier) {
        composable(AppSection.FEED.route) {
            EventFeedScreen(
                state = state,
                model = model,
                onEvent = { navController.navigate("feed/event/${it.id}") },
                onRefresh = { model.connect(resetNetworkSession = true) },
            )
        }
        composable(
            route = "feed/event/{eventId}",
            arguments = listOf(navArgument("eventId") { type = NavType.StringType }),
        ) { entry ->
            val event = state.events.firstOrNull { it.id == entry.arguments?.getString("eventId") }
            if (event != null) {
                EventDetailScreen(event = event, model = model, onBack = { navController.popBackStack() })
            } else {
                DetailLoading(onBack = { navController.popBackStack() })
            }
        }

        composable(AppSection.TIMELINE.route) {
            RecordingTimelineScreen(state = state, model = model)
        }

        composable(AppSection.LIVE.route) {
            LiveCamerasScreen(
                state = state,
                model = model,
                onCamera = { navController.navigate("live/camera/${it.id}") },
                onRefresh = model::connect,
            )
        }
        composable(
            route = "live/camera/{cameraId}",
            arguments = listOf(navArgument("cameraId") { type = NavType.StringType }),
        ) { entry ->
            val camera = state.cameras.firstOrNull { it.id == entry.arguments?.getString("cameraId") }
            if (camera != null) {
                LivePlayerScreen(camera = camera, model = model, onBack = { navController.popBackStack() })
            } else {
                DetailLoading(onBack = { navController.popBackStack() })
            }
        }

        composable(AppSection.TRIGGERS.route) {
            TriggerFeedScreen(
                state = state,
                model = model,
                onTrigger = { navController.navigate("triggers/trigger/${it.id}") },
                onRefresh = model::connect,
            )
        }
        composable(
            route = "triggers/trigger/{triggerId}",
            arguments = listOf(navArgument("triggerId") { type = NavType.StringType }),
        ) { entry ->
            val trigger = state.triggers.firstOrNull { it.id == entry.arguments?.getString("triggerId") }
            if (trigger != null) {
                TriggerDetailScreen(trigger = trigger, model = model, onBack = { navController.popBackStack() })
            } else {
                DetailLoading(onBack = { navController.popBackStack() })
            }
        }

        composable(AppSection.SETTINGS.route) {
            SettingsScreen(
                state = state,
                model = model,
                onManageCameras = { navController.navigate("settings/cameras") },
            )
        }

        composable("settings/cameras") {
            CameraAdminScreen(
                state = state,
                model = model,
                onBack = { navController.popBackStack() },
                onAddCamera = { navController.navigate("settings/cameras/new") },
                onEditCamera = { navController.navigate("settings/cameras/${it.id}") },
                onDiscover = { navController.navigate("settings/cameras/discover") },
            )
        }
        composable("settings/cameras/discover") {
            CameraDiscoveryScreen(
                model = model,
                onBack = { navController.popBackStack() },
                onResultSelected = {
                    navController.navigate(
                        "settings/cameras/new?prefill=${Uri.encode(it.ipAddress)}",
                    )
                },
            )
        }
        composable(
            route = "settings/cameras/new?prefill={prefill}",
            arguments = listOf(
                navArgument("prefill") {
                    type = NavType.StringType
                    nullable = true
                    defaultValue = null
                },
            ),
        ) { entry ->
            CameraEditScreen(
                camera = null,
                prefillStreamUrl = entry.arguments?.getString("prefill"),
                model = model,
                onBack = { navController.popBackStack() },
                onConfigureZone = {},
            )
        }
        composable(
            route = "settings/cameras/{cameraId}",
            arguments = listOf(navArgument("cameraId") { type = NavType.StringType }),
        ) { entry ->
            val camera = state.cameras.firstOrNull { it.id == entry.arguments?.getString("cameraId") }
            if (camera != null) {
                CameraEditScreen(
                    camera = camera,
                    prefillStreamUrl = null,
                    model = model,
                    onBack = { navController.popBackStack() },
                    onConfigureZone = { navController.navigate("settings/cameras/${it.id}/zone") },
                )
            } else {
                DetailLoading(onBack = { navController.popBackStack() })
            }
        }
        composable(
            route = "settings/cameras/{cameraId}/zone",
            arguments = listOf(navArgument("cameraId") { type = NavType.StringType }),
        ) { entry ->
            val camera = state.cameras.firstOrNull { it.id == entry.arguments?.getString("cameraId") }
            if (camera != null) {
                CameraZoneEditorScreen(
                    camera = camera,
                    model = model,
                    onBack = { navController.popBackStack() },
                )
            } else {
                DetailLoading(onBack = { navController.popBackStack() })
            }
        }
    }
}

// Shown briefly for a detail route whose id isn't in `state` yet (e.g. a push notification
// landed before the event list finished its first load) rather than dropping the navigation.
@Composable
private fun DetailLoading(onBack: () -> Unit) {
    Column(Modifier.fillMaxSize()) {
        DetailHeader("Loading…", onBack)
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            CircularProgressIndicator()
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
                // Kept as a deliberate monospace logotype - see Theme.kt for why the rest of the
                // app no longer forces monospace globally.
                Text(">_", color = SentinelGreen, fontFamily = FontFamily.Monospace)
                Text("SENTINEL", color = SentinelGreen, fontFamily = FontFamily.Monospace)
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

private enum class AppSection(
    val title: String,
    val icon: ImageVector,
    val route: String,
) {
    FEED("Feed", Icons.Default.Security, "feed"),
    TIMELINE("Timeline", Icons.Default.History, "timeline"),
    LIVE("Live", Icons.Default.LiveTv, "live"),
    TRIGGERS("Triggers", Icons.Default.Radar, "triggers"),
    SETTINGS("Settings", Icons.Default.Settings, "settings"),
}
