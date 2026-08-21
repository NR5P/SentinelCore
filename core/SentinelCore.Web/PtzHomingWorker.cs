using Microsoft.Extensions.Options;

namespace SentinelCore.Web;

public sealed class PtzHomingOptions
{
    public const string SectionName = "PtzHoming";

    // How long a PTZ camera has to sit untouched before it is sent back to its home position.
    //
    // This is the whole "guard position" idea, and the number is a comfort/coverage trade: too
    // short and it drives itself home while you are still deciding where to look next; too long and
    // detection stays off - because a camera away from home is not detecting - for as long as you
    // forget about it. Three minutes is long enough to look around without being interrupted, short
    // enough that walking away from the app doesn't leave the camera blind all evening.
    public int IdleMinutes { get; init; } = 3;

    // Turning this off leaves a PTZ camera wherever it was left, which also means its trigger zones
    // stay off until somebody presses Home. Here because "my camera keeps moving on its own" is a
    // reasonable thing to want to stop, and hunting for the reason is not.
    public bool Enabled { get; init; } = true;
}

// Sends an idle PTZ camera back to its home position, which is what turns its trigger zones back
// on - see PtzActivityTracker for why detection only runs at home.
//
// Only cameras with detection actually configured are homed. Moving a camera nobody is detecting on
// would be the app rearranging the furniture for its own reasons; the whole justification for
// homing is to restore detection, so no detection means no justification.
public sealed class PtzHomingWorker(
    ICameraStore cameras,
    ITriggerConfigStore configStore,
    PtzActivityTracker activity,
    PtzHomeService home,
    IOptions<PtzHomingOptions> options,
    ILogger<PtzHomingWorker> logger) : BackgroundService
{
    // Far shorter than the idle window it is enforcing, so "three minutes" means three minutes and
    // not "three to four". Cheap: it reads in-memory state and only touches a camera when it is
    // actually going to move one.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation(
                "Idle PTZ homing is switched off; PTZ cameras will stay where they are left, and their trigger zones stay off until Home is pressed.");
            return;
        }

        var idleFor = TimeSpan.FromMinutes(Math.Max(1, settings.IdleMinutes));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendIdleCamerasHomeAsync(idleFor, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A camera that can't be reached this pass will be retried on the next one.
                logger.LogWarning(ex, "Idle PTZ homing pass failed");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SendIdleCamerasHomeAsync(TimeSpan idleFor, CancellationToken cancellationToken)
    {
        foreach (var camera in await cameras.GetAllAsync(cancellationToken))
        {
            if (!camera.PtzEnabled) continue;
            if (string.IsNullOrWhiteSpace(camera.OnvifPtzServiceUrl) || string.IsNullOrWhiteSpace(camera.OnvifProfileToken)) continue;
            if (!activity.ShouldSendHome(camera.Id, idleFor)) continue;

            var config = await configStore.GetAsync(camera.Id, cancellationToken);
            if (config is null || !config.Enabled) continue;

            var wasUnknown = activity.PositionOf(camera.Id) == PtzActivityTracker.Position.Unknown;
            var outcome = await home.GoHomeAsync(camera, cancellationToken);

            switch (outcome)
            {
                case PtzHomeService.HomeOutcome.Moved:
                    logger.LogInformation(
                        wasUnknown
                            ? "Sent {Name} to its home position to establish where it is pointing; its trigger zones are live again."
                            : "{Name} has been idle for {Idle:g}, so it has been sent home; its trigger zones are live again.",
                        camera.Name, idleFor);
                    break;

                case PtzHomeService.HomeOutcome.NoHomeSaved:
                    // Rate-limited by the fact that a failed home leaves the position Away, so this
                    // repeats once per poll - which is the point. Detection is off and will stay off
                    // until somebody saves a home, and that is worth being told about.
                    logger.LogWarning(
                        "{Name} has trigger zones but no saved home position, so its detection stays off. " +
                        "Open the camera, point it where home should be, and press and hold Home.",
                        camera.Name);
                    break;
            }
        }
    }
}
