package com.sentinelcore.android.ui

import android.app.Activity
import android.content.ActivityNotFoundException
import android.content.Intent
import android.net.Uri
import android.speech.RecognizerIntent
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MoreHoriz
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationRail
import androidx.compose.material3.NavigationRailItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
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
import com.sentinelcore.android.data.ConnectionState
import com.sentinelcore.android.organizer.OrganizerKinds
import com.sentinelcore.android.organizer.OrganizerNavigation
import com.sentinelcore.android.organizer.VoiceCapture
import com.sentinelcore.android.ui.organizer.CardEditorScreen
import com.sentinelcore.android.ui.organizer.EventEditorScreen
import com.sentinelcore.android.ui.organizer.NoteEditorScreen
import com.sentinelcore.android.ui.organizer.OrganizerGroupsScreen
import com.sentinelcore.android.ui.organizer.OrganizerRoutes
import com.sentinelcore.android.ui.organizer.OrganizerScreen
import com.sentinelcore.android.ui.organizer.OrganizerSubscribeScreen
import com.sentinelcore.android.ui.organizer.ReminderEditorScreen
import androidx.compose.runtime.DisposableEffect
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import com.sentinelcore.android.publish.DevicePublisher
import com.sentinelcore.android.ui.components.ConnectionBadge
import com.sentinelcore.android.ui.components.LocalIsLicensed
import com.sentinelcore.android.ui.files.DriveEditScreen
import com.sentinelcore.android.ui.files.FilesDrivesScreen
import com.sentinelcore.android.ui.files.FilesScreen
import com.sentinelcore.android.ui.files.FilesTrashScreen
import com.sentinelcore.android.ui.screens.AccountAdminScreen
import com.sentinelcore.android.ui.screens.AccountEditScreen
import com.sentinelcore.android.ui.screens.CameraAdminScreen
import com.sentinelcore.android.ui.screens.MediaLibrariesScreen
import com.sentinelcore.android.ui.screens.MediaLibraryEditScreen
import com.sentinelcore.android.ui.screens.CameraDiscoveryScreen
import com.sentinelcore.android.ui.screens.CameraEditScreen
import com.sentinelcore.android.ui.screens.CameraZoneEditorScreen
import com.sentinelcore.android.ui.screens.DetailHeader
import com.sentinelcore.android.ui.screens.DeviceCameraScreen
import com.sentinelcore.android.ui.screens.DEVICE_WALL_ROUTE
import com.sentinelcore.android.ui.screens.DeviceWallPreference
import com.sentinelcore.android.ui.screens.DeviceWallScreen
import com.sentinelcore.android.ui.screens.DeviceDiscoveryScreen
import com.sentinelcore.android.ui.screens.DeviceEditScreen
import com.sentinelcore.android.ui.screens.AlarmEventsScreen
import com.sentinelcore.android.ui.screens.AlarmScreen
import com.sentinelcore.android.ui.screens.AlarmSensorEditScreen
import com.sentinelcore.android.ui.screens.AlarmSettingsScreen
import com.sentinelcore.android.ui.screens.DeviceHubScreen
import com.sentinelcore.android.ui.screens.EventDetailScreen
import com.sentinelcore.android.ui.screens.ActivityDetailScreen
import com.sentinelcore.android.ui.screens.EventFeedScreen
import com.sentinelcore.android.ui.screens.BackupAdminScreen
import com.sentinelcore.android.ui.screens.DashboardLoginScreen
import com.sentinelcore.android.ui.screens.HomeKitAdminScreen
import com.sentinelcore.android.ui.screens.KnownFaceAdminScreen
import com.sentinelcore.android.ui.screens.KnownVehiclesScreen
import com.sentinelcore.android.ui.screens.LicenseAdminScreen
import com.sentinelcore.android.ui.screens.PromptProfilesScreen
import com.sentinelcore.android.ui.screens.KnownFaceEditScreen
import com.sentinelcore.android.ui.screens.LiveCamerasScreen
import com.sentinelcore.android.ui.screens.MOVIE_DETAIL_ROUTE
import com.sentinelcore.android.ui.screens.MOVIE_PLAYER_ROUTE
import com.sentinelcore.android.ui.screens.MOVIE_ADD_ROUTE
import com.sentinelcore.android.ui.screens.MOVIE_DOWNLOADS_ROUTE
import com.sentinelcore.android.ui.screens.MOVIE_DOWNLOAD_SITES_ROUTE
import com.sentinelcore.android.ui.screens.MovieAddScreen
import com.sentinelcore.android.ui.screens.MovieDetailScreen
import com.sentinelcore.android.ui.screens.MovieDownloadSitesScreen
import com.sentinelcore.android.ui.screens.MovieDownloadsScreen
import com.sentinelcore.android.ui.screens.MoviePlayerScreen
import com.sentinelcore.android.ui.screens.MoviesScreen
import com.sentinelcore.android.ui.weather.WeatherScreen
import com.sentinelcore.android.ui.screens.movieDetailRoute
import com.sentinelcore.android.ui.screens.moviePlayerRoute
import com.sentinelcore.android.ui.screens.LivePlayerScreen
import com.sentinelcore.android.ui.screens.RecordingTimelineScreen
import com.sentinelcore.android.ui.screens.ScheduleEditScreen
import com.sentinelcore.android.ui.screens.ScheduleListScreen
import com.sentinelcore.android.ui.screens.LoginScreen
import com.sentinelcore.android.ui.screens.ServerSetupScreen
import com.sentinelcore.android.ui.screens.ServerSettingsScreen
import com.sentinelcore.android.ui.screens.SettingsScreen
import com.sentinelcore.android.data.ServerCompatibility
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelBackground
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelPanel
import com.sentinelcore.android.ui.theme.SentinelTextDim
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.withTimeoutOrNull

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
    // The Organizer holds a long poll open while the app is in front; not in the background.
    val lifecycleOwner = LocalLifecycleOwner.current
    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            when (event) {
                Lifecycle.Event.ON_START -> model.setOrganizerActive(true)
                Lifecycle.Event.ON_STOP -> model.setOrganizerActive(false)
                else -> Unit
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }

    // Provided once here so the views that draw the UNLICENSED mark - SentinelRemoteImage, the
    // live player - can read it without each one taking the whole view model. See
    // UnlicensedWatermark.
    CompositionLocalProvider(LocalIsLicensed provides state.isLicensed) {
        Column(
            modifier = Modifier.fillMaxSize().background(SentinelBackground),
        ) {
            // Above the login screen as well as the main UI: "this server is too old" explains a
            // login that behaves oddly just as much as it explains an empty screen afterwards. It
            // renders nothing at all unless there's something to say.
            if (state.isConfigured) ServerVersionBanner(state.serverCompatibility)

            Box(modifier = Modifier.weight(1f)) {
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
    }
}

// Advisory, never blocking - the app works normally behind it. Amber rather than red for that
// reason: this is "you'll want to update the server soon", not "something is broken". Collapses to
// nothing whenever the server is current, or when its version simply couldn't be read.
@Composable
private fun ServerVersionBanner(compatibility: ServerCompatibility) {
    val advisory = compatibility.advisory ?: return
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(SentinelAmber.copy(alpha = 0.12f))
            .padding(horizontal = 16.dp, vertical = 10.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Icon(
            Icons.Filled.Warning,
            contentDescription = null,
            tint = SentinelAmber,
            modifier = Modifier.size(18.dp),
        )
        Text(
            text = advisory,
            style = MaterialTheme.typography.bodySmall,
            color = SentinelTextDim,
        )
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
    // Detail routes are nested under their section ("events/{activityId}"), so a route match
    // has to check for that prefix too - otherwise the tab bar would deselect itself the moment
    // a detail screen opens. The push-only "feed/event/{eventId}" route matches no section and
    // falls through to Events, which is where its Back lands anyway.
    val selectedSection = AppSection.entries.firstOrNull { section ->
        currentRoute == section.route || currentRoute?.startsWith("${section.route}/") == true
    } ?: AppSection.EVENTS
    // The Device Hub's wall display is the one screen that takes the whole tablet: no bar, no
    // rail, nothing to tap by accident. Its own Exit (or the back gesture) is the way out.
    // The movie player is the other: a film gets the whole screen.
    val chromeHidden = currentRoute == DEVICE_WALL_ROUTE || currentRoute == MOVIE_PLAYER_ROUTE

    // Navigates straight to the pushed event without waiting for state.events to already contain
    // it - the detail route itself shows a brief loading state if the list hasn't caught up yet,
    // which is simpler and more robust than the old approach of dropping the deep link entirely
    // when the event wasn't loaded yet.
    LaunchedEffect(state.pendingPushEventId) {
        val eventId = state.pendingPushEventId ?: return@LaunchedEffect
        navController.navigate("feed/event/$eventId") { launchSingleTop = true }
        model.consumePushEvent()
    }

    // If this phone was streaming as a camera when the app last ran, it goes back to being one as
    // soon as the server is reachable - and shows the screen, so it is obvious that it did. The
    // notification's tap lands on the same screen. See DevicePublisher.
    val openDeviceCamera by DevicePublisher.openScreenRequested.collectAsStateWithLifecycle()
    LaunchedEffect(state.connectionState, state.isAdmin, openDeviceCamera) {
        if (!DevicePublisher.isAttached) return@LaunchedEffect
        val resumed = state.connectionState == ConnectionState.ONLINE && state.isAdmin &&
            DevicePublisher.resumeIfIntended() != null
        if (resumed || openDeviceCamera) {
            DevicePublisher.openScreenRequested.value = false
            navController.navigate("settings/device-camera") { launchSingleTop = true }
        }
    }

    // An alarm push was tapped - see PushNotifications. The person wants the arm/disarm
    // buttons, not the feed.
    val openAlarm by AlarmNavigation.open.collectAsStateWithLifecycle()
    LaunchedEffect(openAlarm, state.alarmEnabled) {
        if (openAlarm && state.alarmEnabled) {
            AlarmNavigation.open.value = false
            navController.navigate(AppSection.ALARM.route) { launchSingleTop = true }
        }
    }

    // A reminder or event alert was tapped - see OrganizerAlarms.
    val openOrganizer by OrganizerNavigation.open.collectAsStateWithLifecycle()
    LaunchedEffect(openOrganizer, state.organizerEnabled) {
        if (openOrganizer && state.organizerEnabled) {
            OrganizerNavigation.open.value = false
            navController.navigate(AppSection.ORGANIZER.route) { launchSingleTop = true }
        }
    }

    // A voice reminder or note - see organizer/VoiceCapture. The launcher shortcuts arrive with
    // nothing said yet, so the phone's speech recognizer listens first; the Assistant's App Action
    // arrives with the words. Either way it's added and the Organizer shown, so it is obvious
    // where it went.
    val voiceContext = LocalContext.current
    val voiceRequest by VoiceCapture.pending.collectAsStateWithLifecycle()
    var listeningFor by rememberSaveable { mutableStateOf<String?>(null) }
    val addSpoken: (String, String, VoiceCapture.Request?) -> Unit = { kind, text, request ->
        val (added, message) = model.addFromVoice(kind, text, request?.subject, request?.shared == true)
        Toast.makeText(voiceContext, message, Toast.LENGTH_LONG).show()
        if (added && state.organizerEnabled) {
            OrganizerNavigation.pane.value = if (kind == OrganizerKinds.NOTE) "notes" else "reminders"
            navController.navigate(AppSection.ORGANIZER.route) { launchSingleTop = true }
        }
    }
    val listen = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        val kind = listeningFor
        listeningFor = null
        val words = result.data?.getStringArrayListExtra(RecognizerIntent.EXTRA_RESULTS)?.firstOrNull()
        if (result.resultCode == Activity.RESULT_OK && kind != null && !words.isNullOrBlank()) addSpoken(kind, words, null)
    }
    LaunchedEffect(voiceRequest) {
        val request = voiceRequest ?: return@LaunchedEffect
        VoiceCapture.pending.value = null
        val text = request.text
        if (text != null) {
            // Opened cold by a share or the Assistant: give the first sync a moment to say which
            // spaces there are, rather than turning it away.
            withTimeoutOrNull(10_000) { model.organizer.spaces.first { it.isNotEmpty() } }
            addSpoken(request.kind, text, request)
            return@LaunchedEffect
        }
        listeningFor = request.kind
        val prompt = if (request.kind == OrganizerKinds.NOTE) "What should the note say?" else "What's the reminder?"
        try {
            listen.launch(
                Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH)
                    .putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
                    .putExtra(RecognizerIntent.EXTRA_PROMPT, prompt),
            )
        } catch (_: ActivityNotFoundException) {
            listeningFor = null
            Toast.makeText(voiceContext, "This phone has no speech recognition app.", Toast.LENGTH_LONG).show()
        }
    }

    val onSelectSection: (AppSection) -> Unit = { section ->
        // Every tab switch becomes a real back-stack entry (no popUpTo/saveState) so system back
        // actually walks backward through tab history instead of exiting immediately - guarded
        // here so re-tapping the already-selected tab doesn't push a duplicate entry.
        if (currentRoute != section.route) {
            navController.navigate(section.route) { launchSingleTop = true }
        }
    }

    val context = LocalContext.current
    val tabBarPreferences = remember { TabBarPreferences(context.applicationContext) }

    // A tablet on a wall that was left in the wall display comes back to it on launch rather
    // than to Events. Once per run, and only once the server has said the hub is on - before that
    // there's no Device Hub to show. Leaving the wall display clears the preference.
    var openedWallDisplay by rememberSaveable { mutableStateOf(false) }
    LaunchedEffect(state.deviceHubEnabled) {
        if (state.deviceHubEnabled && !openedWallDisplay && DeviceWallPreference.isOn(context)) {
            openedWallDisplay = true
            navController.navigate(DEVICE_WALL_ROUTE) { launchSingleTop = true }
        }
    }
    val visibleSections = AppSection.visible(
        state.deviceHubEnabled,
        state.organizerEnabled,
        moviesEnabled = state.moviesEnabled,
        alarmEnabled = state.alarmEnabled,
        filesEnabled = state.familyFilesEnabled,
        weatherEnabled = state.weatherEnabled,
    )
    val openTabBarEditor = { navController.navigate(TAB_BAR_EDITOR_ROUTE) { launchSingleTop = true } }

    BoxWithConstraints(Modifier.fillMaxSize()) {
        val useRail = maxWidth >= 840.dp
        if (useRail) {
            Row(Modifier.fillMaxSize()) {
                if (!chromeHidden) {
                    SentinelNavigationRail(
                        selected = selectedSection,
                        state = state,
                        onSelected = onSelectSection,
                    )
                }
                SentinelNavHost(
                    navController = navController,
                    state = state,
                    model = model,
                    tabBarPreferences = tabBarPreferences,
                    // The rail shows every section, so there is no bar to customize.
                    onCustomizeTabBar = null,
                    modifier = Modifier.weight(1f),
                )
            }
        } else {
            val layout = tabBarPreferences.layout(visibleSections)
            var showMore by rememberSaveable { mutableStateOf(false) }
            Scaffold(
                containerColor = SentinelBackground,
                bottomBar = {
                    if (!chromeHidden) NavigationBar(containerColor = SentinelPanel) {
                        layout.bar.forEach { item ->
                            NavigationBarItem(
                                selected = selectedSection == item,
                                onClick = { onSelectSection(item) },
                                icon = { Icon(item.icon, contentDescription = item.title) },
                                label = { Text(item.title) },
                            )
                        }
                        if (layout.showsMore) {
                            // Selected whenever the section on screen has no tab of its own -
                            // whether it was picked from More or reached by a deep link (a pushed
                            // alert, a tapped Organizer alert, the device-camera screen).
                            NavigationBarItem(
                                selected = selectedSection !in layout.bar,
                                onClick = { showMore = true },
                                icon = { Icon(Icons.Default.MoreHoriz, contentDescription = "More") },
                                label = { Text("More") },
                            )
                        }
                    }
                },
            ) { padding ->
                SentinelNavHost(
                    navController = navController,
                    state = state,
                    model = model,
                    tabBarPreferences = tabBarPreferences,
                    onCustomizeTabBar = openTabBarEditor,
                    modifier = Modifier.padding(padding),
                )
            }
            if (showMore && layout.showsMore) {
                MoreSectionsSheet(
                    sections = layout.more,
                    onSelect = { section ->
                        showMore = false
                        onSelectSection(section)
                    },
                    onEdit = {
                        showMore = false
                        openTabBarEditor()
                    },
                    onDismiss = { showMore = false },
                )
            }
        }
    }
}

// Under Settings, so the bar highlights Settings (or More, if Settings has no tab) while it's open.
private const val TAB_BAR_EDITOR_ROUTE = "settings/tab-bar"
private const val MEDIA_LIBRARIES_ROUTE = "settings/media-libraries"

@Composable
private fun SentinelNavHost(
    navController: NavHostController,
    state: SentinelUiState,
    model: SentinelViewModel,
    tabBarPreferences: TabBarPreferences,
    onCustomizeTabBar: (() -> Unit)?,
    modifier: Modifier = Modifier,
) {
    NavHost(navController = navController, startDestination = AppSection.EVENTS.route, modifier = modifier) {
        composable(AppSection.EVENTS.route) {
            EventFeedScreen(
                state = state,
                model = model,
                onEvent = { navController.navigate("events/${Uri.encode(it.id)}") },
                onRefresh = { model.connect(resetNetworkSession = true) },
            )
        }
        // Opened from the merged list: an id that may be either an alert guid or a prefixed
        // "trigger:<guid>", so it is encoded into the route.
        composable(
            route = "events/{activityId}",
            arguments = listOf(navArgument("activityId") { type = NavType.StringType }),
        ) { entry ->
            val event = state.activity.firstOrNull { it.id == entry.arguments?.getString("activityId") }
            if (event != null) {
                ActivityDetailScreen(event = event, model = model, onBack = { navController.popBackStack() })
            } else {
                DetailLoading(onBack = { navController.popBackStack() })
            }
        }
        // The push path starts from an alert id rather than from the list, and can open an alert
        // that isn't on the current page at all - see SentinelUiState.pushedEvent.
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

        composable(AppSection.MOVIES.route) {
            MoviesScreen(
                state = state,
                model = model,
                onItem = { navController.navigate(movieDetailRoute(it)) },
                onPlay = { id, start -> navController.navigate(moviePlayerRoute(id, start)) },
                onManageLibraries = { navController.navigate(MEDIA_LIBRARIES_ROUTE) { launchSingleTop = true } },
                onAddDownload = { navController.navigate(MOVIE_ADD_ROUTE) { launchSingleTop = true } },
                onDownloads = { navController.navigate(MOVIE_DOWNLOADS_ROUTE) { launchSingleTop = true } },
            )
        }
        // Movie downloads (core/docs/MOVIE_DOWNLOADS.md). A started download replaces the Add
        // screen with the queue, so Back from there returns to Movies.
        composable(MOVIE_ADD_ROUTE) {
            MovieAddScreen(
                model = model,
                onBack = { navController.popBackStack() },
                onDownloadStarted = {
                    navController.navigate(MOVIE_DOWNLOADS_ROUTE) {
                        popUpTo(MOVIE_ADD_ROUTE) { inclusive = true }
                        launchSingleTop = true
                    }
                },
            )
        }
        composable(MOVIE_DOWNLOADS_ROUTE) {
            MovieDownloadsScreen(
                model = model,
                onBack = { navController.popBackStack() },
                onAdd = { navController.navigate(MOVIE_ADD_ROUTE) { launchSingleTop = true } },
                onSites = { navController.navigate(MOVIE_DOWNLOAD_SITES_ROUTE) { launchSingleTop = true } },
                onOpenItem = { navController.navigate(movieDetailRoute(it)) },
            )
        }
        composable(MOVIE_DOWNLOAD_SITES_ROUTE) {
            MovieDownloadSitesScreen(model = model, onBack = { navController.popBackStack() })
        }
        composable(
            route = MOVIE_DETAIL_ROUTE,
            arguments = listOf(navArgument("id") { type = NavType.StringType }),
        ) { entry ->
            MovieDetailScreen(
                state = state,
                model = model,
                itemId = entry.arguments?.getString("id").orEmpty(),
                onBack = { navController.popBackStack() },
                onItem = { navController.navigate(movieDetailRoute(it)) },
                onPlay = { id, start -> navController.navigate(moviePlayerRoute(id, start)) },
            )
        }
        composable(
            route = MOVIE_PLAYER_ROUTE,
            arguments = listOf(
                navArgument("id") { type = NavType.StringType },
                navArgument("start") { type = NavType.StringType; nullable = true; defaultValue = null },
            ),
        ) { entry ->
            MoviePlayerScreen(
                model = model,
                itemId = entry.arguments?.getString("id").orEmpty(),
                start = entry.arguments?.getString("start")?.toDoubleOrNull(),
                onBack = { navController.popBackStack() },
                // The next episode replaces this player rather than stacking on it, so Back
                // still lands on the screen the first episode was started from.
                onPlayNext = { nextId ->
                    navController.popBackStack()
                    navController.navigate(moviePlayerRoute(nextId, null))
                },
            )
        }

        composable(AppSection.WEATHER.route) {
            WeatherScreen(state = state, model = model)
        }

        composable(AppSection.ALARM.route) {
            AlarmScreen(
                state = state,
                model = model,
                onAddSensor = { navController.navigate("alarm/sensors/new") },
                onEditSensor = { navController.navigate("alarm/sensors/${it.id}") },
                onSettings = { navController.navigate("alarm/settings") },
                onEvents = { navController.navigate("alarm/events") },
            )
        }
        composable("alarm/sensors/new") {
            AlarmSensorEditScreen(model = model, state = state, sensorId = null, onBack = { navController.popBackStack() })
        }
        composable("alarm/sensors/{sensorId}") { entry ->
            AlarmSensorEditScreen(
                model = model,
                state = state,
                sensorId = entry.arguments?.getString("sensorId"),
                onBack = { navController.popBackStack() },
            )
        }
        composable("alarm/settings") {
            AlarmSettingsScreen(model = model, state = state, onBack = { navController.popBackStack() })
        }
        composable("alarm/events") {
            AlarmEventsScreen(model = model, onBack = { navController.popBackStack() })
        }

        composable(AppSection.DEVICES.route) {
            val context = LocalContext.current
            DeviceHubScreen(
                state = state,
                model = model,
                onAddDevice = { navController.navigate("devices/new") },
                onEditDevice = { navController.navigate("devices/${it.id}") },
                onDiscover = { navController.navigate("devices/discover") },
                onWallDisplay = {
                    DeviceWallPreference.set(context, true)
                    navController.navigate(DEVICE_WALL_ROUTE) { launchSingleTop = true }
                },
            )
        }
        composable(DEVICE_WALL_ROUTE) {
            val context = LocalContext.current
            DeviceWallScreen(
                state = state,
                model = model,
                onExit = {
                    DeviceWallPreference.set(context, false)
                    // Back to the hub itself, whether the wall display was opened from it or
                    // straight from launch.
                    navController.navigate(AppSection.DEVICES.route) {
                        popUpTo(DEVICE_WALL_ROUTE) { inclusive = true }
                        launchSingleTop = true
                    }
                },
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
                onDeviceCamera = { navController.navigate("settings/device-camera") },
                onManageAccounts = { navController.navigate("settings/accounts") },
                onDashboardLogin = { navController.navigate("settings/login") },
                onManageKnownFaces = { navController.navigate("settings/faces") },
                onKnownVehicles = { navController.navigate("settings/vehicles") },
                onPromptProfiles = { navController.navigate("settings/prompts") },
                onHomeKit = { navController.navigate("settings/homekit") },
                onBackup = { navController.navigate("settings/backup") },
                onLicense = { navController.navigate("settings/license") },
                onManageSchedules = { navController.navigate("settings/schedules") },
                onServerSettings = { navController.navigate("settings/server") },
                onMediaLibraries = { navController.navigate(MEDIA_LIBRARIES_ROUTE) },
                onCustomizeTabBar = onCustomizeTabBar,
            )
        }
        composable(TAB_BAR_EDITOR_ROUTE) {
            TabBarEditorScreen(
                preferences = tabBarPreferences,
                visible = AppSection.visible(
                    state.deviceHubEnabled,
                    state.organizerEnabled,
                    moviesEnabled = state.moviesEnabled,
                    alarmEnabled = state.alarmEnabled,
                    filesEnabled = state.familyFilesEnabled,
                    weatherEnabled = state.weatherEnabled,
                ),
                onBack = { navController.popBackStack() },
            )
        }

        composable(AppSection.FILES.route) {
            FilesScreen(
                state = state,
                model = model,
                onTrash = { navController.navigate("files/trash") },
                onDrives = { navController.navigate("files/drives") },
            )
        }
        composable("files/trash") {
            FilesTrashScreen(state = state, model = model, onBack = { navController.popBackStack() })
        }
        composable("files/drives") {
            FilesDrivesScreen(
                state = state,
                model = model,
                onBack = { navController.popBackStack() },
                onEditDrive = { id -> navController.navigate("files/drive/${id ?: "new"}") },
            )
        }
        composable("files/drive/{id}", arguments = listOf(navArgument("id") { type = NavType.StringType })) { entry ->
            DriveEditScreen(
                state = state,
                model = model,
                driveId = entry.arguments?.getString("id")?.takeIf { it != "new" },
                onBack = { navController.popBackStack() },
            )
        }

        composable(AppSection.ORGANIZER.route) {
            OrganizerScreen(
                engine = model.organizer,
                routes = OrganizerRoutes(
                    openEvent = { id, day, original ->
                        val query = listOfNotNull(day?.let { "day=$it" }, original?.let { "original=${Uri.encode(it)}" }).joinToString("&")
                        navController.navigate("organizer/event/${id ?: "new"}" + if (query.isEmpty()) "" else "?$query")
                    },
                    openReminder = { navController.navigate("organizer/reminder/${it ?: "new"}") },
                    openNote = { navController.navigate("organizer/note/${it ?: "new"}") },
                    openChecklist = { navController.navigate("organizer/note/new?checklist=true") },
                    openCard = { navController.navigate("organizer/card/$it") },
                    openGroups = { navController.navigate("organizer/groups") },
                    openSubscribe = { navController.navigate("organizer/subscribe") },
                ),
            )
        }
        composable(
            route = "organizer/event/{id}?day={day}&original={original}",
            arguments = listOf(
                navArgument("id") { type = NavType.StringType },
                navArgument("day") { type = NavType.StringType; nullable = true; defaultValue = null },
                navArgument("original") { type = NavType.StringType; nullable = true; defaultValue = null },
            ),
        ) { entry ->
            val id = entry.arguments?.getString("id")?.takeIf { it != "new" }
            EventEditorScreen(
                engine = model.organizer,
                itemId = id,
                day = entry.arguments?.getString("day")?.let(java.time.LocalDate::parse),
                originalStart = entry.arguments?.getString("original"),
                onDone = { navController.popBackStack() },
            )
        }
        composable("organizer/reminder/{id}", arguments = listOf(navArgument("id") { type = NavType.StringType })) { entry ->
            ReminderEditorScreen(model.organizer, entry.arguments?.getString("id")?.takeIf { it != "new" }) { navController.popBackStack() }
        }
        composable(
            route = "organizer/note/{id}?checklist={checklist}",
            arguments = listOf(
                navArgument("id") { type = NavType.StringType },
                navArgument("checklist") { type = NavType.BoolType; defaultValue = false },
            ),
        ) { entry ->
            NoteEditorScreen(
                engine = model.organizer,
                itemId = entry.arguments?.getString("id")?.takeIf { it != "new" },
                startAsChecklist = entry.arguments?.getBoolean("checklist") ?: false,
                onDone = { navController.popBackStack() },
            )
        }
        composable("organizer/card/{id}", arguments = listOf(navArgument("id") { type = NavType.StringType })) { entry ->
            CardEditorScreen(model.organizer, entry.arguments?.getString("id").orEmpty()) { navController.popBackStack() }
        }
        composable("organizer/groups") {
            OrganizerGroupsScreen(model.organizer) { navController.popBackStack() }
        }
        composable("organizer/subscribe") {
            // The remote address if there is one: a calendar service out on the internet can't
            // reach a LAN address.
            OrganizerSubscribeScreen(model.organizer, state.remoteServerUrl.ifBlank { state.activeServerUrl }) { navController.popBackStack() }
        }

        composable("settings/schedules") {
            ScheduleListScreen(
                model = model,
                isAdmin = state.isAdmin,
                onBack = { navController.popBackStack() },
                onAddSchedule = { navController.navigate("settings/schedules/new") },
                onEditSchedule = { navController.navigate("settings/schedules/${it.id}") },
            )
        }
        composable("settings/schedules/new") {
            ScheduleEditScreen(
                model = model,
                scheduleId = null,
                onBack = { navController.popBackStack() },
                onSaved = { navController.popBackStack() },
            )
        }
        composable(
            "settings/schedules/{scheduleId}",
            arguments = listOf(navArgument("scheduleId") { type = NavType.StringType }),
        ) { entry ->
            ScheduleEditScreen(
                model = model,
                scheduleId = entry.arguments?.getString("scheduleId"),
                onBack = { navController.popBackStack() },
                onSaved = { navController.popBackStack() },
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

        composable("settings/prompts") {
            PromptProfilesScreen(
                state = state,
                model = model,
                onBack = { navController.popBackStack() },
            )
        }

        composable("settings/backup") {
            BackupAdminScreen(
                state = state,
                model = model,
                onBack = { navController.popBackStack() },
            )
        }

        composable("settings/homekit") {
            HomeKitAdminScreen(
                state = state,
                model = model,
                onBack = { navController.popBackStack() },
            )
        }

        composable("settings/license") {
            LicenseAdminScreen(
                state = state,
                model = model,
                onBack = { navController.popBackStack() },
            )
        }

        composable(MEDIA_LIBRARIES_ROUTE) {
            MediaLibrariesScreen(
                state = state,
                model = model,
                onBack = { navController.popBackStack() },
                onEditLibrary = { id -> navController.navigate("$MEDIA_LIBRARIES_ROUTE/${Uri.encode(id ?: "new")}") },
            )
        }
        composable(
            "$MEDIA_LIBRARIES_ROUTE/{id}",
            arguments = listOf(navArgument("id") { type = NavType.StringType }),
        ) { entry ->
            MediaLibraryEditScreen(
                state = state,
                model = model,
                libraryId = entry.arguments?.getString("id")?.takeIf { it != "new" },
                onBack = { navController.popBackStack() },
            )
        }

        composable("settings/vehicles") {
            KnownVehiclesScreen(
                state = state,
                model = model,
                onBack = { navController.popBackStack() },
            )
        }

        composable("settings/login") {
            DashboardLoginScreen(
                state = state,
                model = model,
                onBack = { navController.popBackStack() },
            )
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

        composable("settings/device-camera") {
            DeviceCameraScreen(
                state = state,
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
        AppSection.visible(
            state.deviceHubEnabled,
            state.organizerEnabled,
            moviesEnabled = state.moviesEnabled,
            alarmEnabled = state.alarmEnabled,
            filesEnabled = state.familyFilesEnabled,
            weatherEnabled = state.weatherEnabled,
        ).forEach { item ->
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
