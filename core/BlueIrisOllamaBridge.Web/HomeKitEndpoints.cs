namespace BlueIrisOllamaBridge.Web;

// Window onto the HomeKit sidecar (core/homekit-bridge) for the settings UI: is it running, is
// it paired, the pairing QR to scan, and an explicit pairing reset for stale controller keys.
// The sidecar binds 127.0.0.1 only, so the
// browser can't reach it directly - everything is proxied through here, including the QR image
// bytes themselves (a redirect would send the browser to an address it can't resolve).
//
public static class HomeKitEndpoints
{
    private sealed record HomeKitStatus(
        bool Enabled,
        bool Published,
        bool Paired,
        string[] Accessories,
        string? PairingCode,
        int HapPort,
        string? Advertiser,
        DateTimeOffset? StartedAt,
        DateTimeOffset? LastTriggerAt,
        string? LastTriggerCamera);

    private sealed record HomeKitResetRequest(string? Confirm);

    public static void MapHomeKitEndpoints(this WebApplication app)
    {
        app.MapGet("/api/homekit/status", async (
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<HomeKitStatus> logger,
            CancellationToken ct) =>
        {
            var baseUrl = SidecarBaseUrl(configuration);
            var client = httpClientFactory.CreateClient(nameof(HomeKitEndpoints));

            try
            {
                using var response = await client.GetAsync($"{baseUrl}/api/status", ct);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("HomeKit sidecar status returned {StatusCode}", (int)response.StatusCode);
                    return Results.Json(new { running = false, error = "The HomeKit sidecar returned an error." });
                }

                var status = await response.Content.ReadFromJsonAsync<HomeKitStatus>(cancellationToken: ct);
                return Results.Ok(new
                {
                    running = true,
                    // The sidecar runs for everyone but only advertises to the network once
                    // HomeKit is switched on, so "running" and "published" are different things
                    // and the UI needs both to say something useful.
                    enabled = status?.Enabled ?? false,
                    published = status?.Published ?? false,
                    paired = status?.Paired ?? false,
                    accessories = status?.Accessories ?? [],
                    pairingCode = status?.PairingCode,
                    hapPort = status?.HapPort,
                    advertiser = status?.Advertiser,
                    startedAt = status?.StartedAt,
                    lastTriggerAt = status?.LastTriggerAt,
                    lastTriggerCamera = status?.LastTriggerCamera
                });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Not an error worth shouting about - the sidecar is optional and simply may not
                // be installed or running. The UI turns this into a plain "not running" line.
                logger.LogDebug(ex, "HomeKit sidecar is not reachable at {BaseUrl}", baseUrl);
                return Results.Json(new { running = false, error = "The HomeKit sidecar isn't running." });
            }
        });

        app.MapGet("/api/homekit/qr", async (
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<HomeKitStatus> logger,
            CancellationToken ct) =>
        {
            var baseUrl = SidecarBaseUrl(configuration);
            var client = httpClientFactory.CreateClient(nameof(HomeKitEndpoints));

            try
            {
                using var response = await client.GetAsync($"{baseUrl}/api/qr", ct);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("HomeKit sidecar QR returned {StatusCode}", (int)response.StatusCode);
                    return Results.NotFound();
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                return Results.Bytes(bytes, "image/png");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogDebug(ex, "HomeKit sidecar is not reachable at {BaseUrl}", baseUrl);
                return Results.NotFound();
            }
        });

        app.MapPost("/api/homekit/reset", async (
            HomeKitResetRequest request,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<HomeKitStatus> logger,
            CancellationToken ct) =>
        {
            if (!string.Equals(request.Confirm, "RESET", StringComparison.Ordinal))
                return Results.BadRequest(new { error = "Explicit confirmation is required." });

            var baseUrl = SidecarBaseUrl(configuration);
            var client = httpClientFactory.CreateClient(nameof(HomeKitEndpoints));

            try
            {
                using var response = await client.PostAsync($"{baseUrl}/api/reset", content: null, ct);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("HomeKit sidecar reset returned {StatusCode}", (int)response.StatusCode);
                    return Results.Json(
                        new { error = "The HomeKit sidecar could not reset its pairing data." },
                        statusCode: StatusCodes.Status502BadGateway);
                }

                return Results.Accepted(value: new { reset = true, restarting = true });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogDebug(ex, "HomeKit sidecar is not reachable at {BaseUrl}", baseUrl);
                return Results.Json(
                    new { error = "The HomeKit sidecar isn't running." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }

    // Top-level config key, matching how AlertForwarder reads MainAppBaseUrl - this project keeps
    // its handful of service addresses at the root rather than binding an options section.
    private static string SidecarBaseUrl(IConfiguration configuration) =>
        (configuration["HomeKitSidecarBaseUrl"] ?? "http://127.0.0.1:3321").TrimEnd('/');
}
