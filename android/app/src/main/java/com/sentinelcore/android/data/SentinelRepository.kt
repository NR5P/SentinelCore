package com.sentinelcore.android.data

import android.content.Context
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
import java.net.HttpCookie
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import java.nio.charset.StandardCharsets
import java.security.cert.CertPathValidatorException
import java.security.cert.CertificateException
import java.time.Instant
import java.time.OffsetDateTime
import javax.net.ssl.SSLHandshakeException

// The outcome of a reachability probe. CERTIFICATE_UNTRUSTED is split out from UNREACHABLE because
// it is the one failure with somewhere else to go - the same host's plain-HTTP port.
enum class Reachability {
    REACHABLE,
    CERTIFICATE_UNTRUSTED,
    UNREACHABLE,
}

class SentinelRepository(
    context: Context,
    private val logger: (String, DiagnosticLevel) -> Unit,
) {
    @Volatile
    var baseUrl: String = ""

    private val cookieManager = CookieManager(null, CookiePolicy.ACCEPT_ALL)
    private val sessionCookieStore = SessionCookieStore(context.applicationContext)

    // Set by storeCookies when a response handed over a cookie the jar will never give back.
    // Read by login(), where it is the whole difference between "wrong password" and "right
    // password, unusable session" - two failures that otherwise look identical from the outside.
    @Volatile
    private var droppedSecureCookie = false
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

    // Unauthenticated on the server side, so this is readable at the login screen too - which is
    // where knowing the server is too old is arguably most useful. A server old enough to predate
    // version stamping answers this endpoint without a "build" object at all, which is exactly the
    // null that ServerCompatibility.evaluate reads as "too old".
    suspend fun serverHealth(): ServerBuild? = withContext(Dispatchers.IO) {
        val build = JSONObject(request("/api/health")).optJSONObject("build")
            ?: return@withContext null
        ServerBuild(
            version = build.optString("version"),
            commit = build.optString("commit"),
            isDirty = build.optBoolean("isDirty"),
            display = build.optString("display"),
        )
    }

    suspend fun event(id: String): SecurityEvent = withContext(Dispatchers.IO) {
        parseEvent(JSONObject(request("/api/events/${URLEncoder.encode(id, StandardCharsets.UTF_8.name())}")))
    }

    // The merged Events list, and the only list this app fetches now - it replaced separate calls
    // to /api/events and /api/triggers, which returned two rows for one moment whenever a camera
    // forwarded to AI analysis. Those endpoints still exist server-side for older installed
    // builds of this app; nothing here calls them.
    //
    // includeUnconfirmed is the "show hidden" toggle - motion the AI rejected as nothing, plus
    // anything still waiting on a verdict. Off by default, matching the web dashboard.
    suspend fun activity(includeUnconfirmed: Boolean = false): List<ActivityItem> = withContext(Dispatchers.IO) {
        // This list is the day-level audit trail now that it carries motion as well as analyses,
        // so a busy camera's whole day has to fit.
        val path = "/api/activity?limit=500" + if (includeUnconfirmed) "&includeUnconfirmed=true" else ""
        val array = JSONArray(request(path))
        buildList {
            for (index in 0 until array.length()) {
                add(parseActivityItem(array.getJSONObject(index)))
            }
        }
    }

    suspend fun cameras(): List<Camera> = withContext(Dispatchers.IO) {
        val array = JSONArray(request("/api/cameras"))
        buildList {
            for (index in 0 until array.length()) add(parseCamera(array.getJSONObject(index)))
        }
    }

    // The Live grid's tile order for the signed-in account. The server answers with a complete
    // order covering every camera that currently exists, so this is a straight read - see
    // LiveCameraOrder for why the merge rule is nevertheless mirrored on this side.
    suspend fun liveOrder(): List<String> = withContext(Dispatchers.IO) {
        val array = JSONObject(request("/api/live-order")).optJSONArray("cameraIds") ?: JSONArray()
        buildList {
            for (index in 0 until array.length()) add(array.getString(index))
        }
    }

    suspend fun saveLiveOrder(cameraIds: List<String>) = withContext(Dispatchers.IO) {
        request(
            "/api/live-order",
            method = "PUT",
            body = JSONObject().put("cameraIds", JSONArray(cameraIds)).toString(),
        )
    }

    suspend fun createCamera(input: CameraInput): Camera = withContext(Dispatchers.IO) {
        parseCamera(JSONObject(request("/api/cameras", method = "POST", body = input.toJson().toString())))
    }

    // ---------------- Device Hub ----------------

    // Whether the DEVICE HUB destination should exist at all. Its own endpoint rather than read
    // from /api/settings, which is Admin-only - a Viewer gets the screen too, they just can't
    // add or edit devices there.
    suspend fun deviceHubEnabled(): Boolean = withContext(Dispatchers.IO) {
        JSONObject(request("/api/device-hub/enabled")).optBoolean("enabled")
    }

    // Whether the server has a valid licence file, which is what decides the UNLICENSED watermark
    // over camera images and live video. Readable by any signed-in account, not just an Admin,
    // precisely so this app can ask - see the note on the endpoint in LicenseEndpoints.cs.
    suspend fun licenseValid(): Boolean = withContext(Dispatchers.IO) {
        JSONObject(request("/api/license/status")).optBoolean("valid")
    }

    // Status plus the licence holder's name, for the License screen - licenseValid() above stays
    // as it is because the watermark only ever needed the boolean.
    suspend fun licenseStatus(): Pair<Boolean, String?> = withContext(Dispatchers.IO) {
        val json = JSONObject(request("/api/license/status"))
        json.optBoolean("valid") to json.optString("customerName").ifEmpty { null }
    }

    // The raw contents of a .lic file as text/plain, which is what the install endpoint takes - it
    // wants the file's bytes, not a path, since the server can't reach whatever device the licence
    // was emailed to. Verified server-side before anything is written.
    suspend fun installLicense(contents: String): Pair<Boolean, String?> = withContext(Dispatchers.IO) {
        val json = JSONObject(
            request("/api/license/install", method = "POST", body = contents, contentType = "text/plain"),
        )
        json.optBoolean("valid") to json.optString("customerName").ifEmpty { null }
    }

    // Prompt profiles come and go as raw JSON rather than a Kotlin data class, and that is load
    // bearing rather than laziness. The save endpoint replaces the whole PromptProfiles
    // dictionary, so anything this app failed to model would be erased by the act of saving - a
    // profile's crop region is a rectangle no phone form should be editing, but it must survive
    // being edited here. The screen keeps the original object and overwrites only the fields it
    // shows, so the rest round-trips untouched.
    suspend fun promptProfiles(): JSONObject = withContext(Dispatchers.IO) {
        JSONObject(request("/api/settings")).optJSONObject("promptProfiles") ?: JSONObject()
    }

    suspend fun savePromptProfiles(profiles: JSONObject): Unit = withContext(Dispatchers.IO) {
        request("/api/settings/prompt-profiles", method = "PUT", body = profiles.toString())
        Unit
    }

    // The backup is deliberately kept as raw text: it is whatever the server considers its whole
    // configuration, and parsing it into a fixed shape here would quietly drop any section added
    // after this app was built - the one thing a backup must never do. It goes back exactly as it
    // came.
    suspend fun exportBackup(): String = withContext(Dispatchers.IO) {
        request("/api/backup/export")
    }

    // The counts of what was actually restored, by noun. Absent sections are left out rather than
    // reported as zero - a backup taken before camera groups existed hasn't lost any.
    suspend fun importBackup(contents: String): List<Pair<Int, String>> = withContext(Dispatchers.IO) {
        val json = JSONObject(request("/api/backup/import", method = "POST", body = contents))
        listOf(
            "camerasRestored" to "camera",
            "cameraGroupsRestored" to "camera group",
            "triggerZonesRestored" to "trigger zone",
            "gateChecksRestored" to "gate check",
            "accountsRestored" to "account",
        ).mapNotNull { (key, noun) ->
            val count = json.optInt(key)
            if (count > 0) count to noun else null
        }
    }

    suspend fun homeKitStatus(): HomeKitStatus = withContext(Dispatchers.IO) {
        val json = JSONObject(request("/api/homekit/status"))
        val accessories = json.optJSONArray("accessories") ?: JSONArray()
        HomeKitStatus(
            running = json.optBoolean("running"),
            enabled = json.optBoolean("enabled"),
            published = json.optBoolean("published"),
            paired = json.optBoolean("paired"),
            accessories = buildList {
                for (index in 0 until accessories.length()) add(accessories.optString(index))
            },
            pairingCode = json.optString("pairingCode").ifEmpty { null },
            error = json.optString("error").ifEmpty { null },
        )
    }

    suspend fun homeKitSettings(): HomeKitSettings = withContext(Dispatchers.IO) {
        val section = JSONObject(request("/api/settings")).optJSONObject("homeKit") ?: JSONObject()
        HomeKitSettings(
            enabled = section.optBoolean("enabled"),
            sidecarBaseUrl = section.optString("sidecarBaseUrl").ifEmpty { "http://127.0.0.1:3321" },
            timeoutSeconds = if (section.has("timeoutSeconds")) section.optInt("timeoutSeconds") else 10,
            minimumConfidence = if (section.has("minimumConfidence")) section.optDouble("minimumConfidence") else 0.6,
        )
    }

    suspend fun saveHomeKitSettings(settings: HomeKitSettings): Unit = withContext(Dispatchers.IO) {
        val body = JSONObject()
            .put("enabled", settings.enabled)
            .put("sidecarBaseUrl", settings.sidecarBaseUrl)
            .put("timeoutSeconds", settings.timeoutSeconds)
            .put("minimumConfidence", settings.minimumConfidence)
        request("/api/settings/homekit", method = "PUT", body = body.toString())
        Unit
    }

    // The literal "RESET" is what the endpoint requires - an explicit confirmation rather than a
    // bare POST, since this drops the pairing and every Home app has to pair again.
    suspend fun resetHomeKitPairing(): Unit = withContext(Dispatchers.IO) {
        request("/api/homekit/reset", method = "POST", body = JSONObject().put("confirm", "RESET").toString())
        Unit
    }

    suspend fun knownVehicles(): List<KnownVehicle> = withContext(Dispatchers.IO) {
        val array = JSONObject(request("/api/settings")).optJSONArray("knownVehicles") ?: JSONArray()
        buildList {
            for (index in 0 until array.length()) {
                val vehicle = array.getJSONObject(index)
                add(KnownVehicle(vehicle.optString("plate"), vehicle.optString("label")))
            }
        }
    }

    // The whole list every time, which is what the endpoint takes - it replaces the section rather
    // than patching it, so a partial send would delete the rest.
    suspend fun saveKnownVehicles(vehicles: List<KnownVehicle>): Unit = withContext(Dispatchers.IO) {
        val array = JSONArray()
        for (vehicle in vehicles) {
            array.put(JSONObject().put("plate", vehicle.plate).put("label", vehicle.label))
        }
        request("/api/settings/known-vehicles", method = "PUT", body = array.toString())
        Unit
    }

    // Turning the dashboard's login requirement on creates the first admin account in the same
    // step - there is deliberately no way to demand a login nobody can satisfy.
    suspend fun enableDashboardLogin(username: String, password: String): Unit = withContext(Dispatchers.IO) {
        request(
            "/api/auth/enable",
            method = "POST",
            body = JSONObject().put("username", username).put("password", password).toString(),
        )
        Unit
    }

    suspend fun disableDashboardLogin(): Unit = withContext(Dispatchers.IO) {
        request("/api/auth/disable", method = "POST")
        Unit
    }

    suspend fun devices(): List<HubDevice> = withContext(Dispatchers.IO) {
        val array = JSONArray(request("/api/devices"))
        buildList {
            for (index in 0 until array.length()) {
                add(parseDevice(array.getJSONObject(index)))
            }
        }
    }

    // The light poll while the Device Hub screen is open - no topics or payload strings, just
    // what each device is currently doing.
    suspend fun deviceStates(): HubDeviceStateSnapshot = withContext(Dispatchers.IO) {
        val json = JSONObject(request("/api/devices/state"))
        val array = json.optJSONArray("devices") ?: JSONArray()
        HubDeviceStateSnapshot(
            connected = json.optBoolean("connected"),
            devices = buildList {
                for (index in 0 until array.length()) {
                    val item = array.getJSONObject(index)
                    add(
                        HubDeviceState(
                            id = item.getString("id"),
                            power = item.optString("power", "unknown"),
                            brightness = if (item.isNull("brightness")) null else item.optInt("brightness"),
                            available = item.optBoolean("available", true),
                            measurements = parseMeasurements(item),
                        ),
                    )
                }
            },
        )
    }

    suspend fun sendDeviceCommand(id: String, action: String? = null, brightness: Int? = null): DeviceCommandResult =
        withContext(Dispatchers.IO) {
            val body = JSONObject().apply {
                if (action != null) put("action", action)
                if (brightness != null) put("brightness", brightness)
            }
            val json = JSONObject(
                request("/api/devices/${queryValue(id)}/command", method = "POST", body = body.toString()),
            )
            DeviceCommandResult(
                sent = json.optBoolean("sent"),
                acknowledged = json.optBoolean("acknowledged"),
                power = json.optString("power", "unknown"),
                brightness = if (json.isNull("brightness")) null else json.optInt("brightness"),
            )
        }

    // --- Schedules ---------------------------------------------------------------------------

    // Schedules live in the main app and are proxied by the web host, exactly like automation
    // rules - see ScheduleProxyEndpoints.cs. Reads need only a signed-in account; everything that
    // changes or fires one is AdminOnly server-side.
    suspend fun schedules(): List<Schedule> = withContext(Dispatchers.IO) {
        val array = JSONArray(request("/api/schedules"))
        buildList {
            for (index in 0 until array.length()) add(parseSchedule(array.getJSONObject(index)))
        }
    }

    suspend fun scheduleMetadata(): ScheduleMetadata = withContext(Dispatchers.IO) {
        val json = JSONObject(request("/api/schedules/metadata"))

        val levels = json.optJSONArray("darknessLevels") ?: JSONArray()
        val devices = json.optJSONArray("devices") ?: JSONArray()

        ScheduleMetadata(
            darknessLevels = buildList {
                for (index in 0 until levels.length()) {
                    val item = levels.getJSONObject(index)
                    add(
                        DarknessLevel(
                            key = item.optString("key"),
                            label = item.optString("label"),
                            description = item.optString("description"),
                            elevationDegrees = item.optDouble("elevationDegrees", -6.0),
                        ),
                    )
                }
            },
            deviceActions = json.optJSONArray("deviceActions").toStringList(),
            tokens = json.optJSONArray("tokens").toStringList(),
            minimumIntervalSeconds = json.optInt("minimumIntervalSeconds", 10),
            locationSet = json.optBoolean("locationSet"),
            timeZone = json.optString("timeZone", ""),
            devices = buildList {
                for (index in 0 until devices.length()) {
                    val item = devices.getJSONObject(index)
                    add(
                        ScheduleDeviceOption(
                            id = item.getString("id"),
                            name = item.optString("name", "Device"),
                            kind = item.optString("kind", "light"),
                            room = item.optString("room", ""),
                            enabled = item.optBoolean("enabled", true),
                            supportsBrightness = item.optBoolean("supportsBrightness"),
                            momentary = item.optBoolean("momentary"),
                            coverLike = item.optBoolean("coverLike"),
                            hasStop = item.optBoolean("hasStop"),
                        ),
                    )
                }
            },
        )
    }

    suspend fun scheduleRuns(id: String, limit: Int = 25): List<ScheduleRun> = withContext(Dispatchers.IO) {
        val array = JSONArray(request("/api/schedules/${queryValue(id)}/runs?limit=$limit"))
        buildList {
            for (index in 0 until array.length()) {
                val item = array.getJSONObject(index)
                add(
                    ScheduleRun(
                        id = item.getString("id"),
                        scheduleId = item.getString("scheduleId"),
                        dueAtUtc = parseInstant(item.getString("dueAtUtc")),
                        firedAtUtc = parseInstant(item.getString("firedAtUtc")),
                        status = item.optString("status"),
                        detail = item.optString("detail"),
                        durationMs = item.optLong("durationMs"),
                    ),
                )
            }
        }
    }

    // Upcoming firing times for a schedule that hasn't been saved yet - the only practical way to
    // check that a darkness level or a cron expression says what was meant.
    suspend fun previewSchedule(input: ScheduleInput): SchedulePreview = withContext(Dispatchers.IO) {
        val json = JSONObject(
            request("/api/schedules/preview", method = "POST", body = input.toJson().toString()),
        )
        val occurrences = json.optJSONArray("occurrences") ?: JSONArray()
        SchedulePreview(
            occurrences = buildList {
                for (index in 0 until occurrences.length()) {
                    add(occurrences.getJSONObject(index).optString("local"))
                }
            },
            timeZone = json.optString("timeZone", ""),
            explanation = json.optString("explanation", ""),
        )
    }

    suspend fun createSchedule(input: ScheduleInput): Schedule = withContext(Dispatchers.IO) {
        parseSchedule(JSONObject(request("/api/schedules", method = "POST", body = input.toJson().toString())))
    }

    suspend fun updateSchedule(id: String, input: ScheduleInput): Schedule = withContext(Dispatchers.IO) {
        parseSchedule(
            JSONObject(
                request("/api/schedules/${queryValue(id)}", method = "PUT", body = input.toJson().toString()),
            ),
        )
    }

    suspend fun deleteSchedule(id: String) = withContext(Dispatchers.IO) {
        request("/api/schedules/${queryValue(id)}", method = "DELETE")
        Unit
    }

    suspend fun setScheduleEnabled(id: String, enabled: Boolean): Schedule = withContext(Dispatchers.IO) {
        parseSchedule(
            JSONObject(
                request(
                    "/api/schedules/${queryValue(id)}/enabled",
                    method = "POST",
                    body = JSONObject().put("enabled", enabled).toString(),
                ),
            ),
        )
    }

    // Fires the action now, ignoring the timing. This really opens the gate or switches the
    // light, which is why the server puts it behind AdminOnly.
    suspend fun runSchedule(id: String): ScheduleRunResult = withContext(Dispatchers.IO) {
        val json = JSONObject(request("/api/schedules/${queryValue(id)}/run", method = "POST"))
        ScheduleRunResult(
            fired = json.optBoolean("fired"),
            detail = if (json.optBoolean("fired")) json.optString("detail") else json.optString("error"),
        )
    }

    suspend fun createDevice(input: HubDeviceInput): HubDevice = withContext(Dispatchers.IO) {
        parseDevice(JSONObject(request("/api/devices", method = "POST", body = input.toJson().toString())))
    }

    suspend fun updateDevice(id: String, input: HubDeviceInput): HubDevice = withContext(Dispatchers.IO) {
        parseDevice(
            JSONObject(
                request("/api/devices/${queryValue(id)}", method = "PUT", body = input.toJson().toString()),
            ),
        )
    }

    // The whole arrangement in one PUT, mirroring saveLiveOrder for camera tiles.
    suspend fun saveDeviceOrder(deviceIds: List<String>) = withContext(Dispatchers.IO) {
        request(
            "/api/devices/order",
            method = "PUT",
            body = JSONObject().put("deviceIds", JSONArray(deviceIds)).toString(),
        )
        Unit
    }

    suspend fun deleteDevice(id: String) = withContext(Dispatchers.IO) {
        request("/api/devices/${queryValue(id)}", method = "DELETE")
        Unit
    }

    suspend fun startDeviceScan(): DeviceScanStartResult = withContext(Dispatchers.IO) {
        val json = JSONObject(request("/api/devices/discover", method = "POST"))
        DeviceScanStartResult(
            scanId = json.getString("scanId"),
            scanSeconds = json.optInt("scanSeconds", 10),
        )
    }

    suspend fun deviceScanStatus(scanId: String): DeviceScanSnapshot = withContext(Dispatchers.IO) {
        val json = JSONObject(request("/api/devices/discover/${queryValue(scanId)}"))
        val array = json.optJSONArray("results") ?: JSONArray()
        DeviceScanSnapshot(
            status = json.optString("status", "running"),
            results = buildList {
                for (index in 0 until array.length()) {
                    add(parseCandidate(array.getJSONObject(index)))
                }
            },
        )
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

    suspend fun knownFaces(): List<KnownFace> = withContext(Dispatchers.IO) {
        val array = JSONArray(request("/api/known-faces"))
        buildList {
            for (index in 0 until array.length()) add(parseKnownFace(array.getJSONObject(index)))
        }
    }

    suspend fun createKnownFace(label: String, imageBytes: ByteArray, fileName: String = "photo.jpg"): KnownFace =
        withContext(Dispatchers.IO) {
            parseKnownFace(
                JSONObject(
                    requestMultipart(
                        "/api/known-faces",
                        fields = mapOf("label" to label),
                        fileFieldName = "image",
                        fileBytes = imageBytes,
                        fileName = fileName,
                    ),
                ),
            )
        }

    suspend fun addKnownFaceEnrollment(faceId: String, imageBytes: ByteArray, fileName: String = "photo.jpg"): KnownFace =
        withContext(Dispatchers.IO) {
            parseKnownFace(
                JSONObject(
                    requestMultipart(
                        "/api/known-faces/${queryValue(faceId)}/enrollments",
                        fields = emptyMap(),
                        fileFieldName = "image",
                        fileBytes = imageBytes,
                        fileName = fileName,
                    ),
                ),
            )
        }

    suspend fun deleteKnownFaceEnrollment(faceId: String, enrollmentId: String) = withContext(Dispatchers.IO) {
        request("/api/known-faces/${queryValue(faceId)}/enrollments/${queryValue(enrollmentId)}", method = "DELETE")
    }

    suspend fun deleteKnownFace(id: String) = withContext(Dispatchers.IO) {
        request("/api/known-faces/${queryValue(id)}", method = "DELETE")
    }

    suspend fun accounts(): List<Account> = withContext(Dispatchers.IO) {
        val array = JSONArray(request("/api/accounts"))
        buildList {
            for (index in 0 until array.length()) add(parseAccount(array.getJSONObject(index)))
        }
    }

    suspend fun createAccount(
        username: String,
        password: String,
        role: String,
        displayName: String?,
    ): Account = withContext(Dispatchers.IO) {
        val body = JSONObject()
            .put("username", username)
            .put("password", password)
            .put("role", role)
            .put("displayName", displayName)
        parseAccount(JSONObject(request("/api/accounts", method = "POST", body = body.toString())))
    }

    suspend fun updateAccountRole(id: String, role: String) = withContext(Dispatchers.IO) {
        val body = JSONObject().put("role", role)
        request("/api/accounts/${queryValue(id)}/role", method = "PUT", body = body.toString())
    }

    suspend fun resetAccountPassword(id: String, newPassword: String) = withContext(Dispatchers.IO) {
        val body = JSONObject().put("newPassword", newPassword)
        request("/api/accounts/${queryValue(id)}/reset-password", method = "POST", body = body.toString())
    }

    suspend fun deleteAccount(id: String) = withContext(Dispatchers.IO) {
        request("/api/accounts/${queryValue(id)}", method = "DELETE")
    }

    // The server deliberately leaves the current session alone (doesn't invalidate it) - no
    // separate re-login needed here, the existing cookie just keeps working.
    suspend fun changeMyPassword(currentPassword: String, newPassword: String) = withContext(Dispatchers.IO) {
        val body = JSONObject()
            .put("currentPassword", currentPassword)
            .put("newPassword", newPassword)
        request("/api/accounts/change-password", method = "POST", body = body.toString())
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
                supportsPtz = json.optBoolean("supportsPtz"),
                onvifPtzServiceUrl = json.nullableString("onvifPtzServiceUrl"),
                onvifProfileToken = json.nullableString("onvifProfileToken"),
            )
        }

    suspend fun ptzMove(cameraId: String, pan: Double, tilt: Double, zoom: Double) = withContext(Dispatchers.IO) {
        val body = JSONObject().put("pan", pan).put("tilt", tilt).put("zoom", zoom)
        request("/api/cameras/${queryValue(cameraId)}/ptz/move", method = "POST", body = body.toString())
    }

    suspend fun ptzStop(cameraId: String) = withContext(Dispatchers.IO) {
        request("/api/cameras/${queryValue(cameraId)}/ptz/stop", method = "POST")
    }

    suspend fun ptzHome(cameraId: String) = withContext(Dispatchers.IO) {
        request("/api/cameras/${queryValue(cameraId)}/ptz/home", method = "POST")
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
        droppedSecureCookie = false
        request("/api/auth/login", method = "POST", body = body)
        // HTTP 200 here proves the password was right, not that the session survived. When it
        // didn't, every later request comes back 401 and the app reports the server's own wording
        // for that - "This server requires you to sign in." - directly under a login that just
        // succeeded, which reads as a rejected password and sends people off to reset one that
        // was never wrong. Fail here instead, while the actual cause is still known.
        if (droppedSecureCookie) throw IOException(secureCookieRejectedMessage())
    }

    private fun secureCookieRejectedMessage(): String {
        val secureAddress = normalizedBaseUrl()?.let(ServerAddress::httpsEquivalent)
        return "Signed in, but the server issued a secure session cookie, which cannot be sent " +
            "back over an unencrypted http:// address - so every request after this one would be " +
            "rejected. " + if (secureAddress != null) {
            "Change this server's address to $secureAddress and sign in again."
        } else {
            "Use the https:// form of this server's address and sign in again."
        }
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

    suspend fun systemStatus(): SystemStatus = withContext(Dispatchers.IO) {
        val json = JSONObject(request("/api/system/status"))
        SystemStatus(
            cpuUsagePercent = json.optDouble("cpuUsagePercent"),
            processorCount = json.optInt("processorCount"),
            memoryUsedBytes = json.optLong("memoryUsedBytes"),
            memoryTotalBytes = json.optLong("memoryTotalBytes"),
            diskFreeBytes = json.optLong("diskFreeBytes"),
            diskTotalBytes = json.optLong("diskTotalBytes"),
            queuedAlerts = json.optInt("queuedAlerts"),
            uptimeSeconds = json.optDouble("uptimeSeconds"),
            visionBackendHealthy = json.optBoolean("visionBackendHealthy"),
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

    // ExoPlayer opens clip and HLS URLs on its own HTTP stack, which shares nothing with
    // openConnection - so the session has to be handed to it explicitly or every media request
    // comes back 401 while the rest of the app looks perfectly signed in.
    fun mediaHeaders(): Map<String, String> {
        val url = runCatching { URL(normalizedBaseUrl() ?: return emptyMap()) }.getOrNull()
            ?: return emptyMap()
        val cookie = cookieHeader(url) ?: return emptyMap()
        return mapOf("Cookie" to cookie)
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
    suspend fun probe(urlString: String, timeoutMs: Int = 2500): Reachability =
        withContext(Dispatchers.IO) {
            val trimmed = urlString.trim().trimEnd('/')
            if (trimmed.isBlank()) return@withContext Reachability.UNREACHABLE
            val url = runCatching { URL("$trimmed/api/auth/status") }.getOrNull()
                ?: return@withContext Reachability.UNREACHABLE
            var connection: HttpURLConnection? = null
            try {
                connection = url.openConnection() as HttpURLConnection
                connection.connectTimeout = timeoutMs
                connection.readTimeout = timeoutMs
                connection.responseCode
                Reachability.REACHABLE
            } catch (error: IOException) {
                if (isCertificateTrustFailure(error)) Reachability.CERTIFICATE_UNTRUSTED
                else Reachability.UNREACHABLE
            } finally {
                connection?.disconnect()
            }
        }

    // A certificate this device won't trust is worth telling apart from a host that isn't there:
    // the first has a plain-HTTP port worth retrying, the second doesn't. Sentinel Core signs its
    // certificate with a CA it generates on first run (DEPLOYMENT.md 14a), so a stock Android
    // device rejects the local https:// address until that CA is installed - and to a bare
    // reachability probe that looks exactly like being on someone else's network.
    private fun isCertificateTrustFailure(error: Throwable): Boolean {
        val seen = mutableSetOf<Throwable>()
        var current: Throwable? = error
        while (current != null && seen.add(current)) {
            if (current is CertificateException || current is CertPathValidatorException) return true
            current = current.cause
        }
        return error is SSLHandshakeException
    }

    fun clearAuthenticationSession() {
        cookieManager.cookieStore.removeAll()
        normalizedBaseUrl()?.let { runCatching { sessionCookieStore.clear(URL(it)) } }
        imageCache.evictAll()
    }

    // ---------------- Server settings (schema-driven, Admin-only server-side) ----------------

    suspend fun settingsSchema(): List<SettingsSection> = withContext(Dispatchers.IO) {
        val array = JSONArray(request("/api/settings/schema"))
        buildList {
            for (index in 0 until array.length()) add(parseSettingsSection(array.getJSONObject(index)))
        }
    }

    // The full settings snapshot, kept as raw JSON on purpose: its shape is described by the
    // schema above, and hard-coding it here is exactly what the schema exists to avoid. Secrets
    // arrive masked (••••••••) and are sent back unchanged to mean "keep the stored value".
    suspend fun settingsSnapshot(): JSONObject = withContext(Dispatchers.IO) {
        JSONObject(request("/api/settings"))
    }

    // The values for a section that isn't in the bulk snapshot, fetched from the dataEndpoint the
    // schema gives it. Only INTERNET EXPOSED MODE needs this - it configures the dashboard
    // project's own Kestrel binding and so lives in a different settings file - but nothing here
    // knows that: the section says where its values are and this fetches them.
    suspend fun settingsSectionData(endpoint: String): JSONObject = withContext(Dispatchers.IO) {
        JSONObject(request(endpoint))
    }

    // Returns whether the saved section needs a restart to take effect (the Urls section).
    suspend fun saveSettingsSection(endpoint: String, body: JSONObject): Boolean = withContext(Dispatchers.IO) {
        val json = JSONObject(request(endpoint, method = "PUT", body = body.toString()))
        json.optBoolean("restartRequired")
    }

    // Runs a settings field's lookup (SettingsLookup). The endpoint and what the values mean both
    // come from the schema, so this stays one method however many fields grow one.
    suspend fun settingsLookup(lookup: SettingsLookup, query: String): List<SettingsLookupCandidate> =
        withContext(Dispatchers.IO) {
            val json = JSONObject(request("${lookup.endpoint}?query=${queryValue(query)}"))
            val results = json.optJSONArray("results") ?: JSONArray()
            buildList {
                for (index in 0 until results.length()) {
                    val candidate = results.getJSONObject(index)
                    val values = candidate.optJSONObject("values") ?: JSONObject()
                    add(
                        SettingsLookupCandidate(
                            label = candidate.optString("label"),
                            values = buildMap {
                                for (key in values.keys()) put(key, values.get(key))
                            },
                        ),
                    )
                }
            }
        }

    // Returns whatever the action had to say for itself, or null when it had nothing. The test
    // sends report which address the mail actually went to; the setup reset answers with a body of
    // a different shape entirely, and an unreadable answer to a call that succeeded still means it
    // succeeded - a failure has already thrown by here, carrying the server's own message.
    suspend fun runSettingsAction(action: SettingsAction): String? = withContext(Dispatchers.IO) {
        val body = request(action.endpoint, method = action.method)
        runCatching { JSONObject(body).optString("detail").ifEmpty { null } }.getOrNull()
    }

    // service is "bridge" or "dashboard". The server answers 202 and restarts only when running
    // under a supervisor (systemd etc.); otherwise it refuses with an explanation that surfaces
    // through SentinelApiException like any other server error message.
    suspend fun restartServer(service: String): Unit = withContext(Dispatchers.IO) {
        request(
            "/api/system/restart",
            method = "POST",
            body = JSONObject().put("service", service).toString(),
        )
        Unit
    }

    // Rebooting the machine itself, rather than bouncing a service. The probe is a side-effect-
    // free GET and is the only trustworthy answer to "will the button work" - the permission it
    // reports on lives in the server's operating system and can vanish without this app hearing
    // about it, so it is asked again every time the screen is opened.
    suspend fun hostRebootProbe(): HostRebootProbe = withContext(Dispatchers.IO) {
        parseHostRebootProbe(JSONObject(request("/api/system/host-reboot")))
    }

    suspend fun rebootHost(): HostRebootOutcome = withContext(Dispatchers.IO) {
        parseHostRebootOutcome(JSONObject(request("/api/system/host-reboot", method = "POST", body = "{}")))
    }

    suspend fun cancelHostReboot(): HostRebootOutcome = withContext(Dispatchers.IO) {
        parseHostRebootOutcome(JSONObject(request("/api/system/host-reboot/cancel", method = "POST", body = "{}")))
    }

    private fun parseHostRebootProbe(json: JSONObject): HostRebootProbe = HostRebootProbe(
        platform = json.optString("platform"),
        platformLabel = json.optString("platformLabel"),
        readiness = json.optString("readiness"),
        enabled = json.optBoolean("enabled"),
        runningAs = json.optString("runningAs"),
        summary = json.optString("summary"),
        delaySeconds = json.optInt("delaySeconds", 60),
        // optString would turn an absent value into "", which reads as "there is a setup script,
        // and it is empty" - the UI needs to tell those two apart to know whether to show the
        // command block at all.
        setupScript = if (json.isNull("setupScript")) null else json.optString("setupScript").ifBlank { null },
        setupNote = if (json.isNull("setupNote")) null else json.optString("setupNote").ifBlank { null },
        secondsRemaining = if (json.isNull("secondsRemaining")) null else json.optInt("secondsRemaining"),
    )

    private fun parseHostRebootOutcome(json: JSONObject): HostRebootOutcome = HostRebootOutcome(
        ok = json.optBoolean("ok"),
        message = json.optString("message"),
        secondsRemaining = if (json.isNull("secondsRemaining")) null else json.optInt("secondsRemaining"),
    )

    private fun parseSettingsSection(json: JSONObject): SettingsSection {
        val fieldsArray = json.getJSONArray("fields")
        return SettingsSection(
            key = json.getString("key"),
            title = json.getString("title"),
            endpoint = json.getString("endpoint"),
            description = json.optString("description"),
            fields = buildList {
                for (index in 0 until fieldsArray.length()) {
                    val field = fieldsArray.getJSONObject(index)
                    add(
                        SettingsField(
                            name = field.getString("name"),
                            label = field.getString("label"),
                            type = field.getString("type"),
                            tip = field.optString("tip"),
                            options = field.optJSONArray("options")?.let { options ->
                                buildList {
                                    for (o in 0 until options.length()) {
                                        val option = options.getJSONObject(o)
                                        add(SettingsFieldOption(option.getString("value"), option.getString("label")))
                                    }
                                }
                            } ?: emptyList(),
                            // has() rather than optDouble's own default: 0 is a legal bound on
                            // plenty of these fields, so an absent min must stay null and not
                            // become a floor of zero.
                            min = if (field.has("min") && !field.isNull("min")) field.getDouble("min") else null,
                            max = if (field.has("max") && !field.isNull("max")) field.getDouble("max") else null,
                            integer = if (field.has("integer")) field.getBoolean("integer") else null,
                            lookup = field.optJSONObject("lookup")?.let {
                                SettingsLookup(
                                    label = it.getString("label"),
                                    tip = it.optString("tip"),
                                    endpoint = it.getString("endpoint"),
                                )
                            },
                            requiresRestart = field.optBoolean("requiresRestart"),
                        ),
                    )
                }
            },
            extraAction = json.optJSONObject("extraAction")?.let {
                SettingsAction(
                    label = it.getString("label"),
                    tip = it.optString("tip"),
                    endpoint = it.getString("endpoint"),
                    method = it.optString("method", "POST"),
                )
            },
            group = json.optString("group"),
            // A server that predates grouping sends neither, and the defaults put every section in
            // one unnamed group at the end - the flat list this screen used to be.
            groupOrder = if (json.has("groupOrder")) json.optInt("groupOrder") else Int.MAX_VALUE,
            basic = json.optBoolean("basic"),
            restartService = json.optString("restartService").ifEmpty { "bridge" },
            dataEndpoint = json.optString("dataEndpoint").ifEmpty { null },
        )
    }

    private fun request(
        path: String,
        method: String = "GET",
        body: String? = null,
        contentType: String = "application/json",
    ): String {
        val connection = openValidated(path, method, body, contentType)
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

    // contentType is all but always JSON; the licence install endpoint takes the raw .lic file as
    // text/plain, which is the one caller that passes anything else.
    private fun openValidated(
        path: String,
        method: String = "GET",
        body: String? = null,
        contentType: String = "application/json",
    ): HttpURLConnection {
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
                connection.setRequestProperty("Content-Type", contentType)
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

    // Only used for Known Faces photo enrollment - everything else in this repository sends
    // plain JSON via request()/openValidated(). There's no OkHttp/Retrofit in this app (see
    // openValidated's own comment on why), so this hand-builds a standard multipart/form-data
    // body the same way request()'s JSON body is hand-written, rather than adding a dependency
    // for one endpoint's worth of file uploads.
    private fun requestMultipart(
        path: String,
        fields: Map<String, String>,
        fileFieldName: String,
        fileBytes: ByteArray,
        fileName: String,
    ): String {
        val connection = openValidatedMultipart(path, fields, fileFieldName, fileBytes, fileName)
        return try {
            connection.inputStream?.bufferedReader(StandardCharsets.UTF_8)?.use { it.readText() }.orEmpty()
        } finally {
            connection.disconnect()
        }
    }

    private fun openValidatedMultipart(
        path: String,
        fields: Map<String, String>,
        fileFieldName: String,
        fileBytes: ByteArray,
        fileName: String,
    ): HttpURLConnection {
        val url = URL(absoluteUrl(path) ?: throw IllegalArgumentException(
            "Enter a valid server address, such as http://192.168.1.50:3320.",
        ))
        val started = System.nanoTime()
        val method = "POST"
        logger("$method $url", DiagnosticLevel.INFO)
        val connection = openConnection(url)
        val boundary = "Boundary-${System.currentTimeMillis()}"

        try {
            connection.requestMethod = method
            connection.setRequestProperty("Accept", "application/json")
            connection.setRequestProperty("Content-Type", "multipart/form-data; boundary=$boundary")
            connection.doOutput = true
            connection.outputStream.use { output ->
                for ((name, value) in fields) {
                    output.write("--$boundary\r\n".toByteArray(StandardCharsets.UTF_8))
                    output.write(
                        "Content-Disposition: form-data; name=\"$name\"\r\n\r\n".toByteArray(StandardCharsets.UTF_8),
                    )
                    output.write("$value\r\n".toByteArray(StandardCharsets.UTF_8))
                }
                output.write("--$boundary\r\n".toByteArray(StandardCharsets.UTF_8))
                output.write(
                    "Content-Disposition: form-data; name=\"$fileFieldName\"; filename=\"$fileName\"\r\n"
                        .toByteArray(StandardCharsets.UTF_8),
                )
                output.write("Content-Type: image/jpeg\r\n\r\n".toByteArray(StandardCharsets.UTF_8))
                output.write(fileBytes)
                output.write("\r\n--$boundary--\r\n".toByteArray(StandardCharsets.UTF_8))
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
        cookieHeader(url)?.let { connection.setRequestProperty("Cookie", it) }
        return connection
    }

    // The session for `url`, from the in-memory jar or - after an app restart emptied it - from the
    // encrypted store. Shared with mediaHeaders() so ExoPlayer authenticates exactly like every
    // other request does; they used to resolve cookies differently and only one of them worked.
    private fun cookieHeader(url: URL): String? {
        cookieManager.get(url.toURI(), emptyMap())["Cookie"]
            ?.joinToString("; ")
            ?.takeIf(String::isNotBlank)
            ?.let { return it }

        val stored = sessionCookieStore.load(url)?.takeIf(String::isNotBlank) ?: return null
        // Rehydrate CookieManager too. Otherwise the first successful response after an app
        // restart (which normally has no Set-Cookie header) would look like an empty jar and
        // incorrectly erase the persisted session in storeCookies().
        //
        // Each cookie goes back as its own Set-Cookie pinned to Path=/. Both details matter: a
        // bare "a=1; b=2" parses as one cookie with a junk attribute, and a Set-Cookie carrying no
        // Path at all inherits the *requesting* URL's directory - so restoring during a call to
        // /api/timeline/day used to scope the session to /api/timeline/, leaving it invisible to
        // any lookup from the site root. That is what stopped ExoPlayer from sending it.
        val restored = restoredSetCookieValues(stored)
        if (restored.isNotEmpty()) {
            runCatching { cookieManager.put(url.toURI(), mapOf("Set-Cookie" to restored)) }
        }
        return stored
    }

    private fun storeCookies(connection: HttpURLConnection) {
        runCatching {
            cookieManager.put(connection.url.toURI(), connection.headerFields)
            val cookies = cookieManager.get(connection.url.toURI(), emptyMap())["Cookie"]?.joinToString("; ")
            if (cookies.isNullOrBlank()) {
                // An empty jar right after a response that *did* set a cookie is the tell: the
                // cookie was accepted and will never be handed back. Recorded rather than thrown
                // from here, since only login() knows the difference between "no session yet"
                // (every anonymous request) and "the session we just earned is unusable".
                droppedSecureCookie = carriesUnusableSecureCookie(
                    connection.url.protocol,
                    setCookieValues(connection),
                )
                sessionCookieStore.clear(connection.url)
            } else {
                droppedSecureCookie = false
                sessionCookieStore.save(connection.url, cookies)
            }
        }
    }

    private fun setCookieValues(connection: HttpURLConnection): List<String> =
        connection.headerFields
            .filterKeys { it != null && it.equals("Set-Cookie", ignoreCase = true) }
            .values
            .flatten()

    private fun normalizedBaseUrl(): String? = ServerAddress.normalize(baseUrl)

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

    private fun parseInstant(value: String): Instant = ServerTimestamp.parse(value)

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

    // optDouble returns NaN for a missing key rather than throwing, which is how a null
    // confidence/area on a motion row is told apart from a real 0.
    private fun JSONObject.nullableDouble(name: String): Double? =
        optDouble(name).takeUnless { it.isNaN() }

    private fun parseActivityItem(json: JSONObject): ActivityItem =
        ActivityItem(
            id = json.getString("id"),
            kind = json.optString("kind", "analyzed"),
            camera = json.optString("camera", "Unknown camera"),
            occurredAt = parseInstant(json.getString("occurredAt")),
            imageUrl = json.nullableString("imageUrl"),
            clipUrl = json.nullableString("clipUrl"),
            status = json.optString("status"),
            eventType = json.nullableString("eventType"),
            summary = json.nullableString("summary"),
            notified = json.optBoolean("notified"),
            notificationReason = json.nullableString("notificationReason"),
            confidence = json.nullableDouble("confidence"),
            linkedCameras = json.nullableString("linkedCameras"),
            packageTheftReason = json.nullableString("packageTheftReason"),
            incidentId = json.nullableString("incidentId"),
            triggerId = json.nullableString("triggerId"),
            zoneName = json.nullableString("zoneName"),
            confirmation = json.nullableString("confirmation"),
            cameraId = json.nullableString("cameraId"),
            triggerAreaFraction = json.nullableDouble("triggerAreaFraction"),
        )

    private fun parseCamera(json: JSONObject): Camera =
        Camera(
            id = json.getString("id"),
            name = json.optString("name", "Camera"),
            streamUrl = json.nullableString("streamUrl"),
            includeAudio = json.optBoolean("includeAudio"),
            ptzEnabled = json.optBoolean("ptzEnabled"),
            onvifPtzServiceUrl = json.nullableString("onvifPtzServiceUrl"),
            onvifProfileToken = json.nullableString("onvifProfileToken"),
        )

    private fun JSONArray?.toStringList(): List<String> {
        if (this == null) return emptyList()
        return buildList {
            for (index in 0 until length()) add(optString(index))
        }
    }

    private fun parseSchedule(json: JSONObject): Schedule {
        val action = json.optJSONObject("action") ?: JSONObject()
        val days = json.optJSONArray("daysOfWeek") ?: JSONArray()

        return Schedule(
            id = json.getString("id"),
            name = json.optString("name", "Schedule"),
            enabled = json.optBoolean("enabled", true),
            kind = json.optString("kind", "daily"),
            timeOfDay = json.optString("timeOfDay", ""),
            daysOfWeek = buildList {
                for (index in 0 until days.length()) add(days.getInt(index))
            },
            intervalSeconds = json.optInt("intervalSeconds", 900),
            windowStart = json.optString("windowStart", ""),
            windowEnd = json.optString("windowEnd", ""),
            cronExpression = json.optString("cronExpression", ""),
            runAtUtc = json.nullableString("runAtUtc")?.let { parseInstant(it) },
            solarEvent = json.optString("solarEvent", "dusk"),
            solarOffsetMinutes = json.optInt("solarOffsetMinutes"),
            solarElevationDegrees = json.optDouble("solarElevationDegrees", -6.0),
            action = ScheduleAction(
                type = action.optString("type", "device"),
                deviceId = action.nullableString("deviceId"),
                deviceAction = action.optString("deviceAction", ""),
                deviceBrightness = if (action.isNull("deviceBrightness")) null else action.optInt("deviceBrightness"),
                mqttTopic = action.optString("mqttTopic", ""),
                mqttPayload = action.optString("mqttPayload", ""),
                mqttRetain = action.optBoolean("mqttRetain"),
                webhookUrl = action.optString("webhookUrl", ""),
                webhookMethod = action.optString("webhookMethod", "POST"),
                webhookHeaders = action.optString("webhookHeaders", ""),
                webhookBody = action.optString("webhookBody", ""),
                webhookContentType = action.optString("webhookContentType", "application/json"),
            ),
            catchUpPolicy = json.optString("catchUpPolicy", "skip"),
            lastRunUtc = json.nullableString("lastRunUtc")?.let { parseInstant(it) },
            lastStatus = json.optString("lastStatus", ""),
            lastDetail = json.optString("lastDetail", ""),
            nextDueUtc = json.nullableString("nextDueUtc")?.let { parseInstant(it) },
            runCount = json.optLong("runCount"),
        )
    }

    private fun ScheduleInput.toJson(): JSONObject =
        JSONObject().apply {
            put("name", name)
            put("enabled", enabled)
            put("kind", kind)
            put("timeOfDay", timeOfDay)
            put("daysOfWeek", JSONArray(daysOfWeek))
            put("intervalSeconds", intervalSeconds)
            put("windowStart", windowStart)
            put("windowEnd", windowEnd)
            put("cronExpression", cronExpression)
            // JSONObject.put with a null drops the key, which the server reads as "not set" -
            // exactly right for every timing kind that isn't a one-off.
            put("runAtUtc", runAtUtc?.toString())
            put("solarEvent", solarEvent)
            put("solarOffsetMinutes", solarOffsetMinutes)
            put("solarElevationDegrees", solarElevationDegrees)
            put("actionType", actionType)
            put("deviceId", deviceId)
            put("deviceAction", deviceAction)
            put("deviceBrightness", deviceBrightness)
            put("mqttTopic", mqttTopic)
            put("mqttPayload", mqttPayload)
            put("mqttRetain", mqttRetain)
            put("webhookUrl", webhookUrl)
            put("webhookMethod", webhookMethod)
            put("webhookHeaders", webhookHeaders)
            put("webhookBody", webhookBody)
            put("webhookContentType", webhookContentType)
            put("catchUpPolicy", catchUpPolicy)
        }

    private fun parseDevice(json: JSONObject): HubDevice =
        HubDevice(
            id = json.getString("id"),
            name = json.optString("name", "Device"),
            kind = json.optString("kind", "light"),
            room = json.optString("room", ""),
            enabled = json.optBoolean("enabled", true),
            commandTopic = json.optString("commandTopic", ""),
            stateTopic = json.optString("stateTopic", ""),
            availabilityTopic = json.optString("availabilityTopic", ""),
            payloadOn = json.optString("payloadOn", "ON"),
            payloadOff = json.optString("payloadOff", "OFF"),
            payloadStop = json.optString("payloadStop", ""),
            momentary = json.optBoolean("momentary"),
            payloadAvailable = json.optString("payloadAvailable", "online"),
            payloadNotAvailable = json.optString("payloadNotAvailable", "offline"),
            schema = json.optString("schema", "raw"),
            stateValueTemplate = json.optString("stateValueTemplate", ""),
            optimistic = json.optBoolean("optimistic"),
            supportsBrightness = json.optBoolean("supportsBrightness"),
            brightnessCommandTopic = json.optString("brightnessCommandTopic", ""),
            brightnessStateTopic = json.optString("brightnessStateTopic", ""),
            brightnessScale = json.optInt("brightnessScale", 255),
            discoverySource = json.optString("discoverySource", "manual"),
            power = json.optString("power", "unknown"),
            brightness = if (json.isNull("brightness")) null else json.optInt("brightness"),
            available = json.optBoolean("available", true),
            commandable = json.optBoolean("commandable", true),
            measurements = parseMeasurements(json),
        )

    // Absent on a server older than the numeric sensor update, which reads the same as a device
    // that reports nothing - an empty list either way. Order is the server's; it sorts by key so
    // the rows hold still between polls, and re-sorting here would only be a chance to disagree.
    private fun parseMeasurements(json: JSONObject): List<HubMeasurement> {
        val array = json.optJSONArray("measurements") ?: return emptyList()
        return buildList {
            for (index in 0 until array.length()) {
                val item = array.optJSONObject(index) ?: continue
                val key = item.optString("key", "")
                if (key.isEmpty()) continue
                add(HubMeasurement(key = key, value = item.optDouble("value", Double.NaN)))
            }
        }
    }

    private fun parseCandidate(json: JSONObject): DeviceCandidate =
        DeviceCandidate(
            suggestedName = json.optString("suggestedName", "Device"),
            kind = json.optString("kind", "light"),
            commandTopic = json.optString("commandTopic", ""),
            stateTopic = json.optString("stateTopic", ""),
            availabilityTopic = json.optString("availabilityTopic", ""),
            payloadOn = json.optString("payloadOn", "ON"),
            payloadOff = json.optString("payloadOff", "OFF"),
            payloadStop = json.optString("payloadStop", ""),
            momentary = json.optBoolean("momentary"),
            schema = json.optString("schema", "raw"),
            stateValueTemplate = json.optString("stateValueTemplate", ""),
            supportsBrightness = json.optBoolean("supportsBrightness"),
            brightnessCommandTopic = json.optString("brightnessCommandTopic", ""),
            brightnessStateTopic = json.optString("brightnessStateTopic", ""),
            brightnessScale = json.optInt("brightnessScale", 255),
            source = json.optString("source", "manual"),
            alreadyAdded = json.optBoolean("alreadyAdded"),
        )

    private fun HubDeviceInput.toJson(): JSONObject =
        JSONObject()
            .put("name", name)
            .put("kind", kind)
            .put("room", room)
            .put("enabled", enabled)
            .put("commandTopic", commandTopic)
            .put("stateTopic", stateTopic)
            .put("availabilityTopic", availabilityTopic)
            .put("payloadOn", payloadOn)
            .put("payloadOff", payloadOff)
            .put("payloadStop", payloadStop)
            .put("momentary", momentary)
            .put("payloadAvailable", payloadAvailable)
            .put("payloadNotAvailable", payloadNotAvailable)
            .put("schema", schema)
            .put("stateValueTemplate", stateValueTemplate)
            .put("optimistic", optimistic)
            .put("supportsBrightness", supportsBrightness)
            .put("brightnessCommandTopic", brightnessCommandTopic)
            .put("brightnessStateTopic", brightnessStateTopic)
            .put("brightnessScale", brightnessScale)
            .put("discoverySource", discoverySource)

    private fun parseKnownFace(json: JSONObject): KnownFace {
        val array = json.optJSONArray("enrollments") ?: JSONArray()
        val enrollments = buildList {
            for (index in 0 until array.length()) {
                val enrollment = array.getJSONObject(index)
                add(KnownFaceEnrollment(id = enrollment.getString("id"), photoUrl = enrollment.getString("photoUrl")))
            }
        }
        return KnownFace(id = json.getString("id"), label = json.optString("label", ""), enrollments = enrollments)
    }

    private fun parseAccount(json: JSONObject): Account =
        Account(
            id = json.getString("id"),
            username = json.optString("username"),
            displayName = json.nullableString("displayName"),
            role = json.optString("role", "Viewer"),
            createdAt = parseInstant(json.getString("createdAt")),
        )

    private fun parsePolygon(array: JSONArray?): List<Point2D> = buildList {
        val points = array ?: JSONArray()
        for (index in 0 until points.length()) {
            val point = points.getJSONObject(index)
            add(Point2D(x = point.getDouble("x"), y = point.getDouble("y")))
        }
    }

    private fun parseTriggerConfig(json: JSONObject): TriggerConfig {
        // Left null rather than defaulted when the server didn't send it, so the distinction
        // between "no zones key" and "an empty list" survives a round-trip - see TriggerConfig.
        val zonesArray = json.optJSONArray("zones")
        val zones = zonesArray?.let { array ->
            buildList {
                for (index in 0 until array.length()) {
                    val zone = array.getJSONObject(index)
                    add(
                        TriggerZone(
                            name = zone.optString("name").takeIf { it.isNotBlank() }
                                ?: "Zone ${index + 1}",
                            polygon = parsePolygon(zone.optJSONArray("polygon")),
                            minTriggerArea = zone.optDouble("minTriggerArea", 0.02),
                        ),
                    )
                }
            }
        }
        return TriggerConfig(
            enabled = json.optBoolean("enabled"),
            zonePolygon = parsePolygon(json.optJSONArray("zonePolygon")),
            minTriggerArea = json.optDouble("minTriggerArea", 0.02),
            zones = zones,
            cooldownSeconds = json.optInt("cooldownSeconds", 5),
            maxTriggersPerMinute = json.optInt("maxTriggersPerMinute", 6),
            shadowSensitivity = json.optInt("shadowSensitivity", 50),
            recordClips = json.optBoolean("recordClips"),
            clipPreSeconds = json.optInt("clipPreSeconds", 5),
            clipPostSeconds = json.optInt("clipPostSeconds", 10),
            sendToAlerts = json.optBoolean("sendToAlerts"),
            alertProfile = json.optString("alertProfile", "default"),
            sensorDeviceId = json.optString("sensorDeviceId", ""),
            sensorTriggerOn = json.optString("sensorTriggerOn", "on"),
            sensorCooldownSeconds = json.optInt("sensorCooldownSeconds", 30),
            // Left null rather than defaulted when the server didn't send them, so a response from
            // a server older than the night window stays distinguishable from one that really did
            // say "off at 21:00" - see TriggerConfig. optBoolean/optInt would collapse both to a
            // value here and the difference would be gone by the time it mattered.
            nightWindowEnabled = if (json.isNull("nightWindowEnabled")) null else json.optBoolean("nightWindowEnabled"),
            nightStartHour = if (json.isNull("nightStartHour")) null else json.optInt("nightStartHour"),
            nightEndHour = if (json.isNull("nightEndHour")) null else json.optInt("nightEndHour"),
        )
    }

    private fun CameraInput.toJson(): JSONObject =
        JSONObject()
            .put("name", name)
            .put("streamUrl", streamUrl)
            .put("username", username)
            .put("password", password)
            .put("includeAudio", includeAudio)
            .put("ptzEnabled", ptzEnabled)
            .put("onvifPtzServiceUrl", onvifPtzServiceUrl)
            .put("onvifProfileToken", onvifProfileToken)

    private fun polygonJson(points: List<Point2D>): JSONArray {
        val array = JSONArray()
        points.forEach { point ->
            array.put(JSONObject().put("x", point.x).put("y", point.y))
        }
        return array
    }

    private fun TriggerConfig.toJson(): JSONObject {
        val json = JSONObject()
            .put("enabled", enabled)
            .put("zonePolygon", polygonJson(zonePolygon))
            .put("minTriggerArea", minTriggerArea)
            .put("cooldownSeconds", cooldownSeconds)
            .put("maxTriggersPerMinute", maxTriggersPerMinute)
            .put("shadowSensitivity", shadowSensitivity)
            .put("recordClips", recordClips)
            .put("clipPreSeconds", clipPreSeconds)
            .put("clipPostSeconds", clipPostSeconds)
            .put("sendToAlerts", sendToAlerts)
            .put("alertProfile", alertProfile)
            // Round-tripped untouched so saving a zone doesn't unbind the camera's sensor.
            .put("sensorDeviceId", sensorDeviceId)
            .put("sensorTriggerOn", sensorTriggerOn)
            .put("sensorCooldownSeconds", sensorCooldownSeconds)

        // Only when we actually have a list. The key must stay absent otherwise: an empty array
        // tells the server to delete every zone but the primary. JSONObject.put(String, null)
        // would remove the key, but going through it at all invites someone to "simplify" this
        // into always putting a possibly-empty list, which is the bug this guards against.
        zones?.let { list ->
            val array = JSONArray()
            list.forEach { zone ->
                array.put(
                    JSONObject()
                        .put("name", zone.name)
                        .put("polygon", polygonJson(zone.polygon))
                        .put("minTriggerArea", zone.minTriggerArea),
                )
            }
            json.put("zones", array)
        }

        // All three keys or none of them. The server merges each key it doesn't receive against
        // the stored row, so a partial set would save half of what the user chose and leave the
        // rest at whatever was there before - and an absent key is specifically NOT "off". The
        // editor always has all three; this guard is what stops a future caller building a config
        // with only one of them set from silently half-saving it.
        if (nightWindowEnabled != null && nightStartHour != null && nightEndHour != null) {
            json.put("nightWindowEnabled", nightWindowEnabled)
                .put("nightStartHour", nightStartHour)
                .put("nightEndHour", nightEndHour)
        }
        return json
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

// Splits a stored "Cookie:" request header back into one Set-Cookie value per cookie, each pinned
// to the site root, so the jar can be refilled from the encrypted store after a restart.
//
// Both halves are load-bearing. A whole header handed over as a single Set-Cookie parses as the
// first cookie plus junk attributes, silently dropping the rest. And a Set-Cookie with no Path
// takes the requesting URL's directory, so a session restored while fetching /api/timeline/day
// gets scoped to /api/timeline/ - matching later /api calls by luck while staying invisible to any
// lookup from the root, which is how media playback lost the session that the rest of the app had.
internal fun restoredSetCookieValues(storedHeader: String): List<String> =
    storedHeader.split(';')
        .map(String::trim)
        .filter(String::isNotEmpty)
        .map { "$it; Path=/" }

// Whether these Set-Cookie values include a session cookie that this connection's scheme can never
// send back: a Secure cookie delivered over plain http.
//
// java.net.CookieManager accepts such a cookie and then declines to return it on any http:// URL,
// with no error raised anywhere - so a successful login is followed by an unbroken run of 401s.
// The server marks its session cookie Secure whenever the request it answers arrived over https,
// which includes arriving through a TLS-terminating proxy (a Cloudflare Tunnel, say) that this app
// itself reaches over http. That combination is invisible from this side except right here.
//
// Deletion cookies are ignored on purpose: /api/auth/logout clears the session by setting an empty
// value with a past expiry, and that carries the same Secure attribute without being a session
// anyone is about to lose.
internal fun carriesUnusableSecureCookie(scheme: String, setCookieValues: List<String>): Boolean {
    if (!scheme.equals("http", ignoreCase = true)) return false
    return setCookieValues.any { header ->
        runCatching { HttpCookie.parse(header) }.getOrNull()
            .orEmpty()
            .any { it.secure && it.value.isNotBlank() }
    }
}
