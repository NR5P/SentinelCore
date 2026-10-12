package com.sentinelcore.android.ui.weather

import android.content.Context
import android.graphics.BitmapFactory
import androidx.activity.compose.BackHandler
import androidx.compose.animation.animateContentSize
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.runtime.mutableDoubleStateOf
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.positionInParent
import androidx.compose.ui.platform.LocalContext
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.drawText
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.ViewModel
import androidx.lifecycle.repeatOnLifecycle
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import com.sentinelcore.android.data.Mercator
import com.sentinelcore.android.data.SentinelApiException
import com.sentinelcore.android.data.WeatherAlert
import com.sentinelcore.android.data.WeatherDay
import com.sentinelcore.android.data.WeatherDetails
import com.sentinelcore.android.data.WeatherHour
import com.sentinelcore.android.data.WeatherLogic
import com.sentinelcore.android.data.WeatherMapInfo
import com.sentinelcore.android.data.WeatherReport
import com.sentinelcore.android.data.WeatherState
import com.sentinelcore.android.data.WeatherStormCell
import com.sentinelcore.android.data.WeatherStorms
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.screens.ErrorPanel
import com.sentinelcore.android.ui.screens.ScreenHeader
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelTextDim
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale
import kotlin.math.roundToInt

// The Weather tab: current conditions, the radar map, the hourly chart and strip, the seven-day
// forecast, storm tracking and the severe-weather outlook for the location set on the server -
// the dashboard's weather.js, natively. Everything comes from this server, which talks to the
// National Weather Service and friends and caches the answers (WeatherService.cs,
// WeatherMapService.cs). Fetches only while the tab is on screen, like the dashboard.

internal const val REPORT_REFRESH_MS = 10 * 60_000L
internal const val RADAR_REFRESH_MS = 2 * 60_000L

// Times in the location's own zone, in the phone's 12/24-hour style.
internal class WeatherClock(val zone: ZoneId, is24Hour: Boolean) {
    private val locale = Locale.getDefault()
    private val clockFormat = DateTimeFormatter.ofPattern(if (is24Hour) "HH:mm" else "h:mm a", locale).withZone(zone)
    private val hourFormat = DateTimeFormatter.ofPattern(if (is24Hour) "HH" else "h a", locale).withZone(zone)
    private val weekdayFormat = DateTimeFormatter.ofPattern("EEE", locale).withZone(zone)
    private val longWeekdayFormat = DateTimeFormatter.ofPattern("EEEE", locale)

    fun clock(at: Instant): String = clockFormat.format(at)
    fun hour(at: Instant): String = hourFormat.format(at)
    fun weekday(at: Instant): String = weekdayFormat.format(at)
    fun weekdayClock(at: Instant): String = weekdayFormat.format(at) + " " + clockFormat.format(at)
    fun longWeekday(date: LocalDate): String = longWeekdayFormat.format(date)
    fun today(): LocalDate = LocalDate.now(zone)
}

// Per-person map preferences, kept on this phone like the dashboard keeps them in the browser.
internal data class WeatherMapPrefs(
    val layer: String = "reflectivity",
    val station: String? = null,
    val opacity: Float = 0.75f,
    val cells: Boolean = true,
    val warnings: Boolean = true,
    val outlook: Boolean = false,
    val sites: Boolean = true,
)

// Kept per back-stack entry, so the report, the map's position and the loaded tiles survive a
// trip to another tab. Data calls go straight through to SentinelViewModel.
internal class WeatherScreenModel(private val app: SentinelViewModel, context: Context) : ViewModel() {
    private val preferences = context.getSharedPreferences("sentinel_core", Context.MODE_PRIVATE)

    var prefs by mutableStateOf(readPrefs())
        private set
    var report by mutableStateOf<WeatherReport?>(null)
    var details by mutableStateOf<WeatherDetails?>(null)
    var storms by mutableStateOf<WeatherStorms?>(null)
    var mapInfo by mutableStateOf<WeatherMapInfo?>(null)
    var mapUnavailable by mutableStateOf(false)
    var loading by mutableStateOf(false)
    var error by mutableStateOf<String?>(null)
    var refreshFailed by mutableStateOf(false)

    // What's expanded, by id, so a ten-minute refresh doesn't fold up the warning being read.
    val openAlerts = mutableStateListOf<String>()
    val openDays = mutableStateListOf<LocalDate>()
    var discussionOpen by mutableStateOf(false)
    var selectedHour by mutableStateOf<Instant?>(null)

    // The map.
    var centerX by mutableDoubleStateOf(Mercator.x(-98.35))
    var centerY by mutableDoubleStateOf(Mercator.y(39.5))
    var zoom by mutableDoubleStateOf(4.0)
    private var centeredOnHome = false
    var frameIndex by mutableIntStateOf(0)
    var playing by mutableStateOf(false)
        private set
    var selection by mutableStateOf<MapSelection?>(null)
    val tiles = WeatherTileStore(viewModelScope) { app.weatherBytes(it) }
    private var lastView: MapView? = null
    private val legends = HashMap<String, ImageBitmap>()

    private val maxZoom: Double get() = (mapInfo?.maximumBaseZoom ?: 16).toDouble()

    suspend fun load() {
        if (loading) return
        loading = true
        try {
            val next = app.weatherReport()
            report = next
            error = null
            refreshFailed = false
            if (next.state == WeatherState.OK) {
                if (!centeredOnHome) {
                    next.location?.let { centerOn(it.latitude, it.longitude, 8.0) }
                    centeredOnHome = true
                }
                runCatching { app.weatherDetails() }.onSuccess { details = it }
            }
        } catch (failure: Exception) {
            if (report == null) error = failure.message ?: "Could not load the weather."
            else refreshFailed = true
        } finally {
            loading = false
        }
    }

    suspend fun refreshRadar() {
        if (report?.state != WeatherState.OK) return
        if (!mapUnavailable) loadMap()
        loadStorms()
    }

    private suspend fun loadMap() {
        try {
            val next = app.weatherMap(prefs.station)
            val old = mapInfo
            val sameFrames = old != null && old.station == next.station &&
                old.layers.map { it.times } == next.layers.map { it.times }
            mapInfo = next
            mapUnavailable = false
            if (!sameFrames) {
                val count = WeatherLogic.currentLayer(next, prefs.layer)?.times?.size ?: 0
                frameIndex = (count - 1).coerceAtLeast(0)
            }
        } catch (failure: SentinelApiException) {
            if (failure.statusCode == 404) mapUnavailable = true
        } catch (_: Exception) {
            // Kept as it was; the next two-minute refresh tries again.
        }
    }

    // The map's view and home's surroundings, separately, merged - so the near-home list and the
    // banner stay right while someone looks at a storm three states away.
    suspend fun loadStorms() {
        val location = report?.location ?: return
        val view = lastView
        val nearPath = WeatherLogic.stormsPath(
            location.longitude - 2.5, location.latitude - 2, location.longitude + 2.5, location.latitude + 2,
        )
        val viewPath = view?.bounds(0.3)?.let { WeatherLogic.stormsPath(it[0], it[1], it[2], it[3]) }
        coroutineScope {
            val near = async { runCatching { app.weatherStorms(nearPath) }.getOrNull() }
            val seen = viewPath?.let { async { runCatching { app.weatherStorms(it) }.getOrNull() } }
            val nearStorms = near.await()
            val viewStorms = seen?.await()
            val merged = when {
                viewStorms != null -> WeatherLogic.mergeStorms(viewStorms, nearStorms)
                nearStorms != null -> nearStorms
                else -> return@coroutineScope
            }
            storms = merged
        }
    }

    fun onViewSettled(view: MapView) {
        lastView = view
        viewModelScope.launch { loadStorms() }
    }

    suspend fun radarGif(scope: String): ByteArray? =
        app.weatherBytes("/api/weather/radar?scope=$scope&kind=loop&t=${System.currentTimeMillis() / RADAR_REFRESH_MS}")

    suspend fun legend(path: String): ImageBitmap? {
        legends[path]?.let { return it }
        val bytes = app.weatherBytes(path) ?: return null
        val image = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)?.asImageBitmap() ?: return null
        legends[path] = image
        return image
    }

    // ---- Map controls ----

    fun updatePrefs(change: WeatherMapPrefs.() -> WeatherMapPrefs) {
        prefs = prefs.change()
        preferences.edit()
            .putString(PREF_LAYER, prefs.layer)
            .putString(PREF_STATION, prefs.station)
            .putFloat(PREF_OPACITY, prefs.opacity)
            .putBoolean(PREF_CELLS, prefs.cells)
            .putBoolean(PREF_WARNINGS, prefs.warnings)
            .putBoolean(PREF_OUTLOOK, prefs.outlook)
            .putBoolean(PREF_SITES, prefs.sites)
            .apply()
    }

    private fun readPrefs(): WeatherMapPrefs = WeatherMapPrefs(
        layer = preferences.getString(PREF_LAYER, null) ?: "reflectivity",
        station = preferences.getString(PREF_STATION, null),
        opacity = preferences.getFloat(PREF_OPACITY, 0.75f).coerceIn(0.2f, 1f),
        cells = preferences.getBoolean(PREF_CELLS, true),
        warnings = preferences.getBoolean(PREF_WARNINGS, true),
        outlook = preferences.getBoolean(PREF_OUTLOOK, false),
        sites = preferences.getBoolean(PREF_SITES, true),
    )

    fun selectLayer(id: String) {
        stopPlaying()
        updatePrefs { copy(layer = id) }
        val count = WeatherLogic.currentLayer(mapInfo, id)?.times?.size ?: 0
        frameIndex = (count - 1).coerceAtLeast(0)
    }

    fun selectStation(id: String) {
        val info = mapInfo
        val layerIsStation = WeatherLogic.currentLayer(info, prefs.layer)?.isStation == true
        updatePrefs {
            copy(
                station = if (id == info?.homeStation) null else id,
                layer = if (layerIsStation) layer else "station-reflectivity",
            )
        }
        stopPlaying()
        selection = null
        viewModelScope.launch { loadMap() }
    }

    fun togglePlay(frameCount: Int) {
        if (playing) stopPlaying() else if (frameCount > 1) playing = true
    }

    fun stopPlaying() {
        playing = false
    }

    private fun centerOn(latitude: Double, longitude: Double, atLeastZoom: Double) {
        centerX = Mercator.x(longitude)
        centerY = Mercator.y(latitude)
        zoom = maxOf(zoom, atLeastZoom).coerceIn(MIN_ZOOM, maxZoom)
    }

    fun goHome() {
        val home = report?.location ?: return
        centerOn(home.latitude, home.longitude, 8.0)
    }

    fun focusCell(cell: WeatherStormCell) {
        if (!prefs.cells) updatePrefs { copy(cells = true) }
        selection = MapSelection.Cell(cell.id)
        centerOn(cell.latitude, cell.longitude, 9.0)
    }

    fun transform(view: MapView, centroid: Offset, pan: Offset, zoomChange: Float) {
        val newZoom = (view.zoom + log2(zoomChange.toDouble())).coerceIn(MIN_ZOOM, maxZoom)
        val (x, y) = zoomedCenter(view, centroid, newZoom, pan)
        zoom = newZoom
        centerX = x.coerceIn(0.0, 1.0)
        centerY = y.coerceIn(0.0, 1.0)
    }

    fun zoomAround(view: MapView, focus: Offset, delta: Double) {
        val newZoom = (view.zoom + delta).coerceIn(MIN_ZOOM, maxZoom)
        val (x, y) = zoomedCenter(view, focus, newZoom)
        zoom = newZoom
        centerX = x.coerceIn(0.0, 1.0)
        centerY = y.coerceIn(0.0, 1.0)
    }

    fun zoomBy(delta: Double) {
        zoom = (zoom + delta).coerceIn(MIN_ZOOM, maxZoom)
    }

    companion object {
        private const val PREF_LAYER = "weather_map_layer"
        private const val PREF_STATION = "weather_map_station"
        private const val PREF_OPACITY = "weather_map_opacity"
        private const val PREF_CELLS = "weather_map_cells"
        private const val PREF_WARNINGS = "weather_map_warnings"
        private const val PREF_OUTLOOK = "weather_map_outlook"
        private const val PREF_SITES = "weather_map_sites"
    }
}

// ---------------- The screen ----------------

@Composable
fun WeatherScreen(state: SentinelUiState, model: SentinelViewModel) {
    val context = LocalContext.current
    val wx: WeatherScreenModel = viewModel { WeatherScreenModel(model, context.applicationContext) }
    val lifecycleOwner = LocalLifecycleOwner.current
    val scope = rememberCoroutineScope()
    var fullscreen by rememberSaveable { mutableStateOf(false) }

    // The report every ten minutes and the radar every two - only while the tab is on screen and
    // the app is in front.
    LaunchedEffect(lifecycleOwner) {
        lifecycleOwner.repeatOnLifecycle(Lifecycle.State.STARTED) {
            var tick = 0
            while (true) {
                if (tick % (REPORT_REFRESH_MS / RADAR_REFRESH_MS).toInt() == 0) wx.load()
                wx.refreshRadar()
                tick++
                delay(RADAR_REFRESH_MS)
            }
        }
    }

    val is24Hour = android.text.format.DateFormat.is24HourFormat(context)
    val report = wx.report
    val clock = remember(report?.zone, is24Hour) { WeatherClock(report?.zone ?: ZoneId.systemDefault(), is24Hour) }
    val refresh: () -> Unit = {
        wx.tiles.forgetFailures()
        scope.launch {
            wx.load()
            wx.refreshRadar()
        }
    }

    BackHandler(enabled = fullscreen) { fullscreen = false }

    // The weather warning banner was tapped (see WeatherWarningBanner): the radar, full screen, as
    // soon as the map has loaded. A server without the map keeps the request until the tab is
    // left; the animated radar picture is already on the page.
    val openRadar by WeatherNavigation.openRadar.collectAsStateWithLifecycle()
    LaunchedEffect(openRadar, wx.mapInfo) {
        if (openRadar && wx.mapInfo != null) {
            WeatherNavigation.openRadar.value = false
            fullscreen = true
        }
    }
    DisposableEffect(Unit) {
        onDispose {
            WeatherNavigation.openRadar.value = false
            WeatherNavigation.radarShowing.value = false
        }
    }
    LaunchedEffect(fullscreen, wx.mapInfo) {
        WeatherNavigation.radarShowing.value = fullscreen && wx.mapInfo != null
    }

    // The body draws on the plain background rather than inside Surfaces, so the text colour is
    // set here for light and dark alike.
    CompositionLocalProvider(LocalContentColor provides MaterialTheme.colorScheme.onBackground) {
    Column(Modifier.fillMaxSize()) {
        ScreenHeader("Weather", state, onRefresh = refresh)
        when {
            report == null && wx.error == null -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                CircularProgressIndicator(color = SentinelGreen)
            }
            report == null -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                ErrorPanel("Could not load the weather - ${wx.error}", onRetry = refresh)
            }
            fullscreen && wx.mapInfo != null -> RadarMapPanel(
                wx,
                clock,
                fullscreen = true,
                onToggleFullscreen = { fullscreen = false },
                modifier = Modifier.fillMaxSize().padding(12.dp),
            )
            else -> WeatherBody(wx, report, clock, onFullscreen = { fullscreen = true })
        }
    }
    }
}

@Composable
private fun WeatherBody(wx: WeatherScreenModel, report: WeatherReport, clock: WeatherClock, onFullscreen: () -> Unit) {
    val scroll = rememberScrollState()
    val scope = rememberCoroutineScope()
    var mapTop by remember { mutableFloatStateOf(0f) }
    val focus: (WeatherStormCell) -> Unit = { cell ->
        wx.focusCell(cell)
        scope.launch { scroll.animateScrollTo(mapTop.roundToInt()) }
    }

    Column(Modifier.fillMaxSize()) {
        if (wx.loading) LinearProgressIndicator(Modifier.fillMaxWidth(), color = SentinelGreen)
        Column(
            Modifier.fillMaxSize().verticalScroll(scroll).padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            UpdatedLine(wx, report, clock)
            when (report.state) {
                WeatherState.UNCONFIGURED -> {
                    Notice(
                        "NO LOCATION SET",
                        report.message,
                        "The forecast comes from the US National Weather Service, so this tab works for places in " +
                            "the United States and its territories. An admin sets the location in the web dashboard, " +
                            "under Settings → LOCATION.",
                    )
                    return@Column
                }
                WeatherState.UNSUPPORTED -> {
                    Place(report)
                    Notice("NO FORECAST FOR THIS LOCATION", report.message, null)
                    return@Column
                }
                WeatherState.UNREACHABLE -> {
                    Notice(
                        "WEATHER.GOV UNREACHABLE",
                        report.message,
                        "The tab tries again on its own every ten minutes, or pull Refresh.",
                    )
                    return@Column
                }
            }
            Place(report)
            Problems(report.problems + wx.details?.problems.orEmpty())
            StormBanner(wx.storms, clock, focus)
            if (report.alerts.isNotEmpty()) Alerts(wx, report.alerts, clock)
            NowPanel(report, wx.details, clock)
            Box(Modifier.onGloballyPositioned { mapTop = it.positionInParent().y }) {
                when {
                    wx.mapInfo != null -> RadarMapPanel(wx, clock, fullscreen = false, onToggleFullscreen = onFullscreen)
                    wx.mapUnavailable -> RadarGifPanel(wx)
                    else -> Box(Modifier.fillMaxWidth().height(120.dp), contentAlignment = Alignment.Center) {
                        CircularProgressIndicator(color = SentinelGreen)
                    }
                }
            }
            StormsCard(wx.storms, focus)
            OutlookCard(wx.details, clock)
            TotalsCard(wx.details)
            if (report.hourly.isNotEmpty()) Hourly(wx, report.hourly, wx.details, clock)
            if (report.daily.isNotEmpty()) Daily(wx, report, clock)
            wx.details?.discussion?.takeIf { it.text.isNotBlank() }?.let { Discussion(wx, it, clock) }
            Footer(report, clock)
        }
    }
}

// ---------------- Pieces ----------------

@Composable
private fun SectionTitle(text: String, color: Color = Color.Unspecified) {
    Text(text, style = MaterialTheme.typography.labelLarge, fontWeight = FontWeight.Bold, color = color)
}

@Composable
internal fun DetailRow(label: String, value: String) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        Text(label, style = MaterialTheme.typography.bodySmall, color = SentinelTextDim, modifier = Modifier.width(118.dp))
        Text(value, style = MaterialTheme.typography.bodySmall, modifier = Modifier.weight(1f))
    }
}

@Composable
private fun Panel(content: @Composable () -> Unit) {
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) { content() }
    }
}

@Composable
private fun UpdatedLine(wx: WeatherScreenModel, report: WeatherReport, clock: WeatherClock) {
    val parts = listOfNotNull(
        report.fetchedAt?.let { "updated ${clock.clock(it)}" },
        if (report.stale) "showing the last answer weather.gov gave" else null,
        if (wx.refreshFailed) "refresh failed · showing the last answer" else null,
    )
    if (parts.isNotEmpty()) {
        Text(parts.joinToString(" · "), style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
    }
}

@Composable
private fun Notice(title: String, message: String?, extra: String?) {
    Panel {
        SectionTitle(title, SentinelAmber)
        message?.let { Text(it, style = MaterialTheme.typography.bodyMedium) }
        extra?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = SentinelTextDim) }
    }
}

@Composable
private fun Place(report: WeatherReport) {
    val location = report.location ?: return
    val parts = buildList {
        val nearest = location.nearest
        if (location.label.isNotBlank() && nearest.isNotBlank() && !nearest.equals(location.label, ignoreCase = true)) add("near $nearest")
        add(String.format(Locale.US, "%.4f, %.4f", location.latitude, location.longitude))
        location.forecastOffice?.let { add("forecast office $it") }
    }
    Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
        Text(location.displayName.uppercase(), style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Bold, color = SentinelGreen)
        Text(parts.joinToString(" · "), style = MaterialTheme.typography.bodySmall, color = SentinelTextDim)
    }
}

@Composable
private fun Problems(problems: List<String>) {
    if (problems.isEmpty()) return
    Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
        problems.forEach { Text("⚠ $it", style = MaterialTheme.typography.bodySmall, color = SentinelAmber) }
    }
}

// The banner that matters most on a stormy day: a tracked storm on course for home, or home
// inside a warning outline.
@Composable
private fun StormBanner(storms: WeatherStorms?, clock: WeatherClock, onCell: (WeatherStormCell) -> Unit) {
    val covering = WeatherLogic.coveringWarnings(storms)
    val incoming = WeatherLogic.incomingCells(storms).take(3)
    if (covering.isEmpty() && incoming.isEmpty()) return
    val color = if (WeatherLogic.bannerIsSevere(storms)) SentinelRed else SentinelAmber
    Row(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(8.dp))
            .background(color.copy(alpha = 0.14f))
            .border(1.dp, color, RoundedCornerShape(8.dp))
            .padding(12.dp),
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Text("⛈️", fontSize = 26.sp)
        Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
            for (warning in covering) {
                val tags = WeatherLogic.warningTags(warning)
                Text(
                    "Home is inside a ${warning.name}" +
                        (warning.expires?.let { " until ${clock.clock(it)}" } ?: "") +
                        (if (tags.isNotEmpty()) " · $tags" else ""),
                    fontWeight = FontWeight.Bold,
                    style = MaterialTheme.typography.bodyMedium,
                )
            }
            for (cell in incoming) {
                val hazards = WeatherLogic.cellHazards(cell)
                val passing = if ((cell.closestMiles ?: 0.0) < 1) "overhead" else String.format(Locale.US, "%.1f mi away", cell.closestMiles)
                Text(
                    "${WeatherLogic.cellTitle(cell)} heading this way · about ${cell.etaMinutes} min out, passing $passing · " +
                        "now ${(cell.distanceMiles ?: 0.0).roundToInt()} mi ${cell.bearingCompass.orEmpty()}" +
                        (if (hazards.isNotEmpty()) " · $hazards" else ""),
                    style = MaterialTheme.typography.bodyMedium,
                    modifier = Modifier.clickable { onCell(cell) },
                )
            }
        }
    }
}

@Composable
private fun Alerts(wx: WeatherScreenModel, alerts: List<WeatherAlert>, clock: WeatherClock) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        for (alert in alerts) {
            val color = when {
                alert.isSevere -> SentinelRed
                alert.isModerate -> SentinelAmber
                else -> SentinelCyan
            }
            val open = alert.id in wx.openAlerts
            Column(
                Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(8.dp))
                    .background(color.copy(alpha = if (alert.isSevere) 0.16f else 0.08f))
                    .border(1.dp, color.copy(alpha = 0.8f), RoundedCornerShape(8.dp))
                    .clickable { if (open) wx.openAlerts.remove(alert.id) else wx.openAlerts.add(alert.id) }
                    .padding(12.dp)
                    .animateContentSize(),
                verticalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(alert.event, color = color, fontWeight = FontWeight.Bold, modifier = Modifier.weight(1f))
                    alert.ends?.let { Text("until ${clock.weekdayClock(it)}", style = MaterialTheme.typography.labelSmall) }
                    Text(if (open) " ▾" else " ▸", color = color)
                }
                if (alert.headline.isNotBlank()) Text(alert.headline, style = MaterialTheme.typography.bodySmall)
                if (open) {
                    Text(alert.description, style = MaterialTheme.typography.bodySmall, fontFamily = FontFamily.Monospace)
                    if (alert.instruction.isNotBlank()) {
                        Text(alert.instruction, style = MaterialTheme.typography.bodySmall, fontFamily = FontFamily.Monospace, fontWeight = FontWeight.SemiBold)
                    }
                    val meta = listOf(alert.areaDescription, alert.sender).filter { it.isNotBlank() }.joinToString(" · ")
                    if (meta.isNotEmpty()) Text(meta, style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
                }
            }
        }
    }
}

@Composable
private fun NowPanel(report: WeatherReport, details: WeatherDetails?, clock: WeatherClock) {
    val current = report.current
    val next = WeatherLogic.nextPrecipText(report.hourly, clock::hour)
    Panel {
        SectionTitle("NOW")
        if (current == null) {
            Text("no current conditions", color = SentinelTextDim)
            next?.let { Text(it, style = MaterialTheme.typography.bodyMedium, color = SentinelCyan) }
            return@Panel
        }
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(14.dp)) {
            Text(WeatherLogic.glyph(current.icon, current.isDaytime), fontSize = 48.sp)
            Column {
                Text("${current.temperatureF}°F", style = MaterialTheme.typography.displaySmall, fontWeight = FontWeight.Bold)
                Text(current.description.ifBlank { "—" }, style = MaterialTheme.typography.bodyLarge)
                if (current.feelsLikeF != null && current.feelsLikeF != current.temperatureF) {
                    Text("feels like ${current.feelsLikeF}°", style = MaterialTheme.typography.bodyMedium, color = SentinelTextDim)
                }
                val today = report.daily.firstOrNull { it.isDaytime }
                val tonight = report.daily.firstOrNull { !it.isDaytime }
                val hiLo = listOfNotNull(
                    today?.temperatureF?.let { "H $it°" },
                    tonight?.temperatureF?.let { "L $it°" },
                ).joinToString("  ")
                if (hiLo.isNotEmpty()) Text(hiLo, style = MaterialTheme.typography.bodyMedium)
            }
        }
        next?.let { Text(it, style = MaterialTheme.typography.bodyMedium, color = SentinelCyan) }

        val pressure = details?.pressure
        val moon = WeatherLogic.moonPhase(Instant.now())
        val rows = listOfNotNull(
            current.humidityPercent?.let { "humidity" to "$it%" },
            current.dewpointF?.let { "dew point" to "$it°${WeatherLogic.dewComfort(it)}" },
            WeatherLogic.windText(current)?.let { "wind" to it },
            current.windGustMph?.let { "gusts" to "$it mph" },
            current.pressureInHg?.let {
                "pressure" to String.format(Locale.US, "%.2f inHg", it) +
                    (pressure?.let { p -> " ${WeatherLogic.pressureArrow(p.trend)} ${p.trend}" } ?: "")
            },
            current.visibilityMiles?.let { "visibility" to "${trim(it)} mi" },
            report.sun?.sunrise?.let { "sunrise" to clock.clock(it) },
            report.sun?.sunset?.let { "sunset" to clock.clock(it) },
            WeatherLogic.daylightText(report.sun)?.let { "daylight" to it },
            "moon" to "${moon.glyph} ${moon.name}, ${moon.illumination}% lit",
        )
        Spacer(Modifier.height(2.dp))
        rows.forEach { (label, value) -> DetailRow(label, value) }
        Text(
            "${current.station} · ${current.stationName}" + (current.observedAt?.let { " · observed ${clock.weekdayClock(it)}" } ?: ""),
            style = MaterialTheme.typography.labelSmall,
            color = SentinelTextDim,
        )
    }
}

private fun trim(value: Double): String =
    if (value == Math.floor(value)) value.toLong().toString() else String.format(Locale.US, "%.1f", value)

@Composable
private fun StormsCard(storms: WeatherStorms?, onCell: (WeatherStormCell) -> Unit) {
    val cells = WeatherLogic.nearbyCells(storms)
    Panel {
        SectionTitle("STORMS NEAR HOME")
        if (storms == null) {
            Text("loading…", style = MaterialTheme.typography.bodySmall, color = SentinelTextDim)
            return@Panel
        }
        if (cells.isEmpty()) {
            Text("No storms tracked within 100 miles.", style = MaterialTheme.typography.bodySmall, color = SentinelTextDim)
            return@Panel
        }
        for (cell in cells) {
            val motion = cell.speedMph?.takeIf { it > 0 }?.let { "moving ${cell.headingCompass.orEmpty()} $it mph" } ?: "nearly stationary"
            val hazards = WeatherLogic.cellHazards(cell)
            val color = when {
                cell.threat >= 3 -> SentinelRed
                cell.threat >= 1 -> SentinelAmber
                else -> SentinelCyan
            }
            Row(
                Modifier.fillMaxWidth().clickable { onCell(cell) }.padding(vertical = 4.dp),
                horizontalArrangement = Arrangement.spacedBy(10.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Box(Modifier.size(10.dp).clip(RoundedCornerShape(5.dp)).background(color))
                Column(Modifier.weight(1f)) {
                    Text(
                        WeatherLogic.cellTitle(cell) + if (cell.towardHome) "  ~${cell.etaMinutes} min" else "",
                        style = MaterialTheme.typography.bodyMedium,
                        fontWeight = FontWeight.SemiBold,
                        color = if (cell.towardHome) SentinelRed else Color.Unspecified,
                    )
                    Text(
                        listOfNotNull(
                            "${(cell.distanceMiles ?: 0.0).roundToInt()} mi ${cell.bearingCompass.orEmpty()}",
                            motion,
                            cell.topKft?.let { "tops ${it.roundToInt()}k ft" },
                            hazards.takeIf { it.isNotEmpty() },
                        ).joinToString(" · "),
                        style = MaterialTheme.typography.bodySmall,
                        color = SentinelTextDim,
                    )
                }
            }
        }
        Text("Tap a storm to show it on the map.", style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
    }
}

@Composable
private fun OutlookCard(details: WeatherDetails?, clock: WeatherClock) {
    val severe = details?.severe
    Panel {
        SectionTitle("SEVERE OUTLOOK")
        if (severe == null) {
            Text(
                if (details != null) "The Storm Prediction Center's outlook is unavailable right now." else "loading…",
                style = MaterialTheme.typography.bodySmall,
                color = SentinelTextDim,
            )
            return@Panel
        }
        val today = clock.today()
        for (day in severe.days) {
            val name = when (day.day) {
                1 -> "Today"
                2 -> "Tomorrow"
                else -> clock.longWeekday(today.plusDays((day.day - 1).toLong()))
            }
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                RiskChip(day.category)
                Text(name, style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.SemiBold, modifier = Modifier.width(92.dp))
                Text(WeatherLogic.outlookScale(day.category), style = MaterialTheme.typography.bodySmall, color = SentinelTextDim)
            }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(16.dp), modifier = Modifier.padding(top = 4.dp)) {
            Hazard("tornado", severe.tornadoPercent, severe.significantTornado)
            Hazard("hail", severe.hailPercent, severe.significantHail)
            Hazard("wind", severe.windPercent, severe.significantWind)
        }
        severe.issuedAt?.let {
            Text("SPC day 1 issued ${clock.weekdayClock(it)}", style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
        }
    }
}

// The SPC's own category colours.
private fun riskColor(category: String?): Color = when (category) {
    "TSTM" -> Color(0xFFC0E8C0)
    "MRGL" -> Color(0xFF7FC57F)
    "SLGT" -> Color(0xFFF6F67F)
    "ENH" -> Color(0xFFE6C27F)
    "MDT" -> Color(0xFFE67F7F)
    "HIGH" -> Color(0xFFFF7FFF)
    else -> Color(0xFF8A9A92)
}

@Composable
private fun RiskChip(category: String?) {
    Box(
        Modifier
            .width(52.dp)
            .clip(RoundedCornerShape(4.dp))
            .background(riskColor(category))
            .padding(vertical = 3.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(category ?: "NONE", color = Color.Black, style = MaterialTheme.typography.labelSmall, fontWeight = FontWeight.Bold)
    }
}

@Composable
private fun Hazard(label: String, percent: Int?, significant: Boolean) {
    Column {
        Text(label, style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(4.dp)) {
            Text("${percent ?: 0}%", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Bold)
            if (significant) Text("sig", style = MaterialTheme.typography.labelSmall, color = SentinelRed, fontWeight = FontWeight.Bold)
        }
    }
}

@Composable
private fun TotalsCard(details: WeatherDetails?) {
    val totals = details?.totals
    Panel {
        SectionTitle("RAIN & SNOW AHEAD")
        if (totals == null) {
            Text(
                if (details != null) "Precipitation totals are unavailable right now." else "loading…",
                style = MaterialTheme.typography.bodySmall,
                color = SentinelTextDim,
            )
            return@Panel
        }
        val snow = totals.snowNext7DaysInches > 0
        listOfNotNull(
            "rain, next 24 h" to WeatherLogic.inches(totals.rainNext24HoursInches),
            "rain, next 7 days" to WeatherLogic.inches(totals.rainNext7DaysInches),
            if (snow) "snow, next 24 h" to WeatherLogic.inches(totals.snowNext24HoursInches, 1) else null,
            if (snow) "snow, next 7 days" to WeatherLogic.inches(totals.snowNext7DaysInches, 1) else null,
            if (totals.iceNext7DaysInches > 0) "ice, next 7 days" to WeatherLogic.inches(totals.iceNext7DaysInches) else null,
            "thunder, next 24 h" to "${totals.thunderChanceNext24Hours}%",
            "thunder, next 7 days" to "${totals.thunderChanceNext7Days}%",
        ).forEach { (label, value) -> DetailRow(label, value) }
    }
}

// ---------------- Hourly ----------------

private val HOUR_STEP = 56.dp

@Composable
private fun Hourly(wx: WeatherScreenModel, hours: List<WeatherHour>, details: WeatherDetails?, clock: WeatherClock) {
    val extras = remember(details) { details?.hours.orEmpty().associateBy { it.startTime } }
    val tempColor = SentinelAmber
    val feelsColor = SentinelCyan
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        SectionTitle("NEXT ${hours.size} HOURS")
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            LegendKey(tempColor, "temperature")
            LegendKey(feelsColor, "feels like")
            LegendKey(Color(0xFF4DA3FF), "chance of rain")
        }
        Column(Modifier.horizontalScroll(rememberScrollState())) {
            HourlyChart(hours, extras.mapValues { it.value.feelsLikeF }, clock, tempColor, feelsColor)
            Row {
                var lastDay: LocalDate? = null
                for (hour in hours) {
                    val day = hour.startTime.atZone(clock.zone).toLocalDate()
                    val newDay = day != lastDay
                    lastDay = day
                    val selected = wx.selectedHour == hour.startTime
                    Column(
                        Modifier
                            .width(HOUR_STEP)
                            .clip(RoundedCornerShape(6.dp))
                            .background(if (selected) SentinelGreen.copy(alpha = 0.15f) else Color.Transparent)
                            .clickable { wx.selectedHour = if (selected) null else hour.startTime }
                            .padding(vertical = 4.dp),
                        horizontalAlignment = Alignment.CenterHorizontally,
                        verticalArrangement = Arrangement.spacedBy(2.dp),
                    ) {
                        Text(if (newDay) clock.weekday(hour.startTime) else " ", style = MaterialTheme.typography.labelSmall, color = SentinelGreen, fontWeight = FontWeight.Bold)
                        Text(clock.hour(hour.startTime), style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
                        Text(WeatherLogic.glyph(hour.icon, hour.isDaytime), fontSize = 20.sp)
                        Text(hour.temperatureF?.let { "$it°" } ?: "—", style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.SemiBold)
                        Text(
                            hour.precipitationChance?.takeIf { it > 0 }?.let { "💧$it%" } ?: " ",
                            style = MaterialTheme.typography.labelSmall,
                            color = Color(0xFF4DA3FF),
                        )
                        Text(
                            listOfNotNull(hour.windSpeed?.replace(" mph", ""), hour.windDirection).joinToString(" "),
                            style = MaterialTheme.typography.labelSmall,
                            color = SentinelTextDim,
                            textAlign = TextAlign.Center,
                            maxLines = 1,
                        )
                    }
                }
            }
        }
        // The dashboard's hover tip, for a tapped hour.
        hours.firstOrNull { it.startTime == wx.selectedHour }?.let { hour ->
            val extra = extras[hour.startTime]
            val wind = listOfNotNull(hour.windDirection, hour.windSpeed).joinToString(" ")
            Text(
                "${clock.weekday(hour.startTime)} ${clock.hour(hour.startTime)}: " + listOfNotNull(
                    hour.shortForecast,
                    wind.takeIf { it.isNotEmpty() }?.let { "wind $it" },
                    extra?.gustMph?.takeIf { it > 0 }?.let { "gusts $it mph" },
                    hour.humidityPercent?.let { "humidity $it%" },
                    extra?.skyCoverPercent?.let { "cloud $it%" },
                    extra?.rainInches?.takeIf { it > 0 }?.let { "rain ${WeatherLogic.inches(it)}" },
                    extra?.snowInches?.takeIf { it > 0 }?.let { "snow ${WeatherLogic.inches(it, 1)}" },
                ).joinToString(" · "),
                style = MaterialTheme.typography.bodySmall,
            )
        }
    }
}

@Composable
private fun LegendKey(color: Color, label: String) {
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(4.dp)) {
        Box(Modifier.size(width = 12.dp, height = 3.dp).background(color))
        Text(label, style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
    }
}

@Composable
private fun HourlyChart(
    hours: List<WeatherHour>,
    feelsLike: Map<Instant, Int?>,
    clock: WeatherClock,
    tempColor: Color,
    feelsColor: Color,
) {
    val temps = hours.mapNotNull { it.temperatureF }
    if (temps.size < 2) return
    val feels = hours.map { feelsLike[it.startTime] }
    val measurer = rememberTextMeasurer()
    val labelColor = MaterialTheme.colorScheme.onSurface
    val dimColor = SentinelTextDim
    val dayColor = SentinelGreen
    Canvas(Modifier.width(HOUR_STEP * hours.size).height(130.dp)) {
        val step = HOUR_STEP.toPx()
        val top = 24.dp.toPx()
        val tempBottom = 82.dp.toPx()
        val barBottom = size.height - 4.dp.toPx()
        val all = temps + feels.filterNotNull()
        val min = all.min()
        val span = maxOf(all.max() - min, 1).toFloat()
        fun y(t: Int) = tempBottom - (t - min) / span * (tempBottom - top)
        fun x(i: Int) = i * step + step / 2

        drawLine(dimColor.copy(alpha = 0.4f), Offset(0f, barBottom), Offset(size.width, barBottom), 1.dp.toPx())
        val labelStyle = TextStyle(fontSize = 10.sp, color = Color(0xFF4DA3FF))
        hours.forEachIndexed { i, hour ->
            val chance = hour.precipitationChance ?: 0
            if (chance > 0) {
                val h = chance / 100f * 34.dp.toPx()
                drawRect(Color(0xFF4DA3FF).copy(alpha = 0.45f), Offset(x(i) - step * 0.35f, barBottom - h), androidx.compose.ui.geometry.Size(step * 0.7f, h))
                if (chance >= 20) {
                    val text = measurer.measure("$chance", labelStyle)
                    drawText(text, topLeft = Offset(x(i) - text.size.width / 2f, barBottom - h - text.size.height))
                }
            }
        }

        var lastDay: LocalDate? = null
        hours.forEachIndexed { i, hour ->
            val day = hour.startTime.atZone(clock.zone).toLocalDate()
            if (lastDay != null && day != lastDay) {
                drawLine(dayColor.copy(alpha = 0.5f), Offset(i * step, 4.dp.toPx()), Offset(i * step, barBottom), 1.dp.toPx(),
                    pathEffect = PathEffect.dashPathEffect(floatArrayOf(4.dp.toPx(), 3.dp.toPx())))
                val text = measurer.measure(clock.weekday(hour.startTime).uppercase(), TextStyle(fontSize = 10.sp, color = dayColor, fontWeight = FontWeight.Bold))
                drawText(text, topLeft = Offset(i * step + 4.dp.toPx(), 2.dp.toPx()))
            }
            lastDay = day
        }

        fun line(values: List<Int?>, color: Color, dashed: Boolean) {
            val path = Path()
            var started = false
            values.forEachIndexed { i, v ->
                if (v == null) return@forEachIndexed
                if (!started) path.moveTo(x(i), y(v)) else path.lineTo(x(i), y(v))
                started = true
            }
            drawPath(
                path,
                color,
                style = Stroke(
                    width = (if (dashed) 1.5 else 2.5).dp.toPx(),
                    pathEffect = if (dashed) PathEffect.dashPathEffect(floatArrayOf(5.dp.toPx(), 4.dp.toPx())) else null,
                ),
            )
        }
        if (feels.withIndex().any { (i, f) -> f != null && f != hours[i].temperatureF }) line(feels, feelsColor, dashed = true)
        line(hours.map { it.temperatureF }, tempColor, dashed = false)

        val tempStyle = TextStyle(fontSize = 10.sp, color = labelColor, fontWeight = FontWeight.SemiBold)
        hours.forEachIndexed { i, hour ->
            val t = hour.temperatureF ?: return@forEachIndexed
            if (i % 3 != 0) return@forEachIndexed
            val text = measurer.measure("$t°", tempStyle)
            drawText(text, topLeft = Offset(x(i) - text.size.width / 2f, y(t) - text.size.height - 3.dp.toPx()))
        }
    }
}

// ---------------- Seven days ----------------

@Composable
private fun Daily(wx: WeatherScreenModel, report: WeatherReport, clock: WeatherClock) {
    val days = remember(report.daily, clock.zone) { WeatherLogic.groupDays(report.daily, clock.zone) }
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        SectionTitle("${days.size}-DAY FORECAST")
        for (day in days) DayCard(wx, day, report)
    }
}

@Composable
private fun DayCard(wx: WeatherScreenModel, day: WeatherDay, report: WeatherReport) {
    val open = day.date in wx.openDays
    val lead = day.lead
    Card(
        Modifier
            .fillMaxWidth()
            .clickable { if (open) wx.openDays.remove(day.date) else wx.openDays.add(day.date) },
    ) {
        Column(Modifier.padding(12.dp).animateContentSize(), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                Text(WeatherLogic.glyph(lead.icon, lead.isDaytime), fontSize = 28.sp)
                Column(Modifier.weight(1f)) {
                    Text(lead.name, style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.Bold)
                    Text(lead.shortForecast, style = MaterialTheme.typography.bodySmall)
                    val wind = listOfNotNull(lead.windDirection, lead.windSpeed).joinToString(" ")
                    val meta = listOfNotNull(
                        day.chance.takeIf { it > 0 }?.let { "💧 $it%" },
                        wind.takeIf { it.isNotEmpty() },
                    ).joinToString(" · ")
                    if (meta.isNotEmpty()) Text(meta, style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
                }
                Column(horizontalAlignment = Alignment.End) {
                    Row {
                        day.high?.let { Text("$it°", fontWeight = FontWeight.Bold, color = SentinelAmber) }
                        if (day.high != null && day.low != null) Text(" / ", color = SentinelTextDim)
                        day.low?.let { Text("$it°", color = SentinelCyan) }
                        if (day.high == null && day.low == null) Text("—")
                    }
                    WeatherLogic.rangeFractions(day, report.daily)?.let { (start, width) ->
                        Box(
                            Modifier
                                .padding(top = 4.dp)
                                .width(84.dp)
                                .height(5.dp)
                                .clip(RoundedCornerShape(3.dp))
                                .background(SentinelTextDim.copy(alpha = 0.25f)),
                        ) {
                            Row(Modifier.fillMaxHeight()) {
                                if (start > 0f) Spacer(Modifier.weight(start))
                                Box(Modifier.weight(width).fillMaxHeight().background(SentinelAmber))
                                val rest = 1f - start - width
                                if (rest > 0.001f) Spacer(Modifier.weight(rest))
                            }
                        }
                    }
                }
            }
            if (open) {
                listOfNotNull(day.day, day.night).forEach { period ->
                    Text(
                        "${period.name}. ${period.detailedForecast}",
                        style = MaterialTheme.typography.bodySmall,
                    )
                }
            }
        }
    }
}

@Composable
private fun Discussion(wx: WeatherScreenModel, discussion: com.sentinelcore.android.data.WeatherDiscussion, clock: WeatherClock) {
    Panel {
        Row(
            Modifier.fillMaxWidth().clickable { wx.discussionOpen = !wx.discussionOpen },
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                "FORECASTER DISCUSSION · ${discussion.office}" + (discussion.issuedAt?.let { " · issued ${clock.weekdayClock(it)}" } ?: ""),
                style = MaterialTheme.typography.labelLarge,
                fontWeight = FontWeight.Bold,
                modifier = Modifier.weight(1f),
            )
            Text(if (wx.discussionOpen) "▾" else "▸")
        }
        if (!wx.discussionOpen) {
            Text(
                "The forecasters' own notes on why the forecast says what it does - how sure they are and what they're watching. Tap to read.",
                style = MaterialTheme.typography.bodySmall,
                color = SentinelTextDim,
            )
        } else {
            Box(Modifier.horizontalScroll(rememberScrollState())) {
                Text(discussion.text.trim(), style = MaterialTheme.typography.bodySmall, fontFamily = FontFamily.Monospace, softWrap = false)
            }
        }
    }
}

@Composable
private fun Footer(report: WeatherReport, clock: WeatherClock) {
    val issued = report.forecastUpdatedAt?.let { ", forecast issued ${clock.weekdayClock(it)}" }.orEmpty()
    val zone = report.location?.timeZone?.let { " Times are shown in ${it.replace('_', ' ')}." }.orEmpty()
    Text(
        "Forecast, observations, radar and discussion from the US National Weather Service (weather.gov)$issued. " +
            "Severe outlook from the NOAA Storm Prediction Center; storm tracks and warning outlines via the Iowa " +
            "Environmental Mesonet; basemap © Esri, HERE, Garmin and OpenStreetMap contributors.$zone",
        style = MaterialTheme.typography.labelSmall,
        color = SentinelTextDim,
    )
}
