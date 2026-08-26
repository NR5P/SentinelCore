using System.Text.Json.Serialization;

namespace SentinelCore.Client.Models;

// A "do this at that time" rule. The clock-driven counterpart to the automation rules on the
// dashboard, sharing their action vocabulary plus a Device Hub option. See ScheduleModel.cs.
public sealed class ScheduleAction
{
    public string Type { get; set; } = "";
    public Guid? DeviceId { get; set; }
    public string DeviceAction { get; set; } = "";
    public int? DeviceBrightness { get; set; }
    public string MqttTopic { get; set; } = "";
    public string MqttPayload { get; set; } = "";
    public bool MqttRetain { get; set; }
    public string WebhookUrl { get; set; } = "";
    public string WebhookMethod { get; set; } = "";
    public string WebhookHeaders { get; set; } = "";
    public string WebhookBody { get; set; } = "";
    public string WebhookContentType { get; set; } = "";
}

public sealed class Schedule
{
    public Guid Id { get; init; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string Kind { get; set; } = "";
    public string TimeOfDay { get; set; } = "";
    public List<int> DaysOfWeek { get; set; } = new();
    public int IntervalSeconds { get; set; }
    public string WindowStart { get; set; } = "";
    public string WindowEnd { get; set; } = "";
    public string CronExpression { get; set; } = "";
    public DateTimeOffset? RunAtUtc { get; set; }
    public string SolarEvent { get; set; } = "";
    public int SolarOffsetMinutes { get; set; }

    // How far below the horizon the sun must be for a "dusk"/"dawn" firing - a darkness level
    // rather than a time, which is what makes it track the seasons on its own.
    public double SolarElevationDegrees { get; set; }

    public ScheduleAction Action { get; set; } = new();
    public string CatchUpPolicy { get; set; } = "";
    public DateTimeOffset? LastRunUtc { get; set; }
    public string LastStatus { get; set; } = "";
    public string LastDetail { get; set; } = "";
    public DateTimeOffset? NextDueUtc { get; set; }
    public int RunCount { get; set; }

    [JsonIgnore] public bool FollowsTheSun => Kind == "solar";
}

public sealed class ScheduleRun
{
    public Guid Id { get; init; }
    public Guid ScheduleId { get; init; }
    public DateTimeOffset DueAtUtc { get; init; }
    public DateTimeOffset FiredAtUtc { get; init; }
    public string Status { get; init; } = "";
    public string Detail { get; init; } = "";
    public int DurationMs { get; init; }
}

// One of the named darkness levels the server offers, so this app doesn't have to know that
// "civil twilight" means -6 degrees.
public sealed class DarknessLevel
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Description { get; init; } = "";
    public double ElevationDegrees { get; init; }
}

// A commandable Device Hub device, in the trimmed-down shape the schedules metadata returns.
public sealed class ScheduleDeviceOption
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Room { get; init; } = "";
    public bool Enabled { get; init; }
    public bool SupportsBrightness { get; init; }
    public bool Momentary { get; init; }
    public bool CoverLike { get; init; }
    public bool HasStop { get; init; }
}

public sealed class ScheduleMetadata
{
    public List<string> Kinds { get; init; } = new();
    public List<string> SolarEvents { get; init; } = new();
    public List<DarknessLevel> DarknessLevels { get; init; } = new();
    public List<string> ActionTypes { get; init; } = new();
    public List<string> CatchUpPolicies { get; init; } = new();
    public List<string> DeviceActions { get; init; } = new();
    public List<string> Tokens { get; init; } = new();
    public int MinimumIntervalSeconds { get; init; } = 10;

    // Whether a latitude and longitude have been set. A sun-following schedule can't be created
    // without them, and saying so up front beats a save that fails.
    public bool LocationSet { get; init; }

    public string TimeZone { get; init; } = "";
    public List<ScheduleDeviceOption> Devices { get; init; } = new();

    public static ScheduleMetadata Empty { get; } = new();
}

public sealed class SchedulePreviewOccurrence
{
    public DateTimeOffset Utc { get; init; }
    public string Local { get; init; } = "";
}

public sealed class SchedulePreview
{
    public List<SchedulePreviewOccurrence> Occurrences { get; init; } = new();
    public string TimeZone { get; init; } = "";
    public string Explanation { get; init; } = "";
}

public sealed class ScheduleInput
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Kind { get; set; } = "daily";
    public string TimeOfDay { get; set; } = "";
    public List<int> DaysOfWeek { get; set; } = new();
    public int IntervalSeconds { get; set; }
    public string WindowStart { get; set; } = "";
    public string WindowEnd { get; set; } = "";
    public string CronExpression { get; set; } = "";

    // ISO-8601 rather than a DateTimeOffset so the value on the wire is unambiguous whichever
    // serializer settings a caller is using.
    public string? RunAtUtc { get; set; }

    public string SolarEvent { get; set; } = "";
    public int SolarOffsetMinutes { get; set; }
    public double SolarElevationDegrees { get; set; }
    public string ActionType { get; set; } = "";
    public Guid? DeviceId { get; set; }
    public string DeviceAction { get; set; } = "";
    public int? DeviceBrightness { get; set; }
    public string MqttTopic { get; set; } = "";
    public string MqttPayload { get; set; } = "";
    public bool MqttRetain { get; set; }
    public string WebhookUrl { get; set; } = "";
    public string WebhookMethod { get; set; } = "POST";
    public string WebhookHeaders { get; set; } = "";
    public string WebhookBody { get; set; } = "";
    public string WebhookContentType { get; set; } = "application/json";
    public string CatchUpPolicy { get; set; } = "";

    public static ScheduleInput FromSchedule(Schedule schedule) => new()
    {
        Name = schedule.Name,
        Enabled = schedule.Enabled,
        Kind = schedule.Kind,
        TimeOfDay = schedule.TimeOfDay,
        DaysOfWeek = new List<int>(schedule.DaysOfWeek),
        IntervalSeconds = schedule.IntervalSeconds,
        WindowStart = schedule.WindowStart,
        WindowEnd = schedule.WindowEnd,
        CronExpression = schedule.CronExpression,
        RunAtUtc = schedule.RunAtUtc?.UtcDateTime.ToString("o"),
        SolarEvent = schedule.SolarEvent,
        SolarOffsetMinutes = schedule.SolarOffsetMinutes,
        SolarElevationDegrees = schedule.SolarElevationDegrees,
        ActionType = schedule.Action.Type,
        DeviceId = schedule.Action.DeviceId,
        DeviceAction = schedule.Action.DeviceAction,
        DeviceBrightness = schedule.Action.DeviceBrightness,
        MqttTopic = schedule.Action.MqttTopic,
        MqttPayload = schedule.Action.MqttPayload,
        MqttRetain = schedule.Action.MqttRetain,
        WebhookUrl = schedule.Action.WebhookUrl,
        WebhookMethod = string.IsNullOrEmpty(schedule.Action.WebhookMethod) ? "POST" : schedule.Action.WebhookMethod,
        WebhookHeaders = schedule.Action.WebhookHeaders,
        WebhookBody = schedule.Action.WebhookBody,
        WebhookContentType = string.IsNullOrEmpty(schedule.Action.WebhookContentType)
            ? "application/json"
            : schedule.Action.WebhookContentType,
        CatchUpPolicy = schedule.CatchUpPolicy,
    };
}

public sealed class ScheduleEnabledInput
{
    public bool Enabled { get; init; }
}

public sealed class ScheduleRunResult
{
    public bool Fired { get; init; }
    public string? Detail { get; init; }
    public string? Error { get; init; }
}
