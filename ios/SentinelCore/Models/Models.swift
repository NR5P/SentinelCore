import Foundation

struct SecurityEvent: Codable, Identifiable, Hashable {
    let id: UUID
    let camera: String
    let occurredAt: Date
    let status: String
    let eventType: String
    let summary: String
    let notified: Bool
    let notificationReason: String
    let confidence: Double
    let hasImage: Bool
}

struct TriggerEvent: Codable, Identifiable, Hashable {
    let id: UUID
    let cameraId: UUID
    let cameraName: String
    let occurredAt: Date
    let triggerAreaFraction: Double
    let hasClip: Bool
}

struct Camera: Codable, Identifiable, Hashable {
    let id: UUID
    let name: String
    let streamUrl: String?
    let includeAudio: Bool?
}

struct CameraInput: Encodable {
    var name: String
    var streamUrl: String
    var username: String?
    var password: String?
    var includeAudio: Bool
}

struct Point2D: Codable, Hashable {
    var x: Double
    var y: Double
}

// Mirrors the server's TriggerConfigDto - used for both the GET response and the PUT body.
struct TriggerConfig: Codable, Equatable {
    var enabled: Bool
    var zonePolygon: [Point2D]
    var minTriggerArea: Double
    var cooldownSeconds: Int
    var maxTriggersPerMinute: Int
    var shadowSensitivity: Int
    var recordClips: Bool
    var clipPreSeconds: Int
    var clipPostSeconds: Int
    var sendToAlerts: Bool
    var alertProfile: String
}

struct PromptProfileName: Codable, Identifiable, Hashable {
    var id: String { key }
    var key: String
    var displayName: String
}

struct CameraProbeRequest: Encodable {
    var address: String
    var username: String?
    var password: String?
}

struct CameraProbeResult: Codable {
    var status: String // "Found" | "NeedsCredentials" | "NotFound"
    var streamUrl: String?
    var suggestedName: String?
    var manufacturer: String?
    var model: String?
    var resolution: String?
    var message: String?
}

struct ScanInterface: Codable, Identifiable, Hashable {
    var id: String { address }
    var name: String
    var description: String
    var address: String
    var prefixLength: Int
    var looksVirtual: Bool
}

struct ScanRequest: Encodable {
    var interfaceAddresses: [String]
}

struct ScanStartResult: Codable {
    var scanId: UUID
}

struct ScanResultCamera: Codable, Identifiable, Hashable {
    var id: String { ipAddress }
    var ipAddress: String
    var manufacturer: String?
    var model: String?
    var suggestedName: String
    var needsCredentials: Bool
    var alreadySaved: Bool
}

struct ScanJobSnapshot: Codable {
    var status: String // "Running" | "Completed"
    var results: [ScanResultCamera]
}

struct AuthStatus: Codable {
    let enabled: Bool
    let configured: Bool
    let authenticated: Bool
    let username: String?
    // Absent on servers older than the multi-account update - treated as Admin everywhere it's
    // read, since that's what every pre-upgrade single-account login effectively was.
    let role: String?
}

struct Account: Codable, Identifiable, Hashable {
    let id: UUID
    let username: String
    let displayName: String?
    let role: String
    let createdAt: Date
}

struct HealthResponse: Codable {
    let status: String
    let timestamp: Date?
}

struct APIError: Codable {
    let error: String?
    let errors: [String]?
}

struct LoginBody: Encodable {
    let username: String
    let password: String
}

struct CreateAccountBody: Encodable {
    let username: String
    let password: String
    let role: String
    let displayName: String?
}

struct UpdateRoleBody: Encodable {
    let role: String
}

struct ResetPasswordBody: Encodable {
    let newPassword: String
}

struct ChangePasswordBody: Encodable {
    let currentPassword: String
    let newPassword: String
}

struct PushStatus: Codable {
    let enabled: Bool
    let appleConfigured: Bool
    let firebaseConfigured: Bool
    let registeredDevices: Int
}

struct PushDeviceBody: Encodable {
    let platform: String
    let token: String
    let deviceName: String?
}

struct PushDeliveryReport: Codable {
    let attempted: Int
    let delivered: Int
    let failed: Int
    let removed: Int
}

struct RecordingTimelineDay: Codable {
    let camera: String
    let date: String
    let start: Date
    let end: Date
    let recording: TimelineRecording
    var events: [TimelineSecurityEvent]
}

struct TimelineRecording: Codable {
    let configured: Bool
    let available: Bool
    let sourceCamera: String
    let error: String?
    let mode: String
    let preSeconds: Int
    let postSeconds: Int
    let segments: [TimelineRecordingSegment]
}

struct TimelineRecordingSegment: Codable, Hashable {
    let eventId: UUID
    let triggerEventId: UUID
    let start: Date
    let end: Date
}

struct TimelineSecurityEvent: Codable, Identifiable, Hashable {
    let id: UUID
    let camera: String
    let occurredAt: Date
    let eventType: String
    let category: String
    let severity: String
    let summary: String
    let confidence: Double
    let notified: Bool
    var reviewed: Bool
    let hasImage: Bool
    let hasClip: Bool
}

struct ReviewedBody: Encodable {
    let reviewed: Bool
}

extension JSONDecoder {
    static let sentinel: JSONDecoder = {
        let decoder = JSONDecoder()
        // ASP.NET Core emits DateTimeOffset values with fractional seconds. Foundation's
        // built-in `.iso8601` strategy rejects those on some OS releases, so accept both
        // shapes explicitly.
        decoder.dateDecodingStrategy = .custom { decoder in
            let value = try decoder.singleValueContainer().decode(String.self)
            if let date = try? Date.ISO8601FormatStyle(includingFractionalSeconds: true).parse(value) {
                return date
            }
            if let date = try? Date.ISO8601FormatStyle(includingFractionalSeconds: false).parse(value) {
                return date
            }
            throw DecodingError.dataCorruptedError(
                in: try decoder.singleValueContainer(),
                debugDescription: "Invalid ISO-8601 date: \(value)"
            )
        }
        return decoder
    }()
}
