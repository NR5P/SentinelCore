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

    // All four optional, because a server older than they are sends none of them and this list is
    // the app's main screen - a decode that fails here is a blank Events tab, not a missing badge.
    //
    // aiAnalysisOff is the one worth carrying. The server works it out per row precisely because a
    // camera that has quietly dropped out of AI analysis looks exactly like a camera that has
    // stopped seeing anything, and the dashboard has badged it since it was added while the phone,
    // Mac and TV showed nothing at all.
    let aiAnalysisOff: Bool?
    let clipState: String?
    let clipPreSeconds: Int?
    let clipPostSeconds: Int?

    // Where the row came from - "alert" for something the cameras noticed, "manual" for the record
    // button, "scheduled" for a timed recording window (ActivityOrigins server-side). Optional for a
    // server older than the field, which only ever sent alerts.
    let origin: String?
    // How long a recording row's video runs. Absent on every other kind of row, and on a recording
    // still being written.
    let durationSeconds: Int?

    var isAnalyzed: Bool { kind == "analyzed" }
    var hasClip: Bool { clipUrl != nil }

    // Footage somebody asked for - the record button or a timed window - rather than something a
    // camera noticed. The summary is the server's own line about it and stands where an AI
    // description would.
    var isRecording: Bool { kind == "recording" }

    // The badge every card wears. "Alert" is the default so a server that predates the field, which
    // only ever sent alerts, reads as what it always was.
    var originLabel: String {
        switch origin {
        case "manual": "Manual"
        case "scheduled": "Timer"
        default: "Alert"
        }
    }

    var originDescription: String {
        switch origin {
        case "manual": "Record button"
        case "scheduled": "Timed recording"
        default: "The cameras noticed something"
        }
    }

    var durationLabel: String? {
        guard let seconds = durationSeconds else { return nil }
        if seconds < 60 { return "\(seconds)s" }
        if seconds < 3600 { return "\(seconds / 60)m \(String(format: "%02d", seconds % 60))s" }
        return "\(seconds / 3600)h \(String(format: "%02d", seconds % 3600 / 60))m"
    }

    // Nothing looked at this frame and nothing will. Defaults to false so a server that doesn't
    // send the field reads as "analysis is on", which is what every install did before it existed.
    var isAiAnalysisOff: Bool { aiAnalysisOff ?? false }

    // The states the server sends; see ClipStates in ActivityEndpoints. `none` and an absent value
    // both mean there is nothing to say about a clip on this row.
    enum ClipState: String {
        case ready, recording, missing, off
    }

    var clip: ClipState? {
        guard let clipState else { return nil }
        return ClipState(rawValue: clipState)
    }

    // Only an analyzed row has an alert behind it, and only then is `id` a bare alert guid.
    var analysisId: UUID? { isAnalyzed ? UUID(uuidString: id) : nil }

    // Rebuilds what EventDetailView already knows how to show, so the merged list can open the
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
    // For exactly the same reason, and they were missing: with no username or password on this
    // type, every save from this app sent none, and the PUT read that as "this camera has no
    // login" - so editing a camera's name quietly broke its stream until the credentials were
    // typed in again. `password` arrives masked (SettingsSecrets.Mask); sending the mask back
    // unchanged is what tells the server to keep the one it has, and the real value is fetched
    // only when someone presses Show. Optional so a response from an older server still decodes.
    let username: String?
    let password: String?
    // This camera's video is pushed to the server by a device - a browser on the publish page, or
    // this app streaming the phone/Mac it runs on (see DevicePublisher) - rather than pulled from
    // an address. Optional so a response from an older server still decodes; absent reads as an
    // ordinary camera.
    let isBrowserPublished: Bool?

    var isDevicePublished: Bool { isBrowserPublished ?? false }
}

// What the server knows about whether a device is streaming into a published camera right now,
// from /api/cameras/{id}/publish/status. `publishing` is the honest answer - go2rtc is actually
// receiving frames - as opposed to a peer connection that merely reports "connected".
struct PublishStatus: Decodable {
    let publishing: Bool
    let lastSeenAt: Date?
    let missingSince: Date?
}

// SettingsSecrets.Mask on the server. Any stored secret is replaced by this on its way out, and
// sending it back unchanged means "keep the one you have" - the contract shared by camera
// passwords and every secret on the settings screen.
enum SentinelSecrets {
    static let mask = "\u{2022}\u{2022}\u{2022}\u{2022}\u{2022}\u{2022}\u{2022}\u{2022}"
}

struct CameraPasswordResponse: Decodable {
    let password: String
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
    // Carried through every save of a device-published camera: the PUT overwrites the whole record,
    // and without this an edit from here turned it back into an ordinary camera with no URL, which
    // the server rejects. Set on create by DeviceCameraView when this device becomes a camera.
    var isBrowserPublished: Bool = false
}

// The Live grid's tile order for the signed-in account - the same shape in both directions, so
// one type covers the GET and the PUT.
struct LiveOrder: Codable {
    var cameraIds: [UUID]
}

struct KnownFace: Codable, Identifiable, Hashable {
    let id: String
    let label: String
    let enrollments: [KnownFaceEnrollment]
}

struct KnownFaceEnrollment: Codable, Identifiable, Hashable {
    let id: String
    let photoUrl: String
}

struct Point2D: Codable, Hashable {
    var x: Double
    var y: Double
}

// One trigger zone: its own polygon and its own minimum object size, because perspective means
// the same subject covers wildly different pixel counts near the camera and far from it.
// Zones have no server-side id and their names aren't unique, so the editor identifies them by
// position in the list - same as the web build.
struct TriggerZone: Codable, Equatable, Hashable {
    var name: String
    var polygon: [Point2D]
    var minTriggerArea: Double

    // The whole camera view, as an ordinary four-corner polygon. It is spelled out rather than
    // signalled by an empty polygon because empty already means the opposite to the server: a zone
    // with fewer than three points rasterizes to a mask nothing can be inside, so it never fires.
    // The corners really do cover everything - the detector tests pixel centres at (i + 0.5) / size,
    // which puts every cell of its grid strictly inside the unit square.
    static let wholeFramePolygon = [
        Point2D(x: 0, y: 0), Point2D(x: 1, y: 0), Point2D(x: 1, y: 1), Point2D(x: 0, y: 1),
    ]
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
    // Not shown in any UI. Carried through every save purely so editing a zone from the phone
    // doesn't unbind a Device Hub sensor that was set up on the web dashboard - the PUT
    // overwrites the whole record rather than merging, so anything omitted reverts to its
    // default (no sensor, "on", 30s).
    var sensorDeviceId: String = ""
    var sensorTriggerOn: String = "on"
    var sensorCooldownSeconds: Int = 30

    // The overnight trigger pause - see core/docs/NIGHT_WINDOW_HANDOFF.md. Optional for the same
    // reason `zones` is, and it's the same trap: nil omits the key (encodeIfPresent), which the
    // server reads as "this client doesn't know about the night window" and merges against the
    // stored row - it does NOT mean "turn it off". A build that sent a plain `false` on every save
    // would switch someone's window off whenever they nudged a polygon from their phone, and
    // nothing anywhere would say so; the flood would just come back the next night.
    //
    // The editor knows about the feature, so it loads all three and sends all three back. nil is
    // what a server older than the night window looks like; read it through `resolvedNightWindow`
    // below, which supplies the same defaults the server would.
    var nightWindowEnabled: Bool?
    var nightStartHour: Int?
    var nightEndHour: Int?

    // The two per-camera AI routing flags - see core/docs/AI_ROUTING_HANDOFF.md. Optional for
    // exactly the reason the night window above is: nil omits the key, which the server merges
    // against the stored row, and a plain `false` on every save would quietly switch the coop
    // camera back to the detector the first time someone nudged a polygon from their phone.
    //
    // `bypassFastLocal` sends every trigger straight to the full model instead of letting the
    // fast local detector decide what is worth escalating - the detector is COCO-trained and has
    // no class for a raccoon, fox, opossum or weasel, so at a chicken coop it clears those frames
    // as empty. `keepOnlyAlertable` judges a trigger by whether the AI would have alerted on the
    // frame rather than by whether anything was in it, so the coop's own chickens stop confirming
    // every trigger. Both off by default and both read through the resolved helpers below.
    var bypassFastLocal: Bool?
    var keepOnlyAlertable: Bool?

    // Always at least one zone, mirroring the server's TriggerConfig.ResolvedZones: a config
    // written before multi-zone reads as a single zone built from the legacy pair, so the editor
    // never has to special-case the migration.
    var resolvedZones: [TriggerZone] {
        if let zones, !zones.isEmpty { return zones }
        return [TriggerZone(name: TriggerZone.defaultName, polygon: zonePolygon, minTriggerArea: minTriggerArea)]
    }

    // Same shape as resolvedZones, for the same reason: a server older than the night window sends
    // none of the three keys, and the editor needs values to show either way. The fallbacks match
    // the server's own defaults (TriggerConfig in TriggerStore.cs) - off, and 21:00-07:00 rather
    // than 0/0, so switching the feature on offers the window someone would actually have asked
    // for instead of a zero-width one that appears to do nothing.
    var resolvedNightWindow: (enabled: Bool, startHour: Int, endHour: Int) {
        (nightWindowEnabled ?? false, nightStartHour ?? 21, nightEndHour ?? 7)
    }

    // Same fallbacks the server applies to a row written before these two columns existed, and
    // the same reason as resolvedNightWindow: a server older than the feature sends neither key,
    // and the editor still has to show a switch. Off is the server's default for both.
    var resolvedBypassFastLocal: Bool { bypassFastLocal ?? false }

    var resolvedKeepOnlyAlertable: Bool { keepOnlyAlertable ?? false }
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

// The server answers a bad request two different ways: its own {error}/{errors} shape from the
// validation paths, and ASP.NET's RFC 7807 ProblemDetails from Results.Problem, whose message
// lives in `detail`. Only the first two were read, so every Results.Problem in the app surfaced as
// a bare "Server request failed (HTTP 502)" with the sentence explaining what to do about it
// thrown away.
struct APIError: Codable {
    let error: String?
    let errors: [String]?
    let detail: String?
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

struct SystemStatus: Codable {
    let cpuUsagePercent: Double
    let processorCount: Int
    let memoryUsedBytes: Int64
    let memoryTotalBytes: Int64
    let diskFreeBytes: Int64
    let diskTotalBytes: Int64
    let queuedAlerts: Int
    let uptimeSeconds: Double
    let visionBackendHealthy: Bool

    var memoryUsedFraction: Double {
        memoryTotalBytes > 0 ? Double(memoryUsedBytes) / Double(memoryTotalBytes) : 0
    }

    var diskUsedBytes: Int64 { diskTotalBytes - diskFreeBytes }

    var diskUsedFraction: Double {
        diskTotalBytes > 0 ? Double(diskUsedBytes) / Double(diskTotalBytes) : 0
    }
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

// A device in the Device Hub, as returned by /api/devices. Power/brightness/available are the
// live MQTT state the server is tracking, folded into the same object as the configuration so
// the list renders from one call; /api/devices/state re-fetches just those while the tab is open.
struct HubDevice: Codable, Identifiable, Hashable {
    let id: UUID
    var name: String
    var kind: String
    var room: String
    var enabled: Bool
    var commandTopic: String
    var stateTopic: String
    var availabilityTopic: String
    var payloadOn: String
    var payloadOff: String
    // Optional on the wire so this app still decodes a server older than the gate/cover update.
    var payloadStop: String?
    var momentary: Bool?
    var payloadAvailable: String
    var payloadNotAvailable: String
    var schema: String
    var stateValueTemplate: String
    var optimistic: Bool
    var supportsBrightness: Bool
    var brightnessCommandTopic: String
    var brightnessStateTopic: String
    var brightnessScale: Int
    var discoverySource: String
    var sortOrder: Int?
    // "on", "off", or "unknown" - a string rather than a Bool because "we have never heard from
    // this device" is a genuinely different state from "off", and the UI says so.
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

    // A pulse device: one press, no on/off state afterwards. The server already folds the
    // "button" kind into this flag, but the kind check keeps older servers honest too.
    var isMomentary: Bool { (momentary ?? false) || kind == "button" }

    // Kinds that speak open/close rather than on/off - their controls and status wording differ.
    var isCoverLike: Bool { kind == "cover" || kind == "gate" || kind == "garage" }

    var isLock: Bool { kind == "lock" }

    // Whether this device has a third "stop" command its motor understands.
    var hasStop: Bool { !(payloadStop ?? "").isEmpty }

    // SF Symbol per device kind, matching the icons the rest of the app uses for its sections.
    var systemImage: String {
        switch kind {
        case "light": "lightbulb.fill"
        case "switch", "outlet": "poweroutlet.type.b.fill"
        case "fan": "fan.fill"
        case "cover": "blinds.horizontal.closed"
        case "gate": "door.sliding.left.hand.closed"
        case "garage": "door.garage.closed"
        case "lock": "lock.fill"
        case "button": "button.programmable"
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

// What a discovery scan thinks is out there. Not a device until the user adds it - the fields
// line up with HubDeviceInput so accepting one is a straight copy.
struct DeviceCandidate: Codable, Identifiable, Hashable {
    var id: String { commandTopic.isEmpty ? stateTopic : commandTopic }
    var suggestedName: String
    var kind: String
    var commandTopic: String
    var stateTopic: String
    var availabilityTopic: String
    var payloadOn: String
    var payloadOff: String
    // Optional so scans against a server older than the gate/cover update still decode.
    var payloadStop: String?
    var momentary: Bool?
    var schema: String
    var stateValueTemplate: String
    var supportsBrightness: Bool
    var brightnessCommandTopic: String
    var brightnessStateTopic: String
    var brightnessScale: Int
    var source: String
    var alreadyAdded: Bool
}

struct DeviceScanStartResult: Codable {
    var scanId: UUID
    var scanSeconds: Int
}

struct DeviceScanSnapshot: Codable {
    var status: String // "running" | "completed"
    var results: [DeviceCandidate]
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

// What a restore actually put back. Every count is optional because a backup taken before a given
// feature existed simply has no such section, and reporting "0 camera groups" for one of those
// would read like something went missing rather than like nothing was there.
struct BackupRestoreResult: Decodable {
    var camerasRestored: Int?
    var cameraGroupsRestored: Int?
    var triggerZonesRestored: Int?
    var gateChecksRestored: Int?
    var accountsRestored: Int?
    var liveOrderRestored: Int?
    var recordingSchedulesRestored: Int?
    var devicesRestored: Int?
    var automationRulesRestored: Int?
    var schedulesRestored: Int?
    var organizerItemsRestored: Int?
    var attachmentsRestored: Int?
    var knownFacePhotosRestored: Int?
    var organizerAutomationsRestored: Int?
    var alarmSensorsRestored: Int?
    var familyDrivesRestored: Int?
    var mediaLibrariesRestored: Int?
    var mediaProgressRestored: Int?
    var musicRowsRestored: Int?
    var podcastItemsRestored: Int?
    var wakeAlarmsRestored: Int?
    var listenBrainzRestored: Int?
    // Things to act on that the counts can't say - "restart the dashboard", "the main app wasn't
    // running so devices weren't restored".
    var notes: [String]?

    // The non-empty parts, phrased for a sentence: "3 cameras, 1 trigger zone".
    var summary: String {
        let parts: [(Int?, String)] = [
            (camerasRestored, "camera"),
            (cameraGroupsRestored, "camera group"),
            (triggerZonesRestored, "trigger zone"),
            (recordingSchedulesRestored, "recording schedule"),
            (gateChecksRestored, "gate check"),
            (accountsRestored, "account"),
            (liveOrderRestored, "Live camera arrangement"),
            (devicesRestored, "device"),
            (automationRulesRestored, "automation rule"),
            (schedulesRestored, "schedule"),
            (organizerItemsRestored, "Organizer item"),
            (attachmentsRestored, "attachment"),
            (knownFacePhotosRestored, "face photo"),
            (organizerAutomationsRestored, "board automation"),
            (alarmSensorsRestored, "alarm sensor"),
            (familyDrivesRestored, "Files backup drive"),
            (mediaLibrariesRestored, "Movies & TV folder"),
            (mediaProgressRestored, "watch position"),
            (musicRowsRestored, "music item"),
            (podcastItemsRestored, "podcast item"),
            (wakeAlarmsRestored, "wake-up alarm"),
            (listenBrainzRestored, "ListenBrainz connection")
        ]
        let described = parts.compactMap { count, noun -> String? in
            guard let count, count > 0 else { return nil }
            return "\(count) \(noun)\(count == 1 ? "" : "s")"
        }
        return described.isEmpty ? "settings" : described.joined(separator: ", ")
    }

    // The whole outcome as one message: what came back, then anything still to do.
    var message: String {
        (["Restored \(summary)."] + (notes ?? [])).joined(separator: " ")
    }
}

// What the HomeKit sidecar reports about itself. "running" and "published" are deliberately
// separate: the sidecar runs for everyone, but only advertises on the network once HomeKit is
// switched on, and the screen needs both to say anything useful.
struct HomeKitStatus: Decodable {
    let running: Bool
    var enabled: Bool?
    var published: Bool?
    var paired: Bool?
    var accessories: [String]?
    var pairingCode: String?
    var error: String?
}

// The editable HomeKit settings. Hand-written rather than schema-driven because the useful
// half of this section is the pairing code, which no schema can describe - see the note on
// HomeKitSettingsDto in SettingsSchemaTests. Every field the server's DTO has must be here: the
// save merges the whole DTO, so a field left out is written back as false.
struct HomeKitSettings: Codable {
    var enabled: Bool
    var includeDevices: Bool
    var includeVideo: Bool
    var sidecarBaseUrl: String
    var timeoutSeconds: Int
    var minimumConfidence: Double
}

// A number plate the AI should treat as household rather than a stranger's. Encodable as well as
// Decodable because the save endpoint replaces the whole list - see saveKnownVehicles.
struct KnownVehicle: Codable, Identifiable, Hashable {
    var plate: String
    var label: String

    // The plate is the identity here - it's what the matcher compares against, and two rows with
    // the same one would be the same vehicle twice.
    var id: String { plate }
}

struct DeviceHubMetadata: Codable {
    var kinds: [String]
    var schemas: [String]
    var discoverySeconds: Int
}

struct HubDeviceInput: Encodable {
    var name: String
    var kind: String
    var room: String
    var enabled: Bool
    var commandTopic: String
    var stateTopic: String
    var availabilityTopic: String
    var payloadOn: String
    var payloadOff: String
    var payloadStop: String
    var momentary: Bool
    var payloadAvailable: String
    var payloadNotAvailable: String
    var schema: String
    var stateValueTemplate: String
    var optimistic: Bool
    var supportsBrightness: Bool
    var brightnessCommandTopic: String
    var brightnessStateTopic: String
    var brightnessScale: Int
    var discoverySource: String

    init(device: HubDevice) {
        name = device.name
        kind = device.kind
        room = device.room
        enabled = device.enabled
        commandTopic = device.commandTopic
        stateTopic = device.stateTopic
        availabilityTopic = device.availabilityTopic
        payloadOn = device.payloadOn
        payloadOff = device.payloadOff
        payloadStop = device.payloadStop ?? ""
        momentary = device.momentary ?? false
        payloadAvailable = device.payloadAvailable
        payloadNotAvailable = device.payloadNotAvailable
        schema = device.schema
        stateValueTemplate = device.stateValueTemplate
        optimistic = device.optimistic
        supportsBrightness = device.supportsBrightness
        brightnessCommandTopic = device.brightnessCommandTopic
        brightnessStateTopic = device.brightnessStateTopic
        brightnessScale = device.brightnessScale
        discoverySource = device.discoverySource
    }

    init(candidate: DeviceCandidate) {
        name = candidate.suggestedName
        kind = candidate.kind
        room = ""
        enabled = true
        commandTopic = candidate.commandTopic
        stateTopic = candidate.stateTopic
        availabilityTopic = candidate.availabilityTopic
        payloadOn = candidate.payloadOn
        payloadOff = candidate.payloadOff
        payloadStop = candidate.payloadStop ?? ""
        momentary = candidate.momentary ?? false
        payloadAvailable = "online"
        payloadNotAvailable = "offline"
        schema = candidate.schema
        stateValueTemplate = candidate.stateValueTemplate
        optimistic = false
        supportsBrightness = candidate.supportsBrightness
        brightnessCommandTopic = candidate.brightnessCommandTopic
        brightnessStateTopic = candidate.brightnessStateTopic
        brightnessScale = candidate.brightnessScale
        discoverySource = candidate.source
    }

    // A blank device for the add form. Defaults match the most common case by a wide margin:
    // a raw ON/OFF light, which is what Tasmota and ESPHome produce.
    init() {
        name = ""
        kind = "light"
        room = ""
        enabled = true
        commandTopic = ""
        stateTopic = ""
        availabilityTopic = ""
        payloadOn = "ON"
        payloadOff = "OFF"
        payloadStop = ""
        momentary = false
        payloadAvailable = "online"
        payloadNotAvailable = "offline"
        schema = "raw"
        stateValueTemplate = ""
        optimistic = false
        supportsBrightness = false
        brightnessCommandTopic = ""
        brightnessStateTopic = ""
        brightnessScale = 255
        discoverySource = "manual"
    }
}

struct DeviceCommandInput: Encodable {
    var action: String?
    var brightness: Int?
}

// PUT /api/devices/order - the complete arrangement, first device first. Same shape as
// LiveOrder is for camera tiles.
struct DeviceOrderInput: Encodable {
    var deviceIds: [UUID]
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

// MARK: - Schedules

// A "do this at that time" rule. The clock-driven counterpart to the automation rules on the
// dashboard, sharing their action vocabulary plus a Device Hub option. See ScheduleModel.cs.
struct ScheduleAction: Codable, Hashable {
    var type: String
    var deviceId: UUID?
    var deviceAction: String
    var deviceBrightness: Int?
    var mqttTopic: String
    var mqttPayload: String
    var mqttRetain: Bool
    var webhookUrl: String
    var webhookMethod: String
    var webhookHeaders: String
    var webhookBody: String
    var webhookContentType: String
}

struct Schedule: Codable, Identifiable, Hashable {
    let id: UUID
    var name: String
    var enabled: Bool
    var kind: String
    var timeOfDay: String
    var daysOfWeek: [Int]
    var intervalSeconds: Int
    var windowStart: String
    var windowEnd: String
    var cronExpression: String
    var runAtUtc: Date?
    var solarEvent: String
    var solarOffsetMinutes: Int
    // How far below the horizon the sun must be for a "dusk"/"dawn" firing - a darkness level
    // rather than a time, which is what makes it track the seasons on its own.
    var solarElevationDegrees: Double
    var action: ScheduleAction
    var catchUpPolicy: String
    var lastRunUtc: Date?
    var lastStatus: String
    var lastDetail: String
    var nextDueUtc: Date?
    var runCount: Int

    var followsTheSun: Bool { kind == "solar" }
}

struct ScheduleRun: Codable, Identifiable, Hashable {
    let id: UUID
    var scheduleId: UUID
    var dueAtUtc: Date
    var firedAtUtc: Date
    var status: String
    var detail: String
    var durationMs: Int
}

// One of the named darkness levels the server offers, so this app doesn't have to know that
// "civil twilight" means -6 degrees.
struct DarknessLevel: Codable, Identifiable, Hashable {
    var id: String { key }
    var key: String
    var label: String
    var description: String
    var elevationDegrees: Double
}

// A commandable Device Hub device, in the trimmed-down shape the schedules metadata returns.
struct ScheduleDeviceOption: Codable, Identifiable, Hashable {
    let id: UUID
    var name: String
    var kind: String
    var room: String
    var enabled: Bool
    var supportsBrightness: Bool
    var momentary: Bool
    var coverLike: Bool
    var hasStop: Bool
}

struct ScheduleMetadata: Codable {
    var kinds: [String]
    var solarEvents: [String]
    var darknessLevels: [DarknessLevel]
    var actionTypes: [String]
    var catchUpPolicies: [String]
    var deviceActions: [String]
    var tokens: [String]
    var minimumIntervalSeconds: Int
    // Whether a latitude and longitude have been set. A sun-following schedule can't be created
    // without them, and saying so up front beats a save that fails.
    var locationSet: Bool
    var timeZone: String
    var devices: [ScheduleDeviceOption]

    static let empty = ScheduleMetadata(
        kinds: [], solarEvents: [], darknessLevels: [], actionTypes: [], catchUpPolicies: [],
        deviceActions: [], tokens: [], minimumIntervalSeconds: 10, locationSet: false,
        timeZone: "", devices: [])
}

struct SchedulePreviewOccurrence: Codable, Hashable {
    var utc: Date
    var local: String
}

struct SchedulePreview: Codable {
    var occurrences: [SchedulePreviewOccurrence]
    var timeZone: String
    var explanation: String
}

struct ScheduleInput: Encodable {
    var name: String
    var enabled: Bool
    var kind: String
    var timeOfDay: String
    var daysOfWeek: [Int]
    var intervalSeconds: Int
    var windowStart: String
    var windowEnd: String
    var cronExpression: String
    // ISO-8601 rather than a Date: the client's shared JSONEncoder has no date strategy set, so
    // a Date here would go out as a bare number the server can't read. Every other input type in
    // this app happens to have no dates, which is why it has never come up.
    var runAtUtc: String?
    var solarEvent: String
    var solarOffsetMinutes: Int
    var solarElevationDegrees: Double
    var actionType: String
    var deviceId: UUID?
    var deviceAction: String
    var deviceBrightness: Int?
    var mqttTopic: String
    var mqttPayload: String
    var mqttRetain: Bool
    var webhookUrl: String
    var webhookMethod: String
    var webhookHeaders: String
    var webhookBody: String
    var webhookContentType: String
    var catchUpPolicy: String
}

struct ScheduleEnabledInput: Encodable {
    var enabled: Bool
}

struct ScheduleRunResult: Codable {
    var fired: Bool
    var detail: String?
    var error: String?
}

// MARK: - Alarm

// The home alarm as /api/alarm/status reports it. Everything the tab shows comes from this one
// call; the apps poll it while the section is on screen and count the delays down from the
// deadlines it carries. Optional fields decode a server that has the alarm switched off, which
// answers with just `enabled` and `hubEnabled`.
struct AlarmStatus: Codable {
    var enabled: Bool
    var hubEnabled: Bool?
    var canEdit: Bool?
    var mode: String?
    var status: String?
    var changedAt: Date?
    var changedBy: String?
    var exitUntil: Date?
    var pendingUntil: Date?
    var triggeredAt: Date?
    var triggeredBy: String?
    var sirenOn: Bool?
    var pinSet: Bool?
    var brokerConnected: Bool?
    var ready: Bool?
    var issues: [String]?
    var sensors: [AlarmSensor]?
    var config: AlarmConfig?
    var now: Date?
    var modes: [String]?
    var zones: [String]?

    var isArmed: Bool { (status ?? "disarmed") != "disarmed" }
    var isTriggered: Bool { status == "triggered" }
}

struct AlarmSensor: Codable, Identifiable, Hashable {
    let id: UUID
    var deviceId: UUID
    var name: String
    var zone: String
    var armedHome: Bool
    var armedNight: Bool
    var armedAway: Bool
    var inverted: Bool
    var enabled: Bool
    var sortOrder: Int
    var deviceName: String?
    var room: String?
    var deviceMissing: Bool
    // open | closed | unknown
    var state: String
    var available: Bool
    var offline: Bool
    var battery: Double?
    var lowBattery: Bool
    var linkQuality: Double?
    var lastSeen: Date?
    var lastChanged: Date?
    var bypassed: Bool
    var watched: Bool

    var isOpen: Bool { state == "open" }

    // What "open" means for this kind of sensor, for the tile.
    var stateLabel: String {
        switch (zone, state) {
        case ("interior", "open"): "Motion"
        case ("interior", "closed"): "Clear"
        case ("always", "open"): "Alert"
        case ("always", "closed"): "OK"
        case (_, "open"): "Open"
        case (_, "closed"): "Closed"
        default: "No reading"
        }
    }

    var zoneLabel: String { AlarmZone.label(for: zone) }
}

enum AlarmZone {
    static let all = ["entry", "perimeter", "interior", "always"]

    static func label(for zone: String) -> String {
        switch zone {
        case "entry": "Entry door"
        case "perimeter": "Perimeter"
        case "interior": "Interior motion"
        case "always": "24-hour"
        default: zone.capitalized
        }
    }

    static func explanation(for zone: String) -> String {
        switch zone {
        case "entry": "The way in. Opening it while armed starts the entry delay so whoever came in can disarm."
        case "perimeter": "Windows and other doors. Instant when armed."
        case "interior": "Motion inside. Instant, and by default only watched in Away."
        case "always": "Watched around the clock, armed or not: water leak, smoke, tamper."
        default: ""
        }
    }
}

enum AlarmMode {
    static func label(for mode: String) -> String {
        switch mode {
        case "home": "Home"
        case "night": "Night"
        case "away": "Away"
        default: "Disarmed"
        }
    }
}

struct AlarmConfig: Codable, Hashable {
    var exitDelaySeconds: Int
    var entryDelaySeconds: Int
    var sirenSeconds: Int
    var sirenDeviceIds: [UUID]
    var cameraIds: [UUID]
    var notifyPush: Bool
    var notifyArmDisarm: Bool
    var lowBatteryPercent: Int
    var offlineAfterHours: Int
    var requirePinToArm: Bool
}

struct AlarmEvent: Codable, Identifiable, Hashable {
    let id: Int64
    var at: Date
    var kind: String
    var message: String
    var actor: String?
    var sensorId: UUID?
}

// What arm/disarm/panic/test answer. `ok` false comes with an HTTP error status, so on that path
// the client reads it out of the ClientError's message instead - see SentinelClient.armAlarm.
struct AlarmCommandReply: Codable {
    var ok: Bool
    var note: String?
    var error: String?
    var openSensors: [String]?
}

struct AlarmArmInput: Encodable {
    var mode: String
    var force: Bool
    var pin: String?
}

struct AlarmDisarmInput: Encodable {
    var pin: String?
}

struct AlarmSensorInput: Encodable {
    var deviceId: UUID
    var name: String
    var zone: String
    var armedHome: Bool
    var armedNight: Bool
    var armedAway: Bool
    var inverted: Bool
    var enabled: Bool

    init(deviceId: UUID = UUID(), name: String = "", zone: String = "perimeter",
         armedHome: Bool = true, armedNight: Bool = true, armedAway: Bool = true,
         inverted: Bool = false, enabled: Bool = true) {
        self.deviceId = deviceId
        self.name = name
        self.zone = zone
        self.armedHome = armedHome
        self.armedNight = armedNight
        self.armedAway = armedAway
        self.inverted = inverted
        self.enabled = enabled
    }

    init(_ sensor: AlarmSensor) {
        self.init(deviceId: sensor.deviceId, name: sensor.name, zone: sensor.zone,
                  armedHome: sensor.armedHome, armedNight: sensor.armedNight, armedAway: sensor.armedAway,
                  inverted: sensor.inverted, enabled: sensor.enabled)
    }
}

struct AlarmPinInput: Encodable {
    var pin: String
    var currentPin: String?
}
