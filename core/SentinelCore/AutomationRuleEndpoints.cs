namespace SentinelCore;

public sealed class AutomationRuleDto
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public List<string> Cameras { get; set; } = [];
    public List<string> Categories { get; set; } = [];
    public double MinimumConfidence { get; set; } = 0.6;
    public bool OnlyWhenNotificationRecommended { get; set; } = true;
    public int DuplicateCooldownMinutes { get; set; } = 5;
    public string ActionType { get; set; } = "mqtt";
    public string MqttTopic { get; set; } = string.Empty;
    public string MqttPayload { get; set; } = string.Empty;
    public bool MqttRetain { get; set; }
    public string WebhookUrl { get; set; } = string.Empty;
    public string WebhookMethod { get; set; } = "POST";
    public string WebhookHeaders { get; set; } = string.Empty;
    public string WebhookBody { get; set; } = string.Empty;
    public string WebhookContentType { get; set; } = "application/json";
}

// /api/automation-rules is protected the same way as every other /api/* route here - see
// ApiKeyMiddleware - so there's no separate auth attribute per endpoint below.
public static class AutomationRuleEndpoints
{
    public static void MapAutomationRuleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/automation-rules");

        // Ahead of the /{id:guid} routes so "metadata" doesn't get parsed as a rule id.
        group.MapGet("/metadata", () => Results.Ok(new
        {
            categories = AlertCategorizer.KnownCategories,
            tokens = AutomationTemplate.KnownTokens
        }));

        group.MapGet("", async (IAutomationRuleStore store, CancellationToken ct) =>
            Results.Ok(await store.GetAllAsync(ct)));

        group.MapGet("/{id:guid}", async (Guid id, IAutomationRuleStore store, CancellationToken ct) =>
            await store.GetAsync(id, ct) is { } rule ? Results.Ok(rule) : Results.NotFound());

        group.MapPost("", async (AutomationRuleDto dto, IAutomationRuleStore store, CancellationToken ct) =>
        {
            var errors = Validate(dto);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var now = DateTimeOffset.UtcNow;
            var rule = await store.CreateAsync(ToRule(dto, Guid.NewGuid(), now, now), ct);
            return Results.Ok(rule);
        });

        group.MapPut("/{id:guid}", async (Guid id, AutomationRuleDto dto, IAutomationRuleStore store, CancellationToken ct) =>
        {
            var errors = Validate(dto);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var existing = await store.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();

            var updated = await store.UpdateAsync(id, ToRule(dto, id, existing.CreatedAt, DateTimeOffset.UtcNow), ct);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });

        group.MapDelete("/{id:guid}", async (Guid id, IAutomationRuleStore store, CancellationToken ct) =>
            await store.DeleteAsync(id, ct) ? Results.Ok(new { deleted = true }) : Results.NotFound());

        group.MapPost("/{id:guid}/test", async (Guid id, IAutomationRuleStore store, AutomationAlertAction action, CancellationToken ct) =>
        {
            var rule = await store.GetAsync(id, ct);
            if (rule is null) return Results.NotFound();

            var (success, detail) = await action.FireTestAsync(rule, ct);
            return success
                ? Results.Ok(new { fired = true, detail })
                : Results.Json(new { fired = false, error = detail }, statusCode: 502);
        });
    }

    internal static AutomationRule ToRule(AutomationRuleDto dto, Guid id, DateTimeOffset createdAt, DateTimeOffset updatedAt) => new(
        id,
        dto.Name.Trim(),
        dto.Enabled,
        dto.Cameras.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList(),
        dto.Categories.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList(),
        Math.Clamp(dto.MinimumConfidence, 0, 1),
        dto.OnlyWhenNotificationRecommended,
        Math.Clamp(dto.DuplicateCooldownMinutes, 0, 1440),
        string.Equals(dto.ActionType, "mqtt", StringComparison.OrdinalIgnoreCase) ? "mqtt" : "webhook",
        dto.MqttTopic.Trim(),
        dto.MqttPayload,
        dto.MqttRetain,
        dto.WebhookUrl.Trim(),
        string.IsNullOrWhiteSpace(dto.WebhookMethod) ? "POST" : dto.WebhookMethod.Trim().ToUpperInvariant(),
        dto.WebhookHeaders,
        dto.WebhookBody,
        string.IsNullOrWhiteSpace(dto.WebhookContentType) ? "application/json" : dto.WebhookContentType.Trim(),
        createdAt,
        updatedAt);

    internal static List<string> Validate(AutomationRuleDto dto)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(dto.Name)) errors.Add("Name is required.");

        var isMqtt = string.Equals(dto.ActionType, "mqtt", StringComparison.OrdinalIgnoreCase);
        var isWebhook = string.Equals(dto.ActionType, "webhook", StringComparison.OrdinalIgnoreCase);
        if (!isMqtt && !isWebhook) errors.Add("Action type must be 'mqtt' or 'webhook'.");
        if (isMqtt && string.IsNullOrWhiteSpace(dto.MqttTopic)) errors.Add("MQTT topic is required.");
        if (isWebhook && string.IsNullOrWhiteSpace(dto.WebhookUrl)) errors.Add("Webhook URL is required.");

        if (dto.MinimumConfidence is < 0 or > 1) errors.Add("Minimum confidence must be between 0 and 1.");
        if (dto.DuplicateCooldownMinutes is < 0 or > 1440) errors.Add("Cooldown minutes must be between 0 and 1440.");

        return errors;
    }
}
