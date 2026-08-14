package com.sentinelcore.android.data

import java.time.Instant

data class SecurityEvent(
    val id: String,
    val camera: String,
    val occurredAt: Instant,
    val status: String,
    val eventType: String,
    val summary: String,
    val notified: Boolean,
    val notificationReason: String,
    val confidence: Double,
    val hasImage: Boolean,
)

// One row of the Events list: a single moment, whichever records back it. The server joins the
// motion trigger and the AI analysis of that same frame into this (see ActivityEndpoints), so the
// app shows one card per thing that happened rather than one per row in a database.
//
// `kind` is what the UI branches on. "analyzed" carries a summary, event type, and notify
// decision; "motion" carries none of those and never will, because nothing looked at the frame -
// either the camera doesn't forward to AI analysis, or the forward was refused. Both kinds can
// carry a clip, since that comes from the trigger side.
//
// `id` is prefixed ("trigger:<guid>") on a motion row so the two sources can't collide in one
// list. Use triggerId/analysisId to get at the underlying ids.
data class ActivityItem(
    val id: String,
    val kind: String,
    val camera: String,
    val occurredAt: Instant,
    val imageUrl: String?,
    val clipUrl: String?,
    val status: String,
    val eventType: String?,
    val summary: String?,
    val notified: Boolean,
    val notificationReason: String?,
    val confidence: Double?,
    val linkedCameras: String?,
    val packageTheftReason: String?,
    val incidentId: String?,
    val triggerId: String?,
    val zoneName: String?,
    val confirmation: String?,
    val cameraId: String?,
    val triggerAreaFraction: Double?,
) {
    val isAnalyzed: Boolean get() = kind == "analyzed"
    val hasClip: Boolean get() = clipUrl != null

    // Only an analyzed row has an alert behind it, and only then is `id` a bare alert guid.
    val analysisId: String? get() = if (isAnalyzed) id else null

    // Rebuilds what the event detail screen already knows how to show, so the merged list can
    // open it without a second round-trip for fields the list already holds.
    fun asSecurityEvent(): SecurityEvent? {
        val alertId = analysisId ?: return null
        return SecurityEvent(
            id = alertId,
            camera = camera,
            occurredAt = occurredAt,
            status = status,
            eventType = eventType ?: "unknown",
            summary = summary ?: "",
            notified = notified,
            notificationReason = notificationReason ?: "",
            confidence = confidence ?: 0.0,
            hasImage = imageUrl != null,
        )
    }
}

data class Camera(
    val id: String,
    val name: String,
    val streamUrl: String?,
    val includeAudio: Boolean,
    val ptzEnabled: Boolean,
    // Carried along (not shown in any UI) purely so editing an existing camera without
    // re-running Test Connection doesn't wipe these on save - the PUT endpoint overwrites the
    // whole record, it doesn't merge, so these have to round-trip through every edit.
    val onvifPtzServiceUrl: String?,
    val onvifProfileToken: String?,
)

data class CameraInput(
    val name: String,
    val streamUrl: String,
    val username: String?,
    val password: String?,
    val includeAudio: Boolean,
    // onvifPtzServiceUrl/onvifProfileToken come from a discovery probe (see CameraProbeResult)
    // and are just passed through here on save, the same way probeResult.streamUrl already
    // flows into streamUrl - the client never makes these up itself.
    val ptzEnabled: Boolean,
    val onvifPtzServiceUrl: String?,
    val onvifProfileToken: String?,
)

data class KnownFace(
    val id: String,
    val label: String,
    val enrollments: List<KnownFaceEnrollment>,
)

data class KnownFaceEnrollment(
    val id: String,
    val photoUrl: String,
)

data class Point2D(val x: Double, val y: Double)

// One trigger zone: its own polygon and its own minimum object size, because perspective means the
// same subject covers wildly different pixel counts near the camera and far from it.
//
// Zones have no server-side id and their names aren't unique, so the editor identifies them by
// position in the list - same as the web build.
data class TriggerZone(
    val name: String,
    val polygon: List<Point2D>,
    val minTriggerArea: Double,
) {
    companion object {
        // Matches the server's TriggerConfig.DefaultZoneName.
        const val DEFAULT_NAME = "Zone 1"
    }
}

// Mirrors the server's TriggerConfigDto - used for both the GET response and the PUT body.
//
// Two rules here are load-bearing; see core/docs/MULTI_ZONE_CLIENT_HANDOFF.md.
//
// 1. zonePolygon/minTriggerArea must keep being sent whatever else changes - they mirror zones[0],
//    and the server keeps sending them for the benefit of clients that only read those.
// 2. `zones` null and `zones` empty mean different things to the server. Null omits the key
//    entirely, which reads as "this client doesn't know about zones - update the primary and leave
//    the rest alone". An empty list reads as "the editor really did delete them". Never send an
//    empty list as a placeholder; see TriggerConfig.toJson in SentinelRepository.
data class TriggerConfig(
    val enabled: Boolean,
    val zonePolygon: List<Point2D>,
    val minTriggerArea: Double,
    // Absent on a server older than multi-zone; read it through resolvedZones(), never directly.
    val zones: List<TriggerZone>?,
    val cooldownSeconds: Int,
    val maxTriggersPerMinute: Int,
    val shadowSensitivity: Int,
    val recordClips: Boolean,
    val clipPreSeconds: Int,
    val clipPostSeconds: Int,
    val sendToAlerts: Boolean,
    val alertProfile: String,
    // Not shown in any UI. Carried through every save purely so editing a zone from the phone
    // doesn't unbind a Device Hub sensor that was set up on the web dashboard - the PUT overwrites
    // the whole record rather than merging, so anything omitted reverts to its default (no sensor,
    // "on", 30s).
    val sensorDeviceId: String = "",
    val sensorTriggerOn: String = "on",
    val sensorCooldownSeconds: Int = 30,
    // The overnight trigger pause - see core/docs/NIGHT_WINDOW_HANDOFF.md. Nullable for the same
    // reason `zones` is, and it's the same trap: null omits the key, which the server reads as
    // "this client doesn't know about the night window" and merges against the stored row - it does
    // NOT mean "turn it off". A build that sent a plain `false` on every save would switch
    // someone's window off whenever they nudged a polygon from their phone, and nothing anywhere
    // would say so; the flood would just come back the next night.
    //
    // The editor knows about the feature, so it loads all three and sends all three back. Null is
    // what a server older than the night window looks like; read it through resolvedNightWindow(),
    // which supplies the same defaults the server would.
    val nightWindowEnabled: Boolean? = null,
    val nightStartHour: Int? = null,
    val nightEndHour: Int? = null,
) {
    // Always at least one zone, mirroring the server's TriggerConfig.ResolvedZones: a config
    // written before multi-zone reads as a single zone built from the legacy pair, so the editor
    // never has to special-case the migration.
    fun resolvedZones(): List<TriggerZone> =
        zones?.takeIf { it.isNotEmpty() }
            ?: listOf(TriggerZone(TriggerZone.DEFAULT_NAME, zonePolygon, minTriggerArea))

    // Same shape as resolvedZones(), for the same reason: a server older than the night window
    // sends none of the three keys, and the editor needs values to show either way. The fallbacks
    // match the server's own defaults (TriggerConfig in TriggerStore.cs) - off, and 21:00-07:00
    // rather than 0/0, so switching the feature on offers the window someone would actually have
    // asked for instead of a zero-width one that appears to do nothing.
    fun resolvedNightWindow(): NightWindow =
        NightWindow(nightWindowEnabled ?: false, nightStartHour ?: 21, nightEndHour ?: 7)
}

data class NightWindow(val enabled: Boolean, val startHour: Int, val endHour: Int)

data class PromptProfileName(val key: String, val displayName: String)

data class CameraProbeRequest(
    val address: String,
    val username: String?,
    val password: String?,
)

data class CameraProbeResult(
    val status: String, // "Found" | "NeedsCredentials" | "NotFound"
    val streamUrl: String?,
    val suggestedName: String?,
    val manufacturer: String?,
    val model: String?,
    val resolution: String?,
    val message: String?,
    // Only ever populated when an ONVIF PTZ service was actually found - absent (null/false)
    // just means this camera doesn't have one, which is the normal case, not a failure.
    val supportsPtz: Boolean = false,
    val onvifPtzServiceUrl: String? = null,
    val onvifProfileToken: String? = null,
)

data class ScanInterface(
    val name: String,
    val description: String,
    val address: String,
    val prefixLength: Int,
    val looksVirtual: Boolean,
)

data class ScanResultCamera(
    val ipAddress: String,
    val manufacturer: String?,
    val model: String?,
    val suggestedName: String,
    val needsCredentials: Boolean,
    val alreadySaved: Boolean,
)

data class ScanJobSnapshot(
    val status: String, // "Running" | "Completed"
    val results: List<ScanResultCamera>,
)

data class AuthStatus(
    val enabled: Boolean,
    val configured: Boolean,
    val authenticated: Boolean,
    val username: String?,
    // Absent on servers older than the multi-account update - treated as Admin everywhere it's
    // read, since that's what every pre-upgrade single-account login effectively was.
    val role: String?,
)

data class Account(
    val id: String,
    val username: String,
    val displayName: String?,
    val role: String,
    val createdAt: Instant,
)

data class PushStatus(
    val enabled: Boolean,
    val appleConfigured: Boolean,
    val firebaseConfigured: Boolean,
    val registeredDevices: Int,
)

data class SystemStatus(
    val cpuUsagePercent: Double,
    val processorCount: Int,
    val memoryUsedBytes: Long,
    val memoryTotalBytes: Long,
    val diskFreeBytes: Long,
    val diskTotalBytes: Long,
    val queuedAlerts: Int,
    val uptimeSeconds: Double,
    val visionBackendHealthy: Boolean,
) {
    val memoryUsedFraction: Double
        get() = if (memoryTotalBytes > 0) memoryUsedBytes.toDouble() / memoryTotalBytes else 0.0

    val diskUsedBytes: Long
        get() = diskTotalBytes - diskFreeBytes

    val diskUsedFraction: Double
        get() = if (diskTotalBytes > 0) diskUsedBytes.toDouble() / diskTotalBytes else 0.0
}

data class PushDeliveryReport(
    val attempted: Int,
    val delivered: Int,
    val failed: Int,
    val removed: Int,
)

data class RecordingTimelineDay(
    val camera: String,
    val date: String,
    val start: Instant,
    val end: Instant,
    val recording: TimelineRecording,
    val events: List<TimelineSecurityEvent>,
)

data class TimelineRecording(
    val configured: Boolean,
    val available: Boolean,
    val sourceCamera: String,
    val error: String?,
    val mode: String,
    val preSeconds: Int,
    val postSeconds: Int,
    val segments: List<TimelineRecordingSegment>,
)

data class TimelineRecordingSegment(
    val eventId: String,
    val triggerEventId: String,
    val start: Instant,
    val end: Instant,
)

data class TimelineSecurityEvent(
    val id: String,
    val camera: String,
    val occurredAt: Instant,
    val eventType: String,
    val category: String,
    val severity: String,
    val summary: String,
    val confidence: Double,
    val notified: Boolean,
    val reviewed: Boolean,
    val hasImage: Boolean,
    val hasClip: Boolean,
)

// One numeric reading a device published, keyed by whatever the payload called it. There are
// deliberately no units and no friendly name: the server hands over the raw number under the
// device's own key, because no two vendors agree on units well enough for guessing to beat
// saying nothing.
data class HubMeasurement(
    val key: String,
    val value: Double,
) {
    // Round to two decimals, then drop the trailing zeros - 21.500000000000004 is float noise
    // picked up from scaling upstream, not precision the sensor reported. Matches the dashboard's
    // formatMeasurement so the same reading reads identically on every client.
    val formattedValue: String
        get() {
            if (value.isNaN() || value.isInfinite()) return "-"
            val text = String.format(java.util.Locale.US, "%.2f", value)
                .trimEnd('0')
                .trimEnd('.')
            return if (text == "-0") "0" else text
        }
}

// A device in the Device Hub. Power/brightness/available are the live MQTT state the server
// tracks, carried on the same object as the configuration so the list renders from one call;
// /api/devices/state re-fetches just those while the screen is open.
data class HubDevice(
    val id: String,
    val name: String,
    val kind: String,
    val room: String,
    val enabled: Boolean,
    val commandTopic: String,
    val stateTopic: String,
    val availabilityTopic: String,
    val payloadOn: String,
    val payloadOff: String,
    // Optional third command for cover-like devices whose motor can halt mid-travel. Empty
    // means no Stop control is offered.
    val payloadStop: String = "",
    // A pulse device: one press publishes payloadOn and nothing more is expected back - a gate
    // relay wired to pulse, a garage remote, a doorbell. The "button" kind is always momentary.
    val momentary: Boolean = false,
    val payloadAvailable: String,
    val payloadNotAvailable: String,
    val schema: String,
    val stateValueTemplate: String,
    val optimistic: Boolean,
    val supportsBrightness: Boolean,
    val brightnessCommandTopic: String,
    val brightnessStateTopic: String,
    val brightnessScale: Int,
    val discoverySource: String,
    val sortOrder: Int = 0,
    // "on", "off", or "unknown" - a String rather than a Boolean because "never heard from this
    // device" is a genuinely different state from "off", and the UI says so.
    val power: String,
    val brightness: Int?,
    val available: Boolean,
    val commandable: Boolean,
    // The numeric readings this device published, sorted by key server-side. Defaulted rather
    // than required so a server older than the numeric sensor update still parses; render in the
    // order given so the rows don't reshuffle between polls.
    val measurements: List<HubMeasurement> = emptyList(),
) {
    val isOn: Boolean get() = power == "on"

    // A device with no on/off that is publishing numbers is working, not in an unknown state.
    val isReporting: Boolean get() = !commandable && power == "unknown" && measurements.isNotEmpty()

    // Kind-flavoured behaviour, mirroring DeviceTopics on the server.
    val isMomentary: Boolean get() = momentary || kind == "button"
    val isCoverLike: Boolean get() = kind == "cover" || kind == "gate" || kind == "garage"
    val isLock: Boolean get() = kind == "lock"
    val hasStop: Boolean get() = payloadStop.isNotBlank()
}

data class HubDeviceState(
    val id: String,
    val power: String,
    val brightness: Int?,
    val available: Boolean,
    // Defaulted for the same reason as HubDevice.measurements - see the note there.
    val measurements: List<HubMeasurement> = emptyList(),
)

data class HubDeviceStateSnapshot(
    val connected: Boolean,
    val devices: List<HubDeviceState>,
)

data class DeviceCommandResult(
    val sent: Boolean,
    val acknowledged: Boolean,
    val power: String,
    val brightness: Int?,
)

// What a discovery scan believes is out there. Not a device until the user adds it.
data class DeviceCandidate(
    val suggestedName: String,
    val kind: String,
    val commandTopic: String,
    val stateTopic: String,
    val availabilityTopic: String,
    val payloadOn: String,
    val payloadOff: String,
    val payloadStop: String = "",
    val momentary: Boolean = false,
    val schema: String,
    val stateValueTemplate: String,
    val supportsBrightness: Boolean,
    val brightnessCommandTopic: String,
    val brightnessStateTopic: String,
    val brightnessScale: Int,
    val source: String,
    val alreadyAdded: Boolean,
)

data class DeviceScanStartResult(
    val scanId: String,
    val scanSeconds: Int,
)

data class DeviceScanSnapshot(
    val status: String, // "running" | "completed"
    val results: List<DeviceCandidate>,
)

data class HubDeviceInput(
    val name: String = "",
    val kind: String = "light",
    val room: String = "",
    val enabled: Boolean = true,
    val commandTopic: String = "",
    val stateTopic: String = "",
    val availabilityTopic: String = "",
    val payloadOn: String = "ON",
    val payloadOff: String = "OFF",
    val payloadStop: String = "",
    val momentary: Boolean = false,
    val payloadAvailable: String = "online",
    val payloadNotAvailable: String = "offline",
    val schema: String = "raw",
    val stateValueTemplate: String = "",
    val optimistic: Boolean = false,
    val supportsBrightness: Boolean = false,
    val brightnessCommandTopic: String = "",
    val brightnessStateTopic: String = "",
    val brightnessScale: Int = 255,
    val discoverySource: String = "manual",
) {
    companion object {
        fun from(device: HubDevice) = HubDeviceInput(
            name = device.name,
            kind = device.kind,
            room = device.room,
            enabled = device.enabled,
            commandTopic = device.commandTopic,
            stateTopic = device.stateTopic,
            availabilityTopic = device.availabilityTopic,
            payloadOn = device.payloadOn,
            payloadOff = device.payloadOff,
            payloadStop = device.payloadStop,
            momentary = device.momentary,
            payloadAvailable = device.payloadAvailable,
            payloadNotAvailable = device.payloadNotAvailable,
            schema = device.schema,
            stateValueTemplate = device.stateValueTemplate,
            optimistic = device.optimistic,
            supportsBrightness = device.supportsBrightness,
            brightnessCommandTopic = device.brightnessCommandTopic,
            brightnessStateTopic = device.brightnessStateTopic,
            brightnessScale = device.brightnessScale,
            discoverySource = device.discoverySource,
        )

        fun from(candidate: DeviceCandidate) = HubDeviceInput(
            name = candidate.suggestedName,
            kind = candidate.kind,
            commandTopic = candidate.commandTopic,
            stateTopic = candidate.stateTopic,
            availabilityTopic = candidate.availabilityTopic,
            payloadOn = candidate.payloadOn,
            payloadOff = candidate.payloadOff,
            payloadStop = candidate.payloadStop,
            momentary = candidate.momentary,
            schema = candidate.schema,
            // A JSON device found by discovery always keys its state off "state"; without this
            // the server rejects the save for a missing template.
            stateValueTemplate = candidate.stateValueTemplate.ifBlank {
                if (candidate.schema == "json") "state" else ""
            },
            supportsBrightness = candidate.supportsBrightness,
            brightnessCommandTopic = candidate.brightnessCommandTopic,
            brightnessStateTopic = candidate.brightnessStateTopic,
            brightnessScale = candidate.brightnessScale,
            discoverySource = candidate.source,
        )
    }
}

data class DiagnosticEntry(
    val id: Long,
    val timestamp: Instant,
    val level: DiagnosticLevel,
    val message: String,
)

enum class DiagnosticLevel {
    INFO,
    NOTICE,
    ERROR,
}

enum class ConnectionState(val label: String) {
    NOT_CONFIGURED("Set server"),
    CHECKING("Connecting…"),
    ONLINE("Connected"),
    AUTHENTICATION_REQUIRED("Sign in required"),
    OFFLINE("Offline"),
}

class SentinelApiException(
    val statusCode: Int,
    override val message: String,
) : Exception(message)

// The server-side settings schema - which sections exist and what fields they contain, served
// by GET /api/settings/schema and shared verbatim with the web dashboard and the iOS/macOS app.
// A settings section added on the server appears in this app with no update; these classes only
// mirror the shapes, ServerSettingsScreen renders them.

// What the HomeKit sidecar reports about itself. "running" and "published" are deliberately
// separate: the sidecar runs for everyone but only advertises on the network once HomeKit is
// switched on, and the screen needs both to say anything useful.
data class HomeKitStatus(
    val running: Boolean,
    val enabled: Boolean = false,
    val published: Boolean = false,
    val paired: Boolean = false,
    val accessories: List<String> = emptyList(),
    val pairingCode: String? = null,
    val error: String? = null,
)

// The four editable HomeKit settings. Hand-written rather than schema-driven because the useful
// half of that section is the pairing code, which no schema can describe.
data class HomeKitSettings(
    val enabled: Boolean,
    val sidecarBaseUrl: String,
    val timeoutSeconds: Int,
    val minimumConfidence: Double,
)

// A number plate the AI should treat as household rather than a stranger's. Matching ignores
// spaces and punctuation server-side, so "ABC 123" and "ABC-123" are the same vehicle.
data class KnownVehicle(val plate: String, val label: String)

data class SettingsFieldOption(val value: String, val label: String)

// A "look this up for me" button on a text field (today: LOCATION's Town or City, which fills in
// latitude and longitude). Pure data like SettingsAction - an endpoint taking ?query= - so nothing
// here is location-specific, and a field without one is just a text box.
data class SettingsLookup(
    val label: String,
    val tip: String,
    val endpoint: String,
)

// One option a lookup came back with: something to show in the list, and the field values to write
// if it's picked. The keys are field names in the same section; this app applies the ones it
// recognises and ignores the rest, so a newer server can't break an older build.
data class SettingsLookupCandidate(
    val label: String,
    val values: Map<String, Any>,
)

data class SettingsField(
    val name: String,
    val label: String,
    // "text", "number", "checkbox", "password", "select", or "csv". Anything unrecognized is
    // rendered read-only rather than guessed at.
    val type: String,
    val tip: String,
    val options: List<SettingsFieldOption>,
    // Bounds for a "number" field, mirroring the [Range] on the server's DTO property. Carried so
    // this app can say "must be between 0 and 1" under the box instead of sending the value and
    // relaying whatever the server's rejection said - the schema has always published these, and
    // this app was simply dropping them on the floor.
    val min: Double?,
    val max: Double?,
    // True when the number is a whole one (an int or long on the server). Without it there is no
    // telling a port from a free-space threshold in GB: both are plain numbers with no step, and
    // only one of them may be 3.7. Null against a server predating the flag, which has to mean
    // "apply no extra restriction" rather than guessing.
    val integer: Boolean?,
    // Absent on all but a handful of text fields - see SettingsLookup.
    val lookup: SettingsLookup?,
    val requiresRestart: Boolean,
)

// An extra button a section offers (currently just Bridge's "re-run setup wizard") - pure data,
// an endpoint to call, so no client needs bespoke code per action.
data class SettingsAction(
    val label: String,
    val tip: String,
    val endpoint: String,
    val method: String,
)

data class SettingsSection(
    val key: String,
    val title: String,
    val endpoint: String,
    val description: String,
    val fields: List<SettingsField>,
    val extraAction: SettingsAction?,
    // Which heading this section belongs under, and where that heading sorts. Both come from the
    // server (SettingsGroups) so this app and the web dashboard divide the settings the same way
    // without either holding its own copy of the grouping. Defaulted rather than nullable so an
    // older server that predates grouping still renders - as one unnamed group, i.e. the flat list
    // this screen had before.
    val group: String = "",
    val groupOrder: Int = Int.MAX_VALUE,
    // Whether this section shows in basic mode. Advanced sections are the ones a typical install
    // never needs to touch.
    val basic: Boolean = false,
    // Which service has to restart for requiresRestart fields here to take effect. Every section
    // but INTERNET EXPOSED MODE is the bridge.
    val restartService: String = "bridge",
    // Where to GET this section's current values, when they aren't in the bulk /api/settings
    // snapshot - only INTERNET EXPOSED MODE sets it.
    val dataEndpoint: String? = null,
) {
    val needsRestart: Boolean get() = fields.any { it.requiresRestart }

    // Field tips are searched as well as labels: they're where a setting is described in the words
    // someone would actually type - nobody searches for "nuisanceAdvisor".
    fun matches(query: String): Boolean =
        title.contains(query, ignoreCase = true) ||
            description.contains(query, ignoreCase = true) ||
            fields.any {
                it.label.contains(query, ignoreCase = true) || it.tip.contains(query, ignoreCase = true)
            }
}

// Rebooting the machine the server runs on - mirrors HostRebootProbe in HostReboot.cs.
//
// setupScript is the server's own answer for its own operating system, user and paths, and is
// shown verbatim rather than rebuilt here. An app that composed that sudoers line itself would
// eventually compose the wrong one for somebody's install and be believed.
data class HostRebootProbe(
    val platform: String,
    val platformLabel: String,
    val readiness: String,
    val enabled: Boolean,
    val runningAs: String,
    val summary: String,
    val delaySeconds: Int,
    val setupScript: String?,
    val setupNote: String?,
    val secondsRemaining: Int?,
) {
    val isReady: Boolean get() = readiness == "ready"
    val needsSetup: Boolean get() = readiness == "setupRequired"

    // Two separate facts, and the gap between them is the point of the whole screen: one is
    // fixed by the checkbox above, the other only at a shell on the server.
    val canReboot: Boolean get() = isReady && enabled
}

data class HostRebootOutcome(
    val ok: Boolean,
    val message: String,
    val secondsRemaining: Int?,
)

// --- Schedules -------------------------------------------------------------------------------

// A "do this at that time" rule - the clock-driven counterpart to the dashboard's automation
// rules, sharing their action vocabulary plus a Device Hub option. See ScheduleModel.cs.
data class ScheduleAction(
    val type: String,
    val deviceId: String?,
    val deviceAction: String,
    val deviceBrightness: Int?,
    val mqttTopic: String,
    val mqttPayload: String,
    val mqttRetain: Boolean,
    val webhookUrl: String,
    val webhookMethod: String,
    val webhookHeaders: String,
    val webhookBody: String,
    val webhookContentType: String,
)

data class Schedule(
    val id: String,
    val name: String,
    val enabled: Boolean,
    val kind: String,
    val timeOfDay: String,
    val daysOfWeek: List<Int>,
    val intervalSeconds: Int,
    val windowStart: String,
    val windowEnd: String,
    val cronExpression: String,
    val runAtUtc: Instant?,
    val solarEvent: String,
    val solarOffsetMinutes: Int,
    // How far below the horizon the sun must be for a "dusk"/"dawn" firing - a darkness level
    // rather than a time, which is what makes it track the seasons on its own.
    val solarElevationDegrees: Double,
    val action: ScheduleAction,
    val catchUpPolicy: String,
    val lastRunUtc: Instant?,
    val lastStatus: String,
    val lastDetail: String,
    val nextDueUtc: Instant?,
    val runCount: Long,
) {
    val followsTheSun: Boolean get() = kind == "solar"
}

data class ScheduleRun(
    val id: String,
    val scheduleId: String,
    val dueAtUtc: Instant,
    val firedAtUtc: Instant,
    val status: String,
    val detail: String,
    val durationMs: Long,
)

// One of the named darkness levels the server offers, so this app doesn't have to know that
// "civil twilight" means -6 degrees.
data class DarknessLevel(
    val key: String,
    val label: String,
    val description: String,
    val elevationDegrees: Double,
)

data class ScheduleDeviceOption(
    val id: String,
    val name: String,
    val kind: String,
    val room: String,
    val enabled: Boolean,
    val supportsBrightness: Boolean,
    val momentary: Boolean,
    val coverLike: Boolean,
    val hasStop: Boolean,
)

data class ScheduleMetadata(
    val darknessLevels: List<DarknessLevel> = emptyList(),
    val deviceActions: List<String> = emptyList(),
    val tokens: List<String> = emptyList(),
    val minimumIntervalSeconds: Int = 10,
    // Whether coordinates have been set. A sun-following schedule can't work without them, and
    // saying so up front beats a save that fails.
    val locationSet: Boolean = false,
    val timeZone: String = "",
    val devices: List<ScheduleDeviceOption> = emptyList(),
)

data class SchedulePreview(
    val occurrences: List<String> = emptyList(),
    val timeZone: String = "",
    val explanation: String = "",
)

data class ScheduleInput(
    val name: String,
    val enabled: Boolean,
    val kind: String,
    val timeOfDay: String,
    val daysOfWeek: List<Int>,
    val intervalSeconds: Int,
    val windowStart: String,
    val windowEnd: String,
    val cronExpression: String,
    val runAtUtc: Instant?,
    val solarEvent: String,
    val solarOffsetMinutes: Int,
    val solarElevationDegrees: Double,
    val actionType: String,
    val deviceId: String?,
    val deviceAction: String,
    val deviceBrightness: Int?,
    val mqttTopic: String,
    val mqttPayload: String,
    val mqttRetain: Boolean,
    val webhookUrl: String,
    val webhookMethod: String,
    val webhookHeaders: String,
    val webhookBody: String,
    val webhookContentType: String,
    val catchUpPolicy: String,
)

data class ScheduleRunResult(val fired: Boolean, val detail: String)
