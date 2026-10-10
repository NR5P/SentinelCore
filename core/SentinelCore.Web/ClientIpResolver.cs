namespace SentinelCore.Web;

// Resolves the real client address for logging/rate-limiting, accounting for Internet Exposed
// Mode traffic arriving via a local reverse proxy (cloudflared) rather than a direct connection.
//
// On the LAN ports, RemoteIpAddress is the actual TCP peer and is trusted directly - nothing
// sits in front of Kestrel there, so a forwarded-for header could only be a LAN client lying
// about itself, and trusting it would let anyone on the LAN spoof their address in the logs.
//
// On the Internet Exposed port, RemoteIpAddress is always cloudflared's own loopback connection
// (see InternetExposedSettings) - the real internet client is never visible at the TCP layer at
// all. Cloudflare's edge sets CF-Connecting-IP on every request it proxies, and cloudflared
// passes it through unmodified, so that header is what identifies the real caller there.
//
// The headers are trusted only when the TCP peer itself is loopback - cloudflared on this
// machine, as docs/DEPLOYMENT.md 14b sets it up. The port listens on every interface, so if the
// operator's firewall rule is missing, anyone who can reach it directly could otherwise send a
// new made-up address with each request and walk straight past the per-IP login lockout. A
// direct connection is judged by its real address instead.
public static class ClientIpResolver
{
    public static string Resolve(HttpContext context, InternetExposedSettings internetExposed)
    {
        var isExposedRequest = internetExposed.Enabled && context.Connection.LocalPort == internetExposed.Port;
        var viaLocalProxy = context.Connection.RemoteIpAddress is { } peer && System.Net.IPAddress.IsLoopback(peer);
        if (isExposedRequest && viaLocalProxy)
        {
            var cfConnectingIp = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(cfConnectingIp))
                return cfConnectingIp;

            var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(forwardedFor))
                return forwardedFor.Split(',')[0].Trim();
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
