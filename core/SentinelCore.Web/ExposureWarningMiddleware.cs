namespace SentinelCore.Web;

// Watches where requests actually come from. See ExposureTracker for why: the app can't tell
// from config alone whether it's LAN-only, so an off-LAN source address is the first real
// evidence. Logs once per newly-seen address rather than per request, so a genuinely exposed
// instance doesn't drown its own logs.
//
// Never blocks anything. If this is a real exposure the owner needs to see the warning, and if
// it's a false positive (a VPN handing out public-range addresses) they still need their
// dashboard to work.
public sealed class ExposureWarningMiddleware(
    RequestDelegate next,
    ExposureTracker tracker,
    AppSettingsFile settings,
    ILogger<ExposureWarningMiddleware> logger)
{
    private readonly HashSet<string> _loggedAddresses = [];
    private readonly object _gate = new();

    public async Task InvokeAsync(HttpContext context)
    {
        var peer = context.Connection.RemoteIpAddress;
        // Through a tunnel or reverse proxy the peer is the proxy (loopback), so the client it
        // reports is what says whether this dashboard is reachable from the internet.
        var viaProxy = ExposureTracker.IsLocalNetwork(peer);
        var address = viaProxy ? ExposureTracker.ForwardedExternalClient(context.Request) : peer;

        if (address is not null)
        {
            tracker.Record(address, viaProxy);

            bool isNew;
            lock (_gate)
            {
                // Behind a public tunnel every visitor is a new address; start over rather than
                // grow without bound (the cost is one repeat log line per address).
                if (_loggedAddresses.Count >= 1000) _loggedAddresses.Clear();
                isNew = _loggedAddresses.Add(address.ToString());
            }

            if (isNew)
            {
                var loginRequired = await AuthSettingsDto.IsLoginRequiredAsync(settings, context.RequestAborted);
                if (loginRequired && viaProxy)
                {
                    // Published through a tunnel with the login on: the expected setup, not a warning.
                    logger.LogInformation("Request from {Address} through a tunnel or reverse proxy; the dashboard login is on.", address);
                }
                else if (loginRequired)
                {
                    logger.LogWarning(
                        "Request from {Address}, which is outside your local network. The dashboard login is on, " +
                        "but this is plain HTTP - put it behind HTTPS if it's meant to be reachable from the internet.",
                        address);
                }
                else
                {
                    logger.LogWarning(
                        "Request from {Address}, which is outside your local network, and the dashboard login is OFF - " +
                        "anyone who can reach this address can view your cameras and change settings. Turn on the login " +
                        "in Settings, and restrict the port to your LAN unless you meant to publish this.",
                        address);
                }
            }
        }

        await next(context);
    }
}
