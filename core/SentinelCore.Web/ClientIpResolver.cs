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
// passes it through unmodified, so that header is what identifies the real caller there. This is
// safe to trust only because docs/DEPLOYMENT.md 14b has the operator firewall this port to
// 127.0.0.1 - the only thing that can ever open a TCP connection to it - so nothing but
// cloudflared itself is in a position to set that header before Kestrel sees it. Only used for
// logging/rate-limiting, never for an authorization decision, so a misconfigured firewall would
// at worst mislabel a log line rather than open a security hole.
public static class ClientIpResolver
{
    public static string Resolve(HttpContext context, InternetExposedSettings internetExposed)
    {
        var isExposedRequest = internetExposed.Enabled && context.Connection.LocalPort == internetExposed.Port;
        if (isExposedRequest)
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
