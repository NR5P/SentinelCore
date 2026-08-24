using Microsoft.Extensions.Options;

namespace SentinelCore;

public enum DegradationTransition
{
    NoChange,
    Alert,
    Recovered
}

// Requires several consecutive unhealthy observations before declaring degraded (avoids
// paging on a single blip), but declares recovery on the very first healthy observation
// afterward (no reason to delay good news).
public sealed class DegradationTracker(int consecutiveBeforeAlert)
{
    private int _consecutiveUnhealthy;

    public bool IsDegraded { get; private set; }

    public DegradationTransition Observe(bool isHealthy)
    {
        if (!isHealthy)
        {
            _consecutiveUnhealthy++;
            if (!IsDegraded && _consecutiveUnhealthy >= consecutiveBeforeAlert)
            {
                IsDegraded = true;
                return DegradationTransition.Alert;
            }

            return DegradationTransition.NoChange;
        }

        _consecutiveUnhealthy = 0;
        if (IsDegraded)
        {
            IsDegraded = false;
            return DegradationTransition.Recovered;
        }

        return DegradationTransition.NoChange;
    }
}

// Watches for the three ways this service can silently stop protecting the gate: the vision
// analysis backend becoming unreachable, the alert queue backing up faster than it's
// processed, or the disk filling up underneath all of it. Pushes a Telegram alert only on
// state transitions, not on every check, so it doesn't spam.
public sealed class SelfMonitoringWorker : BackgroundService
{
    private const string ApiBaseUrl = "https://api.telegram.org";

    private const long BytesPerGigabyte = 1024L * 1024 * 1024;

    // How far free space has to climb back above the warning threshold before the disk counts as
    // recovered. Without a margin this flaps: the motion retention sweep runs every 12 hours and
    // reclaims between 0.5 and 5 GB in a single pass (measured on this deployment), so a disk
    // sitting near the threshold would cross it in both directions and alert every time. A one-way
    // margin is the right shape - go degraded the moment you're under, come back only once you're
    // clear of it - because the alert exists to be acted on, not to track the boundary precisely.
    private const double RecoveryMargin = 1.10;

    private readonly IVisionAnalysisClient _vision;
    private readonly IAlertQueue _queue;
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<TelegramOptions> _telegramOptionsMonitor;
    private readonly IOptionsMonitor<SelfMonitoringOptions> _optionsMonitor;
    private readonly IOptionsMonitor<BridgeOptions> _bridgeOptionsMonitor;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<SelfMonitoringWorker> _logger;

    // Rebuilt whenever ConsecutiveFailuresBeforeAlert/ConsecutiveBacklogChecksBeforeAlert
    // change, since a tracker's threshold is fixed at construction.
    private DegradationTracker _ollamaTracker;
    private DegradationTracker _queueTracker;
    private int _ollamaTrackerThreshold;
    private int _queueTrackerThreshold;

    // Fixed at 1: unlike a flaky backend or a momentary queue spike, a disk that reads as low is
    // low. Waiting for repeat observations would only delay the warning. The flapping this would
    // otherwise invite is handled by RecoveryMargin instead, which is the axis it actually happens
    // on.
    private readonly DegradationTracker _diskTracker = new(1);

    public SelfMonitoringWorker(
        IVisionAnalysisClient vision,
        IAlertQueue queue,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<TelegramOptions> telegramOptions,
        IOptionsMonitor<SelfMonitoringOptions> options,
        IOptionsMonitor<BridgeOptions> bridgeOptions,
        IHostEnvironment environment,
        ILogger<SelfMonitoringWorker> logger)
    {
        _vision = vision;
        _queue = queue;
        _httpClient = httpClientFactory.CreateClient(nameof(SelfMonitoringWorker));
        _telegramOptionsMonitor = telegramOptions;
        _optionsMonitor = options;
        _bridgeOptionsMonitor = bridgeOptions;
        _environment = environment;
        _logger = logger;

        var initial = options.CurrentValue;
        _ollamaTrackerThreshold = initial.ConsecutiveFailuresBeforeAlert;
        _queueTrackerThreshold = initial.ConsecutiveBacklogChecksBeforeAlert;
        _ollamaTracker = new DegradationTracker(_ollamaTrackerThreshold);
        _queueTracker = new DegradationTracker(_queueTrackerThreshold);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _optionsMonitor.CurrentValue;
            if (!options.Enabled)
            {
                if (!await WaitAsync(TimeSpan.FromSeconds(60), stoppingToken)) break;
                continue;
            }

            RebuildTrackersIfThresholdsChanged(options);

            var telegramOptions = _telegramOptionsMonitor.CurrentValue;
            var canNotify = telegramOptions.Enabled &&
                !string.IsNullOrWhiteSpace(telegramOptions.BotToken) &&
                !string.IsNullOrWhiteSpace(telegramOptions.ChatId);
            if (!canNotify)
                _logger.LogWarning("SelfMonitoring is enabled but Telegram is not configured; degradations will only be logged.");

            try
            {
                await RunChecksAsync(options, telegramOptions, canNotify, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A self-monitoring check cycle failed unexpectedly");
            }

            if (!await WaitAsync(TimeSpan.FromSeconds(options.CheckIntervalSeconds), stoppingToken)) break;
        }
    }

    private void RebuildTrackersIfThresholdsChanged(SelfMonitoringOptions options)
    {
        if (options.ConsecutiveFailuresBeforeAlert != _ollamaTrackerThreshold)
        {
            _ollamaTrackerThreshold = options.ConsecutiveFailuresBeforeAlert;
            _ollamaTracker = new DegradationTracker(_ollamaTrackerThreshold);
        }

        if (options.ConsecutiveBacklogChecksBeforeAlert != _queueTrackerThreshold)
        {
            _queueTrackerThreshold = options.ConsecutiveBacklogChecksBeforeAlert;
            _queueTracker = new DegradationTracker(_queueTrackerThreshold);
        }
    }

    private static async Task<bool> WaitAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task RunChecksAsync(
        SelfMonitoringOptions options,
        TelegramOptions telegramOptions,
        bool canNotify,
        CancellationToken cancellationToken)
    {
        var visionReady = await _vision.IsReadyAsync(cancellationToken);
        await HandleTransitionAsync(
            _ollamaTracker,
            visionReady,
            canNotify,
            telegramOptions,
            alertText: "The vision analysis backend is unreachable. Camera alerts will queue up and go stale until it recovers.",
            recoveryText: "The vision analysis backend is reachable again. Alert processing has resumed.",
            options.NotifyOnRecovery,
            cancellationToken);

        var queueCount = _queue.ApproximateCount;
        var queueHealthy = queueCount < options.QueueBacklogThreshold;
        await HandleTransitionAsync(
            _queueTracker,
            queueHealthy,
            canNotify,
            telegramOptions,
            alertText: $"The alert queue has backed up to {queueCount} pending item(s); analysis may be falling behind.",
            recoveryText: $"The alert queue has caught back up ({queueCount} pending).",
            options.NotifyOnRecovery,
            cancellationToken);

        await CheckDiskAsync(options, telegramOptions, canNotify, cancellationToken);
    }

    // The disk is the one resource here that nothing reclaims on its own. Clips for confirmed
    // motion are kept permanently by design, so free space is monotonically decreasing until
    // someone intervenes - which makes "you have N GB left" a thing you want pushed at you rather
    // than something to notice on the dashboard. Nothing else in either service warns about this:
    // ScheduledRecorder has a floor, but it only refuses to start a recording once you are already
    // there, and SystemStatsSampler measures free space purely to display it.
    private async Task CheckDiskAsync(
        SelfMonitoringOptions options,
        TelegramOptions telegramOptions,
        bool canNotify,
        CancellationToken cancellationToken)
    {
        if (options.FreeDiskWarningGigabytes <= 0) return;

        var freeBytes = TryGetFreeDiskBytes();

        // A disk we could not measure is not a disk we know to be full. Skipping the observation
        // entirely - rather than reporting healthy - also leaves an existing warning standing,
        // since a measurement that starts failing is no reason to announce recovery.
        if (freeBytes is not { } free) return;

        var healthy = IsFreeSpaceHealthy(free, options.FreeDiskWarningGigabytes, _diskTracker.IsDegraded);
        await HandleTransitionAsync(
            _diskTracker,
            healthy,
            canNotify,
            telegramOptions,
            alertText:
                $"Free disk space is down to {DescribeGigabytes(free)}, below the {options.FreeDiskWarningGigabytes} GB " +
                "warning level. Saved clips for confirmed motion are never deleted automatically, so this will not " +
                "recover on its own.",
            recoveryText: $"Free disk space has recovered to {DescribeGigabytes(free)}.",
            options.NotifyOnRecovery,
            cancellationToken);
    }

    // Pure and public for the tests. The asymmetry is the whole point: the threshold decides when
    // to start warning, the threshold plus RecoveryMargin decides when to stop.
    public static bool IsFreeSpaceHealthy(long freeBytes, int warnGigabytes, bool currentlyDegraded)
    {
        if (warnGigabytes <= 0) return true;

        var threshold = warnGigabytes * (double)BytesPerGigabyte;
        return freeBytes >= (currentlyDegraded ? threshold * RecoveryMargin : threshold);
    }

    private static string DescribeGigabytes(long bytes) =>
        $"{bytes / (double)BytesPerGigabyte:0.0} GB";

    // Resolved the same way SystemStatsSampler and RetentionWorker resolve it, so all three agree
    // on which disk "the disk" means. Note this is the INGEST service's data root - on a split
    // install the dashboard's trigger clips, which are the bulk of the footprint, may sit on a
    // different volume that this check would not see.
    private long? TryGetFreeDiskBytes()
    {
        try
        {
            var configured = _bridgeOptionsMonitor.CurrentValue.DataDirectory;
            var root = Path.IsPathRooted(configured) ? configured : Path.Combine(_environment.ContentRootPath, configured);
            var driveRoot = Path.GetPathRoot(Path.GetFullPath(root));
            if (string.IsNullOrEmpty(driveRoot)) return null;

            return new DriveInfo(driveRoot).AvailableFreeSpace;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not measure free disk space; skipping the disk check this cycle");
            return null;
        }
    }

    private async Task HandleTransitionAsync(
        DegradationTracker tracker,
        bool isHealthy,
        bool canNotify,
        TelegramOptions telegramOptions,
        string alertText,
        string recoveryText,
        bool notifyOnRecovery,
        CancellationToken cancellationToken)
    {
        switch (tracker.Observe(isHealthy))
        {
            case DegradationTransition.Alert:
                _logger.LogWarning("{Text}", alertText);
                if (canNotify) await SendAlertAsync(telegramOptions, alertText, cancellationToken);
                break;
            case DegradationTransition.Recovered:
                _logger.LogInformation("{Text}", recoveryText);
                if (canNotify && notifyOnRecovery) await SendAlertAsync(telegramOptions, recoveryText, cancellationToken);
                break;
            case DegradationTransition.NoChange:
            default:
                break;
        }
    }

    private Task SendAlertAsync(TelegramOptions telegramOptions, string text, CancellationToken cancellationToken) =>
        _httpClient.PostAsync(
            $"{ApiBaseUrl}/bot{telegramOptions.BotToken}/sendMessage",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["chat_id"] = telegramOptions.ChatId,
                ["text"] = text
            }),
            cancellationToken);
}
