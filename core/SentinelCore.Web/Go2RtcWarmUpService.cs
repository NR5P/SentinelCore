namespace SentinelCore.Web;

// Starts the go2rtc sidecar at boot rather than leaving it to whichever viewer happens to open the
// Live tab first - see Go2RtcManager.WarmUpAsync for why that viewer was getting a raw deal.
//
// Best effort throughout: go2rtc is optional, and an install without it must boot exactly as it
// always did, with live tiles quietly using HLS. So nothing here can fail startup, and nothing here
// blocks it either.
public sealed class Go2RtcWarmUpService(Go2RtcManager go2Rtc, ILogger<Go2RtcWarmUpService> logger)
    : BackgroundService
{
    // A restart is the one moment the sidecar is most likely to fail to start, and for a reason
    // that fixes itself: the outgoing process may still be holding the API, RTSP and WebRTC ports
    // for a moment after systemd has started the replacement. So give the old one a beat to let go
    // before the first attempt.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(3);

    // Comfortably past Go2RtcManager's transient-failure backoff, so a retry actually gets to try
    // rather than bouncing straight off the blackout it just set.
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(25);

    private const int Attempts = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);

            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                try
                {
                    await go2Rtc.WarmUpAsync(stoppingToken);
                    return;
                }
                catch (Go2RtcUnavailableException ex)
                {
                    // Nothing alarming on an install that simply doesn't have go2rtc - the manager
                    // has already logged that case at Information. This is the retry trail for the
                    // case that does recover, kept at Debug so a machine without go2rtc doesn't
                    // grow three warnings on every boot.
                    logger.LogDebug("go2rtc warm-up attempt {Attempt} of {Attempts} failed: {Reason}",
                        attempt, Attempts, ex.Message);
                }

                if (attempt < Attempts) await Task.Delay(RetryDelay, stoppingToken);
            }

            logger.LogInformation(
                "go2rtc did not start after {Attempts} attempts - live tiles will use HLS until it does. "
                + "A viewer opening the Live tab will try again.", Attempts);
        }
        catch (OperationCanceledException)
        {
            // Shutting down mid-warm-up is not a problem worth reporting.
        }
    }
}
