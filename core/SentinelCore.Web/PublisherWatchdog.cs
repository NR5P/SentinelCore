namespace SentinelCore.Web;

// Notices when a webcam device stops streaming, and says so. A laptop left in a room for a week
// stops being a camera the moment it sleeps, loses Wi-Fi, or has its tab closed - and nothing
// else in the system would say a word: the camera's tile goes blank, and clips and trigger zones
// simply stop happening. This is the part that tells the owner, through OwnerNotifier, within
// a couple of minutes, and tells them again when it is back.
//
// Asks go2rtc rather than tracking connections here, for the reason HasLivePublisherAsync gives:
// the publisher's connection is between the browser and the sidecar, and this process never
// sees it end. The rule for what counts as gone lives in PublisherPresence; this is the loop,
// the clock and the message.
public sealed class PublisherWatchdog(
    ICameraStore cameras,
    Go2RtcManager go2rtc,
    OwnerNotifier notifier,
    PublishedMseTranscoder mseTranscoder,
    ILogger<PublisherWatchdog> logger) : BackgroundService
{
    // Thirty seconds between looks is prompt enough for a two-minute grace period and is one
    // loopback HTTP call per webcam device - nothing a Reolink's connection budget cares about.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    // Long enough for a publishing browser's own reconnect (three seconds between attempts) to
    // ride out a Wi-Fi hiccup or a server restart without an alert; short enough that a laptop
    // that has actually gone to sleep is reported while there is still something to do about it.
    public static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(2);

    public PublisherPresence Presence { get; } = new(GracePeriod);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The webcam device watchdog failed a pass");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var all = await cameras.GetAllAsync(cancellationToken);
        var published = all.Where(c => c.IsBrowserPublished).ToList();

        foreach (var stale in Presence.Known().Except(published.Select(c => c.Id)))
        {
            Presence.Forget(stale);
            await mseTranscoder.StopAsync(stale);
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var camera in published)
        {
            if (cancellationToken.IsCancellationRequested) return;

            var live = await go2rtc.HasLivePublisherAsync(camera.Id, cancellationToken);

            // The MSE transcode's lifetime is this, not "is anyone watching". It cannot be started
            // on demand: its input is the publisher's stream, whose keyframes are ~30s apart, so a
            // cold encode has nothing to emit for far longer than the 10s a client allows for a
            // first frame. Started when a viewer arrived, it therefore lost that race every time -
            // the viewer fell back to HLS, the encode was reaped for having no consumers, and the
            // next viewer repeated it. Kept warm alongside the publisher it is already ~20% of a
            // core to run, the encode emits a keyframe every second (-g 30), and an MSE viewer
            // gets a picture immediately.
            if (live)
                await mseTranscoder.EnsureRunningAsync(camera, cancellationToken);
            else
                await mseTranscoder.StopAsync(camera.Id);

            switch (Presence.Observe(camera.Id, live, now))
            {
                case PublisherPresence.Transition.Dropped:
                    await ReportDroppedAsync(camera, now, cancellationToken);
                    break;
                case PublisherPresence.Transition.Recovered:
                    await ReportRecoveredAsync(camera, cancellationToken);
                    break;
            }
        }
    }

    private async Task ReportDroppedAsync(Camera camera, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var status = Presence.Get(camera.Id);
        var lastSeen = status?.LastLiveAt;
        var subject = $"Webcam device \"{camera.Name}\" has stopped streaming";
        var text =
            $"The server stopped receiving video from \"{camera.Name}\" " +
            (lastSeen is null ? "a few minutes ago." : $"at {lastSeen.Value.ToLocalTime():yyyy-MM-dd HH:mm}.") + "\n\n" +
            "The device it runs on has most likely gone to sleep, lost its network, or had the tab or app closed. " +
            "Nothing is being recorded from it, and its trigger zones are not being watched, until it is back.\n\n" +
            "To fix it: wake the device. If it streams from the Sentinel Core app (a phone, Mac or PC), open the app - " +
            "it picks streaming back up on its own. If it streams from a browser, open the dashboard on it and on the " +
            "Cameras tab press edit on this camera and save (or reopen its publish page). " +
            "Then keep the device plugged in with sleep turned off.\n\n" +
            "You'll get one more message when it is streaming again.";

        logger.LogWarning("Webcam device {Camera} ({Id}) has stopped streaming (last seen {LastSeen})", camera.Name, camera.Id, lastSeen);
        if (!await notifier.SendAsync(subject, text, cancellationToken))
            logger.LogInformation("No email, Telegram or push channel is configured, so nobody was told");
    }

    private async Task ReportRecoveredAsync(Camera camera, CancellationToken cancellationToken)
    {
        logger.LogInformation("Webcam device {Camera} ({Id}) is streaming again", camera.Name, camera.Id);
        await notifier.SendAsync(
            $"Webcam device \"{camera.Name}\" is streaming again",
            $"The server is receiving video from \"{camera.Name}\" again. Recording and trigger zones have resumed.",
            cancellationToken);
    }
}
