namespace BlueIrisOllamaBridge.Web;

// Read-only window onto the HomeKit sidecar (core/homekit-bridge) for the settings UI: is it
// running, is it paired, and the pairing QR to scan. The sidecar binds 127.0.0.1 only, so the
// browser can't reach it directly - everything is proxied through here, including the QR image
// bytes themselves (a redirect would send the browser to an address it can't resolve).
//
// Nothing here can change HomeKit state. Turning the integration on/off is a normal settings
// write (PUT /api/settings/homekit), and unpairing is done from the Home app on the phone.
public static class HomeKitEndpoints
{
    private sealed record HomeKitStatus(bool Paired, string[] Accessories, string? PairingCode);

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
                    paired = status?.Paired ?? false,
                    accessories = status?.Accessories ?? [],
                    pairingCode = status?.PairingCode
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
    }

    // Top-level config key, matching how AlertForwarder reads MainAppBaseUrl - this project keeps
    // its handful of service addresses at the root rather than binding an options section.
    private static string SidecarBaseUrl(IConfiguration configuration) =>
        (configuration["HomeKitSidecarBaseUrl"] ?? "http://127.0.0.1:3321").TrimEnd('/');
}
