package com.sentinelcore.android.organizer

import android.content.ContentValues
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper
import java.io.File
import org.json.JSONObject

data class BlobRecord(
    val hash: String,
    val needsUpload: Boolean,
    val hasOriginal: Boolean,
    val size: Long,
    val lastUsedMillis: Long,
)

// This device's copy: items, the queue of unacknowledged edits, sync state and the index of
// attachment files. An interface so the engine's tests run on the JVM against memory, while the
// app uses SQLite - see SqliteOrganizerStore.
interface OrganizerLocalStore {
    val blobDirectory: File
    val thumbnailDirectory: File

    fun loadItems(): List<OrganizerItem>
    fun saveItems(items: Collection<OrganizerItem>)
    fun deleteItems(ids: Collection<String>)
    fun clearItems(keeping: Set<String>)

    fun loadPending(): Map<String, OrganizerChange>
    fun savePending(id: String, change: OrganizerChange?)

    fun meta(key: String): String?
    fun setMeta(key: String, value: String?)

    fun blob(hash: String): BlobRecord?
    fun allBlobs(): List<BlobRecord>
    fun saveBlob(record: BlobRecord)
    fun deleteBlob(hash: String)

    // Everything, except edits still waiting to go up when keepPending is set.
    fun wipe(keepPending: Boolean)

    fun originalFile(hash: String) = File(blobDirectory, hash)
    fun thumbnailFile(hash: String) = File(thumbnailDirectory, "$hash.jpg")
}

class InMemoryOrganizerStore(root: File) : OrganizerLocalStore {
    override val blobDirectory = File(root, "blobs").apply { mkdirs() }
    override val thumbnailDirectory = File(root, "thumbnails").apply { mkdirs() }
    private val items = LinkedHashMap<String, OrganizerItem>()
    private val pending = LinkedHashMap<String, OrganizerChange>()
    private val meta = HashMap<String, String>()
    private val blobs = HashMap<String, BlobRecord>()

    @Synchronized override fun loadItems() = items.values.toList()
    @Synchronized override fun saveItems(items: Collection<OrganizerItem>) { items.forEach { this.items[it.id] = it } }
    @Synchronized override fun deleteItems(ids: Collection<String>) { ids.forEach { items.remove(it) } }
    @Synchronized override fun clearItems(keeping: Set<String>) { items.keys.retainAll(keeping) }
    @Synchronized override fun loadPending() = pending.toMap()
    @Synchronized override fun savePending(id: String, change: OrganizerChange?) {
        if (change == null) pending.remove(id) else pending[id] = change
    }
    @Synchronized override fun meta(key: String) = meta[key]
    @Synchronized override fun setMeta(key: String, value: String?) { if (value == null) meta.remove(key) else meta[key] = value }
    @Synchronized override fun blob(hash: String) = blobs[hash]
    @Synchronized override fun allBlobs() = blobs.values.toList()
    @Synchronized override fun saveBlob(record: BlobRecord) { blobs[record.hash] = record }
    @Synchronized override fun deleteBlob(hash: String) {
        blobs.remove(hash)
        originalFile(hash).delete()
        thumbnailFile(hash).delete()
    }
    @Synchronized override fun wipe(keepPending: Boolean) {
        items.clear()
        meta.clear()
        if (!keepPending) {
            pending.clear()
            blobs.clear()
            blobDirectory.listFiles()?.forEach { it.delete() }
            thumbnailDirectory.listFiles()?.forEach { it.delete() }
        }
    }
}

// Items are stored whole as JSON - only this device reads them, the engine holds them in memory
// while running, and JSON is what lets fields this build doesn't know about survive.
class SqliteOrganizerStore(context: Context, root: File) :
    SQLiteOpenHelper(context, File(root, "organizer.sqlite").absolutePath, null, 1), OrganizerLocalStore {

    override val blobDirectory = File(root, "blobs").apply { mkdirs() }
    override val thumbnailDirectory = File(root, "thumbnails").apply { mkdirs() }

    override fun onCreate(db: SQLiteDatabase) {
        db.execSQL("CREATE TABLE items (id TEXT PRIMARY KEY, json TEXT NOT NULL)")
        db.execSQL("CREATE TABLE pending (id TEXT PRIMARY KEY, json TEXT NOT NULL)")
        db.execSQL("CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)")
        db.execSQL(
            "CREATE TABLE blobs (hash TEXT PRIMARY KEY, needsUpload INTEGER NOT NULL, hasOriginal INTEGER NOT NULL, " +
                "size INTEGER NOT NULL, lastUsed INTEGER NOT NULL)",
        )
    }

    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) = Unit

    override fun loadItems(): List<OrganizerItem> = readableDatabase.rawQuery("SELECT json FROM items", null).use { cursor ->
        buildList { while (cursor.moveToNext()) runCatching { add(OrganizerJson.item(JSONObject(cursor.getString(0)))) } }
    }

    override fun saveItems(items: Collection<OrganizerItem>) {
        if (items.isEmpty()) return
        val db = writableDatabase
        db.beginTransaction()
        try {
            items.forEach { item ->
                db.insertWithOnConflict("items", null, ContentValues().apply {
                    put("id", item.id)
                    put("json", OrganizerJson.itemJson(item).toString())
                }, SQLiteDatabase.CONFLICT_REPLACE)
            }
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
    }

    override fun deleteItems(ids: Collection<String>) {
        if (ids.isEmpty()) return
        val db = writableDatabase
        db.beginTransaction()
        try {
            ids.forEach { db.delete("items", "id = ?", arrayOf(it)) }
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
    }

    override fun clearItems(keeping: Set<String>) {
        deleteItems(loadItems().map { it.id }.filterNot { it in keeping })
    }

    override fun loadPending(): Map<String, OrganizerChange> = readableDatabase.rawQuery("SELECT id, json FROM pending", null).use { cursor ->
        buildMap { while (cursor.moveToNext()) runCatching { put(cursor.getString(0), OrganizerJson.change(JSONObject(cursor.getString(1)))) } }
    }

    override fun savePending(id: String, change: OrganizerChange?) {
        if (change == null) {
            writableDatabase.delete("pending", "id = ?", arrayOf(id))
        } else {
            writableDatabase.insertWithOnConflict("pending", null, ContentValues().apply {
                put("id", id)
                put("json", OrganizerJson.changeJson(change).toString())
            }, SQLiteDatabase.CONFLICT_REPLACE)
        }
    }

    override fun meta(key: String): String? =
        readableDatabase.rawQuery("SELECT value FROM meta WHERE key = ?", arrayOf(key)).use { if (it.moveToFirst()) it.getString(0) else null }

    override fun setMeta(key: String, value: String?) {
        if (value == null) {
            writableDatabase.delete("meta", "key = ?", arrayOf(key))
        } else {
            writableDatabase.insertWithOnConflict("meta", null, ContentValues().apply {
                put("key", key)
                put("value", value)
            }, SQLiteDatabase.CONFLICT_REPLACE)
        }
    }

    override fun blob(hash: String): BlobRecord? =
        readableDatabase.rawQuery("SELECT hash, needsUpload, hasOriginal, size, lastUsed FROM blobs WHERE hash = ?", arrayOf(hash))
            .use { if (it.moveToFirst()) record(it) else null }

    override fun allBlobs(): List<BlobRecord> =
        readableDatabase.rawQuery("SELECT hash, needsUpload, hasOriginal, size, lastUsed FROM blobs", null)
            .use { cursor -> buildList { while (cursor.moveToNext()) add(record(cursor)) } }

    override fun saveBlob(record: BlobRecord) {
        writableDatabase.insertWithOnConflict("blobs", null, ContentValues().apply {
            put("hash", record.hash)
            put("needsUpload", if (record.needsUpload) 1 else 0)
            put("hasOriginal", if (record.hasOriginal) 1 else 0)
            put("size", record.size)
            put("lastUsed", record.lastUsedMillis)
        }, SQLiteDatabase.CONFLICT_REPLACE)
    }

    override fun deleteBlob(hash: String) {
        writableDatabase.delete("blobs", "hash = ?", arrayOf(hash))
        originalFile(hash).delete()
        thumbnailFile(hash).delete()
    }

    override fun wipe(keepPending: Boolean) {
        writableDatabase.execSQL("DELETE FROM items")
        writableDatabase.execSQL("DELETE FROM meta")
        if (!keepPending) {
            writableDatabase.execSQL("DELETE FROM pending")
            writableDatabase.execSQL("DELETE FROM blobs")
            blobDirectory.listFiles()?.forEach { it.delete() }
            thumbnailDirectory.listFiles()?.forEach { it.delete() }
        }
    }

    private fun record(cursor: android.database.Cursor) = BlobRecord(
        cursor.getString(0), cursor.getInt(1) != 0, cursor.getInt(2) != 0, cursor.getLong(3), cursor.getLong(4),
    )
}
