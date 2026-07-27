using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueIrisOllamaBridge;

namespace BlueIrisOllamaBridge.Web;

public sealed record BlueIrisAlertClip(
    string AlertRecord,
    string ClipRecord,
    DateTimeOffset OccurredAt);
public sealed record BlueIrisAlertClipResult(
    bool Configured,
    bool Available,
    string Camera,
    int PreSeconds,
    int PostSeconds,
    IReadOnlyList<BlueIrisAlertClip> Clips,
    string? Error);
public sealed record BlueIrisFrame(byte[] Bytes, string ContentType);

public sealed class BlueIrisTimelineClient(
    AppSettingsFile settings,
    IHttpClientFactory clients,
    ILogger<BlueIrisTimelineClient> logger)
{
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private string? _session;
    private string? _sessionIdentity;
    private DateTimeOffset _sessionCreatedAt;

    public async Task<BlueIrisAlertClipResult> GetAlertClipsAsync(
        string requestedCamera,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var options = await GetOptionsAsync(cancellationToken);
        if (!IsConfigured(options))
            return new(
                false,
                false,
                requestedCamera,
                options.EventClipPreSeconds,
                options.EventClipPostSeconds,
                [],
                "Blue Iris trigger-clip access is not configured.");

        var camera = ResolveCamera(options, requestedCamera);
        try
        {
            var session = await GetSessionAsync(options, false, cancellationToken);
            using var response = await PostCommandAsync(options, new
            {
                cmd = "alertlist",
                session,
                camera,
                startdate = start.ToUnixTimeSeconds(),
                enddate = end.ToUnixTimeSeconds()
            }, cancellationToken);

            if (!IsSuccess(response.RootElement))
            {
                session = await GetSessionAsync(options, true, cancellationToken);
                using var retry = await PostCommandAsync(options, new
                {
                    cmd = "alertlist",
                    session,
                    camera,
                    startdate = start.ToUnixTimeSeconds(),
                    enddate = end.ToUnixTimeSeconds()
                }, cancellationToken);
                return ParseAlertClips(retry.RootElement, camera, options);
            }

            return ParseAlertClips(response.RootElement, camera, options);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not load the Blue Iris trigger clips for {Camera}", camera);
            return new(
                true,
                false,
                camera,
                options.EventClipPreSeconds,
                options.EventClipPostSeconds,
                [],
                exception.Message);
        }
    }

    public async Task<BlueIrisFrame?> GetFrameAsync(
        string requestedCamera,
        DateTimeOffset at,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var options = await GetOptionsAsync(cancellationToken);
        if (!IsConfigured(options)) return null;

        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 300)));
        var requestToken = requestTimeout.Token;
        var session = await GetSessionAsync(options, false, requestToken);
        var camera = ResolveCamera(options, requestedCamera);
        var uri = BuildUri(options.BaseUrl, $"/time/{Uri.EscapeDataString(camera)}", new Dictionary<string, string>
        {
            ["jpeg"] = "1",
            ["pos"] = at.ToUnixTimeMilliseconds().ToString(),
            ["w"] = Math.Clamp(width, 320, 1920).ToString(),
            ["h"] = Math.Clamp(height, 180, 1080).ToString(),
            ["isolate"] = "1",
            ["session"] = session
        });

        using var response = await clients.CreateClient(nameof(BlueIrisTimelineClient))
            .GetAsync(uri, requestToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            session = await GetSessionAsync(options, true, requestToken);
            uri = BuildUri(options.BaseUrl, $"/time/{Uri.EscapeDataString(camera)}", new Dictionary<string, string>
            {
                ["jpeg"] = "1",
                ["pos"] = at.ToUnixTimeMilliseconds().ToString(),
                ["w"] = Math.Clamp(width, 320, 1920).ToString(),
                ["h"] = Math.Clamp(height, 180, 1080).ToString(),
                ["isolate"] = "1",
                ["session"] = session
            });
            using var retry = await clients.CreateClient(nameof(BlueIrisTimelineClient))
                .GetAsync(uri, requestToken);
            if (!retry.IsSuccessStatusCode) return null;
            return new(
                await retry.Content.ReadAsByteArrayAsync(requestToken),
                retry.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
        }

        if (!response.IsSuccessStatusCode) return null;
        return new(
            await response.Content.ReadAsByteArrayAsync(requestToken),
            response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
    }

    private async Task<BlueIrisOptions> GetOptionsAsync(CancellationToken cancellationToken) =>
        await settings.GetSectionAsync<BlueIrisOptions>(BlueIrisOptions.SectionName, cancellationToken)
        ?? new BlueIrisOptions();

    private async Task<string> GetSessionAsync(
        BlueIrisOptions options,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var identity = $"{options.BaseUrl}|{options.Username}";
        if (!forceRefresh &&
            _session is not null &&
            identity == _sessionIdentity &&
            DateTimeOffset.UtcNow - _sessionCreatedAt < TimeSpan.FromMinutes(20))
            return _session;

        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh &&
                _session is not null &&
                identity == _sessionIdentity &&
                DateTimeOffset.UtcNow - _sessionCreatedAt < TimeSpan.FromMinutes(20))
                return _session;

            using var challenge = await PostCommandAsync(options, new { cmd = "login" }, cancellationToken);
            var session = challenge.RootElement.TryGetProperty("session", out var sessionValue)
                ? sessionValue.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(session))
                throw new InvalidOperationException("Blue Iris did not return a login session.");

            var material = $"{options.Username}:{session}:{options.Password}";
            var responseHash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(material)))
                .ToLowerInvariant();
            using var login = await PostCommandAsync(options, new
            {
                cmd = "login",
                session,
                response = responseHash
            }, cancellationToken);
            if (!IsSuccess(login.RootElement))
                throw new InvalidOperationException("Blue Iris rejected the configured clip credentials.");

            _session = session;
            _sessionIdentity = identity;
            _sessionCreatedAt = DateTimeOffset.UtcNow;
            return session;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task<JsonDocument> PostCommandAsync(
        BlueIrisOptions options,
        object command,
        CancellationToken cancellationToken)
    {
        var uri = BuildUri(options.BaseUrl, "/json", new Dictionary<string, string>());
        using var content = new StringContent(
            JsonSerializer.Serialize(command),
            Encoding.UTF8,
            "application/json");
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 300)));
        try
        {
            using var response = await clients.CreateClient(nameof(BlueIrisTimelineClient))
                .PostAsync(uri, content, requestTimeout.Token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(requestTimeout.Token);
            return await JsonDocument.ParseAsync(stream, cancellationToken: requestTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Blue Iris did not respond within {options.TimeoutSeconds} seconds.");
        }
    }

    private static BlueIrisAlertClipResult ParseAlertClips(
        JsonElement root,
        string camera,
        BlueIrisOptions options)
    {
        if (!IsSuccess(root))
            return new(
                true,
                false,
                camera,
                options.EventClipPreSeconds,
                options.EventClipPostSeconds,
                [],
                "Blue Iris rejected the trigger-clip request.");

        var data = root.TryGetProperty("data", out var nested) ? nested : root;
        if (data.ValueKind != JsonValueKind.Array)
            return new(
                true,
                true,
                camera,
                options.EventClipPreSeconds,
                options.EventClipPostSeconds,
                [],
                null);

        var clips = new List<BlueIrisAlertClip>();
        foreach (var alert in data.EnumerateArray())
        {
            var alertRecord = GetString(alert, "path");
            var clipRecord = GetString(alert, "clip");
            if (string.IsNullOrWhiteSpace(alertRecord) ||
                string.IsNullOrWhiteSpace(clipRecord) ||
                !alert.TryGetProperty("date", out var dateValue) ||
                !dateValue.TryGetInt64(out var date))
                continue;

            clips.Add(new BlueIrisAlertClip(
                NormalizeRecord(alertRecord),
                clipRecord,
                DateTimeOffset.FromUnixTimeSeconds(date)));
        }

        return new(
            true,
            true,
            camera,
            options.EventClipPreSeconds,
            options.EventClipPostSeconds,
            clips,
            null);
    }

    public static string NormalizeRecord(string? value)
    {
        var record = value?.Trim() ?? string.Empty;
        var extension = record.LastIndexOf('.');
        return extension > 0 ? record[..extension] : record;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsSuccess(JsonElement root) =>
        root.TryGetProperty("result", out var result) &&
        string.Equals(result.GetString(), "success", StringComparison.OrdinalIgnoreCase);

    private static bool IsConfigured(BlueIrisOptions options) =>
        options.Enabled &&
        !string.IsNullOrWhiteSpace(options.BaseUrl) &&
        (!string.IsNullOrWhiteSpace(options.Camera) || options.CameraMappings.Count > 0);

    private static string ResolveCamera(BlueIrisOptions options, string requestedCamera)
    {
        if (!string.IsNullOrWhiteSpace(requestedCamera) &&
            options.CameraMappings.TryGetValue(requestedCamera, out var mapped) &&
            !string.IsNullOrWhiteSpace(mapped))
            return mapped;
        if (options.CameraMappings.Values.Contains(requestedCamera, StringComparer.OrdinalIgnoreCase))
            return requestedCamera;
        // Backward-compatible single-camera setup: the event's friendly camera name may not
        // resemble Blue Iris's short name at all.
        return string.IsNullOrWhiteSpace(options.Camera) ? requestedCamera : options.Camera;
    }

    private static Uri BuildUri(
        string baseUrl,
        string path,
        IReadOnlyDictionary<string, string> query)
    {
        var builder = new UriBuilder($"{baseUrl.TrimEnd('/')}{path}");
        if (query.Count > 0)
            builder.Query = string.Join('&', query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return builder.Uri;
    }
}
