using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Options;

namespace SentinelCore;

// Per-rule custom MQTT publish or HTTP webhook call, evaluated against every completed alert.
// Unlike MqttAlertAction/WebhookAlertAction (one fixed global destination each), any number of
// rules can be defined here, each scoped to specific cameras/detected categories, with its own
// MQTT topic or webhook URL and a templated payload (see AutomationTemplate). CRUD lives behind
// AutomationRuleEndpoints; this class only matches and fires.
public sealed class AutomationAlertAction : IAlertAction
{
    private readonly HttpClient _httpClient;
    private readonly IAutomationRuleStore _store;
    private readonly IMqttPublisher _mqttPublisher;
    private readonly IOptionsMonitor<MqttOptions> _mqttOptions;
    private readonly ILogger<AutomationAlertAction> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastFired = new(StringComparer.OrdinalIgnoreCase);

    public AutomationAlertAction(
        HttpClient httpClient,
        IAutomationRuleStore store,
        IMqttPublisher mqttPublisher,
        IOptionsMonitor<MqttOptions> mqttOptions,
        ILogger<AutomationAlertAction> logger)
    {
        _httpClient = httpClient;
        _store = store;
        _mqttPublisher = mqttPublisher;
        _mqttOptions = mqttOptions;
        _logger = logger;
    }

    public async Task ExecuteAsync(AlertResult result, CancellationToken cancellationToken)
    {
        if (result.Analysis is null) return;

        var rules = await _store.GetEnabledAsync(cancellationToken);
        if (rules.Count == 0) return;

        var category = AlertCategorizer.Categorize(result.Analysis);

        foreach (var rule in rules)
        {
            if (!Matches(rule, result, category)) continue;

            var cooldownKey = $"{rule.Id}|{result.Camera}|{category ?? "none"}";
            if (IsInCooldown(rule, cooldownKey))
            {
                _logger.LogInformation("Automation rule {RuleName} suppressed for {Camera} (cooldown)", rule.Name, result.Camera);
                continue;
            }

            try
            {
                var detail = await DispatchAsync(rule, result, category, cancellationToken);
                if (rule.DuplicateCooldownMinutes > 0) _lastFired[cooldownKey] = DateTimeOffset.UtcNow;
                _logger.LogInformation("Automation rule {RuleName} fired for {AlertId}: {Detail}", rule.Name, result.Id, detail);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Automation rule {RuleName} failed for {AlertId}", rule.Name, result.Id);
            }
        }
    }

    // Fires a rule immediately against a synthetic alert, bypassing camera/category/confidence/
    // cooldown matching entirely - backs the "test" button in the dashboard so a rule's
    // topic/payload or URL/body can be checked without waiting for a real detection.
    public async Task<(bool Success, string Detail)> FireTestAsync(AutomationRule rule, CancellationToken cancellationToken)
    {
        var result = BuildTestAlertResult(rule);
        try
        {
            var detail = await DispatchAsync(rule, result, "test", cancellationToken);
            return (true, detail);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static bool Matches(AutomationRule rule, AlertResult result, string? category)
    {
        if (rule.Cameras.Count > 0 && !rule.Cameras.Any(c => string.Equals(c, result.Camera, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (rule.OnlyWhenNotificationRecommended && !AlertNotificationPolicy.ShouldNotify(result))
            return false;

        if (result.Analysis!.Confidence < rule.MinimumConfidence)
            return false;

        if (rule.Categories.Count > 0)
        {
            if (category is null) return false;
            if (!rule.Categories.Any(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase))) return false;
        }

        return true;
    }

    private bool IsInCooldown(AutomationRule rule, string key)
    {
        if (rule.DuplicateCooldownMinutes <= 0) return false;
        if (!_lastFired.TryGetValue(key, out var lastFired)) return false;
        return DateTimeOffset.UtcNow - lastFired < TimeSpan.FromMinutes(rule.DuplicateCooldownMinutes);
    }

    private Task<string> DispatchAsync(AutomationRule rule, AlertResult result, string? category, CancellationToken cancellationToken) =>
        string.Equals(rule.ActionType, "mqtt", StringComparison.OrdinalIgnoreCase)
            ? DispatchMqttAsync(rule, result, category, cancellationToken)
            : DispatchWebhookAsync(rule, result, category, cancellationToken);

    private async Task<string> DispatchMqttAsync(AutomationRule rule, AlertResult result, string? category, CancellationToken cancellationToken)
    {
        if (!_mqttOptions.CurrentValue.Enabled)
            throw new InvalidOperationException("MQTT is not enabled - turn on the MQTT integration in Settings first.");

        var topic = AutomationTemplate.Render(rule.MqttTopic, result, category);
        if (string.IsNullOrWhiteSpace(topic))
            throw new InvalidOperationException("The MQTT topic is empty after substitution.");

        var payload = AutomationTemplate.Render(rule.MqttPayload, result, category);
        await _mqttPublisher.PublishAsync(topic, payload, rule.MqttRetain, cancellationToken);
        return $"published to {topic}";
    }

    private async Task<string> DispatchWebhookAsync(AutomationRule rule, AlertResult result, string? category, CancellationToken cancellationToken)
    {
        var url = AutomationTemplate.Render(rule.WebhookUrl, result, category);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"'{url}' is not a valid URL after substitution.");

        var method = ParseMethod(rule.WebhookMethod);
        using var request = new HttpRequestMessage(method, uri);

        foreach (var (name, value) in ParseHeaders(rule.WebhookHeaders, result, category))
            request.Headers.TryAddWithoutValidation(name, value);

        if (method != HttpMethod.Get && method != HttpMethod.Head && !string.IsNullOrEmpty(rule.WebhookBody))
        {
            var body = AutomationTemplate.Render(rule.WebhookBody, result, category);
            var contentType = string.IsNullOrWhiteSpace(rule.WebhookContentType) ? "application/json" : rule.WebhookContentType;
            request.Content = new StringContent(body, Encoding.UTF8, contentType);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
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
    private static IEnumerable<(string Name, string Value)> ParseHeaders(string raw, AlertResult result, string? category)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;

        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            var separator = trimmed.IndexOf(':');
            if (separator <= 0) continue;

            var name = trimmed[..separator].Trim();
            var value = AutomationTemplate.Render(trimmed[(separator + 1)..].Trim(), result, category);
            if (name.Length > 0) yield return (name, value);
        }
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    private static AlertResult BuildTestAlertResult(AutomationRule rule)
    {
        var analysis = new VisionAnalysis
        {
            EventType = "test_alert",
            Summary = $"Test alert fired manually for automation rule \"{rule.Name}\".",
            Person = new PersonAnalysis { Visible = true, Location = "at_gate", Activity = "walking", Description = "test person" },
            Vehicle = new VehicleAnalysis { Visible = true, Location = "outside_gate_entrance", Activity = "stopped", Type = "sedan", Color = "blue", Description = "test vehicle" },
            Confidence = 1.0,
            NotificationRecommended = true,
            NotificationReason = "Manual test fire"
        };

        return new AlertResult
        {
            Id = Guid.NewGuid(),
            Camera = rule.Cameras.FirstOrDefault() ?? "test-camera",
            OccurredAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            Profile = "default",
            Model = "test",
            Status = "completed",
            Analysis = analysis
        };
    }
}
