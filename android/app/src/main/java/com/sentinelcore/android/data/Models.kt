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
