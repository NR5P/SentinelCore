using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace BlueIrisOllamaBridge;

public sealed class MqttAlertAction : IAlertAction
{
    private readonly IMqttPublisher _publisher;
    private readonly IOptionsMonitor<MqttOptions> _optionsMonitor;
    private readonly ILogger<MqttAlertAction> _logger;

    // Tracks which cameras already got their (retained) Home Assistant discovery config
    // published this run - discovery only needs to be sent once per camera, not per event.
    private readonly ConcurrentDictionary<string, bool> _discoveryPublished = new(StringComparer.OrdinalIgnoreCase);

    public MqttAlertAction(IMqttPublisher publisher, IOptionsMonitor<MqttOptions> options, ILogger<MqttAlertAction> logger)
    {
        _publisher = publisher;
        _optionsMonitor = options;
        _logger = logger;
    }

    public async Task ExecuteAsync(AlertResult result, CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        if (!options.Enabled || result.Analysis is null) return;

        if (options.OnlyWhenNotificationRecommended && !AlertNotificationPolicy.ShouldNotify(result))
            return;

        if (options.HomeAssistantDiscovery)
            await EnsureDiscoveryPublishedAsync(options, result.Camera, cancellationToken);

        await _publisher.PublishAsync(
            MqttTopics.EventState(options.TopicPrefix, result.Camera),
            result.Analysis.EventType,
            retain: false,
            cancellationToken);

        await _publisher.PublishAsync(
            MqttTopics.EventAttributes(options.TopicPrefix, result.Camera),
            MqttTopics.BuildEventAttributesPayload(result),
            retain: false,
            cancellationToken);

        if (AlertNotificationPolicy.ShouldNotify(result))
        {
            await _publisher.PublishAsync(
                MqttTopics.NotifiedState(options.TopicPrefix, result.Camera),
                "ON",
                retain: false,
                cancellationToken);
        }

        _logger.LogInformation("Published MQTT event for {AlertId} on camera {Camera}", result.Id, result.Camera);
    }

    private async Task EnsureDiscoveryPublishedAsync(MqttOptions options, string camera, CancellationToken cancellationToken)
    {
        if (!_discoveryPublished.TryAdd(camera, true)) return;

        await _publisher.PublishAsync(
            MqttTopics.DiscoveryConfigTopic(options.HomeAssistantDiscoveryPrefix, "binary_sensor", options.TopicPrefix, camera, "notified"),
            MqttTopics.BuildNotifiedDiscoveryPayload(options, camera),
            retain: true,
            cancellationToken);

        await _publisher.PublishAsync(
            MqttTopics.DiscoveryConfigTopic(options.HomeAssistantDiscoveryPrefix, "sensor", options.TopicPrefix, camera, "last_event"),
            MqttTopics.BuildLastEventDiscoveryPayload(options, camera),
            retain: true,
            cancellationToken);
    }
}
