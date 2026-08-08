using System.Net.Http.Json;
using MQTTnet;
using MQTTnet.Protocol;
using SentinelCore;

namespace SentinelCore.Web;

// Fires a camera when a Device Hub sensor reports - a proximity sensor by the gate, a PIR in the
// barn, a contact switch on a shed door - so something that isn't a camera can still put eyes on
// a camera. The binding lives on the camera's own trigger config (TriggerConfig.SensorDeviceId),
// not in a rules table of its own: it's one dropdown on the camera that already owns the cooldown
// and the prompt profile this would otherwise have to duplicate.
//
// This is the second MQTT subscriber in the product and the first outside the main app, which
// deserves justifying. The devices, their topics, and the hub's own subscription all live over in
// SentinelCore (see MqttDeviceBus) - but the cameras, the snapshots, and the trigger pipeline all
// live here, and no push channel from that app to this one exists. The alternatives were polling
// /api/devices/state on a timer (seconds of lag on something whose whole value is immediacy) or
// inventing a main-app-to-web callback with its own URL and shared secret. A read-only
// subscription to the handful of topics belonging to bound sensors is less machinery than either,
// and it reuses that project's own MqttConnection/DeviceTopics so there's no second definition of
// how a broker is reached or how a payload is read. Device *definitions* still come from the main
// app over HTTP - this never touches the devices database.
public sealed class SensorTriggerWatcher(
    ICameraStore cameras,
    ITriggerConfigStore configStore,
    CameraTriggerService triggers,
    AppSettingsFile mainAppSettings,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<SensorTriggerWatcher> logger) : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(15);

    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();

    // topic -> the sensors listening on it. One topic can legitimately drive more than one camera.
    private volatile Dictionary<string, List<SensorBinding>> _bindings = new(StringComparer.Ordinal);

    // What we last saw for each binding, so a sensor that republishes "ON" (or a distance that
    // stays under the threshold) every 30 seconds fires once on the change rather than every time
    // it repeats itself. A binding missing from here has no baseline yet - see OnMessageAsync for
    // why that first message never fires anything.
    //
    // Keyed per binding rather than per device: two cameras can watch the same sensor with
    // different thresholds, and one crossing its line says nothing about whether the other did.
    private readonly Dictionary<BindingKey, DevicePower> _lastPower = [];
    private readonly Dictionary<BindingKey, bool> _lastSatisfied = [];

    // Bindings we've already complained about naming a reading the sensor doesn't publish. The
    // warning is worth making loudly once; repeating it for every message on a chatty topic would
    // bury the log.
    private readonly HashSet<BindingKey> _warnedMissingMeasurement = [];
    private readonly object _stateLock = new();

    // The condition is part of the identity, not just the camera and device. Editing a threshold
    // from "below 100" to "below 200" produces a different key, so the new condition re-baselines
    // instead of being compared against what the old one had decided - otherwise moving the line
    // past a stationary reading fires the camera on the save, with nothing having happened.
    // Reconcile prunes keys that no longer correspond to a live binding.
    private readonly record struct BindingKey(
        Guid CameraId, Guid DeviceId, string TriggerOn, string Measurement, double Threshold);

    private HashSet<string> _subscribedTopics = new(StringComparer.Ordinal);
    private CancellationToken _stoppingToken = CancellationToken.None;

    private sealed record SensorBinding(
        HubDevice Device, Guid CameraId, string TriggerOn, int CooldownSeconds, string Measurement, double Threshold)
    {
        public bool IsThreshold => TriggerOn is "below" or "above";

        public BindingKey Key => new(CameraId, Device.Id, TriggerOn, Measurement, Threshold);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        _client.ApplicationMessageReceivedAsync += OnMessageAsync;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Sensor trigger reconcile failed");
            }

            try
            {
                await Task.Delay(ReconcileInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await DisconnectAsync();
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var bound = (await configStore.GetAllAsync(cancellationToken)).Where(c => c.HasSensorBinding).ToList();

        // Nothing bound, or the hub the sensors come from is switched off: hold no connection at
        // all. The overwhelmingly common install has no sensor triggers and should pay nothing.
        var mqtt = await mainAppSettings.GetSectionAsync<MqttOptions>(MqttOptions.SectionName, cancellationToken);
        var hub = await mainAppSettings.GetSectionAsync<DeviceHubOptions>(DeviceHubOptions.SectionName, cancellationToken);
        if (bound.Count == 0 || mqtt is not { Enabled: true } || hub is not { Enabled: true })
        {
            if (_client.IsConnected) await DisconnectAsync();
            _bindings = new Dictionary<string, List<SensorBinding>>(StringComparer.Ordinal);
            return;
        }

        var devices = await FetchDevicesAsync(cancellationToken);
        if (devices is null) return;

        var bindings = new Dictionary<string, List<SensorBinding>>(StringComparer.Ordinal);
        foreach (var config in bound)
        {
            if (!Guid.TryParse(config.SensorDeviceId, out var deviceId)) continue;

            if (!devices.TryGetValue(deviceId, out var device))
            {
                logger.LogWarning(
                    "Camera {CameraId} is bound to sensor {DeviceId}, which no longer exists in the Device Hub - it will never fire",
                    config.CameraId, deviceId);
                continue;
            }

            if (!device.Enabled || string.IsNullOrWhiteSpace(device.StateTopic)) continue;

            if (!bindings.TryGetValue(device.StateTopic, out var list))
                bindings[device.StateTopic] = list = [];

            list.Add(new SensorBinding(
                device, config.CameraId, config.SensorTriggerOn, config.SensorCooldownSeconds,
                config.SensorMeasurement, config.SensorThreshold));
        }

        _bindings = bindings;
        PruneBaselines(bindings.Values.SelectMany(list => list).Select(b => b.Key).ToHashSet());

        if (bindings.Count == 0)
        {
            if (_client.IsConnected) await DisconnectAsync();
            return;
        }

        await ConnectAndSubscribeAsync(mqtt, bindings.Keys.ToHashSet(StringComparer.Ordinal), cancellationToken);
    }

    // Device definitions (topics, payload strings, schema) come from the main app rather than the
    // devices database directly - same forwarding shape as DeviceHubProxyEndpoints, authenticated
    // with the API key already shared between the two processes.
    private async Task<Dictionary<Guid, HubDevice>?> FetchDevicesAsync(CancellationToken cancellationToken)
    {
        var bridge = await mainAppSettings.GetSectionAsync<BridgeSettingsDto>("Bridge", cancellationToken);
        if (bridge is null || string.IsNullOrWhiteSpace(bridge.ApiKey))
        {
            logger.LogWarning("Cannot resolve sensor triggers - the main app's API key is not configured.");
            return null;
        }

        var baseUrl = (configuration["MainAppBaseUrl"] ?? "http://127.0.0.1:5080").TrimEnd('/');
        var client = httpClientFactory.CreateClient(nameof(SensorTriggerWatcher));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/devices");
        request.Headers.TryAddWithoutValidation("X-Api-Key", bridge.ApiKey);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Could not read the device list for sensor triggers: {StatusCode}", (int)response.StatusCode);
                return null;
            }

            var devices = await response.Content.ReadFromJsonAsync<List<HubDevice>>(cancellationToken);
            return devices?.ToDictionary(d => d.Id) ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "Could not read the device list for sensor triggers from {BaseUrl}", baseUrl);
            return null;
        }
    }

    private async Task ConnectAndSubscribeAsync(MqttOptions mqtt, HashSet<string> wanted, CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
        {
            var (host, port) = MqttConnection.ResolveBroker(mqtt);
            var clientOptions = MqttConnection
                .CreateBuilder(mqtt, $"sentinel-core-web-sensor-triggers-{Environment.MachineName}")
                .Build();

            await _client.ConnectAsync(clientOptions, cancellationToken);

            // Clean session, so the broker kept nothing: every wanted topic has to be sent again,
            // and every device needs a fresh baseline before it's allowed to fire (see below).
            _subscribedTopics = new HashSet<string>(StringComparer.Ordinal);
            lock (_stateLock) ClearBaselines();

            logger.LogInformation("Sensor triggers connected to MQTT broker at {Host}:{Port}", host, port);
        }

        var stale = _subscribedTopics.Where(topic => !wanted.Contains(topic)).ToArray();
        if (stale.Length > 0)
        {
            var unsubscribe = new MqttClientUnsubscribeOptionsBuilder();
            foreach (var topic in stale) unsubscribe = unsubscribe.WithTopicFilter(topic);

            try
            {
                await _client.UnsubscribeAsync(unsubscribe.Build(), cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Unsubscribing {TopicCount} stale sensor topic(s) failed; continuing", stale.Length);
            }
        }

        var newTopics = wanted.Where(topic => !_subscribedTopics.Contains(topic)).ToArray();
        if (newTopics.Length > 0)
        {
            var subscribe = new MqttClientSubscribeOptionsBuilder();
            foreach (var topic in newTopics)
            {
                subscribe = subscribe.WithTopicFilter(filter => filter
                    .WithTopic(topic)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce));
            }

            await _client.SubscribeAsync(subscribe.Build(), cancellationToken);
            logger.LogInformation("Sensor triggers watching {TopicCount} sensor topic(s)", wanted.Count);
        }

        _subscribedTopics = wanted;
    }

    private Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var topic = args.ApplicationMessage.Topic;
        if (!_bindings.TryGetValue(topic, out var bindings)) return Task.CompletedTask;

        var payload = args.ApplicationMessage.ConvertPayloadToString() ?? string.Empty;

        foreach (var binding in bindings)
        {
            var reading = DeviceTopics.ParseState(binding.Device, payload);
            if (reading is null) continue;

            // Fire-and-forget below: an MQTT message handler must not block the client's receive
            // loop for the seconds a snapshot and a pipeline round-trip take, and every failure
            // path inside TriggerAsync already logs and returns rather than throwing.
            if (binding.IsThreshold)
            {
                if (CrossedThreshold(binding, reading, out var detail)) _ = FireAsync(binding, detail);
                continue;
            }

            if (reading.Power == DevicePower.Unknown) continue;

            bool isFirstReading;
            bool changed;
            lock (_stateLock)
            {
                isFirstReading = !_lastPower.TryGetValue(binding.Key, out var previous);
                changed = isFirstReading || previous != reading.Power;
                _lastPower[binding.Key] = reading.Power;
            }

            // The first message after subscribing is the baseline, never a trigger. Brokers
            // replay retained state the instant you subscribe, so without this every restart of
            // this app would fire every camera whose sensor happened to be sitting at ON.
            if (isFirstReading || !changed) continue;

            if (!Fires(binding.TriggerOn, reading.Power)) continue;

            _ = FireAsync(binding, reading.Power.ToString().ToLowerInvariant());
        }

        return Task.CompletedTask;
    }

    // True only on the message where the reading crosses INTO the firing range. A tape measure
    // pointed at a driveway sits below its threshold for as long as the car is parked there, and
    // the interesting moment is the arrival, not each of the thousand messages that follow. Going
    // back out of range re-arms it and fires nothing by itself.
    private bool CrossedThreshold(SensorBinding binding, DeviceStateReading reading, out string detail)
    {
        detail = string.Empty;

        if (!reading.Measurements.TryGetValue(binding.Measurement, out var value))
        {
            WarnMissingMeasurementOnce(binding, reading);
            return false;
        }

        var satisfied = Satisfies(binding.TriggerOn, value, binding.Threshold);

        bool isFirstReading;
        bool wasSatisfied;
        lock (_stateLock)
        {
            isFirstReading = !_lastSatisfied.TryGetValue(binding.Key, out wasSatisfied);
            _lastSatisfied[binding.Key] = satisfied;
        }

        // Same baseline rule as the on/off path: a retained message replayed at subscribe time
        // describes how the world already was, not a change worth waking a camera for.
        if (isFirstReading || wasSatisfied || !satisfied) return false;

        detail = $"{binding.Measurement} {binding.TriggerOn} {binding.Threshold:0.##}, read {value:0.##}";
        return true;
    }

    private void WarnMissingMeasurementOnce(SensorBinding binding, DeviceStateReading reading)
    {
        lock (_stateLock)
        {
            if (!_warnedMissingMeasurement.Add(binding.Key)) return;
        }

        // The keys it *did* publish are the whole diagnosis - the usual cause is a name that
        // differs from what was guessed ("illuminance_lux", not "illuminance").
        logger.LogWarning(
            "Camera {CameraId} fires on '{Measurement}' from sensor '{DeviceName}', which doesn't report it. It reports: {Available}",
            binding.CameraId, binding.Measurement, binding.Device.Name,
            reading.Measurements.Count == 0 ? "(no numeric readings)" : string.Join(", ", reading.Measurements.Keys));
    }

    // Every baseline this instance holds. Called under _stateLock whenever the subscription is
    // torn down or re-established: a clean session means the broker kept nothing, so every binding
    // has to earn a fresh baseline before it is allowed to fire again.
    private void ClearBaselines()
    {
        _lastPower.Clear();
        _lastSatisfied.Clear();
        _warnedMissingMeasurement.Clear();
    }

    // Drops what belongs to bindings that are gone or have been edited, so the state here can't
    // outgrow the configuration that justifies it. Dropping is always safe: a key that comes back
    // simply re-baselines and skips one message, which is the same thing a restart does.
    private void PruneBaselines(HashSet<BindingKey> live)
    {
        lock (_stateLock)
        {
            foreach (var key in _lastPower.Keys.Where(k => !live.Contains(k)).ToArray()) _lastPower.Remove(key);
            foreach (var key in _lastSatisfied.Keys.Where(k => !live.Contains(k)).ToArray()) _lastSatisfied.Remove(key);
            _warnedMissingMeasurement.IntersectWith(live);
        }
    }

    // Public so the truth table can be tested directly - this project has no InternalsVisibleTo,
    // same reason MqttDeviceBus.SetStateForTesting is public.
    public static bool Fires(string triggerOn, DevicePower power) => triggerOn switch
    {
        "off" => power == DevicePower.Off,
        "any" => true,
        _ => power == DevicePower.On
    };

    // Strict comparison both ways, so a reading sitting exactly on the threshold is not "below"
    // it. The alternative makes a sensor parked on a round number - which is where cheap sensors
    // like to sit - flap in and out of range forever.
    public static bool Satisfies(string triggerOn, double value, double threshold) =>
        triggerOn == "below" ? value < threshold : value > threshold;

    private async Task FireAsync(SensorBinding binding, string detail)
    {
        try
        {
            var camera = await cameras.GetAsync(binding.CameraId, _stoppingToken);
            if (camera is null) return;

            var source = $"sensor '{binding.Device.Name}' ({detail})";
            var result = await triggers.TriggerAsync(
                camera,
                new CameraTriggerRequest(source, AlertProfile: null, binding.CooldownSeconds),
                _stoppingToken);

            if (!result.Triggered)
                logger.LogDebug("Sensor trigger for camera {CameraId} did not fire: {Detail}", binding.CameraId, result.Detail);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sensor trigger for camera {CameraId} failed", binding.CameraId);
        }
    }

    private async Task DisconnectAsync()
    {
        _subscribedTopics = new HashSet<string>(StringComparer.Ordinal);
        lock (_stateLock) ClearBaselines();

        if (!_client.IsConnected) return;

        try
        {
            await _client.DisconnectAsync();
        }
        catch
        {
            // Best-effort, same as MqttDeviceBus on shutdown.
        }
    }

    public override void Dispose()
    {
        _client.Dispose();
        base.Dispose();
    }
}
