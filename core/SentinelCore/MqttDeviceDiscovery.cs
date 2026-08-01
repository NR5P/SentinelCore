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
// reports what looks like a controllable device. Two strategies run in the one job because they
// catch different populations - well-behaved devices announce themselves via Home Assistant
// discovery, while older or hand-rolled ones only ever appear as bare topics.
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

            var haConfigs = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            var seenTopics = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

            client = new MqttClientFactory().CreateMqttClient();
            client.ApplicationMessageReceivedAsync += args =>
            {
                var topic = args.ApplicationMessage.Topic;
                if (topic.EndsWith("/config", StringComparison.Ordinal))
                {
                    var payload = args.ApplicationMessage.ConvertPayloadToString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(payload)) haConfigs[topic] = payload;
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
                haConfigs, seenTopics.Keys, mqtt.HomeAssistantDiscoveryPrefix, existingCommandTopics, existingStateTopics))
            {
                job.Results.Add(candidate);
            }

            _logger.LogInformation(
                "MQTT device discovery found {CandidateCount} candidate(s) from {ConfigCount} discovery config(s) and {TopicCount} topic(s)",
                job.Results.Count, haConfigs.Count, seenTopics.Count);
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
        IReadOnlyDictionary<string, string> homeAssistantConfigs,
        IEnumerable<string> observedTopics,
        string discoveryPrefix,
        IReadOnlySet<string> existingCommandTopics,
        IReadOnlySet<string> existingStateTopics)
    {
        var candidates = new List<DeviceCandidate>();
        var claimedTopics = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (topic, payload) in homeAssistantConfigs)
        {
            if (!IsDiscoveryConfigTopic(topic, discoveryPrefix)) continue;
            if (ParseHomeAssistantConfig(topic, payload, existingCommandTopics) is not { } candidate) continue;

            candidates.Add(candidate);
            claimedTopics.Add(candidate.CommandTopic);
            claimedTopics.Add(candidate.StateTopic);
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
