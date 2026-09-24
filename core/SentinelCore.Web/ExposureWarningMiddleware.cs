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
        var address = context.Connection.RemoteIpAddress;

        if (!ExposureTracker.IsLocalNetwork(address))
        {
            tracker.Record(address!);

            bool isNew;
            lock (_gate) isNew = _loggedAddresses.Add(address!.ToString());

            if (isNew)
            {
                if (await AuthSettingsDto.IsLoginRequiredAsync(settings, context.RequestAborted))
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
