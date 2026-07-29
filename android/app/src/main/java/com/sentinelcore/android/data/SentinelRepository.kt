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
            role = json.nullableString("role"),
        )
    }

    suspend fun events(): List<SecurityEvent> = withContext(Dispatchers.IO) {
        val array = JSONArray(request("/api/events?limit=100"))
        buildList {
            for (index in 0 until array.length()) {
                add(parseEvent(array.getJSONObject(index)))
            }
        }
    }

    suspend fun event(id: String): SecurityEvent = withContext(Dispatchers.IO) {
        parseEvent(JSONObject(request("/api/events/${URLEncoder.encode(id, StandardCharsets.UTF_8.name())}")))
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
            for (index in 0 until array.length()) add(parseCamera(array.getJSONObject(index)))
        }
    }

    suspend fun createCamera(input: CameraInput): Camera = withContext(Dispatchers.IO) {
        parseCamera(JSONObject(request("/api/cameras", method = "POST", body = input.toJson().toString())))
    }

    suspend fun updateCamera(id: String, input: CameraInput) = withContext(Dispatchers.IO) {
        request(
            "/api/cameras/${queryValue(id)}",
            method = "PUT",
            body = input.toJson().toString(),
        )
    }

    suspend fun deleteCamera(id: String) = withContext(Dispatchers.IO) {
        request("/api/cameras/${queryValue(id)}", method = "DELETE")
    }

    suspend fun probeCamera(probeRequest: CameraProbeRequest): CameraProbeResult =
        withContext(Dispatchers.IO) {
            val body = JSONObject()
                .put("address", probeRequest.address)
                .put("username", probeRequest.username)
                .put("password", probeRequest.password)
                .toString()
            val json = JSONObject(request("/api/cameras/discovery/probe", method = "POST", body = body))
            CameraProbeResult(
                status = json.optString("status", "NotFound"),
                streamUrl = json.nullableString("streamUrl"),
                suggestedName = json.nullableString("suggestedName"),
                manufacturer = json.nullableString("manufacturer"),
                model = json.nullableString("model"),
                resolution = json.nullableString("resolution"),
                message = json.nullableString("message"),
            )
        }

    suspend fun fetchScanInterfaces(): List<ScanInterface> = withContext(Dispatchers.IO) {
        val array = JSONArray(request("/api/cameras/discovery/interfaces"))
        buildList {
            for (index in 0 until array.length()) {
                val json = array.getJSONObject(index)
                add(
                    ScanInterface(
                        name = json.optString("name"),
                        description = json.optString("description"),
                        address = json.getString("address"),
                        prefixLength = json.optInt("prefixLength"),
                        looksVirtual = json.optBoolean("looksVirtual"),
                    ),
                )
            }
        }
    }

    suspend fun startScan(interfaceAddresses: List<String>): String = withContext(Dispatchers.IO) {
        val addresses = JSONArray()
        interfaceAddresses.forEach(addresses::put)
        val body = JSONObject().put("interfaceAddresses", addresses).toString()
        JSONObject(request("/api/cameras/discovery/scan", method = "POST", body = body))
            .getString("scanId")
    }

    suspend fun fetchScanStatus(scanId: String): ScanJobSnapshot = withContext(Dispatchers.IO) {
        val json = JSONObject(request("/api/cameras/discovery/scan/${queryValue(scanId)}"))
        val resultsArray = json.optJSONArray("results") ?: JSONArray()
        val results = buildList {
            for (index in 0 until resultsArray.length()) {
                val item = resultsArray.getJSONObject(index)
                add(
                    ScanResultCamera(
                        ipAddress = item.getString("ipAddress"),
                        manufacturer = item.nullableString("manufacturer"),
                        model = item.nullableString("model"),
                        suggestedName = item.optString("suggestedName", item.getString("ipAddress")),
                        needsCredentials = item.optBoolean("needsCredentials"),
                        alreadySaved = item.optBoolean("alreadySaved"),
                    ),
                )
            }
        }
        ScanJobSnapshot(status = json.optString("status", "Running"), results = results)
    }

    suspend fun fetchTriggerConfig(cameraId: String): TriggerConfig = withContext(Dispatchers.IO) {
        parseTriggerConfig(JSONObject(request("/api/cameras/${queryValue(cameraId)}/trigger-config")))
    }

    suspend fun saveTriggerConfig(cameraId: String, config: TriggerConfig): TriggerConfig =
        withContext(Dispatchers.IO) {
            val json = JSONObject(
                request(
                    "/api/cameras/${queryValue(cameraId)}/trigger-config",
                    method = "PUT",
                    body = config.toJson().toString(),
                ),
            )
            parseTriggerConfig(json)
        }

    // Live ffmpeg snapshot, never cached - fetched fresh every time the zone editor opens.
    suspend fun fetchReferenceFrame(cameraId: String): ByteArray = withContext(Dispatchers.IO) {
        requestBytes("/api/cameras/${queryValue(cameraId)}/reference-frame")
    }

    suspend fun fetchPromptProfileNames(): List<PromptProfileName> = withContext(Dispatchers.IO) {
        val array = JSONArray(request("/api/prompt-profile-names"))
        buildList {
            for (index in 0 until array.length()) {
                val json = array.getJSONObject(index)
                add(
                    PromptProfileName(
                        key = json.getString("key"),
                        displayName = json.optString("displayName", json.getString("key")),
                    ),
                )
            }
        }
    }

    suspend fun timeline(camera: String, date: String): RecordingTimelineDay =
        withContext(Dispatchers.IO) {
            val path = "/api/timeline/day?camera=" +
                URLEncoder.encode(camera, StandardCharsets.UTF_8.name()) +
                "&date=" + URLEncoder.encode(date, StandardCharsets.UTF_8.name())
            val json = JSONObject(request(path))
            val recordingJson = json.getJSONObject("recording")
            val segmentArray = recordingJson.optJSONArray("segments") ?: JSONArray()
            val segments = buildList {
                for (index in 0 until segmentArray.length()) {
                    val item = segmentArray.getJSONObject(index)
                    add(
                        TimelineRecordingSegment(
                            eventId = item.getString("eventId"),
                            triggerEventId = item.getString("triggerEventId"),
                            start = parseInstant(item.getString("start")),
                            end = parseInstant(item.getString("end")),
                        ),
                    )
                }
            }
            val eventArray = json.optJSONArray("events") ?: JSONArray()
            val timelineEvents = buildList {
                for (index in 0 until eventArray.length()) {
                    val item = eventArray.getJSONObject(index)
                    add(
                        TimelineSecurityEvent(
                            id = item.getString("id"),
                            camera = item.getString("camera"),
                            occurredAt = parseInstant(item.getString("occurredAt")),
                            eventType = item.optString("eventType", "unknown"),
                            category = item.optString("category", "other"),
                            severity = item.optString("severity", "low"),
                            summary = item.optString("summary"),
                            confidence = item.optDouble("confidence"),
                            notified = item.optBoolean("notified"),
                            reviewed = item.optBoolean("reviewed"),
                            hasImage = item.optBoolean("hasImage"),
                            hasClip = item.optBoolean("hasClip"),
                        ),
                    )
                }
            }
            RecordingTimelineDay(
                camera = json.getString("camera"),
                date = json.getString("date"),
                start = parseInstant(json.getString("start")),
                end = parseInstant(json.getString("end")),
                recording = TimelineRecording(
                    configured = recordingJson.optBoolean("configured"),
                    available = recordingJson.optBoolean("available"),
                    sourceCamera = recordingJson.optString("sourceCamera"),
                    error = recordingJson.nullableString("error"),
                    mode = recordingJson.optString("mode", "triggerClips"),
                    preSeconds = recordingJson.optInt("preSeconds", 5),
                    postSeconds = recordingJson.optInt("postSeconds", 10),
                    segments = segments,
                ),
                events = timelineEvents,
            )
        }

    suspend fun timelineCameras(): List<String> = withContext(Dispatchers.IO) {
        val array = JSONArray(request("/api/timeline/cameras"))
        buildList {
            for (index in 0 until array.length()) add(array.getString(index))
        }
    }

    suspend fun setReviewed(eventId: String, reviewed: Boolean) = withContext(Dispatchers.IO) {
        val body = JSONObject().put("reviewed", reviewed).toString()
        request(
            "/api/events/${URLEncoder.encode(eventId, StandardCharsets.UTF_8.name())}/reviewed",
            method = "PUT",
            body = body,
        )
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

    suspend fun pushStatus(): PushStatus = withContext(Dispatchers.IO) {
        val json = JSONObject(request("/api/push/status"))
        PushStatus(
            enabled = json.optBoolean("enabled"),
            appleConfigured = json.optBoolean("appleConfigured"),
            firebaseConfigured = json.optBoolean("firebaseConfigured"),
            registeredDevices = json.optInt("registeredDevices"),
        )
    }

    suspend fun registerPushDevice(token: String, deviceName: String) = withContext(Dispatchers.IO) {
        val body = JSONObject()
            .put("platform", "android")
            .put("token", token)
            .put("deviceName", deviceName)
            .toString()
        request("/api/push/devices", method = "POST", body = body)
    }

    suspend fun unregisterPushDevice(token: String) = withContext(Dispatchers.IO) {
        val body = JSONObject()
            .put("platform", "android")
            .put("token", token)
            .toString()
        request("/api/push/devices/unregister", method = "POST", body = body)
    }

    suspend fun testPushNotification(): PushDeliveryReport = withContext(Dispatchers.IO) {
        val json = JSONObject(request("/api/push/test", method = "POST"))
        PushDeliveryReport(
            attempted = json.optInt("attempted"),
            delivered = json.optInt("delivered"),
            failed = json.optInt("failed"),
            removed = json.optInt("removed"),
        )
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

    // Used to decide between the local and remote server addresses before a real connect - any
    // HTTP response counts as "reachable" (even a 401 or 500), since the point is just to detect
    // whether the host is reachable at all. Opens its own connection with a short timeout rather
    // than going through openConnection()/the shared baseUrl, so the real-request 15s/30s
    // timeouts used elsewhere stay untouched.
    suspend fun probeReachable(urlString: String, timeoutMs: Int = 2500): Boolean =
        withContext(Dispatchers.IO) {
            val trimmed = urlString.trim().trimEnd('/')
            if (trimmed.isBlank()) return@withContext false
            val url = runCatching { URL("$trimmed/api/auth/status") }.getOrNull() ?: return@withContext false
            var connection: HttpURLConnection? = null
            try {
                connection = url.openConnection() as HttpURLConnection
                connection.connectTimeout = timeoutMs
                connection.readTimeout = timeoutMs
                connection.responseCode
                true
            } catch (error: IOException) {
                false
            } finally {
                connection?.disconnect()
            }
        }

    fun clearAuthenticationSession() {
        cookieManager.cookieStore.removeAll()
        imageCache.evictAll()
    }

    private fun request(path: String, method: String = "GET", body: String? = null): String {
        val connection = openValidated(path, method, body)
        return try {
            connection.inputStream?.bufferedReader(StandardCharsets.UTF_8)?.use { it.readText() }.orEmpty()
        } finally {
            connection.disconnect()
        }
    }

    // Raw-bytes counterpart to request() - shares the same connect/validate/error-mapping logic,
    // just skips the text decode step. Used for the trigger-zone editor's reference-frame JPEG.
    private fun requestBytes(path: String): ByteArray {
        val connection = openValidated(path)
        return try {
            connection.inputStream.use { it.readBytes() }
        } finally {
            connection.disconnect()
        }
    }

    private fun openValidated(path: String, method: String = "GET", body: String? = null): HttpURLConnection {
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
            if (status !in 200..299) {
                val errorBody = connection.errorStream
                    ?.bufferedReader(StandardCharsets.UTF_8)
                    ?.use { it.readText() }
                    .orEmpty()
                val message = parseServerError(errorBody)
                    ?: if (status == 401) "This server requires you to sign in."
                    else "Server request failed (HTTP $status)."
                connection.disconnect()
                throw SentinelApiException(status, message)
            }
            return connection
        } catch (error: SentinelApiException) {
            throw error
        } catch (error: Exception) {
            connection.disconnect()
            logger(
                "$method failed after ${elapsedMilliseconds(started)} ms: " +
                    (error.message ?: error.javaClass.simpleName),
                DiagnosticLevel.ERROR,
            )
            throw IOException(error.message ?: "Could not connect to Sentinel Core.", error)
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

    private fun parseEvent(json: JSONObject): SecurityEvent =
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
        )

    private fun parseCamera(json: JSONObject): Camera =
        Camera(
            id = json.getString("id"),
            name = json.optString("name", "Camera"),
            streamUrl = json.nullableString("streamUrl"),
            includeAudio = json.optBoolean("includeAudio"),
        )

    private fun parseTriggerConfig(json: JSONObject): TriggerConfig {
        val polygonArray = json.optJSONArray("zonePolygon") ?: JSONArray()
        val polygon = buildList {
            for (index in 0 until polygonArray.length()) {
                val point = polygonArray.getJSONObject(index)
                add(Point2D(x = point.getDouble("x"), y = point.getDouble("y")))
            }
        }
        return TriggerConfig(
            enabled = json.optBoolean("enabled"),
            zonePolygon = polygon,
            minTriggerArea = json.optDouble("minTriggerArea", 0.02),
            cooldownSeconds = json.optInt("cooldownSeconds", 5),
            maxTriggersPerMinute = json.optInt("maxTriggersPerMinute", 6),
            shadowSensitivity = json.optInt("shadowSensitivity", 50),
            recordClips = json.optBoolean("recordClips"),
            clipPreSeconds = json.optInt("clipPreSeconds", 5),
            clipPostSeconds = json.optInt("clipPostSeconds", 10),
            sendToAlerts = json.optBoolean("sendToAlerts"),
            alertProfile = json.optString("alertProfile", "default"),
        )
    }

    private fun CameraInput.toJson(): JSONObject =
        JSONObject()
            .put("name", name)
            .put("streamUrl", streamUrl)
            .put("username", username)
            .put("password", password)
            .put("includeAudio", includeAudio)

    private fun TriggerConfig.toJson(): JSONObject {
        val polygon = JSONArray()
        zonePolygon.forEach { point ->
            polygon.put(JSONObject().put("x", point.x).put("y", point.y))
        }
        return JSONObject()
            .put("enabled", enabled)
            .put("zonePolygon", polygon)
            .put("minTriggerArea", minTriggerArea)
            .put("cooldownSeconds", cooldownSeconds)
            .put("maxTriggersPerMinute", maxTriggersPerMinute)
            .put("shadowSensitivity", shadowSensitivity)
            .put("recordClips", recordClips)
            .put("clipPreSeconds", clipPreSeconds)
            .put("clipPostSeconds", clipPostSeconds)
            .put("sendToAlerts", sendToAlerts)
            .put("alertProfile", alertProfile)
    }

    private fun elapsedMilliseconds(started: Long): Long =
        (System.nanoTime() - started) / 1_000_000

    private fun JSONObject.nullableString(key: String): String? =
        if (isNull(key)) null else optString(key).takeIf { it.isNotBlank() }

    companion object {
        // Local addresses default to http:// (a bare LAN IP shouldn't force a scheme nobody
        // configured). The remote address defaults to https:// instead: it's meant to reach a
        // public internet-facing hostname (e.g. a Cloudflare Tunnel), and silently falling back
        // to plain HTTP there would send credentials/cookies over the internet in the clear.
        fun normalizeAddress(value: String, defaultScheme: String = "http"): String {
            val trimmed = value.trim()
            if (trimmed.isBlank()) return ""
            val withScheme = if ("://" in trimmed) trimmed else "$defaultScheme://$trimmed"
            return withScheme.trimEnd('/')
        }

        fun queryValue(value: String): String =
            URLEncoder.encode(value, StandardCharsets.UTF_8.name())
    }
}
