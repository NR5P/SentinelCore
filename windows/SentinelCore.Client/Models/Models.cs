using System.Text.Json.Serialization;

namespace SentinelCore.Client.Models;

// The wire types the dashboard's JSON API speaks, mirroring ios/SentinelCore/Models/Models.swift
// field for field. Where a comment there explains why a field is optional or has to round-trip
// untouched, the same reasoning is repeated here rather than assumed - a Windows client that
// quietly drops a field on save breaks a camera exactly as thoroughly as an iPhone that does.

public sealed class SecurityEvent
{
    public Guid Id { get; init; }
    public string Camera { get; init; } = "";
    public DateTimeOffset OccurredAt { get; init; }
    public string Status { get; init; } = "";
    public string EventType { get; init; } = "";
    public string Summary { get; init; } = "";
    public bool Notified { get; init; }
    public string NotificationReason { get; init; } = "";
    public double Confidence { get; init; }
    public bool HasImage { get; init; }
}

// One row of the Events list: a single moment, whichever records back it. The server joins the
// motion trigger and the AI analysis of that same frame into this (see ActivityEndpoints), so the
// app shows one card per thing that happened rather than one per row in a database.
//
// Kind is what a view should branch on. "analyzed" carries a summary, event type, and notify
// decision; "motion" carries none of those and never will, because nothing looked at the frame -
// either the camera doesn't forward to AI analysis, or the forward was refused. Both kinds can
// carry a clip, since that comes from the trigger side.
//
// Id is a string, not a Guid: a motion row's id is prefixed ("trigger:<guid>") so the two sources
// can't collide in one list. Use TriggerId/AnalysisId to get at the underlying ids.
public sealed class ActivityItem
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Camera { get; init; } = "";
    public DateTimeOffset OccurredAt { get; init; }
    public string? ImageUrl { get; init; }
    public string? ClipUrl { get; init; }
    public string Status { get; init; } = "";
    public string? EventType { get; init; }
    public string? Summary { get; init; }
    public bool Notified { get; init; }
    public string? NotificationReason { get; init; }
    public double? Confidence { get; init; }
    public string? LinkedCameras { get; init; }
    public string? PackageTheftReason { get; init; }
    public string? IncidentId { get; init; }
    public string? TriggerId { get; init; }
    public string? ZoneName { get; init; }
    public string? Confirmation { get; init; }
    public Guid? CameraId { get; init; }
    public double? TriggerAreaFraction { get; init; }

    // All four optional, because a server older than they are sends none of them and this list is
    // the app's main screen - a decode that fails here is a blank Events tab, not a missing badge.
    //
    // AiAnalysisOff is the one worth carrying. The server works it out per row precisely because a
    // camera that has quietly dropped out of AI analysis looks exactly like a camera that has
    // stopped seeing anything.
    public bool? AiAnalysisOff { get; init; }
    public string? ClipState { get; init; }
    public int? ClipPreSeconds { get; init; }
    public int? ClipPostSeconds { get; init; }

    // Where the row came from - "alert" for something the cameras noticed, "manual" for the record
    // button, "scheduled" for a timed recording window. Null from a server older than the field,
    // which only ever sent alerts.
    public string? Origin { get; init; }

    // How long a recording row's video runs. Null on every other kind of row.
    public int? DurationSeconds { get; init; }

    [JsonIgnore] public bool IsAnalyzed => Kind == "analyzed";

    // Footage somebody asked for rather than something a camera noticed. Its summary is the
    // server's own line about it and stands where an AI description would.
    [JsonIgnore] public bool IsRecording => Kind == "recording";

    // The badge every row wears. "Alert" is the default so an older server reads as it always did.
    [JsonIgnore] public string OriginLabel => Origin switch
    {
        "manual" => "Manual",
        "scheduled" => "Timer",
        _ => "Alert",
    };

    [JsonIgnore] public string OriginDescription => Origin switch
    {
        "manual" => "Record button",
        "scheduled" => "Timed recording",
        _ => "The cameras noticed something",
    };

    [JsonIgnore] public string? DurationLabel => DurationSeconds switch
    {
        null => null,
        < 60 => $"{DurationSeconds}s",
        < 3600 => $"{DurationSeconds / 60}m {DurationSeconds % 60:00}s",
        _ => $"{DurationSeconds / 3600}h {DurationSeconds % 3600 / 60:00}m",
    };

    [JsonIgnore] public bool HasClip => !string.IsNullOrEmpty(ClipUrl);

    // Nothing looked at this frame and nothing will. Defaults to false so a server that doesn't
    // send the field reads as "analysis is on", which is what every install did before it existed.
    [JsonIgnore] public bool IsAiAnalysisOff => AiAnalysisOff ?? false;

    // The states the server sends; see ClipStates in ActivityEndpoints. Null and "none" both mean
    // there is nothing to say about a clip on this row.
    [JsonIgnore]
    public ActivityClipState? Clip => ClipState switch
    {
        "ready" => ActivityClipState.Ready,
        "recording" => ActivityClipState.Recording,
        "missing" => ActivityClipState.Missing,
        "off" => ActivityClipState.Off,
        _ => null,
    };

    // Only an analyzed row has an alert behind it, and only then is Id a bare alert guid.
    [JsonIgnore]
    public Guid? AnalysisId => IsAnalyzed && Guid.TryParse(Id, out var id) ? id : null;

    // Rebuilds what the event detail screen already knows how to show, so the merged list can open
    // the existing detail screen without a second round-trip for fields the list already holds.
    public SecurityEvent? AsSecurityEvent()
    {
        if (AnalysisId is not { } id) return null;
        return new SecurityEvent
        {
            Id = id,
            Camera = Camera,
            OccurredAt = OccurredAt,
            Status = Status,
            EventType = EventType ?? "unknown",
            Summary = Summary ?? "",
            Notified = Notified,
            NotificationReason = NotificationReason ?? "",
            Confidence = Confidence ?? 0,
            HasImage = ImageUrl is not null,
        };
    }
}

public enum ActivityClipState
{
    Ready,
    Recording,
    Missing,
    Off,
}

public sealed class Camera
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string? StreamUrl { get; init; }
    public bool? IncludeAudio { get; init; }
    public bool? PtzEnabled { get; init; }

    // Carried along (not shown in any UI) purely so editing an existing camera without re-running
    // Test Connection doesn't wipe these on save - the PUT endpoint overwrites the whole record,
    // it doesn't merge, so these have to round-trip through every edit.
    public string? OnvifPtzServiceUrl { get; init; }
    public string? OnvifProfileToken { get; init; }

    // For exactly the same reason. Password arrives masked (SettingsSecrets.Mask); sending the
    // mask back unchanged is what tells the server to keep the one it has, and the real value is
    // fetched only when someone presses Show.
    public string? Username { get; init; }
    public string? Password { get; init; }

    // This camera's video is pushed to the server by a device - a browser on the publish page, a
    // phone or Mac running the Sentinel Core app, or this PC (see DeviceCameraWindow) - rather
    // than pulled from an address. Absent on an older server, which reads as an ordinary camera.
    public bool? IsBrowserPublished { get; init; }

    public bool IsDevicePublished => IsBrowserPublished == true;
}

// SettingsSecrets.Mask on the server. Any stored secret is replaced by this on its way out, and
// sending it back unchanged means "keep the one you have" - the contract shared by camera
// passwords and every secret on the settings screen.
public static class SentinelSecrets
{
    public const string Mask = "••••••••";
}

public sealed class CameraPasswordResponse
{
    public string Password { get; init; } = "";
}

public sealed class CameraInput
{
    public string Name { get; set; } = "";
    public string StreamUrl { get; set; } = "";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool IncludeAudio { get; set; }

    // OnvifPtzServiceUrl/OnvifProfileToken come from a discovery probe (see CameraProbeResult) and
    // are just passed through here on save, the same way a probe's StreamUrl already flows into
    // StreamUrl - the client never makes these up itself.
    public bool PtzEnabled { get; set; }
    public string? OnvifPtzServiceUrl { get; set; }
    public string? OnvifProfileToken { get; set; }

    // Carried through every save of a device-published camera: the PUT overwrites the whole record,
    // and without this an edit from here turned it back into an ordinary camera with no URL, which
    // the server rejects. Set on create by DeviceCameraWindow when this PC becomes a camera.
    public bool IsBrowserPublished { get; set; }
}

// The Live grid's tile order for the signed-in account - the same shape in both directions, so
// one type covers the GET and the PUT.
public sealed class LiveOrder
{
    public List<Guid> CameraIds { get; set; } = new();
}

public sealed class KnownFace
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public List<KnownFaceEnrollment> Enrollments { get; init; } = new();
}

public sealed class KnownFaceEnrollment
{
    public string Id { get; init; } = "";
    public string PhotoUrl { get; init; } = "";
}

public sealed class Point2D
{
    public double X { get; set; }
    public double Y { get; set; }

    public Point2D()
    {
    }

    public Point2D(double x, double y)
    {
        X = x;
        Y = y;
    }
}

// One trigger zone: its own polygon and its own minimum object size, because perspective means
// the same subject covers wildly different pixel counts near the camera and far from it. Zones
// have no server-side id and their names aren't unique, so the editor identifies them by position
// in the list - same as the web build.
public sealed class TriggerZone
{
    // Matches the server's TriggerConfig.DefaultZoneName.
    public const string DefaultName = "Zone 1";

    public string Name { get; set; } = DefaultName;
    public List<Point2D> Polygon { get; set; } = new();
    public double MinTriggerArea { get; set; }

    // The whole camera view, as an ordinary four-corner polygon - what the editor's "Cover
    // everything" button fills a zone with. It is spelled out rather than signalled by an empty
    // polygon because empty already means the opposite to the server: a zone with fewer than three
    // points rasterizes to a mask nothing can be inside, so it never fires. The corners really do
    // cover everything - the detector tests pixel centres at (i + 0.5) / size, which puts every
    // cell of its grid strictly inside the unit square.
    public static List<Point2D> WholeFramePolygon() =>
        [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
}

// Mirrors the server's TriggerConfigDto - used for both the GET response and the PUT body.
//
// Two rules here are load-bearing; see core/docs/MULTI_ZONE_CLIENT_HANDOFF.md.
//
// 1. ZonePolygon/MinTriggerArea must keep being sent whatever else changes. They mirror Zones[0].
// 2. Zones null and Zones empty mean different things to the server. Null omits the key entirely,
//    which the server reads as "this client doesn't know about zones - update the primary and
//    leave the rest alone". An empty array reads as "the editor really did delete them". Never
//    send an empty list as a placeholder.
public sealed class TriggerConfig
{
    public bool Enabled { get; set; }
    public List<Point2D> ZonePolygon { get; set; } = new();
    public double MinTriggerArea { get; set; }

    // Absent on a server older than multi-zone; read it through ResolvedZones, never directly.
    public List<TriggerZone>? Zones { get; set; }

    public int CooldownSeconds { get; set; }
    public int MaxTriggersPerMinute { get; set; }
    public int ShadowSensitivity { get; set; }
    public bool RecordClips { get; set; }
    public int ClipPreSeconds { get; set; }
    public int ClipPostSeconds { get; set; }
    public bool SendToAlerts { get; set; }
    public string AlertProfile { get; set; } = "";

    // Not shown in any UI. Carried through every save purely so editing a zone from this app
    // doesn't unbind a Device Hub sensor that was set up on the web dashboard - the PUT overwrites
    // the whole record rather than merging, so anything omitted reverts to its default.
    public string SensorDeviceId { get; set; } = "";
    public string SensorTriggerOn { get; set; } = "on";
    public int SensorCooldownSeconds { get; set; } = 30;

    // The overnight trigger pause - see core/docs/NIGHT_WINDOW_HANDOFF.md. Optional for the same
    // reason Zones is, and it is the same trap: null omits the key, which the server reads as
    // "this client doesn't know about the night window" and merges against the stored row - it
    // does NOT mean "turn it off". A build that sent a plain false on every save would switch
    // someone's window off whenever they nudged a polygon, and nothing anywhere would say so.
    public bool? NightWindowEnabled { get; set; }
    public int? NightStartHour { get; set; }
    public int? NightEndHour { get; set; }

    // The two per-camera AI routing flags - see core/docs/AI_ROUTING_HANDOFF.md. Nullable for
    // exactly the reason the night window above is, and it is the same trap: null omits the key,
    // which the server merges against the stored row, while a plain false on every save would
    // switch the coop camera back to the fast detector the first time someone nudged a polygon.
    //
    // BypassFastLocal sends every trigger straight to the full model rather than letting the fast
    // local detector decide what is worth escalating - it is COCO-trained and has no class for a
    // raccoon, fox, opossum or weasel, so at a chicken coop it clears those frames as empty.
    // KeepOnlyAlertable judges a trigger by whether the AI would have alerted on the frame instead
    // of by whether anything was in it, so the coop's own chickens stop confirming every trigger.
    public bool? BypassFastLocal { get; set; }
    public bool? KeepOnlyAlertable { get; set; }

    // Always at least one zone, mirroring the server's TriggerConfig.ResolvedZones: a config
    // written before multi-zone reads as a single zone built from the legacy pair, so the editor
    // never has to special-case the migration.
    [JsonIgnore]
    public List<TriggerZone> ResolvedZones =>
        Zones is { Count: > 0 } zones
            ? zones
            : new List<TriggerZone>
            {
                new()
                {
                    Name = TriggerZone.DefaultName,
                    Polygon = ZonePolygon,
                    MinTriggerArea = MinTriggerArea,
                },
            };

    // Same shape as ResolvedZones, for the same reason: a server older than the night window sends
    // none of the three keys, and the editor needs values to show either way. The fallbacks match
    // the server's own defaults - off, and 21:00-07:00 rather than 0/0, so switching the feature
    // on offers the window someone would actually have asked for.
    [JsonIgnore] public bool ResolvedNightEnabled => NightWindowEnabled ?? false;

    [JsonIgnore] public int ResolvedNightStartHour => NightStartHour ?? 21;

    [JsonIgnore] public int ResolvedNightEndHour => NightEndHour ?? 7;

    // Same fallbacks the server applies to a row written before these two columns existed, and
    // the same reason as the three above: a server older than the feature sends neither key, and
    // the editor still has to show a checkbox. Off is the server's default for both.
    [JsonIgnore] public bool ResolvedBypassFastLocal => BypassFastLocal ?? false;

    [JsonIgnore] public bool ResolvedKeepOnlyAlertable => KeepOnlyAlertable ?? false;
}

public sealed class PromptProfileName
{
    public string Key { get; init; } = "";
    public string DisplayName { get; init; } = "";
}

public sealed class CameraProbeRequest
{
    public string Address { get; set; } = "";
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public sealed class CameraProbeResult
{
    public string Status { get; init; } = ""; // "Found" | "NeedsCredentials" | "NotFound"
    public string? StreamUrl { get; init; }
    public string? SuggestedName { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? Resolution { get; init; }
    public string? Message { get; init; }

    // Only ever populated when an ONVIF PTZ service was actually found - absent just means this
    // camera doesn't have one, which is the normal case, not a failure.
    public bool? SupportsPtz { get; init; }
    public string? OnvifPtzServiceUrl { get; init; }
    public string? OnvifProfileToken { get; init; }
}

public sealed class ScanInterface
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Address { get; init; } = "";
    public int PrefixLength { get; init; }
    public bool LooksVirtual { get; init; }
}

public sealed class ScanRequest
{
    public List<string> InterfaceAddresses { get; set; } = new();
}

public sealed class ScanStartResult
{
    public Guid ScanId { get; init; }
}

public sealed class ScanResultCamera
{
    public string IpAddress { get; init; } = "";
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string SuggestedName { get; init; } = "";
    public bool NeedsCredentials { get; init; }
    public bool AlreadySaved { get; init; }
}

public sealed class ScanJobSnapshot
{
    public string Status { get; init; } = ""; // "Running" | "Completed"
    public List<ScanResultCamera> Results { get; init; } = new();
}

public sealed class AuthStatus
{
    public bool Enabled { get; init; }
    public bool Configured { get; init; }
    public bool Authenticated { get; init; }
    public string? Username { get; init; }

    // Absent on servers older than the multi-account update - treated as Admin everywhere it is
    // read, since that is what every pre-upgrade single-account login effectively was.
    public string? Role { get; init; }
}

public sealed class Account
{
    public Guid Id { get; init; }
    public string Username { get; init; } = "";
    public string? DisplayName { get; init; }
    public string Role { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
}

// The server answers a bad request two different ways: its own {error}/{errors} shape from the
// validation paths, and ASP.NET's RFC 7807 ProblemDetails from Results.Problem, whose message
// lives in Detail. Reading only the first two turns every Results.Problem into a bare
// "Server request failed (HTTP 502)" with the sentence explaining what to do about it thrown away.
public sealed class ApiError
{
    public string? Error { get; init; }
    public List<string>? Errors { get; init; }
    public string? Detail { get; init; }

    /// <summary>Set by the Movies &amp; TV network sign-in when signing in again would fix the failure.</summary>
    public bool? NeedsLogin { get; init; }

    /// <summary>Set by the movie-download site routes (503) while the VPN is still coming up: ask again shortly.</summary>
    public bool? Starting { get; init; }

    /// <summary>A machine-readable reason, e.g. "alreadyInLibrary" on a movie download's 409.</summary>
    public string? Code { get; init; }
}

public sealed class LoginBody
{
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
}

public sealed class CreateAccountBody
{
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public string Role { get; init; } = "";
    public string? DisplayName { get; init; }
}

public sealed class UpdateRoleBody
{
    public string Role { get; init; } = "";
}

public sealed class ResetPasswordBody
{
    public string NewPassword { get; init; } = "";
}

public sealed class ChangePasswordBody
{
    public string CurrentPassword { get; init; } = "";
    public string NewPassword { get; init; } = "";
}

public sealed class PushStatus
{
    public bool Enabled { get; init; }
    public bool AppleConfigured { get; init; }
    public bool FirebaseConfigured { get; init; }
    public int RegisteredDevices { get; init; }
}

public sealed class SystemStatus
{
    public double CpuUsagePercent { get; init; }
    public int ProcessorCount { get; init; }
    public long MemoryUsedBytes { get; init; }
    public long MemoryTotalBytes { get; init; }
    public long DiskFreeBytes { get; init; }
    public long DiskTotalBytes { get; init; }
    public int QueuedAlerts { get; init; }
    public double UptimeSeconds { get; init; }
    public bool VisionBackendHealthy { get; init; }

    [JsonIgnore]
    public double MemoryUsedFraction =>
        MemoryTotalBytes > 0 ? (double)MemoryUsedBytes / MemoryTotalBytes : 0;

    [JsonIgnore] public long DiskUsedBytes => DiskTotalBytes - DiskFreeBytes;

    [JsonIgnore]
    public double DiskUsedFraction => DiskTotalBytes > 0 ? (double)DiskUsedBytes / DiskTotalBytes : 0;
}

public sealed class PushDeviceBody
{
    public string Platform { get; init; } = "";
    public string Token { get; init; } = "";
    public string? DeviceName { get; init; }
}

public sealed class PushDeliveryReport
{
    public int Attempted { get; init; }
    public int Delivered { get; init; }
    public int Failed { get; init; }
    public int Removed { get; init; }
}

public sealed class RecordingTimelineDay
{
    public string Camera { get; init; } = "";
    public string Date { get; init; } = "";
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public TimelineRecording Recording { get; init; } = new();
    public List<TimelineSecurityEvent> Events { get; set; } = new();
}

public sealed class TimelineRecording
{
    public bool Configured { get; init; }
    public bool Available { get; init; }
    public string SourceCamera { get; init; } = "";
    public string? Error { get; init; }
    public string Mode { get; init; } = "";
    public int PreSeconds { get; init; }
    public int PostSeconds { get; init; }
    public List<TimelineRecordingSegment> Segments { get; init; } = new();
}

public sealed class TimelineRecordingSegment
{
    public Guid EventId { get; init; }
    public Guid TriggerEventId { get; init; }
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
}

public sealed class TimelineSecurityEvent
{
    public Guid Id { get; init; }
    public string Camera { get; init; } = "";
    public DateTimeOffset OccurredAt { get; init; }
    public string EventType { get; init; } = "";
    public string Category { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Summary { get; init; } = "";
    public double Confidence { get; init; }
    public bool Notified { get; init; }
    public bool Reviewed { get; set; }
    public bool HasImage { get; init; }
    public bool HasClip { get; init; }
}

public sealed class ReviewedBody
{
    public bool Reviewed { get; init; }
}

public sealed class PtzMoveBody
{
    public double Pan { get; init; }
    public double Tilt { get; init; }
    public double Zoom { get; init; }
}

// Telemetry for the live ladder - see SentinelClient.ReportLivePathAsync. Tier is "mse" or "hls"
// from this app (the dashboard also reports "webrtc"); outcome is "playing", "failed" or
// "degraded".
public sealed class LivePathReportBody
{
    public string Tier { get; init; } = "";
    public string Outcome { get; init; } = "";
    public string? Reason { get; init; }
    public int ElapsedMs { get; init; }
}

// Whether the server has a valid licence file, which is what decides the UNLICENSED watermark
// over camera images and live video.
public sealed class LicenseState
{
    public bool Valid { get; init; }
    public string? CustomerName { get; init; }
}

// What a restore actually put back. Every count is optional because a backup taken before a given
// feature existed simply has no such section, and reporting "0 camera groups" for one of those
// would read like something went missing rather than like nothing was there.
public sealed class BackupRestoreResult
{
    public int? CamerasRestored { get; init; }
    public int? CameraGroupsRestored { get; init; }
    public int? TriggerZonesRestored { get; init; }
    public int? GateChecksRestored { get; init; }
    public int? AccountsRestored { get; init; }
    public int? LiveOrderRestored { get; init; }
    public int? RecordingSchedulesRestored { get; init; }
    public int? DevicesRestored { get; init; }
    public int? AutomationRulesRestored { get; init; }
    public int? SchedulesRestored { get; init; }
    public int? OrganizerItemsRestored { get; init; }
    public int? AttachmentsRestored { get; init; }
    public int? KnownFacePhotosRestored { get; init; }
    public int? OrganizerAutomationsRestored { get; init; }
    public int? AlarmSensorsRestored { get; init; }
    public int? FamilyDrivesRestored { get; init; }
    public int? MediaLibrariesRestored { get; init; }
    public int? MediaProgressRestored { get; init; }
    public int? MusicRowsRestored { get; init; }
    public int? PodcastItemsRestored { get; init; }
    public int? WakeAlarmsRestored { get; init; }
    public int? ListenBrainzRestored { get; init; }

    // Things to act on that the counts can't say - "restart the dashboard", "the main app wasn't
    // running so devices weren't restored".
    public List<string>? Notes { get; init; }

    // The whole outcome as one message: what came back, then anything still to do.
    [JsonIgnore]
    public string Message => string.Join(" ", new[] { $"Restored {Summary}." }.Concat(Notes ?? []));

    // The non-empty parts, phrased for a sentence: "3 cameras, 1 trigger zone".
    [JsonIgnore]
    public string Summary
    {
        get
        {
            (int? Count, string Noun)[] parts =
            {
                (CamerasRestored, "camera"),
                (CameraGroupsRestored, "camera group"),
                (TriggerZonesRestored, "trigger zone"),
                (RecordingSchedulesRestored, "recording schedule"),
                (GateChecksRestored, "gate check"),
                (AccountsRestored, "account"),
                (LiveOrderRestored, "Live camera arrangement"),
                (DevicesRestored, "device"),
                (AutomationRulesRestored, "automation rule"),
                (SchedulesRestored, "schedule"),
                (OrganizerItemsRestored, "Organizer item"),
                (AttachmentsRestored, "attachment"),
                (KnownFacePhotosRestored, "face photo"),
                (OrganizerAutomationsRestored, "board automation"),
                (AlarmSensorsRestored, "alarm sensor"),
                (FamilyDrivesRestored, "Files backup drive"),
                (MediaLibrariesRestored, "Movies & TV folder"),
                (MediaProgressRestored, "watch position"),
                (MusicRowsRestored, "music item"),
                (PodcastItemsRestored, "podcast item"),
                (WakeAlarmsRestored, "wake-up alarm"),
                (ListenBrainzRestored, "ListenBrainz connection"),
            };
            var described = parts
                .Where(part => part.Count is > 0)
                .Select(part => $"{part.Count} {part.Noun}{(part.Count == 1 ? "" : "s")}")
                .ToList();
            return described.Count == 0 ? "settings" : string.Join(", ", described);
        }
    }
}

// What the HomeKit sidecar reports about itself. Running and Published are deliberately separate:
// the sidecar runs for everyone, but only advertises on the network once HomeKit is switched on,
// and the screen needs both to say anything useful.
public sealed class HomeKitStatus
{
    public bool Running { get; init; }
    public bool? Enabled { get; init; }
    public bool? Published { get; init; }
    public bool? Paired { get; init; }
    public List<string>? Accessories { get; init; }
    public string? PairingCode { get; init; }
    public string? Error { get; init; }
}

// The four editable HomeKit settings. Hand-written rather than schema-driven because the useful
// half of this section is the pairing code, which no schema can describe.
public sealed class HomeKitSettings
{
    public bool Enabled { get; set; }
    public string SidecarBaseUrl { get; set; } = "http://127.0.0.1:3321";
    public int TimeoutSeconds { get; set; } = 10;
    public double MinimumConfidence { get; set; } = 0.6;
}

// A number plate the AI should treat as household rather than a stranger's. The plate is the
// identity here - it is what the matcher compares against, and two rows with the same one would
// be the same vehicle twice.
public sealed class KnownVehicle
{
    public string Plate { get; set; } = "";
    public string Label { get; set; } = "";
}

public sealed class DeviceHubMetadata
{
    public List<string> Kinds { get; init; } = new();
    public List<string> Schemas { get; init; } = new();
    public int DiscoverySeconds { get; init; }
}

public sealed class ServerHealthResponse
{
    public string Status { get; init; } = "";

    // Absent on any server older than the version-stamping update, in the same way AuthStatus.Role
    // is absent before multi-account. Its absence is itself a version signal.
    public ServerBuild? Build { get; init; }
}

/// <summary>
/// Whether a camera is recording by hand right now, and when the server will stop it if nobody
/// does. <see cref="StopsAtUtc"/> is the press-again deadline, not a guess: the server writes it
/// when the recording starts and honours it with ffmpeg's own -t.
/// </summary>
public sealed class ManualRecordingState
{
    public bool IsRecording { get; set; }
    public Guid? RecordingId { get; set; }
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? StopsAtUtc { get; set; }
    public int MaxMinutes { get; set; }

    public static ManualRecordingState Idle => new();
}

public sealed class ManualRecordingResponse
{
    public bool Success { get; set; }
    public ManualRecordingState State { get; set; } = new();
}

public sealed class ManualRecordingStopped
{
    public bool Success { get; set; }
    public ManualRecordingState State { get; set; } = new();

    /// <summary>
    /// How the finished recording was filed - "complete", "partial", "failed" - and how many seconds
    /// of video it holds. Both are null when the stop found nothing running, which is not an error:
    /// two devices racing to stop the same recording is an ordinary thing to do.
    /// </summary>
    public string? Status { get; set; }

    public string Detail { get; set; } = string.Empty;
    public int? Seconds { get; set; }
}
