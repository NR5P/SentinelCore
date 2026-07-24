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
    private readonly TelegramOptions _telegramOptions;
    private readonly SelfMonitoringOptions _options;
    private readonly ILogger<SelfMonitoringWorker> _logger;

    private readonly DegradationTracker _ollamaTracker;
    private readonly DegradationTracker _queueTracker;

    public SelfMonitoringWorker(
        IOllamaVisionClient ollama,
        IAlertQueue queue,
        IHttpClientFactory httpClientFactory,
        IOptions<TelegramOptions> telegramOptions,
        IOptions<SelfMonitoringOptions> options,
        ILogger<SelfMonitoringWorker> logger)
    {
        _ollama = ollama;
        _queue = queue;
        _httpClient = httpClientFactory.CreateClient(nameof(SelfMonitoringWorker));
        _telegramOptions = telegramOptions.Value;
        _options = options.Value;
        _logger = logger;
        _ollamaTracker = new DegradationTracker(_options.ConsecutiveFailuresBeforeAlert);
        _queueTracker = new DegradationTracker(_options.ConsecutiveBacklogChecksBeforeAlert);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;

        var canNotify = _telegramOptions.Enabled &&
            !string.IsNullOrWhiteSpace(_telegramOptions.BotToken) &&
            !string.IsNullOrWhiteSpace(_telegramOptions.ChatId);
        if (!canNotify)
            _logger.LogWarning("SelfMonitoring is enabled but Telegram is not configured; degradations will only be logged.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunChecksAsync(canNotify, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A self-monitoring check cycle failed unexpectedly");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(_options.CheckIntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task RunChecksAsync(bool canNotify, CancellationToken cancellationToken)
    {
        var ollamaReady = await _ollama.IsReadyAsync(cancellationToken);
        await HandleTransitionAsync(
            _ollamaTracker,
            ollamaReady,
            canNotify,
            alertText: "Ollama is unreachable. Camera alerts will queue up and go stale until it recovers.",
            recoveryText: "Ollama is reachable again. Alert processing has resumed.",
            cancellationToken);

        var queueCount = _queue.ApproximateCount;
        var queueHealthy = queueCount < _options.QueueBacklogThreshold;
        await HandleTransitionAsync(
            _queueTracker,
            queueHealthy,
            canNotify,
            alertText: $"The alert queue has backed up to {queueCount} pending item(s); analysis may be falling behind.",
            recoveryText: $"The alert queue has caught back up ({queueCount} pending).",
            cancellationToken);
    }

    private async Task HandleTransitionAsync(
        DegradationTracker tracker,
        bool isHealthy,
        bool canNotify,
        string alertText,
        string recoveryText,
        CancellationToken cancellationToken)
    {
        switch (tracker.Observe(isHealthy))
        {
            case DegradationTransition.Alert:
                _logger.LogWarning("{Text}", alertText);
                if (canNotify) await SendAlertAsync(alertText, cancellationToken);
                break;
            case DegradationTransition.Recovered:
                _logger.LogInformation("{Text}", recoveryText);
                if (canNotify && _options.NotifyOnRecovery) await SendAlertAsync(recoveryText, cancellationToken);
                break;
            case DegradationTransition.NoChange:
            default:
                break;
        }
    }

    private Task SendAlertAsync(string text, CancellationToken cancellationToken) =>
        _httpClient.PostAsync(
            $"{ApiBaseUrl}/bot{_telegramOptions.BotToken}/sendMessage",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["chat_id"] = _telegramOptions.ChatId,
                ["text"] = text
            }),
            cancellationToken);
}
