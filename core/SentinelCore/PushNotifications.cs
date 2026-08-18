using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace SentinelCore;

public sealed record PushMessage(
    string Title,
    string Body,
    string? EventId,
    string? Camera,
    string? EventType)
{
    public static PushMessage FromAlert(AlertResult result)
    {
        var analysis = result.Analysis;
        var title = string.IsNullOrWhiteSpace(result.Camera)
            ? "Sentinel Core alert"
            : result.Camera;
        var body = string.IsNullOrWhiteSpace(analysis?.Summary)
            ? "New security camera activity"
            : analysis.Summary;
        return new PushMessage(
            title,
            body,
            result.Id.ToString(),
            result.Camera,
            analysis?.EventType);
    }

    public static PushMessage Test() => new(
        "Sentinel Core test",
        "Push notifications are connected and ready.",
        null,
        null,
        "test");
}

public sealed record PushDeliveryReport(int Attempted, int Delivered, int Failed, int Removed);

public sealed class PushNotificationDispatcher(
    IPushDeviceStore devices,
    IHttpClientFactory httpClientFactory,
    IHostEnvironment environment,
    ILogger<PushNotificationDispatcher> logger)
{
    private readonly SemaphoreSlim _appleTokenLock = new(1, 1);
    private readonly SemaphoreSlim _firebaseTokenLock = new(1, 1);
    private string? _appleToken;
    private string? _appleTokenIdentity;
    private DateTimeOffset _appleTokenCreatedAt;
    private string? _firebaseToken;
    private string? _firebaseTokenIdentity;
    private DateTimeOffset _firebaseTokenExpiresAt;

    public async Task<PushDeliveryReport> SendAsync(
        PushNotificationOptions options,
        PushMessage message,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled) return new PushDeliveryReport(0, 0, 0, 0);

        var registered = await devices.GetAllAsync(cancellationToken);
        var attempted = 0;
        var delivered = 0;
        var failed = 0;
        var removed = 0;

        foreach (var device in registered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PushAttempt attempt;
            try
            {
                attempt = device.Platform switch
                {
                    PushPlatforms.Apple when options.Apple.Enabled =>
                        await SendAppleAsync(options.Apple, device.Token, device.AppVariant, message, cancellationToken),
                    PushPlatforms.Android when options.Firebase.Enabled =>
                        await SendFirebaseAsync(options.Firebase, device.Token, message, cancellationToken),
                    _ => PushAttempt.Skipped
                };
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Push delivery failed for {Platform} device {DeviceId}",
                    device.Platform,
                    device.Id);
                attempt = PushAttempt.Failed;
            }

            if (attempt == PushAttempt.Skipped) continue;
            attempted++;
            if (attempt == PushAttempt.Delivered)
            {
                delivered++;
                continue;
            }

            failed++;
            if (attempt == PushAttempt.Unregistered)
            {
                await devices.RemoveAsync(device.Platform, device.Token, cancellationToken);
                removed++;

                // Logged here, at the point the row is actually deleted, rather than left to the
                // callers - neither of them reads PushDeliveryReport.Removed, so this permanent
                // change to the device store had no record anywhere. It matters most in the case
                // that reads as harmless: with two devices registered and one token gone stale,
                // PushAlertAction reports "delivered to 1/2 registered devices", which is
                // indistinguishable from a transient partial failure even though that device has
                // been dropped for good and will never be attempted again. Re-registration only
                // happens when the app next launches and calls /api/push/devices/register, so
                // without this line a phone or Apple TV can stop receiving alerts indefinitely
                // and the journal's only hint is a rejection warning that says nothing about the
                // registration being removed.
                logger.LogWarning(
                    "Removed the push registration for {Platform} device {DeviceName} ({DeviceId}) because the provider " +
                    "reported its token is no longer valid - that device receives no further pushes until its app " +
                    "re-registers on next launch",
                    device.Platform,
                    string.IsNullOrWhiteSpace(device.DeviceName) ? "unnamed" : device.DeviceName,
                    device.Id);
            }
        }

        return new PushDeliveryReport(attempted, delivered, failed, removed);
    }

    private async Task<PushAttempt> SendAppleAsync(
        ApplePushOptions options,
        string deviceToken,
        string appVariant,
        PushMessage message,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.TeamId) ||
            string.IsNullOrWhiteSpace(options.KeyId) ||
            string.IsNullOrWhiteSpace(options.BundleId) ||
            string.IsNullOrWhiteSpace(options.PrivateKeyPath))
        {
            logger.LogWarning("Apple push is enabled but its provider credentials are incomplete.");
            return PushAttempt.Failed;
        }

        var topic = options.ResolveApnsTopic(appVariant);

        var providerToken = await GetAppleProviderTokenAsync(options, cancellationToken);
        var host = options.UseSandbox
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

        using var response = await httpClientFactory.CreateClient(nameof(PushNotificationDispatcher))
            .SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return PushAttempt.Delivered;

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var reason = TryReadJsonProperty(responseBody, "reason");
        logger.LogWarning(
            "APNs rejected a push with HTTP {StatusCode}: {Reason}",
            (int)response.StatusCode,
            reason ?? "unknown");
        return response.StatusCode == HttpStatusCode.Gone ||
               string.Equals(reason, "Unregistered", StringComparison.OrdinalIgnoreCase)
            ? PushAttempt.Unregistered
            : PushAttempt.Failed;
    }

    private async Task<string> GetAppleProviderTokenAsync(
        ApplePushOptions options,
        CancellationToken cancellationToken)
    {
        var identity = $"{options.TeamId}|{options.KeyId}|{options.PrivateKeyPath}";
        if (_appleToken is not null &&
            identity == _appleTokenIdentity &&
            DateTimeOffset.UtcNow - _appleTokenCreatedAt < TimeSpan.FromMinutes(50))
        {
            return _appleToken;
        }

        await _appleTokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_appleToken is not null &&
                identity == _appleTokenIdentity &&
                DateTimeOffset.UtcNow - _appleTokenCreatedAt < TimeSpan.FromMinutes(50))
            {
                return _appleToken;
            }

            var path = ResolveSecretPath(options.PrivateKeyPath);
            var privateKey = await File.ReadAllTextAsync(path, cancellationToken);
            var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                alg = "ES256",
                kid = options.KeyId
            }));
            var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
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

            _appleToken = $"{unsignedToken}.{Base64Url(signature)}";
            _appleTokenIdentity = identity;
            _appleTokenCreatedAt = DateTimeOffset.UtcNow;
            return _appleToken;
        }
        finally
        {
            _appleTokenLock.Release();
        }
    }

    private async Task<PushAttempt> SendFirebaseAsync(
        FirebasePushOptions options,
        string deviceToken,
        PushMessage message,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ProjectId) ||
            string.IsNullOrWhiteSpace(options.ServiceAccountPath))
        {
            logger.LogWarning("Firebase push is enabled but its service-account configuration is incomplete.");
            return PushAttempt.Failed;
        }

        var accessToken = await GetFirebaseAccessTokenAsync(options, cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://fcm.googleapis.com/v1/projects/{Uri.EscapeDataString(options.ProjectId)}/messages:send")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    message = new
                    {
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

        using var response = await httpClientFactory.CreateClient(nameof(PushNotificationDispatcher))
            .SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return PushAttempt.Delivered;

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        logger.LogWarning(
            "FCM rejected a push with HTTP {StatusCode}: {Response}",
            (int)response.StatusCode,
            Truncate(responseBody, 500));
        return responseBody.Contains("UNREGISTERED", StringComparison.OrdinalIgnoreCase)
            ? PushAttempt.Unregistered
            : PushAttempt.Failed;
    }

    private async Task<string> GetFirebaseAccessTokenAsync(
        FirebasePushOptions options,
        CancellationToken cancellationToken)
    {
        var identity = $"{options.ProjectId}|{options.ServiceAccountPath}";
        if (_firebaseToken is not null &&
            identity == _firebaseTokenIdentity &&
            DateTimeOffset.UtcNow < _firebaseTokenExpiresAt - TimeSpan.FromMinutes(5))
        {
            return _firebaseToken;
        }

        await _firebaseTokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_firebaseToken is not null &&
                identity == _firebaseTokenIdentity &&
                DateTimeOffset.UtcNow < _firebaseTokenExpiresAt - TimeSpan.FromMinutes(5))
            {
                return _firebaseToken;
            }

            var path = ResolveSecretPath(options.ServiceAccountPath);
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
            var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                alg = "RS256",
                typ = "JWT"
            }));
            var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
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
            var assertion = $"{unsignedAssertion}.{Base64Url(signature)}";

            using var request = new HttpRequestMessage(HttpMethod.Post, tokenUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = assertion
                })
            };
            using var response = await httpClientFactory.CreateClient(nameof(PushNotificationDispatcher))
                .SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Firebase OAuth returned HTTP {(int)response.StatusCode}: {Truncate(responseBody, 300)}");
            }

            var tokenResponse = JsonSerializer.Deserialize<FirebaseTokenResponse>(responseBody)
                ?? throw new InvalidOperationException("Firebase OAuth returned invalid JSON.");
            if (string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
                throw new InvalidOperationException("Firebase OAuth did not return an access token.");

            _firebaseToken = tokenResponse.AccessToken;
            _firebaseTokenIdentity = identity;
            _firebaseTokenExpiresAt = issuedAt.AddSeconds(Math.Max(60, tokenResponse.ExpiresIn));
            return _firebaseToken;
        }
        finally
        {
            _firebaseTokenLock.Release();
        }
    }

    private string ResolveSecretPath(string configuredPath) =>
        Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? TryReadJsonProperty(string json, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(propertyName, out var value)
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private enum PushAttempt
    {
        Skipped,
        Delivered,
        Failed,
        Unregistered
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

public sealed class PushAlertAction(
    PushNotificationDispatcher dispatcher,
    IOptionsMonitor<PushNotificationOptions> optionsMonitor,
    IOptionsMonitor<QuietHoursOptions> quietHoursMonitor,
    ILogger<PushAlertAction> logger) : IAlertAction
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task ExecuteAsync(AlertResult result, CancellationToken cancellationToken)
    {
        var options = optionsMonitor.CurrentValue;
        if (!options.Enabled || result.Analysis is null) return;
        if (options.OnlyWhenNotificationRecommended && !AlertNotificationPolicy.ShouldNotify(result)) return;

        var minimumConfidence = QuietHoursPolicy.EffectiveMinimumConfidence(
            quietHoursMonitor.CurrentValue,
            options.MinimumConfidence,
            result.OccurredAt);
        if (result.Analysis.Confidence < minimumConfidence) return;

        var category = AlertCategorizer.Categorize(result.Analysis) ?? result.Analysis.EventType;
        var cooldownKey = $"{result.Camera}|{category}";
        if (options.DuplicateCooldownMinutes > 0 &&
            _lastSent.TryGetValue(cooldownKey, out var lastSent) &&
            DateTimeOffset.UtcNow - lastSent < TimeSpan.FromMinutes(options.DuplicateCooldownMinutes))
        {
            logger.LogInformation(
                "Duplicate push category {Category} suppressed for camera {Camera}",
                category,
                result.Camera);
            return;
        }

        var report = await dispatcher.SendAsync(options, PushMessage.FromAlert(result), cancellationToken);
        if (report.Delivered > 0)
        {
            _lastSent[cooldownKey] = DateTimeOffset.UtcNow;
            logger.LogInformation(
                "Push alert {AlertId} delivered to {Delivered}/{Attempted} registered devices",
                result.Id,
                report.Delivered,
                report.Attempted);
        }
        else if (report.Attempted > 0)
        {
            logger.LogWarning(
                "Push alert {AlertId} failed for all {Attempted} attempted devices",
                result.Id,
                report.Attempted);
        }
    }
}
