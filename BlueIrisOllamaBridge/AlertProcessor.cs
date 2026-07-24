using Microsoft.Extensions.Options;

namespace BlueIrisOllamaBridge;

public sealed class AlertProcessor : BackgroundService
{
    private readonly IAlertQueue _queue;
    private readonly IOllamaVisionClient _ollama;
    private readonly IResultStore _store;
    private readonly IReadOnlyDictionary<string, PromptProfile> _profiles;
    private readonly IEnumerable<IAlertAction> _actions;
    private readonly OllamaOptions _ollamaOptions;
    private readonly ILogger<AlertProcessor> _logger;

    public AlertProcessor(
        IAlertQueue queue,
        IOllamaVisionClient ollama,
        IResultStore store,
        IReadOnlyDictionary<string, PromptProfile> profiles,
        IEnumerable<IAlertAction> actions,
        IOptions<OllamaOptions> ollamaOptions,
        ILogger<AlertProcessor> logger)
    {
        _queue = queue;
        _ollama = ollama;
        _store = store;
        _actions = actions;
        _ollamaOptions = ollamaOptions.Value;
        _logger = logger;
        _profiles = profiles;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var alert in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(alert, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected processing failure for alert {AlertId}", alert.Id);
            }
        }
    }

    private async Task ProcessAsync(IncomingAlert alert, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Analyzing alert {AlertId} from {Camera}", alert.Id, alert.Camera);
        var imagePath = await _store.SaveImageAsync(alert, cancellationToken);
        var profile = _profiles.TryGetValue(alert.Profile, out var selected)
            ? selected
            : _profiles.GetValueOrDefault("default")
              ?? throw new InvalidOperationException("The requested profile and default profile do not exist.");

        try
        {
            var (analysis, raw) = await _ollama.AnalyzeAsync(alert, profile, cancellationToken);
            var result = new AlertResult
            {
                Id = alert.Id,
                Camera = alert.Camera,
                BlueIrisAlertId = alert.BlueIrisAlertId,
                OccurredAt = alert.OccurredAt,
                ReceivedAt = alert.ReceivedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                Profile = alert.Profile,
                Model = _ollamaOptions.Model,
                Status = "completed",
                ImagePath = imagePath,
                Analysis = analysis,
                RawModelResponse = raw
            };
            await _store.SaveAsync(result, cancellationToken);

            foreach (var action in _actions)
            {
                try { await action.ExecuteAsync(result, cancellationToken); }
                catch (Exception ex) { _logger.LogError(ex, "Post-analysis action failed for {AlertId}", alert.Id); }
            }
            _logger.LogInformation("Completed alert {AlertId}: {Summary}", alert.Id, analysis.Summary);
        }
        catch (Exception ex)
        {
            var failed = new AlertResult
            {
                Id = alert.Id,
                Camera = alert.Camera,
                BlueIrisAlertId = alert.BlueIrisAlertId,
                OccurredAt = alert.OccurredAt,
                ReceivedAt = alert.ReceivedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                Profile = alert.Profile,
                Model = _ollamaOptions.Model,
                Status = "failed",
                ImagePath = imagePath,
                Error = ex.ToString()
            };
            await _store.SaveAsync(failed, cancellationToken);
            _logger.LogError(ex, "Analysis failed for alert {AlertId}", alert.Id);
        }
    }
}
