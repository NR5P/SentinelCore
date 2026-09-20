using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentinelCore;

// Where this process finds its license file. The main app and the web dashboard resolve the path
// differently (one from IConfiguration, the other through the shared appsettings.json), so each
// registers its own implementation; PushRelayClient only ever asks for the file's contents.
public interface IRelayLicenseSource
{
    // The raw .lic file contents when a valid license is installed, otherwise null. Re-read on
    // every call by design - dropping a license in must start working without a restart, same
    // as the dashboard's nag banner clearing.
    Task<string?> ReadAsync(CancellationToken cancellationToken);
}

public sealed class FileRelayLicenseSource(Func<CancellationToken, ValueTask<string>> pathProvider) : IRelayLicenseSource
{
    public FileRelayLicenseSource(string path) : this(_ => ValueTask.FromResult(path)) { }

    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        var path = await pathProvider(cancellationToken);
        if (!File.Exists(path)) return null;
        try
        {
            var contents = await File.ReadAllTextAsync(path, cancellationToken);
            return LicenseStatus.Verify(contents) is null ? null : contents;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

// The wire shapes the relay accepts. Shared with SentinelCore.PushRelay by source rather than by
// reference - the relay project deliberately doesn't depend on this one.
public sealed record RelayRegisterRequest(string Platform, string Token, string AppVariant, bool ApnsSandbox);
public sealed record RelayUnregisterRequest(string Platform, string Token);
public sealed record RelayPushRequest(string Platform, string Token, string AppVariant, PushMessage Message);
public sealed record RelayPushResponse(string Status);

// Talks to the push relay on behalf of an install that has no Apple/Firebase credentials of its
// own. The license file is the credential: sent base64-encoded in a header, verified by the relay
// against the same public key this app carries, and used by the relay to keep one install's
// device tokens separate from another's.
public sealed class PushRelayClient(
    IHttpClientFactory httpClientFactory,
    IRelayLicenseSource licenseSource,
    ILogger<PushRelayClient> logger)
{
    public const string LicenseHeader = "X-Sentinel-License";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private int _warnedUnlicensed;

    public async Task<bool> RegisterAsync(
        RelayPushOptions options, string platform, string token, string appVariant, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(
            options, "/v1/devices",
            new RelayRegisterRequest(PushPlatforms.Normalize(platform), token, appVariant, options.ApnsSandbox),
            cancellationToken);
        if (response is null) return false;
        if (response.IsSuccessStatusCode) return true;

        logger.LogWarning(
            "The push relay refused to register a {Platform} device: HTTP {StatusCode} {Body}",
            platform, (int)response.StatusCode, await ReadBodyAsync(response, cancellationToken));
        return false;
    }

    public async Task<bool> UnregisterAsync(
        RelayPushOptions options, string platform, string token, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(
            options, "/v1/devices/unregister",
            new RelayUnregisterRequest(PushPlatforms.Normalize(platform), token),
            cancellationToken);
        return response?.IsSuccessStatusCode == true;
    }

    public async Task<PushAttempt> SendAsync(
        RelayPushOptions options,
        string platform,
        string token,
        string appVariant,
        PushMessage message,
        CancellationToken cancellationToken)
    {
        var request = new RelayPushRequest(PushPlatforms.Normalize(platform), token, appVariant, message);
        using var response = await PostAsync(options, "/v1/push", request, cancellationToken);
        if (response is null) return PushAttempt.Failed;

        // The relay has never heard of this token - typically a device registered before relay
        // mode was turned on, or the relay's own store having been rebuilt. Register it and try
        // once more rather than waiting for the periodic reconciler.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            if (!await RegisterAsync(options, platform, token, appVariant, cancellationToken))
                return PushAttempt.Failed;
            using var retry = await PostAsync(options, "/v1/push", request, cancellationToken);
            return retry is null ? PushAttempt.Failed : await ReadAttemptAsync(retry, platform, cancellationToken);
        }

        return await ReadAttemptAsync(response, platform, cancellationToken);
    }

    private async Task<PushAttempt> ReadAttemptAsync(
        HttpResponseMessage response, string platform, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "The push relay rejected a {Platform} push: HTTP {StatusCode} {Body}",
                platform, (int)response.StatusCode, await ReadBodyAsync(response, cancellationToken));
            return PushAttempt.Failed;
        }

        RelayPushResponse? result = null;
        try
        {
            result = await response.Content.ReadFromJsonAsync<RelayPushResponse>(JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            // Fall through to the "unknown status" warning.
        }

        switch (result?.Status?.ToLowerInvariant())
        {
            case "delivered":
                return PushAttempt.Delivered;
            case "unregistered":
                return PushAttempt.Unregistered;
            case "failed":
                return PushAttempt.Failed;
            default:
                logger.LogWarning("The push relay returned an unrecognised status {Status}", result?.Status ?? "(none)");
                return PushAttempt.Failed;
        }
    }

    private async Task<HttpResponseMessage?> PostAsync<T>(
        RelayPushOptions options, string path, T body, CancellationToken cancellationToken)
    {
        if (!options.IsConfigured)
        {
            logger.LogWarning("The push relay is enabled but its URL {Url} is not a valid http(s) address.", options.Url);
            return null;
        }

        var license = await licenseSource.ReadAsync(cancellationToken);
        if (license is null)
        {
            // Once, not per device per alert: an unlicensed install with relay mode on would
            // otherwise log this for every push attempt indefinitely.
            if (Interlocked.Exchange(ref _warnedUnlicensed, 1) == 0)
            {
                logger.LogWarning(
                    "Push notifications are set to use the relay at {Url}, but no valid license is installed - " +
                    "the relay only carries pushes for licensed installs, so nothing will be delivered until one is " +
                    "added (Settings → LICENSE) or your own Apple/Firebase credentials are configured.",
                    options.Url);
            }
            return null;
        }
        Interlocked.Exchange(ref _warnedUnlicensed, 0);

        var uri = new Uri(new Uri(options.Url.TrimEnd('/') + "/"), path.TrimStart('/'));
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        request.Headers.TryAddWithoutValidation(LicenseHeader, Convert.ToBase64String(Encoding.UTF8.GetBytes(license)));

        try
        {
            return await httpClientFactory.CreateClient(nameof(PushRelayClient)).SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not reach the push relay at {Url}", options.Url);
            return null;
        }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return body.Length <= 300 ? body : body[..300];
    }
}
