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

// One row of the Events list: a single moment, whichever records back it. The server joins the
// motion trigger and the AI analysis of that same frame into this (see ActivityEndpoints), so the
// app shows one card per thing that happened rather than one per row in a database.
//
// `kind` is what a view should branch on. "analyzed" carries a summary, event type, and notify
// decision; "motion" carries none of those and never will, because nothing looked at the frame -
// either the camera doesn't forward to AI analysis, or the forward was refused. Both kinds can
// carry a clip, since that comes from the trigger side.
//
// `id` is a String, not a UUID: a motion row's id is prefixed ("trigger:<guid>") so the two
// sources can't collide in one list. Use triggerId/analysisId to get at the underlying ids.
struct ActivityItem: Codable, Identifiable, Hashable {
    let id: String
    let kind: String
    let camera: String
    let occurredAt: Date
    let imageUrl: String?
    let clipUrl: String?
    let status: String
    let eventType: String?
    let summary: String?
    let notified: Bool
    let notificationReason: String?
    let confidence: Double?
    let linkedCameras: String?
    let packageTheftReason: String?
    let incidentId: String?
    let triggerId: String?
    let zoneName: String?
    let confirmation: String?
    let cameraId: UUID?
    let triggerAreaFraction: Double?

    var isAnalyzed: Bool { kind == "analyzed" }
    var hasClip: Bool { clipUrl != nil }

    // Only an analyzed row has an alert behind it, and only then is `id` a bare alert guid.
    var analysisId: UUID? { isAnalyzed ? UUID(uuidString: id) : nil }

    // Rebuilds what AlertDetailView already knows how to show, so the merged list can open the
    // existing detail screen without a second round-trip for fields the list already holds.
    var asSecurityEvent: SecurityEvent? {
        guard let analysisId else { return nil }
        return SecurityEvent(
            id: analysisId,
            camera: camera,
            occurredAt: occurredAt,
            status: status,
            eventType: eventType ?? "unknown",
            summary: summary ?? "",
            notified: notified,
            notificationReason: notificationReason ?? "",
            confidence: confidence ?? 0,
            hasImage: imageUrl != nil
        )
    }
}

struct Camera: Codable, Identifiable, Hashable {
    let id: UUID
    let name: String
    let streamUrl: String?
    let includeAudio: Bool?
    let ptzEnabled: Bool?
    // Carried along (not shown in any UI) purely so editing an existing camera without
    // re-running Test Connection doesn't wipe these on save - the PUT endpoint overwrites the
    // whole record, it doesn't merge, so these have to round-trip through every edit.
    let onvifPtzServiceUrl: String?
    let onvifProfileToken: String?
}

struct CameraInput: Encodable {
    var name: String
    var streamUrl: String
    var username: String?
    var password: String?
    var includeAudio: Bool
    // onvifPtzServiceUrl/onvifProfileToken come from a discovery probe (see CameraProbeResult)
    // and are just passed through here on save, the same way probeResult.streamUrl already
    // flows into streamUrl - the client never makes these up itself.
    var ptzEnabled: Bool
    var onvifPtzServiceUrl: String?
    var onvifProfileToken: String?
}

// The Live grid's tile order for the signed-in account, as arranged on a phone or in the
// dashboard. Decodable only - this app reads the arrangement, it never sets one.
struct LiveOrder: Decodable {
    var cameraIds: [UUID]
}

struct Point2D: Codable, Hashable {
    var x: Double
    var y: Double
}

// Zones have no server-side id and their names aren't unique, so they're identified by position
// in the list. tvOS has no zone editor - this mirrors the iOS model so the two stay in step, and
// so a config fetched here round-trips its zones instead of dropping them.
struct TriggerZone: Codable, Equatable, Hashable {
    var name: String
    var polygon: [Point2D]
    var minTriggerArea: Double
}

// Mirrors the server's TriggerConfigDto - used for both the GET response and the PUT body.
//
// Two rules here are load-bearing; see core/docs/MULTI_ZONE_CLIENT_HANDOFF.md.
//
// 1. zonePolygon/minTriggerArea must keep being sent whatever else changes. They mirror zones[0],
//    and they're declared non-optional here, so a server that stopped sending them would break
//    decoding on every installed copy of this app.
// 2. `zones` nil and `zones` empty mean different things to the server. Nil omits the key
//    entirely (Swift's synthesized encoder uses encodeIfPresent), which the server reads as "this
//    client doesn't know about zones - update the primary and leave the rest alone". An empty
//    array reads as "the editor really did delete them". Never send [] as a placeholder.
struct TriggerConfig: Codable, Equatable {
    var enabled: Bool
    var zonePolygon: [Point2D]
    var minTriggerArea: Double
    // Absent on a server older than multi-zone; read it through `resolvedZones`, never directly.
    var zones: [TriggerZone]?
    var cooldownSeconds: Int
    var maxTriggersPerMinute: Int
    var shadowSensitivity: Int
    var recordClips: Bool
    var clipPreSeconds: Int
    var clipPostSeconds: Int
    var sendToAlerts: Bool
    var alertProfile: String
    // Not shown in any UI. Carried through every save purely so writing this config back doesn't
    // unbind a Device Hub sensor that was set up on the web dashboard - the PUT overwrites the
    // whole record rather than merging, so anything omitted reverts to its default.
    var sensorDeviceId: String = ""
    var sensorTriggerOn: String = "on"
    var sensorCooldownSeconds: Int = 30

    // Always at least one zone, mirroring the server's TriggerConfig.ResolvedZones: a config
    // written before multi-zone reads as a single zone built from the legacy pair.
    var resolvedZones: [TriggerZone] {
        if let zones, !zones.isEmpty { return zones }
        return [TriggerZone(name: TriggerZone.defaultName, polygon: zonePolygon, minTriggerArea: minTriggerArea)]
    }
}

extension TriggerZone {
    // Matches the server's TriggerConfig.DefaultZoneName.
    static let defaultName = "Zone 1"
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
    // Only ever populated when an ONVIF PTZ service was actually found - absent (nil/false) just
    // means this camera doesn't have one, which is the normal case, not a failure.
    var supportsPtz: Bool?
    var onvifPtzServiceUrl: String?
    var onvifProfileToken: String?
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
    // Lets Core pick the matching APNs apns-topic (this app's own bundle id, not the iPhone
    // app's) - see core/SentinelCore/Options.cs ApplePushOptions.ResolveApnsTopic.
    let appVariant: String?
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

struct PtzMoveBody: Encodable {
    let pan: Double
    let tilt: Double
    let zoom: Double
}

// Telemetry for the live ladder - see SentinelClient.reportLivePath. Tier is "mse" or "hls" from
// this app (the dashboard also reports "webrtc"); outcome is "playing", "failed" or "degraded".
struct LivePathReportBody: Encodable {
    let tier: String
    let outcome: String
    let reason: String?
    let elapsedMs: Int
}

// MARK: - Device Hub

// Apple TV is control-only for the Device Hub: devices are added and configured on the web
// dashboard, iPhone, Mac, or Android, where entering an MQTT topic isn't a fight with an on-screen
// keyboard. So there's no candidate/input type here, unlike the other clients - just what's needed
// to show a device and switch it.
// One numeric reading a device published, keyed by whatever the payload called it. There are
// deliberately no units and no friendly name: the server hands over the raw number under the
// device's own key, because no two vendors agree on units well enough for guessing to beat
// saying nothing.
struct HubMeasurement: Codable, Identifiable, Hashable {
    var key: String
    var value: Double

    var id: String { key }

    // Round to two decimals, then drop the trailing zeros - 21.500000000000004 is float noise
    // picked up from scaling upstream, not precision the sensor reported. Matches the dashboard's
    // formatMeasurement so the same reading reads identically on every client.
    var formattedValue: String {
        guard value.isFinite else { return "-" }
        var text = String(format: "%.2f", value)
        if text.contains(".") {
            while text.hasSuffix("0") { text.removeLast() }
            if text.hasSuffix(".") { text.removeLast() }
        }
        return text == "-0" ? "0" : text
    }
}

struct HubDevice: Codable, Identifiable, Hashable {
    let id: UUID
    var name: String
    var kind: String
    var room: String
    var enabled: Bool
    var supportsBrightness: Bool
    var power: String
    var brightness: Int?
    var available: Bool
    var commandable: Bool
    // Optional on the wire so this app still decodes against a server older than the numeric
    // sensor update - a synthesized Codable throws on a missing key it declares non-optional,
    // which would stop the whole Device Hub decoding rather than just this one field. Sorted by
    // key server-side; render in the order given so the rows don't reshuffle between polls.
    var measurements: [HubMeasurement]?

    var isOn: Bool { power == "on" }

    // A device with no on/off that is publishing numbers is working, not in an unknown state.
    var isReporting: Bool { !commandable && power == "unknown" && !(measurements ?? []).isEmpty }

    var systemImage: String {
        switch kind {
        case "light": "lightbulb.fill"
        case "switch", "outlet": "poweroutlet.type.b.fill"
        case "fan": "fan.fill"
        case "cover": "blinds.horizontal.closed"
        case "lock": "lock.fill"
        default: "sensor.fill"
        }
    }
}

struct HubDeviceState: Codable, Identifiable, Hashable {
    let id: UUID
    var power: String
    var brightness: Int?
    var available: Bool
    // Optional for the same reason as HubDevice.measurements - see the note there.
    var measurements: [HubMeasurement]?
}

struct HubDeviceStateSnapshot: Codable {
    var connected: Bool
    var devices: [HubDeviceState]
}

struct DeviceCommandResult: Codable {
    var sent: Bool
    var acknowledged: Bool
    var power: String
    var brightness: Int?
}

struct DeviceHubEnabled: Codable {
    let enabled: Bool
}

// Whether the server has a valid licence file, which is what decides the UNLICENSED watermark
// over camera images and live video - see UnlicensedWatermark.
struct LicenseState: Codable {
    let valid: Bool
    var customerName: String?
}

struct DeviceCommandInput: Encodable {
    var action: String?
    var brightness: Int?
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
