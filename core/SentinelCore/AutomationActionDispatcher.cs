using System.Text;
using Microsoft.Extensions.Options;

namespace SentinelCore;

// Carries out an AutomationAction: publish MQTT, call a webhook, or switch a Device Hub device.
// Shared by AutomationAlertAction (fired by what a camera saw) and ScheduleWorker (fired by the
// clock), which is the whole reason it exists as its own class - the two triggers have nothing
// in common, but what they can do is identical, and it should stay identical without anyone
// having to remember to add a feature twice.
//
// Every method throws on failure rather than returning a flag, so callers can record one error
// message with no branching. The string returned on success is the human-readable detail both
// callers log and store.
public sealed class AutomationActionDispatcher(
    HttpClient httpClient,
    IMqttPublisher mqttPublisher,
    IOptionsMonitor<MqttOptions> mqttOptions,
    IDeviceStore deviceStore,
    MqttDeviceBus deviceBus)
{
    public Task<string> DispatchAsync(
        AutomationAction action, IReadOnlyDictionary<string, string> tokens, CancellationToken cancellationToken) =>
        action.Type?.ToLowerInvariant() switch
        {
            AutomationAction.TypeDevice => DispatchDeviceAsync(action, cancellationToken),
            AutomationAction.TypeWebhook => DispatchWebhookAsync(action, tokens, cancellationToken),

            // MQTT is the historical default: automation rules predate the action-type field and
            // every one of them is an MQTT publish, so an unrecognised type must keep behaving
            // the way those rows already do.
            _ => DispatchMqttAsync(action, tokens, cancellationToken)
        };

    // The friendly path, and the one a schedule almost always wants: name a device from the
    // Device Hub and an action in its own vocabulary ("open" for a gate, "on" for a light), and
    // let MqttDeviceBus work out the topics and payloads that device needs.
    private async Task<string> DispatchDeviceAsync(AutomationAction action, CancellationToken cancellationToken)
    {
        if (action.DeviceId is not { } deviceId)
            throw new InvalidOperationException("No device was chosen for this action.");

        var device = await deviceStore.GetAsync(deviceId, cancellationToken)
            ?? throw new InvalidOperationException("That device is no longer in the Device Hub.");

        if (!device.Enabled)
            throw new InvalidOperationException($"{device.Name} is turned off in the Device Hub.");

        if (!deviceBus.IsEnabled)
            throw new InvalidOperationException(
                "The Device Hub is not running - turn on MQTT and the Device Hub in Settings first.");

        var verb = action.DeviceAction?.Trim().ToLowerInvariant();

        // "stop" is its own path for the same reason it is in DeviceHubEndpoints: it publishes a
        // dedicated payload rather than a power state.
        if (verb == "stop")
        {
            var stopResult = await deviceBus.SendStopAsync(device, cancellationToken);
            if (!stopResult.Published)
                throw new InvalidOperationException(stopResult.Error ?? $"Could not send stop to {device.Name}.");

            return $"stopped {device.Name}";
        }

        // The same aliases DeviceHubEndpoints accepts, resolved the same way, so a schedule can
        // speak a gate's language rather than translating to on/off in someone's head.
        DevicePower? power = verb switch
        {
            "on" or "open" or "lock" or "press" => DevicePower.On,
            "off" or "close" or "unlock" => DevicePower.Off,
            "toggle" => DeviceTopics.IsMomentary(device) || deviceBus.GetState(device).Power != DevicePower.On
                ? DevicePower.On
                : DevicePower.Off,
            null or "" => null,
            _ => throw new InvalidOperationException(
                $"'{action.DeviceAction}' is not something a device can be told to do.")
        };

        if (action.DeviceBrightness is { } brightness)
        {
            if (brightness is < 0 or > 100)
                throw new InvalidOperationException("Brightness must be between 0 and 100.");
            if (!device.SupportsBrightness)
                throw new InvalidOperationException($"{device.Name} does not support brightness.");
        }

        if (power is null && action.DeviceBrightness is null)
            throw new InvalidOperationException("This action does not say what to do with the device.");

        var result = await deviceBus.SendCommandAsync(device, power, action.DeviceBrightness, cancellationToken);
        if (!result.Published)
            throw new InvalidOperationException(result.Error ?? $"Could not send the command to {device.Name}.");

        var described = power switch
        {
            DevicePower.On => DeviceTopics.IsMomentary(device) ? "pressed" : "switched on",
            DevicePower.Off => "switched off",
            _ => "set"
        };

        return action.DeviceBrightness is { } level
            ? $"{described} {device.Name} at {level}%"
            : $"{described} {device.Name}";
    }

    private async Task<string> DispatchMqttAsync(
        AutomationAction action, IReadOnlyDictionary<string, string> tokens, CancellationToken cancellationToken)
    {
        if (!mqttOptions.CurrentValue.Enabled)
            throw new InvalidOperationException("MQTT is not enabled - turn on the MQTT integration in Settings first.");

        var topic = AutomationTemplate.Render(action.MqttTopic, tokens);
        if (string.IsNullOrWhiteSpace(topic))
            throw new InvalidOperationException("The MQTT topic is empty after substitution.");

        var payload = AutomationTemplate.Render(action.MqttPayload, tokens);
        await mqttPublisher.PublishAsync(topic, payload, action.MqttRetain, cancellationToken);
        return $"published to {topic}";
    }

    private async Task<string> DispatchWebhookAsync(
        AutomationAction action, IReadOnlyDictionary<string, string> tokens, CancellationToken cancellationToken)
    {
        var url = AutomationTemplate.Render(action.WebhookUrl, tokens);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"'{url}' is not a valid URL after substitution.");

        var method = ParseMethod(action.WebhookMethod);
        using var request = new HttpRequestMessage(method, uri);

        foreach (var (name, value) in ParseHeaders(action.WebhookHeaders, tokens))
            request.Headers.TryAddWithoutValidation(name, value);

        if (method != HttpMethod.Get && method != HttpMethod.Head && !string.IsNullOrEmpty(action.WebhookBody))
        {
            var body = AutomationTemplate.Render(action.WebhookBody, tokens);
            var contentType = string.IsNullOrWhiteSpace(action.WebhookContentType)
                ? "application/json"
                : action.WebhookContentType;
            request.Content = new StringContent(body, Encoding.UTF8, contentType);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"{method} {uri} returned {(int)response.StatusCode}: {Truncate(responseBody, 300)}");
        }

        return $"{method} {uri} -> {(int)response.StatusCode}";
    }

    private static HttpMethod ParseMethod(string method) => method?.Trim().ToUpperInvariant() switch
    {
        "GET" => HttpMethod.Get,
        "PUT" => HttpMethod.Put,
        "PATCH" => HttpMethod.Patch,
        "DELETE" => HttpMethod.Delete,
        _ => HttpMethod.Post
    };

    // One "Name: Value" header per line - blank lines and lines without a colon are ignored.
    // Values go through the same {{token}} substitution as the URL/body, so e.g.
    // "X-Camera: {{camera}}" works as a header too.
    private static IEnumerable<(string Name, string Value)> ParseHeaders(
        string raw, IReadOnlyDictionary<string, string> tokens)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;

        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            var separator = trimmed.IndexOf(':');
            if (separator <= 0) continue;

            var name = trimmed[..separator].Trim();
            var value = AutomationTemplate.Render(trimmed[(separator + 1)..].Trim(), tokens);
            if (name.Length > 0) yield return (name, value);
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
