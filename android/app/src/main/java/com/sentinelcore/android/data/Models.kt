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

data class TriggerEvent(
    val id: String,
    val cameraId: String,
    val cameraName: String,
    val occurredAt: Instant,
    val triggerAreaFraction: Double,
    val hasClip: Boolean,
)

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

// Mirrors the server's TriggerConfigDto - used for both the GET response and the PUT body.
data class TriggerConfig(
    val enabled: Boolean,
    val zonePolygon: List<Point2D>,
    val minTriggerArea: Double,
    val cooldownSeconds: Int,
    val maxTriggersPerMinute: Int,
    val shadowSensitivity: Int,
    val recordClips: Boolean,
    val clipPreSeconds: Int,
    val clipPostSeconds: Int,
    val sendToAlerts: Boolean,
    val alertProfile: String,
)

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
    // "on", "off", or "unknown" - a String rather than a Boolean because "never heard from this
    // device" is a genuinely different state from "off", and the UI says so.
    val power: String,
    val brightness: Int?,
    val available: Boolean,
    val commandable: Boolean,
) {
    val isOn: Boolean get() = power == "on"
}

data class HubDeviceState(
    val id: String,
    val power: String,
    val brightness: Int?,
    val available: Boolean,
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
