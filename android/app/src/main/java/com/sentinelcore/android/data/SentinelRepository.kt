package com.sentinelcore.android.data

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.LruCache
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.net.CookieManager
import java.net.CookiePolicy
import java.net.HttpURLConnection
import java.net.URI
import java.net.URL
import java.net.URLEncoder
import java.nio.charset.StandardCharsets
import java.time.Instant
import java.time.OffsetDateTime

class SentinelRepository(
    private val logger: (String, DiagnosticLevel) -> Unit,
) {
    @Volatile
    var baseUrl: String = ""

    private val cookieManager = CookieManager(null, CookiePolicy.ACCEPT_ALL)
    private val imageCache = object : LruCache<String, Bitmap>(
        minOf(32 * 1024, (Runtime.getRuntime().maxMemory() / 1024 / 8).toInt()),
    ) {
        override fun sizeOf(key: String, value: Bitmap): Int =
            (value.byteCount / 1024).coerceAtLeast(1)
    }

    suspend fun authStatus(): AuthStatus = withContext(Dispatchers.IO) {
        val json = JSONObject(request("/api/auth/status"))
        AuthStatus(
            enabled = json.optBoolean("enabled"),
            configured = json.optBoolean("configured"),
            authenticated = json.optBoolean("authenticated"),
            username = json.nullableString("username"),
        )
    }

    suspend fun events(): List<SecurityEvent> = withContext(Dispatchers.IO) {
        val array = JSONArray(request("/api/events?limit=100"))
        buildList {
            for (index in 0 until array.length()) {
                val json = array.getJSONObject(index)
                add(
                    SecurityEvent(
                        id = json.getString("id"),
                        camera = json.optString("camera", "Unknown camera"),
                        occurredAt = parseInstant(json.getString("occurredAt")),
                        status = json.optString("status"),
                        eventType = json.optString("eventType"),
                        summary = json.optString("summary"),
                        notified = json.optBoolean("notified"),
                        notificationReason = json.optString("notificationReason"),
                        confidence = json.optDouble("confidence", 0.0),
                        hasImage = json.optBoolean("hasImage"),
                    ),
                )
            }
        }
    }

    suspend fun triggers(): List<TriggerEvent> = withContext(Dispatchers.IO) {
        val array = JSONArray(request("/api/triggers?limit=100"))
        buildList {
            for (index in 0 until array.length()) {
                val json = array.getJSONObject(index)
                add(
                    TriggerEvent(
                        id = json.getString("id"),
                        cameraId = json.optString("cameraId"),
                        cameraName = json.optString("cameraName", "Unknown camera"),
                        occurredAt = parseInstant(json.getString("occurredAt")),
                        triggerAreaFraction = json.optDouble("triggerAreaFraction", 0.0),
                        hasClip = json.optBoolean("hasClip"),
                    ),
                )
            }
        }
    }

    suspend fun cameras(): List<Camera> = withContext(Dispatchers.IO) {
        val array = JSONArray(request("/api/cameras"))
        buildList {
            for (index in 0 until array.length()) {
                val json = array.getJSONObject(index)
                add(
                    Camera(
                        id = json.getString("id"),
                        name = json.optString("name", "Camera"),
                        streamUrl = json.nullableString("streamUrl"),
                        includeAudio = json.optBoolean("includeAudio"),
                    ),
                )
            }
        }
    }

    suspend fun login(username: String, password: String) = withContext(Dispatchers.IO) {
        val body = JSONObject()
            .put("username", username)
            .put("password", password)
            .toString()
        request("/api/auth/login", method = "POST", body = body)
    }

    suspend fun logout() = withContext(Dispatchers.IO) {
        request("/api/auth/logout", method = "POST")
    }

    fun absoluteUrl(path: String): String? {
        val root = normalizedBaseUrl() ?: return null
        return "$root/${path.trimStart('/')}"
    }

    fun mediaHeaders(): Map<String, String> {
        val uri = runCatching { URI(normalizedBaseUrl() ?: return emptyMap()) }.getOrNull()
            ?: return emptyMap()
        val cookie = cookieManager.get(uri, emptyMap())["Cookie"]?.joinToString("; ")
        return if (cookie.isNullOrBlank()) emptyMap() else mapOf("Cookie" to cookie)
    }

    suspend fun loadImage(path: String): Bitmap? = withContext(Dispatchers.IO) {
        val urlString = absoluteUrl(path) ?: return@withContext null
        imageCache.get(urlString)?.let { return@withContext it }

        val connection = openConnection(URL(urlString))
        try {
            connection.requestMethod = "GET"
            connection.connect()
            storeCookies(connection)
            if (connection.responseCode !in 200..299) return@withContext null
            val bitmap = connection.inputStream.use(BitmapFactory::decodeStream)
            if (bitmap != null) imageCache.put(urlString, bitmap)
            bitmap
        } catch (error: Exception) {
            logger("Image request failed: ${error.message ?: error.javaClass.simpleName}", DiagnosticLevel.NOTICE)
            null
        } finally {
            connection.disconnect()
        }
    }

    fun resetSession() {
        imageCache.evictAll()
        logger("Created a fresh network session", DiagnosticLevel.INFO)
    }

    fun clearAuthenticationSession() {
        cookieManager.cookieStore.removeAll()
        imageCache.evictAll()
    }

    private fun request(path: String, method: String = "GET", body: String? = null): String {
        val url = URL(absoluteUrl(path) ?: throw IllegalArgumentException(
            "Enter a valid server address, such as http://192.168.1.50:3320.",
        ))
        val started = System.nanoTime()
        logger("$method $url", DiagnosticLevel.INFO)
        val connection = openConnection(url)

        try {
            connection.requestMethod = method
            connection.setRequestProperty("Accept", "application/json")
            if (body != null) {
                connection.doOutput = true
                connection.setRequestProperty("Content-Type", "application/json")
                connection.outputStream.bufferedWriter(StandardCharsets.UTF_8).use { it.write(body) }
            }

            val status = connection.responseCode
            storeCookies(connection)
            logger("$method $status in ${elapsedMilliseconds(started)} ms", DiagnosticLevel.INFO)
            val response = (if (status in 200..299) connection.inputStream else connection.errorStream)
                ?.bufferedReader(StandardCharsets.UTF_8)
                ?.use { it.readText() }
                .orEmpty()

            if (status !in 200..299) {
                val message = parseServerError(response)
                    ?: if (status == 401) "This server requires you to sign in."
                    else "Server request failed (HTTP $status)."
                throw SentinelApiException(status, message)
            }
            return response
        } catch (error: SentinelApiException) {
            throw error
        } catch (error: Exception) {
            logger(
                "$method failed after ${elapsedMilliseconds(started)} ms: " +
                    (error.message ?: error.javaClass.simpleName),
                DiagnosticLevel.ERROR,
            )
            throw IOException(error.message ?: "Could not connect to Sentinel Core.", error)
        } finally {
            connection.disconnect()
        }
    }

    private fun openConnection(url: URL): HttpURLConnection {
        val connection = url.openConnection() as HttpURLConnection
        connection.connectTimeout = 15_000
        connection.readTimeout = 30_000
        connection.instanceFollowRedirects = true
        val cookies = cookieManager.get(url.toURI(), emptyMap())["Cookie"]?.joinToString("; ")
        if (!cookies.isNullOrBlank()) connection.setRequestProperty("Cookie", cookies)
        return connection
    }

    private fun storeCookies(connection: HttpURLConnection) {
        runCatching {
            cookieManager.put(connection.url.toURI(), connection.headerFields)
        }
    }

    private fun normalizedBaseUrl(): String? {
        val trimmed = baseUrl.trim().trimEnd('/')
        if (trimmed.isBlank()) return null
        val uri = runCatching { URI(trimmed) }.getOrNull() ?: return null
        if (uri.scheme !in setOf("http", "https") || uri.host.isNullOrBlank()) return null
        return trimmed
    }

    private fun parseServerError(body: String): String? {
        if (body.isBlank()) return null
        return runCatching {
            val json = JSONObject(body)
            json.nullableString("error")
                ?: json.optJSONArray("errors")
                    ?.let { array ->
                        buildList {
                            for (index in 0 until array.length()) add(array.optString(index))
                        }.filter(String::isNotBlank).joinToString("\n").ifBlank { null }
                    }
        }.getOrNull()
    }

    private fun parseInstant(value: String): Instant =
        runCatching { Instant.parse(value) }
            .recoverCatching { OffsetDateTime.parse(value).toInstant() }
            .getOrElse { throw IOException("Invalid server timestamp: $value") }

    private fun elapsedMilliseconds(started: Long): Long =
        (System.nanoTime() - started) / 1_000_000

    private fun JSONObject.nullableString(key: String): String? =
        if (isNull(key)) null else optString(key).takeIf { it.isNotBlank() }

    companion object {
        fun normalizeAddress(value: String): String {
            val trimmed = value.trim()
            if (trimmed.isBlank()) return ""
            val withScheme = if ("://" in trimmed) trimmed else "http://$trimmed"
            return withScheme.trimEnd('/')
        }

        fun queryValue(value: String): String =
            URLEncoder.encode(value, StandardCharsets.UTF_8.name())
    }
}
