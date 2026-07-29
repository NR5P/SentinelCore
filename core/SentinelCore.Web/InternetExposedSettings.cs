namespace SentinelCore.Web;

// A second, dedicated Kestrel port for traffic reaching this server from outside the LAN - e.g.
// via a Cloudflare Tunnel pointed at this port instead of the normal one. Requests on this port
// always require login and are always genuine HTTPS, by construction: Kestrel terminates real
// TLS here using the same certificate as the optional LAN HTTPS listener (see HttpsSettings),
// rather than trusting a forwarded-proto header from whatever's in front of it. The plain-HTTP
// (and optional-HTTPS) LAN listeners are completely unaffected by this setting.
public sealed record InternetExposedSettings(bool Enabled, int Port)
{
    public const int DefaultPort = 3444;

    public static InternetExposedSettings FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("InternetExposed");
        return new InternetExposedSettings(
            section.GetValue<bool>("Enabled"),
            int.TryParse(section["Port"], out var port) ? port : DefaultPort);
    }

    // Deliberately requires LAN HTTPS to already be configured and loadable - this mode must
    // never fall back to serving forced-login traffic over plain HTTP just because a certificate
    // path went stale. Also requires a different port than the HTTPS listener; sharing one would
    // silently apply forced-login to LAN HTTPS visitors too, which isn't what this setting means.
    public bool CanEnable(HttpsSettings httpsSettings, out string? reason)
    {
        if (!Enabled)
        {
            reason = null;
            return false;
        }

        if (!httpsSettings.CanEnable(out var httpsReason))
        {
            reason = httpsReason
                ?? "Internet Exposed Mode requires a local HTTPS certificate to be configured first - see docs/DEPLOYMENT.md.";
            return false;
        }

        if (Port is < 1 or > 65535)
        {
            reason = $"Internet Exposed port {Port} is not a usable port number.";
            return false;
        }

        if (Port == httpsSettings.Port)
        {
            reason = $"Internet Exposed port {Port} must be different from the HTTPS port.";
            return false;
        }

        reason = null;
        return true;
    }
}
