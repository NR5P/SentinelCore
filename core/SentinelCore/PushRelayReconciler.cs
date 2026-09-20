using Microsoft.Extensions.Options;

namespace SentinelCore;

// Keeps the relay's view of this install's devices in step with the local device store. The
// dashboard forwards each registration as it happens (PushEndpoints), and PushRelayClient
// registers a token the relay says it doesn't know before retrying a push - this is the sweep
// behind both, so nothing depends on a single forwarded call having got through.
public sealed class PushRelayReconciler(
    IPushDeviceStore devices,
    PushRelayClient relay,
    IOptionsMonitor<PushNotificationOptions> options,
    ILogger<PushRelayReconciler> logger) : BackgroundService
{
    // Not exposed as a setting: a wrong value here can only make the relay's store stale for
    // longer, and the per-push self-heal in PushRelayClient already covers that gap.
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the rest of the host settle first - this is the least urgent thing that happens
        // at boot, and a relay round-trip in the first second competes with camera startup.
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Push relay reconciliation failed; will try again in {Interval}", Interval);
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        if (!PushProviderSelection.UsesRelay(current)) return 0;

        var registered = 0;
        foreach (var device in await devices.GetAllAsync(cancellationToken))
        {
            if (PushProviderSelection.For(current, device.Platform) != PushProvider.Relay) continue;
            if (await relay.RegisterAsync(current.Relay, device.Platform, device.Token, device.AppVariant, cancellationToken))
                registered++;
        }

        if (registered > 0)
            logger.LogInformation("Confirmed {Count} push device registration(s) with the relay at {Url}", registered, current.Relay.Url);
        return registered;
    }
}
