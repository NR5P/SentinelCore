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
//
// Measurements are the numeric readings the payload carried, keyed by the payload key that held
// them - "temperature", "battery", "ENERGY.Power". They are harvested from whatever the device
// happened to publish rather than declared in advance, deliberately: one Zigbee2MQTT sensor
// reports five or six of these in a single message, and requiring someone to name each one
// before any of them appear would make the common device the most work to set up. The cost is a
// few keys nobody cares about (linkquality, voltage), which a display preference can hide later.
public sealed record DeviceStateReading(
    DevicePower Power,
    int? BrightnessPercent,
    IReadOnlyDictionary<string, double> Measurements)
{
    public static readonly IReadOnlyDictionary<string, double> NoMeasurements =
        new Dictionary<string, double>(0, StringComparer.Ordinal);
}

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
    // gate/garage are cover-like (open/close/stop wording) and "button" is a stateless
    // momentary press - a doorbell, a garage remote, a scene trigger.
    public static readonly IReadOnlyList<string> KnownKinds =
        ["light", "switch", "outlet", "fan", "cover", "gate", "garage", "lock", "button", "sensor"];

    // Vendors disagree on what to call the brightness key inside a JSON payload, and it isn't
    // worth a settings field the user would have to know the answer to - Zigbee2MQTT and Home
    // Assistant say "brightness", Tasmota says "Dimmer", and a few report a percentage directly.
    private static readonly string[] BrightnessKeys = ["brightness", "Dimmer", "brightness_pct"];

    // A ceiling on how many numbers one state message can contribute. Real devices report a
    // handful; this only exists so a misconfigured topic pointing at something enormous can't
    // turn every cached device state into a large allocation.
    private const int MaxMeasurements = 32;

    public static bool IsCommandable(string kind) =>
        !string.Equals(kind, "sensor", StringComparison.OrdinalIgnoreCase);

    // Kinds that speak open/close rather than on/off. Their PayloadOn/PayloadOff carry the open
    // and close payloads (OPEN/CLOSE for a Home Assistant cover), and PayloadStop - when set -
    // adds a third "stop" command for motors that can halt mid-travel.
    public static bool IsCoverLike(string kind) =>
        kind.ToLowerInvariant() is "cover" or "gate" or "garage";

    // A "button" kind is momentary by nature; anything else can be marked Momentary explicitly
    // (a gate relay wired to pulse, for instance). A momentary device has exactly one command -
    // publish PayloadOn - and no meaningful on/off state to track afterwards.
    public static bool IsMomentary(HubDevice device) =>
        device.Momentary || string.Equals(device.Kind, "button", StringComparison.OrdinalIgnoreCase);

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

    // The third command a cover-like device can take: halt mid-travel. Only exists when the
    // device was given a stop payload; the empty list tells the endpoint to refuse rather than
    // guess at a payload the motor was never taught.
    public static IReadOnlyList<DevicePublish> BuildStopCommand(HubDevice device)
    {
        if (string.IsNullOrWhiteSpace(device.PayloadStop) || string.IsNullOrWhiteSpace(device.CommandTopic))
            return [];

        var wantsJson = string.Equals(device.Schema, SchemaJson, StringComparison.OrdinalIgnoreCase);
        var payload = wantsJson
            ? JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
              {
                  [StateKeyFor(device)] = device.PayloadStop
              })
            : device.PayloadStop;

        return [new DevicePublish(device.CommandTopic, payload, Retain: false)];
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
        {
            var trimmed = payload.Trim();
            var rawPower = MatchPower(device, trimmed);

            // "raw" describes how the on/off payload is written; it is not a promise that the
            // device only ever publishes bare strings - and a sensor, having no on/off payload,
            // gives it nothing to describe at all. This matters because Home Assistant discovery
            // hands every `sensor` component this schema ("schema" is a light-only field in that
            // spec), so the most common real sensor there is - a Zigbee2MQTT climate device that
            // announces itself and then publishes {"temperature":21.5,...} - arrives here rather
            // than on the JSON path below. Reading the payload for what it plainly is beats
            // making the user correct a schema that discovery had no way to get right.
            if (TryParseObject(trimmed, out var rawRoot))
            {
                return new DeviceStateReading(
                    rawPower,
                    BrightnessPercent: null,
                    HarvestMeasurements(rawRoot, device.StateValueTemplate ?? string.Empty, brightnessKey: null));
            }

            // Otherwise the entire payload is the reading - "21.5" arriving on a temperature
            // topic. Keyed "value" because the topic is the only name that reading has. A payload
            // that already matched PayloadOn/PayloadOff is a switch state, not a measurement, so
            // a device using "1"/"0" for on/off doesn't also sprout a meaningless number.
            var rawMeasurements = rawPower == DevicePower.Unknown && TryParseNumber(trimmed, out var single)
                ? new Dictionary<string, double>(StringComparer.Ordinal) { ["value"] = single }
                : DeviceStateReading.NoMeasurements;

            return new DeviceStateReading(rawPower, BrightnessPercent: null, rawMeasurements);
        }

        if (!TryParseObject(payload, out var root)) return null;

        var power = DevicePower.Unknown;
        if (TryResolvePath(root, string.IsNullOrWhiteSpace(device.StateValueTemplate) ? "state" : device.StateValueTemplate, out var stateElement))
        {
            // A boolean is "on" unless the device's payloads say otherwise: a Zigbee2MQTT door
            // contact reports contact:true for CLOSED, and its discovery says payload_on is
            // false, so the payloads win whenever they name a boolean at all.
            power = stateElement.ValueKind switch
            {
                JsonValueKind.True => MatchPower(device, "true") is var t && t != DevicePower.Unknown ? t : DevicePower.On,
                JsonValueKind.False => MatchPower(device, "false") is var f && f != DevicePower.Unknown ? f : DevicePower.Off,
                JsonValueKind.String => MatchPower(device, stateElement.GetString()?.Trim() ?? string.Empty),
                JsonValueKind.Number => MatchPower(device, stateElement.GetRawText()) is var n && n != DevicePower.Unknown
                    ? n
                    : stateElement.GetDouble() > 0 ? DevicePower.On : DevicePower.Off,
                _ => DevicePower.Unknown
            };
        }

        int? brightnessPercent = null;
        string? brightnessKey = null;
        foreach (var key in BrightnessKeys)
        {
            if (!root.TryGetProperty(key, out var element) || element.ValueKind != JsonValueKind.Number) continue;

            // brightness_pct is already a percentage, so scaling it again would halve it.
            brightnessPercent = key == "brightness_pct"
                ? Math.Clamp((int)Math.Round(element.GetDouble(), MidpointRounding.AwayFromZero), 0, 100)
                : ToPercentBrightness(element.GetDouble(), device.BrightnessScale);
            brightnessKey = key;
            break;
        }

        var statePath = string.IsNullOrWhiteSpace(device.StateValueTemplate) ? "state" : device.StateValueTemplate;
        return new DeviceStateReading(power, brightnessPercent, HarvestMeasurements(root, statePath, brightnessKey));
    }

    // Every numeric value in a state payload, minus the two that already have a home: the key
    // holding on/off and the key holding brightness would otherwise be reported twice, once as
    // the thing they are and once as an anonymous number.
    private static IReadOnlyDictionary<string, double> HarvestMeasurements(
        JsonElement root, string statePath, string? brightnessKey)
    {
        var measurements = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var property in root.EnumerateObject())
        {
            if (measurements.Count >= MaxMeasurements) break;
            if (brightnessKey is not null && string.Equals(property.Name, brightnessKey, StringComparison.Ordinal))
                continue;

            if (property.Value.ValueKind == JsonValueKind.Number)
            {
                if (string.Equals(property.Name, statePath, StringComparison.Ordinal)) continue;
                if (property.Value.TryGetDouble(out var value)) measurements[property.Name] = value;
                continue;
            }

            // One level down and no further. Tasmota buries its power meter in
            // {"ENERGY":{"Power":42,"Total":1.2}}, which is worth reaching, but an unbounded walk
            // of an arbitrary payload is an unbounded amount of junk to render.
            if (property.Value.ValueKind != JsonValueKind.Object) continue;

            foreach (var nested in property.Value.EnumerateObject())
            {
                if (measurements.Count >= MaxMeasurements) break;
                if (nested.Value.ValueKind != JsonValueKind.Number) continue;

                var key = $"{property.Name}.{nested.Name}";
                if (string.Equals(key, statePath, StringComparison.Ordinal)) continue;
                if (nested.Value.TryGetDouble(out var value)) measurements[key] = value;
            }
        }

        return measurements.Count == 0 ? DeviceStateReading.NoMeasurements : measurements;
    }

    // A JSON object, or nothing. The leading-brace check is a cheap gate before paying for a
    // parse: almost every raw payload in existence is "ON" or a bare number, and neither is
    // worth a JsonDocument to rule out.
    private static bool TryParseObject(string payload, out JsonElement root)
    {
        root = default;

        var trimmed = payload.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '{') return false;

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseNumber(string value, out double result) =>
        double.TryParse(
            value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out result);

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
