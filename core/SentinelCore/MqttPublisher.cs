using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;

namespace SentinelCore;

// Owns the single outbound MQTT connection - whether that's to this app's own embedded broker
// (EmbeddedMqttBrokerService) or an external one - and handles connect/reconnect. Deliberately
// separate from MqttAlertAction, which only decides what topics/payloads an alert becomes.
public interface IMqttPublisher
{
    Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken);
}

public sealed class MqttPublisher : IMqttPublisher, IHostedService, IAsyncDisposable
{
    private readonly IOptionsMonitor<MqttOptions> _options;
    private readonly ILogger<MqttPublisher> _logger;
    private readonly IMqttClient _client;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private CancellationTokenSource? _lifetimeCts;
    private bool _disposed;
    private IDisposable? _optionsSubscription;
    private object? _connectionKey;

    public MqttPublisher(IOptionsMonitor<MqttOptions> options, ILogger<MqttPublisher> logger)
    {
        _options = options;
        _logger = logger;
        _client = new MqttClientFactory().CreateMqttClient();
        _client.DisconnectedAsync += OnDisconnectedAsync;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _connectionKey = MqttConnection.ConnectionKey(_options.CurrentValue);
        if (_options.CurrentValue.Enabled)
            _ = EnsureConnectedAsync(_lifetimeCts.Token);

        // Settings saved from the dashboard or apps land in appsettings.json and reload live;
        // when the broker connection details actually changed, the open connection is stale and
        // gets cycled so the next connect reads the new options. OnChange also fires for saves
        // of unrelated sections, which is what the ConnectionKey comparison filters out.
        _optionsSubscription = _options.OnChange((_, _) => _ = ApplyOptionsChangeAsync());
        return Task.CompletedTask;
    }

    private async Task ApplyOptionsChangeAsync()
    {
        var current = _options.CurrentValue;
        var key = MqttConnection.ConnectionKey(current);
        if (Equals(key, _connectionKey)) return;
        _connectionKey = key;

        var token = _lifetimeCts?.Token ?? CancellationToken.None;
        try
        {
            if (_client.IsConnected)
            {
                // OnDisconnectedAsync reconnects with the new options - or stays down if the
                // change was turning MQTT off, which is exactly what it checks.
                await _client.DisconnectAsync(cancellationToken: token);
            }
            else if (current.Enabled)
            {
                await EnsureConnectedAsync(token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Applying changed MQTT settings to the publisher connection failed; will retry on next publish.");
        }
    }

    // Dispose may run before StopAsync as well as after it - the DI container owns this
    // instance's lifetime and the host owns its start/stop, and nothing orders the two against
    // each other. Both orders have to be safe, and this one was not: StopAsync touched a
    // CancellationTokenSource and a SemaphoreSlim that Dispose had already released, so shutdown
    // threw ObjectDisposedException out of Host.StopAsync. Disposal also has to tolerate being
    // called more than once, which .NET's own guidance requires and which this type gets for
    // real: it is registered three times (concrete, interface, hosted service) and the container
    // tracks each registration separately.
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_disposed) return;

        _optionsSubscription?.Dispose();
        _optionsSubscription = null;
        _lifetimeCts?.Cancel();
        if (_client.IsConnected)
        {
            try
            {
                await PublishInternalAsync(MqttTopics.Availability(_options.CurrentValue.TopicPrefix), "offline", true, cancellationToken);
                await _client.DisconnectAsync(cancellationToken: cancellationToken);
            }
            catch
            {
                // Best-effort on shutdown - the broker's own LWT covers this if it fails.
            }
        }
    }

    public async Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled) return;

        try
        {
            if (!_client.IsConnected)
                await EnsureConnectedAsync(cancellationToken);

            if (_client.IsConnected)
                await PublishInternalAsync(topic, payload, retain, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish MQTT message to {Topic}", topic);
        }
    }

    private Task PublishInternalAsync(string topic, string payload, bool retain, CancellationToken cancellationToken)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        return _client.PublishAsync(message, cancellationToken);
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client.IsConnected) return;

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_client.IsConnected) return;

            var options = _options.CurrentValue;
            var (host, port) = MqttConnection.ResolveBroker(options);
            var availabilityTopic = MqttTopics.Availability(options.TopicPrefix);

            var clientOptions = MqttConnection
                .CreateBuilder(options, $"sentinel-core-{Environment.MachineName}")
                .WithWillTopic(availabilityTopic)
                .WithWillPayload("offline")
                .WithWillRetain(true)
                .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            await _client.ConnectAsync(clientOptions, cancellationToken);
            await PublishInternalAsync(availabilityTopic, "online", true, cancellationToken);
            _logger.LogInformation("Connected to MQTT broker at {Host}:{Port}", host, port);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        var token = _lifetimeCts?.Token ?? CancellationToken.None;
        if (token.IsCancellationRequested || !_options.CurrentValue.Enabled) return;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token);
            await EnsureConnectedAsync(token);
        }
        catch (OperationCanceledException)
        {
            // App is shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MQTT reconnect attempt failed; will retry on next publish");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _optionsSubscription?.Dispose();
        _client.DisconnectedAsync -= OnDisconnectedAsync;
        _connectLock.Dispose();
        _lifetimeCts?.Dispose();
        if (_client is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
        else _client.Dispose();
    }
}
