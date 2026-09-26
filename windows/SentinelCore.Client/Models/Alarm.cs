namespace SentinelCore.Client.Models;

// The home alarm as /api/alarm/status reports it. Everything the section shows comes from this
// one call; the app polls it while the section is on screen and counts the delays down from the
// deadlines it carries. Most properties are null when the alarm is switched off server-side,
// which answers with just Enabled and HubEnabled.
public sealed class AlarmStatus
{
    public bool Enabled { get; init; }
    public bool? HubEnabled { get; init; }
    public bool? CanEdit { get; init; }
    public string? Mode { get; init; }
    public string? Status { get; init; }
    public DateTimeOffset? ChangedAt { get; init; }
    public string? ChangedBy { get; init; }
    public DateTimeOffset? ExitUntil { get; init; }
    public DateTimeOffset? PendingUntil { get; init; }
    public DateTimeOffset? TriggeredAt { get; init; }
    public string? TriggeredBy { get; init; }
    public bool? SirenOn { get; init; }
    public bool? PinSet { get; init; }
    public bool? BrokerConnected { get; init; }
    public bool? Ready { get; init; }
    public List<string>? Issues { get; init; }
    public List<AlarmSensor>? Sensors { get; init; }
    public AlarmConfig? Config { get; init; }
    public DateTimeOffset? Now { get; init; }

    public bool IsTriggered => Status == "triggered";
}

public sealed class AlarmSensor
{
    public Guid Id { get; init; }
    public Guid DeviceId { get; init; }
    public string Name { get; init; } = "";
    public string Zone { get; init; } = "perimeter";
    public bool ArmedHome { get; init; }
    public bool ArmedNight { get; init; }
    public bool ArmedAway { get; init; }
    public bool Inverted { get; init; }
    public bool Enabled { get; init; }
    public int SortOrder { get; init; }
    public string? DeviceName { get; init; }
    public string? Room { get; init; }
    public bool DeviceMissing { get; init; }
    // open | closed | unknown
    public string State { get; init; } = "unknown";
    public bool Available { get; init; }
    public bool Offline { get; init; }
    public double? Battery { get; init; }
    public bool LowBattery { get; init; }
    public double? LinkQuality { get; init; }
    public DateTimeOffset? LastSeen { get; init; }
    public DateTimeOffset? LastChanged { get; init; }
    public bool Bypassed { get; init; }
    public bool Watched { get; init; }

    public bool IsOpen => State == "open";

    public string StateLabel => (Zone, State) switch
    {
        ("interior", "open") => "Motion",
        ("interior", "closed") => "Clear",
        ("always", "open") => "Alert",
        ("always", "closed") => "OK",
        (_, "open") => "Open",
        (_, "closed") => "Closed",
        _ => "No reading"
    };

    public string ZoneLabel => AlarmZones.Label(Zone);
}

public static class AlarmZones
{
    public static readonly string[] All = ["entry", "perimeter", "interior", "always"];

    public static string Label(string zone) => zone switch
    {
        "entry" => "Entry door",
        "perimeter" => "Perimeter",
        "interior" => "Interior motion",
        "always" => "24-hour",
        _ => zone
    };

    public static string Explanation(string zone) => zone switch
    {
        "entry" => "The way in. Opening it while armed starts the entry delay so whoever came in can disarm.",
        "perimeter" => "Windows and other doors. Instant when armed.",
        "interior" => "Motion inside. Instant, and by default only watched in Away.",
        "always" => "Watched around the clock, armed or not: water leak, smoke, tamper.",
        _ => ""
    };
}

public static class AlarmModes
{
    public static string Label(string? mode) => mode switch
    {
        "home" => "Home",
        "night" => "Night",
        "away" => "Away",
        _ => "Disarmed"
    };
}

public sealed class AlarmConfig
{
    public int ExitDelaySeconds { get; set; } = 60;
    public int EntryDelaySeconds { get; set; } = 30;
    public int SirenSeconds { get; set; } = 300;
    public List<Guid> SirenDeviceIds { get; set; } = new();
    public List<Guid> CameraIds { get; set; } = new();
    public bool NotifyPush { get; set; } = true;
    public bool NotifyArmDisarm { get; set; } = true;
    public int LowBatteryPercent { get; set; } = 20;
    public int OfflineAfterHours { get; set; } = 24;
    public bool RequirePinToArm { get; set; }
}

public sealed class AlarmEvent
{
    public long Id { get; init; }
    public DateTimeOffset At { get; init; }
    public string Kind { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Actor { get; init; }
    public Guid? SensorId { get; init; }
}

// What arm, disarm, panic and test answer on success. A refusal (something open, wrong PIN) is a
// 4xx SentinelServerException whose message is the server's explanation.
public sealed class AlarmCommandReply
{
    public bool Ok { get; init; }
    public string? Note { get; init; }
}

public sealed class AlarmArmInput
{
    public string Mode { get; set; } = "away";
    public bool Force { get; set; }
    public string? Pin { get; set; }
}

public sealed class AlarmDisarmInput
{
    public string? Pin { get; set; }
}

public sealed class AlarmSensorInput
{
    public Guid DeviceId { get; set; }
    public string Name { get; set; } = "";
    public string Zone { get; set; } = "perimeter";
    public bool ArmedHome { get; set; } = true;
    public bool ArmedNight { get; set; } = true;
    public bool ArmedAway { get; set; } = true;
    public bool Inverted { get; set; }
    public bool Enabled { get; set; } = true;

    public static AlarmSensorInput FromSensor(AlarmSensor sensor) => new()
    {
        DeviceId = sensor.DeviceId,
        Name = sensor.Name,
        Zone = sensor.Zone,
        ArmedHome = sensor.ArmedHome,
        ArmedNight = sensor.ArmedNight,
        ArmedAway = sensor.ArmedAway,
        Inverted = sensor.Inverted,
        Enabled = sensor.Enabled,
    };
}

public sealed class AlarmPinInput
{
    public string Pin { get; set; } = "";
    public string? CurrentPin { get; set; }
}
