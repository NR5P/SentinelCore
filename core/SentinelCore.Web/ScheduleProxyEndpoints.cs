using System.Net.Http.Json;
using System.Text.Json;

namespace SentinelCore.Web;

// Proxies schedule CRUD, previews and run-now to the main app, where the schedules live and
// where the worker that fires them runs (see ScheduleEndpoints/ScheduleWorker in the SentinelCore
// project). Mirrors AutomationRuleProxyEndpoints exactly: read the API key out of the shared
// appsettings.json, forward the call, pass the response straight through.
// Marker type only, for a distinctly-named ILogger<T> category - naming the static
// ScheduleProxyEndpoints class itself here isn't legal (CS0718).
public sealed class ScheduleProxyLog;

public static class ScheduleProxyEndpoints
{
    public static void MapScheduleProxyEndpoints(this WebApplication app)
    {
        app.MapGet("/api/schedules", (
            IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<ScheduleProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Get, "/api/schedules", null, ct))
            .RequireAuthorization();

        app.MapGet("/api/schedules/metadata", (
            IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<ScheduleProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Get, "/api/schedules/metadata", null, ct))
            .RequireAuthorization();

        app.MapGet("/api/schedules/{id:guid}", (
            Guid id, IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<ScheduleProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Get, $"/api/schedules/{id}", null, ct))
            .RequireAuthorization();

        app.MapGet("/api/schedules/{id:guid}/runs", (
            Guid id, int? limit, IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<ScheduleProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Get,
                $"/api/schedules/{id}/runs{(limit is null ? "" : $"?limit={limit}")}", null, ct))
            .RequireAuthorization();

        // A preview computes upcoming times and changes nothing, so it needs no more than a
        // signed-in user - it is how the editor explains itself while being filled in.
        app.MapPost("/api/schedules/preview", (
            JsonElement body, IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<ScheduleProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Post, "/api/schedules/preview", body, ct))
            .RequireAuthorization();

        app.MapPost("/api/schedules", (
            JsonElement body, IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<ScheduleProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Post, "/api/schedules", body, ct))
            .RequireAuthorization("AdminOnly");

        app.MapPut("/api/schedules/{id:guid}", (
            Guid id, JsonElement body, IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<ScheduleProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Put, $"/api/schedules/{id}", body, ct))
            .RequireAuthorization("AdminOnly");

        app.MapDelete("/api/schedules/{id:guid}", (
            Guid id, IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<ScheduleProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Delete, $"/api/schedules/{id}", null, ct))
            .RequireAuthorization("AdminOnly");

        app.MapPost("/api/schedules/{id:guid}/enabled", (
            Guid id, JsonElement body, IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<ScheduleProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Post, $"/api/schedules/{id}/enabled", body, ct))
            .RequireAuthorization("AdminOnly");

        // AdminOnly, like the automation rules' test button and for the same reason: this
        // actually opens the gate or switches the light, right now.
        app.MapPost("/api/schedules/{id:guid}/run", (
            Guid id, IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<ScheduleProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Post, $"/api/schedules/{id}/run", null, ct))
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
        CancellationToken ct)
    {
        var bridge = await mainAppSettings.GetSectionAsync<BridgeSettingsDto>("Bridge", ct);
        if (bridge is null || string.IsNullOrWhiteSpace(bridge.ApiKey))
            return Results.Json(new { error = "The main app's API key is not configured." }, statusCode: 503);

        var baseUrl = (configuration["MainAppBaseUrl"] ?? "http://127.0.0.1:5080").TrimEnd('/');
        var client = httpClientFactory.CreateClient(nameof(ScheduleProxyEndpoints));

        using var request = new HttpRequestMessage(method, $"{baseUrl}{path}");
        request.Headers.TryAddWithoutValidation("X-Api-Key", bridge.ApiKey);
        if (body is not null) request.Content = JsonContent.Create(body.Value);

        try
        {
            using var response = await client.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Schedule proxy {Method} {Path} failed: {StatusCode} {Body}", method, path, (int)response.StatusCode, responseBody);

            return Results.Content(responseBody, "application/json", statusCode: (int)response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Could not reach the main app at {BaseUrl} for {Path}", baseUrl, path);
            return Results.Json(new { error = "Could not reach the main app." }, statusCode: 502);
        }
    }
}
