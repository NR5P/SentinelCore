using System.Net.Http.Json;

namespace SentinelCore.Web;

// Proxies the main app's resource stats (CPU/RAM/disk/queue/uptime) the same way
// QueueEndpoints.cs proxies queue depth - the sampler lives in the main app's process, this
// project has no access to it directly.
public static class SystemStatusEndpoints
{
    public static void MapSystemStatusEndpoints(this WebApplication app)
    {
        app.MapGet("/api/system/status", async (
            IHttpClientFactory httpClientFactory,
            AppSettingsFile mainAppSettings,
            IConfiguration configuration,
            ILogger<SystemStatusResponse> logger,
            CancellationToken ct) =>
        {
            var bridge = await mainAppSettings.GetSectionAsync<BridgeSettingsDto>("Bridge", ct);
            if (bridge is null || string.IsNullOrWhiteSpace(bridge.ApiKey))
                return Results.Json(new { error = "The main app's API key is not configured." }, statusCode: 503);

            var baseUrl = (configuration["MainAppBaseUrl"] ?? "http://127.0.0.1:5080").TrimEnd('/');
            var client = httpClientFactory.CreateClient(nameof(SystemStatusEndpoints));

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/system/status");
            request.Headers.TryAddWithoutValidation("X-Api-Key", bridge.ApiKey);

            try
            {
                using var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    logger.LogWarning("System status forward failed: {StatusCode} {Body}", (int)response.StatusCode, body);
                    return Results.Json(new { error = "Could not reach the main app just now." }, statusCode: 502);
                }

                var payload = await response.Content.ReadFromJsonAsync<SystemStatusResponse>(cancellationToken: ct);
                if (payload is null)
                    return Results.Json(new { error = "The main app returned an empty status." }, statusCode: 502);

                return Results.Ok(payload);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Could not reach the main app at {BaseUrl} for system status", baseUrl);
                return Results.Json(new { error = "Could not reach the main app." }, statusCode: 502);
            }
        }).RequireAuthorization();
    }

    // Mirrors SystemStatusSnapshot in the main app - kept as a separate type since this project
    // doesn't reference the main app's assembly.
    private sealed record SystemStatusResponse(
        DateTimeOffset TimestampUtc,
        double CpuUsagePercent,
        int ProcessorCount,
        long MemoryUsedBytes,
        long MemoryTotalBytes,
        long DiskFreeBytes,
        long DiskTotalBytes,
        int QueuedAlerts,
        double UptimeSeconds,
        bool VisionBackendHealthy);
}
