package com.sentinelcore.android.data

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Instant
import java.time.ZoneId

// Fed the sample JSON from core/docs/MOVIE_DOWNLOADS.md (and trimmed-down answers with fields
// missing), so a drift between the contract and these parsers shows up here rather than as a
// blank screen or a crash on an older server.
class MediaDownloadParsingTest {

    private val statusJson = """
        { "enabled": true, "canDownload": true, "canManageSites": true, "tmdbConfigured": true,
          "movieLibrary": true, "showLibrary": false,
          "status": { "enabled": true, "services": "running", "idleMinutes": 5,
                      "vpn": { "state": "connected", "required": true,
                               "verified": true, "verifiedAt": "2026-09-29T10:00:05Z", "ip": "185.65.134.1",
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
        assertNull(download.replacesItemId)
        assertEquals("m1", parseMediaDownload(JSONObject("""{ "id": "d3", "title": "Dune", "state": "queued", "replacesItemId": "m1" }""")).replacesItemId)

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
        // No `replace` until the server has asked; then true or false, never null.
        assertFalse(grab.has("replace"))
        assertTrue(MediaDownloadApi.grabBody("s1", "r1", replace = true).getBoolean("replace"))
        assertFalse(MediaDownloadApi.grabBody("s1", "r1", replace = false).getBoolean("replace"))
    }

    @Test
    fun `a 409 for a movie already in the library carries the existing copy`() {
        val duplicate = parseMediaAlreadyInLibrary(
            """{ "error": "You already have Dune (2021).", "code": "alreadyInLibrary",
                 "existing": { "itemId": "m1", "title": "Dune", "year": 2021, "sizeBytes": 1400000000, "height": 720, "canReplace": true } }""",
        )!!
        assertEquals("You already have Dune (2021).", duplicate.message)
        assertEquals(MediaExistingCopy("m1", "Dune", 2021, 1_400_000_000L, 720, true), duplicate.existing)
        assertEquals("720p · 1.4 GB", MediaDownloadFormat.existingCopyLabel(duplicate.existing))
        assertEquals(
            "The copy you have is 720p · 1.4 GB. Replace it with this download? The old copy is deleted once the new one has downloaded.",
            MediaDownloadFormat.alreadyInLibraryText(duplicate.existing),
        )

        // A copy on another computer's share can't be replaced; missing size and height say nothing.
        val shared = parseMediaAlreadyInLibrary(
            """{ "error": "You already have Dune (2021).", "code": "alreadyInLibrary", "existing": { "itemId": "m2", "canReplace": false } }""",
        )!!
        assertFalse(shared.existing.canReplace)
        assertNull(MediaDownloadFormat.existingCopyLabel(shared.existing))
        assertFalse(MediaDownloadFormat.alreadyInLibraryText(shared.existing).startsWith("The copy you have"))

        // Any other 409, or something that is not JSON, is an ordinary error.
        assertNull(parseMediaAlreadyInLibrary("""{ "error": "Busy." }"""))
        assertNull(parseMediaAlreadyInLibrary("not json"))
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

        assertEquals(12, MediaDownloadFormat.wholeNumber(" 12 "))
        assertNull(MediaDownloadFormat.wholeNumber("1a"))
    }

    // The doc's "The VPN light" table, one look per row (plus the in-between cases).
    @Test
    fun `the VPN light follows the contract table`() {
        val services = parseMediaDownloadStatus(JSONObject(statusJson)).status
        assertEquals(
            VpnLook(VpnTone.ON, "VPN · SE", "Connected and verified · Stockholm, SE · 185.65.134.1 · your home address is hidden"),
            MediaDownloadFormat.vpnLook(services),
        )
        // Connected and verified without a country still reads as on.
        val noCountry = MediaDownloadServices(services = "running", vpn = MediaDownloadVpn(state = "connected", verified = true))
        assertEquals(VpnTone.ON, MediaDownloadFormat.vpnLook(noCountry)!!.tone)
        assertEquals("VPN on", MediaDownloadFormat.vpnLook(noCountry)!!.short)
        assertEquals("Connected and verified · your home address is hidden", MediaDownloadFormat.vpnLook(noCountry)!!.text)

        // Connected but not yet verified: cyan, checking.
        val unverified = services!!.copy(vpn = services.vpn.copy(verified = false))
        val checking = MediaDownloadFormat.vpnLook(unverified)!!
        assertEquals(VpnTone.BUSY, checking.tone)
        assertEquals("VPN checking", checking.short)
        assertEquals("Connected · Stockholm, SE · 185.65.134.1 · checking it hides your home address…", checking.text)

        // Coming up: cyan, connecting.
        assertEquals(
            VpnLook(VpnTone.BUSY, "VPN connecting", "Connecting…"),
            MediaDownloadFormat.vpnLook(MediaDownloadServices(services = "starting", vpn = MediaDownloadVpn(state = "off"))),
        )
        assertEquals(VpnTone.BUSY, MediaDownloadFormat.vpnLook(MediaDownloadServices(services = "running", vpn = MediaDownloadVpn(state = "connecting")))!!.tone)

        // Idle: grey - the normal resting state, and qBittorrent/Prowlarr run elsewhere.
        val idle = MediaDownloadFormat.vpnLook(MediaDownloadServices(services = "stopped", vpn = MediaDownloadVpn(state = "off")))!!
        assertEquals(VpnTone.IDLE, idle.tone)
        assertEquals("VPN idle", idle.short)
        assertTrue(idle.text.startsWith("Off until someone searches or downloads, then it connects by itself."))
        assertEquals(VpnTone.IDLE, MediaDownloadFormat.vpnLook(MediaDownloadServices())!!.tone)
        assertEquals(VpnTone.IDLE, MediaDownloadFormat.vpnLook(MediaDownloadServices(services = "external"))!!.tone)

        // Use VPN turned off in Settings: amber, and it wins over everything else.
        val disabled = parseMediaDownloadStatus(
            JSONObject(
                """{ "canDownload": true, "status": { "enabled": true, "services": "running",
                     "vpn": { "state": "disabled", "required": false, "verified": false, "verifiedAt": null,
                              "ip": null, "location": null, "country": null, "provider": null, "error": null } } }""",
            ),
        )
        val off = MediaDownloadFormat.vpnLook(disabled.status)!!
        assertEquals(VpnTone.OFF, off.tone)
        assertEquals("VPN off", off.short)
        assertEquals(
            "Turned off in Settings · searching and downloading use your home connection, so the sites see your home address.",
            off.text,
        )
        assertEquals(
            "The VPN is turned off in Settings, so the sites and everyone sharing will see your home address.",
            MediaDownloadFormat.vpnOffNote(disabled),
        )
        assertNull(MediaDownloadFormat.vpnOffNote(parseMediaDownloadStatus(JSONObject(statusJson))))
        assertNull(MediaDownloadFormat.vpnOffNote(null))

        // Problems: red.
        assertEquals(
            VpnLook(VpnTone.BAD, "VPN problem", "tunnel down"),
            MediaDownloadFormat.vpnLook(MediaDownloadServices(services = "failed", vpn = MediaDownloadVpn(state = "down", error = "tunnel down"))),
        )
        assertEquals(VpnTone.BAD, MediaDownloadFormat.vpnLook(MediaDownloadServices(services = "running", vpn = MediaDownloadVpn(state = "down")))!!.tone)
        val notInstalled = MediaDownloadFormat.vpnLook(MediaDownloadServices(services = "notInstalled"))!!
        assertEquals(VpnTone.BAD, notInstalled.tone)
        assertEquals("VPN not set up", notInstalled.short)

        assertNull(MediaDownloadFormat.vpnLook(null))
    }

    @Test
    fun `verified and disabled parse, and missing verified reads as false`() {
        val vpn = parseMediaDownloadStatus(JSONObject(statusJson)).status!!.vpn
        assertTrue(vpn.verified)
        assertEquals("2026-09-29T10:00:05Z", vpn.verifiedAt)

        val old = parseMediaDownloadVpn(JSONObject("""{ "state": "connected", "ip": "1.2.3.4" }"""))
        assertFalse(old.verified)
        assertNull(old.verifiedAt)

        val disabled = parseMediaDownloadVpn(JSONObject("""{ "state": "disabled", "required": false, "ip": null, "error": null }"""))
        assertEquals("disabled", disabled.state)
        assertFalse(disabled.required)
        assertNull(disabled.ip)
        assertNull(disabled.location)
    }

    // No TMDB key: Find goes straight to the site search, with the same input the
    // "Search sites for …" button sends. With a key, Find stops at the TMDB results as before.
    @Test
    fun `without TMDB, Find searches the sites for the typed text`() {
        val noKey = parseMediaLookup(JSONObject("""{ "tmdbConfigured": false, "results": [] }"""))
        assertTrue(MediaDownloadFormat.searchSitesDirectly(noKey))
        val withKey = parseMediaLookup(JSONObject("""{ "tmdbConfigured": true, "results": [] }"""))
        assertFalse(MediaDownloadFormat.searchSitesDirectly(withKey))

        assertEquals(
            MediaReleaseSearchInput("movie", "Dune Part Two"),
            MediaDownloadFormat.directSearchInput("movie", "  Dune Part Two ", "2", "5"),
        )
        assertEquals(
            MediaReleaseSearchInput("show", "Bluey", season = 2, episode = 5),
            MediaDownloadFormat.directSearchInput("show", "Bluey", "2", "5"),
        )
        // Season alone is the whole season; an episode without a season is ignored; junk is ignored.
        assertEquals(
            MediaReleaseSearchInput("show", "Bluey", season = 2),
            MediaDownloadFormat.directSearchInput("show", "Bluey", "2", ""),
        )
        assertEquals(MediaReleaseSearchInput("show", "Bluey"), MediaDownloadFormat.directSearchInput("show", "Bluey", "", "5"))
        assertEquals(MediaReleaseSearchInput("show", "Bluey"), MediaDownloadFormat.directSearchInput("show", "Bluey", "2a", "5"))
        assertNull(MediaDownloadFormat.directSearchInput("movie", "   ", "", ""))
    }

    @Test
    fun `the VPN test answer parses`() {
        val result = parseMediaVpnTestResult(
            JSONObject(
                """{ "ok": true, "message": "Working. The sites see 185.195.233.76 in Stockholm, SE, not your home address.",
                     "status": ${JSONObject(statusJson).getJSONObject("status")} }""",
            ),
        )
        assertTrue(result.ok)
        assertEquals("Working. The sites see 185.195.233.76 in Stockholm, SE, not your home address.", result.message)
        assertEquals(VpnTone.ON, MediaDownloadFormat.vpnLook(result.status)!!.tone)

        val failed = parseMediaVpnTestResult(JSONObject("""{ "ok": false, "message": "The tunnel did not come up." }"""))
        assertFalse(failed.ok)
        assertEquals("The tunnel did not come up.", failed.message)
        assertNull(failed.status)

        val bare = parseMediaVpnTestResult(JSONObject("{}"))
        assertFalse(bare.ok)
        assertEquals("The VPN test failed.", bare.message)
        assertEquals("/api/media/downloads/vpn/test", MediaDownloadApi.VPN_TEST_PATH)
    }

    // Fixed clock and zone so the "Oct 27" dates do not depend on the machine running the tests.
    private val now = Instant.parse("2026-10-01T15:00:00Z")
    private val utc = ZoneId.of("UTC")

    @Test
    fun `the Mullvad account parses, and is null when absent or null`() {
        val vpn = parseMediaDownloadVpn(
            JSONObject(
                """{ "state": "connected", "error": null,
                     "account": { "provider": "mullvad", "paidUntil": "2026-10-27T12:02:43+00:00", "daysLeft": 26,
                                  "runningOut": false, "expired": false, "checkedAt": "2026-10-01T15:00:00+00:00" } }""",
            ),
        )
        assertEquals(
            MediaVpnAccount("mullvad", "2026-10-27T12:02:43+00:00", 26, false, false, "2026-10-01T15:00:00+00:00"),
            vpn.account,
        )
        assertNull(parseMediaDownloadVpn(JSONObject("""{ "state": "connected" }""")).account)
        assertNull(parseMediaDownloadVpn(JSONObject("""{ "state": "connected", "account": null }""")).account)
        assertNull(parseMediaDownloadStatus(JSONObject(statusJson)).status!!.vpn.account)
    }

    @Test
    fun `an expired account turns the light red, after disabled and not installed`() {
        val expired = MediaVpnAccount(paidUntil = "2026-10-27T12:02:43+00:00", expired = true)
        val look = MediaDownloadFormat.vpnLook(
            MediaDownloadServices(services = "running", vpn = MediaDownloadVpn(state = "connected", verified = true, account = expired)),
            now,
            utc,
        )!!
        assertEquals(
            VpnLook(
                VpnTone.BAD,
                "VPN out of time",
                "Your Mullvad account ran out of time on Oct 27. Add time at mullvad.net and downloads work again.",
            ),
            look,
        )
        assertEquals(
            "VPN off",
            MediaDownloadFormat.vpnLook(MediaDownloadServices(vpn = MediaDownloadVpn(state = "disabled", account = expired)), now, utc)!!.short,
        )
        assertEquals(
            "VPN not set up",
            MediaDownloadFormat.vpnLook(MediaDownloadServices(services = "notInstalled", vpn = MediaDownloadVpn(account = expired)), now, utc)!!.short,
        )
        // Paid time left leaves the light alone.
        val paid = MediaVpnAccount(paidUntil = "2026-10-27T12:02:43+00:00", daysLeft = 26)
        assertEquals(
            VpnTone.ON,
            MediaDownloadFormat.vpnLook(
                MediaDownloadServices(services = "running", vpn = MediaDownloadVpn(state = "connected", verified = true, account = paid)),
                now,
                utc,
            )!!.tone,
        )
    }

    @Test
    fun `the account line follows the contract table`() {
        val until = "2026-10-27T12:02:43+00:00"
        fun line(daysLeft: Int, runningOut: Boolean = false, expired: Boolean = false, paidUntil: String = until) =
            MediaDownloadFormat.vpnAccountLine(MediaVpnAccount("mullvad", paidUntil, daysLeft, runningOut, expired), now, utc)

        assertNull(MediaDownloadFormat.vpnAccountLine(null, now, utc))
        assertEquals(VpnAccountLine(VpnTone.IDLE, "Mullvad paid until Oct 27 · 26 days left"), line(26))
        assertEquals(VpnAccountLine(VpnTone.IDLE, "Mullvad paid until Oct 27 · 1 day left"), line(1))
        assertEquals(VpnAccountLine(VpnTone.IDLE, "Mullvad paid until Oct 27 · less than a day left"), line(0))
        assertEquals(VpnAccountLine(VpnTone.OFF, "Mullvad runs out in 5 days (Oct 27) · add time at mullvad.net"), line(5, runningOut = true))
        assertEquals(VpnAccountLine(VpnTone.OFF, "Mullvad runs out tomorrow (Oct 27) · add time at mullvad.net"), line(1, runningOut = true))
        assertEquals(VpnAccountLine(VpnTone.OFF, "Mullvad runs out today (Oct 27) · add time at mullvad.net"), line(0, runningOut = true))
        assertEquals(VpnAccountLine(VpnTone.BAD, "Mullvad ran out on Oct 27 · add time at mullvad.net"), line(0, runningOut = true, expired = true))
        // Another year gets the year; the date is the device's local one.
        assertEquals(
            VpnAccountLine(VpnTone.IDLE, "Mullvad paid until Oct 27, 2027 · 391 days left"),
            line(391, paidUntil = "2027-10-27T12:02:43+00:00"),
        )
        assertEquals(
            "Oct 28",
            MediaDownloadFormat.accountDate("2026-10-27T23:30:00+00:00", now, ZoneId.of("Asia/Tokyo")),
        )
    }

    // ---- Search offers a download ----

    @Test
    fun `the search offer shows only for an unfiltered empty search by someone who can download`() {
        assertTrue(MediaDownloadFormat.offersDownload("dune 2", libraryEmpty = true, filtered = false, canDownload = true))
        assertTrue(MediaDownloadFormat.offersDownload("  du ", libraryEmpty = true, filtered = false, canDownload = true))
        // One character (after trimming) is not enough.
        assertFalse(MediaDownloadFormat.offersDownload(" d  ", libraryEmpty = true, filtered = false, canDownload = true))
        assertFalse(MediaDownloadFormat.offersDownload("", libraryEmpty = true, filtered = false, canDownload = true))
        assertFalse(MediaDownloadFormat.offersDownload("dune", libraryEmpty = false, filtered = false, canDownload = true))
        assertFalse(MediaDownloadFormat.offersDownload("dune", libraryEmpty = true, filtered = true, canDownload = true))
        assertFalse(MediaDownloadFormat.offersDownload("dune", libraryEmpty = true, filtered = false, canDownload = false))
    }

    @Test
    fun `a kind=any lookup keeps known kinds, in order, up to six`() {
        val results = JSONArray()
        results.put(JSONObject("""{ "kind": "show", "tmdbId": 1, "title": "Dune: Prophecy", "inLibrary": true, "itemId": "s1" }"""))
        results.put(JSONObject("""{ "kind": "music", "tmdbId": 2, "title": "Dune (Soundtrack)" }"""))
        for (id in 3..9) results.put(JSONObject("""{ "kind": "movie", "tmdbId": $id, "title": "Dune $id" }"""))
        val lookup = parseMediaLookup(JSONObject().put("tmdbConfigured", true).put("results", results))
        val offered = MediaDownloadFormat.offerResults(lookup)
        assertEquals(6, offered.size)
        assertEquals(listOf(1L, 3L, 4L, 5L, 6L, 7L), offered.map { it.tmdbId })
        assertTrue(offered.none { it.kind == "music" })
        assertTrue(offered.first().inLibrary)
        assertEquals("s1", offered.first().itemId)
        assertTrue(MediaDownloadFormat.offerResults(parseMediaLookup(JSONObject("""{ "tmdbConfigured": false }"""))).isEmpty())
    }

    @Test
    fun `kind labels come from one table`() {
        assertEquals("Movie", MediaDownloadFormat.kindLabel("movie"))
        assertEquals("TV show", MediaDownloadFormat.kindLabel("show"))
        assertNull(MediaDownloadFormat.kindLabel("music"))
        assertEquals(listOf("movie", "show"), MediaDownloadFormat.DOWNLOAD_KINDS.map { it.first })
        assertEquals("/api/media/downloads/lookup?q=dune+2&kind=any", MediaDownloadApi.lookupPath("dune 2", "any"))
    }
}
