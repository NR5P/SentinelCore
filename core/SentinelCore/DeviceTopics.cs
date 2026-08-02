using System.Text.Json;

namespace SentinelCore;

public enum DevicePower
{
    Unknown,
    On,
    Off
}

// One MQTT publish. A single user-facing command can become two of these - e.g. a Tasmota lamp
// takes its on/off on cmnd/<x>/POWER and its brightness on cmnd/<x>/Dimmer.
public sealed record DevicePublish(string Topic, string Payload, bool Retain);

// What a device's own state message told us. Null members mean "this message didn't say".
public sealed record DeviceStateReading(DevicePower Power, int? BrightnessPercent);

// Pure payload building and parsing, with no MQTT connection anywhere in sight - the same split
// MqttTopics/MqttPublisher already use, so every vendor quirk below is testable directly against
// a real captured payload instead of needing a broker.
public static class DeviceTopics
{
    public const string SchemaRaw = "raw";
    public const string SchemaJson = "json";

    public const string SourceManual = "manual";
    public const string SourceHomeAssistantDiscovery = "homeassistant-discovery";
    public const string SourceTasmotaDiscovery = "tasmota-discovery";
    public const string SourceTopicSweep = "topic-sweep";

    public static readonly IReadOnlyList<string> KnownSchemas = [SchemaRaw, SchemaJson];

    // "sensor" is display-only; everything else can be switched. Kept as data rather than an
    // enum so the metadata endpoint can hand the same list straight to every client's UI.
    public static readonly IReadOnlyList<string> KnownKinds =
        ["light", "switch", "outlet", "fan", "cover", "lock", "sensor"];

    // Vendors disagree on what to call the brightness key inside a JSON payload, and it isn't
    // worth a settings field the user would have to know the answer to - Zigbee2MQTT and Home
    // Assistant say "brightness", Tasmota says "Dimmer", and a few report a percentage directly.
    private static readonly string[] BrightnessKeys = ["brightness", "Dimmer", "brightness_pct"];

    public static bool IsCommandable(string kind) =>
        !string.Equals(kind, "sensor", StringComparison.OrdinalIgnoreCase);

    public static bool IsKnownKind(string kind) =>
        KnownKinds.Contains(kind, StringComparer.OrdinalIgnoreCase);

    public static bool IsKnownSchema(string schema) =>
        KnownSchemas.Contains(schema, StringComparer.OrdinalIgnoreCase);

    // A topic this app publishes to or subscribes for has to name exactly one topic. Wildcards
    // are legal in a subscription but never in a publish, and a command topic of "#" would
    // broadcast every switch command to every device on the broker - so they're refused on the
    // way in rather than discovered later.
    public static bool IsValidConcreteTopic(string topic) =>
        !string.IsNullOrWhiteSpace(topic) &&
        topic.Length <= 512 &&
        !topic.Contains('+', StringComparison.Ordinal) &&
        !topic.Contains('#', StringComparison.Ordinal) &&
        !topic.Contains('\0', StringComparison.Ordinal) &&
        topic.Trim().Length == topic.Length;

    // The API speaks 0-100 everywhere so the clients never have to care what the device's own
    // range is; only these two methods know about BrightnessScale.
    public static int ToDeviceBrightness(int percent, int scale)
    {
        var effectiveScale = scale <= 0 ? 255 : scale;
        var clamped = Math.Clamp(percent, 0, 100);
        return (int)Math.Round(clamped * effectiveScale / 100.0, MidpointRounding.AwayFromZero);
    }

    public static int ToPercentBrightness(double deviceValue, int scale)
    {
        var effectiveScale = scale <= 0 ? 255 : scale;
        var percent = (int)Math.Round(deviceValue * 100.0 / effectiveScale, MidpointRounding.AwayFromZero);
        return Math.Clamp(percent, 0, 100);
    }

    // Turns a resolved command into the publishes that carry it out. `power` null means "leave
    // the switch alone, only change brightness"; `brightnessPercent` null means the reverse.
    // Toggle is resolved to a concrete On/Off by the caller, which is the only place that knows
    // the current state.
    public static IReadOnlyList<DevicePublish> BuildCommands(HubDevice device, DevicePower? power, int? brightnessPercent)
    {
        var publishes = new List<DevicePublish>();
        var wantsJson = string.Equals(device.Schema, SchemaJson, StringComparison.OrdinalIgnoreCase);

        // Brightness rides the main command topic as another JSON key unless the device was
        // given a topic of its own. A raw payload can't carry two values at once, so a raw
        // device without a dedicated brightness topic simply has no way to express brightness -
        // which is exactly what ValidateDevice refuses to save.
        var hasDedicatedBrightnessTopic = !string.IsNullOrWhiteSpace(device.BrightnessCommandTopic);

        if (wantsJson)
        {
            var brightnessInBody = brightnessPercent is not null && !hasDedicatedBrightnessTopic;
            if (power is not null || brightnessInBody)
            {
                var body = new Dictionary<string, object>(StringComparer.Ordinal);
                if (power is not null)
                    body[StateKeyFor(device)] = power == DevicePower.On ? device.PayloadOn : device.PayloadOff;
                if (brightnessPercent is int percent && brightnessInBody)
                    body["brightness"] = ToDeviceBrightness(percent, device.BrightnessScale);

                publishes.Add(new DevicePublish(device.CommandTopic, JsonSerializer.Serialize(body), Retain: false));
            }
        }
        else if (power is not null)
        {
            var payload = power == DevicePower.On ? device.PayloadOn : device.PayloadOff;
            publishes.Add(new DevicePublish(device.CommandTopic, payload, Retain: false));
        }

        if (brightnessPercent is int brightness && hasDedicatedBrightnessTopic)
        {
            var value = ToDeviceBrightness(brightness, device.BrightnessScale);
            publishes.Add(new DevicePublish(
                device.BrightnessCommandTopic,
                value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Retain: false));
        }

        return publishes;
    }

    // Some MQTT ecosystems define a read command in addition to their retained state topic.
    // Only infer protocols whose query is unambiguous: publishing an empty payload to Tasmota's
    // POWER command asks for state, and Zigbee2MQTT's sibling /get topic accepts an empty state
    // value. Unknown/manual topic layouts deliberately produce nothing—a guessed publish to an
    // arbitrary command topic could change a real device instead of merely querying it.
    public static IReadOnlyList<DevicePublish> BuildStateQueries(HubDevice device)
    {
        if (!device.Enabled || string.IsNullOrWhiteSpace(device.CommandTopic)) return [];

        var command = device.CommandTopic;
        var isTasmota = string.Equals(device.DiscoverySource, SourceTasmotaDiscovery, StringComparison.OrdinalIgnoreCase) ||
                        (command.StartsWith("cmnd/", StringComparison.OrdinalIgnoreCase) &&
                         command.EndsWith("/POWER", StringComparison.OrdinalIgnoreCase));
        if (isTasmota)
            return [new DevicePublish(command, string.Empty, Retain: false)];

        if (command.StartsWith("zigbee2mqtt/", StringComparison.OrdinalIgnoreCase) &&
            command.EndsWith("/set", StringComparison.OrdinalIgnoreCase))
        {
            var getTopic = command[..^"/set".Length] + "/get";
            return [new DevicePublish(getTopic, "{\"state\":\"\"}", Retain: false)];
        }

        return [];
    }

    // Only the top-level key matters for building a command - a device that reports state at a
    // nested path still takes commands at the leaf name.
    private static string StateKeyFor(HubDevice device)
    {
        var template = string.IsNullOrWhiteSpace(device.StateValueTemplate) ? "state" : device.StateValueTemplate;
        var lastSeparator = template.LastIndexOf('.');
        return lastSeparator >= 0 && lastSeparator < template.Length - 1
            ? template[(lastSeparator + 1)..]
            : template;
    }

    // Reads a message that arrived on a device's state topic. Returns null when the payload is
    // unparseable, so the caller can leave the last known state alone rather than blanking a
    // working device because one malformed message came through.
    public static DeviceStateReading? ParseState(HubDevice device, string payload)
    {
        if (payload is null) return null;

        if (!string.Equals(device.Schema, SchemaJson, StringComparison.OrdinalIgnoreCase))
            return new DeviceStateReading(MatchPower(device, payload.Trim()), BrightnessPercent: null);

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(payload);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object) return null;

        var power = DevicePower.Unknown;
        if (TryResolvePath(root, string.IsNullOrWhiteSpace(device.StateValueTemplate) ? "state" : device.StateValueTemplate, out var stateElement))
        {
            power = stateElement.ValueKind switch
            {
                JsonValueKind.True => DevicePower.On,
                JsonValueKind.False => DevicePower.Off,
                JsonValueKind.String => MatchPower(device, stateElement.GetString()?.Trim() ?? string.Empty),
                JsonValueKind.Number => stateElement.GetDouble() > 0 ? DevicePower.On : DevicePower.Off,
                _ => DevicePower.Unknown
            };
        }

        int? brightnessPercent = null;
        foreach (var key in BrightnessKeys)
        {
            if (!root.TryGetProperty(key, out var element) || element.ValueKind != JsonValueKind.Number) continue;

            // brightness_pct is already a percentage, so scaling it again would halve it.
            brightnessPercent = key == "brightness_pct"
                ? Math.Clamp((int)Math.Round(element.GetDouble(), MidpointRounding.AwayFromZero), 0, 100)
                : ToPercentBrightness(element.GetDouble(), device.BrightnessScale);
            break;
        }

        return new DeviceStateReading(power, brightnessPercent);
    }

    // A brightness-only state topic (Tasmota's stat/<x>/Dimmer) carries a bare number.
    public static int? ParseBrightnessPayload(HubDevice device, string payload) =>
        double.TryParse(payload?.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? ToPercentBrightness(value, device.BrightnessScale)
            : null;

    public static bool? ParseAvailability(HubDevice device, string payload)
    {
        var trimmed = payload?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(device.PayloadAvailable) &&
            string.Equals(trimmed, device.PayloadAvailable, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrEmpty(device.PayloadNotAvailable) &&
            string.Equals(trimmed, device.PayloadNotAvailable, StringComparison.OrdinalIgnoreCase))
            return false;
        return null;
    }

    private static DevicePower MatchPower(HubDevice device, string value)
    {
        if (string.Equals(value, device.PayloadOn, StringComparison.OrdinalIgnoreCase)) return DevicePower.On;
        if (string.Equals(value, device.PayloadOff, StringComparison.OrdinalIgnoreCase)) return DevicePower.Off;
        return DevicePower.Unknown;
    }

    private static bool TryResolvePath(JsonElement root, string path, out JsonElement result)
    {
        result = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(segment, out var next))
                return false;
            result = next;
        }
        return true;
    }
}
