using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SentinelCore.Web;

// Proxies automation-rule CRUD and test-fire to the main app - the rules and the alert pipeline
// that evaluates them both live over there (see AutomationRuleProxyEndpoints/AutomationAlertAction in
// the SentinelCore project). Mirrors QueueEndpoints.cs's forwarding pattern: read the API key out
// of the shared appsettings.json, forward the call, pass the response straight through.
// Marker type only, for a distinctly-named ILogger<T> category - naming the static
// AutomationRuleProxyEndpoints class itself here isn't legal (CS0718), and ILogger<Program>
// collides with the SentinelCore project's own top-level Program type once both are referenced.
public sealed class AutomationRuleProxyLog;

public static class AutomationRuleProxyEndpoints
{
    public static void MapAutomationRuleProxyEndpoints(this WebApplication app)
    {
        // Any account can read rules (the Automations tab is visible to everyone), but only an
        // Admin sees their secrets - see RedactForViewer.
        app.MapGet("/api/automation-rules", (
            HttpContext context, IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<AutomationRuleProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Get, "/api/automation-rules", null, ct,
                redact: !context.User.IsInRole(nameof(AccountRole.Admin))))
            .RequireAuthorization();

        app.MapGet("/api/automation-rules/metadata", (
            IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<AutomationRuleProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Get, "/api/automation-rules/metadata", null, ct))
            .RequireAuthorization();

        app.MapGet("/api/automation-rules/{id:guid}", (
            Guid id, HttpContext context, IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<AutomationRuleProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Get, $"/api/automation-rules/{id}", null, ct,
                redact: !context.User.IsInRole(nameof(AccountRole.Admin))))
            .RequireAuthorization();

        app.MapPost("/api/automation-rules", (
            JsonElement body, IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<AutomationRuleProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Post, "/api/automation-rules", body, ct))
            .RequireAuthorization("AdminOnly");

        app.MapPut("/api/automation-rules/{id:guid}", (
            Guid id, JsonElement body, IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<AutomationRuleProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Put, $"/api/automation-rules/{id}", body, ct))
            .RequireAuthorization("AdminOnly");

        app.MapDelete("/api/automation-rules/{id:guid}", (
            Guid id, IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<AutomationRuleProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Delete, $"/api/automation-rules/{id}", null, ct))
            .RequireAuthorization("AdminOnly");

        // AdminOnly, not just RequireAuthorization - this actually sends a live MQTT publish or
        // HTTP call out to whatever the rule is configured with, the same bar as clearing the queue.
        app.MapPost("/api/automation-rules/{id:guid}/test", (
            Guid id, IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<AutomationRuleProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Post, $"/api/automation-rules/{id}/test", null, ct))
            .RequireAuthorization("AdminOnly");
    }

    private static async Task<IResult> ForwardAsync(
        IHttpClientFactory httpClientFactory,
        AppSettingsFile mainAppSettings,
        IConfiguration configuration,
        ILogger logger,
        HttpMethod method,
        string path,
        JsonElement? body,
        CancellationToken ct,
        bool redact = false)
    {
        var bridge = await mainAppSettings.GetSectionAsync<BridgeSettingsDto>("Bridge", ct);
        if (bridge is null || string.IsNullOrWhiteSpace(bridge.ApiKey))
            return Results.Json(new { error = "The main app's API key is not configured." }, statusCode: 503);

        var baseUrl = (configuration["MainAppBaseUrl"] ?? "http://127.0.0.1:5080").TrimEnd('/');
        var client = httpClientFactory.CreateClient(nameof(AutomationRuleProxyEndpoints));

        using var request = new HttpRequestMessage(method, $"{baseUrl}{path}");
        request.Headers.TryAddWithoutValidation("X-Api-Key", bridge.ApiKey);
        if (body is not null) request.Content = JsonContent.Create(body.Value);

        try
        {
            using var response = await client.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Automation rule proxy {Method} {Path} failed: {StatusCode} {Body}", method, path, (int)response.StatusCode, responseBody);

            if (redact && response.IsSuccessStatusCode) responseBody = RedactForViewer(responseBody);
            return Results.Content(responseBody, "application/json", statusCode: (int)response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Could not reach the main app at {BaseUrl} for {Path}", baseUrl, path);
            return Results.Json(new { error = "Could not reach the main app." }, statusCode: 502);
        }
    }

    public const string Hidden = "(hidden - admins only)";

    // A webhook rule carries whatever the target needs to accept the call - typically a Home
    // Assistant long-lived token in an Authorization header, sometimes a token in the URL's query
    // string or the body. Viewers can't edit rules, so they never need the real values: keep what
    // says where the rule goes (scheme, host, path) and hide the rest.
    public static string RedactForViewer(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return json; }

        foreach (var rule in root is JsonArray array ? array.OfType<JsonObject>() : root is JsonObject single ? [single] : [])
        {
            foreach (var field in new[] { "webhookHeaders", "webhookBody" })
                if (rule[field] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
                    rule[field] = Hidden;

            if (rule["webhookUrl"] is JsonValue url && url.TryGetValue<string>(out var address) && address.Length > 0)
                rule["webhookUrl"] = Uri.TryCreate(address, UriKind.Absolute, out var uri)
                    // Authority is host:port without any user:password@.
                    ? $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}" + (uri.Query.Length > 0 ? "?" + Hidden : "")
                    : Hidden;
        }

        return root?.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? json;
    }
}
