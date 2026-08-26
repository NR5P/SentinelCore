using System.Globalization;
using System.Text.Json.Serialization;

namespace SentinelCore.Client.Models;

// One numeric reading a device published, keyed by whatever the payload called it. There are
// deliberately no units and no friendly name: the server hands over the raw number under the
// device's own key, because no two vendors agree on units well enough for guessing to beat
// saying nothing.
public sealed class HubMeasurement
{
    public string Key { get; init; } = "";
    public double Value { get; init; }

    // Round to two decimals, then drop the trailing zeros - 21.500000000000004 is float noise
    // picked up from scaling upstream, not precision the sensor reported. Matches the dashboard's
    // formatMeasurement so the same reading reads identically on every client.
    [JsonIgnore]
    public string FormattedValue
    {
        get
        {
            if (double.IsNaN(Value) || double.IsInfinity(Value)) return "-";
            var text = Value.ToString("F2", CultureInfo.InvariantCulture);
            if (text.Contains('.'))
            {
                text = text.TrimEnd('0').TrimEnd('.');
            }
            return text is "-0" or "" ? "0" : text;
        }
    }
}

// A device in the Device Hub, as returned by /api/devices. Power/brightness/available are the
// live MQTT state the server is tracking, folded into the same object as the configuration so the
// list renders from one call; /api/devices/state re-fetches just those while the tab is open.
public sealed class HubDevice
{
    public Guid Id { get; init; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Room { get; set; } = "";
    public bool Enabled { get; set; }
    public string CommandTopic { get; set; } = "";
    public string StateTopic { get; set; } = "";
    public string AvailabilityTopic { get; set; } = "";
    public string PayloadOn { get; set; } = "";
    public string PayloadOff { get; set; } = "";

    // Optional on the wire so this app still decodes a server older than the gate/cover update.
    public string? PayloadStop { get; set; }
    public bool? Momentary { get; set; }

    public string PayloadAvailable { get; set; } = "";
    public string PayloadNotAvailable { get; set; } = "";
    public string Schema { get; set; } = "";
    public string StateValueTemplate { get; set; } = "";
    public bool Optimistic { get; set; }
    public bool SupportsBrightness { get; set; }
    public string BrightnessCommandTopic { get; set; } = "";
    public string BrightnessStateTopic { get; set; } = "";
    public int BrightnessScale { get; set; }
    public string DiscoverySource { get; set; } = "";
    public int? SortOrder { get; set; }

    // "on", "off", or "unknown" - a string rather than a bool because "we have never heard from
    // this device" is a genuinely different state from "off", and the UI says so.
    public string Power { get; set; } = "unknown";
    public int? Brightness { get; set; }
    public bool Available { get; set; }
    public bool Commandable { get; set; }

    // Optional on the wire so this app still decodes against a server older than the numeric
    // sensor update. Sorted by key server-side; render in the order given so the rows don't
    // reshuffle between polls.
    public List<HubMeasurement>? Measurements { get; set; }

    [JsonIgnore] public bool IsOn => Power == "on";

    // A device with no on/off that is publishing numbers is working, not in an unknown state.
    [JsonIgnore]
    public bool IsReporting => !Commandable && Power == "unknown" && Measurements is { Count: > 0 };

    // A pulse device: one press, no on/off state afterwards. The server already folds the "button"
    // kind into this flag, but the kind check keeps older servers honest too.
    [JsonIgnore] public bool IsMomentary => (Momentary ?? false) || Kind == "button";

    // Kinds that speak open/close rather than on/off - their controls and status wording differ.
    [JsonIgnore] public bool IsCoverLike => Kind is "cover" or "gate" or "garage";

    [JsonIgnore] public bool IsLock => Kind == "lock";

    // Whether this device has a third "stop" command its motor understands.
    [JsonIgnore] public bool HasStop => !string.IsNullOrEmpty(PayloadStop);
}

public sealed class HubDeviceState
{
    public Guid Id { get; init; }
    public string Power { get; set; } = "unknown";
    public int? Brightness { get; set; }
    public bool Available { get; set; }

    // Optional for the same reason as HubDevice.Measurements - see the note there.
    public List<HubMeasurement>? Measurements { get; set; }
}

public sealed class HubDeviceStateSnapshot
{
    public bool Connected { get; set; }
    public List<HubDeviceState> Devices { get; set; } = new();
}

public sealed class DeviceCommandResult
{
    public bool Sent { get; init; }
    public bool Acknowledged { get; init; }
    public string Power { get; init; } = "";
    public int? Brightness { get; init; }
}

// What a discovery scan thinks is out there. Not a device until the user adds it - the fields line
// up with HubDeviceInput so accepting one is a straight copy.
public sealed class DeviceCandidate
{
    public string SuggestedName { get; init; } = "";
    public string Kind { get; init; } = "";
    public string CommandTopic { get; init; } = "";
    public string StateTopic { get; init; } = "";
    public string AvailabilityTopic { get; init; } = "";
    public string PayloadOn { get; init; } = "";
    public string PayloadOff { get; init; } = "";

    // Optional so scans against a server older than the gate/cover update still decode.
    public string? PayloadStop { get; init; }
    public bool? Momentary { get; init; }

    public string Schema { get; init; } = "";
    public string StateValueTemplate { get; init; } = "";
    public bool SupportsBrightness { get; init; }
    public string BrightnessCommandTopic { get; init; } = "";
    public string BrightnessStateTopic { get; init; } = "";
    public int BrightnessScale { get; init; }
    public string Source { get; init; } = "";
    public bool AlreadyAdded { get; init; }

    [JsonIgnore]
    public string CandidateId => string.IsNullOrEmpty(CommandTopic) ? StateTopic : CommandTopic;
}

public sealed class DeviceScanStartResult
{
    public Guid ScanId { get; init; }
    public int ScanSeconds { get; init; }
}

public sealed class DeviceScanSnapshot
{
    public string Status { get; init; } = ""; // "running" | "completed"
    public List<DeviceCandidate> Results { get; init; } = new();
}

public sealed class DeviceHubEnabled
{
    public bool Enabled { get; init; }
}

public sealed class HubDeviceInput
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "light";
    public string Room { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string CommandTopic { get; set; } = "";
    public string StateTopic { get; set; } = "";
    public string AvailabilityTopic { get; set; } = "";
    public string PayloadOn { get; set; } = "ON";
    public string PayloadOff { get; set; } = "OFF";
    public string PayloadStop { get; set; } = "";
    public bool Momentary { get; set; }
    public string PayloadAvailable { get; set; } = "online";
    public string PayloadNotAvailable { get; set; } = "offline";
    public string Schema { get; set; } = "raw";
    public string StateValueTemplate { get; set; } = "";
    public bool Optimistic { get; set; }
    public bool SupportsBrightness { get; set; }
    public string BrightnessCommandTopic { get; set; } = "";
    public string BrightnessStateTopic { get; set; } = "";
    public int BrightnessScale { get; set; } = 255;
    public string DiscoverySource { get; set; } = "manual";

    // A blank device for the add form. Defaults match the most common case by a wide margin: a raw
    // ON/OFF light, which is what Tasmota and ESPHome produce.
    public HubDeviceInput()
    {
    }

    public static HubDeviceInput FromDevice(HubDevice device) => new()
    {
        Name = device.Name,
        Kind = device.Kind,
        Room = device.Room,
        Enabled = device.Enabled,
        CommandTopic = device.CommandTopic,
        StateTopic = device.StateTopic,
        AvailabilityTopic = device.AvailabilityTopic,
        PayloadOn = device.PayloadOn,
        PayloadOff = device.PayloadOff,
        PayloadStop = device.PayloadStop ?? "",
        Momentary = device.Momentary ?? false,
        PayloadAvailable = device.PayloadAvailable,
        PayloadNotAvailable = device.PayloadNotAvailable,
        Schema = device.Schema,
        StateValueTemplate = device.StateValueTemplate,
        Optimistic = device.Optimistic,
        SupportsBrightness = device.SupportsBrightness,
        BrightnessCommandTopic = device.BrightnessCommandTopic,
        BrightnessStateTopic = device.BrightnessStateTopic,
        BrightnessScale = device.BrightnessScale,
        DiscoverySource = device.DiscoverySource,
    };

    public static HubDeviceInput FromCandidate(DeviceCandidate candidate) => new()
    {
        Name = candidate.SuggestedName,
        Kind = candidate.Kind,
        Room = "",
        Enabled = true,
        CommandTopic = candidate.CommandTopic,
        StateTopic = candidate.StateTopic,
        AvailabilityTopic = candidate.AvailabilityTopic,
        PayloadOn = candidate.PayloadOn,
        PayloadOff = candidate.PayloadOff,
        PayloadStop = candidate.PayloadStop ?? "",
        Momentary = candidate.Momentary ?? false,
        PayloadAvailable = "online",
        PayloadNotAvailable = "offline",
        Schema = candidate.Schema,
        StateValueTemplate = candidate.StateValueTemplate,
        Optimistic = false,
        SupportsBrightness = candidate.SupportsBrightness,
        BrightnessCommandTopic = candidate.BrightnessCommandTopic,
        BrightnessStateTopic = candidate.BrightnessStateTopic,
        BrightnessScale = candidate.BrightnessScale,
        DiscoverySource = candidate.Source,
    };
}

public sealed class DeviceCommandInput
{
    public string? Action { get; init; }
    public int? Brightness { get; init; }
}

// PUT /api/devices/order - the complete arrangement, first device first. Same shape as LiveOrder
// is for camera tiles.
public sealed class DeviceOrderInput
{
    public List<Guid> DeviceIds { get; init; } = new();
}
