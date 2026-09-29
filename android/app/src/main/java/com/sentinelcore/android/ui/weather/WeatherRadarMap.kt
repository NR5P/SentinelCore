package com.sentinelcore.android.ui.weather

import android.graphics.BitmapFactory
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.detectTransformGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Fullscreen
import androidx.compose.material.icons.filled.FullscreenExit
import androidx.compose.material.icons.filled.Home
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Remove
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Slider
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.FilterQuality
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.PathFillType
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.rotate
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.TextMeasurer
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.drawText
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.sentinelcore.android.data.GeoPoint
import com.sentinelcore.android.data.GeoPolygons
import com.sentinelcore.android.data.Mercator
import com.sentinelcore.android.data.WeatherLogic
import com.sentinelcore.android.data.WeatherMapLayer
import com.sentinelcore.android.data.WeatherStormCell
import com.sentinelcore.android.ui.theme.SentinelTextDim
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.debounce
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.withContext
import java.time.Duration
import java.time.Instant
import kotlin.math.ceil
import kotlin.math.floor
import kotlin.math.hypot
import kotlin.math.ln
import kotlin.math.pow
import kotlin.math.roundToInt

// The radar map: a small slippy map drawn on a Compose Canvas from the server's own tile endpoints
// (the basemap, its labels, and a radar layer with its frame loop), with the storm cells, warning
// outlines, the SPC outlook and the radar sites over it - the dashboard's Leaflet map, natively.
// Tiles come through the app's own HTTP stack, so they carry the session cookie like every other
// request; a WebView would need the session handed across and its own certificate handling.

internal const val FRAME_INTERVAL_MS = 550L
internal const val LAST_FRAME_HOLD_MS = 1800L
internal const val MIN_ZOOM = 3.0

// A viewport: the world point at the centre (Mercator units, 0..1), the zoom, and the canvas size.
internal data class MapView(
    val centerX: Double,
    val centerY: Double,
    val zoom: Double,
    val width: Float,
    val height: Float,
    val tilePx: Float,
) {
    val worldPx: Double = tilePx * 2.0.pow(zoom)

    fun screenX(x: Double): Float = ((x - centerX) * worldPx + width / 2).toFloat()
    fun screenY(y: Double): Float = ((y - centerY) * worldPx + height / 2).toFloat()
    fun screen(point: GeoPoint): Offset = Offset(screenX(Mercator.x(point.longitude)), screenY(Mercator.y(point.latitude)))
    fun screen(latitude: Double, longitude: Double): Offset = Offset(screenX(Mercator.x(longitude)), screenY(Mercator.y(latitude)))

    fun geo(offset: Offset): GeoPoint = GeoPoint(
        latitude = Mercator.latitude(centerY + (offset.y - height / 2) / worldPx),
        longitude = Mercator.longitude(centerX + (offset.x - width / 2) / worldPx),
    )

    // west, south, east, north, padded by `pad` of the view's size each side.
    fun bounds(pad: Double = 0.0): DoubleArray {
        val halfW = width / 2 / worldPx * (1 + 2 * pad)
        val halfH = height / 2 / worldPx * (1 + 2 * pad)
        return doubleArrayOf(
            Mercator.longitude(centerX - halfW).coerceAtLeast(-180.0),
            Mercator.latitude((centerY + halfH).coerceAtMost(1.0)),
            Mercator.longitude(centerX + halfW).coerceAtMost(180.0),
            Mercator.latitude((centerY - halfH).coerceAtLeast(0.0)),
        )
    }

    // The tiles at zoom `z` that cover the canvas, centre-out so the middle loads first.
    fun tiles(z: Int): List<Pair<Int, Int>> {
        val n = 1 shl z
        val halfW = width / 2 / worldPx
        val halfH = height / 2 / worldPx
        val x0 = floor((centerX - halfW) * n).toInt()
        val x1 = floor((centerX + halfW) * n).toInt()
        val y0 = floor((centerY - halfH) * n).toInt().coerceAtLeast(0)
        val y1 = floor((centerY + halfH) * n).toInt().coerceAtMost(n - 1)
        val cx = centerX * n
        val cy = centerY * n
        val list = ArrayList<Pair<Int, Int>>()
        for (x in x0..x1) for (y in y0..y1) list.add(x to y)
        return list.sortedBy { (x, y) -> hypot(x + 0.5 - cx, y + 0.5 - cy) }
    }

    companion object {
        fun tileZoom(zoom: Double, maxNative: Int): Int = zoom.roundToInt().coerceIn(0, maxNative)
    }
}

// Decoded tiles, kept in memory and fetched a few at a time. Only tiles still wanted by the time
// their turn comes are fetched, so a fast pan across the country doesn't queue up every tile it
// passed over. Lives on the screen's model, so going to another tab and back keeps what loaded.
internal class WeatherTileStore(
    private val scope: CoroutineScope,
    private val fetch: suspend (String) -> ByteArray?,
) {
    private val tiles = androidx.compose.runtime.mutableStateMapOf<String, ImageBitmap>()
    private val recency = LinkedHashMap<String, Unit>(64, 0.75f, true)
    private val inFlight = HashSet<String>()
    private val failedAt = HashMap<String, Long>()
    private var wanted: Set<String> = emptySet()
    private val gate = Semaphore(6)

    fun get(path: String): ImageBitmap? = tiles[path]

    // Replaces the wanted list (in priority order) and starts fetching whatever isn't loaded.
    fun want(paths: List<String>) {
        wanted = paths.toHashSet()
        val now = System.currentTimeMillis()
        for (path in paths) {
            if (tiles.containsKey(path)) {
                recency[path] = Unit
                continue
            }
            if (path in inFlight) continue
            val failed = failedAt[path]
            if (failed != null && now - failed < 60_000) continue
            inFlight.add(path)
            scope.launch {
                try {
                    val image = gate.withPermit {
                        if (path !in wanted) return@withPermit null
                        val bytes = fetch(path) ?: return@withPermit null
                        withContext(Dispatchers.Default) {
                            BitmapFactory.decodeByteArray(bytes, 0, bytes.size)?.asImageBitmap()
                        }
                    }
                    if (image != null) put(path, image) else if (path in wanted) failedAt[path] = System.currentTimeMillis()
                } finally {
                    inFlight.remove(path)
                }
            }
        }
    }

    private fun put(path: String, image: ImageBitmap) {
        tiles[path] = image
        recency[path] = Unit
        failedAt.remove(path)
        if (recency.size > MAX_TILES) {
            val iterator = recency.keys.iterator()
            while (recency.size > MAX_TILES && iterator.hasNext()) {
                val key = iterator.next()
                if (key in wanted) continue
                iterator.remove()
                tiles.remove(key)
            }
        }
    }

    fun forgetFailures() = failedAt.clear()

    companion object {
        // A twelve-frame loop over a phone screen of tiles, plus the basemap and labels.
        const val MAX_TILES = 240
    }
}

internal sealed interface MapSelection {
    data class Cell(val id: String) : MapSelection
    data class Warning(val id: String) : MapSelection
    data class Site(val id: String) : MapSelection
    data class Outlook(val label: String) : MapSelection
}

// ---------------- The panel ----------------

@Composable
internal fun RadarMapPanel(
    wx: WeatherScreenModel,
    clock: WeatherClock,
    fullscreen: Boolean,
    onToggleFullscreen: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val info = wx.mapInfo ?: return
    val layer = WeatherLogic.currentLayer(info, wx.prefs.layer)
    val frames = layer?.times.orEmpty().ifEmpty { listOf(null) }
    LaunchedEffect(wx.playing, frames.size) {
        if (wx.playing) runLoop(wx, frames.size)
    }

    Column(modifier, verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(
            if (layer == null) "RADAR"
            else "RADAR · " + (if (layer.isStation) "${info.station} " else "") + layer.name.uppercase(),
            style = MaterialTheme.typography.labelLarge,
            fontWeight = FontWeight.Bold,
        )
        Box(
            Modifier
                .fillMaxWidth()
                .then(if (fullscreen) Modifier.weight(1f) else Modifier.heightIn(min = 340.dp, max = 340.dp))
                .clip(RoundedCornerShape(8.dp)),
        ) {
            RadarCanvas(wx, layer, frames, Modifier.fillMaxSize())
            // Frame time, top right.
            val time = frames.getOrNull(wx.frameIndex)
            Surface(
                color = Color.Black.copy(alpha = 0.6f),
                shape = RoundedCornerShape(6.dp),
                modifier = Modifier.align(Alignment.TopEnd).padding(8.dp),
            ) {
                Text(
                    frameLabel(time, clock),
                    color = if (wx.frameIndex < frames.size - 1) Color(0xFFFFC400) else Color.White,
                    style = MaterialTheme.typography.labelMedium,
                    modifier = Modifier.padding(horizontal = 8.dp, vertical = 4.dp),
                )
            }
            Column(
                Modifier.align(Alignment.TopStart).padding(8.dp),
                verticalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                MapButton(Icons.Default.Add, "Zoom in") { wx.zoomBy(1.0) }
                MapButton(Icons.Default.Remove, "Zoom out") { wx.zoomBy(-1.0) }
                MapButton(Icons.Default.Home, "Back to home") { wx.goHome() }
                MapButton(
                    if (fullscreen) Icons.Default.FullscreenExit else Icons.Default.Fullscreen,
                    if (fullscreen) "Exit full screen" else "Full screen",
                    onToggleFullscreen,
                )
            }
        }

        // Frame playback.
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            IconButton(onClick = { wx.togglePlay(frames.size) }, enabled = frames.size > 1) {
                Icon(
                    if (wx.playing) Icons.Default.Pause else Icons.Default.PlayArrow,
                    contentDescription = if (wx.playing) "Pause the radar loop" else "Play the radar loop",
                )
            }
            Slider(
                value = wx.frameIndex.toFloat(),
                onValueChange = { wx.stopPlaying(); wx.frameIndex = it.roundToInt().coerceIn(0, frames.size - 1) },
                valueRange = 0f..(frames.size - 1).coerceAtLeast(1).toFloat(),
                steps = (frames.size - 2).coerceAtLeast(0),
                enabled = frames.size > 1,
                modifier = Modifier.weight(1f),
            )
        }
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("opacity", style = MaterialTheme.typography.labelMedium, color = SentinelTextDim)
            Slider(
                value = wx.prefs.opacity,
                onValueChange = { wx.updatePrefs { copy(opacity = it) } },
                valueRange = 0.2f..1f,
                modifier = Modifier.weight(1f),
            )
        }

        if (!fullscreen) {
            MapSelectionCard(wx, clock)
            LayerPicker(wx, info.layers, layer, info.station)
            StationPicker(wx)
            OverlayToggles(wx)
            layer?.let { Legend(wx, it, info.station) }
        } else {
            MapSelectionCard(wx, clock)
        }
    }
}

private fun frameLabel(time: String?, clock: WeatherClock): String {
    if (time == null) return "latest"
    val instant = runCatching { Instant.parse(time) }.getOrNull() ?: return time
    val minutesAgo = Duration.between(instant, Instant.now()).toMinutes()
    return clock.clock(instant) + " · " + if (minutesAgo <= 1) "just now" else "$minutesAgo min ago"
}

@Composable
private fun MapButton(icon: androidx.compose.ui.graphics.vector.ImageVector, label: String, onClick: () -> Unit) {
    Surface(
        color = MaterialTheme.colorScheme.surface.copy(alpha = 0.92f),
        shape = RoundedCornerShape(6.dp),
        shadowElevation = 2.dp,
        modifier = Modifier.size(36.dp).clickable(onClick = onClick),
    ) {
        Box(contentAlignment = Alignment.Center) { Icon(icon, contentDescription = label, modifier = Modifier.size(20.dp)) }
    }
}

@Composable
private fun LayerPicker(wx: WeatherScreenModel, layers: List<WeatherMapLayer>, current: WeatherMapLayer?, station: String?) {
    Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
        for ((scope, title) in listOf("national" to "NATIONAL", "station" to (station ?: "RADAR"))) {
            val group = layers.filter { it.scope == scope }
            if (group.isEmpty()) continue
            Text(title, style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
            Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                for (layer in group) {
                    FilterChip(
                        selected = layer.id == current?.id,
                        onClick = { wx.selectLayer(layer.id) },
                        enabled = !layer.isStation || station != null,
                        label = { Text(layer.name) },
                    )
                }
            }
        }
    }
}

@Composable
private fun StationPicker(wx: WeatherScreenModel) {
    val info = wx.mapInfo ?: return
    if (info.stations.isEmpty()) return
    var open by remember { mutableStateOf(false) }
    val home = info.home?.let { GeoPoint(it.latitude, it.longitude) }
    val sites = remember(info.stations, home) {
        info.stations
            .map { it to (home?.let { h -> WeatherLogic.milesBetween(h, GeoPoint(it.latitude, it.longitude)) }) }
            .sortedBy { it.second ?: 0.0 }
    }
    val selected = sites.firstOrNull { it.first.id == info.station }
    Box {
        OutlinedButton(onClick = { open = true }) {
            Text(
                "Radar: " + (selected?.let { siteLabel(it.first.id, it.first.name, it.second) } ?: "choose"),
                maxLines = 1,
            )
        }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            for ((site, miles) in sites.take(40)) {
                DropdownMenuItem(
                    text = { Text(siteLabel(site.id, site.name, miles)) },
                    onClick = { open = false; wx.selectStation(site.id) },
                )
            }
        }
    }
}

private fun siteLabel(id: String, name: String, miles: Double?): String =
    "$id · $name" + (miles?.let { " (${it.roundToInt()} mi)" } ?: "")

@Composable
private fun OverlayToggles(wx: WeatherScreenModel) {
    val prefs = wx.prefs
    Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        FilterChip(prefs.cells, { wx.updatePrefs { copy(cells = !cells) } }, label = { Text("Storm tracks") })
        FilterChip(prefs.warnings, { wx.updatePrefs { copy(warnings = !warnings) } }, label = { Text("Warnings") })
        FilterChip(prefs.outlook, { wx.updatePrefs { copy(outlook = !outlook) } }, label = { Text("SPC outlook") })
        FilterChip(prefs.sites, { wx.updatePrefs { copy(sites = !sites) } }, label = { Text("Radar sites") })
    }
}

@Composable
private fun Legend(wx: WeatherScreenModel, layer: WeatherMapLayer, station: String?) {
    val path = WeatherLogic.legendPath(layer, station)
    val legend by produceState<ImageBitmap?>(null, path) {
        value = wx.legend(path)
    }
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        // One legend pixel per dp: NOAA's keys are drawn for a desktop browser, and at one pixel
        // per phone pixel their numbers are too small to read.
        legend?.let {
            Image(
                it,
                contentDescription = "${layer.name} colour key",
                contentScale = ContentScale.Fit,
                modifier = Modifier
                    .size(it.width.dp, it.height.dp)
                    .heightIn(max = 240.dp),
            )
        }
        Text(
            layer.description + " " +
                (if (layer.isStation) "From radar $station; it sees about 150 miles out." else "Updated every few minutes.") +
                " Pinch to zoom, drag to pan, tap a storm or warning for details.",
            style = MaterialTheme.typography.bodySmall,
            color = SentinelTextDim,
        )
    }
}

// What a tap on the map picked, under the map (the dashboard shows these as popups).
@Composable
private fun MapSelectionCard(wx: WeatherScreenModel, clock: WeatherClock) {
    val selection = wx.selection ?: return
    val storms = wx.storms
    val rows: List<Pair<String, String>>
    val title: String
    var titleColor: Color? = null
    var action: (@Composable () -> Unit)? = null
    when (selection) {
        is MapSelection.Cell -> {
            val cell = storms?.cells?.firstOrNull { it.id == selection.id } ?: return
            title = WeatherLogic.cellTitle(cell)
            rows = cellRows(cell, clock)
        }
        is MapSelection.Warning -> {
            val warning = storms?.warnings?.firstOrNull { it.id == selection.id } ?: return
            title = warning.name
            titleColor = Color(WeatherLogic.warningColor(warning.phenomena))
            val badges = listOfNotNull(
                if (warning.emergency) "EMERGENCY" else null,
                if (warning.particularlyDangerous) "PARTICULARLY DANGEROUS SITUATION" else null,
            )
            rows = listOfNotNull(
                if (badges.isNotEmpty()) "note" to badges.joinToString(" · ") else null,
                "in effect" to "${warning.issued?.let(clock::clock).orEmpty()} - ${warning.expires?.let(clock::clock).orEmpty()}",
                WeatherLogic.warningTags(warning).takeIf { it.isNotEmpty() }?.let { "threats" to it },
                warning.office?.let { "issued by" to "NWS $it" },
                if (warning.containsHome) "home" to "inside this warning" else null,
            )
        }
        is MapSelection.Site -> {
            val site = wx.mapInfo?.stations?.firstOrNull { it.id == selection.id } ?: return
            title = "${site.id} · ${site.name}"
            val selected = site.id == wx.mapInfo?.station
            rows = listOf("radar" to if (selected) "the per-radar layers come from this one" else "NEXRAD WSR-88D site")
            if (!selected) action = { TextButton(onClick = { wx.selectStation(site.id) }) { Text("Use this radar") } }
        }
        is MapSelection.Outlook -> {
            val area = storms?.outlook?.firstOrNull { it.label == selection.label } ?: return
            title = "SPC today: ${area.name}"
            rows = listOf("outlook" to WeatherLogic.outlookScale(area.label))
        }
    }
    androidx.compose.material3.Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    title,
                    style = MaterialTheme.typography.titleSmall,
                    fontWeight = FontWeight.Bold,
                    color = titleColor ?: Color.Unspecified,
                    modifier = Modifier.weight(1f),
                )
                IconButton(onClick = { wx.selection = null }, modifier = Modifier.size(28.dp)) {
                    Icon(Icons.Default.Close, contentDescription = "Close", modifier = Modifier.size(18.dp))
                }
            }
            for ((label, value) in rows) DetailRow(label, value)
            action?.invoke()
        }
    }
}

private fun cellRows(cell: WeatherStormCell, clock: WeatherClock): List<Pair<String, String>> = listOfNotNull(
    "tracked by" to "${cell.radar} radar · cell ${cell.stormId}",
    "motion" to (cell.speedMph?.takeIf { it > 0 }?.let { "toward the ${cell.headingCompass.orEmpty()} at $it mph" } ?: "nearly stationary"),
    cell.distanceMiles?.let { "from home" to String.format(java.util.Locale.US, "%.1f mi %s", it, cell.bearingCompass.orEmpty()) },
    if (cell.towardHome) "reaches home" to "in about ${cell.etaMinutes} min, passing ${String.format(java.util.Locale.US, "%.1f", cell.closestMiles ?: 0.0)} mi away" else null,
    if (cell.tornadic) "tornado" to "tornado vortex signature - strong rotation near the ground" else null,
    cell.mesoStrength?.let { "rotation" to "mesocyclone rank $it of 25" + if (cell.rotation) "" else " (weak)" },
    "hail" to "${cell.hailProbability ?: 0}% chance, ${cell.severeHailProbability ?: 0}% severe" +
        (cell.maxHailInches?.takeIf { it > 0 }?.let { String.format(java.util.Locale.US, ", up to %.2f\"", it) } ?: ""),
    cell.maxDbz?.let { "max reflectivity" to "$it dBZ" },
    cell.topKft?.let { "storm top" to "${it.roundToInt()},000 ft" },
    cell.vil?.let { "VIL" to "$it kg/m² (water in the column; 40+ often means hail)" },
    cell.validAt?.let { "scan" to clock.clock(it) },
)

// ---------------- Drawing ----------------

@Composable
private fun RadarCanvas(
    wx: WeatherScreenModel,
    layer: WeatherMapLayer?,
    frames: List<String?>,
    modifier: Modifier,
) {
    val density = LocalDensity.current
    val tilePx = with(density) { 256.dp.toPx() }
    val measurer = rememberTextMeasurer()
    val info = wx.mapInfo ?: return
    val station = info.station
    val radarMaxZoom = info.maximumRadarZoom
    val baseMaxZoom = info.maximumBaseZoom
    var size by remember { mutableStateOf(IntSize.Zero) }

    fun view() = MapView(wx.centerX, wx.centerY, wx.zoom, size.width.toFloat(), size.height.toFloat(), tilePx)

    fun radarPath(frame: String?): (Int, Int, Int) -> String? = { z, x, y ->
        layer?.let { WeatherLogic.tilePath(it.id, z, x, y, station = if (it.isStation) station else null, time = frame) }
    }

    // Ask for the tiles the view needs whenever it (or the layer, frame or loop) changes: the
    // basemap and labels, the frame on screen, and every frame while the loop plays.
    LaunchedEffect(layer, station, frames, tilePx) {
        snapshotFlow { Triple(view(), wx.frameIndex, wx.playing) }.collect { (view, index, playing) ->
            if (view.width <= 0f || (layer != null && layer.isStation && station == null)) {
                if (view.width > 0f) wx.tiles.want(visiblePaths(view, baseMaxZoom) { z, x, y -> "/api/weather/map/tile/base/$z/$x/$y" } +
                    visiblePaths(view, baseMaxZoom) { z, x, y -> "/api/weather/map/tile/labels/$z/$x/$y" })
                return@collect
            }
            val wanted = ArrayList<String>()
            wanted += visiblePaths(view, baseMaxZoom) { z, x, y -> "/api/weather/map/tile/base/$z/$x/$y" }
            wanted += visiblePaths(view, radarMaxZoom, radarPath(frames.getOrNull(index)))
            wanted += visiblePaths(view, baseMaxZoom) { z, x, y -> "/api/weather/map/tile/labels/$z/$x/$y" }
            if (playing) frames.forEach { wanted += visiblePaths(view, radarMaxZoom, radarPath(it)) }
            wx.tiles.want(wanted.distinct())
        }
    }
    // Storms for the part of the map on screen, a moment after the map stops moving.
    LaunchedEffect(Unit) {
        @OptIn(kotlinx.coroutines.FlowPreview::class)
        snapshotFlow { view() }.debounce(400).collect { view ->
            if (view.width > 0f) wx.onViewSettled(view)
        }
    }

    val labelStyle = TextStyle(color = Color.White, fontSize = 11.sp, fontWeight = FontWeight.Bold)
    Canvas(
        modifier
            .background(Color(0xFF1B2127))
            .onSizeChanged { size = it }
            .pointerInput(tilePx) {
                detectTransformGestures { centroid, pan, zoomChange, _ ->
                    wx.transform(view(), centroid, pan, zoomChange)
                }
            }
            .pointerInput(tilePx) {
                detectTapGestures(
                    onDoubleTap = { wx.zoomAround(view(), it, 1.0) },
                    onTap = { wx.selection = hitTest(wx, view(), it, density.density) },
                )
            },
    ) {
        val view = view()
        val prefs = wx.prefs
        val storms = wx.storms
        drawTiles(wx.tiles, view, baseMaxZoom, 1f, fallback = true) { z, x, y -> "/api/weather/map/tile/base/$z/$x/$y" }
        if (prefs.outlook) storms?.outlook?.sortedBy { it.rank }?.forEach { area ->
            drawPolygons(view, area.polygons, parseColor(area.stroke) ?: Color.Gray, parseColor(area.fill) ?: Color.Gray, 0.18f, 1.5.dp.toPx())
        }
        if (layer != null && !(layer.isStation && station == null)) {
            drawTiles(wx.tiles, view, radarMaxZoom, prefs.opacity, fallback = !wx.playing, radarPath(frames.getOrNull(wx.frameIndex)))
        }
        drawTiles(wx.tiles, view, baseMaxZoom, 1f, fallback = true) { z, x, y -> "/api/weather/map/tile/labels/$z/$x/$y" }
        if (prefs.warnings) storms?.warnings
            ?.sortedBy { WeatherLogic.warningDrawOrder(it.phenomena) }
            ?.forEach { warning ->
                val color = Color(WeatherLogic.warningColor(warning.phenomena))
                val selected = (wx.selection as? MapSelection.Warning)?.id == warning.id
                drawPolygons(view, warning.polygons, color, color, if (selected) 0.28f else 0.12f, (if (warning.phenomena == "TO") 3 else 2).dp.toPx())
            }
        if (prefs.sites) info.stations.forEach { site ->
            val at = view.screen(site.latitude, site.longitude)
            if (!onCanvas(at)) return@forEach
            val chosen = site.id == station
            drawCircle(Color(0xFF4BE7FF).copy(alpha = if (chosen) 1f else 0.7f), (if (chosen) 6 else 4).dp.toPx(), at)
            drawCircle(Color.Black.copy(alpha = 0.6f), (if (chosen) 6 else 4).dp.toPx(), at, style = Stroke(1.dp.toPx()))
        }
        if (prefs.cells) drawCells(view, storms?.cells.orEmpty(), (wx.selection as? MapSelection.Cell)?.id, measurer)
        info.home?.let { home ->
            drawHome(view.screen(home.latitude, home.longitude), home.label.ifBlank { "Home" }.uppercase(), measurer, labelStyle)
        }
    }
}

private fun visiblePaths(view: MapView, maxNative: Int, path: (Int, Int, Int) -> String?): List<String> {
    val z = MapView.tileZoom(view.zoom, maxNative)
    val n = 1 shl z
    return view.tiles(z).mapNotNull { (x, y) -> path(z, Math.floorMod(x, n), y) }
}

private fun DrawScope.onCanvas(at: Offset, margin: Float = 40f): Boolean =
    at.x > -margin && at.y > -margin && at.x < size.width + margin && at.y < size.height + margin

private fun DrawScope.drawTiles(
    store: WeatherTileStore,
    view: MapView,
    maxNative: Int,
    alpha: Float,
    fallback: Boolean,
    path: (Int, Int, Int) -> String?,
) {
    val z = MapView.tileZoom(view.zoom, maxNative)
    val n = 1 shl z
    val tileSize = view.worldPx / n
    for ((x, y) in view.tiles(z)) {
        val left = view.screenX(x.toDouble() / n)
        val top = view.screenY(y.toDouble() / n)
        val dstOffset = IntOffset(floor(left).toInt(), floor(top).toInt())
        val dstSize = IntSize(ceil(left + tileSize).toInt() - dstOffset.x, ceil(top + tileSize).toInt() - dstOffset.y)
        val image = path(z, Math.floorMod(x, n), y)?.let(store::get)
        if (image != null) {
            drawImage(image, dstOffset = dstOffset, dstSize = dstSize, alpha = alpha, filterQuality = FilterQuality.Low)
            continue
        }
        if (!fallback) continue
        // A coarser tile already in memory stands in, stretched, until this one arrives.
        for (up in 1..4) {
            val pz = z - up
            if (pz < 0) break
            val scale = 1 shl up
            val px = Math.floorDiv(x, scale)
            val py = Math.floorDiv(y, scale)
            val parent = path(pz, Math.floorMod(px, 1 shl pz), py)?.let(store::get) ?: continue
            val sub = parent.width / scale
            if (sub <= 0) break
            drawImage(
                parent,
                srcOffset = IntOffset((x - px * scale) * sub, (y - py * scale) * sub),
                srcSize = IntSize(sub, sub),
                dstOffset = dstOffset,
                dstSize = dstSize,
                alpha = alpha,
                filterQuality = FilterQuality.Low,
            )
            break
        }
    }
}

private fun DrawScope.drawPolygons(view: MapView, polygons: GeoPolygons, stroke: Color, fill: Color, fillAlpha: Float, width: Float) {
    for (rings in polygons) {
        val path = Path().apply { fillType = PathFillType.EvenOdd }
        for (ring in rings) {
            ring.forEachIndexed { i, point ->
                val at = view.screen(point)
                if (i == 0) path.moveTo(at.x, at.y) else path.lineTo(at.x, at.y)
            }
            path.close()
        }
        val bounds = path.getBounds()
        if (bounds.right < 0 || bounds.bottom < 0 || bounds.left > size.width || bounds.top > size.height) continue
        drawPath(path, fill, alpha = fillAlpha)
        drawPath(path, stroke, style = Stroke(width))
    }
}

private fun threatColor(threat: Int): Color = when {
    threat >= 3 -> Color(0xFFFF2D3D)
    threat == 2 -> Color(0xFFFF8C00)
    threat == 1 -> Color(0xFFFFC400)
    else -> Color(0xFF4BE7FF)
}

private fun DrawScope.drawCells(
    view: MapView,
    cells: List<WeatherStormCell>,
    selectedId: String?,
    measurer: TextMeasurer,
) {
    for (cell in cells.sortedBy { it.threat }) {
        if (!WeatherLogic.cellVisible(cell, view.zoom, selectedId)) continue
        val at = view.screen(cell.latitude, cell.longitude)
        val color = threatColor(cell.threat)
        if (WeatherLogic.trackVisible(cell, view.zoom)) {
            var last = at
            for (point in cell.track) {
                val next = view.screen(point.latitude, point.longitude)
                drawLine(
                    if (cell.towardHome) Color(0xFFFF4D5E) else color.copy(alpha = 0.8f),
                    last,
                    next,
                    strokeWidth = 2.dp.toPx(),
                    cap = StrokeCap.Round,
                    pathEffect = androidx.compose.ui.graphics.PathEffect.dashPathEffect(floatArrayOf(6.dp.toPx(), 4.dp.toPx())),
                )
                drawCircle(Color.White, 2.5.dp.toPx(), next)
                last = next
            }
        }
        if (!onCanvas(at)) continue
        val radius = (if (cell.threat >= 1) 8 else 6).dp.toPx()
        if (cell.id == selectedId) drawCircle(Color.White, radius + 4.dp.toPx(), at, style = Stroke(2.dp.toPx()))
        if (cell.towardHome) drawCircle(Color(0xFFFF4D5E), radius + 2.dp.toPx(), at, style = Stroke(2.dp.toPx()))
        drawCircle(color, radius, at)
        drawCircle(Color.Black.copy(alpha = 0.7f), radius, at, style = Stroke(1.dp.toPx()))
        cell.headingDegrees?.takeIf { (cell.speedMph ?: 0) > 0 }?.let { heading ->
            rotate(heading.toFloat(), pivot = at) {
                val tip = Offset(at.x, at.y - radius - 7.dp.toPx())
                val arrow = Path().apply {
                    moveTo(tip.x, tip.y)
                    lineTo(at.x - 4.dp.toPx(), at.y - radius - 1.dp.toPx())
                    lineTo(at.x + 4.dp.toPx(), at.y - radius - 1.dp.toPx())
                    close()
                }
                drawPath(arrow, color)
            }
        }
        val letter = WeatherLogic.cellLetter(cell)
        if (letter.isNotEmpty()) {
            val text = measurer.measure(letter, TextStyle(color = Color.Black, fontSize = 10.sp, fontWeight = FontWeight.Bold))
            drawText(text, topLeft = Offset(at.x - text.size.width / 2f, at.y - text.size.height / 2f))
        }
    }
}

private fun DrawScope.drawHome(at: Offset, label: String, measurer: TextMeasurer, style: TextStyle) {
    if (!onCanvas(at, 200f)) return
    drawCircle(Color(0xFF38FF9C).copy(alpha = 0.25f), 16.dp.toPx(), at)
    drawCircle(Color(0xFF38FF9C), 16.dp.toPx(), at, style = Stroke(1.5.dp.toPx()))
    drawCircle(Color(0xFF0E8C4E), 6.dp.toPx(), at)
    drawCircle(Color.White, 6.dp.toPx(), at, style = Stroke(2.dp.toPx()))
    val text = measurer.measure(label, style)
    val topLeft = Offset(at.x + 12.dp.toPx(), at.y - text.size.height - 6.dp.toPx())
    drawRoundRect(
        Color.Black.copy(alpha = 0.65f),
        topLeft = Offset(topLeft.x - 4.dp.toPx(), topLeft.y - 2.dp.toPx()),
        size = androidx.compose.ui.geometry.Size(text.size.width + 8.dp.toPx(), text.size.height + 4.dp.toPx()),
        cornerRadius = androidx.compose.ui.geometry.CornerRadius(4.dp.toPx()),
    )
    drawText(text, topLeft = topLeft)
}

private fun parseColor(hex: String?): Color? {
    val value = hex?.trim()?.removePrefix("#") ?: return null
    return runCatching {
        when (value.length) {
            6 -> Color(0xFF000000 or value.toLong(16))
            8 -> Color(value.toLong(16))
            3 -> Color(0xFF000000 or value.map { "$it$it" }.joinToString("").toLong(16))
            else -> null
        }
    }.getOrNull()
}

// What's under a tap: a storm cell first (they're small), then a radar site, then the most
// serious warning containing the point, then the outlook area.
private fun hitTest(wx: WeatherScreenModel, view: MapView, at: Offset, density: Float): MapSelection? {
    val prefs = wx.prefs
    val storms = wx.storms
    val reach = 20 * density
    if (prefs.cells) {
        storms?.cells.orEmpty()
            .filter { WeatherLogic.cellVisible(it, view.zoom, null) }
            .map { it to (view.screen(it.latitude, it.longitude) - at).getDistance() }
            .filter { it.second <= reach }
            .minByOrNull { it.second }
            ?.let { return MapSelection.Cell(it.first.id) }
    }
    if (prefs.sites) {
        wx.mapInfo?.stations.orEmpty()
            .map { it to (view.screen(it.latitude, it.longitude) - at).getDistance() }
            .filter { it.second <= 14 * density }
            .minByOrNull { it.second }
            ?.let { return MapSelection.Site(it.first.id) }
    }
    val point = view.geo(at)
    if (prefs.warnings) {
        storms?.warnings.orEmpty()
            .filter { WeatherLogic.polygonsContain(it.polygons, point) }
            .maxByOrNull { WeatherLogic.warningDrawOrder(it.phenomena) }
            ?.let { return MapSelection.Warning(it.id) }
    }
    if (prefs.outlook) {
        storms?.outlook.orEmpty()
            .filter { WeatherLogic.polygonsContain(it.polygons, point) }
            .maxByOrNull { it.rank }
            ?.let { return MapSelection.Outlook(it.label) }
    }
    return null
}

// ---------------- View arithmetic (on the model) ----------------

internal fun zoomedCenter(view: MapView, focus: Offset, newZoom: Double, pan: Offset = Offset.Zero): Pair<Double, Double> {
    // The world point that was under the focus before the gesture moved it stays under the finger.
    val worldX = view.centerX + (focus.x - pan.x - view.width / 2) / view.worldPx
    val worldY = view.centerY + (focus.y - pan.y - view.height / 2) / view.worldPx
    val newWorldPx = view.tilePx * 2.0.pow(newZoom)
    return (worldX - (focus.x - view.width / 2) / newWorldPx) to (worldY - (focus.y - view.height / 2) / newWorldPx)
}

internal fun log2(value: Double): Double = ln(value) / ln(2.0)

// Keeps the loop running while `playing` is on: each frame for FRAME_INTERVAL_MS, the newest held a
// little longer so the eye can tell where the loop ends.
internal suspend fun runLoop(wx: WeatherScreenModel, frameCount: Int) {
    if (frameCount < 2) return
    if (wx.frameIndex >= frameCount - 1) wx.frameIndex = 0
    while (wx.playing) {
        delay(if (wx.frameIndex >= frameCount - 1) LAST_FRAME_HOLD_MS else FRAME_INTERVAL_MS)
        if (!wx.playing) break
        wx.frameIndex = if (wx.frameIndex >= frameCount - 1) 0 else wx.frameIndex + 1
    }
}
