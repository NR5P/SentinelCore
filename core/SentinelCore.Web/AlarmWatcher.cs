using System.Net.Http.Json;
using MQTTnet;
using MQTTnet.Protocol;
using SentinelCore;

namespace SentinelCore.Web;

// The alarm's ears: a subscription to the state (and availability) topics of every device that
// is an alarm sensor, handing each reading to AlarmEngine the moment it arrives. Same shape and
// same reasoning as SensorTriggerWatcher - the devices live in the main app, the alarm lives
// here, and a subscription of our own is less machinery than a callback between the two.
//
// Also the alarm's clock: it ticks the engine once a second so exit and entry delays run out on
// time whether or not anyone has a tab open.
public sealed class AlarmWatcher(
    AlarmEngine engine,
    AppSettingsFile mainAppSettings,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<AlarmWatcher> logger) : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
    private readonly SemaphoreSlim _wake = new(0, 1);

    // topic -> (device, whether the topic is its availability topic rather than its state)
    private volatile Dictionary<string, List<(HubDevice Device, bool Availability)>> _bindings = new(StringComparer.Ordinal);
    private HashSet<string> _subscribedTopics = new(StringComparer.Ordinal);
    private CancellationToken _stoppingToken = CancellationToken.None;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        _client.ApplicationMessageReceivedAsync += OnMessageAsync;
        _client.DisconnectedAsync += _ =>
        {
            engine.BrokerConnected = false;
            return Task.CompletedTask;
        };
        engine.SensorsChanged += () =>
        {
            if (_wake.CurrentCount == 0) _wake.Release();
        };

        try
        {
            await engine.InitializeAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Alarm state could not be loaded");
        }

        await Task.WhenAll(ReconcileLoopAsync(stoppingToken), TickLoopAsync(stoppingToken));
        await DisconnectAsync();
    }

    private async Task ReconcileLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Alarm sensor reconcile failed");
                engine.BrokerConnected = _client.IsConnected;
            }

            try
            {
                await _wake.WaitAsync(ReconcileInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await engine.TickAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Alarm tick failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var alarm = await mainAppSettings.GetSectionAsync<AlarmSettingsDto>(AlarmSettingsDto.SectionName, cancellationToken) ?? new AlarmSettingsDto();
        var mqtt = await mainAppSettings.GetSectionAsync<MqttOptions>(MqttOptions.SectionName, cancellationToken);
        var hub = await mainAppSettings.GetSectionAsync<DeviceHubOptions>(DeviceHubOptions.SectionName, cancellationToken);
        var wanted = await engine.WatchedDeviceIdsAsync(cancellationToken);

        if (!alarm.Enabled || wanted.Count == 0 || mqtt is not { Enabled: true } || hub is not { Enabled: true })
        {
            if (_client.IsConnected) await DisconnectAsync();
            _bindings = new Dictionary<string, List<(HubDevice, bool)>>(StringComparer.Ordinal);
            engine.BrokerConnected = false;
            return;
        }

        var devices = await FetchDevicesAsync(cancellationToken);
        if (devices is null) return;
        engine.SetDevices(devices);

        var bindings = new Dictionary<string, List<(HubDevice Device, bool Availability)>>(StringComparer.Ordinal);
        foreach (var deviceId in wanted)
        {
            if (!devices.TryGetValue(deviceId, out var device) || !device.Enabled) continue;
            if (!string.IsNullOrWhiteSpace(device.StateTopic)) Bind(bindings, device.StateTopic, device, availability: false);
            if (!string.IsNullOrWhiteSpace(device.AvailabilityTopic)) Bind(bindings, device.AvailabilityTopic, device, availability: true);
        }
        _bindings = bindings;

        if (bindings.Count == 0)
        {
            if (_client.IsConnected) await DisconnectAsync();
            engine.BrokerConnected = false;
            return;
        }

        await ConnectAndSubscribeAsync(mqtt, bindings.Keys.ToHashSet(StringComparer.Ordinal), cancellationToken);
        engine.BrokerConnected = _client.IsConnected;
    }

    private static void Bind(Dictionary<string, List<(HubDevice, bool)>> bindings, string topic, HubDevice device, bool availability)
    {
        if (!bindings.TryGetValue(topic, out var list)) bindings[topic] = list = [];
        list.Add((device, availability));
    }

    private async Task<Dictionary<Guid, HubDevice>?> FetchDevicesAsync(CancellationToken cancellationToken)
    {
        var bridge = await mainAppSettings.GetSectionAsync<BridgeSettingsDto>("Bridge", cancellationToken);
        if (bridge is null || string.IsNullOrWhiteSpace(bridge.ApiKey))
        {
            logger.LogWarning("The alarm cannot read its sensors - the main app's API key is not configured.");
            return null;
        }

        var baseUrl = (configuration["MainAppBaseUrl"] ?? "http://127.0.0.1:5080").TrimEnd('/');
        var client = httpClientFactory.CreateClient(nameof(AlarmWatcher));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/devices");
        request.Headers.TryAddWithoutValidation("X-Api-Key", bridge.ApiKey);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Could not read the device list for the alarm: {StatusCode}", (int)response.StatusCode);
                return null;
            }
            var devices = await response.Content.ReadFromJsonAsync<List<HubDevice>>(cancellationToken);
            return devices?.ToDictionary(d => d.Id) ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "Could not read the device list for the alarm from {BaseUrl}", baseUrl);
            return null;
        }
    }

    private async Task ConnectAndSubscribeAsync(MqttOptions mqtt, HashSet<string> wanted, CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
        {
            var (host, port) = MqttConnection.ResolveBroker(mqtt);
            var clientOptions = MqttConnection.CreateBuilder(mqtt, $"sentinel-core-web-alarm-{Environment.MachineName}").Build();
            await _client.ConnectAsync(clientOptions, cancellationToken);
            _subscribedTopics = new HashSet<string>(StringComparer.Ordinal);
            logger.LogInformation("Alarm connected to MQTT broker at {Host}:{Port}", host, port);
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
                logger.LogDebug(ex, "Unsubscribing {TopicCount} stale alarm topic(s) failed; continuing", stale.Length);
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
            logger.LogInformation("Alarm listening on {TopicCount} sensor topic(s)", wanted.Count);
        }

        _subscribedTopics = wanted;
    }

    private async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var topic = args.ApplicationMessage.Topic;
        if (!_bindings.TryGetValue(topic, out var bindings)) return;
        var payload = args.ApplicationMessage.ConvertPayloadToString() ?? string.Empty;

        foreach (var (device, availability) in bindings)
        {
            try
            {
                if (availability)
                {
                    if (DeviceTopics.ParseAvailability(device, payload) is { } available)
                        await engine.ObserveAvailabilityAsync(device.Id, available, _stoppingToken);
                    continue;
                }

                var reading = DeviceTopics.ParseState(device, payload);
                if (reading is not null) await engine.ObserveAsync(device.Id, reading, _stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The alarm could not handle a reading from {Device}", device.Name);
            }
        }
    }

    private async Task DisconnectAsync()
    {
        _subscribedTopics = new HashSet<string>(StringComparer.Ordinal);
        engine.BrokerConnected = false;
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
        _wake.Dispose();
        base.Dispose();
    }
}

// The real actions: a Device Hub command through the main app, a push through the same sender
// the camera alerts use, and a camera trigger through the same service a sensor-bound camera
// uses.
public sealed class AlarmActions(
    AppSettingsFile mainAppSettings,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    PushNotificationDispatcher push,
    ICameraStore cameras,
    CameraTriggerService triggers,
    ILogger<AlarmActions> logger) : IAlarmActions
{
    // Long enough that an alarm's sirens can't be muted by an ordinary camera's motion cooldown,
    // short enough that a second trigger a minute later still gets a fresh frame.
    private const int CameraCooldownSeconds = 15;

    public async Task<string?> SetDeviceAsync(Guid deviceId, bool on, CancellationToken cancellationToken)
    {
        var bridge = await mainAppSettings.GetSectionAsync<BridgeSettingsDto>("Bridge", cancellationToken);
        if (bridge is null || string.IsNullOrWhiteSpace(bridge.ApiKey)) return "the main app's API key is not configured";

        var baseUrl = (configuration["MainAppBaseUrl"] ?? "http://127.0.0.1:5080").TrimEnd('/');
        var client = httpClientFactory.CreateClient(nameof(AlarmWatcher));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/devices/{deviceId}/command");
        request.Headers.TryAddWithoutValidation("X-Api-Key", bridge.ApiKey);
        request.Content = JsonContent.Create(new { action = on ? "on" : "off" });
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return $"HTTP {(int)response.StatusCode} {body}".Trim();
        }
        catch (HttpRequestException ex)
        {
            return $"could not reach the main app ({ex.Message})";
        }
    }

    public async Task PushAsync(string title, string body, CancellationToken cancellationToken)
    {
        var options = await mainAppSettings.GetSectionAsync<PushNotificationOptions>(PushNotificationOptions.SectionName, cancellationToken);
        if (options is null || !options.Enabled || !PushProviderSelection.AnyConfigured(options)) return;
        try
        {
            var report = await push.SendAsync(options, new PushMessage(title, body, null, null, PushMessage.AlarmType), cancellationToken);
            logger.LogInformation("Alarm push '{Title}': {Delivered}/{Attempted} delivered", title, report.Delivered, report.Attempted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Alarm push '{Title}' failed", title);
        }
    }

    public async Task TriggerCamerasAsync(IReadOnlyList<Guid> cameraIds, string source, CancellationToken cancellationToken)
    {
        var all = await cameras.GetAllAsync(cancellationToken);
        var chosen = cameraIds.Count == 0 ? all : all.Where(c => cameraIds.Contains(c.Id)).ToList();
        foreach (var camera in chosen)
        {
            try
            {
                var result = await triggers.TriggerAsync(camera, new CameraTriggerRequest(source, AlertProfile: null, CameraCooldownSeconds), cancellationToken);
                if (!result.Triggered) logger.LogDebug("Alarm trigger for camera {Camera} did not fire: {Detail}", camera.Name, result.Detail);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Alarm trigger for camera {Camera} failed", camera.Name);
            }
        }
    }
}
