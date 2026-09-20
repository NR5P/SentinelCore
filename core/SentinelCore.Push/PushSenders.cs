using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace SentinelCore;

// Helpers shared by ApnsSender and FcmSender. Kept out of the senders so each reads as just its
// provider's protocol.
internal static class PushSenders
{
    // Kept at the dispatcher's name on purpose: the main app's logging filters (and the test that
    // pins them) name this HttpClient by its category, and both senders share the one client.
    public const string HttpClientName = "PushNotificationDispatcher";

    public static string ResolveSecretPath(IHostEnvironment environment, string configuredPath) =>
        Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath);

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string? TryReadJsonProperty(string json, string propertyName)
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

    public static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];
}
