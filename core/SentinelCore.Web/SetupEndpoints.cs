using System.Security.Cryptography;

namespace SentinelCore.Web;

public sealed class SetupStatusDto
{
    public bool Completed { get; set; }
}

// Powers the first-run wizard: whether it should show at all, an idempotent way to hand a
// fresh install a real Bridge:ApiKey without making someone run `openssl rand -hex 32` by
// hand first, and a proxy to the main app's own /health/ready so the wizard can offer a
// "test connection" button without duplicating any connectivity-checking logic.
public static class SetupEndpoints
{
    public static void MapSetupEndpoints(this WebApplication app)
    {
        var setup = app.MapGroup("/api/setup").RequireAuthorization("AdminOnly");

        setup.MapGet("/status", async (AppSettingsFile settings, CancellationToken ct) =>
        {
            var status = await settings.GetSectionAsync<SetupStatusDto>("Setup", ct);
            return Results.Ok(new { completed = status?.Completed ?? false });
        });

        setup.MapPost("/complete", async (AppSettingsFile settings, CancellationToken ct) =>
        {
            await settings.MergeSectionAsync("Setup", new SetupStatusDto { Completed = true }, ct);
            return Results.Ok(new { completed = true });
        });

        setup.MapPost("/reset", async (AppSettingsFile settings, CancellationToken ct) =>
        {
            await settings.MergeSectionAsync("Setup", new SetupStatusDto { Completed = false }, ct);
            return Results.Ok(new { completed = false });
        });

        setup.MapPost("/generate-api-key", async (AppSettingsFile settings, CancellationToken ct) =>
        {
            var bridge = await settings.GetSectionAsync<BridgeSettingsDto>("Bridge", ct);

            // Idempotent - a real key already in use by a live Blue Iris integration is never
            // regenerated out from under it. Only fills in a genuinely unconfigured install.
            var alreadyConfigured = bridge is not null
                && !string.IsNullOrWhiteSpace(bridge.ApiKey)
                && !bridge.ApiKey.StartsWith("CHANGE-ME", StringComparison.OrdinalIgnoreCase);

            var apiKey = alreadyConfigured ? bridge!.ApiKey : RandomNumberGenerator.GetHexString(64);

            if (!alreadyConfigured)
                await settings.MergeSectionAsync("Bridge", new { ApiKey = apiKey }, ct);

            return Results.Ok(new { apiKey, generated = !alreadyConfigured });
        });

        setup.MapGet("/vision-status", async (IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken ct) =>
        {
            var baseUrl = (configuration["MainAppBaseUrl"] ?? "http://127.0.0.1:5080").TrimEnd('/');
            var client = httpClientFactory.CreateClient(nameof(SetupEndpoints));
            client.Timeout = TimeSpan.FromSeconds(10);

            try
            {
                using var response = await client.GetAsync($"{baseUrl}/health/ready", ct);
                return Results.Ok(new { reachable = response.IsSuccessStatusCode });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Results.Ok(new { reachable = false });
            }
        });
    }
}
