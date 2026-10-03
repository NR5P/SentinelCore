package com.sentinelcore.android.audio

import android.util.Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.IOException

// Plays and progress made with no connection (offline downloads on a plane) are kept here and
// sent once the server answers again. A play carries the real `playedAt`, so Home, Stats and
// ListenBrainz still put it at the right time; progress keeps only the latest per episode.
class ReportQueue(private val runtime: AudioRuntime) {
    private val file: File get() = File(runtime.context.filesDir, "audio/reports.json")
    private val lock = Mutex()

    data class Pending(val method: String, val path: String, val body: String, val replaceKey: String?)

    // Sends now; on a network failure keeps it for later. A server refusal (4xx) is dropped: it
    // will never succeed, and retrying it forever would block the rest.
    suspend fun send(method: String, path: String, body: JSONObject, replaceKey: String? = null) {
        try {
            runtime.repository.organizerRequest(method, path, emptyMap(), body.toString().toByteArray(), "application/json", null).let { (status, _) ->
                if (status >= 500) throw IOException("HTTP $status")
            }
            flush()
        } catch (error: IOException) {
            Log.d(AudioApi.TAG, "Keeping $path for later: ${error.message}")
            add(Pending(method, path, body.toString(), replaceKey))
        }
    }

    suspend fun pendingCount(): Int = lock.withLock { read().size }

    suspend fun flush() {
        lock.withLock {
            val pending = read()
            if (pending.isEmpty()) return
            val left = mutableListOf<Pending>()
            var offline = false
            for (item in pending) {
                if (offline) {
                    left.add(item)
                    continue
                }
                try {
                    val (status, _) = runtime.repository.organizerRequest(item.method, item.path, emptyMap(), item.body.toByteArray(), "application/json", null)
                    if (status >= 500) {
                        left.add(item)
                        offline = true
                    }
                } catch (_: IOException) {
                    left.add(item)
                    offline = true
                }
            }
            write(left)
        }
    }

    private suspend fun add(item: Pending) = lock.withLock {
        val list = read().toMutableList()
        if (item.replaceKey != null) list.removeAll { it.replaceKey == item.replaceKey }
        list.add(item)
        // A bound on a phone left offline for months.
        write(list.takeLast(2000))
    }

    private suspend fun read(): List<Pending> = withContext(Dispatchers.IO) {
        if (!file.exists()) return@withContext emptyList()
        runCatching {
            JSONArray(file.readText()).objects {
                Pending(it.getString("method"), it.getString("path"), it.getString("body"), it.str("replaceKey"))
            }
        }.getOrDefault(emptyList())
    }

    private suspend fun write(list: List<Pending>) = withContext(Dispatchers.IO) {
        file.parentFile?.mkdirs()
        val array = JSONArray()
        list.forEach { array.put(JSONObject().put("method", it.method).put("path", it.path).put("body", it.body).putOpt2("replaceKey", it.replaceKey)) }
        file.writeText(array.toString())
    }
}
