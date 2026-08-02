using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;

namespace SentinelCore;

// What the Device Hub currently believes about a device. LastSeen is when the device itself last
// said something, not when we last asked - a device that never publishes stays Unknown forever
// unless it's marked Optimistic.
public sealed record DeviceLiveState(
    DevicePower Power,
    int? BrightnessPercent,
    bool Available,
    DateTimeOffset? LastSeen)
{
    public static readonly DeviceLiveState Unknown = new(DevicePower.Unknown, null, Available: true, LastSeen: null);
}

public sealed record DeviceCommandResult(bool Published, bool Acknowledged, string? Error);

// The first MQTT *subscriber* in this app. Everything else here (MqttPublisher, MqttAlertAction,
// AutomationAlertAction) only ever pushes outward; controlling a light means also listening for
// what the light says back.
//
// Runs its own IMqttClient rather than sharing MqttPublisher's, deliberately: device topics are
// far chattier than camera alerts and get resubscribed every time the user edits a device, and
// none of that churn should be able to disturb the connection that alerts depend on.
public sealed class MqttDeviceBus : IHostedService, IAsyncDisposable
{
    private readonly IOptionsMonitor<MqttOptions> _mqttOptions;
    private readonly IOptionsMonitor<DeviceHubOptions> _hubOptions;
    private readonly IDeviceStore _store;
    private readonly ILogger<MqttDeviceBus> _logger;
    private readonly IMqttClient _client;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private readonly ConcurrentDictionary<Guid, DeviceLiveState> _states = new();

    // Rebuilt wholesale on every resubscribe. A topic can legitimately feed more than one device
    // (two entities behind one Zigbee2MQTT friendly name), so each maps to a list.
    private volatile Dictionary<string, List<TopicBinding>> _bindings = new(StringComparer.Ordinal);

    // What we last told the broker we wanted, so the next rebuild knows which filters to
    // withdraw. Only ever touched under _connectLock.
    private HashSet<string> _subscribedTopics = new(StringComparer.Ordinal);
    private volatile bool _subscriptionsReady;
    private readonly object _stateQueryLock = new();
    private DateTimeOffset _lastStateQueryAt = DateTimeOffset.MinValue;

    private CancellationTokenSource? _lifetimeCts;
    private IDisposable? _mqttSubscription;
    private IDisposable? _hubSubscription;
    private object? _connectionKey;

    private enum BindingKind { State, Availability, Brightness }

    private sealed record TopicBinding(HubDevice Device, BindingKind Kind);

    public MqttDeviceBus(
        IOptionsMonitor<MqttOptions> mqttOptions,
        IOptionsMonitor<DeviceHubOptions> hubOptions,
        IDeviceStore store,
        ILogger<MqttDeviceBus> logger)
    {
        _mqttOptions = mqttOptions;
        _hubOptions = hubOptions;
        _store = store;
        _logger = logger;
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;
    }

    public bool IsEnabled => _mqttOptions.CurrentValue.Enabled && _hubOptions.CurrentValue.Enabled;

    public bool IsConnected => _client.IsConnected;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _connectionKey = CurrentConnectionKey();
        if (IsEnabled)
        {
            // Fire-and-forget like MqttPublisher's own startup connect: a broker that isn't up
            // yet must not hold up the rest of the host, and the reconnect loop covers it.
            _ = Task.Run(() => ConnectAndSubscribeAsync(_lifetimeCts.Token), CancellationToken.None);
        }

        // Same live-settings contract as MqttPublisher: flipping the Device Hub or changing the
        // broker connection from any settings screen takes effect here without a restart. The
        // key comparison filters out the OnChange firings caused by saves of unrelated sections.
        _mqttSubscription = _mqttOptions.OnChange((_, _) => _ = ApplyOptionsChangeAsync());
        _hubSubscription = _hubOptions.OnChange((_, _) => _ = ApplyOptionsChangeAsync());
        return Task.CompletedTask;
    }

    // Hub enablement joins the broker details because it changes what this client should be
    // doing at all, not just where it connects.
    private object CurrentConnectionKey() =>
        (MqttConnection.ConnectionKey(_mqttOptions.CurrentValue), _hubOptions.CurrentValue.Enabled);

    private async Task ApplyOptionsChangeAsync()
    {
        var key = CurrentConnectionKey();
        if (Equals(key, _connectionKey)) return;
        _connectionKey = key;
        _subscriptionsReady = false;

        var token = _lifetimeCts?.Token ?? CancellationToken.None;
        try
        {
            if (_client.IsConnected)
            {
                // OnDisconnectedAsync brings the connection back up against the new options and
                // resubscribes - or leaves it down if the change was a disable, which is what it
                // checks IsEnabled for.
                await _client.DisconnectAsync(cancellationToken: token);
            }
            else if (IsEnabled)
            {
                await ConnectAndSubscribeAsync(token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Applying changed MQTT settings to the Device Hub connection failed; will retry on next command.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _mqttSubscription?.Dispose();
        _mqttSubscription = null;
        _hubSubscription?.Dispose();
        _hubSubscription = null;
        _lifetimeCts?.Cancel();
        if (!_client.IsConnected) return;

        try
        {
            await _client.DisconnectAsync(cancellationToken: cancellationToken);
        }
        catch
        {
            // Best-effort on shutdown, same as MqttPublisher.
        }
    }

    // Current state for one device, with the staleness rule applied. Kept as a read of the cache
    // rather than a broker round-trip so the clients can poll this cheaply.
    public DeviceLiveState GetState(HubDevice device)
    {
        var state = _states.GetValueOrDefault(device.Id, DeviceLiveState.Unknown);

        // An availability topic (a last-will, in practice) is the device telling us directly
        // whether it's reachable, so it wins outright. Ageing its state out on top of that would
        // be wrong for the common case: most devices publish only when something changes, so a
        // lamp that's simply been on all afternoon is silent but perfectly available.
        if (!string.IsNullOrWhiteSpace(device.AvailabilityTopic)) return state;

        // Without one, silence is the only signal there is - but it's still only meaningful for
        // a device that has reported at least once, and never for an optimistic device, which
        // by definition never reports at all.
        if (device.Optimistic || state.LastSeen is not DateTimeOffset lastSeen) return state;

        var timeout = TimeSpan.FromSeconds(_hubOptions.CurrentValue.StateTimeoutSeconds);
        return DateTimeOffset.UtcNow - lastSeen <= timeout ? state : state with { Available = false };
    }

    // Seeds the cached state directly so the GetState staleness rules can be tested without
    // waiting real minutes for a device to go quiet. Public for the same reason
    // CameraDiscoveryService.ParseFfprobeOutput is - this project has no InternalsVisibleTo.
    public void SetStateForTesting(Guid deviceId, DeviceLiveState state) => _states[deviceId] = state;

    public async Task<DeviceCommandResult> SendCommandAsync(
        HubDevice device,
        DevicePower? power,
        int? brightnessPercent,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled)
            return new DeviceCommandResult(false, false, "The Device Hub is turned off.");
        if (!DeviceTopics.IsCommandable(device.Kind))
            return new DeviceCommandResult(false, false, $"A {device.Kind} is read-only and cannot be commanded.");

        var publishes = DeviceTopics.BuildCommands(device, power, brightnessPercent);
        if (publishes.Count == 0)
            return new DeviceCommandResult(false, false, "Nothing to send - no power or brightness change was requested.");

        try
        {
            if (!_client.IsConnected) await ConnectAndSubscribeAsync(cancellationToken);
            if (!_client.IsConnected)
                return new DeviceCommandResult(false, false, "Not connected to the MQTT broker.");

            foreach (var publish in publishes)
            {
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(publish.Topic)
                    .WithPayload(publish.Payload)
                    .WithRetainFlag(publish.Retain)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();
                await _client.PublishAsync(message, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send MQTT command to device {DeviceName}", device.Name);
            return new DeviceCommandResult(false, false, "Could not publish the command to the broker.");
        }

        _logger.LogInformation(
            "Sent {PublishCount} MQTT command message(s) to device {DeviceName}", publishes.Count, device.Name);

        // An optimistic device never echoes anything back, so its own reported state is what we
        // just told it to be. Anything else gets a short window to confirm, and the UI shows the
        // command as un-acknowledged if it doesn't - the publish itself still happened.
        if (device.Optimistic)
        {
            ApplyOptimisticState(device, power, brightnessPercent);
            return new DeviceCommandResult(true, true, null);
        }

        var acknowledged = await WaitForEchoAsync(device, power, brightnessPercent, cancellationToken);
        return new DeviceCommandResult(true, acknowledged, null);
    }

    // Called after any device is added, edited, enabled, or deleted - the subscription set is
    // derived entirely from the store, so it's rebuilt rather than patched.
    public async Task ResubscribeAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            _bindings = new Dictionary<string, List<TopicBinding>>(StringComparer.Ordinal);
            return;
        }

        try
        {
            await ConnectAndSubscribeAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh Device Hub MQTT subscriptions");
        }
    }

    // Used by the first Device Hub read after server/app startup. The hosted-service connection
    // is intentionally fire-and-forget, so an API request can otherwise race it and receive
    // "unknown" immediately before retained MQTT state arrives. Serializing through the same
    // connection lock guarantees subscription setup has completed; the short grace lets the
    // broker deliver retained state messages queued by SUBSCRIBE.
    public async Task EnsureStateSubscriptionsReadyAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled) return;

        try
        {
            if (!_subscriptionsReady)
                await ConnectAndSubscribeAsync(cancellationToken);

            await RequestCurrentStatesAsync(cancellationToken);
            // Allow retained replies and active query responses to reach OnMessageReceivedAsync
            // before the HTTP endpoint snapshots the cache.
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // State reads still return their cached/unknown values when MQTT is down; losing the
            // broker should not turn the whole Device Hub endpoint into HTTP 500.
            _logger.LogWarning(ex, "Could not prepare Device Hub state subscriptions for a state read");
        }
    }

    private async Task RequestCurrentStatesAsync(CancellationToken cancellationToken)
    {
        // The iPhone asks every five seconds while Device Hub is visible. Querying active state
        // on every poll would be needless broker/device traffic; normal state subscriptions keep
        // the cache current between these occasional reconciliations.
        lock (_stateQueryLock)
        {
            if (DateTimeOffset.UtcNow - _lastStateQueryAt < TimeSpan.FromSeconds(30)) return;
            _lastStateQueryAt = DateTimeOffset.UtcNow;
        }

        if (!_client.IsConnected) return;
        var devices = await _store.GetEnabledAsync(cancellationToken);
        var queries = devices.SelectMany(DeviceTopics.BuildStateQueries).ToArray();
        foreach (var query in queries)
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(query.Topic)
                .WithPayload(query.Payload)
                .WithRetainFlag(false)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            await _client.PublishAsync(message, cancellationToken);
        }

        if (queries.Length > 0)
            _logger.LogInformation("Device Hub requested fresh state from {QueryCount} MQTT device(s)", queries.Length);
    }

    private async Task ConnectAndSubscribeAsync(CancellationToken cancellationToken)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (!_client.IsConnected)
            {
                var options = _mqttOptions.CurrentValue;
                var (host, port) = MqttConnection.ResolveBroker(options);
                var clientOptions = MqttConnection
                    .CreateBuilder(options, $"sentinel-core-devices-{Environment.MachineName}")
                    .Build();

                await _client.ConnectAsync(clientOptions, cancellationToken);

                // A clean session means the broker kept none of our previous subscriptions, so
                // the tracking set has to start empty or the rebuild below would skip
                // resubscribing every topic it thinks is already covered.
                _subscribedTopics = new HashSet<string>(StringComparer.Ordinal);
                _logger.LogInformation("Device Hub connected to MQTT broker at {Host}:{Port}", host, port);
            }

            await SubscribeCurrentDevicesAsync(cancellationToken);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task SubscribeCurrentDevicesAsync(CancellationToken cancellationToken)
    {
        var devices = await _store.GetEnabledAsync(cancellationToken);

        var bindings = new Dictionary<string, List<TopicBinding>>(StringComparer.Ordinal);
        void Bind(string topic, HubDevice device, BindingKind kind)
        {
            if (string.IsNullOrWhiteSpace(topic)) return;
            if (!bindings.TryGetValue(topic, out var list))
                bindings[topic] = list = [];
            list.Add(new TopicBinding(device, kind));
        }

        foreach (var device in devices)
        {
            Bind(device.StateTopic, device, BindingKind.State);
            Bind(device.AvailabilityTopic, device, BindingKind.Availability);
            if (device.SupportsBrightness) Bind(device.BrightnessStateTopic, device, BindingKind.Brightness);
        }

        _bindings = bindings;

        // Drop cached state for devices that no longer exist, so a deleted-then-recreated device
        // doesn't inherit the old one's reading.
        var liveIds = devices.Select(d => d.Id).ToHashSet();
        foreach (var id in _states.Keys.Where(id => !liveIds.Contains(id)))
            _states.TryRemove(id, out _);

        if (!_client.IsConnected) return;

        // MQTT UNSUBSCRIBE matches topic filters exactly - there's no "unsubscribe from
        // everything" filter - so a topic the user just edited away has to be named explicitly
        // or it would keep delivering messages for the rest of the process's life.
        var wanted = bindings.Keys.ToHashSet(StringComparer.Ordinal);
        var stale = _subscribedTopics.Where(topic => !wanted.Contains(topic)).ToArray();

        if (stale.Length > 0)
        {
            var unsubscribeOptions = new MqttClientUnsubscribeOptionsBuilder();
            foreach (var topic in stale) unsubscribeOptions = unsubscribeOptions.WithTopicFilter(topic);

            try
            {
                await _client.UnsubscribeAsync(unsubscribeOptions.Build(), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unsubscribing {TopicCount} stale device topic(s) failed; continuing", stale.Length);
            }
        }

        if (wanted.Count > 0)
        {
            // Resubscribing a topic that's already subscribed is a no-op at the broker, so this
            // sends the whole current set rather than tracking a delta in both directions.
            var subscribeOptions = new MqttClientSubscribeOptionsBuilder();
            foreach (var topic in wanted)
            {
                subscribeOptions = subscribeOptions.WithTopicFilter(filter => filter
                    .WithTopic(topic)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce));
            }

            await _client.SubscribeAsync(subscribeOptions.Build(), cancellationToken);
        }

        _subscribedTopics = wanted;
        _subscriptionsReady = true;
        _logger.LogInformation("Device Hub subscribed to {TopicCount} device topic(s)", wanted.Count);
    }

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var topic = args.ApplicationMessage.Topic;
        if (!_bindings.TryGetValue(topic, out var bindings)) return Task.CompletedTask;

        var payload = args.ApplicationMessage.ConvertPayloadToString() ?? string.Empty;

        foreach (var (device, kind) in bindings)
        {
            switch (kind)
            {
                case BindingKind.State:
                    if (DeviceTopics.ParseState(device, payload) is { } reading)
                    {
                        Update(device.Id, current => current with
                        {
                            Power = reading.Power == DevicePower.Unknown ? current.Power : reading.Power,
                            BrightnessPercent = reading.BrightnessPercent ?? current.BrightnessPercent,
                            LastSeen = DateTimeOffset.UtcNow
                        });
                    }
                    break;

                case BindingKind.Availability:
                    if (DeviceTopics.ParseAvailability(device, payload) is { } available)
                        Update(device.Id, current => current with { Available = available, LastSeen = DateTimeOffset.UtcNow });
                    break;

                case BindingKind.Brightness:
                    if (DeviceTopics.ParseBrightnessPayload(device, payload) is { } percent)
                        Update(device.Id, current => current with { BrightnessPercent = percent, LastSeen = DateTimeOffset.UtcNow });
                    break;
            }
        }

        return Task.CompletedTask;
    }

    private void Update(Guid deviceId, Func<DeviceLiveState, DeviceLiveState> transform) =>
        _states.AddOrUpdate(deviceId, _ => transform(DeviceLiveState.Unknown), (_, current) => transform(current));

    private void ApplyOptimisticState(HubDevice device, DevicePower? power, int? brightnessPercent) =>
        Update(device.Id, current => current with
        {
            Power = power ?? current.Power,
            BrightnessPercent = brightnessPercent ?? current.BrightnessPercent,
            Available = true,
            LastSeen = DateTimeOffset.UtcNow
        });

    private async Task<bool> WaitForEchoAsync(
        HubDevice device, DevicePower? power, int? brightnessPercent, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(_hubOptions.CurrentValue.CommandTimeoutSeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = _states.GetValueOrDefault(device.Id, DeviceLiveState.Unknown);
            var powerMatches = power is null || state.Power == power;
            var brightnessMatches = brightnessPercent is null || state.BrightnessPercent == brightnessPercent;
            if (powerMatches && brightnessMatches) return true;

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        _subscriptionsReady = false;
        var token = _lifetimeCts?.Token ?? CancellationToken.None;
        if (token.IsCancellationRequested || !IsEnabled) return;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token);
            await ConnectAndSubscribeAsync(token);
        }
        catch (OperationCanceledException)
        {
            // App is shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Device Hub MQTT reconnect attempt failed; will retry on next command");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _mqttSubscription?.Dispose();
        _hubSubscription?.Dispose();
        _client.ApplicationMessageReceivedAsync -= OnMessageReceivedAsync;
        _client.DisconnectedAsync -= OnDisconnectedAsync;
        _connectLock.Dispose();
        _lifetimeCts?.Dispose();
        if (_client is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
        else _client.Dispose();
    }
}
