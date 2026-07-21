using System.Text.Json.Serialization;

namespace BlueIrisOllamaBridge;

public sealed record IncomingAlert(
    Guid Id,
    string Camera,
    string? BlueIrisAlertId,
    DateTimeOffset OccurredAt,
    DateTimeOffset ReceivedAt,
    string Profile,
    string ContentType,
    string OriginalFileName,
    byte[] ImageBytes);

public sealed record AcceptedAlertResponse(Guid Id, string Status, string StatusUrl);

public sealed class VisionAnalysis
{
    [JsonPropertyName("event_type")]
    public string EventType { get; init; } = "unknown";

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

    [JsonPropertyName("notification_recommended")]
    public bool NotificationRecommended { get; init; }

    [JsonPropertyName("notification_reason")]
    public string NotificationReason { get; init; } = string.Empty;
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
}

public sealed class VehicleAnalysis
{
    [JsonPropertyName("visible")]
    public bool Visible { get; init; }

    [JsonPropertyName("location")]
    public string Location { get; init; } = "none";

    [JsonPropertyName("orientation")]
    public string Orientation { get; init; } = "unknown";

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
}
