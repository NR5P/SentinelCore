package com.sentinelcore.android.organizer

import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import java.nio.file.Files
import java.security.MessageDigest
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Assume.assumeTrue
import org.junit.Before
import org.junit.Test

// Two devices and the real SentinelCore server - the same end-to-end checks OrganizerKit runs on
// Apple, so both platforms are held to the one server. Needs the two throwaway servers
// scripts/with-organizer-servers.sh starts:
//   scripts/with-organizer-servers.sh ./gradlew -p android testDebugUnitTest
// A plain test run skips these.
class SwitchableTransport(@Volatile var base: String) : OrganizerTransport {
    @Volatile var offline = false

    override suspend fun send(method: String, path: String, query: Map<String, String>, body: ByteArray?, contentType: String?, timeoutMillis: Int?): OrganizerResponse =
        withContext(Dispatchers.IO) {
            if (offline) throw OrganizerException.Offline()
            val queryString = query.entries.joinToString("&") { "${it.key}=${URLEncoder.encode(it.value, "UTF-8")}" }
            val connection = URL(base + path + if (queryString.isEmpty()) "" else "?$queryString").openConnection() as HttpURLConnection
            try {
                connection.requestMethod = method
                connection.connectTimeout = 10_000
                connection.readTimeout = timeoutMillis ?: 30_000
                if (body != null) {
                    connection.doOutput = true
                    contentType?.let { connection.setRequestProperty("Content-Type", it) }
                    connection.outputStream.use { it.write(body) }
                }
                val status = connection.responseCode
                val bytes = (if (status in 200..299) connection.inputStream else connection.errorStream)?.use { it.readBytes() } ?: ByteArray(0)
                OrganizerResponse(status, bytes)
            } catch (error: java.io.IOException) {
                throw OrganizerException.Offline()
            } finally {
                connection.disconnect()
            }
        }
}

class OrganizerLiveServerTest {
    private var serverA = ""
    private var serverB = ""
    private val engines = mutableListOf<OrganizerEngine>()
    private val scopes = mutableListOf<CoroutineScope>()
    private val directories = mutableListOf<File>()

    @Before
    fun setUp() {
        val a = System.getenv("ORGANIZER_TEST_SERVER_A")
        val b = System.getenv("ORGANIZER_TEST_SERVER_B")
        assumeTrue("Run under scripts/with-organizer-servers.sh to exercise the real server.", a != null && b != null)
        serverA = a!!
        serverB = b!!
    }

    @After
    fun tearDown() {
        engines.forEach { it.disconnect() }
        scopes.forEach { it.cancel() }
        directories.forEach { it.deleteRecursively() }
    }

    private fun device(name: String, policy: OfflinePolicy = OfflinePolicy.FULL_DEFAULT, server: String? = null): Pair<OrganizerEngine, SwitchableTransport> {
        val folder = Files.createTempDirectory("organizer-live-$name").toFile().also { directories += it }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO).also { scopes += it }
        val engine = OrganizerEngine(InMemoryOrganizerStore(folder), name, policy, scope).also { engines += it }
        return engine to SwitchableTransport(server ?: serverA)
    }

    private suspend fun start(engine: OrganizerEngine, transport: SwitchableTransport) {
        engine.connect(transport)
        engine.refreshStatus()
        engine.sync()
    }

    private fun unique(label: String) = "$label ${java.util.UUID.randomUUID().toString().take(8)}"

    private suspend fun waitUntil(timeoutMillis: Long = 15_000, condition: () -> Boolean) {
        val deadline = System.currentTimeMillis() + timeoutMillis
        while (!condition()) {
            if (System.currentTimeMillis() > deadline) fail("timed out waiting")
            delay(100)
        }
    }

    @Test
    fun `an edit on one device appears on the other`() = runBlocking {
        val (phone, phoneLink) = device("phone")
        val (laptop, laptopLink) = device("laptop")
        start(phone, phoneLink)
        start(laptop, laptopLink)
        val title = unique("Soccer practice")
        val event = phone.create(OrganizerKinds.EVENT, mapOf("title" to title, "start" to "2026-09-23T15:00:00-05:00",
            "recurrence" to "FREQ=WEEKLY", "links" to listOf(mapOf("url" to "https://example.com/schedule", "title" to "Schedule"))), spaceId = "family")
        phone.sync()
        laptop.sync()
        val seen = laptop.item(event.id)
        assertNotNull(seen)
        assertEquals(title, seen!!.title)
        assertEquals("Schedule", (seen.list("links").first() as Map<*, *>)["title"])
    }

    @Test
    fun `offline edits to different fields both survive`() = runBlocking {
        val (phone, phoneLink) = device("phone")
        val (laptop, laptopLink) = device("laptop")
        start(phone, phoneLink)
        start(laptop, laptopLink)
        val reminder = phone.create(OrganizerKinds.REMINDER, mapOf("title" to unique("Call vet")), spaceId = "family")
        phone.sync()
        laptop.sync()

        phoneLink.offline = true
        phone.update(reminder.id, mapOf("title" to "Call the vet about Max"))
        phone.sync()
        assertEquals(1, phone.syncState.value.waitingChanges)
        laptop.update(reminder.id, mapOf("due" to "2026-09-30", "allDay" to true))
        laptop.sync()
        phoneLink.offline = false
        phone.sync()
        laptop.sync()
        for (device in listOf(phone, laptop)) {
            assertEquals("Call the vet about Max", device.item(reminder.id)!!.title)
            assertEquals("2026-09-30", device.item(reminder.id)!!.string("due"))
        }
    }

    @Test
    fun `two people rewriting one note keep both versions`() = runBlocking {
        val (phone, phoneLink) = device("phone")
        val (laptop, laptopLink) = device("laptop")
        start(phone, phoneLink)
        start(laptop, laptopLink)
        val title = unique("Trip")
        val note = phone.create(OrganizerKinds.NOTE, mapOf("title" to title, "body" to "pack bags"), spaceId = "family")
        phone.sync()
        laptop.sync()
        phoneLink.offline = true
        phone.update(note.id, mapOf("body" to "pack bags, snacks"))
        laptop.update(note.id, mapOf("body" to "pack bags, passports"))
        laptop.sync()
        phoneLink.offline = false
        phone.sync()
        laptop.sync()
        for (device in listOf(phone, laptop)) {
            val notes = device.items(OrganizerKinds.NOTE).filter { it.title.startsWith(title) }
            assertEquals(setOf("pack bags, snacks", "pack bags, passports"), notes.mapNotNull { it.string("body") }.toSet())
            assertTrue(notes.any { it.title == "$title (conflicted copy)" })
        }
    }

    @Test
    fun `deletes reach every device and a stale edit can't bring it back`() = runBlocking {
        val (phone, phoneLink) = device("phone")
        val (laptop, laptopLink) = device("laptop")
        start(phone, phoneLink)
        start(laptop, laptopLink)
        val note = phone.create(OrganizerKinds.NOTE, mapOf("title" to unique("Old list")), spaceId = "family")
        phone.sync()
        laptop.sync()
        phoneLink.offline = true
        phone.update(note.id, mapOf("title" to "Old list (edited offline)"))
        laptop.delete(note.id)
        laptop.sync()
        phoneLink.offline = false
        phone.sync()
        assertNull(phone.item(note.id))
        assertNull(laptop.item(note.id))
    }

    @Test
    fun `a board made offline arrives whole`() = runBlocking {
        val (phone, phoneLink) = device("phone")
        val (laptop, laptopLink) = device("laptop")
        start(phone, phoneLink)
        start(laptop, laptopLink)
        phoneLink.offline = true
        val board = phone.create(OrganizerKinds.BOARD, mapOf("title" to unique("House")), spaceId = "family")
        val todo = phone.create(OrganizerKinds.COLUMN, mapOf("title" to "To do", "position" to 1.0), parentId = board.id)
        val done = phone.create(OrganizerKinds.COLUMN, mapOf("title" to "Done", "position" to 2.0), parentId = board.id)
        val card = phone.create(OrganizerKinds.CARD, mapOf("title" to "Paint fence", "checklist" to listOf(mapOf("id" to "a", "text" to "buy paint", "done" to false))), parentId = todo.id)
        phone.update(card.id, mapOf("parentId" to done.id, "position" to 1.5))
        phoneLink.offline = false
        phone.sync()
        laptop.sync()
        assertEquals(2, laptop.children(board.id, OrganizerKinds.COLUMN).size)
        assertEquals(done.id, laptop.item(card.id)!!.parentId)
        assertEquals(1, laptop.item(card.id)!!.list("checklist").size)
        assertEquals(0, phone.syncState.value.waitingChanges)
    }

    @Test
    fun `attachments travel and a light device keeps only a thumbnail`() = runBlocking {
        val (phone, phoneLink) = device("phone")
        val (laptop, laptopLink) = device("laptop")
        val (light, lightLink) = device("light", OfflinePolicy.PHONE_DEFAULT)
        start(phone, phoneLink)
        start(laptop, laptopLink)
        start(light, lightLink)

        // A real 1600x1200 JPEG (src/test/resources), so the server's thumbnailer has work to do.
        val jpeg = javaClass.getResourceAsStream("/organizer-test.jpg")!!.readBytes()
        val note = phone.create(OrganizerKinds.NOTE, mapOf("title" to unique("Field map")), spaceId = "family")
        phoneLink.offline = true
        val attachment = phone.attach(jpeg, "map.jpg", "image/jpeg", note.id)
        val hash = attachment.string("hash")!!
        phoneLink.offline = false
        phone.sync()
        waitUntil { phone.storageSummary().waitingUploads == 0 }

        laptop.sync()
        waitUntil { laptop.hasOriginal(hash) }
        assertArrayEquals(jpeg, laptop.imageBytes(hash, thumbnail = false))

        light.sync()
        waitUntil { light.storageSummary().bytesOnDevice > 0 }
        assertTrue("a light copy keeps thumbnails, not originals", !light.hasOriginal(hash))
        val thumbnail = light.imageBytes(hash, thumbnail = true)!!
        assertTrue(thumbnail.size < jpeg.size)
    }

    // The point of a full copy: the server dies, a new one is installed, a device refills it.
    @Test
    fun `a rebuilt server is refilled from a device`() = runBlocking {
        val (laptop, link) = device("laptop")
        start(laptop, link)
        val title = unique("Survives the server")
        val note = laptop.create(OrganizerKinds.NOTE, mapOf("title" to title, "body" to "important"), spaceId = "family")
        val bytes = "a picture that has to survive".toByteArray()
        val hash = laptop.attach(bytes, "photo.jpg", "image/jpeg", note.id).string("hash")!!
        laptop.sync()
        waitUntil { laptop.storageSummary().waitingUploads == 0 }

        link.base = serverB
        laptop.update(note.id, mapOf("body" to "important - edited after the server died"))
        laptop.sync()
        assertTrue((laptop.resetPrompt.value?.localCount ?: 0) > 0)
        laptop.resolveReset(upload = true)
        waitUntil { laptop.storageSummary().waitingUploads == 0 && !laptop.syncState.value.syncing }

        val (fresh, freshLink) = device("fresh", server = serverB)
        start(fresh, freshLink)
        val restored = fresh.items(OrganizerKinds.NOTE).firstOrNull { it.title == title }
        assertEquals("important - edited after the server died", restored?.string("body"))
        waitUntil { fresh.hasOriginal(hash) }
        assertArrayEquals(bytes, fresh.imageBytes(hash, thumbnail = false))
        assertEquals(hash, MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) })
    }

    @Test
    fun `the subscription feed lists events`() = runBlocking {
        val (phone, link) = device("phone")
        start(phone, link)
        val title = unique("Dentist")
        phone.create(OrganizerKinds.EVENT, mapOf("title" to title, "start" to "2026-10-01T09:00:00Z"), spaceId = "family")
        phone.sync()
        val path = phone.calendarFeedPath()
        val ics = withContext(Dispatchers.IO) { URL(serverA + path).readText() }
        assertTrue(ics.contains("SUMMARY:$title"))
    }

    @Test
    fun `a holiday choice follows the person to their other devices`() = runBlocking {
        val (phone, phoneLink) = device("phone")
        val (tablet, tabletLink) = device("tablet")
        start(phone, phoneLink)
        start(tablet, tabletLink)
        phone.setHolidaySets(listOf("jewish", "us-federal"))
        waitUntil { !phone.holidays.value.dirty && phone.holidays.value.list.isNotEmpty() }
        tablet.refreshHolidays(force = true)
        assertEquals(setOf("jewish", "us-federal"), tablet.holidays.value.sets.toSet())

        val zone = java.time.ZoneId.systemDefault()
        val september = java.time.LocalDate.of(2026, 9, 1).atStartOfDay(zone)
        val found = tablet.holidaysBetween(september, september.plusMonths(1))
        val roshHashanah = found.first { it.first.name == "Rosh Hashanah" }
        assertEquals(java.time.LocalDate.of(2026, 9, 12), roshHashanah.second.toLocalDate())
        assertEquals(2L, java.time.temporal.ChronoUnit.DAYS.between(roshHashanah.second, roshHashanah.third))
        assertTrue(found.any { it.first.name == "Labor Day" })

        tablet.setHolidaySets(emptyList())
        waitUntil { !tablet.holidays.value.dirty }
        phone.refreshHolidays(force = true)
        assertTrue(phone.holidays.value.sets.isEmpty())
        assertTrue(phone.holidaysBetween(september, september.plusMonths(1)).isEmpty())
    }
}
