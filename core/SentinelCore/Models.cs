using System.Text.Json.Serialization;

namespace SentinelCore;

public sealed record IncomingAlert(
    Guid Id,
    string Camera,
    string? BlueIrisAlertId,
    DateTimeOffset OccurredAt,
    DateTimeOffset ReceivedAt,
    string Profile,
    string ContentType,
    string OriginalFileName,
    byte[] ImageBytes,
    // Where the image was written at ingest, before the alert ever reached the queue. This is
    // what makes an in-flight alert recoverable after a restart - see AlertRecoveryService.
    // Null only when Bridge:SaveImages is off, in which case there's nothing to recover from.
    string? ImagePath = null)
{
    // Extra frames captured a fraction of a second apart around the same trigger moment as
    // ImageBytes, only ever populated for trigger-zone cameras with MultiFrameAnalysis enabled
    // (see TriggerZoneMonitor/AlertForwarder) - Blue Iris alerts never populate this, since Blue
    // Iris pushes exactly one image per alert and nothing here reaches back out to fetch more.
    // Purely transient: sent to the vision client alongside ImageBytes, never persisted to disk
    // or shown anywhere, unlike ImageBytes/ImagePath above.
    public IReadOnlyList<byte[]> ExtraFrames { get; init; } = [];

    // Only populated for a trigger-zone camera that belongs to a Camera Group (see
    // TriggerZoneMonitor/CameraGroup) - Blue Iris alerts never populate either of these.
    // LinkedCameras is a plain display string (comma-joined companion camera names) shown
    // verbatim in notifications/Feed, not a foreign key. IncidentId is an opaque correlation
    // key shared by every alert (this camera's and, if AnalyzeCompanionCaptures is on for the
    // group, its companions') that belongs to the same incident.
    public string? LinkedCameras { get; init; }
    public string? IncidentId { get; init; }

    // Set only by a trigger zone whose camera asked to skip the fast local detector (see
    // TriggerConfig.BypassFastLocal) - Blue Iris alerts never populate it. Read once, in
    // FailoverVisionClient, to decide whether the detector gets a look before the full model.
    //
    // Transient like ExtraFrames: it is not on PendingAlertRecord, so an alert recovered after a
    // restart routes normally. Same as LinkedCameras/IncidentId today, and the cost is one
    // detector-gated analysis for an alert that was already interrupted.
    public bool BypassFastLocal { get; init; }
}

public sealed record AcceptedAlertResponse(Guid Id, string Status, string StatusUrl);

public sealed class VisionAnalysis
{
    // Not init-only, for the same reason as NotificationRecommended below: AlertProcessor corrects
    // it after the model has spoken. As returned, this field is very nearly always "none" or
    // "unknown" even on alerts the same response recommended notifying about - see
    // EventTypeClassifier, which re-derives it from the structured fields when the model's own
    // answer contradicts them.
    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = "unknown";

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("gate")]
    public GateAnalysis Gate { get; init; } = new();

    [JsonPropertyName("person")]
    public PersonAnalysis Person { get; init; } = new();

    [JsonPropertyName("vehicle")]
    public VehicleAnalysis Vehicle { get; init; } = new();

    [JsonPropertyName("package")]
    public PackageAnalysis Package { get; init; } = new();

    [JsonPropertyName("animal")]
    public AnimalAnalysis Animal { get; init; } = new();

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }

    // Not init-only: Package Theft Watch (AlertProcessor) forces this true after the model's own
    // judgment when a watched package disappears with nobody recognized handling it, the same
    // way KnownFaceLabels/KnownVehicleLabel below are filled in after the fact.
    [JsonPropertyName("notification_recommended")]
    public bool NotificationRecommended { get; set; }

    [JsonPropertyName("notification_reason")]
    public string NotificationReason { get; init; } = string.Empty;

    // Only requested from the model when the profile has PromptProfile.CameraBlockedCheckEnabled
    // set - false otherwise, same opt-in shape as VehicleAnalysis.LicensePlateText. Used by the
    // gate/door open check (GateCheckAnalyzer) to also catch a covered or obstructed lens.
    [JsonPropertyName("camera_blocked")]
    public bool CameraBlocked { get; init; }

    // Not part of the model's JSON output - filled in afterward by AlertProcessor's Package
    // Theft Watch (see PackageWatchStore) when a package it was watching on this camera
    // disappears on a later alert without a recognized face/vehicle handling it. Off by default
    // (feature is opt-in) and never set for the delivery event itself, only the later one where
    // the package is observed gone.
    [JsonPropertyName("package_theft_suspected")]
    public bool PackageTheftSuspected { get; set; }

    [JsonPropertyName("package_theft_reason")]
    public string? PackageTheftReason { get; set; }
}

public sealed class GateAnalysis
{
    [JsonPropertyName("state")]
    public string State { get; init; } = "unknown";
}

public sealed class PersonAnalysis
{
    [JsonPropertyName("visible")]
    public bool Visible { get; init; }

    [JsonPropertyName("location")]
    public string Location { get; init; } = "none";

    [JsonPropertyName("activity")]
    public string Activity { get; init; } = "none";

    [JsonPropertyName("outside_vehicle")]
    public bool OutsideVehicle { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    // Not part of the model's JSON output - filled in afterward by AlertProcessor running local
    // face detection/matching (see FaceRecognitionService, KnownFaceMatcher). Empty when face
    // recognition wasn't enabled for this profile, no face was detected, or no detected face
    // matched a configured KnownFace closely enough.
    [JsonPropertyName("known_face_labels")]
    public List<string> KnownFaceLabels { get; set; } = [];

    // True when at least one face was detected but didn't match any KnownFace closely enough -
    // lets notifications distinguish "no one recognizable" from "no face at all", the same
    // spirit as a vehicle's "unrecognized" plate vs. no plate read at all.
    [JsonPropertyName("unrecognized_face_detected")]
    public bool UnrecognizedFaceDetected { get; set; }
}

public sealed class VehicleAnalysis
{
    [JsonPropertyName("visible")]
    public bool Visible { get; init; }

    [JsonPropertyName("location")]
    public string Location { get; init; } = "none";

    [JsonPropertyName("orientation")]
    public string Orientation { get; init; } = "unknown";

    // Which way the vehicle is actually travelling, as opposed to Orientation, which is only which
    // way it is pointed - a vehicle reversing out of the driveway is facing_gate and receding at
    // the same time. Only requested when a burst of frames was captured (see MultiFrameOptions);
    // a single image cannot show motion, so this stays "unknown" for every Blue Iris alert.
    [JsonPropertyName("motion")]
    public string Motion { get; init; } = "unknown";

    [JsonPropertyName("activity")]
    public string Activity { get; init; } = "none";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "none";

    [JsonPropertyName("color")]
    public string Color { get; init; } = "unknown";

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("delivery_company")]
    public string DeliveryCompany { get; init; } = "none";

    [JsonPropertyName("delivery_company_confidence")]
    public double DeliveryCompanyConfidence { get; init; }

    // Only requested from the model when the profile has license-plate recognition enabled;
    // empty otherwise. Best-effort OCR from a general vision model - accuracy depends heavily
    // on camera resolution and plate angle, so this is never used to gate notifications.
    [JsonPropertyName("license_plate")]
    public string LicensePlateText { get; init; } = string.Empty;

    // Not part of the model's JSON output - filled in afterward by AlertProcessor comparing
    // LicensePlateText against the configured KnownVehicles list. Null means either plate
    // recognition wasn't enabled, no plate was read, or it didn't match a known vehicle.
    [JsonPropertyName("known_vehicle_label")]
    public string? KnownVehicleLabel { get; set; }
}

public sealed class PackageAnalysis
{
    [JsonPropertyName("visible")]
    public bool Visible { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "none";

    [JsonPropertyName("location")]
    public string Location { get; init; } = "none";
}

public sealed class AnimalAnalysis
{
    [JsonPropertyName("visible")]
    public bool Visible { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;
}

public sealed class AlertResult
{
    public Guid Id { get; init; }
    public string Camera { get; init; } = string.Empty;
    public string? BlueIrisAlertId { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string Profile { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string Status { get; init; } = "queued";
    public string? ImagePath { get; init; }
    public VisionAnalysis? Analysis { get; init; }
    public string? RawModelResponse { get; init; }
    public string? Error { get; init; }

    // See IncomingAlert.LinkedCameras/IncidentId - carried through unchanged from ingest.
    public string? LinkedCameras { get; init; }
    public string? IncidentId { get; init; }

    // See ImageEmbeddingService - only populated when Semantic Search is enabled and encoding
    // this alert's image succeeded.
    public float[]? Embedding { get; init; }

    // Set when this alert showed the same subjects as a notification the same camera sent moments
    // ago - see NotificationDeduplicator. It suppresses notifications and nothing else: the alert
    // is analyzed, stored and shown in the Feed exactly as any other, and its trigger and clip are
    // unaffected. Kept on the record rather than left in a log line so the Feed can say why an
    // event that plainly had someone in it never reached anyone's phone.
    //
    // Not init-only, for the same reason VisionAnalysis.NotificationRecommended isn't: the check
    // needs a finished result to run against, so AlertProcessor fills this in on the way to
    // saving rather than at construction.
    public Guid? DuplicateOfAlertId { get; set; }

    // Set when a newer frame from the same camera arrived while this one was still waiting to be
    // analyzed, and took its place - see AlertQueue. Status is "superseded" and there is no
    // Analysis, because none was ever run. The id is what lets SentinelCore.Web's trigger
    // confirmation follow the chain to a verdict instead of giving up on this alert - see
    // TriggerConfirmationService.
    public Guid? SupersededByAlertId { get; init; }
}
