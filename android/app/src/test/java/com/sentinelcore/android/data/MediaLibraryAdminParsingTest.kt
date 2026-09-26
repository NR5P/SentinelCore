package com.sentinelcore.android.data

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

// The library admin screens' side of core/docs/MOVIES.md ("Libraries", "Folder picker",
// "Folders on other computers"), fed that file's sample JSON.
class MediaLibraryAdminParsingTest {

    @Test
    fun `a library on another computer carries network and its scan times`() {
        val library = parseMediaLibrary(
            JSONObject(
                """
                { "id": "l1", "name": "Den movies", "kind": "movies", "path": "smb://192.168.1.20/Movies/Films",
                  "network": true, "itemCount": 12, "createdAt": "2026-09-20T10:00:00Z",
                  "lastScanStartedAt": "2026-09-25T10:00:00Z", "lastScanFinishedAt": "2026-09-25T10:05:00Z",
                  "lastScanError": null }
                """.trimIndent(),
            ),
        )
        assertTrue(library.network)
        assertEquals("smb://192.168.1.20/Movies/Films", library.path)
        assertEquals(12, library.itemCount)
        assertEquals("2026-09-20T10:00:00Z", library.createdAt)
        assertEquals("2026-09-25T10:00:00Z", library.lastScanStartedAt)
        assertNull(library.lastScanError)
        assertFalse(library.isShows)
    }

    @Test
    fun `a library from an older server without network is local`() {
        val library = parseMediaLibrary(
            JSONObject("""{ "id": "l2", "name": "TV Shows", "kind": "shows", "path": "/mnt/tv", "itemCount": 3, "lastScanError": "Folder not found" }"""),
        )
        assertFalse(library.network)
        assertTrue(library.isShows)
        assertEquals("Folder not found", library.lastScanError)
        assertNull(library.createdAt)
        assertEquals(2, parseMediaLibraries(JSONArray("[" + """{ "id": "a", "name": "A", "kind": "movies" }, { "id": "b", "name": "B", "kind": "shows" }""" + "]")).size)
    }

    @Test
    fun `the starting page lists local folders and signed-in computers`() {
        val listing = parseMediaBrowseListing(
            JSONObject(
                """
                { "path": "", "parent": null, "kind": "roots", "videoCount": 0,
                  "folders": [ { "name": "/mnt/movies", "path": "/mnt/movies", "videoCount": 4 } ],
                  "computers": [ { "id": "c1", "name": "Den PC", "host": "192.168.1.20", "username": "sam",
                                   "domain": "", "hasPassword": true, "path": "smb://192.168.1.20", "createdAt": "2026-09-26T01:00:00Z" } ] }
                """.trimIndent(),
            ),
        )
        assertTrue(listing.isRoots)
        assertNull(listing.parent)
        assertFalse(listing.canGoUp)
        assertFalse(listing.canUse)
        assertEquals(1, listing.folders.size)
        assertEquals(4, listing.folders[0].videoCount)
        val computer = listing.computers.single()
        assertEquals("c1", computer.id)
        assertEquals("Den PC", computer.name)
        assertEquals("sam", computer.username)
        assertTrue(computer.hasPassword)
        assertEquals("smb://192.168.1.20", computer.path)
        assertNull(listing.error)
    }

    @Test
    fun `a computer lists its shares, can go up to the start, and is not pickable`() {
        val listing = parseMediaBrowseListing(
            JSONObject(
                """
                { "path": "smb://NAS", "parent": "", "kind": "computer", "host": "NAS",
                  "folders": [ { "name": "Movies", "path": "smb://NAS/Movies", "videoCount": null } ],
                  "videoCount": 0, "hint": "Type the share name if yours is missing." }
                """.trimIndent(),
            ),
        )
        assertEquals("", listing.parent)
        assertTrue(listing.canGoUp)
        assertFalse(listing.canUse)
        assertEquals("NAS", listing.host)
        assertNull(listing.folders.single().videoCount)
        assertEquals("Type the share name if yours is missing.", listing.hint)
    }

    @Test
    fun `a network folder has null folder counts and is pickable`() {
        val listing = parseMediaBrowseListing(
            JSONObject(
                """
                { "path": "smb://NAS/Movies/Films", "parent": "smb://NAS/Movies", "kind": "network", "host": "NAS",
                  "folders": [ { "name": "Kids", "path": "smb://NAS/Movies/Films/Kids", "videoCount": null } ],
                  "videoCount": 3 }
                """.trimIndent(),
            ),
        )
        assertEquals("smb://NAS/Movies", listing.parent)
        assertEquals(3, listing.videoCount)
        assertNull(listing.folders.single().videoCount)
        assertTrue(listing.canUse)
        assertFalse(listing.needsLogin)
        assertNull(listing.hint)
    }

    @Test
    fun `a folder that needs a sign-in comes back as an error with needsLogin and host`() {
        val listing = parseMediaBrowseListing(
            JSONObject(
                """
                { "path": "smb://DESKTOP-PC/Movies", "parent": "smb://DESKTOP-PC", "kind": "network", "host": "DESKTOP-PC",
                  "folders": [], "videoCount": null, "error": "DESKTOP-PC wants a username and password.", "needsLogin": true }
                """.trimIndent(),
            ),
        )
        assertEquals("DESKTOP-PC wants a username and password.", listing.error)
        assertTrue(listing.needsLogin)
        assertEquals("DESKTOP-PC", listing.host)
        assertNull(listing.videoCount)
        assertFalse(listing.canUse)
        assertTrue(listing.folders.isEmpty())
    }

    @Test
    fun `an error with no host keeps host null`() {
        val listing = parseMediaBrowseListing(
            JSONObject("""{ "path": "/nope", "parent": "/", "kind": "local", "folders": [], "videoCount": 0, "error": "That folder does not exist.", "needsLogin": false, "host": null }"""),
        )
        assertNull(listing.host)
        assertFalse(listing.needsLogin)
        assertFalse(listing.canUse)
    }

    @Test
    fun `discovered computers parse, including a null address and a saved id`() {
        val found = parseMediaDiscoveredComputers(
            JSONArray(
                """
                [ { "name": "Living Room Mac", "host": "Living-Room-Mac.local", "address": "192.168.1.31",
                    "path": "smb://Living-Room-Mac.local", "savedId": null },
                  { "name": "NAS", "host": "nas.local", "address": null, "path": "smb://nas.local", "savedId": "c9" } ]
                """.trimIndent(),
            ),
        )
        assertEquals(2, found.size)
        assertEquals("192.168.1.31", found[0].address)
        assertNull(found[0].savedId)
        assertEquals("smb://Living-Room-Mac.local", found[0].path)
        assertNull(found[1].address)
        assertEquals("c9", found[1].savedId)
    }

    @Test
    fun `a sign-in answer carries the computer and its shares`() {
        val result = parseMediaNetworkSignIn(
            JSONObject(
                """
                { "computer": { "id": "c1", "name": "Den PC", "host": "DESKTOP-PC", "username": "sam", "domain": "",
                                "hasPassword": true, "path": "smb://DESKTOP-PC", "createdAt": "…" },
                  "shares": [ { "name": "Movies", "path": "smb://DESKTOP-PC/Movies" } ] }
                """.trimIndent(),
            ),
        )
        assertEquals("smb://DESKTOP-PC", result.computer.path)
        assertEquals(listOf(MediaShare("Movies", "smb://DESKTOP-PC/Movies")), result.shares)
    }

    @Test
    fun `a computer with no name shows its host`() {
        val computer = parseMediaNetworkComputer(JSONObject("""{ "id": "c2", "name": "", "host": "nas.local", "username": "", "hasPassword": false }"""))
        assertEquals("nas.local", computer.name)
        assertEquals("smb://nas.local", computer.path)
        assertEquals("", computer.username)
        assertFalse(computer.hasPassword)
    }

    @Test
    fun `a refused sign-in keeps every message and needsLogin`() {
        val error = parseMediaNetworkSignInError("""{ "errors": [ "Wrong username or password.", "Try the Windows account." ], "needsLogin": true }""")
        assertNotNull(error)
        assertEquals("Wrong username or password.\nTry the Windows account.", error!!.message)
        assertTrue(error.needsLogin)

        val unreachable = parseMediaNetworkSignInError("""{ "errors": [ "Could not reach NAS." ], "needsLogin": false }""")
        assertFalse(unreachable!!.needsLogin)
        assertNull(parseMediaNetworkSignInError("not json"))
        assertNull(parseMediaNetworkSignInError("""{ "errors": [] }"""))
    }

    @Test
    fun `sign-in and library bodies match the contract`() {
        val body = MediaNetworkSignInInput(host = "DESKTOP-PC", name = "Den PC", username = "sam", password = "pw").toJson()
        assertEquals("DESKTOP-PC", body.getString("host"))
        assertEquals("Den PC", body.getString("name"))
        assertEquals("sam", body.getString("username"))
        assertEquals("pw", body.getString("password"))
        assertEquals("", body.getString("domain"))

        val library = MediaLibraryInput("Movies", "movies", "/mnt/movies").toJson()
        assertEquals("Movies", library.getString("name"))
        assertEquals("movies", library.getString("kind"))
        assertEquals("/mnt/movies", library.getString("path"))
    }

    @Test
    fun `browse and admin paths encode the folder`() {
        assertEquals("/api/media/browse", MediaApi.browsePath(null))
        assertEquals("/api/media/browse", MediaApi.browsePath(""))
        assertEquals("/api/media/browse?path=smb%3A%2F%2FNAS%2FMy+Movies", MediaApi.browsePath("smb://NAS/My Movies"))
        assertEquals("/api/media/libraries/a%20b", MediaApi.libraryPath("a b"))
        assertEquals("/api/media/network/c1", MediaApi.networkComputerPath("c1"))
    }

    @Test
    fun `a default name follows the kind but a typed one does not`() {
        assertEquals("TV Shows", MediaLibraryForm.nameAfterKindChange("Movies", "shows"))
        assertEquals("Movies", MediaLibraryForm.nameAfterKindChange("TV Shows", "movies"))
        assertEquals("TV Shows", MediaLibraryForm.nameAfterKindChange("  ", "shows"))
        assertEquals("Kids films", MediaLibraryForm.nameAfterKindChange("Kids films", "shows"))
        assertEquals("1 video", MediaLibraryForm.videoCountLabel(1))
        assertEquals("3 videos", MediaLibraryForm.videoCountLabel(3))
    }
}
