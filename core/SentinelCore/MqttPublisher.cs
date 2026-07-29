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
        if (_options.CurrentValue.Enabled)
            _ = EnsureConnectedAsync(_lifetimeCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
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
            var host = options.RunEmbeddedBroker ? "127.0.0.1" : options.BrokerHost;
            var port = options.RunEmbeddedBroker ? options.EmbeddedBrokerPort : options.BrokerPort;
            var availabilityTopic = MqttTopics.Availability(options.TopicPrefix);

            var builder = new MqttClientFactory().CreateClientOptionsBuilder()
                .WithClientId($"sentinel-core-{Environment.MachineName}")
                .WithTcpServer(host, port)
                .WithCleanSession()
                .WithWillTopic(availabilityTopic)
                .WithWillPayload("offline")
                .WithWillRetain(true)
                .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

            if (!string.IsNullOrWhiteSpace(options.Username))
                builder = builder.WithCredentials(options.Username, options.Password);

            var clientOptions = builder.Build();
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
        _client.DisconnectedAsync -= OnDisconnectedAsync;
        _connectLock.Dispose();
        _lifetimeCts?.Dispose();
        if (_client is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
        else _client.Dispose();
    }
}
