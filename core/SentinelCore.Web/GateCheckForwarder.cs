using System.Net.Http.Json;

namespace SentinelCore.Web;

// Posts a gate/door-check snapshot to the main app's own /api/gate-check - not /api/blueiris/alert
// (see AlertForwarder), because that endpoint runs the full AlertProcessor queue and
// AlertNotificationPolicy this feature deliberately bypasses. Same X-Api-Key/MainAppBaseUrl
// pattern as AlertForwarder otherwise.
public sealed record GateCheckOutcome(
    string State,
    bool Blocked,
    DateTimeOffset? StateChangedAt,
    DateTimeOffset? NotifiedAt,
    bool NotifiedOpen,
    bool NotifiedBlocked);

public sealed class GateCheckForwarder(
    IHttpClientFactory httpClientFactory,
    AppSettingsFile mainAppSettings,
    IConfiguration configuration,
    ILogger<GateCheckForwarder> logger)
{
    public async Task<GateCheckOutcome?> ForwardAsync(byte[] imageBytes, Camera camera, GateCheckConfig config, CancellationToken cancellationToken)
    {
        var bridge = await mainAppSettings.GetSectionAsync<BridgeSettingsDto>("Bridge", cancellationToken);
        if (bridge is null || string.IsNullOrWhiteSpace(bridge.ApiKey))
        {
            logger.LogWarning("Cannot run the gate check for {Camera} - the main app's API key is not configured.", camera.Name);
            return null;
        }

        var baseUrl = (configuration["MainAppBaseUrl"] ?? "http://127.0.0.1:5080").TrimEnd('/');

        using var content = new MultipartFormDataContent();
        var imageContent = new ByteArrayContent(imageBytes);
        imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        content.Add(imageContent, "image", "gate-check.jpg");
        content.Add(new StringContent(camera.Name), "camera");
        content.Add(new StringContent(config.CheckLabel), "checkLabel");
        content.Add(new StringContent(config.CheckBlocked ? "true" : "false"), "checkBlocked");
        content.Add(new StringContent(config.LastKnownState), "previousState");
        content.Add(new StringContent(config.LastKnownBlocked ? "true" : "false"), "previousBlocked");
        if (config.LastStateChangedAt is { } changedAt)
            content.Add(new StringContent(changedAt.ToString("O")), "lastStateChangedAt");
        if (config.LastNotifiedAt is { } notifiedAt)
            content.Add(new StringContent(notifiedAt.ToString("O")), "lastNotifiedAt");
        if (config.RemindIfStillOpenAfterHours is { } hours)
            content.Add(new StringContent(hours.ToString()), "remindIfStillOpenAfterHours");

        var client = httpClientFactory.CreateClient(nameof(GateCheckForwarder));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/gate-check") { Content = content };
        request.Headers.TryAddWithoutValidation("X-Api-Key", bridge.ApiKey);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning("Gate check for {Camera} failed: {StatusCode} {Body}", camera.Name, (int)response.StatusCode, body);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<GateCheckOutcome>(cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // TaskCanceledException also covers the HttpClient's own timeout firing (a slow
            // Ollama response past the 330s configured in Program.cs) - that's a real, expected
            // failure mode for this specific request, not the caller's cancellationToken, so it
            // belongs here alongside HttpRequestException rather than escaping uncaught.
            logger.LogWarning(ex, "Could not reach the main app at {BaseUrl} for a gate check on {Camera}", baseUrl, camera.Name);
            return null;
        }
    }
}
