using System.Net.Http;
using SentinelCore.Client.Networking;

namespace SentinelCore.Client.Organizer;

/// <summary>The engine's transport in the app: this session's SentinelClient.</summary>
public sealed class ClientOrganizerTransport(SentinelClient client) : IOrganizerTransport
{
    public async Task<OrganizerResponse> SendAsync(
        string method,
        string path,
        IReadOnlyDictionary<string, string>? query = null,
        byte[]? body = null,
        string? contentType = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        try
        {
            var (status, data) = await client.OrganizerRequestAsync(method, path, query, body, contentType, timeout, ct)
                .ConfigureAwait(false);
            return new OrganizerResponse(status, data);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException
                                          or SentinelInvalidAddressException or ObjectDisposedException)
        {
            // A timeout, a refused connection, no address yet, a session Refresh just replaced: all
            // "can't reach the server", which the engine answers by keeping everything queued and
            // trying again later.
            throw new OrganizerOfflineException();
        }
    }
}

/// <summary>
/// Where this PC keeps its Organizer copy and which offline policy it uses. A computer keeps the
/// whole thing by default: it is the copy that refills a rebuilt server.
/// </summary>
public static class OrganizerDevice
{
    private const string DeviceIdKey = "organizerDeviceId";
    private const string PolicyKey = "organizerPolicy";
    private const string EnabledKey = "organizerEnabled";

    public static string DataDirectory(string? root = null) => Path.Combine(
        root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SentinelCore"),
        "Organizer");

    /// <summary>Stable for the life of this install; the tiebreak inside every clock this PC writes.</summary>
    public static string Id(PreferenceStore preferences)
    {
        var stored = preferences.GetString(DeviceIdKey);
        if (stored.Length > 0) return stored;
        var id = "windows-" + Guid.NewGuid().ToString("N")[..8];
        preferences.Set(DeviceIdKey, id);
        return id;
    }

    public static OfflinePolicy Policy(PreferenceStore preferences) =>
        OfflinePolicy.FromJson(preferences.GetString(PolicyKey)) ?? OfflinePolicy.FullDefault;

    public static void SavePolicy(PreferenceStore preferences, OfflinePolicy policy) => preferences.Set(PolicyKey, policy.ToJson());

    /// <summary>What the server said last time, so the section is there before it has answered -
    /// or when it can't be reached at all, which is the point of an offline copy.</summary>
    public static bool LastKnownEnabled(PreferenceStore preferences) => preferences.GetBool(EnabledKey);

    public static void SaveLastKnownEnabled(PreferenceStore preferences, bool enabled) => preferences.Set(EnabledKey, enabled);
}
