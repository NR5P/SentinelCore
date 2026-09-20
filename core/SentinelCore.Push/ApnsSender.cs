using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SentinelCore;

// One push to one Apple device, signed with the operator's APNs auth key. Used directly by the
// main app when it holds the key itself, and by SentinelCore.PushRelay on behalf of installs that
// don't. Register as a singleton: the provider JWT is cached here for its ~50-minute life.
public sealed class ApnsSender(
    IHttpClientFactory httpClientFactory,
    IHostEnvironment environment,
    ILogger<ApnsSender> logger)
{
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private string? _tokenIdentity;
    private DateTimeOffset _tokenCreatedAt;

    // useSandbox is a parameter rather than read off options because the relay serves both
    // environments at once - a token is sandbox or production per app install, not per operator.
    public async Task<PushAttempt> SendAsync(
        ApplePushOptions options,
        bool useSandbox,
        string deviceToken,
        string topic,
        PushMessage message,
        CancellationToken cancellationToken)
    {
        if (!options.HasCredentials)
        {
            logger.LogWarning("Apple push was attempted but its provider credentials are incomplete.");
            return PushAttempt.Failed;
        }

        var providerToken = await GetProviderTokenAsync(options, cancellationToken);
        var host = useSandbox
            ? "https://api.sandbox.push.apple.com"
            : "https://api.push.apple.com";
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{host}/3/device/{Uri.EscapeDataString(deviceToken)}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    aps = new Dictionary<string, object?>
                    {
                        ["alert"] = new { title = message.Title, body = message.Body },
                        ["sound"] = "default",
                        ["category"] = "SECURITY_EVENT",
                        ["thread-id"] = message.Camera ?? "sentinel-core"
                    },
                    eventId = message.EventId,
                    camera = message.Camera,
                    eventType = message.EventType
                }),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("bearer", providerToken);
        request.Headers.TryAddWithoutValidation("apns-topic", topic);
        request.Headers.TryAddWithoutValidation("apns-push-type", "alert");
        request.Headers.TryAddWithoutValidation("apns-priority", "10");

        using var response = await httpClientFactory.CreateClient(PushSenders.HttpClientName)
            .SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return PushAttempt.Delivered;

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var reason = PushSenders.TryReadJsonProperty(responseBody, "reason");
        logger.LogWarning(
            "APNs rejected a push with HTTP {StatusCode}: {Reason}",
            (int)response.StatusCode,
            reason ?? "unknown");
        return response.StatusCode == HttpStatusCode.Gone ||
               string.Equals(reason, "Unregistered", StringComparison.OrdinalIgnoreCase)
            ? PushAttempt.Unregistered
            : PushAttempt.Failed;
    }

    private async Task<string> GetProviderTokenAsync(ApplePushOptions options, CancellationToken cancellationToken)
    {
        var identity = $"{options.TeamId}|{options.KeyId}|{options.PrivateKeyPath}";
        if (_token is not null &&
            identity == _tokenIdentity &&
            DateTimeOffset.UtcNow - _tokenCreatedAt < TimeSpan.FromMinutes(50))
        {
            return _token;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null &&
                identity == _tokenIdentity &&
                DateTimeOffset.UtcNow - _tokenCreatedAt < TimeSpan.FromMinutes(50))
            {
                return _token;
            }

            var path = PushSenders.ResolveSecretPath(environment, options.PrivateKeyPath);
            var privateKey = await File.ReadAllTextAsync(path, cancellationToken);
            var header = PushSenders.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                alg = "ES256",
                kid = options.KeyId
            }));
            var payload = PushSenders.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                iss = options.TeamId,
                iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }));
            var unsignedToken = $"{header}.{payload}";

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(privateKey);
            var signature = ecdsa.SignData(
                Encoding.ASCII.GetBytes(unsignedToken),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

            _token = $"{unsignedToken}.{PushSenders.Base64Url(signature)}";
            _tokenIdentity = identity;
            _tokenCreatedAt = DateTimeOffset.UtcNow;
            return _token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }
}
