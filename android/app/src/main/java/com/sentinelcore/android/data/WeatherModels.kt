package com.sentinelcore.android.data

import org.json.JSONArray
import org.json.JSONObject
import java.time.Instant
import java.time.ZoneId

// The Weather tab's data, as core/SentinelCore.Web/WeatherService.cs and WeatherMapService.cs send
// it (camelCase JSON). /api/weather always answers 200 with a `state`; everything past it is
// optional, so a report that only explains itself - no location set, outside the NWS's coverage,
// weather.gov unreachable - still parses. The same shapes as the Apple TV app's WeatherModels.swift
// and the dashboard's weather.js.

object WeatherState {
    const val OK = "ok"
    const val UNCONFIGURED = "unconfigured"
    const val UNSUPPORTED = "unsupported"
    const val UNREACHABLE = "unreachable"
}

data class WeatherReport(
    val state: String,
    val message: String? = null,
    val location: WeatherLocation? = null,
    val current: WeatherCurrent? = null,
    val daily: List<WeatherPeriod> = emptyList(),
    val hourly: List<WeatherHour> = emptyList(),
    val alerts: List<WeatherAlert> = emptyList(),
    val sun: WeatherSun? = null,
    val radarStation: String? = null,
    val forecastUpdatedAt: Instant? = null,
    val fetchedAt: Instant? = null,
    val stale: Boolean = false,
    val problems: List<String> = emptyList(),
) {
    // Times read in the house's own zone, like the dashboard, so the forecast is right even when
    // the phone is two zones away.
    val zone: ZoneId
        get() = location?.timeZone?.let { runCatching { ZoneId.of(it) }.getOrNull() } ?: ZoneId.systemDefault()
}

data class WeatherLocation(
    val latitude: Double,
    val longitude: Double,
    val label: String,
    val city: String? = null,
    val state: String? = null,
    val timeZone: String? = null,
    val radarStation: String? = null,
    val forecastOffice: String? = null,
) {
    val nearest: String get() = listOfNotNull(city, state).joinToString(", ")

    val displayName: String
        get() = label.ifBlank { nearest.ifBlank { "This location" } }
}

data class WeatherCurrent(
    val observedAt: Instant?,
    val station: String,
    val stationName: String,
    val description: String,
    val icon: String?,
    val isDaytime: Boolean?,
    val temperatureF: Int,
    val feelsLikeF: Int?,
    val dewpointF: Int?,
    val humidityPercent: Int?,
    val windMph: Int?,
    val windGustMph: Int?,
    val windDirection: String?,
    val pressureInHg: Double?,
    val visibilityMiles: Double?,
)

data class WeatherPeriod(
    val name: String,
    val startTime: Instant,
    val endTime: Instant?,
    val isDaytime: Boolean,
    val temperatureF: Int?,
    val precipitationChance: Int?,
    val windSpeed: String?,
    val windDirection: String?,
    val icon: String?,
    val shortForecast: String,
    val detailedForecast: String,
)

data class WeatherHour(
    val startTime: Instant,
    val isDaytime: Boolean,
    val temperatureF: Int?,
    val precipitationChance: Int?,
    val humidityPercent: Int?,
    val windSpeed: String?,
    val windDirection: String?,
    val icon: String?,
    val shortForecast: String,
)

data class WeatherAlert(
    val id: String,
    val event: String,
    val severity: String,
    val urgency: String,
    val headline: String,
    val description: String,
    val instruction: String,
    val ends: Instant?,
    val areaDescription: String,
    val sender: String,
) {
    val isSevere: Boolean get() = severity == "Extreme" || severity == "Severe"
    val isModerate: Boolean get() = severity == "Moderate"
}

data class WeatherSun(val sunrise: Instant?, val sunset: Instant?)

// ---------------- /api/weather/details ----------------

data class WeatherDetails(
    val state: String,
    val totals: WeatherPrecipTotals? = null,
    val hours: List<WeatherHourDetail> = emptyList(),
    val pressure: WeatherPressureTrend? = null,
    val discussion: WeatherDiscussion? = null,
    val severe: WeatherSevereOutlook? = null,
    val problems: List<String> = emptyList(),
)

data class WeatherPrecipTotals(
    val rainNext24HoursInches: Double,
    val rainNext7DaysInches: Double,
    val snowNext24HoursInches: Double,
    val snowNext7DaysInches: Double,
    val iceNext7DaysInches: Double,
    val thunderChanceNext24Hours: Int,
    val thunderChanceNext7Days: Int,
)

data class WeatherHourDetail(
    val startTime: Instant,
    val feelsLikeF: Int?,
    val gustMph: Int?,
    val skyCoverPercent: Int?,
    val rainInches: Double,
    val snowInches: Double,
)

data class WeatherPressureTrend(val changeInHgPer3Hours: Double, val trend: String)

data class WeatherDiscussion(val office: String, val issuedAt: Instant?, val text: String)

data class WeatherSevereOutlook(
    val days: List<WeatherOutlookDay>,
    val tornadoPercent: Int?,
    val hailPercent: Int?,
    val windPercent: Int?,
    val significantTornado: Boolean,
    val significantHail: Boolean,
    val significantWind: Boolean,
    val issuedAt: Instant?,
)

data class WeatherOutlookDay(val day: Int, val category: String?, val name: String, val rank: Int)

// ---------------- /api/weather/map and /api/weather/storms ----------------

data class GeoPoint(val latitude: Double, val longitude: Double)

// GeoJSON-shaped: a list of polygons, each a list of rings (the first the outline, the rest holes).
typealias GeoPolygons = List<List<List<GeoPoint>>>

data class WeatherMapInfo(
    val home: WeatherMapHome?,
    val homeStation: String?,
    val station: String?,
    val stations: List<WeatherRadarSite>,
    val layers: List<WeatherMapLayer>,
    val maximumRadarZoom: Int,
    val maximumBaseZoom: Int,
    val problems: List<String> = emptyList(),
)

data class WeatherMapHome(val latitude: Double, val longitude: Double, val label: String)

data class WeatherRadarSite(val id: String, val name: String, val latitude: Double, val longitude: Double)

data class WeatherMapLayer(
    val id: String,
    val name: String,
    val scope: String,
    val description: String,
    // Frame times, oldest first, passed back to the tile endpoint exactly as received.
    val times: List<String>,
) {
    val isStation: Boolean get() = scope == "station"
}

data class WeatherStorms(
    val cells: List<WeatherStormCell> = emptyList(),
    val warnings: List<WeatherWarningArea> = emptyList(),
    val outlook: List<WeatherOutlookArea> = emptyList(),
    val problems: List<String> = emptyList(),
)

data class WeatherStormCell(
    val id: String,
    val radar: String,
    val stormId: String,
    val latitude: Double,
    val longitude: Double,
    val threat: Int,
    val tornadic: Boolean,
    val rotation: Boolean,
    val mesoStrength: String?,
    val hailProbability: Int?,
    val severeHailProbability: Int?,
    val maxHailInches: Double?,
    val vil: Int?,
    val maxDbz: Int?,
    val topKft: Double?,
    val headingDegrees: Int?,
    val headingCompass: String?,
    val speedMph: Int?,
    val track: List<WeatherTrackPoint>,
    val distanceMiles: Double?,
    val bearingCompass: String?,
    val towardHome: Boolean,
    val closestMiles: Double?,
    val etaMinutes: Int?,
    val validAt: Instant?,
)

data class WeatherTrackPoint(val minutes: Int, val latitude: Double, val longitude: Double)

data class WeatherWarningArea(
    val id: String,
    val phenomena: String,
    val significance: String,
    val name: String,
    val office: String?,
    val issued: Instant?,
    val expires: Instant?,
    val hailInches: Double?,
    val windMph: Double?,
    val tornadoTag: String?,
    val damageTag: String?,
    val emergency: Boolean,
    val particularlyDangerous: Boolean,
    val containsHome: Boolean,
    val polygons: GeoPolygons,
)

data class WeatherOutlookArea(
    val label: String,
    val name: String,
    val rank: Int,
    val fill: String?,
    val stroke: String?,
    val polygons: GeoPolygons,
)

// ---------------- Parsing ----------------

internal object WeatherJson {
    fun report(json: JSONObject): WeatherReport = WeatherReport(
        state = json.optString("state", WeatherState.OK),
        message = json.str("message"),
        location = json.obj("location")?.let(::location),
        current = json.obj("current")?.let(::current),
        daily = json.arr("daily").objects(::period),
        hourly = json.arr("hourly").objects(::hour),
        alerts = json.arr("alerts").objects(::alert),
        sun = json.obj("sun")?.let { WeatherSun(it.instant("sunrise"), it.instant("sunset")) },
        radarStation = json.obj("radar")?.str("station"),
        forecastUpdatedAt = json.instant("forecastUpdatedAt"),
        fetchedAt = json.instant("fetchedAt"),
        stale = json.optBoolean("stale", false),
        problems = json.arr("problems").strings(),
    )

    fun details(json: JSONObject): WeatherDetails = WeatherDetails(
        state = json.optString("state", WeatherState.OK),
        totals = json.obj("totals")?.let {
            WeatherPrecipTotals(
                rainNext24HoursInches = it.dbl("rainNext24HoursInches") ?: 0.0,
                rainNext7DaysInches = it.dbl("rainNext7DaysInches") ?: 0.0,
                snowNext24HoursInches = it.dbl("snowNext24HoursInches") ?: 0.0,
                snowNext7DaysInches = it.dbl("snowNext7DaysInches") ?: 0.0,
                iceNext7DaysInches = it.dbl("iceNext7DaysInches") ?: 0.0,
                thunderChanceNext24Hours = it.optInt("thunderChanceNext24Hours"),
                thunderChanceNext7Days = it.optInt("thunderChanceNext7Days"),
            )
        },
        hours = json.arr("hours").objects { h ->
            h.instant("startTime")?.let {
                WeatherHourDetail(
                    startTime = it,
                    feelsLikeF = h.int("feelsLikeF"),
                    gustMph = h.int("gustMph"),
                    skyCoverPercent = h.int("skyCoverPercent"),
                    rainInches = h.dbl("rainInches") ?: 0.0,
                    snowInches = h.dbl("snowInches") ?: 0.0,
                )
            }
        }.filterNotNull(),
        pressure = json.obj("pressure")?.let {
            WeatherPressureTrend(it.dbl("changeInHgPer3Hours") ?: 0.0, it.optString("trend", "steady"))
        },
        discussion = json.obj("discussion")?.let {
            WeatherDiscussion(it.optString("office"), it.instant("issuedAt"), it.optString("text"))
        },
        severe = json.obj("severe")?.let { s ->
            WeatherSevereOutlook(
                days = s.arr("days").objects { d ->
                    WeatherOutlookDay(d.optInt("day"), d.str("category"), d.optString("name"), d.optInt("rank"))
                },
                tornadoPercent = s.int("tornadoPercent"),
                hailPercent = s.int("hailPercent"),
                windPercent = s.int("windPercent"),
                significantTornado = s.optBoolean("significantTornado"),
                significantHail = s.optBoolean("significantHail"),
                significantWind = s.optBoolean("significantWind"),
                issuedAt = s.instant("issuedAt"),
            )
        },
        problems = json.arr("problems").strings(),
    )

    fun mapInfo(json: JSONObject): WeatherMapInfo = WeatherMapInfo(
        home = json.obj("home")?.let {
            WeatherMapHome(it.optDouble("latitude"), it.optDouble("longitude"), it.optString("label"))
        },
        homeStation = json.str("homeStation"),
        station = json.str("station"),
        stations = json.arr("stations").objects {
            WeatherRadarSite(it.optString("id"), it.optString("name"), it.optDouble("latitude"), it.optDouble("longitude"))
        },
        layers = json.arr("layers").objects {
            WeatherMapLayer(
                id = it.optString("id"),
                name = it.optString("name"),
                scope = it.optString("scope", "national"),
                description = it.optString("description"),
                times = it.arr("times").strings(),
            )
        },
        maximumRadarZoom = json.optInt("maximumRadarZoom", 12).takeIf { it > 0 } ?: 12,
        maximumBaseZoom = json.optInt("maximumBaseZoom", 16).takeIf { it > 0 } ?: 16,
        problems = json.arr("problems").strings(),
    )

    fun storms(json: JSONObject): WeatherStorms = WeatherStorms(
        cells = json.arr("cells").objects(::cell),
        warnings = json.arr("warnings").objects(::warning),
        outlook = json.obj("outlook")?.arr("areas").objects { a ->
            WeatherOutlookArea(
                label = a.optString("label"),
                name = a.optString("name"),
                rank = a.optInt("rank"),
                fill = a.str("fill"),
                stroke = a.str("stroke"),
                polygons = polygons(a.arr("polygons")),
            )
        },
        problems = json.arr("problems").strings(),
    )

    private fun location(j: JSONObject) = WeatherLocation(
        latitude = j.optDouble("latitude"),
        longitude = j.optDouble("longitude"),
        label = j.optString("label"),
        city = j.str("city"),
        state = j.str("state"),
        timeZone = j.str("timeZone"),
        radarStation = j.str("radarStation"),
        forecastOffice = j.str("forecastOffice"),
    )

    private fun current(j: JSONObject) = WeatherCurrent(
        observedAt = j.instant("observedAt"),
        station = j.optString("station"),
        stationName = j.optString("stationName"),
        description = j.optString("description"),
        icon = j.str("icon"),
        isDaytime = j.bool("isDaytime"),
        temperatureF = j.optInt("temperatureF"),
        feelsLikeF = j.int("feelsLikeF"),
        dewpointF = j.int("dewpointF"),
        humidityPercent = j.int("humidityPercent"),
        windMph = j.int("windMph"),
        windGustMph = j.int("windGustMph"),
        windDirection = j.str("windDirection"),
        pressureInHg = j.dbl("pressureInHg"),
        visibilityMiles = j.dbl("visibilityMiles"),
    )

    private fun period(j: JSONObject) = WeatherPeriod(
        name = j.optString("name"),
        startTime = j.instant("startTime") ?: Instant.EPOCH,
        endTime = j.instant("endTime"),
        isDaytime = j.optBoolean("isDaytime"),
        temperatureF = j.int("temperatureF"),
        precipitationChance = j.int("precipitationChance"),
        windSpeed = j.str("windSpeed"),
        windDirection = j.str("windDirection"),
        icon = j.str("icon"),
        shortForecast = j.optString("shortForecast"),
        detailedForecast = j.optString("detailedForecast"),
    )

    private fun hour(j: JSONObject) = WeatherHour(
        startTime = j.instant("startTime") ?: Instant.EPOCH,
        isDaytime = j.optBoolean("isDaytime"),
        temperatureF = j.int("temperatureF"),
        precipitationChance = j.int("precipitationChance"),
        humidityPercent = j.int("humidityPercent"),
        windSpeed = j.str("windSpeed"),
        windDirection = j.str("windDirection"),
        icon = j.str("icon"),
        shortForecast = j.optString("shortForecast"),
    )

    private fun alert(j: JSONObject) = WeatherAlert(
        id = j.optString("id"),
        event = j.optString("event"),
        severity = j.optString("severity", "Unknown"),
        urgency = j.optString("urgency", "Unknown"),
        headline = j.optString("headline"),
        description = j.optString("description"),
        instruction = j.optString("instruction"),
        ends = j.instant("ends"),
        areaDescription = j.optString("areaDescription"),
        sender = j.optString("sender"),
    )

    private fun cell(j: JSONObject) = WeatherStormCell(
        id = j.optString("id"),
        radar = j.optString("radar"),
        stormId = j.optString("stormId"),
        latitude = j.optDouble("latitude"),
        longitude = j.optDouble("longitude"),
        threat = j.optInt("threat"),
        tornadic = j.optBoolean("tornadic"),
        rotation = j.optBoolean("rotation"),
        mesoStrength = j.str("mesoStrength"),
        hailProbability = j.int("hailProbability"),
        severeHailProbability = j.int("severeHailProbability"),
        maxHailInches = j.dbl("maxHailInches"),
        vil = j.int("vil"),
        maxDbz = j.int("maxDbz"),
        topKft = j.dbl("topKft"),
        headingDegrees = j.int("headingDegrees"),
        headingCompass = j.str("headingCompass"),
        speedMph = j.int("speedMph"),
        track = j.arr("track").objects { WeatherTrackPoint(it.optInt("minutes"), it.optDouble("latitude"), it.optDouble("longitude")) },
        distanceMiles = j.dbl("distanceMiles"),
        bearingCompass = j.str("bearingCompass"),
        towardHome = j.optBoolean("towardHome"),
        closestMiles = j.dbl("closestMiles"),
        etaMinutes = j.int("etaMinutes"),
        validAt = j.instant("validAt"),
    )

    private fun warning(j: JSONObject) = WeatherWarningArea(
        id = j.optString("id"),
        phenomena = j.optString("phenomena"),
        significance = j.optString("significance"),
        name = j.optString("name"),
        office = j.str("office"),
        issued = j.instant("issued"),
        expires = j.instant("expires"),
        hailInches = j.dbl("hailInches"),
        windMph = j.dbl("windMph"),
        tornadoTag = j.str("tornadoTag"),
        damageTag = j.str("damageTag"),
        emergency = j.optBoolean("emergency"),
        particularlyDangerous = j.optBoolean("particularlyDangerous"),
        containsHome = j.optBoolean("containsHome"),
        polygons = polygons(j.arr("polygons")),
    )

    // [[[ [lon, lat], ... ], ...ring ], ...polygon ] - GeoJSON order, longitude first.
    internal fun polygons(array: JSONArray?): GeoPolygons {
        if (array == null) return emptyList()
        return (0 until array.length()).mapNotNull { p ->
            val rings = array.optJSONArray(p) ?: return@mapNotNull null
            (0 until rings.length()).mapNotNull { r ->
                val ring = rings.optJSONArray(r) ?: return@mapNotNull null
                (0 until ring.length()).mapNotNull { i ->
                    val pair = ring.optJSONArray(i) ?: return@mapNotNull null
                    if (pair.length() < 2) null else GeoPoint(latitude = pair.optDouble(1), longitude = pair.optDouble(0))
                }.takeIf { it.size >= 3 }
            }.takeIf { it.isNotEmpty() }
        }
    }

    private fun JSONObject.str(key: String): String? =
        if (!has(key) || isNull(key)) null else optString(key).takeIf { it.isNotBlank() }

    private fun JSONObject.int(key: String): Int? =
        if (!has(key) || isNull(key)) null else optDouble(key).takeUnless { it.isNaN() }?.let { Math.round(it).toInt() }

    private fun JSONObject.dbl(key: String): Double? =
        if (!has(key) || isNull(key)) null else optDouble(key).takeUnless { it.isNaN() }

    private fun JSONObject.bool(key: String): Boolean? =
        if (!has(key) || isNull(key)) null else optBoolean(key)

    private fun JSONObject.obj(key: String): JSONObject? =
        if (!has(key) || isNull(key)) null else optJSONObject(key)

    private fun JSONObject.arr(key: String): JSONArray? =
        if (!has(key) || isNull(key)) null else optJSONArray(key)

    private fun JSONObject.instant(key: String): Instant? =
        str(key)?.let { runCatching { ServerTimestamp.parse(it) }.getOrNull() }

    private fun JSONArray?.strings(): List<String> {
        val array = this ?: return emptyList()
        return (0 until array.length()).mapNotNull { array.optString(it).takeIf(String::isNotBlank) }
    }

    private fun <T> JSONArray?.objects(parse: (JSONObject) -> T): List<T> {
        val array = this ?: return emptyList()
        return (0 until array.length()).mapNotNull { array.optJSONObject(it)?.let(parse) }
    }
}
