namespace SentinelCore.Client.Networking;

// Pure parsing helpers kept out of AppModel so they can be unit tested without a window, a
// network, or a server. Both of these decide whether the app talks to the server at all - a base
// address that normalizes wrong shows "Enter a valid server address" on a perfectly good one - so
// they are worth pinning down. The Android client has the same pair, for the same reason.
public static class ServerAddress
{
    // The plain-HTTP port the dashboard ships with. A custom HTTP port can't be derived from an
    // HTTPS address, so this is the only sensible guess - callers probe the result before actually
    // switching to it.
    public const int DefaultHttpPort = 3320;

    /// <summary>
    /// The address to use as the API base, or null if it isn't usable.
    /// Only http/https with a real host qualify. Trailing slashes are dropped so callers can
    /// concatenate paths that start with "/" without doubling the separator.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim().TrimEnd('/');
        if (trimmed.Length == 0) return null;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        return string.IsNullOrWhiteSpace(uri.Host) ? null : trimmed;
    }

    /// <summary>
    /// What the user typed, turned into something Normalize accepts. A bare LAN address
    /// ("192.168.1.50:3320") gets the default scheme rather than being rejected.
    /// </summary>
    /// <remarks>
    /// With no scheme passed, a home-network address (an IP, a .local name, a bare computer name)
    /// gets http://, which is how the dashboard ships; anything else is an internet address and
    /// gets https:// - typing "home.example.com" must never send a password across the internet in
    /// plain text. The remote address passes "https" to always get https://. Same rule as the
    /// iPhone, Apple TV and Android setup screens.
    /// </remarks>
    public static string AddSchemeIfMissing(string raw, string? defaultScheme = null)
    {
        var trimmed = (raw ?? "").Trim();
        var scheme = defaultScheme ?? (IsHomeNetworkAddress(trimmed) ? "http" : "https");
        var withScheme = trimmed.Contains("://", StringComparison.Ordinal)
            ? trimmed
            : $"{scheme}://{trimmed}";
        return withScheme.TrimEnd('/');
    }

    public static bool IsHomeNetworkAddress(string typed)
    {
        if (!Uri.TryCreate($"http://{typed}", UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return true;
        var host = uri.Host.Trim('[', ']').ToLowerInvariant();
        if (host == "localhost" || !host.Contains('.') || host.Contains(':')) return true;
        if (host.Split('.').All(part => int.TryParse(part, out _))) return true;
        return new[] { ".local", ".lan", ".home", ".internal", ".home.arpa" }.Any(host.EndsWith);
    }

    /// <summary>
    /// The plain-HTTP address to retry an untrusted https:// address on, or null if there isn't
    /// one. <c>https://host[:port]</c> becomes <c>http://host:3320</c>.
    /// </summary>
    public static string? HttpFallback(string? address)
    {
        if (Normalize(address) is not { } normalized) return null;
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != "https") return null;
        return string.IsNullOrWhiteSpace(uri.Host) ? null : $"http://{uri.Host}:{DefaultHttpPort}";
    }

    /// <summary>
    /// The https:// form of a plain-http address, or null when it can't be guessed.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow: swapping the scheme is only sound advice when no port is pinned. On
    /// <c>http://host:3320</c> the https counterpart is a *different* listener on a different port
    /// (3443), not the same one with TLS, so that case gets no guess rather than a wrong one.
    /// </remarks>
    public static string? HttpsEquivalent(string? address)
    {
        if (Normalize(address) is not { } normalized) return null;
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != "http") return null;
        if (!uri.IsDefaultPort) return null;
        return string.IsNullOrWhiteSpace(uri.Host) ? null : $"https://{uri.Host}";
    }

    /// <summary>
    /// The ws:// or wss:// form of an address with the given path, for the MSE live socket. Same
    /// host and port rules as the HTTP base.
    /// </summary>
    public static Uri? WebSocketUri(string? address, string path)
    {
        if (Normalize(address) is not { } normalized) return null;
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)) return null;
        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme == "https" ? "wss" : "ws",
            Path = path,
            Query = "",
        };
        return builder.Uri;
    }
}
