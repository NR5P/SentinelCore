package com.sentinelcore.android.organizer

import java.io.File
import java.nio.file.Files
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import org.json.JSONArray
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

// A server that answers what each test tells it to, and remembers what it was asked - for the
// engine behaviours that don't depend on the real merge rules. Those run against the real server
// in OrganizerLiveServerTest.
class ScriptedTransport(var handler: (String, String, ByteArray?) -> OrganizerResponse) : OrganizerTransport {
    val requests = mutableListOf<Pair<String, ByteArray?>>()
    @Volatile var syncDelayMillis = 0L

    override suspend fun send(method: String, path: String, query: Map<String, String>, body: ByteArray?, contentType: String?, timeoutMillis: Int?): OrganizerResponse {
        synchronized(requests) { requests += path to body }
        if (path == "/api/organizer/wait") delay(3_600_000)
        if (path == "/api/organizer/sync" && syncDelayMillis > 0) delay(syncDelayMillis)
        return handler(method, path, body)
    }

    fun syncBodies(): List<JSONObject> = synchronized(requests) { requests.filter { it.first == "/api/organizer/sync" }.map { JSONObject(String(it.second!!)) } }

    companion object {
        fun json(text: String, status: Int = 200) = OrganizerResponse(status, text.toByteArray())
        fun status(username: String? = null, serverId: String = "server-a") = json(JSONObject().apply {
            put("enabled", true); put("serverId", serverId); put("username", username ?: JSONObject.NULL)
            put("personalSpaces", username != null); put("itemCount", 0); put("maxAttachmentBytes", 52_428_800)
        }.toString())
        fun reply(serverId: String = "server-a", reset: Boolean = false, cursor: Int = 1, spaces: JSONArray? = null,
                  items: JSONArray = JSONArray(), rejected: JSONArray = JSONArray()) = json(JSONObject().apply {
            put("serverId", serverId); put("reset", reset); put("cursor", cursor); put("hasMore", false)
            put("spaces", spaces ?: JSONArray().put(JSONObject("""{"id":"family","kind":"Family","name":"Family","members":[]}""")))
            put("items", items); put("removed", JSONArray()); put("rejected", rejected); put("missingBlobs", JSONArray())
        }.toString())
    }
}

class OrganizerEngineTest {
    private val directories = mutableListOf<File>()
    private val scopes = mutableListOf<CoroutineScope>()

    @After
    fun tearDown() {
        scopes.forEach { it.cancel() }
        directories.forEach { it.deleteRecursively() }
    }

    private fun directory() = Files.createTempDirectory("organizer-engine").toFile().also { directories += it }

    private fun engine(store: OrganizerLocalStore, policy: OfflinePolicy = OfflinePolicy.FULL_DEFAULT): OrganizerEngine {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO).also { scopes += it }
        return OrganizerEngine(store, "phone", policy, scope)
    }

    private fun onlyStatus(handler: (String, String, ByteArray?) -> OrganizerResponse) =
        ScriptedTransport { method, path, body -> if (path == "/api/organizer/status") ScriptedTransport.status() else handler(method, path, body) }

    private suspend fun settle(engine: OrganizerEngine) {
        delay(100)
        engine.sync()
    }

    @Test
    fun `edits made offline survive a restart and go up as one change`() = runBlocking {
        val store = InMemoryOrganizerStore(directory())
        val first = engine(store)
        val note = first.create(OrganizerKinds.NOTE, mapOf("title" to "Groceries", "body" to "milk"), spaceId = "family")
        first.update(note.id, mapOf("body" to "milk, eggs"))
        first.update(note.id, mapOf("body" to "milk, eggs, bread"))
        assertEquals(1, first.syncState.value.waitingChanges)

        // The app was killed with no signal. The next launch still has the note and the edit.
        val second = engine(store)
        assertEquals("milk, eggs, bread", second.item(note.id)?.string("body"))
        val transport = onlyStatus { _, _, _ -> ScriptedTransport.reply() }
        second.connect(transport)
        settle(second)

        val sent = transport.syncBodies().first().getJSONArray("changes")
        assertEquals(1, sent.length())
        val fields = sent.getJSONObject(0).getJSONObject("fields")
        assertEquals("milk, eggs, bread", fields.getString("body"))
        assertEquals("family", fields.getString("spaceId"))
        assertEquals(setOf("title", "body", "spaceId"), sent.getJSONObject(0).getJSONObject("clocks").keySet())
    }

    // The app coming forward while a background pass (syncOnce) is running must still end up
    // connected. It used to see the borrowed transport, skip its own connect, and then be left with
    // nothing when the pass hung up - so nothing it added synced until the connection next changed.
    @Test
    fun `the app still connects while a background pass is running`() = runBlocking {
        val engine = engine(InMemoryOrganizerStore(directory()))
        val transport = onlyStatus { _, _, _ -> ScriptedTransport.reply() }
        transport.syncDelayMillis = 400
        val background = async(Dispatchers.IO) { engine.syncOnce(transport) }
        delay(100)
        assertFalse("a borrowed transport isn't the app's connection", engine.isConnected)
        engine.connect(transport)
        assertTrue(engine.isConnected)
        background.await()
        assertTrue("the background pass finishing must not hang up the app's connection", engine.isConnected)
        engine.disconnect()
    }

    @Test
    fun `a failed sync keeps everything queued`() = runBlocking {
        val engine = engine(InMemoryOrganizerStore(directory()))
        engine.connect(onlyStatus { _, _, _ -> throw OrganizerException.Offline() })
        settle(engine)
        engine.create(OrganizerKinds.REMINDER, mapOf("title" to "Call vet"), spaceId = "family")
        engine.sync()
        assertFalse(engine.syncState.value.online)
        assertEquals(1, engine.syncState.value.waitingChanges)
        assertEquals(listOf("Call vet"), engine.items(OrganizerKinds.REMINDER).map { it.title })
    }

    @Test
    fun `a sync called after an edit made mid-sync sends that edit`() = runBlocking {
        val engine = engine(InMemoryOrganizerStore(directory()))
        val transport = onlyStatus { _, _, _ -> ScriptedTransport.reply() }
        engine.connect(transport)
        settle(engine)

        transport.syncDelayMillis = 200
        engine.create(OrganizerKinds.NOTE, mapOf("title" to "first"), spaceId = "family")
        val first = async(Dispatchers.IO) { engine.sync() }
        delay(50)
        val second = engine.create(OrganizerKinds.NOTE, mapOf("title" to "second"), spaceId = "family")
        engine.sync()
        val sentIds = transport.syncBodies().flatMap { body ->
            val changes = body.getJSONArray("changes")
            (0 until changes.length()).map { changes.getJSONObject(it).getString("id") }
        }
        assertTrue("the edit made mid-sync was not sent before sync() returned", second.id in sentIds)
        first.await()
    }

    // Unreachable for requests begun while `down` is set, however long they take to fail - like a
    // long poll left over from before the network dropped.
    private class FlakyTransport : OrganizerTransport {
        @Volatile var down = false
        val sentIds = mutableListOf<String>()

        override suspend fun send(method: String, path: String, query: Map<String, String>, body: ByteArray?, contentType: String?, timeoutMillis: Int?): OrganizerResponse {
            val wasDown = down
            if (path == "/api/organizer/wait") delay(3_600_000)
            if (path == "/api/organizer/status") return ScriptedTransport.status()
            delay(200)
            if (wasDown) throw OrganizerException.Offline()
            val changes = JSONObject(String(body!!)).getJSONArray("changes")
            synchronized(sentIds) { (0 until changes.length()).forEach { sentIds += changes.getJSONObject(it).getString("id") } }
            return ScriptedTransport.reply()
        }
    }

    @Test
    fun `a sync after reconnecting is not answered by a pass that failed while offline`() = runBlocking {
        val engine = engine(InMemoryOrganizerStore(directory()))
        val transport = FlakyTransport()
        engine.connect(transport)
        settle(engine)

        transport.down = true
        val failing = async(Dispatchers.IO) { engine.sync() }
        delay(50)
        transport.down = false
        val note = engine.create(OrganizerKinds.NOTE, mapOf("title" to "written as the network came back"), spaceId = "family")
        engine.sync()
        assertTrue("the sync after reconnecting was swallowed by the failed pass", synchronized(transport.sentIds) { note.id in transport.sentIds })
        failing.await()
    }

    @Test
    fun `a note body edit carries the clock it started from`() = runBlocking {
        val engine = engine(InMemoryOrganizerStore(directory()))
        val transport = onlyStatus { _, _, _ ->
            ScriptedTransport.reply(items = JSONArray().put(JSONObject("""{"id":"n1","kind":"note","spaceId":"family","parentId":null,"deleted":false,
                "fields":{"title":"Trip","body":"pack"},"clocks":{"title":"1790000000000-0000-web","body":"1790000000000-0000-web","spaceId":"1790000000000-0000-web"},"seq":1}""")))
        }
        engine.connect(transport)
        settle(engine)
        transport.handler = { _, _, _ -> ScriptedTransport.reply(cursor = 2) }
        engine.update("n1", mapOf("body" to "pack bags"))
        engine.update("n1", mapOf("body" to "pack bags, snacks"))
        engine.sync()
        val change = transport.syncBodies().last().getJSONArray("changes").getJSONObject(0)
        assertEquals("1790000000000-0000-web", change.getJSONObject("baseClocks").getString("body"))
    }

    @Test
    fun `a rebuilt server asks before uploading anything`() = runBlocking {
        val engine = engine(InMemoryOrganizerStore(directory()))
        val transport = onlyStatus { _, _, _ -> ScriptedTransport.reply("server-a") }
        engine.connect(transport)
        settle(engine)
        engine.create(OrganizerKinds.NOTE, mapOf("title" to "Keep me"), spaceId = "family")
        engine.sync()

        transport.handler = { _, path, _ ->
            if (path == "/api/organizer/status") ScriptedTransport.status(serverId = "server-b") else ScriptedTransport.reply("server-b", reset = true, cursor = 0)
        }
        engine.create(OrganizerKinds.NOTE, mapOf("title" to "Written after"), spaceId = "family")
        engine.sync()
        assertEquals("server-b", engine.resetPrompt.value?.serverId)
        assertEquals(2, engine.resetPrompt.value?.localCount)
        assertEquals(1, engine.syncState.value.waitingChanges)
        assertEquals(2, engine.items(OrganizerKinds.NOTE).size)
    }

    @Test
    fun `signing in as someone else starts from an empty copy`() = runBlocking {
        val engine = engine(InMemoryOrganizerStore(directory()))
        val transport = ScriptedTransport { _, path, _ ->
            if (path == "/api/organizer/status") ScriptedTransport.status("nathan")
            else ScriptedTransport.reply(items = JSONArray().put(JSONObject("""{"id":"diary","kind":"note","spaceId":"family","deleted":false,"fields":{"title":"Mine"},"clocks":{},"seq":1}""")))
        }
        engine.connect(transport)
        settle(engine)
        assertEquals(1, engine.items(OrganizerKinds.NOTE).size)
        transport.handler = { _, path, _ -> if (path == "/api/organizer/status") ScriptedTransport.status("terry") else ScriptedTransport.reply(cursor = 0) }
        engine.refreshStatus()
        assertTrue(engine.items(OrganizerKinds.NOTE).isEmpty())
    }

    @Test
    fun `losing access to a group drops what was in it`() = runBlocking {
        val engine = engine(InMemoryOrganizerStore(directory()))
        val family = JSONObject("""{"id":"family","kind":"Family","name":"Family","members":[]}""")
        val group = JSONObject("""{"id":"group:abc","kind":"Group","name":"Parents","members":["nathan","terry"]}""")
        val transport = onlyStatus { _, _, _ ->
            ScriptedTransport.reply(spaces = JSONArray().put(family).put(group),
                items = JSONArray().put(JSONObject("""{"id":"gift","kind":"reminder","spaceId":"group:abc","deleted":false,"fields":{"title":"Bike"},"clocks":{},"seq":1}""")))
        }
        engine.connect(transport)
        settle(engine)
        assertEquals(listOf("Bike"), engine.items(OrganizerKinds.REMINDER).map { it.title })
        transport.handler = { _, _, _ -> ScriptedTransport.reply(cursor = 2, spaces = JSONArray().put(family)) }
        engine.sync()
        assertTrue(engine.items(OrganizerKinds.REMINDER).isEmpty())
    }

    @Test
    fun `a new item the server refuses is removed and explained`() = runBlocking {
        val engine = engine(InMemoryOrganizerStore(directory()))
        val transport = onlyStatus { _, _, _ -> ScriptedTransport.reply() }
        engine.connect(transport)
        settle(engine)
        val doomed = engine.create(OrganizerKinds.NOTE, mapOf("title" to "x"), spaceId = "personal:someone")
        transport.handler = { _, _, _ -> ScriptedTransport.reply(cursor = 2, rejected = JSONArray().put(JSONObject().put("id", doomed.id).put("error", "You don't have access to that space."))) }
        engine.sync()
        assertNull(engine.item(doomed.id))
        assertEquals("You don't have access to that space.", engine.syncState.value.lastError)
        assertEquals(0, engine.syncState.value.waitingChanges)
    }

    @Test
    fun `deleting a board deletes its columns and cards, and children take their parent's space`() {
        val engine = engine(InMemoryOrganizerStore(directory()))
        val board = engine.create(OrganizerKinds.BOARD, mapOf("title" to "House"), spaceId = "family")
        val column = engine.create(OrganizerKinds.COLUMN, mapOf("title" to "To do"), parentId = board.id)
        val card = engine.create(OrganizerKinds.CARD, mapOf("title" to "Paint"), parentId = column.id)
        assertEquals("family", card.spaceId)
        engine.deleteTree(board.id)
        assertTrue(engine.items(OrganizerKinds.BOARD).isEmpty())
        assertTrue(engine.items(OrganizerKinds.CARD).isEmpty())
    }

    @Test
    fun `attaching works offline and keeps the file`() {
        val engine = engine(InMemoryOrganizerStore(directory()))
        val note = engine.create(OrganizerKinds.NOTE, mapOf("title" to "Photo"), spaceId = "family")
        val attachment = engine.attach("not really a photo".toByteArray(), "photo.jpg", "image/jpeg", note.id)
        val hash = attachment.string("hash")!!
        assertEquals(64, hash.length)
        assertTrue(engine.hasOriginal(hash))
        assertEquals(1, engine.storageSummary().waitingUploads)
    }

    @Test
    fun `off mode keeps nothing between launches except unsent edits`() = runBlocking {
        val store = InMemoryOrganizerStore(directory())
        val off = OfflinePolicy.PHONE_DEFAULT.copy(mode = OfflinePolicy.Mode.OFF)
        val first = engine(store, off)
        first.connect(onlyStatus { _, _, _ ->
            ScriptedTransport.reply(items = JSONArray().put(JSONObject("""{"id":"synced","kind":"note","spaceId":"family","deleted":false,"fields":{"title":"From server"},"clocks":{},"seq":1}""")))
        })
        settle(first)
        first.disconnect()
        first.create(OrganizerKinds.NOTE, mapOf("title" to "Unsent"), spaceId = "family")
        assertEquals(2, first.items(OrganizerKinds.NOTE).size)

        val second = engine(store, off)
        assertEquals(listOf("Unsent"), second.items(OrganizerKinds.NOTE).map { it.title })
        assertEquals(1, second.syncState.value.waitingChanges)
    }

    @Test
    fun `holidays are off by default and the choice is kept`() {
        val store = InMemoryOrganizerStore(directory())
        val first = engine(store)
        assertTrue(first.holidays.value.sets.isEmpty())
        first.setHolidaySets(listOf("christian", "christian", "jewish"))
        assertEquals(listOf("christian", "jewish"), first.holidays.value.sets)
        val second = engine(store)
        assertEquals(listOf("christian", "jewish"), second.holidays.value.sets)
        assertTrue("an unsent choice is still unsent after a restart", second.holidays.value.dirty)
    }

    @Test
    fun `a rebuilt server gets the holiday choice back from the phone`() = runBlocking {
        val store = InMemoryOrganizerStore(directory())
        engine(store).setHolidaySets(listOf("jewish"))
        val engine = engine(store)
        var sent: String? = null
        val transport = ScriptedTransport { method, path, body ->
            when {
                path == "/api/organizer/status" -> ScriptedTransport.status()
                path == "/api/organizer/holidays/sets" -> ScriptedTransport.json("""{"sets":[{"id":"jewish","name":"Jewish holidays","description":""}]}""")
                path == "/api/organizer/preferences" && method == "PUT" -> {
                    sent = String(body!!)
                    ScriptedTransport.json(sent!!)
                }
                path == "/api/organizer/preferences" -> ScriptedTransport.json("""{"holidaySets":null}""")
                path == "/api/organizer/holidays" -> ScriptedTransport.json(
                    """{"holidays":[{"id":"holiday:jewish:20260912:rosh-hashanah","setId":"jewish","name":"Rosh Hashanah","date":"2026-09-12","days":2}]}""")
                else -> ScriptedTransport.reply()
            }
        }
        engine.connect(transport)
        settle(engine)
        engine.refreshHolidays(force = true)
        assertEquals(listOf("jewish"), HolidayJson.strings(JSONObject(sent!!).getJSONArray("holidaySets")))
        assertFalse(engine.holidays.value.dirty)
        assertEquals(listOf("Rosh Hashanah"), engine.holidays.value.list.map { it.name })
        assertEquals("Jewish holidays", engine.holidaySetName("jewish"))
    }
}
