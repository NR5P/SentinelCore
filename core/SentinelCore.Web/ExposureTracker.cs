using System.Net;

namespace SentinelCore.Web;

// Notices when a request arrives from outside the local network, which is direct evidence this
// dashboard is reachable further than intended - both services bind to 0.0.0.0 by design (that's
// what makes them reachable from a phone on the same Wi-Fi), so the app can't tell from its own
// configuration whether it's LAN-only or facing the internet. An off-LAN request is the first
// hard proof either way.
//
// Deliberately observational: it never blocks, never rate-limits, never changes a response. A
// false positive would be someone reaching the dashboard over a VPN that hands out public-range
// addresses, and misreporting that is a far better failure than refusing their request.
public sealed class ExposureTracker
{
    private readonly object _gate = new();

    public bool HasSeenExternalRequest { get; private set; }
    public string? LastExternalAddress { get; private set; }
    public DateTimeOffset? LastExternalAt { get; private set; }
    // True when that request came through a tunnel or reverse proxy, which usually means the owner
    // published the dashboard on purpose and the visitor reached it over HTTPS at the proxy.
    public bool LastExternalViaProxy { get; private set; }

    public void Record(IPAddress address, bool viaProxy = false)
    {
        lock (_gate)
        {
            HasSeenExternalRequest = true;
            LastExternalAddress = address.ToString();
            LastExternalAt = DateTimeOffset.Now;
            LastExternalViaProxy = viaProxy;
        }
    }

    // Loopback, RFC1918, RFC6598 carrier-grade NAT, link-local, and their IPv6 equivalents.
    // Anything else is treated as off-LAN.
    public static bool IsLocalNetwork(IPAddress? address)
    {
        if (address is null) return true;

        if (IPAddress.IsLoopback(address)) return true;

        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            // fe80::/10 link-local, fc00::/7 unique-local - the IPv6 analogues of the ranges below.
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || IsIPv6UniqueLocal(address);
        }

        var octets = address.GetAddressBytes();
        if (octets.Length != 4) return true;

        return octets[0] switch
        {
            10 => true,                                          // 10.0.0.0/8
            127 => true,                                         // 127.0.0.0/8
            169 when octets[1] == 254 => true,                   // 169.254.0.0/16 link-local
            172 when octets[1] >= 16 && octets[1] <= 31 => true,  // 172.16.0.0/12
            192 when octets[1] == 168 => true,                    // 192.168.0.0/16
            100 when octets[1] >= 64 && octets[1] <= 127 => true, // 100.64.0.0/10 CGNAT
            _ => false
        };
    }

    private static bool IsIPv6UniqueLocal(IPAddress address) => (address.GetAddressBytes()[0] & 0xFE) == 0xFC;

    // Headers a reverse proxy or tunnel adds on the way in: cloudflared (CF-Connecting-IP, CF-Ray),
    // nginx/Caddy/Traefik/ngrok/Tailscale Funnel (X-Forwarded-For, X-Real-IP, Forwarded), Akamai
    // and Cloudflare Enterprise (True-Client-IP). A tunnel's own connection is loopback, so without
    // these the whole internet would look like the LAN.
    private static readonly string[] ForwardingHeaders =
        ["CF-Connecting-IP", "CF-Ray", "X-Forwarded-For", "X-Real-IP", "Forwarded", "True-Client-IP"];

    public static bool ArrivedThroughProxy(HttpRequest request) =>
        ForwardingHeaders.Any(header => request.Headers.ContainsKey(header));

    // For decisions that must not be fooled: a direct connection from a LAN address, with no sign
    // of a proxy in between. Any forwarding header disqualifies the request - its contents can be
    // forged, but a client can only ever add such a header, never strip the one a proxy adds, so
    // this can only err towards refusing.
    public static bool IsDirectFromLocalNetwork(HttpContext context) =>
        IsLocalNetwork(context.Connection.RemoteIpAddress) && !ArrivedThroughProxy(context.Request);

    // For the exposure warning: the first address a proxy reports that is outside the LAN, or the
    // proxy's own peer when it names no client (CF-Ray alone). Null when everything named is local -
    // a LAN reverse proxy relaying a LAN device. Observational only, so forged values just mislabel
    // a warning.
    public static IPAddress? ForwardedExternalClient(HttpRequest request)
    {
        if (!ArrivedThroughProxy(request)) return null;
        var named = new List<string>();
        foreach (var header in new[] { "CF-Connecting-IP", "True-Client-IP", "X-Real-IP", "X-Forwarded-For" })
            foreach (var value in request.Headers[header])
                if (value is not null) named.AddRange(value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        foreach (var value in request.Headers["Forwarded"])
            if (value is not null)
                foreach (var part in value.Split([',', ';'], StringSplitOptions.TrimEntries))
                    if (part.StartsWith("for=", StringComparison.OrdinalIgnoreCase))
                        named.Add(part[4..].Trim('"', '[', ']'));

        var parsed = named.Select(n => IPAddress.TryParse(n, out var ip) ? ip : null).Where(ip => ip is not null).ToList();
        if (parsed.Count == 0) return request.HttpContext.Connection.RemoteIpAddress ?? IPAddress.Loopback;
        return parsed.FirstOrDefault(ip => !IsLocalNetwork(ip));
    }
}
