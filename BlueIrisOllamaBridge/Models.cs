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
    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("person_visible")]
    public bool PersonVisible { get; init; }

    [JsonPropertyName("vehicle_visible")]
    public bool VehicleVisible { get; init; }

    [JsonPropertyName("animal_visible")]
    public bool AnimalVisible { get; init; }

    [JsonPropertyName("package_visible")]
    public bool PackageVisible { get; init; }

    [JsonPropertyName("notification_recommended")]
    public bool NotificationRecommended { get; init; }

    [JsonPropertyName("notification_reason")]
    public string NotificationReason { get; init; } = string.Empty;

    [JsonPropertyName("answers")]
    public List<QuestionAnswer> Answers { get; init; } = [];
}

public sealed record QuestionAnswer(
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("answer")] string Answer);

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
