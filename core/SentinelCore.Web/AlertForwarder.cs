namespace SentinelCore.Web;

// Opt-in bridge from a trigger zone into the main app's own alert pipeline: same Ollama
// analysis, the zone's chosen prompt profile, same email/Telegram/webhook dispatch Blue Iris
// alerts already use. Reuses the main app's existing /api/blueiris/alert endpoint rather than
// duplicating any AI or notification logic here - the trigger's snapshot just becomes another
// incoming alert, authenticated with the same API key already configured for Blue Iris.
public sealed class AlertForwarder(
    IHttpClientFactory httpClientFactory,
    AppSettingsFile mainAppSettings,
    IConfiguration configuration,
    ILogger<AlertForwarder> logger)
{
    public async Task<bool> ForwardAsync(
        byte[] imageBytes, IReadOnlyList<byte[]> extraFrames, string cameraName, string profile, DateTimeOffset occurredAt,
        IReadOnlyList<string>? linkedCameraNames, string? incidentId, CancellationToken cancellationToken)
    {
        var bridge = await mainAppSettings.GetSectionAsync<BridgeSettingsDto>("Bridge", cancellationToken);
        if (bridge is null || string.IsNullOrWhiteSpace(bridge.ApiKey))
        {
            logger.LogWarning("Cannot forward trigger to the alert pipeline - the main app's API key is not configured.");
            return false;
        }

        var baseUrl = (configuration["MainAppBaseUrl"] ?? "http://127.0.0.1:5080").TrimEnd('/');

        using var content = new MultipartFormDataContent();
        var imageContent = new ByteArrayContent(imageBytes);
        imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        content.Add(imageContent, "image", "trigger.jpg");
        // Extra burst frames, only present when MultiFrameAnalysis is on for this camera - see
        // TriggerZoneMonitor.ForwardToAlertPipelineAsync. Field names match what
        // /api/blueiris/alert optionally reads (frame1, frame2).
        for (var i = 0; i < extraFrames.Count; i++)
        {
            var frameContent = new ByteArrayContent(extraFrames[i]);
            frameContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            content.Add(frameContent, $"frame{i + 1}", $"trigger-frame{i + 1}.jpg");
        }
        content.Add(new StringContent(cameraName), "camera");
        content.Add(new StringContent(string.IsNullOrWhiteSpace(profile) ? "default" : profile), "profile");
        content.Add(new StringContent(occurredAt.ToString("O")), "occurredAt");
        // Only present for a camera that belongs to a Camera Group - see
        // TriggerZoneMonitor.RecordCompanionCamerasAsync/MaybeTriggerAsync. linkedCameras is a
        // plain display string (comma-joined names), not a foreign key - the main app doesn't
        // need to know anything about companion cameras beyond what to show in a notification.
        if (linkedCameraNames is { Count: > 0 })
            content.Add(new StringContent(string.Join(", ", linkedCameraNames)), "linkedCameras");
        if (!string.IsNullOrWhiteSpace(incidentId))
            content.Add(new StringContent(incidentId), "incidentId");

        var client = httpClientFactory.CreateClient(nameof(AlertForwarder));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/blueiris/alert") { Content = content };
        request.Headers.TryAddWithoutValidation("X-Api-Key", bridge.ApiKey);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning("Forwarding trigger to the alert pipeline failed: {StatusCode} {Body}", (int)response.StatusCode, body);
                return false;
            }

            logger.LogInformation("Forwarded trigger for camera {CameraName} to the alert pipeline", cameraName);
            return true;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Could not reach the main app at {BaseUrl} to forward a trigger", baseUrl);
            return false;
        }
    }
}
