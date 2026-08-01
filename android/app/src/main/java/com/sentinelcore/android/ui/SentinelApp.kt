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
import androidx.compose.material.icons.filled.Lightbulb
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
import com.sentinelcore.android.ui.screens.AccountAdminScreen
import com.sentinelcore.android.ui.screens.AccountEditScreen
import com.sentinelcore.android.ui.screens.CameraAdminScreen
import com.sentinelcore.android.ui.screens.CameraDiscoveryScreen
import com.sentinelcore.android.ui.screens.CameraEditScreen
import com.sentinelcore.android.ui.screens.CameraZoneEditorScreen
import com.sentinelcore.android.ui.screens.DetailHeader
import com.sentinelcore.android.ui.screens.DeviceDiscoveryScreen
import com.sentinelcore.android.ui.screens.DeviceEditScreen
import com.sentinelcore.android.ui.screens.DeviceHubScreen
import com.sentinelcore.android.ui.screens.EventDetailScreen
import com.sentinelcore.android.ui.screens.EventFeedScreen
import com.sentinelcore.android.ui.screens.KnownFaceAdminScreen
import com.sentinelcore.android.ui.screens.KnownFaceEditScreen
import com.sentinelcore.android.ui.screens.LiveCamerasScreen
import com.sentinelcore.android.ui.screens.LivePlayerScreen
import com.sentinelcore.android.ui.screens.RecordingTimelineScreen
import com.sentinelcore.android.ui.screens.LoginScreen
import com.sentinelcore.android.ui.screens.ServerSetupScreen
import com.sentinelcore.android.ui.screens.ServerSettingsScreen
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
    // Local-network discovery is only useful on the setup screen - stopped as soon as a server
    // is configured so a background NSD listener doesn't keep running (and draining battery)
    // for the rest of the app's lifetime.
    LaunchedEffect(state.isConfigured) {
        if (state.isConfigured) model.stopDiscovery() else model.startDiscovery()
    }

    Box(
        modifier = Modifier.fillMaxSize().background(SentinelBackground),
    ) {
        when {
            !state.isConfigured -> ServerSetupScreen(
                initialAddress = state.serverUrl,
                discoveredServers = state.discoveredServers,
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
                        AppSection.visible(state.deviceHubEnabled).forEach { item ->
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

        composable(AppSection.DEVICES.route) {
            DeviceHubScreen(
                state = state,
                model = model,
                onAddDevice = { navController.navigate("devices/new") },
                onEditDevice = { navController.navigate("devices/${it.id}") },
                onDiscover = { navController.navigate("devices/discover") },
            )
        }
        composable("devices/new") {
            DeviceEditScreen(
                model = model,
                deviceId = null,
                onBack = { navController.popBackStack() },
                onSaved = { navController.popBackStack() },
            )
        }
        composable("devices/discover") {
            DeviceDiscoveryScreen(
                model = model,
                onBack = { navController.popBackStack() },
                // Review opens the add form pre-filled with what the scan found. The candidate is
                // stashed on the ViewModel rather than serialised through the route, since a
                // command topic can contain slashes that would break the path.
                onReview = { candidate ->
                    model.stageDiscoveredDevice(candidate)
                    navController.navigate("devices/review")
                },
            )
        }
        composable("devices/review") {
            DeviceEditScreen(
                model = model,
                deviceId = null,
                prefill = model.stagedDiscoveredDevice(),
                onBack = { navController.popBackStack() },
                onSaved = { navController.popBackStack(AppSection.DEVICES.route, inclusive = false) },
            )
        }
        composable(
            route = "devices/{deviceId}",
            arguments = listOf(navArgument("deviceId") { type = NavType.StringType }),
        ) { entry ->
            DeviceEditScreen(
                model = model,
                deviceId = entry.arguments?.getString("deviceId"),
                onBack = { navController.popBackStack() },
                onSaved = { navController.popBackStack() },
            )
        }
        composable(AppSection.SETTINGS.route) {
            SettingsScreen(
                state = state,
                model = model,
                onManageCameras = { navController.navigate("settings/cameras") },
                onManageAccounts = { navController.navigate("settings/accounts") },
                onManageKnownFaces = { navController.navigate("settings/faces") },
                onServerSettings = { navController.navigate("settings/server") },
            )
        }

        composable("settings/server") {
            ServerSettingsScreen(
                state = state,
                model = model,
                onBack = { navController.popBackStack() },
            )
        }

        composable("settings/faces") {
            KnownFaceAdminScreen(
                state = state,
                model = model,
                onBack = { navController.popBackStack() },
                onAddFace = { navController.navigate("settings/faces/new") },
                onEditFace = { navController.navigate("settings/faces/${it.id}") },
            )
        }
        composable("settings/faces/new") {
            KnownFaceEditScreen(
                face = null,
                model = model,
                onBack = { navController.popBackStack() },
            )
        }
        composable(
            route = "settings/faces/{faceId}",
            arguments = listOf(navArgument("faceId") { type = NavType.StringType }),
        ) { entry ->
            val face = state.knownFaces.firstOrNull { it.id == entry.arguments?.getString("faceId") }
            if (face != null) {
                KnownFaceEditScreen(
                    face = face,
                    model = model,
                    onBack = { navController.popBackStack() },
                )
            } else {
                DetailLoading(onBack = { navController.popBackStack() })
            }
        }

        composable("settings/accounts") {
            AccountAdminScreen(
                state = state,
                model = model,
                onBack = { navController.popBackStack() },
                onAddAccount = { navController.navigate("settings/accounts/new") },
            )
        }
        composable("settings/accounts/new") {
            AccountEditScreen(
                model = model,
                onBack = { navController.popBackStack() },
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
        AppSection.visible(state.deviceHubEnabled).forEach { item ->
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
    DEVICES("Devices", Icons.Default.Lightbulb, "devices"),
    SETTINGS("Settings", Icons.Default.Settings, "settings"),
    ;

    companion object {
        // The Device Hub is off by default and only appears once it's switched on server-side.
        // Compose's NavigationBar has no five-item limit like an iPhone tab bar, so this is a
        // plain filter with no other destination displaced.
        fun visible(deviceHubEnabled: Boolean): List<AppSection> =
            entries.filter { it != DEVICES || deviceHubEnabled }
    }
}
