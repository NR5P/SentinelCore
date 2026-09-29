package com.sentinelcore.android.data

import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.util.Locale
import kotlin.math.PI
import kotlin.math.atan
import kotlin.math.cos
import kotlin.math.ln
import kotlin.math.roundToInt
import kotlin.math.sin
import kotlin.math.tan

// The Weather tab's small rules, kept pure (no Android, no Compose) for the unit tests. Each one
// mirrors a function of the dashboard's weather.js, so the tab reads the same on every surface.

// The NWS forecast is a list of half-days (Today, Tonight, Saturday, Saturday Night, ...). One card
// per calendar day in the location's zone, the day period leading and the night giving the low.
data class WeatherDay(
    val date: LocalDate,
    val day: WeatherPeriod?,
    val night: WeatherPeriod?,
) {
    val lead: WeatherPeriod get() = day ?: night!!
    val high: Int? get() = day?.temperatureF
    val low: Int? get() = night?.temperatureF
    val chance: Int get() = maxOf(day?.precipitationChance ?: 0, night?.precipitationChance ?: 0)
}

data class MoonPhase(val name: String, val glyph: String, val illumination: Int, val daysToFull: Int)

object WeatherLogic {

    fun groupDays(periods: List<WeatherPeriod>, zone: ZoneId): List<WeatherDay> {
        val days = LinkedHashMap<LocalDate, WeatherDay>()
        for (period in periods) {
            val date = period.startTime.atZone(zone).toLocalDate()
            val existing = days[date] ?: WeatherDay(date, null, null)
            days[date] = if (period.isDaytime) existing.copy(day = period) else existing.copy(night = period)
        }
        return days.values.toList()
    }

    // Where a day's low-to-high sits within the whole week's range, as fractions 0..1 for the bar
    // on each card. Null when the day has no temperature at all.
    fun rangeFractions(day: WeatherDay, periods: List<WeatherPeriod>): Pair<Float, Float>? {
        val temps = periods.mapNotNull { it.temperatureF }
        if (temps.isEmpty()) return null
        val from = day.low ?: day.high ?: return null
        val to = day.high ?: day.low ?: return null
        val min = temps.min()
        val span = maxOf(temps.max() - min, 1).toFloat()
        val start = (minOf(from, to) - min) / span
        val width = maxOf(kotlin.math.abs(to - from) / span, 0.03f)
        return start.coerceIn(0f, 1f) to width.coerceIn(0.03f, 1f - start.coerceIn(0f, 0.97f))
    }

    // A one-line "what's next" from the hourly forecast: when rain (or snow, or storms) starts or
    // stops in the next twelve hours.
    fun nextPrecipText(hourly: List<WeatherHour>, hourLabel: (Instant) -> String): String? {
        val hours = hourly.take(12)
        if (hours.isEmpty()) return null
        fun wet(h: WeatherHour) = (h.precipitationChance ?: 0) >= 50
        if (wet(hours[0])) {
            val stop = hours.indexOfFirst { !wet(it) }
            return if (stop < 0) "${precipKind(hours[0])} likely for the next 12 hours."
            else "${precipKind(hours[0])} likely until around ${hourLabel(hours[stop].startTime)}."
        }
        hours.firstOrNull(::wet)?.let {
            return "${precipKind(it)} likely from around ${hourLabel(it.startTime)} (${it.precipitationChance}%)."
        }
        val peak = hours.maxBy { it.precipitationChance ?: 0 }
        val chance = peak.precipitationChance ?: 0
        if (chance >= 20) {
            return "Slight chance of ${precipKind(peak).lowercase(Locale.US)} around ${hourLabel(peak.startTime)} ($chance%)."
        }
        return "No rain expected in the next 12 hours."
    }

    fun precipKind(hour: WeatherHour): String {
        val text = hour.shortForecast.lowercase(Locale.US)
        return when {
            "thunder" in text -> "Storms"
            "snow" in text || "flurr" in text -> "Snow"
            "sleet" in text || "freezing" in text -> "Wintry mix"
            "drizzle" in text -> "Drizzle"
            else -> "Rain"
        }
    }

    // The moon's phase from its age since a known new moon (6 Jan 2000 18:14 UTC). Good to within
    // a few hours, which is all a phase name needs.
    private const val SYNODIC_DAYS = 29.530588853
    private val KNOWN_NEW_MOON: Instant = Instant.parse("2000-01-06T18:14:00Z")

    fun moonPhase(at: Instant): MoonPhase {
        val days = Duration.between(KNOWN_NEW_MOON, at).toMillis() / 86_400_000.0
        val age = ((days % SYNODIC_DAYS) + SYNODIC_DAYS) % SYNODIC_DAYS
        val illumination = ((1 - cos(2 * PI * age / SYNODIC_DAYS)) / 2 * 100).roundToInt()
        val (name, glyph) = when {
            age < 1.84566 -> "New moon" to "🌑"
            age < 5.53699 -> "Waxing crescent" to "🌒"
            age < 9.22831 -> "First quarter" to "🌓"
            age < 12.91963 -> "Waxing gibbous" to "🌔"
            age < 16.61096 -> "Full moon" to "🌕"
            age < 20.30228 -> "Waning gibbous" to "🌖"
            age < 23.99361 -> "Last quarter" to "🌗"
            age < 27.68493 -> "Waning crescent" to "🌘"
            else -> "New moon" to "🌑"
        }
        val toFull = ((((14.765 - age) % SYNODIC_DAYS) + SYNODIC_DAYS) % SYNODIC_DAYS).roundToInt()
        return MoonPhase(name, glyph, illumination, toFull)
    }

    fun daylightText(sun: WeatherSun?): String? {
        val rise = sun?.sunrise ?: return null
        val set = sun.sunset ?: return null
        val minutes = Duration.between(rise, set).toMinutes().toInt()
        if (minutes <= 0) return null
        return "${minutes / 60}h ${minutes % 60}m"
    }

    // How the dew point feels, the number meteorologists actually use for "muggy".
    fun dewComfort(dewpointF: Int): String = when {
        dewpointF >= 70 -> " (oppressive)"
        dewpointF >= 65 -> " (muggy)"
        dewpointF >= 60 -> " (sticky)"
        dewpointF <= 30 -> " (dry)"
        else -> ""
    }

    fun windText(current: WeatherCurrent): String? {
        val mph = current.windMph ?: return null
        if (mph == 0) return "calm"
        return listOfNotNull(current.windDirection, "$mph mph").joinToString(" ")
    }

    fun pressureArrow(trend: String): String = when (trend) {
        "rising fast" -> "⇈"
        "rising" -> "↑"
        "steady" -> "→"
        "falling" -> "↓"
        "falling fast" -> "⇊"
        else -> ""
    }

    fun inches(value: Double?, digits: Int = 2): String = when {
        value == null -> "—"
        value == 0.0 -> "0\""
        value < 0.01 -> "trace"
        else -> String.format(Locale.US, "%.${digits}f\"", value)
    }

    fun outlookScale(category: String?): String = when (category) {
        "TSTM" -> "thunderstorms possible"
        "MRGL" -> "1 of 5 · marginal"
        "SLGT" -> "2 of 5 · slight"
        "ENH" -> "3 of 5 · enhanced"
        "MDT" -> "4 of 5 · moderate"
        "HIGH" -> "5 of 5 · high"
        else -> "no severe storms expected"
    }

    // ---------------- Storms ----------------

    fun cellTitle(cell: WeatherStormCell): String = when {
        cell.tornadic -> "Possible tornado"
        cell.rotation -> "Rotating storm"
        cell.threat == 2 -> "Severe hail storm"
        cell.threat == 1 -> "Strong storm"
        else -> "Storm"
    }

    fun cellHazards(cell: WeatherStormCell): String = buildList {
        if (cell.tornadic) add("tornado vortex signature")
        if (cell.rotation) add("mesocyclone (rank ${cell.mesoStrength})")
        val hail = cell.maxHailInches
        if (hail != null && hail > 0) add(String.format(Locale.US, "hail to %.2f\"", hail))
        else if ((cell.hailProbability ?: 0) > 0) add("${cell.hailProbability}% hail")
    }.joinToString(" · ")

    fun cellLetter(cell: WeatherStormCell): String = when {
        cell.tornadic -> "T"
        cell.rotation -> "M"
        cell.threat == 2 -> "H"
        else -> ""
    }

    fun warningTags(warning: WeatherWarningArea): String = buildList {
        warning.tornadoTag?.let { add("tornado ${it.lowercase(Locale.US)}") }
        warning.hailInches?.takeIf { it > 0 }?.let { add("hail ${trimNumber(it)}\"") }
        warning.windMph?.takeIf { it > 0 }?.let { add("wind ${trimNumber(it)} mph") }
        warning.damageTag?.let { add("${it.lowercase(Locale.US)} damage threat") }
    }.joinToString(" · ")

    private fun trimNumber(value: Double): String =
        if (value == Math.floor(value)) value.toLong().toString() else value.toString()

    // Warning outline colours, as ARGB - the dashboard's warningColor().
    fun warningColor(phenomena: String): Long = when (phenomena) {
        "TO" -> 0xFFFF2D3D
        "SV" -> 0xFFFFC400
        "FF" -> 0xFF00E676
        "FA", "FL" -> 0xFF2E9D4F
        "MA" -> 0xFFFF8C00
        "EW" -> 0xFFFF3DF5
        "SQ" -> 0xFFD63384
        "DS" -> 0xFFD9B38C
        "SS" -> 0xFFB388FF
        else -> 0xFF4BE7FF
    }

    // Weakest first, so a tornado warning draws over the severe thunderstorm warning it sits in.
    fun warningDrawOrder(phenomena: String): Int = when (phenomena) {
        "TO" -> 9
        "EW" -> 8
        "SV" -> 7
        "SQ" -> 6
        "FF" -> 5
        "MA" -> 4
        else -> 0
    }

    private val BANNER_PHENOMENA = setOf("TO", "SV", "FF", "EW", "SQ", "MA")

    // The warnings with home inside them that deserve the banner at the top of the tab.
    fun coveringWarnings(storms: WeatherStorms?): List<WeatherWarningArea> =
        storms?.warnings.orEmpty().filter { it.containsHome && it.phenomena in BANNER_PHENOMENA }

    // Tracked storms on course for home, most dangerous then soonest first.
    fun incomingCells(storms: WeatherStorms?): List<WeatherStormCell> =
        storms?.cells.orEmpty()
            .filter { it.towardHome && it.etaMinutes != null }
            .sortedWith(compareByDescending<WeatherStormCell> { it.threat }.thenBy { it.etaMinutes })

    fun bannerIsSevere(storms: WeatherStorms?): Boolean =
        coveringWarnings(storms).any { it.phenomena == "TO" } || incomingCells(storms).any { it.threat >= 3 }

    // The STORMS NEAR HOME list: within 100 miles, heading-home first, then threat, then distance.
    fun nearbyCells(storms: WeatherStorms?, limit: Int = 6): List<WeatherStormCell> =
        storms?.cells.orEmpty()
            .filter { (it.distanceMiles ?: Double.MAX_VALUE) <= 100 }
            .sortedWith(
                compareByDescending<WeatherStormCell> { it.towardHome }
                    .thenByDescending { it.threat }
                    .thenBy { it.distanceMiles },
            )
            .take(limit)

    // The map's view and home's surroundings are asked for separately so the near-home list and the
    // banner stay right while someone looks at a storm three states away; merged by id.
    fun mergeStorms(view: WeatherStorms, near: WeatherStorms?): WeatherStorms {
        if (near == null) return view
        val cells = LinkedHashMap<String, WeatherStormCell>()
        view.cells.forEach { cells[it.id] = it }
        near.cells.forEach { cells[it.id] = it }
        val warnings = LinkedHashMap<String, WeatherWarningArea>()
        view.warnings.forEach { warnings[it.id] = it }
        near.warnings.forEach { warnings[it.id] = it }
        return view.copy(cells = cells.values.toList(), warnings = warnings.values.toList())
    }

    // Which storm cells go on the map at this zoom: anything notable or heading home, and
    // everything once zoomed in far enough that it won't turn the map into a hairball.
    fun cellVisible(cell: WeatherStormCell, zoom: Double, selectedId: String?): Boolean =
        !(cell.threat == 0 && zoom < 9 && !cell.towardHome && cell.id != selectedId)

    fun trackVisible(cell: WeatherStormCell, zoom: Double): Boolean =
        cell.track.isNotEmpty() && (cell.threat >= 1 || cell.towardHome || zoom >= 10)

    // ---------------- Map layers ----------------

    fun currentLayer(info: WeatherMapInfo?, preferred: String?): WeatherMapLayer? {
        val layers = info?.layers ?: return null
        return layers.firstOrNull { it.id == preferred }
            ?: layers.firstOrNull { it.id == "reflectivity" }
            ?: layers.firstOrNull()
    }

    fun tilePath(layer: String, z: Int, x: Int, y: Int, station: String? = null, time: String? = null): String {
        val query = buildList {
            station?.let { add("station=${encode(it)}") }
            time?.let { add("time=${encode(it)}") }
        }.joinToString("&")
        return "/api/weather/map/tile/${encode(layer)}/$z/$x/$y" + if (query.isEmpty()) "" else "?$query"
    }

    fun legendPath(layer: WeatherMapLayer, station: String?): String =
        "/api/weather/map/legend/${encode(layer.id)}" +
            if (layer.isStation && station != null) "?station=${encode(station)}" else ""

    fun stormsPath(west: Double, south: Double, east: Double, north: Double): String =
        String.format(
            Locale.US,
            "/api/weather/storms?west=%.3f&south=%.3f&east=%.3f&north=%.3f",
            west, south, east, north,
        )

    private fun encode(value: String): String = java.net.URLEncoder.encode(value, "UTF-8")

    fun milesBetween(a: GeoPoint, b: GeoPoint): Double {
        val lat1 = Math.toRadians(a.latitude)
        val lat2 = Math.toRadians(b.latitude)
        val dLat = lat2 - lat1
        val dLon = Math.toRadians(b.longitude - a.longitude)
        val h = sin(dLat / 2) * sin(dLat / 2) + cos(lat1) * cos(lat2) * sin(dLon / 2) * sin(dLon / 2)
        return 2 * 3958.7613 * kotlin.math.asin(kotlin.math.sqrt(h.coerceIn(0.0, 1.0)))
    }

    // Even-odd point-in-polygon across every ring, so holes stay holes.
    fun polygonsContain(polygons: GeoPolygons, point: GeoPoint): Boolean =
        polygons.any { rings ->
            var inside = false
            for (ring in rings) {
                var j = ring.size - 1
                for (i in ring.indices) {
                    val a = ring[i]
                    val b = ring[j]
                    if ((a.latitude > point.latitude) != (b.latitude > point.latitude) &&
                        point.longitude < (b.longitude - a.longitude) * (point.latitude - a.latitude) /
                        (b.latitude - a.latitude) + a.longitude
                    ) inside = !inside
                    j = i
                }
            }
            inside
        }

    // NWS icon codes (api.weather.gov/icons) to a glyph, day then night - the same emoji as the
    // dashboard. Anything unrecognised gets a thermometer rather than nothing.
    private val GLYPHS = mapOf(
        "skc" to ("☀️" to "🌙"),
        "few" to ("🌤️" to "🌙"),
        "sct" to ("⛅" to "☁️"),
        "bkn" to ("🌥️" to "☁️"),
        "ovc" to ("☁️" to "☁️"),
        "wind_skc" to ("💨" to "💨"),
        "wind_few" to ("💨" to "💨"),
        "wind_sct" to ("💨" to "💨"),
        "wind_bkn" to ("💨" to "💨"),
        "wind_ovc" to ("💨" to "💨"),
        "snow" to ("🌨️" to "🌨️"),
        "rain_snow" to ("🌨️" to "🌨️"),
        "rain_sleet" to ("🌨️" to "🌨️"),
        "snow_sleet" to ("🌨️" to "🌨️"),
        "fzra" to ("🧊" to "🧊"),
        "rain_fzra" to ("🧊" to "🧊"),
        "snow_fzra" to ("🧊" to "🧊"),
        "sleet" to ("🌨️" to "🌨️"),
        "rain" to ("🌧️" to "🌧️"),
        "rain_showers" to ("🌦️" to "🌧️"),
        "rain_showers_hi" to ("🌦️" to "🌧️"),
        "tsra" to ("⛈️" to "⛈️"),
        "tsra_sct" to ("⛈️" to "⛈️"),
        "tsra_hi" to ("🌩️" to "🌩️"),
        "tornado" to ("🌪️" to "🌪️"),
        "hurricane" to ("🌀" to "🌀"),
        "tropical_storm" to ("🌀" to "🌀"),
        "dust" to ("🌫️" to "🌫️"),
        "smoke" to ("🌫️" to "🌫️"),
        "haze" to ("🌫️" to "🌫️"),
        "fog" to ("🌫️" to "🌫️"),
        "hot" to ("🥵" to "🥵"),
        "cold" to ("🥶" to "🥶"),
        "blizzard" to ("🌨️" to "🌨️"),
    )

    fun glyph(code: String?, isDaytime: Boolean?): String {
        val pair = GLYPHS[code] ?: return "🌡️"
        return if (isDaytime == false) pair.second else pair.first
    }
}

// Web Mercator, in "world" units: x and y both run 0..1 across the whole map, so a zoom level only
// scales them. The tile scheme is the usual XYZ one the server's tile endpoint takes.
object Mercator {
    private const val MAX_LATITUDE = 85.05112878

    fun x(longitude: Double): Double = (longitude + 180.0) / 360.0

    fun y(latitude: Double): Double {
        val lat = Math.toRadians(latitude.coerceIn(-MAX_LATITUDE, MAX_LATITUDE))
        return (1.0 - ln(tan(lat) + 1.0 / cos(lat)) / PI) / 2.0
    }

    fun longitude(x: Double): Double = x * 360.0 - 180.0

    fun latitude(y: Double): Double = Math.toDegrees(atan(kotlin.math.sinh(PI * (1 - 2 * y))))
}
