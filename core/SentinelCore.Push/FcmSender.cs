using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SentinelCore;

// One push to one Android installation through the FCM HTTP v1 API, authenticated with the
// operator's Firebase service account. Same two homes as ApnsSender. Register as a singleton:
// the OAuth access token is cached here until shortly before it expires.
public sealed class FcmSender(
    IHttpClientFactory httpClientFactory,
    IHostEnvironment environment,
    ILogger<FcmSender> logger)
{
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private string? _tokenIdentity;
    private DateTimeOffset _tokenExpiresAt;

    public async Task<PushAttempt> SendAsync(
        FirebasePushOptions options,
        string deviceToken,
        PushMessage message,
        CancellationToken cancellationToken)
    {
        if (!options.HasCredentials)
        {
            logger.LogWarning("Firebase push was attempted but its service-account configuration is incomplete.");
            return PushAttempt.Failed;
        }

        var accessToken = await GetAccessTokenAsync(options, cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://fcm.googleapis.com/v1/projects/{Uri.EscapeDataString(options.ProjectId)}/messages:send")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    message = new
                    {
                        // The Android app registers its Firebase Installation ID, not a legacy
                        // registration token - hence fid rather than token.
                        fid = deviceToken,
                        notification = new { title = message.Title, body = message.Body },
                        data = new Dictionary<string, string>
                        {
                            ["eventId"] = message.EventId ?? string.Empty,
                            ["camera"] = message.Camera ?? string.Empty,
                            ["eventType"] = message.EventType ?? string.Empty
                        },
                        android = new
                        {
                            priority = "high",
                            notification = new
                            {
                                channel_id = "security_alerts",
                                sound = "default",
                                tag = message.EventId ?? "sentinel-test"
                            }
                        }
                    }
                }),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClientFactory.CreateClient(PushSenders.HttpClientName)
            .SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return PushAttempt.Delivered;

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        logger.LogWarning(
            "FCM rejected a push with HTTP {StatusCode}: {Response}",
            (int)response.StatusCode,
            PushSenders.Truncate(responseBody, 500));
        return responseBody.Contains("UNREGISTERED", StringComparison.OrdinalIgnoreCase)
            ? PushAttempt.Unregistered
            : PushAttempt.Failed;
    }

    private async Task<string> GetAccessTokenAsync(FirebasePushOptions options, CancellationToken cancellationToken)
    {
        var identity = $"{options.ProjectId}|{options.ServiceAccountPath}";
        if (_token is not null &&
            identity == _tokenIdentity &&
            DateTimeOffset.UtcNow < _tokenExpiresAt - TimeSpan.FromMinutes(5))
        {
            return _token;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null &&
                identity == _tokenIdentity &&
                DateTimeOffset.UtcNow < _tokenExpiresAt - TimeSpan.FromMinutes(5))
            {
                return _token;
            }

            var path = PushSenders.ResolveSecretPath(environment, options.ServiceAccountPath);
            await using var stream = File.OpenRead(path);
            var account = await JsonSerializer.DeserializeAsync<FirebaseServiceAccount>(
                stream,
                cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Firebase service-account JSON is invalid.");
            if (string.IsNullOrWhiteSpace(account.ClientEmail) ||
                string.IsNullOrWhiteSpace(account.PrivateKey))
            {
                throw new InvalidOperationException("Firebase service-account JSON is missing required fields.");
            }

            var tokenUri = string.IsNullOrWhiteSpace(account.TokenUri)
                ? "https://oauth2.googleapis.com/token"
                : account.TokenUri;
            var issuedAt = DateTimeOffset.UtcNow;
            var header = PushSenders.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                alg = "RS256",
                typ = "JWT"
            }));
            var payload = PushSenders.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                iss = account.ClientEmail,
                scope = "https://www.googleapis.com/auth/firebase.messaging",
                aud = tokenUri,
                iat = issuedAt.ToUnixTimeSeconds(),
                exp = issuedAt.AddMinutes(55).ToUnixTimeSeconds()
            }));
            var unsignedAssertion = $"{header}.{payload}";

            using var rsa = RSA.Create();
            rsa.ImportFromPem(account.PrivateKey);
            var signature = rsa.SignData(
                Encoding.ASCII.GetBytes(unsignedAssertion),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            var assertion = $"{unsignedAssertion}.{PushSenders.Base64Url(signature)}";

            using var request = new HttpRequestMessage(HttpMethod.Post, tokenUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = assertion
                })
            };
            using var response = await httpClientFactory.CreateClient(PushSenders.HttpClientName)
                .SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Firebase OAuth returned HTTP {(int)response.StatusCode}: {PushSenders.Truncate(responseBody, 300)}");
            }

            var tokenResponse = JsonSerializer.Deserialize<FirebaseTokenResponse>(responseBody)
                ?? throw new InvalidOperationException("Firebase OAuth returned invalid JSON.");
            if (string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
                throw new InvalidOperationException("Firebase OAuth did not return an access token.");

            _token = tokenResponse.AccessToken;
            _tokenIdentity = identity;
            _tokenExpiresAt = issuedAt.AddSeconds(Math.Max(60, tokenResponse.ExpiresIn));
            return _token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private sealed class FirebaseServiceAccount
    {
        [JsonPropertyName("client_email")]
        public string ClientEmail { get; init; } = string.Empty;

        [JsonPropertyName("private_key")]
        public string PrivateKey { get; init; } = string.Empty;

        [JsonPropertyName("token_uri")]
        public string TokenUri { get; init; } = string.Empty;
    }

    private sealed class FirebaseTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; init; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; } = 3600;
    }
}
