using System.Net.Http.Json;

namespace BlueIrisOllamaBridge.Web;

// Proxies the main app's analysis queue depth/clear operations - the queue itself lives in the
// main app's process, this project has no access to it directly. Mirrors ChatEndpoints.cs's
// forwarding pattern: read the API key out of the shared appsettings.json, forward the call,
// translate the response.
public static class QueueEndpoints
{
    public static void MapQueueEndpoints(this WebApplication app)
    {
        app.MapGet("/api/queue/status", async (
            IHttpClientFactory httpClientFactory,
            AppSettingsFile mainAppSettings,
            IConfiguration configuration,
            ILogger<QueueStatusResponse> logger,
            CancellationToken ct) =>
        {
            var bridge = await mainAppSettings.GetSectionAsync<BridgeSettingsDto>("Bridge", ct);
            if (bridge is null || string.IsNullOrWhiteSpace(bridge.ApiKey))
                return Results.Json(new { error = "The main app's API key is not configured." }, statusCode: 503);

            var baseUrl = (configuration["MainAppBaseUrl"] ?? "http://127.0.0.1:5080").TrimEnd('/');
            var client = httpClientFactory.CreateClient(nameof(QueueEndpoints));

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/queue/status");
            request.Headers.TryAddWithoutValidation("X-Api-Key", bridge.ApiKey);

            try
            {
                using var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    logger.LogWarning("Queue status forward failed: {StatusCode} {Body}", (int)response.StatusCode, body);
                    return Results.Json(new { error = "Could not reach the main app just now." }, statusCode: 502);
                }

                var payload = await response.Content.ReadFromJsonAsync<QueueStatusResponse>(cancellationToken: ct);
                return Results.Ok(new { queued = payload?.Queued ?? 0 });
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Could not reach the main app at {BaseUrl} for queue status", baseUrl);
                return Results.Json(new { error = "Could not reach the main app." }, statusCode: 502);
            }
        }).RequireAuthorization();

        // AdminOnly, not just RequireAuthorization - this discards work in progress rather than
        // just reading a number, the same bar every other mutating camera/settings endpoint sets.
        app.MapPost("/api/queue/clear", async (
            IHttpClientFactory httpClientFactory,
            AppSettingsFile mainAppSettings,
            IConfiguration configuration,
            ILogger<QueueClearResponse> logger,
            CancellationToken ct) =>
        {
            var bridge = await mainAppSettings.GetSectionAsync<BridgeSettingsDto>("Bridge", ct);
            if (bridge is null || string.IsNullOrWhiteSpace(bridge.ApiKey))
                return Results.Json(new { error = "The main app's API key is not configured." }, statusCode: 503);

            var baseUrl = (configuration["MainAppBaseUrl"] ?? "http://127.0.0.1:5080").TrimEnd('/');
            var client = httpClientFactory.CreateClient(nameof(QueueEndpoints));

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/queue/clear");
            request.Headers.TryAddWithoutValidation("X-Api-Key", bridge.ApiKey);

            try
            {
                using var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    logger.LogWarning("Queue clear forward failed: {StatusCode} {Body}", (int)response.StatusCode, body);
                    return Results.Json(new { error = "Could not reach the main app just now." }, statusCode: 502);
                }

                var payload = await response.Content.ReadFromJsonAsync<QueueClearResponse>(cancellationToken: ct);
                return Results.Ok(new { cleared = payload?.Cleared ?? 0 });
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Could not reach the main app at {BaseUrl} to clear the queue", baseUrl);
                return Results.Json(new { error = "Could not reach the main app." }, statusCode: 502);
            }
        }).RequireAuthorization("AdminOnly");
    }

    private sealed record QueueStatusResponse(int Queued);
    private sealed record QueueClearResponse(int Cleared);
}
