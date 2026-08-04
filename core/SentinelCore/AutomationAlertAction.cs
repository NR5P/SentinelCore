using System.Collections.Concurrent;

namespace SentinelCore;

// Per-rule custom MQTT publish or HTTP webhook call, evaluated against every completed alert.
// Unlike MqttAlertAction/WebhookAlertAction (one fixed global destination each), any number of
// rules can be defined here, each scoped to specific cameras/detected categories, with its own
// MQTT topic or webhook URL and a templated payload (see AutomationTemplate). CRUD lives behind
// AutomationRuleEndpoints; this class only matches and decides, and hands the doing to
// AutomationActionDispatcher - which ScheduleWorker also uses, so a time-driven firing and a
// detection-driven one behave identically.
public sealed class AutomationAlertAction : IAlertAction
{
    private readonly IAutomationRuleStore _store;
    private readonly AutomationActionDispatcher _dispatcher;
    private readonly ILogger<AutomationAlertAction> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastFired = new(StringComparer.OrdinalIgnoreCase);

    public AutomationAlertAction(
        IAutomationRuleStore store,
        AutomationActionDispatcher dispatcher,
        ILogger<AutomationAlertAction> logger)
    {
        _store = store;
        _dispatcher = dispatcher;
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
        _dispatcher.DispatchAsync(
            rule.ToAction(), AutomationTemplate.BuildValues(result, category), cancellationToken);

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
