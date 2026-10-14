package com.sentinelcore.android.data

import org.json.JSONArray
import org.json.JSONObject
import java.text.NumberFormat
import java.util.Locale
import kotlin.math.atan2
import kotlin.math.cos
import kotlin.math.log10
import kotlin.math.roundToInt
import kotlin.math.roundToLong
import kotlin.math.sin

// ---------------- /api/weather/lightning ----------------
//
// Flashes from the server's NOAA GOES-19 Lightning Mapper feed for the radar loop's window, drawn
// for whatever moment the loop is showing: the last 15 minutes before it, the newest as bolts, older
// ones fading from yellow to red - so playing the loop shows the lightning marching along with the
// storm. Pure (no Android, no Compose) for the unit tests; each rule mirrors the "---- Lightning ----"
// block of the dashboard's weather.js.

// One flash. `x`/`y` are its Mercator position, worked out once so a redraw per loop frame over a
// few thousand flashes stays cheap.
data class LightningFlash(val latitude: Double, val longitude: Double, val timeMs: Long, val energy: Double) {
    val x: Double = Mercator.x(longitude)
    val y: Double = Mercator.y(latitude)
}

// One answer from the server. `incremental` means "only what arrived since the `after` you sent".
data class LightningAnswer(
    val state: String,
    val epoch: String?,
    val sequence: Long,
    val incremental: Boolean,
    val truncated: Boolean,
    val flashes: List<LightningFlash>,
) {
    val ready: Boolean get() = state == LightningData.READY
}

// What the map holds: every flash of the last 95 minutes in the box last fetched, oldest first.
data class LightningData(
    val epoch: String? = null,
    val sequence: Long = 0,
    val state: String? = null,
    val flashes: List<LightningFlash> = emptyList(),
) {
    val ready: Boolean get() = state == READY

    companion object {
        const val READY = "ready"
    }
}

// west, south, east, north - the same order as MapView.bounds.
data class LightningBox(val west: Double, val south: Double, val east: Double, val north: Double) {
    fun contains(other: LightningBox): Boolean =
        other.west >= west && other.east <= east && other.south >= south && other.north <= north

    fun contains(latitude: Double, longitude: Double): Boolean =
        latitude in south..north && longitude in west..east
}

// How one flash looks at the moment showing: a bolt while under two minutes old, otherwise a dot
// that goes yellow, orange, red and fades as it ages.
sealed interface LightningMark {
    data object Bolt : LightningMark
    data class Dot(val radiusDp: Float, val color: Long, val alpha: Float) : LightningMark
}

data class LightningNearest(val flash: LightningFlash, val miles: Double)

enum class LightningLevel { NORMAL, NEAR, CLOSE }

data class LightningStatus(val text: String, val level: LightningLevel)

object WeatherLightning {
    const val TRAIL_MS = 15 * 60_000L
    const val BOLT_MS = 2 * 60_000L
    const val KEEP_MS = 95 * 60_000L
    const val REFRESH_MS = 30_000L
    const val LOADING_REFRESH_MS = 4_000L
    const val NEAREST_MILES = 100.0

    // The answer's flashes are [lat, lon, secondsAfterOrigin, energyFemtojoules], oldest first.
    fun parse(json: JSONObject): LightningAnswer {
        val origin = json.optLong("origin")
        val rows = json.optJSONArray("flashes") ?: JSONArray()
        val flashes = ArrayList<LightningFlash>(rows.length())
        for (i in 0 until rows.length()) {
            val row = rows.optJSONArray(i) ?: continue
            if (row.length() < 3) continue
            flashes += LightningFlash(
                latitude = row.optDouble(0),
                longitude = row.optDouble(1),
                timeMs = origin + (row.optDouble(2) * 1000).roundToLong(),
                energy = row.optDouble(3, 0.0).takeUnless { it.isNaN() } ?: 0.0,
            )
        }
        return LightningAnswer(
            state = json.optString("state", "loading"),
            epoch = json.optString("epoch").takeIf { it.isNotEmpty() && !json.isNull("epoch") },
            sequence = json.optLong("sequence"),
            incremental = json.optBoolean("incremental", false),
            truncated = json.optBoolean("truncated", false),
            flashes = flashes,
        )
    }

    // An incremental answer adds to what's held; any other replaces it. Either way only the last
    // 95 minutes stay, oldest first.
    fun merge(current: LightningData, answer: LightningAnswer, nowMs: Long): LightningData {
        val cutoff = nowMs - KEEP_MS
        val combined = if (answer.incremental) current.flashes + answer.flashes else answer.flashes
        return LightningData(
            epoch = answer.epoch,
            sequence = answer.sequence,
            state = answer.state,
            flashes = combined.filter { it.timeMs >= cutoff }.sortedBy { it.timeMs },
        )
    }

    // The box asked for: what's on screen padded 30% (the caller passes MapView.bounds(0.3)), plus
    // home's surroundings so the "nearest strike" line stays right while looking somewhere else.
    fun box(paddedView: LightningBox, home: GeoPoint?): LightningBox {
        if (home == null) return paddedView
        return LightningBox(
            west = minOf(paddedView.west, home.longitude - 2),
            south = minOf(paddedView.south, home.latitude - 1.5),
            east = maxOf(paddedView.east, home.longitude + 2),
            north = maxOf(paddedView.north, home.latitude + 1.5),
        )
    }

    fun path(box: LightningBox, epoch: String? = null, after: Long? = null): String {
        val base = String.format(
            Locale.US,
            "/api/weather/lightning?west=%.3f&south=%.3f&east=%.3f&north=%.3f",
            box.west, box.south, box.east, box.north,
        )
        if (epoch == null || after == null) return base
        return base + "&epoch=" + java.net.URLEncoder.encode(epoch, "UTF-8") + "&after=$after"
    }

    // The moment the flashes are drawn for: the loop frame's time, or null for "now" when on the
    // newest frame (lightning runs a few minutes ahead of radar, so the newest frame shows it all).
    fun frameMoment(frames: List<String?>, index: Int): Long? {
        if (frames.isEmpty() || index >= frames.size - 1) return null
        val time = frames.getOrNull(index) ?: return null
        return runCatching { java.time.Instant.parse(time).toEpochMilli() }.getOrNull()
    }

    // The flashes showing at `atMs`: no older than 15 minutes, none from after it, oldest first.
    fun showing(flashes: List<LightningFlash>, atMs: Long): List<LightningFlash> =
        flashes.filter { val age = atMs - it.timeMs; age in 0..TRAIL_MS }

    fun mark(ageMs: Long, energy: Double): LightningMark {
        if (ageMs < BOLT_MS) return LightningMark.Bolt
        val radius = 2 + minOf(2.5, maxOf(0.0, log10(maxOf(energy, 1.0)) - 1.5) * 0.9)
        val color = when {
            ageMs < 5 * 60_000L -> 0xFFFFEB3B
            ageMs < 10 * 60_000L -> 0xFFFF9800
            else -> 0xFFF44336
        }
        return LightningMark.Dot(radius.toFloat(), color, (1 - 0.55 * ageMs / TRAIL_MS).toFloat())
    }

    fun nearest(flashes: List<LightningFlash>, home: GeoPoint, atMs: Long): LightningNearest? {
        var best: LightningNearest? = null
        for (flash in showing(flashes, atMs)) {
            val miles = WeatherLogic.milesBetween(home, GeoPoint(flash.latitude, flash.longitude))
            if (best == null || miles < best.miles) best = LightningNearest(flash, miles)
        }
        return best
    }

    private val COMPASS = listOf("N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW")

    fun compass(degrees: Double): String =
        COMPASS[(((degrees % 360) + 360) % 360 / 22.5).roundToInt() % 16]

    // Initial great-circle bearing from `from` to `to`, degrees clockwise from north.
    fun bearing(from: GeoPoint, to: GeoPoint): Double {
        val lat1 = Math.toRadians(from.latitude)
        val lat2 = Math.toRadians(to.latitude)
        val dLon = Math.toRadians(to.longitude - from.longitude)
        val y = sin(dLon) * cos(lat2)
        val x = cos(lat1) * sin(lat2) - sin(lat1) * cos(lat2) * cos(dLon)
        return Math.toDegrees(atan2(y, x))
    }

    // "6.2 mi NE" - a decimal under ten miles, whole miles past it.
    fun fromHomeText(home: GeoPoint, latitude: Double, longitude: Double): String {
        val point = GeoPoint(latitude, longitude)
        val miles = WeatherLogic.milesBetween(home, point)
        val distance = if (miles < 10) String.format(Locale.US, "%.1f", miles) else miles.roundToInt().toString()
        return "$distance mi ${compass(bearing(home, point))}"
    }

    fun agoText(ms: Long): String {
        val minutes = (ms / 60_000.0).roundToInt()
        return if (minutes < 1) "under a minute" else "$minutes min"
    }

    // "⚡ 1,260 flashes in view, last 15 min · nearest home 6.2 mi NE, 3 min ago". Within 10 miles
    // is the "if you can hear thunder, you can be struck" distance, so it turns red.
    fun status(data: LightningData, view: LightningBox?, home: GeoPoint?, atMs: Long): LightningStatus {
        val recent = showing(data.flashes, atMs)
        val inView = if (view == null) 0 else recent.count { view.contains(it.latitude, it.longitude) }
        val nearest = home?.let { nearest(recent, it, atMs) }
        val parts = ArrayList<String>()
        if (!data.ready && data.flashes.isEmpty()) parts += "loading lightning…"
        else parts += "${NumberFormat.getIntegerInstance(Locale.US).format(inView)} flash${if (inView == 1) "" else "es"} in view, last 15 min"
        if (home != null && nearest != null && nearest.miles <= NEAREST_MILES) {
            parts += "nearest home ${fromHomeText(home, nearest.flash.latitude, nearest.flash.longitude)}, ${agoText(atMs - nearest.flash.timeMs)} ago"
        } else if (home != null && data.ready) {
            parts += "none within 100 mi of home"
        }
        if (!data.ready && data.flashes.isNotEmpty()) parts += "still filling in"
        val miles = nearest?.miles ?: Double.POSITIVE_INFINITY
        val level = when {
            miles <= 10 -> LightningLevel.CLOSE
            miles <= 25 -> LightningLevel.NEAR
            else -> LightningLevel.NORMAL
        }
        return LightningStatus("⚡ " + parts.joinToString(" · "), level)
    }

    // A tapped flash's details. `onNewest` = the loop is on its newest frame, so ages are "ago"
    // rather than "before this frame"; `time` formats the clock with seconds.
    fun details(
        flash: LightningFlash,
        atMs: Long,
        onNewest: Boolean,
        home: GeoPoint?,
        time: (Long) -> String,
    ): List<Pair<String, String>> = listOfNotNull(
        "time" to time(flash.timeMs),
        "age" to agoText(atMs - flash.timeMs) + if (onNewest) " ago" else " before this frame",
        home?.let { "from home" to fromHomeText(it, flash.latitude, flash.longitude) },
        "where" to String.format(Locale.US, "%.3f, %.3f (good to a few miles)", flash.latitude, flash.longitude),
        "brightness" to "${NumberFormat.getIntegerInstance(Locale.US).format(flash.energy.roundToLong())} fJ seen from orbit" +
            if (flash.energy >= 1000) " - a bright one" else "",
        "seen by" to "NOAA GOES-19 Lightning Mapper",
    )
}
