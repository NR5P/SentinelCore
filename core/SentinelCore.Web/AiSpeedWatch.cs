using SentinelCore;

namespace SentinelCore.Web;

// SELF-MONITORING's AI speed warnings (see AiSlowdownRule for what counts as slow), told to the
// owner over every channel through OwnerNotifier, at most once per RepeatAfter for each model and
// kind - the same throttle, persisted the same way, as MemoryWatch.
//
// No consecutive-checks counter like MemoryWatch has: the rule already looks at five analyses,
// which is the smoothing.
public sealed class AiSpeedWatch(IOwnerNotifier notifier, ILogger<AiSpeedWatch> logger, TimeProvider? clock = null, string? statePath = null)
{
    public static readonly TimeSpan RepeatAfter = MemoryWatch.RepeatAfter;

    private readonly WarningThrottle _throttle = new(RepeatAfter, logger, clock, statePath);

    public async Task CheckAsync(IEnumerable<AiSlowdownWarning> warnings, CancellationToken cancellationToken)
    {
        foreach (var warning in warnings)
        {
            if (!_throttle.IsDue(warning.Key)) continue;
            logger.LogWarning("AI speed warning: {Subject}", warning.Subject);
            _throttle.MarkSent(warning.Key);
            if (!await notifier.SendAsync(warning.Subject, warning.Text, cancellationToken))
                logger.LogWarning("AI speed warning: no email, Telegram or push is set up to say so");
        }
    }
}

// Reads events.db directly (this process already shares it through IEventStore) rather than
// asking the main app, so a check costs one query and nothing on the wire.
public sealed class AiSpeedWatchWorker(
    AiSpeedWatch watch,
    IEventStore events,
    AppSettingsFile settings,
    TimeProvider clock,
    ILogger<AiSpeedWatchWorker> logger) : BackgroundService
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var options = await settings.GetSectionAsync<SelfMonitoringSettingsDto>("SelfMonitoring", stoppingToken) ?? new SelfMonitoringSettingsDto();
                if (options.Enabled && (options.AiSlowdownFactor > 0 || options.AiTimeoutWarningPercent > 0))
                {
                    var ollama = await settings.GetSectionAsync<OllamaOptions>("Ollama", stoppingToken) ?? new OllamaOptions();
                    var now = clock.GetUtcNow();
                    var rows = await events.GetAnalysisTimingsAsync(now - AiSpeedStats.Week, stoppingToken);
                    var warnings = AiSlowdownRule.Evaluate(rows, now, options.AiSlowdownFactor, options.AiTimeoutWarningPercent, ollama.RequestTimeoutSeconds);
                    await watch.CheckAsync(warnings, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "The AI speed check failed"); }
            try { await Task.Delay(CheckEvery, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
