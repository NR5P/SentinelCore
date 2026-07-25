using Microsoft.Extensions.Options;

namespace BlueIrisOllamaBridge;

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

// Watches for the two ways this service can silently stop protecting the gate: Ollama
// becoming unreachable, or the alert queue backing up faster than it's processed. Pushes a
// Telegram alert only on state transitions, not on every check, so it doesn't spam.
public sealed class SelfMonitoringWorker : BackgroundService
{
    private const string ApiBaseUrl = "https://api.telegram.org";

    private readonly IOllamaVisionClient _ollama;
    private readonly IAlertQueue _queue;
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<TelegramOptions> _telegramOptionsMonitor;
    private readonly IOptionsMonitor<SelfMonitoringOptions> _optionsMonitor;
    private readonly ILogger<SelfMonitoringWorker> _logger;

    // Rebuilt whenever ConsecutiveFailuresBeforeAlert/ConsecutiveBacklogChecksBeforeAlert
    // change, since a tracker's threshold is fixed at construction.
    private DegradationTracker _ollamaTracker;
    private DegradationTracker _queueTracker;
    private int _ollamaTrackerThreshold;
    private int _queueTrackerThreshold;

    public SelfMonitoringWorker(
        IOllamaVisionClient ollama,
        IAlertQueue queue,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<TelegramOptions> telegramOptions,
        IOptionsMonitor<SelfMonitoringOptions> options,
        ILogger<SelfMonitoringWorker> logger)
    {
        _ollama = ollama;
        _queue = queue;
        _httpClient = httpClientFactory.CreateClient(nameof(SelfMonitoringWorker));
        _telegramOptionsMonitor = telegramOptions;
        _optionsMonitor = options;
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
        var ollamaReady = await _ollama.IsReadyAsync(cancellationToken);
        await HandleTransitionAsync(
            _ollamaTracker,
            ollamaReady,
            canNotify,
            telegramOptions,
            alertText: "Ollama is unreachable. Camera alerts will queue up and go stale until it recovers.",
            recoveryText: "Ollama is reachable again. Alert processing has resumed.",
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
