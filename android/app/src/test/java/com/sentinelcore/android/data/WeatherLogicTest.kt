package com.sentinelcore.android.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId

// The Weather tab's rules, each a port of a weather.js function - the same answers as the
// dashboard for the same forecast.
class WeatherLogicTest {

    private val chicago = ZoneId.of("America/Chicago")

    private fun period(name: String, start: String, day: Boolean, temp: Int?, chance: Int? = null) = WeatherPeriod(
        name = name,
        startTime = Instant.parse(start),
        endTime = null,
        isDaytime = day,
        temperatureF = temp,
        precipitationChance = chance,
        windSpeed = null,
        windDirection = null,
        icon = null,
        shortForecast = name,
        detailedForecast = "",
    )

    private fun hour(start: Instant, chance: Int?, forecast: String = "Showers") = WeatherHour(
        startTime = start,
        isDaytime = true,
        temperatureF = 70,
        precipitationChance = chance,
        humidityPercent = null,
        windSpeed = null,
        windDirection = null,
        icon = null,
        shortForecast = forecast,
    )

    private fun cell(
        id: String,
        threat: Int = 0,
        toward: Boolean = false,
        eta: Int? = null,
        distance: Double? = 50.0,
    ) = WeatherStormCell(
        id = id, radar = "KLSX", stormId = id, latitude = 38.0, longitude = -90.0, threat = threat,
        tornadic = false, rotation = false, mesoStrength = null, hailProbability = null, severeHailProbability = null,
        maxHailInches = null, vil = null, maxDbz = null, topKft = null, headingDegrees = null, headingCompass = null,
        speedMph = null, track = emptyList(), distanceMiles = distance, bearingCompass = "W", towardHome = toward,
        closestMiles = null, etaMinutes = eta, validAt = null,
    )

    private fun warning(id: String, phenomena: String, home: Boolean) = WeatherWarningArea(
        id = id, phenomena = phenomena, significance = "W", name = "$phenomena warning", office = null, issued = null,
        expires = null, hailInches = null, windMph = null, tornadoTag = null, damageTag = null, emergency = false,
        particularlyDangerous = false, containsHome = home, polygons = emptyList(),
    )

    @Test
    fun `half-day periods group into one card per local calendar day`() {
        val periods = listOf(
            // Starting the evening: tonight alone, then day and night pairs.
            period("Tonight", "2026-09-29T23:00:00Z", day = false, temp = 60),
            period("Wednesday", "2026-09-30T11:00:00Z", day = true, temp = 80),
            period("Wednesday Night", "2026-09-30T23:00:00Z", day = false, temp = 62),
        )

        val days = WeatherLogic.groupDays(periods, chicago)

        assertEquals(2, days.size)
        assertEquals(LocalDate.of(2026, 9, 29), days[0].date)
        assertNull(days[0].day)
        assertEquals("Tonight", days[0].lead.name)
        assertEquals(60, days[0].low)
        assertEquals(80, days[1].high)
        assertEquals(62, days[1].low)
        assertEquals("Wednesday", days[1].lead.name)
    }

    @Test
    fun `grouping follows the location's zone, not UTC`() {
        // 03:00 UTC on the 30th is still the evening of the 29th in Chicago.
        val periods = listOf(
            period("Today", "2026-09-29T15:00:00Z", day = true, temp = 80),
            period("Tonight", "2026-09-30T03:00:00Z", day = false, temp = 60),
        )
        assertEquals(1, WeatherLogic.groupDays(periods, chicago).size)
        assertEquals(2, WeatherLogic.groupDays(periods, ZoneId.of("UTC")).size)
    }

    @Test
    fun `a day's chance is the wetter of its two halves`() {
        val day = WeatherDay(
            LocalDate.of(2026, 9, 30),
            period("Wed", "2026-09-30T11:00:00Z", true, 80, chance = 20),
            period("Wed Night", "2026-09-30T23:00:00Z", false, 60, chance = 70),
        )
        assertEquals(70, day.chance)
    }

    @Test
    fun `the range bar places the day within the week`() {
        val periods = listOf(
            period("A", "2026-09-30T11:00:00Z", true, 90),
            period("B", "2026-09-30T23:00:00Z", false, 50),
            period("C", "2026-10-01T11:00:00Z", true, 70),
            period("D", "2026-10-01T23:00:00Z", false, 60),
        )
        val day = WeatherLogic.groupDays(periods, ZoneId.of("UTC"))[1]
        val (start, width) = WeatherLogic.rangeFractions(day, periods)!!
        assertEquals(0.25f, start, 0.001f)
        assertEquals(0.25f, width, 0.001f)
    }

    @Test
    fun `next precipitation reads the next twelve hours like the dashboard`() {
        val start = Instant.parse("2026-09-29T15:00:00Z")
        val label: (Instant) -> String = { "H${(it.epochSecond - start.epochSecond) / 3600}" }
        fun hours(vararg chances: Int?) = chances.mapIndexed { i, c -> hour(start.plusSeconds(3600L * i), c) }

        assertEquals("Rain likely until around H2.", WeatherLogic.nextPrecipText(hours(80, 60, 10), label))
        assertEquals("Rain likely for the next 12 hours.", WeatherLogic.nextPrecipText(hours(80, 60, 55), label))
        assertEquals("Rain likely from around H1 (50%).", WeatherLogic.nextPrecipText(hours(10, 50), label))
        assertEquals("Slight chance of rain around H1 (30%).", WeatherLogic.nextPrecipText(hours(0, 30, 20), label))
        assertEquals("No rain expected in the next 12 hours.", WeatherLogic.nextPrecipText(hours(0, 10, null), label))
        assertNull(WeatherLogic.nextPrecipText(emptyList(), label))
    }

    @Test
    fun `precipitation kind comes from the short forecast`() {
        val at = Instant.EPOCH
        assertEquals("Storms", WeatherLogic.precipKind(hour(at, 80, "Showers And Thunderstorms")))
        assertEquals("Snow", WeatherLogic.precipKind(hour(at, 80, "Snow Flurries")))
        assertEquals("Wintry mix", WeatherLogic.precipKind(hour(at, 80, "Freezing Rain")))
        assertEquals("Drizzle", WeatherLogic.precipKind(hour(at, 80, "Patchy Drizzle")))
        assertEquals("Rain", WeatherLogic.precipKind(hour(at, 80, "Rain Showers")))
    }

    @Test
    fun `moon phase at a known full and new moon`() {
        val full = WeatherLogic.moonPhase(Instant.parse("2026-09-26T16:49:00Z"))
        assertEquals("Full moon", full.name)
        assertTrue(full.illumination >= 97)

        val new = WeatherLogic.moonPhase(Instant.parse("2000-01-06T18:14:00Z"))
        assertEquals("New moon", new.name)
        assertEquals(0, new.illumination)
        assertEquals(15, new.daysToFull)
    }

    @Test
    fun `small formatting rules`() {
        assertEquals("11h 51m", WeatherLogic.daylightText(WeatherSun(Instant.parse("2026-09-29T12:01:00Z"), Instant.parse("2026-09-29T23:52:00Z"))))
        assertNull(WeatherLogic.daylightText(WeatherSun(null, null)))
        assertEquals(" (muggy)", WeatherLogic.dewComfort(66))
        assertEquals(" (dry)", WeatherLogic.dewComfort(20))
        assertEquals("", WeatherLogic.dewComfort(50))
        assertEquals("—", WeatherLogic.inches(null))
        assertEquals("0\"", WeatherLogic.inches(0.0))
        assertEquals("trace", WeatherLogic.inches(0.004))
        assertEquals("0.42\"", WeatherLogic.inches(0.42))
        assertEquals("1.5\"", WeatherLogic.inches(1.46, 1))
        assertEquals("2 of 5 · slight", WeatherLogic.outlookScale("SLGT"))
        assertEquals("no severe storms expected", WeatherLogic.outlookScale(null))
        assertEquals("↓", WeatherLogic.pressureArrow("falling"))
        assertEquals("🌙", WeatherLogic.glyph("skc", false))
        assertEquals("☀️", WeatherLogic.glyph("skc", null))
        assertEquals("🌡️", WeatherLogic.glyph("nonsense", true))
    }

    @Test
    fun `wind text reads calm, and direction when known`() {
        val base = WeatherCurrent(null, "K", "K", "", null, null, 70, null, null, null, 0, null, null, null, null)
        assertEquals("calm", WeatherLogic.windText(base))
        assertEquals("SW 12 mph", WeatherLogic.windText(base.copy(windMph = 12, windDirection = "SW")))
        assertEquals("12 mph", WeatherLogic.windText(base.copy(windMph = 12)))
        assertNull(WeatherLogic.windText(base.copy(windMph = null)))
    }

    @Test
    fun `the banner and near-home list order storms like the dashboard`() {
        val storms = WeatherStorms(
            cells = listOf(
                cell("far", threat = 3, distance = 150.0),
                cell("weak-incoming", threat = 0, toward = true, eta = 20, distance = 30.0),
                cell("strong-incoming", threat = 2, toward = true, eta = 50, distance = 60.0),
                cell("near", threat = 1, distance = 10.0),
                cell("no-eta", threat = 3, toward = true, eta = null, distance = 5.0),
            ),
            warnings = listOf(
                warning("to", "TO", home = true),
                warning("fl", "FL", home = true),
                warning("sv-away", "SV", home = false),
            ),
        )

        assertEquals(listOf("strong-incoming", "weak-incoming"), WeatherLogic.incomingCells(storms).map { it.id })
        assertEquals(listOf("to"), WeatherLogic.coveringWarnings(storms).map { it.id })
        assertTrue(WeatherLogic.bannerIsSevere(storms))
        assertEquals(
            listOf("no-eta", "strong-incoming", "weak-incoming", "near"),
            WeatherLogic.nearbyCells(storms).map { it.id },
        )
        assertFalse(WeatherLogic.bannerIsSevere(storms.copy(warnings = emptyList())))
    }

    @Test
    fun `merged storms keep the view's and add home's, by id`() {
        val view = WeatherStorms(cells = listOf(cell("a"), cell("b")), warnings = listOf(warning("w1", "SV", false)))
        val near = WeatherStorms(cells = listOf(cell("b", threat = 2), cell("c")), warnings = listOf(warning("w2", "TO", true)))

        val merged = WeatherLogic.mergeStorms(view, near)

        assertEquals(listOf("a", "b", "c"), merged.cells.map { it.id })
        assertEquals(2, merged.cells.first { it.id == "b" }.threat)
        assertEquals(listOf("w1", "w2"), merged.warnings.map { it.id })
        assertEquals(view, WeatherLogic.mergeStorms(view, null))
    }

    @Test
    fun `weak cells only show when zoomed in, heading home or selected`() {
        val weak = cell("x")
        assertFalse(WeatherLogic.cellVisible(weak, 8.0, null))
        assertTrue(WeatherLogic.cellVisible(weak, 9.0, null))
        assertTrue(WeatherLogic.cellVisible(weak, 8.0, "x"))
        assertTrue(WeatherLogic.cellVisible(weak.copy(towardHome = true), 5.0, null))
        assertTrue(WeatherLogic.cellVisible(cell("y", threat = 1), 5.0, null))
    }

    @Test
    fun `cell titles and hazards`() {
        val base = cell("c")
        assertEquals("Possible tornado", WeatherLogic.cellTitle(base.copy(tornadic = true, rotation = true)))
        assertEquals("Rotating storm", WeatherLogic.cellTitle(base.copy(rotation = true)))
        assertEquals("Severe hail storm", WeatherLogic.cellTitle(base.copy(threat = 2)))
        assertEquals("Strong storm", WeatherLogic.cellTitle(base.copy(threat = 1)))
        assertEquals("Storm", WeatherLogic.cellTitle(base))
        assertEquals(
            "tornado vortex signature · mesocyclone (rank 6) · hail to 1.50\"",
            WeatherLogic.cellHazards(base.copy(tornadic = true, rotation = true, mesoStrength = "6", maxHailInches = 1.5)),
        )
        assertEquals("40% hail", WeatherLogic.cellHazards(base.copy(hailProbability = 40)))
    }

    @Test
    fun `warning tags read like the dashboard's`() {
        val tagged = warning("w", "SV", false).copy(tornadoTag = "POSSIBLE", hailInches = 1.75, windMph = 70.0, damageTag = "CONSIDERABLE")
        assertEquals("tornado possible · hail 1.75\" · wind 70 mph · considerable damage threat", WeatherLogic.warningTags(tagged))
        assertEquals(0xFFFF2D3D, WeatherLogic.warningColor("TO"))
        assertTrue(WeatherLogic.warningDrawOrder("TO") > WeatherLogic.warningDrawOrder("SV"))
    }

    @Test
    fun `current layer falls back to reflectivity then the first`() {
        val layers = listOf(
            WeatherMapLayer("precip-type", "Precip type", "national", "", emptyList()),
            WeatherMapLayer("reflectivity", "Reflectivity", "national", "", emptyList()),
        )
        val info = WeatherMapInfo(null, null, null, emptyList(), layers, 12, 16)
        assertEquals("precip-type", WeatherLogic.currentLayer(info, "precip-type")?.id)
        assertEquals("reflectivity", WeatherLogic.currentLayer(info, "gone")?.id)
        assertEquals("precip-type", WeatherLogic.currentLayer(info.copy(layers = layers.take(1)), "gone")?.id)
        assertNull(WeatherLogic.currentLayer(null, "reflectivity"))
    }

    @Test
    fun `tile and storm paths match the server's routes`() {
        assertEquals("/api/weather/map/tile/base/8/64/97", WeatherLogic.tilePath("base", 8, 64, 97))
        assertEquals(
            "/api/weather/map/tile/velocity/9/129/195?station=KLSX&time=2026-09-29T14%3A50%3A00.000Z",
            WeatherLogic.tilePath("velocity", 9, 129, 195, station = "KLSX", time = "2026-09-29T14:50:00.000Z"),
        )
        assertEquals(
            "/api/weather/storms?west=-92.700&south=36.600&east=-87.700&north=40.600",
            WeatherLogic.stormsPath(-92.7, 36.6, -87.7, 40.6),
        )
        val station = WeatherMapLayer("velocity", "Velocity", "station", "", emptyList())
        assertEquals("/api/weather/map/legend/velocity?station=KLSX", WeatherLogic.legendPath(station, "KLSX"))
        assertEquals("/api/weather/map/legend/velocity", WeatherLogic.legendPath(station.copy(scope = "national"), "KLSX"))
    }

    @Test
    fun `point in polygon honours holes`() {
        fun ring(w: Double, s: Double, e: Double, n: Double) =
            listOf(GeoPoint(s, w), GeoPoint(s, e), GeoPoint(n, e), GeoPoint(n, w), GeoPoint(s, w))
        val donut: GeoPolygons = listOf(listOf(ring(-91.0, 38.0, -89.0, 40.0), ring(-90.5, 38.5, -89.5, 39.5)))
        assertTrue(WeatherLogic.polygonsContain(donut, GeoPoint(38.2, -90.0)))
        assertFalse(WeatherLogic.polygonsContain(donut, GeoPoint(39.0, -90.0)))
        assertFalse(WeatherLogic.polygonsContain(donut, GeoPoint(41.0, -90.0)))
    }

    @Test
    fun `mercator round-trips and distances are sane`() {
        assertEquals(0.5, Mercator.x(0.0), 1e-12)
        assertEquals(0.5, Mercator.y(0.0), 1e-12)
        assertEquals(-90.1994, Mercator.longitude(Mercator.x(-90.1994)), 1e-9)
        assertEquals(38.627, Mercator.latitude(Mercator.y(38.627)), 1e-9)
        // St. Louis to Kansas City, about 238 miles as the crow flies.
        val miles = WeatherLogic.milesBetween(GeoPoint(38.627, -90.1994), GeoPoint(39.0997, -94.5786))
        assertEquals(238.0, miles, 5.0)
    }
}
