package com.sentinelcore.android.organizer

import java.security.MessageDigest
import java.time.Instant
import java.time.ZoneId
import java.util.UUID
import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import org.json.JSONArray
import org.json.JSONObject

class OrganizerResponse(val status: Int, val body: ByteArray) {
    val text: String get() = String(body, Charsets.UTF_8)
}

// How the engine reaches the server. The app hands it one backed by its own repository, so the
// Organizer shares the signed-in cookie and the chosen local/remote address; the tests hand it
// one pointed at a server they started.
interface OrganizerTransport {
    suspend fun send(
        method: String,
        path: String,
        query: Map<String, String> = emptyMap(),
        body: ByteArray? = null,
        contentType: String? = null,
        timeoutMillis: Int? = null,
    ): OrganizerResponse
}

sealed class OrganizerException(message: String) : Exception(message) {
    class Offline : OrganizerException("Can't reach the server.")
    class SignInRequired : OrganizerException("Sign in to use the Organizer.")
    class TurnedOff : OrganizerException("The Organizer is turned off in the server's settings.")
    class Server(val status: Int, message: String?) : OrganizerException(message ?: "The server answered with an error (HTTP $status).")
}

// The Organizer on an Android device: this device's copy, every edit made to it, and keeping it in
// step with the server. The rules are core/docs/ORGANIZER_SYNC.md; OrganizerKit's OrganizerEngine
// (iOS/Mac/TV) and the dashboard's organizer-sync.js are the same algorithm, and should read alike.
//
// Every edit lands in the local copy at once, stamped with a fresh clock per field, and joins a
// queue that sync() pushes. The queue is on disk before an edit returns, so killing the app with no
// signal loses nothing. State is guarded by one lock; Compose reads it through `revision`, which
// changes on every edit and every sync.
class OrganizerEngine(
    private val store: OrganizerLocalStore,
    deviceId: String,
    policy: OfflinePolicy,
    private val scope: CoroutineScope,
    private val zone: () -> ZoneId = { ZoneId.systemDefault() },
) {
    data class SyncState(
        val online: Boolean = false,
        val syncing: Boolean = false,
        val lastSyncedAt: Instant? = null,
        val lastError: String? = null,
        val waitingChanges: Int = 0,
        val needsSignIn: Boolean = false,
        val turnedOff: Boolean = false,
    )

    // The server answering isn't the one this device last synced with. Nothing is sent until the
    // person decides.
    data class ResetPrompt(val serverId: String, val localCount: Int, val serverCount: Int?)

    data class StorageSummary(
        val itemCount: Int,
        val serverItemCount: Int?,
        val attachmentCount: Int,
        val attachmentsWithOriginal: Int,
        val bytesOnDevice: Long,
        val waitingUploads: Int,
    )

    private val lock = Any()
    private val clock = HybridClock(deviceId)
    private val items = HashMap<String, OrganizerItem>()
    private val archive = HashMap<String, OrganizerItem>()
    private val pending = HashMap<String, OrganizerChange>()
    private val inflight = HashMap<String, OrganizerChange>()
    private var serverId: String? = null
    private var cursor = 0L
    private var missingBlobs = emptySet<String>()
    private var childIndex: Pair<Long, Map<String, List<OrganizerItem>>>? = null

    @Volatile private var transport: OrganizerTransport? = null
    private var waitJob: Job? = null
    private var debounceJob: Job? = null
    private var blobJob: Job? = null
    private val syncMutex = Mutex()
    private val requestedPass = AtomicLong(0)
    @Volatile private var completedPass = 0L

    private val _revision = MutableStateFlow(0L)
    val revision: StateFlow<Long> = _revision.asStateFlow()
    private val _status = MutableStateFlow<OrganizerStatus?>(null)
    val status: StateFlow<OrganizerStatus?> = _status.asStateFlow()
    private val _spaces = MutableStateFlow<List<OrganizerSpace>>(emptyList())
    val spaces: StateFlow<List<OrganizerSpace>> = _spaces.asStateFlow()
    private val _syncState = MutableStateFlow(SyncState())
    val syncState: StateFlow<SyncState> = _syncState.asStateFlow()
    private val _resetPrompt = MutableStateFlow<ResetPrompt?>(null)
    val resetPrompt: StateFlow<ResetPrompt?> = _resetPrompt.asStateFlow()
    private val _policy = MutableStateFlow(policy)
    val policy: StateFlow<OfflinePolicy> = _policy.asStateFlow()
    private val _keptOffline = MutableStateFlow<Set<String>>(emptySet())
    val keptOffline: StateFlow<Set<String>> = _keptOffline.asStateFlow()
    @Volatile var plannedAlerts: List<PlannedAlert> = emptyList()
        private set
    @Volatile var onAlertsChanged: ((List<PlannedAlert>) -> Unit)? = null

    init {
        synchronized(lock) {
            // "Off" keeps nothing between launches - only what has edits that never made it up.
            if (policy.mode == OfflinePolicy.Mode.OFF) store.clearItems(store.loadPending().keys)
            store.loadItems().forEach { item ->
                items[item.id] = item
                item.clocks.values.forEach(clock::observe)
            }
            pending.putAll(store.loadPending())
            pending.values.forEach { change -> change.clocks.values.forEach(clock::observe) }
            serverId = store.meta("serverId")
            cursor = store.meta("cursor")?.toLongOrNull() ?: 0L
            store.meta("spaces")?.let { text ->
                runCatching { JSONArray(text).let { array -> (0 until array.length()).map { OrganizerJson.space(array.getJSONObject(it)) } } }
                    .getOrNull()?.let { _spaces.value = it }
            }
            store.meta("keptOffline")?.let { text ->
                runCatching { JSONArray(text).let { array -> (0 until array.length()).map { array.getString(it) }.toSet() } }
                    .getOrNull()?.let { _keptOffline.value = it }
            }
            _syncState.update { it.copy(waitingChanges = pending.size) }
        }
        replanAlerts()
    }

    // ---- reading ---------------------------------------------------------------------------

    fun items(kind: String): List<OrganizerItem> = synchronized(lock) {
        items.values.filter { it.kind == kind && !it.deleted } +
            archive.values.filter { it.kind == kind && !it.deleted && it.id !in items }
    }

    fun item(id: String): OrganizerItem? = synchronized(lock) { (items[id] ?: archive[id])?.takeIf { !it.deleted } }

    fun children(id: String, kind: String? = null): List<OrganizerItem> = synchronized(lock) {
        val revision = _revision.value
        val index = childIndex?.takeIf { it.first == revision }?.second ?: run {
            val map = HashMap<String, MutableList<OrganizerItem>>()
            (items.values + archive.values.filter { it.id !in items }).forEach { item ->
                if (!item.deleted) item.parentId?.let { map.getOrPut(it) { mutableListOf() }.add(item) }
            }
            childIndex = revision to map
            map
        }
        val all = index[id].orEmpty()
        if (kind == null) all else all.filter { it.kind == kind }
    }

    val visibleSpaces: List<OrganizerSpace> get() = _spaces.value.ifEmpty { listOf(OrganizerSpace.FAMILY) }

    fun space(id: String): OrganizerSpace? = visibleSpaces.firstOrNull { it.id == id }

    // Where something new goes when nobody says otherwise: this person's own space if there is
    // one, otherwise Family.
    val defaultSpaceId: String
        get() = visibleSpaces.firstOrNull { it.kind == OrganizerSpaceKind.PERSONAL }?.id ?: OrganizerSpace.FAMILY.id

    // ---- editing ---------------------------------------------------------------------------

    fun create(kind: String, fields: Map<String, Any?>, spaceId: String? = null, parentId: String? = null): OrganizerItem {
        val created: OrganizerItem
        synchronized(lock) {
            val parent = parentId?.let { items[it] ?: archive[it] }
            val now = Instant.now()
            val item = OrganizerItem(
                id = UUID.randomUUID().toString(), kind = kind,
                spaceId = parent?.spaceId ?: spaceId ?: defaultSpaceId,
                parentId = parent?.id,
                createdAt = now, createdBy = _status.value?.username, updatedAt = now, updatedBy = _status.value?.username,
            )
            val structural: Map<String, Any?> = if (parent != null) mapOf("parentId" to parent.id) else mapOf("spaceId" to item.spaceId)
            created = editLocked(item, structural + fields)
        }
        afterEdit()
        return created
    }

    // A null value clears the field.
    fun update(id: String, changes: Map<String, Any?>) {
        synchronized(lock) {
            val item = items[id] ?: archive[id] ?: return
            archive.remove(id)
            val moved = (changes["spaceId"] as? String)?.let { it != item.spaceId } ?: false
            val edited = editLocked(item, changes)
            if (moved) cascadeSpaceLocked(edited)
        }
        afterEdit()
    }

    fun delete(id: String) = update(id, mapOf("deleted" to true))

    // An item and everything under it - a board's columns and cards, a note's attachments.
    fun deleteTree(id: String) {
        val queue = ArrayDeque(listOf(id))
        val seen = mutableSetOf<String>()
        while (queue.isNotEmpty()) {
            val next = queue.removeLast()
            if (!seen.add(next)) continue
            queue.addAll(children(next).map { it.id })
            delete(next)
        }
    }

    private fun editLocked(original: OrganizerItem, changes: Map<String, Any?>): OrganizerItem {
        var item = original
        var change = pending[item.id] ?: OrganizerChange(item.id, item.kind)
        for ((name, value) in changes) {
            // The note body's starting point, so the server can tell two people rewrote the same
            // note rather than letting the later one silently win.
            if (name == "body" && item.kind == OrganizerKinds.NOTE && change.baseClocks?.get("body") == null &&
                inflight[item.id]?.baseClocks?.get("body") == null
            ) {
                item.clocks["body"]?.let { change = change.copy(baseClocks = mapOf("body" to it)) }
            }
            val stamp = clock.next()
            item = applyField(item, name, value, stamp)
            change = change.copy(fields = change.fields + (name to value), clocks = change.clocks + (name to stamp))
        }
        item = item.copy(updatedAt = Instant.now(), updatedBy = _status.value?.username ?: item.updatedBy)
        items[item.id] = item
        pending[item.id] = change
        store.saveItems(listOf(item))
        persistPendingLocked(item.id)
        _syncState.update { it.copy(waitingChanges = pending.size + inflight.size) }
        return item
    }

    private fun afterEdit() {
        changed()
        replanAlerts()
        scheduleSync(800)
    }

    private fun applyField(item: OrganizerItem, name: String, value: Any?, stamp: String): OrganizerItem {
        val clocks = item.clocks + (name to stamp)
        return when (name) {
            "spaceId" -> (value as? String)?.let { item.copy(spaceId = it, clocks = clocks) } ?: item.copy(clocks = clocks)
            "parentId" -> item.copy(parentId = value as? String, clocks = clocks)
            "deleted" -> item.copy(deleted = value as? Boolean ?: false, clocks = clocks)
            else -> item.copy(fields = if (value == null) item.fields - name else item.fields + (name to value), clocks = clocks)
        }
    }

    private fun cascadeSpaceLocked(root: OrganizerItem) {
        val queue = ArrayDeque(listOf(root.id))
        val touched = mutableListOf<OrganizerItem>()
        while (queue.isNotEmpty()) {
            val parentId = queue.removeFirst()
            items.values.filter { it.parentId == parentId }.forEach { child ->
                queue.add(child.id)
                if (child.spaceId != root.spaceId) {
                    val moved = child.copy(spaceId = root.spaceId)
                    items[child.id] = moved
                    touched += moved
                }
            }
        }
        store.saveItems(touched)
    }

    private fun persistPendingLocked(id: String) {
        val older = inflight[id]
        val newer = pending[id]
        store.savePending(id, when {
            older != null && newer != null -> older.merging(newer)
            else -> older ?: newer
        })
    }

    private fun changed() {
        _revision.update { it + 1 }
    }

    // ---- attachments -----------------------------------------------------------------------

    // Keeps the file on this device straight away and creates the attachment; the bytes go up
    // whenever the server is reachable. Works with no signal at all.
    fun attach(bytes: ByteArray, name: String, contentType: String, parentId: String, width: Int? = null, height: Int? = null): OrganizerItem {
        _status.value?.maxAttachmentBytes?.let { limit ->
            if (bytes.size > limit) throw OrganizerException.Server(413, "$name is larger than the ${limit / 1_048_576} MB attachment limit.")
        }
        val hash = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }
        store.originalFile(hash).writeBytes(bytes)
        store.saveBlob(BlobRecord(hash, needsUpload = true, hasOriginal = true, size = bytes.size.toLong(), lastUsedMillis = System.currentTimeMillis()))
        val fields = mutableMapOf<String, Any?>(
            "hash" to hash, "name" to name, "contentType" to contentType,
            "size" to bytes.size.toDouble(), "position" to children(parentId, OrganizerKinds.ATTACHMENT).size.toDouble(),
        )
        width?.let { fields["width"] = it.toDouble() }
        height?.let { fields["height"] = it.toDouble() }
        val item = create(OrganizerKinds.ATTACHMENT, fields, parentId = parentId)
        startBlobWork()
        return item
    }

    fun hasOriginal(hash: String) = store.originalFile(hash).exists()

    fun originalFile(hash: String) = store.originalFile(hash)

    // Image bytes to show. A thumbnail request is happy with the original if that's what's here.
    suspend fun imageBytes(hash: String, thumbnail: Boolean): ByteArray? {
        store.originalFile(hash).takeIf { it.exists() }?.let {
            touch(hash)
            return it.readBytes()
        }
        if (thumbnail) store.thumbnailFile(hash).takeIf { it.exists() }?.let { return it.readBytes() }
        return runCatching { download(hash, thumbnail) }.getOrNull()
            ?: if (!thumbnail) store.thumbnailFile(hash).takeIf { it.exists() }?.readBytes() else null
    }

    // Makes sure the original is on this device, for opening or sharing.
    suspend fun ensureOriginal(hash: String): java.io.File {
        if (!hasOriginal(hash)) download(hash, thumbnail = false)
        return store.originalFile(hash)
    }

    private suspend fun download(hash: String, thumbnail: Boolean): ByteArray {
        val response = send("GET", "/api/organizer/blobs/$hash" + if (thumbnail) "/thumbnail" else "", timeoutMillis = 120_000)
        if (thumbnail) {
            store.thumbnailFile(hash).writeBytes(response.body)
            val record = store.blob(hash) ?: BlobRecord(hash, false, false, 0, 0)
            store.saveBlob(record.copy(lastUsedMillis = System.currentTimeMillis()))
        } else {
            store.originalFile(hash).writeBytes(response.body)
            val record = store.blob(hash) ?: BlobRecord(hash, false, true, 0, 0)
            store.saveBlob(record.copy(hasOriginal = true, size = response.body.size.toLong(), lastUsedMillis = System.currentTimeMillis()))
            enforceStorageCap()
        }
        return response.body
    }

    private fun touch(hash: String) {
        store.blob(hash)?.let { store.saveBlob(it.copy(lastUsedMillis = System.currentTimeMillis())) }
    }

    private fun startBlobWork() {
        if (transport == null) return
        synchronized(lock) {
            if (blobJob?.isActive == true) return
            blobJob = scope.launch { runBlobWork() }
        }
    }

    // Uploads first (files attached here, and anything the server says it lost that this device
    // still has), then downloads by policy, then tidying.
    private suspend fun runBlobWork() {
        val attachments = synchronized(lock) { items.values.filter { it.kind == OrganizerKinds.ATTACHMENT && !it.deleted } }
        val referenced = attachments.mapNotNull { it.string("hash") }.toSet()
        val missing = synchronized(lock) { missingBlobs }

        for (record in store.allBlobs().filter { it.needsUpload || (it.hash in missing && it.hasOriginal) }) {
            val file = store.originalFile(record.hash).takeIf { it.exists() } ?: continue
            val response = try {
                transport?.send("PUT", "/api/organizer/blobs/${record.hash}", body = file.readBytes(),
                    contentType = "application/octet-stream", timeoutMillis = 300_000) ?: return
            } catch (_: Exception) {
                return
            }
            when {
                response.status in 200..299 -> {
                    store.saveBlob(record.copy(needsUpload = false))
                    synchronized(lock) { missingBlobs = missingBlobs - record.hash }
                }
                response.status == 400 || response.status == 413 -> {
                    store.saveBlob(record.copy(needsUpload = false))
                    _syncState.update { it.copy(lastError = OrganizerJson.errorMessage(response.text) ?: "An attachment was too large to upload.") }
                }
                else -> return
            }
        }

        val policy = _policy.value
        if (policy.attachments != OfflinePolicy.Attachments.NONE) {
            for (attachment in attachments) {
                val hash = attachment.string("hash") ?: continue
                val isImage = attachment.string("contentType")?.startsWith("image/") == true
                try {
                    if (policy.attachments == OfflinePolicy.Attachments.FULL && !hasOriginal(hash)) {
                        download(hash, thumbnail = false)
                    } else if (policy.attachments == OfflinePolicy.Attachments.THUMBNAILS && isImage && !hasOriginal(hash) &&
                        !store.thumbnailFile(hash).exists()
                    ) {
                        download(hash, thumbnail = true)
                    }
                } catch (_: OrganizerException.Offline) {
                    break
                } catch (error: CancellationException) {
                    throw error
                } catch (_: Exception) {
                    continue
                }
            }
        }

        store.allBlobs().filter { it.hash !in referenced && !it.needsUpload }.forEach { store.deleteBlob(it.hash) }
        enforceStorageCap()
        changed()
    }

    // Least recently looked-at originals go first; never one not yet uploaded, never thumbnails.
    private fun enforceStorageCap() {
        val cap = _policy.value.storageCapBytes ?: return
        val records = store.allBlobs().filter { it.hasOriginal }.sortedBy { it.lastUsedMillis }
        var total = records.sumOf { it.size }
        for (record in records) {
            if (total <= cap) break
            if (record.needsUpload) continue
            store.originalFile(record.hash).delete()
            store.saveBlob(record.copy(hasOriginal = false))
            total -= record.size
        }
    }

    // ---- connection ------------------------------------------------------------------------

    val isConnected: Boolean get() = transport != null

    fun connect(transport: OrganizerTransport) {
        this.transport = transport
        scope.launch {
            refreshStatus()
            sync()
            startWaiting()
        }
    }

    fun disconnect() {
        synchronized(lock) {
            waitJob?.cancel()
            debounceJob?.cancel()
            blobJob?.cancel()
            waitJob = null
            debounceJob = null
            blobJob = null
        }
        transport = null
    }

    suspend fun refreshStatus() {
        try {
            val fresh = OrganizerJson.status(JSONObject(send("GET", "/api/organizer/status").text))
            adoptOwner(fresh.username.orEmpty())
            _status.value = fresh
            _syncState.update { it.copy(turnedOff = !fresh.enabled, needsSignIn = false) }
        } catch (_: OrganizerException.SignInRequired) {
            _syncState.update { it.copy(needsSignIn = true) }
        } catch (error: CancellationException) {
            throw error
        } catch (_: Exception) {
            // Keep the last status; being offline is reported by sync.
        }
    }

    // Signing in as someone else on this device must not show them the last person's copy.
    private fun adoptOwner(owner: String) {
        val stored = store.meta("owner")
        if (stored != null && stored != owner) resetLocalCopy(keepPending = false)
        store.setMeta("owner", owner)
    }

    private fun resetLocalCopy(keepPending: Boolean) {
        synchronized(lock) {
            items.clear()
            archive.clear()
            inflight.clear()
            if (!keepPending) pending.clear()
            serverId = null
            cursor = 0
            _spaces.value = emptyList()
            store.wipe(keepPending)
            _syncState.update { it.copy(waitingChanges = pending.size) }
        }
        replanAlerts()
        changed()
    }

    private fun scheduleSync(delayMillis: Long) {
        if (transport == null) return
        synchronized(lock) {
            debounceJob?.cancel()
            debounceJob = scope.launch {
                delay(delayMillis)
                sync()
            }
        }
    }

    private fun startWaiting() {
        synchronized(lock) {
            waitJob?.cancel()
            waitJob = scope.launch {
                while (isActive) {
                    val state = _syncState.value
                    if (_resetPrompt.value != null || state.turnedOff || state.needsSignIn) {
                        delay(30_000)
                        if (state.turnedOff || state.needsSignIn) refreshStatus()
                        continue
                    }
                    try {
                        val response = send("GET", "/api/organizer/wait",
                            query = mapOf("cursor" to cursorValue().toString(), "timeout" to "25"), timeoutMillis = 40_000)
                        val answer = JSONObject(response.text).optLong("cursor")
                        if (answer != cursorValue()) sync()
                    } catch (error: CancellationException) {
                        throw error
                    } catch (_: Exception) {
                        _syncState.update { it.copy(online = false) }
                        delay(15_000)
                        sync()
                    }
                }
            }
        }
    }

    private fun cursorValue() = synchronized(lock) { cursor }

    // ---- sync ------------------------------------------------------------------------------

    // Returns once a sync pass that started after this call has finished - so an edit made just
    // before calling it has been sent. Passes never overlap. (OrganizerKit's engine explains why
    // this counts passes rather than keeping a "sync again" flag: the flag raced.)
    suspend fun sync() {
        val wanted = requestedPass.incrementAndGet()
        syncMutex.withLock {
            if (completedPass >= wanted || transport == null) {
                if (transport == null) completedPass = requestedPass.get()
                return
            }
            _syncState.update { it.copy(syncing = true) }
            try {
                while (completedPass < requestedPass.get()) {
                    val pass = requestedPass.get()
                    try {
                        syncOnce(restoreSpaces = null)
                        completedPass = maxOf(completedPass, pass)
                        _syncState.update { it.copy(online = true, needsSignIn = false, turnedOff = false, lastSyncedAt = Instant.now()) }
                    } catch (error: CancellationException) {
                        completedPass = requestedPass.get()
                        throw error
                    } catch (_: OrganizerException.SignInRequired) {
                        completedPass = requestedPass.get()
                        _syncState.update { it.copy(needsSignIn = true) }
                        return
                    } catch (_: OrganizerException.TurnedOff) {
                        completedPass = requestedPass.get()
                        _syncState.update { it.copy(turnedOff = true) }
                        return
                    } catch (error: Exception) {
                        completedPass = requestedPass.get()
                        _syncState.update {
                            it.copy(online = false, lastError = if (error is OrganizerException && error !is OrganizerException.Offline) error.message else it.lastError)
                        }
                        scheduleSync(20_000)
                        return
                    }
                }
            } finally {
                _syncState.update { it.copy(syncing = false, waitingChanges = synchronized(lock) { pending.size + inflight.size }) }
            }
        }
        startBlobWork()
    }

    private suspend fun syncOnce(restoreSpaces: List<OrganizerSpace>?) {
        if (_resetPrompt.value != null) return
        synchronized(lock) {
            for ((id, change) in pending) inflight[id] = inflight[id]?.merging(change) ?: change
            pending.clear()
        }

        var first = true
        var hasMore = true
        while (hasMore) {
            val body = synchronized(lock) {
                JSONObject().apply {
                    put("serverId", serverId ?: JSONObject.NULL)
                    put("cursor", cursor)
                    put("knownSpaces", JSONArray(_spaces.value.map { it.id }))
                    put("changes", JSONArray(if (first) inflight.values.map(OrganizerJson::changeJson) else emptyList<JSONObject>()))
                    if (first && restoreSpaces != null) put("restoreSpaces", JSONArray(restoreSpaces.map(OrganizerJson::spaceJson)))
                }
            }
            val reply = try {
                JSONObject(send("POST", "/api/organizer/sync", body = body.toString().toByteArray(), timeoutMillis = 60_000).text)
            } catch (error: Exception) {
                restoreInflight()
                throw error
            }

            if (reply.optBoolean("reset")) {
                restoreInflight()
                handleReset(reply.getString("serverId"))
                return
            }
            apply(reply, settlesInflight = first)
            first = false
            hasMore = reply.optBoolean("hasMore")
        }
    }

    private fun restoreInflight() = synchronized(lock) {
        for ((id, change) in inflight) pending[id] = pending[id]?.let { change.merging(it) } ?: change
        inflight.clear()
    }

    private fun apply(reply: JSONObject, settlesInflight: Boolean) {
        synchronized(lock) {
            val saved = mutableListOf<OrganizerItem>()
            val dropped = mutableListOf<String>()
            reply.optJSONArray("removed")?.let { array ->
                for (i in 0 until array.length()) {
                    val id = array.getString(i)
                    items.remove(id)
                    dropped += id
                }
            }

            val echoed = mutableSetOf<String>()
            reply.optJSONArray("items")?.let { array ->
                for (i in 0 until array.length()) {
                    var item = runCatching { OrganizerJson.item(array.getJSONObject(i)) }.getOrNull() ?: continue
                    echoed += item.id
                    item.clocks.values.forEach(clock::observe)
                    // Edits made while this request was out are newer than anything in it.
                    pending[item.id]?.let { newer ->
                        newer.fields.forEach { (name, value) -> item = applyField(item, name, value, newer.clocks[name].orEmpty()) }
                    }
                    items[item.id] = item
                    archive.remove(item.id)
                    saved += item
                }
            }

            if (settlesInflight) {
                reply.optJSONArray("rejected")?.let { array ->
                    for (i in 0 until array.length()) {
                        val rejection = array.getJSONObject(i)
                        val id = rejection.getString("id")
                        _syncState.update { it.copy(lastError = rejection.optString("error")) }
                        // Refused and not on the server at all - a new item that can never be made.
                        if (id !in echoed && id !in pending && items[id]?.seq == null) {
                            items.remove(id)
                            dropped += id
                        }
                    }
                }
                val settled = inflight.keys.toList()
                inflight.clear()
                settled.forEach(::persistPendingLocked)
            }

            val spaces = reply.optJSONArray("spaces")?.let { array -> (0 until array.length()).map { OrganizerJson.space(array.getJSONObject(it)) } }
                ?: emptyList()
            _spaces.value = spaces
            val visible = spaces.map { it.id }.toSet()
            items.values.filter { it.spaceId !in visible && it.id !in pending }.forEach {
                items.remove(it.id)
                dropped += it.id
            }

            val prune = _policy.value.itemsToPrune(items, pending.keys + _keptOffline.value, zone = zone())
            prune.forEach {
                items.remove(it)
                dropped += it
            }

            cursor = reply.optLong("cursor")
            serverId = reply.getString("serverId")
            missingBlobs = reply.optJSONArray("missingBlobs")?.let { array -> (0 until array.length()).map { array.getString(it) }.toSet() } ?: emptySet()
            store.saveItems(saved.filter { it.id in items })
            store.deleteItems(dropped)
            store.setMeta("cursor", cursor.toString())
            store.setMeta("serverId", serverId)
            store.setMeta("spaces", JSONArray(spaces.map(OrganizerJson::spaceJson)).toString())
        }
        replanAlerts()
        changed()
    }

    private suspend fun handleReset(newServerId: String) {
        val live = synchronized(lock) { items.values.count { !it.deleted } }
        val known = synchronized(lock) { serverId }
        if (known == null || live == 0) {
            // Nothing here worth offering - take the server's copy.
            resetLocalCopy(keepPending = known == null)
            synchronized(lock) {
                serverId = newServerId
                store.setMeta("serverId", newServerId)
            }
            requestedPass.incrementAndGet()
            return
        }
        refreshStatus()
        _resetPrompt.value = ResetPrompt(newServerId, live, _status.value?.itemCount)
    }

    // The person's answer to a ResetPrompt. Uploading sends every item with the clocks it
    // already has, so the new server merges it exactly like the original edits and newer edits
    // already there still win; groups come back with it.
    suspend fun resolveReset(upload: Boolean) {
        val prompt = _resetPrompt.value ?: return
        _resetPrompt.value = null
        if (!upload) {
            resetLocalCopy(keepPending = false)
            synchronized(lock) {
                serverId = prompt.serverId
                store.setMeta("serverId", prompt.serverId)
            }
            sync()
            return
        }
        val groups: List<OrganizerSpace>
        synchronized(lock) {
            val restore = HashMap<String, OrganizerChange>()
            for (item in items.values) {
                val fields = item.fields.toMutableMap<String, Any?>()
                fields["spaceId"] = item.spaceId
                fields["deleted"] = item.deleted
                item.parentId?.let { fields["parentId"] = it }
                val fallback = clock.next()
                restore[item.id] = OrganizerChange(item.id, item.kind, fields, fields.keys.associateWith { item.clocks[it] ?: fallback })
            }
            for ((id, change) in pending) restore[id] = restore[id]?.merging(change) ?: change
            pending.clear()
            inflight.clear()
            inflight.putAll(restore)
            restore.keys.forEach(::persistPendingLocked)
            serverId = prompt.serverId
            cursor = 0
            store.setMeta("serverId", prompt.serverId)
            store.setMeta("cursor", "0")
            groups = _spaces.value.filter { it.kind == OrganizerSpaceKind.GROUP }
        }
        syncMutex.withLock {
            _syncState.update { it.copy(syncing = true) }
            try {
                syncOnce(restoreSpaces = groups)
                _syncState.update { it.copy(online = true, lastSyncedAt = Instant.now()) }
                synchronized(lock) { missingBlobs = missingBlobs + store.allBlobs().filter { it.hasOriginal }.map { it.hash } }
            } catch (error: CancellationException) {
                throw error
            } catch (_: Exception) {
                _syncState.update { it.copy(online = false) }
            } finally {
                _syncState.update { it.copy(syncing = false, waitingChanges = synchronized(lock) { pending.size + inflight.size }) }
            }
        }
        startBlobWork()
    }

    // ---- policy & storage ------------------------------------------------------------------

    fun setPolicy(newPolicy: OfflinePolicy) {
        val widened = newPolicy.widens(_policy.value)
        _policy.value = newPolicy
        synchronized(lock) {
            if (widened) {
                // Items thrown away under the old policy are behind the cursor; read everything once.
                cursor = 0
                store.setMeta("cursor", "0")
            } else {
                val prune = newPolicy.itemsToPrune(items, pending.keys + _keptOffline.value, zone = zone())
                prune.forEach { items.remove(it) }
                store.deleteItems(prune)
            }
        }
        changed()
        scope.launch {
            sync()
            enforceStorageCap()
        }
    }

    fun setKeptOffline(id: String, keep: Boolean) {
        _keptOffline.update { if (keep) it + id else it - id }
        store.setMeta("keptOffline", JSONArray(_keptOffline.value.toList()).toString())
    }

    fun storageSummary(): StorageSummary = synchronized(lock) {
        val attachments = items.values.filter { it.kind == OrganizerKinds.ATTACHMENT && !it.deleted }
        val blobs = store.allBlobs()
        val bytes = blobs.filter { it.hasOriginal }.sumOf { it.size } +
            (store.thumbnailDirectory.listFiles()?.sumOf { it.length() } ?: 0L)
        StorageSummary(
            itemCount = items.values.count { !it.deleted },
            serverItemCount = _status.value?.itemCount,
            attachmentCount = attachments.size,
            attachmentsWithOriginal = attachments.count { a -> a.string("hash")?.let(::hasOriginal) == true },
            bytesOnDevice = bytes,
            waitingUploads = blobs.count { it.needsUpload },
        )
    }

    fun clearError() = _syncState.update { it.copy(lastError = null) }

    // ---- alerts ----------------------------------------------------------------------------

    private fun replanAlerts() {
        val planned = AlertPlanner.plan(synchronized(lock) { items.values.toList() }, zone = zone())
        if (planned == plannedAlerts) return
        plannedAlerts = planned
        onAlertsChanged?.invoke(planned)
    }

    fun refreshAlerts() {
        plannedAlerts = AlertPlanner.plan(synchronized(lock) { items.values.toList() }, zone = zone())
        onAlertsChanged?.invoke(plannedAlerts)
    }

    // ---- online-only actions ---------------------------------------------------------------

    // Everything of one kind, including what the offline window leaves out. Memory only.
    suspend fun loadArchive(kind: String) {
        val array = JSONObject(send("GET", "/api/organizer/items", query = mapOf("kind" to kind)).text).optJSONArray("items") ?: return
        synchronized(lock) {
            for (i in 0 until array.length()) {
                val item = OrganizerJson.item(array.getJSONObject(i))
                if (item.id !in items) archive[item.id] = item
            }
        }
        changed()
    }

    suspend fun people(): List<OrganizerPerson> {
        val array = JSONObject(send("GET", "/api/organizer/people").text).optJSONArray("people") ?: return emptyList()
        return (0 until array.length()).map { i ->
            val json = array.getJSONObject(i)
            OrganizerPerson(json.getString("username"), json.optString("displayName").takeIf { it.isNotEmpty() && it != "null" })
        }
    }

    suspend fun saveGroup(id: String?, name: String, members: List<String>, color: String?) {
        val body = JSONObject().apply {
            put("name", name)
            put("members", JSONArray(members))
            put("color", color ?: JSONObject.NULL)
        }.toString().toByteArray()
        if (id != null) send("PUT", "/api/organizer/spaces/${java.net.URLEncoder.encode(id, "UTF-8")}", body = body)
        else send("POST", "/api/organizer/spaces", body = body)
        sync()
    }

    suspend fun deleteGroup(id: String) {
        send("DELETE", "/api/organizer/spaces/${java.net.URLEncoder.encode(id, "UTF-8")}")
        sync()
    }

    suspend fun calendarFeedPath(reset: Boolean = false): String {
        val response = if (reset) send("POST", "/api/organizer/calendar-feed/reset") else send("GET", "/api/organizer/calendar-feed")
        return JSONObject(response.text).getString("path")
    }

    // ---- transport -------------------------------------------------------------------------

    private suspend fun send(
        method: String,
        path: String,
        query: Map<String, String> = emptyMap(),
        body: ByteArray? = null,
        timeoutMillis: Int? = null,
    ): OrganizerResponse {
        val transport = transport ?: throw OrganizerException.Offline()
        val response = transport.send(method, path, query, body, if (body == null) null else "application/json", timeoutMillis)
        return when {
            response.status in 200..299 -> response
            response.status == 401 -> throw OrganizerException.SignInRequired()
            response.status == 404 && OrganizerJson.errorMessage(response.text)?.contains("turned off") == true ->
                throw OrganizerException.TurnedOff()
            else -> throw OrganizerException.Server(response.status, OrganizerJson.errorMessage(response.text))
        }
    }
}
