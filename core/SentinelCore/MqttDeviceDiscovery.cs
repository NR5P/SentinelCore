using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;

namespace SentinelCore;

public enum DeviceScanStatus { Running, Completed }

// A device the scan believes is out there, with as many fields pre-filled as its announcement
// allowed. Never saved on its own - the user picks which candidates become real devices, exactly
// like the camera network scan. AlreadyAdded lets the UI grey out ones already in the hub.
public sealed record DeviceCandidate(
    string SuggestedName,
    string Kind,
    string CommandTopic,
    string StateTopic,
    string AvailabilityTopic,
    string PayloadOn,
    string PayloadOff,
    string Schema,
    string StateValueTemplate,
    bool SupportsBrightness,
    string BrightnessCommandTopic,
    string BrightnessStateTopic,
    int BrightnessScale,
    string Source,
    bool AlreadyAdded);

public sealed record DeviceScanSnapshot(DeviceScanStatus Status, IReadOnlyList<DeviceCandidate> Results);

// The "discover" button's engine: connects to the same broker, listens for a fixed window, and
// reports what looks like a controllable device. Three strategies run in the one job because they
// catch different populations - some devices announce themselves via Home Assistant discovery,
// Tasmota's own firmware announces itself in a different format entirely, and older or
// hand-rolled ones only ever appear as bare topics.
//
// Job/poll shape is lifted from CameraDiscoveryService so every client can reuse the polling it
// already does for camera network scans.
public sealed class MqttDeviceDiscovery
{
    private static readonly TimeSpan JobRetention = TimeSpan.FromMinutes(10);

    private readonly IOptionsMonitor<MqttOptions> _mqttOptions;
    private readonly IOptionsMonitor<DeviceHubOptions> _hubOptions;
    private readonly IDeviceStore _store;
    private readonly ILogger<MqttDeviceDiscovery> _logger;
    private readonly ConcurrentDictionary<Guid, ScanJob> _scans = new();

    public MqttDeviceDiscovery(
        IOptionsMonitor<MqttOptions> mqttOptions,
        IOptionsMonitor<DeviceHubOptions> hubOptions,
        IDeviceStore store,
        ILogger<MqttDeviceDiscovery> logger)
    {
        _mqttOptions = mqttOptions;
        _hubOptions = hubOptions;
        _store = store;
        _logger = logger;
    }

    public Guid StartScan()
    {
        PruneStaleJobs();

        var jobId = Guid.NewGuid();
        var job = new ScanJob();
        _scans[jobId] = job;

        _ = RunScanAsync(job);

        return jobId;
    }

    public DeviceScanSnapshot? GetScanStatus(Guid jobId) =>
        _scans.TryGetValue(jobId, out var job) ? new DeviceScanSnapshot(job.Status, job.Results.ToList()) : null;

    private async Task RunScanAsync(ScanJob job)
    {
        IMqttClient? client = null;
        try
        {
            var mqtt = _mqttOptions.CurrentValue;
            var window = TimeSpan.FromSeconds(_hubOptions.CurrentValue.DiscoverySeconds);

            var existing = await _store.GetAllAsync(CancellationToken.None);
            var existingCommandTopics = existing
                .Select(device => device.CommandTopic)
                .Where(topic => !string.IsNullOrWhiteSpace(topic))
                .ToHashSet(StringComparer.Ordinal);
            var existingStateTopics = existing
                .Select(device => device.StateTopic)
                .Where(topic => !string.IsNullOrWhiteSpace(topic))
                .ToHashSet(StringComparer.Ordinal);

            var discoveryConfigs = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            var seenTopics = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

            client = new MqttClientFactory().CreateMqttClient();
            client.ApplicationMessageReceivedAsync += args =>
            {
                var topic = args.ApplicationMessage.Topic;
                if (topic.EndsWith("/config", StringComparison.Ordinal))
                {
                    var payload = args.ApplicationMessage.ConvertPayloadToString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(payload)) discoveryConfigs[topic] = payload;
                }
                else
                {
                    seenTopics.TryAdd(topic, 0);
                }
                return Task.CompletedTask;
            };

            var clientOptions = MqttConnection
                .CreateBuilder(mqtt, $"sentinel-core-discovery-{Guid.NewGuid():N}")
                .Build();
            await client.ConnectAsync(clientOptions, CancellationToken.None);

            // "#" catches both strategies at once: retained Home Assistant discovery configs
            // arrive immediately on subscribe, and everything else accumulates over the window.
            await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(filter => filter
                    .WithTopic("#")
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce))
                .Build(), CancellationToken.None);

            await Task.Delay(window, CancellationToken.None);

            foreach (var candidate in BuildCandidates(
                discoveryConfigs, seenTopics.Keys, mqtt.HomeAssistantDiscoveryPrefix, existingCommandTopics, existingStateTopics))
            {
                job.Results.Add(candidate);
            }

            _logger.LogInformation(
                "MQTT device discovery found {CandidateCount} candidate(s) from {ConfigCount} discovery config(s) and {TopicCount} topic(s)",
                job.Results.Count, discoveryConfigs.Count, seenTopics.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MQTT device discovery scan failed");
        }
        finally
        {
            if (client is not null)
            {
                try
                {
                    if (client.IsConnected) await client.DisconnectAsync();
                }
                catch
                {
                    // The scan's results are already collected - a failed disconnect changes nothing.
                }
                client.Dispose();
            }

            job.Status = DeviceScanStatus.Completed;
        }
    }

    // Public and static so the whole heuristic is testable against captured payloads without a
    // broker anywhere in the picture - the same reason MqttTopics is split out of MqttPublisher.
    public static IReadOnlyList<DeviceCandidate> BuildCandidates(
        IReadOnlyDictionary<string, string> discoveryConfigs,
        IEnumerable<string> observedTopics,
        string discoveryPrefix,
        IReadOnlySet<string> existingCommandTopics,
        IReadOnlySet<string> existingStateTopics)
    {
        var candidates = new List<DeviceCandidate>();
        var claimedTopics = new HashSet<string>(StringComparer.Ordinal);

        void Claim(DeviceCandidate candidate)
        {
            candidates.Add(candidate);
            claimedTopics.Add(candidate.CommandTopic);
            claimedTopics.Add(candidate.StateTopic);
        }

        // Both formats land on a topic ending in /config, so which parser applies is decided by
        // the rest of the topic rather than by asking the user which firmware they run.
        foreach (var (topic, payload) in discoveryConfigs)
        {
            if (IsDiscoveryConfigTopic(topic, discoveryPrefix))
            {
                if (ParseHomeAssistantConfig(topic, payload, existingCommandTopics) is { } candidate)
                    Claim(candidate);
            }
            else if (IsTasmotaDiscoveryTopic(topic))
            {
                foreach (var candidate in ParseTasmotaDiscoveryConfig(payload, existingCommandTopics))
                    Claim(candidate);
            }
        }

        // Anything already described by a discovery config is skipped here - a Tasmota device
        // that announces itself properly shouldn't also show up as a raw cmnd/stat guess.
        foreach (var candidate in SweepTopics(observedTopics, claimedTopics, existingCommandTopics, existingStateTopics))
            candidates.Add(candidate);

        return candidates
            .GroupBy(candidate => candidate.CommandTopic, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.SuggestedName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // homeassistant/<component>/<node>/config or homeassistant/<component>/<node>/<object>/config
    private static bool IsDiscoveryConfigTopic(string topic, string discoveryPrefix)
    {
        if (!topic.EndsWith("/config", StringComparison.Ordinal)) return false;
        var segments = topic.Split('/');
        return segments.Length is 4 or 5 &&
               string.Equals(segments[0], discoveryPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static DeviceCandidate? ParseHomeAssistantConfig(
        string topic, string payload, IReadOnlySet<string> existingCommandTopics)
    {
        var component = topic.Split('/')[1];
        var kind = MapComponentToKind(component);
        if (kind is null) return null;

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

        // "~" is Home Assistant's abbreviation for a shared topic base, and the abbreviated key
        // names (cmd_t, stat_t, avty_t) are what most real firmware actually publishes.
        var basePrefix = ReadString(root, "~") ?? string.Empty;
        var commandTopic = Expand(ReadString(root, "command_topic", "cmd_t"), basePrefix);
        var stateTopic = Expand(ReadString(root, "state_topic", "stat_t"), basePrefix);
        var availabilityTopic = Expand(ReadString(root, "availability_topic", "avty_t"), basePrefix);

        // A sensor has no command topic and that's fine; anything else without one can't be
        // controlled, so there's nothing for the Device Hub to offer.
        var commandable = DeviceTopics.IsCommandable(kind);
        if (commandable && string.IsNullOrWhiteSpace(commandTopic)) return null;
        if (!commandable && string.IsNullOrWhiteSpace(stateTopic)) return null;

        var name = ReadString(root, "name", "dev_cla") ?? DeriveNameFromTopic(commandTopic ?? stateTopic ?? topic);
        var schemaField = ReadString(root, "schema", "schema");
        var isJsonSchema = string.Equals(schemaField, "json", StringComparison.OrdinalIgnoreCase);

        var brightnessCommandTopic = Expand(ReadString(root, "brightness_command_topic", "bri_cmd_t"), basePrefix);
        var brightnessStateTopic = Expand(ReadString(root, "brightness_state_topic", "bri_stat_t"), basePrefix);
        var brightnessScale = ReadInt(root, "brightness_scale", "bri_scl") ?? 255;
        var supportsBrightness = isJsonSchema
            ? string.Equals(kind, "light", StringComparison.Ordinal)
            : !string.IsNullOrWhiteSpace(brightnessCommandTopic);

        return new DeviceCandidate(
            SuggestedName: name,
            Kind: kind,
            CommandTopic: commandTopic ?? string.Empty,
            StateTopic: stateTopic ?? string.Empty,
            AvailabilityTopic: availabilityTopic ?? string.Empty,
            PayloadOn: ReadString(root, "payload_on", "pl_on") ?? "ON",
            PayloadOff: ReadString(root, "payload_off", "pl_off") ?? "OFF",
            Schema: isJsonSchema ? DeviceTopics.SchemaJson : DeviceTopics.SchemaRaw,
            StateValueTemplate: isJsonSchema ? "state" : string.Empty,
            SupportsBrightness: supportsBrightness,
            BrightnessCommandTopic: brightnessCommandTopic ?? string.Empty,
            BrightnessStateTopic: brightnessStateTopic ?? string.Empty,
            BrightnessScale: brightnessScale,
            Source: DeviceTopics.SourceHomeAssistantDiscovery,
            AlreadyAdded: commandTopic is not null && existingCommandTopics.Contains(commandTopic));
    }

    // tasmota/discovery/<mac>/config - a fixed prefix, unlike Home Assistant's configurable one,
    // because Tasmota doesn't let it be changed.
    private static bool IsTasmotaDiscoveryTopic(string topic)
    {
        var segments = topic.Split('/');
        return segments.Length == 4 &&
               string.Equals(segments[0], "tasmota", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(segments[1], "discovery", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(segments[3], "config", StringComparison.Ordinal);
    }

    // Tasmota's native discovery (SetOption19 0 - the default, and what a device that has never
    // been paired with Home Assistant publishes). It describes the whole device rather than one
    // entity, and crucially never states its topics outright: they have to be assembled from the
    // FullTopic template. A switch with a customised FullTopic - "office/%topic%/%prefix%/"
    // instead of the stock "%prefix%/%topic%/" - is invisible to every other strategy here,
    // because the sweep's hardcoded "stat/" prefix will never match it.
    private static List<DeviceCandidate> ParseTasmotaDiscoveryConfig(
        string payload, IReadOnlySet<string> existingCommandTopics)
    {
        var candidates = new List<DeviceCandidate>();

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(payload);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return candidates;
        }

        if (root.ValueKind != JsonValueKind.Object) return candidates;

        var topic = ReadString(root, "t");
        var fullTopic = ReadString(root, "ft");
        if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(fullTopic)) return candidates;

        // tp is [cmnd, stat, tele]. All three are user-settable, so none can be assumed.
        var prefixes = ReadStringArray(root, "tp");
        if (prefixes.Count < 3 || prefixes.Take(3).Any(string.IsNullOrWhiteSpace)) return candidates;

        var hostname = ReadString(root, "hn") ?? string.Empty;
        var mac = ReadString(root, "mac") ?? string.Empty;
        var commandBase = ExpandFullTopic(fullTopic!, prefixes[0]!, topic!, hostname, mac);
        var stateBase = ExpandFullTopic(fullTopic!, prefixes[1]!, topic!, hostname, mac);
        var telemetryBase = ExpandFullTopic(fullTopic!, prefixes[2]!, topic!, hostname, mac);

        // state is [off, on, toggle, hold] - a device can be taught to answer to something other
        // than ON/OFF, and it ignores any payload it wasn't taught.
        var states = ReadStringArray(root, "state");
        var payloadOff = states.Count > 0 && !string.IsNullOrWhiteSpace(states[0]) ? states[0]! : "OFF";
        var payloadOn = states.Count > 1 && !string.IsNullOrWhiteSpace(states[1]) ? states[1]! : "ON";

        // rl marks which of the 32 relay slots are populated, and a slot set to 2 drives a light
        // rather than a plain relay. lt_st is the light subtype: non-zero means there's a Dimmer.
        var relays = ReadIntArray(root, "rl");
        var lightSubType = ReadInt(root, "lt_st") ?? 0;
        var friendlyNames = ReadStringArray(root, "fn");
        var deviceName = ReadString(root, "dn") ?? (string.IsNullOrWhiteSpace(hostname) ? "Tasmota" : hostname);

        var activeRelays = relays
            .Select((value, index) => (Value: value, Index: index))
            .Where(relay => relay.Value != 0)
            .ToList();

        foreach (var (value, index) in activeRelays)
        {
            // A single-relay device answers on POWER; a multi-relay one on POWER1..POWERn.
            var suffix = activeRelays.Count > 1
                ? (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty;
            var command = "POWER" + suffix;

            var friendlyName = friendlyNames.ElementAtOrDefault(index);
            var name = !string.IsNullOrWhiteSpace(friendlyName) ? friendlyName! : deviceName;
            if (activeRelays.Count > 1 && string.Equals(name, deviceName, StringComparison.Ordinal))
                name = $"{deviceName} {index + 1}";

            var isLight = lightSubType > 0 || value == 2;
            var commandTopic = commandBase + command;

            candidates.Add(new DeviceCandidate(
                SuggestedName: name,
                Kind: isLight ? "light" : "switch",
                CommandTopic: commandTopic,
                StateTopic: stateBase + command,
                AvailabilityTopic: telemetryBase + "LWT",
                PayloadOn: payloadOn,
                PayloadOff: payloadOff,
                Schema: DeviceTopics.SchemaRaw,
                StateValueTemplate: string.Empty,
                SupportsBrightness: lightSubType > 0,
                BrightnessCommandTopic: lightSubType > 0 ? commandBase + "Dimmer" : string.Empty,
                // Tasmota reports the dimmer level inside the stat/.../RESULT JSON blob rather
                // than on a topic of its own, which is not a bare number and so nothing this app
                // can read a level off. Commands still work; only the readback is missing.
                BrightnessStateTopic: string.Empty,
                // Tasmota's Dimmer is already a percentage, unlike Home Assistant's 0-255.
                BrightnessScale: 100,
                Source: DeviceTopics.SourceTasmotaDiscovery,
                AlreadyAdded: existingCommandTopics.Contains(commandTopic)));
        }

        return candidates;
    }

    // Tasmota's FullTopic is a template, not a topic - "%prefix%/%topic%/" out of the box, but
    // freely rearranged by the user. %id% is the last six digits of the MAC.
    private static string ExpandFullTopic(
        string fullTopic, string prefix, string topic, string hostname, string mac)
    {
        var expanded = fullTopic
            .Replace("%prefix%", prefix, StringComparison.OrdinalIgnoreCase)
            .Replace("%topic%", topic, StringComparison.OrdinalIgnoreCase)
            .Replace("%hostname%", hostname, StringComparison.OrdinalIgnoreCase)
            .Replace("%id%", mac.Length >= 6 ? mac[^6..] : mac, StringComparison.OrdinalIgnoreCase);

        // Tasmota keeps a trailing separator so the command name can simply be appended, but a
        // hand-edited FullTopic doesn't always have one.
        return expanded.EndsWith('/') ? expanded : expanded + "/";
    }

    // The fallback for devices that never announce themselves: pair up the command and state
    // topics that the common firmwares publish under predictable names. Tasmota's
    // cmnd/<name>/POWER + stat/<name>/POWER is the overwhelmingly common case, with Zigbee2MQTT's
    // <base>/<name>/set + <base>/<name> second.
    private static IEnumerable<DeviceCandidate> SweepTopics(
        IEnumerable<string> observedTopics,
        IReadOnlySet<string> claimedTopics,
        IReadOnlySet<string> existingCommandTopics,
        IReadOnlySet<string> existingStateTopics)
    {
        var topics = observedTopics.ToHashSet(StringComparer.Ordinal);

        foreach (var topic in topics.OrderBy(t => t, StringComparer.Ordinal))
        {
            if (claimedTopics.Contains(topic)) continue;

            // Tasmota: a stat/<name>/POWER seen on the wire implies cmnd/<name>/POWER accepts
            // commands, even though nothing ever publishes to a command topic for us to observe.
            if (topic.StartsWith("stat/", StringComparison.Ordinal) &&
                topic.EndsWith("/POWER", StringComparison.OrdinalIgnoreCase))
            {
                var commandTopic = "cmnd/" + topic["stat/".Length..];
                if (claimedTopics.Contains(commandTopic)) continue;

                var dimmerCommandTopic = commandTopic[..^"POWER".Length] + "Dimmer";
                var dimmerStateTopic = topic[..^"POWER".Length] + "Dimmer";
                var hasDimmer = topics.Contains(dimmerStateTopic);

                yield return new DeviceCandidate(
                    SuggestedName: DeriveNameFromTopic(topic),
                    Kind: hasDimmer ? "light" : "switch",
                    CommandTopic: commandTopic,
                    StateTopic: topic,
                    AvailabilityTopic: "tele/" + topic["stat/".Length..^"/POWER".Length] + "/LWT",
                    PayloadOn: "ON",
                    PayloadOff: "OFF",
                    Schema: DeviceTopics.SchemaRaw,
                    StateValueTemplate: string.Empty,
                    SupportsBrightness: hasDimmer,
                    BrightnessCommandTopic: hasDimmer ? dimmerCommandTopic : string.Empty,
                    BrightnessStateTopic: hasDimmer ? dimmerStateTopic : string.Empty,
                    // Tasmota's Dimmer is already a percentage, unlike Home Assistant's 0-255.
                    BrightnessScale: 100,
                    Source: DeviceTopics.SourceTopicSweep,
                    AlreadyAdded: existingCommandTopics.Contains(commandTopic));
                continue;
            }

            // Zigbee2MQTT: the device publishes state on <base>/<friendly name> and takes
            // commands on the same path with /set appended. Only the state topic is observable.
            if (topic.StartsWith("zigbee2mqtt/", StringComparison.OrdinalIgnoreCase) &&
                !topic.EndsWith("/set", StringComparison.OrdinalIgnoreCase) &&
                !topic.EndsWith("/get", StringComparison.OrdinalIgnoreCase) &&
                !topic.Contains("/bridge/", StringComparison.OrdinalIgnoreCase) &&
                topic.Split('/').Length == 2 &&
                !existingStateTopics.Contains(topic))
            {
                yield return new DeviceCandidate(
                    SuggestedName: DeriveNameFromTopic(topic),
                    Kind: "light",
                    CommandTopic: topic + "/set",
                    StateTopic: topic,
                    AvailabilityTopic: topic + "/availability",
                    PayloadOn: "ON",
                    PayloadOff: "OFF",
                    Schema: DeviceTopics.SchemaJson,
                    StateValueTemplate: "state",
                    SupportsBrightness: true,
                    BrightnessCommandTopic: string.Empty,
                    BrightnessStateTopic: string.Empty,
                    BrightnessScale: 254,
                    Source: DeviceTopics.SourceTopicSweep,
                    AlreadyAdded: existingCommandTopics.Contains(topic + "/set"));
            }
        }
    }

    private static string? MapComponentToKind(string component) => component.ToLowerInvariant() switch
    {
        "light" => "light",
        "switch" => "switch",
        "fan" => "fan",
        "cover" => "cover",
        "lock" => "lock",
        "binary_sensor" or "sensor" => "sensor",
        _ => null
    };

    private static string? Expand(string? topic, string basePrefix)
    {
        if (string.IsNullOrWhiteSpace(topic)) return null;
        if (string.IsNullOrEmpty(basePrefix)) return topic;
        return topic.Replace("~", basePrefix, StringComparison.Ordinal);
    }

    private static string? ReadString(JsonElement root, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (root.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        return null;
    }

    private static int? ReadInt(JsonElement root, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (root.TryGetProperty(key, out var element) &&
                element.ValueKind == JsonValueKind.Number &&
                element.TryGetInt32(out var value))
            {
                return value;
            }
        }
        return null;
    }

    // Position is meaningful in every array Tasmota publishes - fn[2] is the name of relay 3 -
    // so entries that aren't strings become nulls in place rather than being skipped.
    private static List<string?> ReadStringArray(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var element) || element.ValueKind != JsonValueKind.Array)
            return [];

        return element.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
            .ToList();
    }

    private static List<int> ReadIntArray(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var element) || element.ValueKind != JsonValueKind.Array)
            return [];

        return element.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var value) ? value : 0)
            .ToList();
    }

    // "stat/kitchen_lamp/POWER" -> "Kitchen Lamp". Only ever a suggestion the user can edit.
    private static string DeriveNameFromTopic(string topic)
    {
        var segments = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var meaningful = segments
            .Where(segment => segment is not ("stat" or "cmnd" or "tele" or "POWER" or "set" or "state" or "zigbee2mqtt"))
            .ToArray();
        var chosen = meaningful.LastOrDefault() ?? segments.LastOrDefault() ?? "Device";

        var words = chosen.Split(['_', '-', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Length <= 1 ? word.ToUpperInvariant() : char.ToUpperInvariant(word[0]) + word[1..]);
        var name = string.Join(' ', words).Trim();
        return string.IsNullOrEmpty(name) ? "Device" : name;
    }

    private void PruneStaleJobs()
    {
        var cutoff = DateTimeOffset.UtcNow - JobRetention;
        foreach (var (id, job) in _scans)
        {
            if (job.Status == DeviceScanStatus.Completed && job.StartedAt < cutoff)
                _scans.TryRemove(id, out _);
        }
    }

    private sealed class ScanJob
    {
        public DeviceScanStatus Status { get; set; } = DeviceScanStatus.Running;
        public List<DeviceCandidate> Results { get; } = [];
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    }
}
