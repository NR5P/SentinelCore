package com.sentinelcore.android.data

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Instant
import java.time.ZoneId

// Fed JSON in the shape core/SentinelCore.Web/WeatherService.cs and WeatherMapService.cs send
// (camelCase, System.Text.Json's DateTimeOffset format), so a drift between the server's DTOs and
// these parsers shows up here rather than as an empty Weather tab.
class WeatherParsingTest {

    private val report = """
        {
          "state": "ok", "message": null,
          "location": { "latitude": 38.6270, "longitude": -90.1994, "label": "Home", "city": "St. Louis", "state": "MO",
                        "timeZone": "America/Chicago", "radarStation": "KLSX", "forecastOffice": "LSX" },
          "current": { "observedAt": "2026-09-29T14:53:00+00:00", "station": "KSTL", "stationName": "St. Louis Lambert",
                       "description": "Partly Cloudy", "icon": "sct", "isDaytime": true, "temperatureF": 72, "feelsLikeF": 74,
                       "dewpointF": 66, "humidityPercent": 81, "windMph": 9, "windGustMph": null, "windDirection": "SSW",
                       "pressureInHg": 29.94, "visibilityMiles": 10 },
          "daily": [
            { "name": "Today", "startTime": "2026-09-29T10:00:00-05:00", "endTime": "2026-09-29T18:00:00-05:00", "isDaytime": true,
              "temperatureF": 84, "precipitationChance": 20, "windSpeed": "5 to 10 mph", "windDirection": "S", "icon": "tsra_sct",
              "shortForecast": "Chance Showers And Thunderstorms", "detailedForecast": "A chance of storms after 1pm." },
            { "name": "Tonight", "startTime": "2026-09-29T18:00:00-05:00", "endTime": "2026-09-30T06:00:00-05:00", "isDaytime": false,
              "temperatureF": 66, "precipitationChance": 60, "windSpeed": "5 mph", "windDirection": "S", "icon": "tsra",
              "shortForecast": "Showers And Thunderstorms Likely", "detailedForecast": "Storms likely." }
          ],
          "hourly": [
            { "startTime": "2026-09-29T10:00:00-05:00", "endTime": "2026-09-29T11:00:00-05:00", "isDaytime": true, "temperatureF": 75,
              "precipitationChance": 10, "humidityPercent": 70, "windSpeed": "8 mph", "windDirection": "S", "icon": "sct",
              "shortForecast": "Partly Sunny" }
          ],
          "alerts": [
            { "id": "urn:oid:1", "event": "Tornado Warning", "severity": "Extreme", "urgency": "Immediate", "certainty": "Observed",
              "headline": "Tornado Warning issued", "description": "AT 2:52 PM...", "instruction": "TAKE COVER NOW!",
              "onset": null, "ends": "2026-09-29T15:30:00-05:00", "expires": null, "areaDescription": "St. Louis, MO",
              "sender": "NWS St. Louis MO", "messageType": "Alert", "references": [] }
          ],
          "sun": { "sunrise": "2026-09-29T12:01:00+00:00", "sunset": "2026-09-29T23:52:00+00:00" },
          "radar": { "station": "KLSX" },
          "forecastUpdatedAt": "2026-09-29T14:00:00+00:00",
          "fetchedAt": "2026-09-29T15:00:12.345+00:00",
          "stale": false,
          "problems": ["Couldn't read the alerts."]
        }
    """.trimIndent()

    @Test
    fun `a full report parses every field the tab reads`() {
        val parsed = WeatherJson.report(JSONObject(report))

        assertEquals(WeatherState.OK, parsed.state)
        assertEquals("Home", parsed.location?.displayName)
        assertEquals("St. Louis, MO", parsed.location?.nearest)
        assertEquals(ZoneId.of("America/Chicago"), parsed.zone)
        assertEquals("LSX", parsed.location?.forecastOffice)

        val current = parsed.current!!
        assertEquals(72, current.temperatureF)
        assertEquals(74, current.feelsLikeF)
        assertNull(current.windGustMph)
        assertEquals(29.94, current.pressureInHg!!, 0.0001)
        assertEquals(10.0, current.visibilityMiles!!, 0.0001)
        assertEquals(true, current.isDaytime)
        assertEquals(Instant.parse("2026-09-29T14:53:00Z"), current.observedAt)

        assertEquals(2, parsed.daily.size)
        assertEquals(Instant.parse("2026-09-29T15:00:00Z"), parsed.daily[0].startTime)
        assertFalse(parsed.daily[1].isDaytime)
        assertEquals(60, parsed.daily[1].precipitationChance)

        assertEquals(1, parsed.hourly.size)
        assertEquals(70, parsed.hourly[0].humidityPercent)

        val alert = parsed.alerts.single()
        assertTrue(alert.isSevere)
        assertEquals("TAKE COVER NOW!", alert.instruction)
        assertEquals(Instant.parse("2026-09-29T20:30:00Z"), alert.ends)

        assertEquals("KLSX", parsed.radarStation)
        assertEquals(Instant.parse("2026-09-29T15:00:12.345Z"), parsed.fetchedAt)
        assertEquals(listOf("Couldn't read the alerts."), parsed.problems)
    }

    @Test
    fun `a report that only explains itself still parses`() {
        val parsed = WeatherJson.report(
            JSONObject("""{ "state": "unconfigured", "message": "Set a location first.", "daily": [], "hourly": [], "alerts": [], "problems": [], "fetchedAt": "2026-09-29T15:00:00+00:00", "stale": false }"""),
        )

        assertEquals(WeatherState.UNCONFIGURED, parsed.state)
        assertEquals("Set a location first.", parsed.message)
        assertNull(parsed.location)
        assertNull(parsed.current)
        assertTrue(parsed.daily.isEmpty())
        assertEquals(ZoneId.systemDefault(), parsed.zone)
    }

    @Test
    fun `an unknown time zone falls back to the phone's rather than failing`() {
        val parsed = WeatherJson.report(
            JSONObject("""{ "state": "ok", "location": { "latitude": 1, "longitude": 2, "label": "", "timeZone": "Mars/Olympus" } }"""),
        )
        assertEquals(ZoneId.systemDefault(), parsed.zone)
        assertEquals("This location", parsed.location?.displayName)
    }

    @Test
    fun `details parse totals, hours, pressure, discussion and the severe outlook`() {
        val parsed = WeatherJson.details(
            JSONObject(
                """
                {
                  "state": "ok",
                  "totals": { "rainNext24HoursInches": 0.42, "rainNext7DaysInches": 1.3, "snowNext24HoursInches": 0,
                              "snowNext7DaysInches": 0, "iceNext7DaysInches": 0, "thunderChanceNext24Hours": 60, "thunderChanceNext7Days": 70 },
                  "hours": [ { "startTime": "2026-09-29T10:00:00-05:00", "feelsLikeF": 77, "gustMph": 21, "skyCoverPercent": 55,
                               "rainInches": 0.05, "snowInches": 0 } ],
                  "pressure": { "changeInHgPer3Hours": -0.07, "trend": "falling" },
                  "discussion": { "office": "LSX", "issuedAt": "2026-09-29T13:30:00+00:00", "text": ".SHORT TERM..." },
                  "severe": { "days": [ { "day": 1, "category": "SLGT", "name": "Slight Risk", "rank": 3, "color": "#f6f67f" },
                                        { "day": 2, "category": null, "name": "", "rank": 0, "color": null } ],
                              "tornadoPercent": 5, "hailPercent": 15, "windPercent": 15,
                              "significantTornado": false, "significantHail": true, "significantWind": false,
                              "issuedAt": "2026-09-29T12:50:00+00:00" },
                  "fetchedAt": "2026-09-29T15:00:00+00:00",
                  "problems": []
                }
                """.trimIndent(),
            ),
        )

        assertEquals(0.42, parsed.totals!!.rainNext24HoursInches, 0.0001)
        assertEquals(70, parsed.totals!!.thunderChanceNext7Days)
        assertEquals(21, parsed.hours.single().gustMph)
        assertEquals(Instant.parse("2026-09-29T15:00:00Z"), parsed.hours.single().startTime)
        assertEquals("falling", parsed.pressure?.trend)
        assertEquals("LSX", parsed.discussion?.office)
        val severe = parsed.severe!!
        assertEquals("SLGT", severe.days[0].category)
        assertNull(severe.days[1].category)
        assertTrue(severe.significantHail)
        assertEquals(5, severe.tornadoPercent)
    }

    @Test
    fun `map info parses stations, layers and their frame times`() {
        val parsed = WeatherJson.mapInfo(
            JSONObject(
                """
                {
                  "home": { "latitude": 38.6, "longitude": -90.2, "label": "Home" },
                  "homeStation": "KLSX", "station": "KLSX",
                  "stations": [ { "id": "KLSX", "name": "St. Louis", "latitude": 38.69, "longitude": -90.68 } ],
                  "layers": [
                    { "id": "reflectivity", "name": "Reflectivity", "scope": "national", "description": "Rain.",
                      "times": ["2026-09-29T14:50:00.000Z", "2026-09-29T14:56:00.000Z"] },
                    { "id": "velocity", "name": "Velocity", "scope": "station", "description": "Wind.", "times": [] }
                  ],
                  "maximumRadarZoom": 12, "maximumBaseZoom": 16, "problems": []
                }
                """.trimIndent(),
            ),
        )

        assertEquals("KLSX", parsed.station)
        assertEquals(1, parsed.stations.size)
        assertEquals(2, parsed.layers[0].times.size)
        assertEquals("2026-09-29T14:50:00.000Z", parsed.layers[0].times[0])
        assertTrue(parsed.layers[1].isStation)
        assertEquals(12, parsed.maximumRadarZoom)
    }

    @Test
    fun `storms parse cells, tracks and warning polygons in GeoJSON order`() {
        val parsed = WeatherJson.storms(
            JSONObject(
                """
                {
                  "cells": [ { "id": "KLSX-Q4", "radar": "KLSX", "stormId": "Q4", "latitude": 38.9, "longitude": -90.9,
                               "threat": 3, "tornadic": true, "rotation": true, "mesoStrength": "6", "hailProbability": 70,
                               "severeHailProbability": 30, "maxHailInches": 1.5, "vil": 55, "maxDbz": 62, "topKft": 45.2,
                               "headingDegrees": 60, "headingCompass": "ENE", "speedMph": 35,
                               "track": [ { "minutes": 15, "latitude": 38.95, "longitude": -90.7 } ],
                               "distanceMiles": 25.4, "bearingCompass": "WNW", "towardHome": true, "closestMiles": 0.6,
                               "etaMinutes": 40, "validAt": "2026-09-29T14:55:00+00:00" } ],
                  "warnings": [ { "id": "w1", "phenomena": "TO", "significance": "W", "name": "Tornado Warning", "office": "LSX",
                                  "issued": "2026-09-29T14:50:00+00:00", "expires": "2026-09-29T15:30:00+00:00",
                                  "hailInches": null, "windMph": null, "tornadoTag": "RADAR INDICATED", "damageTag": null,
                                  "emergency": false, "particularlyDangerous": false, "containsHome": true,
                                  "polygons": [ [ [ [-91, 38], [-90, 38], [-90, 39], [-91, 39], [-91, 38] ] ] ] } ],
                  "outlook": { "areas": [ { "label": "SLGT", "name": "Slight", "rank": 3, "fill": "#f6f67f", "stroke": "#dddd00",
                                             "polygons": [] } ], "issuedAt": null },
                  "fetchedAt": "2026-09-29T15:00:00+00:00", "problems": []
                }
                """.trimIndent(),
            ),
        )

        val cell = parsed.cells.single()
        assertTrue(cell.tornadic)
        assertEquals(40, cell.etaMinutes)
        assertEquals(15, cell.track.single().minutes)
        val warning = parsed.warnings.single()
        assertEquals(38.0, warning.polygons[0][0][0].latitude, 0.0)
        assertEquals(-91.0, warning.polygons[0][0][0].longitude, 0.0)
        assertEquals("SLGT", parsed.outlook.single().label)
    }
}
