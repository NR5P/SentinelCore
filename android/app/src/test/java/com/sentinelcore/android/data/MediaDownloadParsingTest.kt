package com.sentinelcore.android.data

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

// Fed the sample JSON from core/docs/MOVIE_DOWNLOADS.md (and trimmed-down answers with fields
// missing), so a drift between the contract and these parsers shows up here rather than as a
// blank screen or a crash on an older server.
class MediaDownloadParsingTest {

    private val statusJson = """
        { "enabled": true, "canDownload": true, "canManageSites": true, "tmdbConfigured": true,
          "movieLibrary": true, "showLibrary": false,
          "status": { "enabled": true, "services": "running", "idleMinutes": 5,
                      "vpn": { "state": "connected", "required": true, "ip": "185.65.134.1",
                               "location": "Stockholm, SE", "country": "SE", "provider": "AS39351 31173 Services AB",
                               "checkedAt": "2026-09-29T10:00:00Z", "error": null } } }
    """.trimIndent()

    private val releaseJson = """
        { "id": "r1", "title": "Dune.Part.Two.2024.1080p.WEB-DL.DDP5.1.H.264-FLUX", "site": "TheSite",
          "sizeBytes": 6400000000, "seeders": 812, "leechers": 40, "publishedAt": "2024-05-14T00:00:00Z",
          "quality": "1080p", "codec": "H.264", "source": "WEB-DL", "hdr": false,
          "season": null, "episode": null, "seasonPack": false, "matches": true,
          "warning": "Filmed in a cinema - poor picture and sound." }
    """.trimIndent()

    private val downloadJson = """
        { "id": "d1", "kind": "episode", "title": "Bluey", "year": 2018, "tmdbId": 82728,
          "season": 2, "episode": 5, "subtitle": "S02E05",
          "posterUrl": "https://image.tmdb.org/t/p/w342/b.jpg", "releaseTitle": "Bluey.S02E05.1080p", "site": "TheSite",
          "sizeBytes": 400000000, "state": "downloading",
          "progress": 0.42, "downloadedBytes": 168000000, "speedBytesPerSecond": 3100000, "etaSeconds": 75,
          "peers": 14, "error": "Looking for people sharing it...", "libraryId": "lib2", "itemId": null, "requestedBy": "sam",
          "createdAt": "2026-09-29T10:00:00Z", "completedAt": null, "finishedAt": null }
    """.trimIndent()

    @Test
    fun `status parses every contract field`() {
        val status = parseMediaDownloadStatus(JSONObject(statusJson))
        assertTrue(status.enabled)
        assertTrue(status.canDownload)
        assertTrue(status.canManageSites)
        assertTrue(status.tmdbConfigured)
        assertTrue(status.movieLibrary)
        assertFalse(status.showLibrary)
        val services = status.status!!
        assertEquals("running", services.services)
        assertEquals(5, services.idleMinutes)
        assertEquals("connected", services.vpn.state)
        assertEquals("185.65.134.1", services.vpn.ip)
        assertEquals("Stockholm, SE", services.vpn.location)
        assertEquals("AS39351 31173 Services AB", services.vpn.provider)
        assertNull(services.vpn.error)
    }

    @Test
    fun `a minimal status reads as off`() {
        val status = parseMediaDownloadStatus(JSONObject("{}"))
        assertFalse(status.enabled)
        assertFalse(status.canDownload)
        assertFalse(status.canManageSites)
        assertNull(status.status)
        val bare = parseMediaDownloadStatus(JSONObject("""{ "canDownload": true, "status": { "services": "stopped" } }"""))
        assertTrue(bare.canDownload)
        assertEquals("unknown", bare.status!!.vpn.state)
        assertEquals(5, bare.status!!.idleMinutes)
    }

    @Test
    fun `lookup parses results, and no TMDB key means no results`() {
        val lookup = parseMediaLookup(
            JSONObject(
                """{ "tmdbConfigured": true, "results": [ { "kind": "movie", "tmdbId": 693134, "title": "Dune: Part Two", "year": 2024,
                  "overview": "…", "posterPath": "/abc.jpg", "posterUrl": "https://image.tmdb.org/t/p/w342/abc.jpg",
                  "inLibrary": false, "itemId": null } ] }""",
            ),
        )
        assertTrue(lookup.tmdbConfigured)
        val result = lookup.results.single()
        assertEquals(693134L, result.tmdbId)
        assertEquals("Dune: Part Two", result.title)
        assertEquals(2024, result.year)
        assertEquals("/abc.jpg", result.posterPath)
        assertEquals("https://image.tmdb.org/t/p/w342/abc.jpg", result.posterUrl)
        assertFalse(result.inLibrary)
        assertNull(result.itemId)
        assertFalse(result.isShow)

        val none = parseMediaLookup(JSONObject("""{ "tmdbConfigured": false, "results": [] }"""))
        assertFalse(none.tmdbConfigured)
        assertTrue(none.results.isEmpty())

        val minimal = parseMediaLookup(JSONObject("""{ "results": [ { "tmdbId": 1, "title": "X" } ] }"""))
        assertEquals("movie", minimal.results.single().kind)
        assertNull(minimal.results.single().year)
    }

    @Test
    fun `a show parses its seasons and picks the first season with a gap`() {
        val show = parseMediaDownloadShow(
            JSONObject(
                """{ "tmdbId": 82728, "title": "Bluey", "year": 2018, "overview": "…", "posterPath": "/b.jpg", "posterUrl": null, "itemId": "s1",
                  "seasons": [
                    { "number": 1, "name": "Season 1", "episodeCount": 2, "ownedCount": 2,
                      "episodes": [ { "number": 1, "name": "Magic Xylophone", "airDate": "2018-10-01", "aired": true, "inLibrary": true },
                                    { "number": 2, "name": "Hospital", "airDate": "2018-10-02", "aired": true, "inLibrary": true } ] },
                    { "number": 2, "name": "Season 2", "episodeCount": 2, "ownedCount": 0,
                      "episodes": [ { "number": 1, "name": "Dance Mode", "airDate": "2020-03-17", "aired": true, "inLibrary": false },
                                    { "number": 2, "airDate": null, "aired": false, "inLibrary": false } ] },
                    { "number": 0, "name": "Specials", "episodeCount": 1, "ownedCount": 0,
                      "episodes": [ { "number": 1, "name": "Special", "aired": true, "inLibrary": false } ] } ] }""",
            ),
        )
        assertEquals(82728L, show.tmdbId)
        assertEquals("s1", show.itemId)
        assertNull(show.posterUrl)
        assertEquals(listOf(1, 2, 0), show.seasons.map { it.number })
        assertTrue(show.seasons[0].isComplete)
        assertEquals("Magic Xylophone", show.seasons[0].episodes[0].name)
        assertFalse(show.seasons[1].episodes[1].aired)
        assertNull(show.seasons[1].episodes[1].name)
        // Season 1 is all there; season 2 has an aired episode missing; specials don't count.
        assertEquals(2, MediaDownloadFormat.startingSeason(show))
        assertEquals("S1 ✓", MediaDownloadFormat.seasonTabLabel(show.seasons[0]))
        assertEquals("Specials", MediaDownloadFormat.seasonTabLabel(show.seasons[2]))

        // An episode missing `aired` counts as out; a season missing its name gets one.
        val bare = parseMediaDownloadShow(JSONObject("""{ "tmdbId": 5, "title": "T", "seasons": [ { "number": 3, "episodes": [ { "number": 1 } ] } ] }"""))
        assertEquals("Season 3", bare.seasons.single().name)
        assertEquals(1, bare.seasons.single().episodeCount)
        assertTrue(bare.seasons.single().episodes.single().aired)
        assertEquals(3, MediaDownloadFormat.startingSeason(bare))
    }

    @Test
    fun `a release parses every contract field`() {
        val release = parseMediaRelease(JSONObject(releaseJson))
        assertEquals("r1", release.id)
        assertEquals("TheSite", release.site)
        assertEquals(6_400_000_000L, release.sizeBytes)
        assertEquals(812, release.seeders)
        assertEquals(40, release.leechers)
        assertEquals("1080p", release.quality)
        assertEquals("H.264", release.codec)
        assertEquals("WEB-DL", release.source)
        assertFalse(release.hdr)
        assertNull(release.season)
        assertFalse(release.seasonPack)
        assertTrue(release.matches)
        assertEquals("Filmed in a cinema - poor picture and sound.", release.warning)
        assertEquals(listOf("1080p", "H.264", "WEB-DL"), release.chips)

        val minimal = parseMediaRelease(JSONObject("""{ "id": "r2", "title": "x", "hdr": true, "seasonPack": true, "quality": null }"""))
        assertNull(minimal.sizeBytes)
        assertNull(minimal.seeders)
        assertNull(minimal.quality)
        assertEquals(listOf("HDR", "whole season"), minimal.chips)
    }

    @Test
    fun `a search parses its phase and releases`() {
        val search = parseMediaReleaseSearch(
            JSONObject(
                """{ "id": "s1", "phase": "done", "message": null, "error": null,
                  "kind": "movie", "title": "Dune: Part Two", "year": 2024, "season": null, "episode": null,
                  "results": [ $releaseJson ], "others": [ $releaseJson, $releaseJson ] }""",
            ),
        )
        assertEquals("s1", search.id)
        assertTrue(search.isFinished)
        assertEquals(1, search.results.size)
        assertEquals(2, search.others.size)
        assertEquals(2024, search.year)

        val started = parseMediaReleaseSearch(JSONObject("""{ "id": "s2", "phase": "connecting", "message": "Connecting to the VPN..." }"""))
        assertFalse(started.isFinished)
        assertEquals("Connecting to the VPN...", started.message)
        assertTrue(started.results.isEmpty())
        assertTrue(started.others.isEmpty())
    }

    @Test
    fun `a download parses every contract field`() {
        val download = parseMediaDownload(JSONObject(downloadJson))
        assertEquals("d1", download.id)
        assertEquals("episode", download.kind)
        assertEquals(82728L, download.tmdbId)
        assertEquals(2, download.season)
        assertEquals(5, download.episode)
        assertEquals("S02E05", download.subtitle)
        assertEquals("downloading", download.state)
        assertTrue(download.isActive)
        assertEquals(0.42, download.progress, 0.0001)
        assertEquals(168_000_000L, download.downloadedBytes)
        assertEquals(3_100_000L, download.speedBytesPerSecond)
        assertEquals(75L, download.etaSeconds)
        assertEquals(14, download.peers)
        assertEquals("Looking for people sharing it...", download.error)
        assertNull(download.itemId)
        assertEquals("sam", download.requestedBy)
        assertNull(download.finishedAt)

        val minimal = parseMediaDownload(JSONObject("""{ "id": "d2", "title": "Old", "state": "done", "itemId": "m9" }"""))
        assertTrue(minimal.isDone)
        assertFalse(minimal.isActive)
        assertEquals(0.0, minimal.progress, 0.0)
        assertNull(minimal.sizeBytes)
        assertEquals("m9", minimal.itemId)
    }

    @Test
    fun `the download list parses, and a bare one is empty`() {
        val list = parseMediaDownloadList(
            JSONObject("""{ "status": ${JSONObject(statusJson).getJSONObject("status")}, "active": [ $downloadJson ], "recent": [] }"""),
        )
        assertEquals("connected", list.status!!.vpn.state)
        assertEquals(1, list.active.size)
        assertTrue(list.recent.isEmpty())

        val bare = parseMediaDownloadList(JSONObject("{}"))
        assertNull(bare.status)
        assertTrue(bare.active.isEmpty())
        assertTrue(bare.recent.isEmpty())
    }

    @Test
    fun `sites and the catalog parse, with typed form fields`() {
        val sites = parseMediaDownloadSites(
            JSONArray("""[ { "id": 3, "name": "YTS", "enabled": true, "privacy": "public", "language": "en-US", "description": "Movies", "definition": "yts" },
                            { "id": 4, "name": "Other" } ]"""),
        )
        assertEquals("3", sites[0].id)
        assertEquals("public", sites[0].privacy)
        assertTrue(sites[1].enabled)
        assertNull(sites[1].privacy)

        val catalog = parseMediaSiteCatalog(
            JSONArray(
                """[ { "definition": "yts", "name": "YTS", "description": "…", "privacy": "public", "language": "en-US", "needsSetup": false, "fields": [] },
                     { "definition": "privatehd", "name": "PrivateHD", "privacy": "private", "needsSetup": true, "fields": [
                       { "name": "username", "label": "Username", "type": "text", "help": "Your login", "value": null, "options": null },
                       { "name": "freeleech", "label": "Freeleech only", "type": "checkbox", "value": true },
                       { "name": "sort", "label": "Sort", "type": "select", "value": 1, "options": [ { "value": "0", "label": "Newest" }, { "value": 1, "label": "Seeders" } ] },
                       { "name": "pages", "label": "Pages", "type": "number", "value": 2 } ] } ]""",
            ),
        )
        assertFalse(catalog[0].needsSetup)
        val fields = catalog[1].fields
        assertTrue(catalog[1].needsSetup)
        assertEquals("", fields[0].value)
        assertEquals("Your login", fields[0].help)
        assertTrue(fields[0].options.isEmpty())
        assertTrue(fields[1].checked)
        assertEquals("1", fields[2].value)
        assertEquals(listOf("0", "1"), fields[2].options.map { it.value })
        assertEquals("2", fields[3].value)

        val body = MediaDownloadApi.siteFieldsJson(
            fields,
            values = mapOf("username" to "sam", "sort" to "0", "pages" to ""),
            checks = mapOf("freeleech" to false),
        )
        assertEquals("sam", body.getString("username"))
        assertEquals(false, body.getBoolean("freeleech"))
        assertEquals(0L, body.getLong("sort"))
        assertTrue(body.isNull("pages"))
    }

    @Test
    fun `a 503 while the VPN starts is recognised`() {
        val starting = parseMediaDownloadsStarting("""{ "error": "Connecting to the VPN... try again in a moment.", "starting": true }""")
        assertEquals("Connecting to the VPN... try again in a moment.", starting!!.message)
        assertNull(parseMediaDownloadsStarting("""{ "error": "Prowlarr is down." }"""))
        assertNull(parseMediaDownloadsStarting("not json"))
    }

    @Test
    fun `request paths and bodies follow the contract`() {
        assertEquals("/api/media/downloads/lookup?q=dune+part+two&kind=movie", MediaDownloadApi.lookupPath(" dune part two ", "movie"))
        assertEquals("/api/media/downloads/show/82728", MediaDownloadApi.showPath(82728))
        assertEquals("/api/media/downloads/search/a%20b", MediaDownloadApi.searchPath("a b"))
        assertEquals("/api/media/downloads/d1/retry", MediaDownloadApi.retryPath("d1"))
        assertEquals("/api/media/downloads/sites/3", MediaDownloadApi.sitePath("3"))
        assertEquals("/api/media/downloads/sites/catalog?q=", MediaDownloadApi.catalogPath(""))

        val input = MediaReleaseSearchInput("show", "Bluey", 2018, 82728, "/b.jpg", season = 2, episode = 5)
        val json = input.toJson()
        assertEquals("show", json.getString("kind"))
        assertEquals(2, json.getInt("season"))
        assertEquals(5, json.getInt("episode"))
        assertEquals("Bluey (2018) · S02E05", input.label)
        assertEquals("Bluey (2018) · season 2", input.copy(episode = null).label)
        assertTrue(MediaReleaseSearchInput("movie", "Dune").toJson().isNull("season"))

        val grab = MediaDownloadApi.grabBody("s1", "r1")
        assertEquals("s1", grab.getString("searchId"))
        assertTrue(grab.isNull("libraryId"))
    }

    @Test
    fun `wording matches the web`() {
        assertEquals("3.1 MB/s", MediaDownloadFormat.speedLabel(3_100_000))
        assertEquals("640 KB/s", MediaDownloadFormat.speedLabel(640_000))
        assertNull(MediaDownloadFormat.speedLabel(0))
        assertEquals("about a minute left", MediaDownloadFormat.etaLabel(75))
        assertEquals("5 min left", MediaDownloadFormat.etaLabel(300))
        assertEquals("1 h 30 min left", MediaDownloadFormat.etaLabel(5400))
        assertNull(MediaDownloadFormat.etaLabel(null))
        assertEquals("42%", MediaDownloadFormat.percentLabel(0.42))
        assertEquals("42.5%", MediaDownloadFormat.percentLabel(0.425))
        assertEquals("S02E05", MediaDownloadFormat.episodeCode(2, 5))
        assertEquals("sharing back", MediaDownloadFormat.stateWord("seeding"))

        val one = parseMediaDownload(JSONObject(downloadJson))
        assertEquals("Downloading Bluey S02E05 · 42%", MediaDownloadFormat.bannerText(listOf(one)))
        assertEquals("2 downloads in progress", MediaDownloadFormat.bannerText(listOf(one, one)))
        assertNull(MediaDownloadFormat.bannerText(emptyList()))

        val services = parseMediaDownloadStatus(JSONObject(statusJson)).status
        assertEquals(VpnTone.ON to "Connected · Stockholm, SE · 185.65.134.1", MediaDownloadFormat.vpnLine(services))
        assertEquals(VpnTone.OFF, MediaDownloadFormat.vpnLine(MediaDownloadServices())!!.first)
        assertEquals(VpnTone.WARN, MediaDownloadFormat.vpnLine(MediaDownloadServices(services = "notInstalled"))!!.first)
        assertEquals(VpnTone.BUSY, MediaDownloadFormat.vpnLine(MediaDownloadServices(services = "starting"))!!.first)
        assertEquals(
            VpnTone.WARN to "tunnel down",
            MediaDownloadFormat.vpnLine(MediaDownloadServices(services = "failed", vpn = MediaDownloadVpn(state = "down", error = "tunnel down"))),
        )
        assertNull(MediaDownloadFormat.vpnLine(null))
        assertEquals(12, MediaDownloadFormat.wholeNumber(" 12 "))
        assertNull(MediaDownloadFormat.wholeNumber("1a"))
    }
}
