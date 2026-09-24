using System.Net.Http.Json;
using System.Text.Json;

namespace SentinelCore.Web;

// Talks to the main app's /api/backup/hub (HubBackupEndpoints in the SentinelCore project) for
// the devices, automation rules and schedules that live in its database rather than this one's.
// Same addressing and API-key rules as the proxy endpoints - see AutomationRuleProxyEndpoints.
public sealed class MainAppHubBackupClient(
    IHttpClientFactory httpClientFactory,
    AppSettingsFile mainAppSettings,
    IConfiguration configuration,
    ILogger<MainAppHubBackupClient> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public sealed record Outcome<T>(T? Value, string? Error);

    public async Task<Outcome<HubBackup>> ExportAsync(CancellationToken ct)
    {
        var (response, error) = await SendAsync(HttpMethod.Get, "/api/backup/hub", null, ct);
        if (response is null) return new(null, error);
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return new(null, $"the main app answered {(int)response.StatusCode} when asked for its devices, rules and schedules");
            return new(await response.Content.ReadFromJsonAsync<HubBackup>(JsonOptions, ct), null);
        }
    }

    // Problems with the section, or a single error saying the main app couldn't be asked.
    public async Task<IReadOnlyList<string>> ValidateAsync(HubBackup backup, CancellationToken ct)
    {
        var (response, error) = await SendAsync(HttpMethod.Post, "/api/backup/hub/validate", backup, ct);
        if (response is null) return [UnreachableForRestore(error)];
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return [$"The main app refused to check the backup's devices, rules and schedules ({(int)response.StatusCode})."];
            var body = await response.Content.ReadFromJsonAsync<ErrorsBody>(JsonOptions, ct);
            return body?.Errors ?? [];
        }
    }

    public async Task<Outcome<HubRestoreCounts>> RestoreAsync(HubBackup backup, CancellationToken ct)
    {
        var (response, error) = await SendAsync(HttpMethod.Post, "/api/backup/hub/restore", backup, ct);
        if (response is null) return new(null, UnreachableForRestore(error));
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Main app hub restore failed: {StatusCode} {Body}", (int)response.StatusCode, body);
                return new(null, $"The main app refused the backup's devices, rules and schedules ({(int)response.StatusCode}).");
            }
            return new(await response.Content.ReadFromJsonAsync<HubRestoreCounts>(JsonOptions, ct), null);
        }
    }

    private static string UnreachableForRestore(string? error) =>
        $"This backup includes Device Hub devices, automation rules and schedules, but {error}. " +
        "Start the main Sentinel Core service and try the restore again - nothing has been changed.";

    private async Task<(HttpResponseMessage? Response, string? Error)> SendAsync(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var bridge = await mainAppSettings.GetSectionAsync<BridgeSettingsDto>("Bridge", ct);
        if (bridge is null || string.IsNullOrWhiteSpace(bridge.ApiKey))
            return (null, "the main app's API key is not configured");

        var baseUrl = (configuration["MainAppBaseUrl"] ?? "http://127.0.0.1:5080").TrimEnd('/');
        var client = httpClientFactory.CreateClient(nameof(MainAppHubBackupClient));
        using var request = new HttpRequestMessage(method, $"{baseUrl}{path}");
        request.Headers.TryAddWithoutValidation("X-Api-Key", bridge.ApiKey);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);

        try
        {
            return (await client.SendAsync(request, ct), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not reach the main app at {BaseUrl} for {Path}", baseUrl, path);
            return (null, $"the main app at {baseUrl} could not be reached");
        }
    }

    private sealed record ErrorsBody(List<string>? Errors);
}

public sealed record HubRestoreCounts(int DevicesRestored, int RulesRestored, int SchedulesRestored);
