using System.Net.Http.Json;
using SentinelCore;

namespace SentinelCore.Web;

// Proxies the main app's resource stats (CPU/RAM/disk/queue/uptime) the same way
// QueueEndpoints.cs proxies queue depth - the sampler lives in the main app's process, this
// project has no access to it directly.
public static class SystemStatusEndpoints
{
    public sealed record RestartRequest(string? Service);

    public static void MapSystemStatusEndpoints(this WebApplication app)
    {
        // The other half of "no setting should need a shell": restart-required settings (Urls,
        // Internet Exposed Mode, Https) save fine remotely but only apply on restart, and until
        // now the restart itself was the step that still needed SSH. "bridge" forwards to the
        // main app's own restart endpoint; "dashboard" restarts this process. Explicit service
        // name required - a client restarting the wrong process because of a default would be
        // worse than an error. Both sides refuse when the process isn't supervised - see
        // RestartSupport - so this can't take an unmanaged install down for good.
        app.MapPost("/api/system/restart", async (
            RestartRequest request,
            IHttpClientFactory httpClientFactory,
            AppSettingsFile mainAppSettings,
            IConfiguration configuration,
            IHostApplicationLifetime lifetime,
            ILogger<RestartRequest> logger,
            CancellationToken ct) =>
        {
            switch (request.Service)
            {
                case "dashboard":
                    if (!RestartSupport.IsSupervised(configuration))
                    {
                        return Results.Conflict(new
                        {
                            error = "The dashboard isn't running under a supervisor that would restart it, so exiting would just take it down. " +
                                    "Run it as a service (the shipped systemd units restart automatically), or set Restart:ForceAllowed to true " +
                                    "if a supervisor this can't detect (Docker, launchd) is in charge."
                        });
                    }

                    logger.LogWarning("Dashboard restart requested - shutting down for the supervisor to bring us back.");
                    RestartSupport.StopSoon(lifetime);
                    return Results.Accepted(value: new { restarting = true });

                case "bridge":
                    var bridge = await mainAppSettings.GetSectionAsync<BridgeSettingsDto>("Bridge", ct);
                    if (bridge is null || string.IsNullOrWhiteSpace(bridge.ApiKey))
                        return Results.Json(new { error = "The main app's API key is not configured." }, statusCode: 503);

                    var baseUrl = (configuration["MainAppBaseUrl"] ?? "http://127.0.0.1:5080").TrimEnd('/');
                    var client = httpClientFactory.CreateClient(nameof(SystemStatusEndpoints));

                    using (var forward = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/system/restart"))
                    {
                        forward.Headers.TryAddWithoutValidation("X-Api-Key", bridge.ApiKey);
                        try
                        {
                            using var response = await client.SendAsync(forward, ct);
                            var body = await response.Content.ReadAsStringAsync(ct);
                            if (!response.IsSuccessStatusCode)
                                logger.LogWarning("Bridge restart forward failed: {StatusCode} {Body}", (int)response.StatusCode, body);
                            return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
                        }
                        catch (HttpRequestException ex)
                        {
                            logger.LogWarning(ex, "Could not reach the main app at {BaseUrl} to restart it", baseUrl);
                            return Results.Json(new { error = "Could not reach the main app." }, statusCode: 502);
                        }
                    }

                default:
                    return Results.BadRequest(new { errors = new[] { "Service must be 'bridge' or 'dashboard'." } });
            }
        }).RequireAuthorization("AdminOnly");
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
