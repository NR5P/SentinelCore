namespace SentinelCore.Web;

// Proxies the "send a test" buttons on the EMAIL, TELEGRAM and WEBHOOK settings sections to the
// main app, where the senders and the saved credentials both live (see NotificationTestEndpoints.cs
// in the SentinelCore project). Same forwarding pattern as AutomationRuleProxyEndpoints: read the API
// key out of the shared appsettings.json, forward, pass the response straight through - the
// clients need the main app's own wording ("chat not found", "No such host is known"), and a
// paraphrase here would only lose it.
//
// Marker type only, for a distinctly-named ILogger<T> category - see AutomationRuleProxyLog.
public sealed class NotificationTestProxyLog;

public static class NotificationTestProxyEndpoints
{
    public static void MapNotificationTestProxyEndpoints(this WebApplication app)
    {
        // AdminOnly, the same bar as the settings themselves: this sends real mail through the
        // configured account, messages a real chat, and posts a real event to whatever the webhook
        // URL points at - which may be something that switches on a light when it arrives.
        foreach (var channel in (string[])["email", "telegram", "webhook"])
        {
            app.MapPost($"/api/notifications/test/{channel}", (
                IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
                ILogger<NotificationTestProxyLog> logger, CancellationToken ct) =>
                ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, $"/api/notifications/test/{channel}", ct))
                .RequireAuthorization("AdminOnly");
        }

        // Not a notification channel, but the same arrangement for the same reason: the vision
        // clients and the saved credentials both live in the main app, and the CLOUD VISION
        // section's test button needs the provider's own wording to be worth pressing. Spends
        // real money on the configured account, so AdminOnly like its neighbours.
        app.MapPost("/api/settings/cloud-vision/test", (
            IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<NotificationTestProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, "/api/settings/cloud-vision/test", ct))
            .RequireAuthorization("AdminOnly");
    }

    private static async Task<IResult> ForwardAsync(
        IHttpClientFactory httpClientFactory,
        AppSettingsFile mainAppSettings,
        IConfiguration configuration,
        ILogger logger,
        string path,
        CancellationToken ct)
    {
        var bridge = await mainAppSettings.GetSectionAsync<BridgeSettingsDto>("Bridge", ct);
        if (bridge is null || string.IsNullOrWhiteSpace(bridge.ApiKey))
            return Results.Json(new { error = "The main app's API key is not configured." }, statusCode: 503);

        var baseUrl = (configuration["MainAppBaseUrl"] ?? "http://127.0.0.1:5080").TrimEnd('/');
        var client = httpClientFactory.CreateClient(nameof(NotificationTestProxyEndpoints));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}{path}");
        request.Headers.TryAddWithoutValidation("X-Api-Key", bridge.ApiKey);

        try
        {
            using var response = await client.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Notification test {Path} failed: {StatusCode} {Body}", path, (int)response.StatusCode, responseBody);

            return Results.Content(responseBody, "application/json", statusCode: (int)response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Could not reach the main app at {BaseUrl} for {Path}", baseUrl, path);
            return Results.Json(new { error = "Could not reach the main app to send the test." }, statusCode: 502);
        }
    }
}
