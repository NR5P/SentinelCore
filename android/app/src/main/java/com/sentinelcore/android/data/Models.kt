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
)

data class AuthStatus(
    val enabled: Boolean,
    val configured: Boolean,
    val authenticated: Boolean,
    val username: String?,
)

data class PushStatus(
    val enabled: Boolean,
    val appleConfigured: Boolean,
    val firebaseConfigured: Boolean,
    val registeredDevices: Int,
)

data class PushDeliveryReport(
    val attempted: Int,
    val delivered: Int,
    val failed: Int,
    val removed: Int,
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
    NOT_CONFIGURED("SET SERVER"),
    CHECKING("CONNECTING"),
    ONLINE("LINK ESTABLISHED"),
    AUTHENTICATION_REQUIRED("SIGN IN"),
    OFFLINE("LINK LOST"),
}

class SentinelApiException(
    val statusCode: Int,
    override val message: String,
) : Exception(message)
