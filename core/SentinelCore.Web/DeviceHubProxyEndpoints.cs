using System.Net.Http.Json;
using System.Text.Json;

namespace SentinelCore.Web;

// Proxies Device Hub reads, commands, and CRUD to the main app - the devices, the MQTT
// subscription, and the broker connection all live over there (see DeviceHubEndpoints and
// MqttDeviceBus in the SentinelCore project). Same forwarding shape as
// AutomationRuleProxyEndpoints: read the API key out of the shared appsettings.json, forward the
// call, pass the response straight through.
// Marker type only, for a distinctly-named ILogger<T> category - see the note on
// AutomationRuleProxyLog for why the static class itself can't be named here.
public sealed class DeviceHubProxyLog;

public static class DeviceHubProxyEndpoints
{
    public static void MapDeviceHubProxyEndpoints(this WebApplication app)
    {
        // Whether the tab should exist at all. Deliberately NOT AdminOnly, unlike /api/settings:
        // a Viewer needs to know whether to show the Device Hub, and this leaks nothing beyond
        // a single boolean. Every client (web, iOS, macOS, tvOS, Android) gates its tab on this.
        // Answered straight from the shared settings file rather than forwarded, since the main
        // app would only be reading the same file back to us.
        app.MapGet("/api/device-hub/enabled", async (AppSettingsFile mainAppSettings, CancellationToken ct) =>
        {
            var hub = await mainAppSettings.GetSectionAsync<DeviceHubSettingsDto>("DeviceHub", ct);
            var mqtt = await mainAppSettings.GetSectionAsync<MqttSettingsDto>("Mqtt", ct);
            return Results.Ok(new { enabled = (hub?.Enabled ?? false) && (mqtt?.Enabled ?? false) });
        }).RequireAuthorization();

        // Reading the device list and switching something on are what a Viewer signs in to do -
        // a household member who can't turn on a light has no use for this tab.
        app.MapGet("/api/devices", (
            IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<DeviceHubProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Get, "/api/devices", null, ct))
            .RequireAuthorization();

        app.MapGet("/api/devices/metadata", (
            IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<DeviceHubProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Get, "/api/devices/metadata", null, ct))
            .RequireAuthorization();

        app.MapGet("/api/devices/state", (
            IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<DeviceHubProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Get, "/api/devices/state", null, ct))
            .RequireAuthorization();

        app.MapGet("/api/devices/{id:guid}", (
            Guid id, IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<DeviceHubProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Get, $"/api/devices/{id}", null, ct))
            .RequireAuthorization();

        app.MapPost("/api/devices/{id:guid}/command", (
            Guid id, JsonElement body, IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<DeviceHubProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Post, $"/api/devices/{id}/command", body, ct))
            .RequireAuthorization();

        // Everything below points a device at an arbitrary MQTT topic, or sweeps the whole
        // broker - that's configuration, not use, so it's held to the same bar as Settings.
        app.MapPost("/api/devices", (
            JsonElement body, IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<DeviceHubProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Post, "/api/devices", body, ct))
            .RequireAuthorization("AdminOnly");

        app.MapPut("/api/devices/{id:guid}", (
            Guid id, JsonElement body, IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<DeviceHubProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Put, $"/api/devices/{id}", body, ct))
            .RequireAuthorization("AdminOnly");

        // Rearranging the list is configuration the same way editing a device is - the order is
        // stored on the devices themselves and shared by every account.
        app.MapPut("/api/devices/order", (
            JsonElement body, IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<DeviceHubProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Put, "/api/devices/order", body, ct))
            .RequireAuthorization("AdminOnly");

        app.MapDelete("/api/devices/{id:guid}", (
            Guid id, IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<DeviceHubProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Delete, $"/api/devices/{id}", null, ct))
            .RequireAuthorization("AdminOnly");

        app.MapPost("/api/devices/discover", (
            IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<DeviceHubProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Post, "/api/devices/discover", null, ct))
            .RequireAuthorization("AdminOnly");

        app.MapGet("/api/devices/discover/{scanId:guid}", (
            Guid scanId, IHttpClientFactory httpClientFactory, AppSettingsFile mainAppSettings, IConfiguration configuration,
            ILogger<DeviceHubProxyLog> logger, CancellationToken ct) =>
            ForwardAsync(httpClientFactory, mainAppSettings, configuration, logger, HttpMethod.Get, $"/api/devices/discover/{scanId}", null, ct))
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
        var client = httpClientFactory.CreateClient(nameof(DeviceHubProxyEndpoints));

        using var request = new HttpRequestMessage(method, $"{baseUrl}{path}");
        request.Headers.TryAddWithoutValidation("X-Api-Key", bridge.ApiKey);
        if (body is not null) request.Content = JsonContent.Create(body.Value);

        try
        {
            using var response = await client.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Device Hub proxy {Method} {Path} failed: {StatusCode} {Body}", method, path, (int)response.StatusCode, responseBody);

            return Results.Content(responseBody, "application/json", statusCode: (int)response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Could not reach the main app at {BaseUrl} for {Path}", baseUrl, path);
            return Results.Json(new { error = "Could not reach the main app." }, statusCode: 502);
        }
    }
}
