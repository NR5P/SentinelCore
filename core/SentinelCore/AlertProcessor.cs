using Microsoft.Extensions.Options;

namespace SentinelCore;

public sealed class AlertProcessor : BackgroundService
{
    private readonly IAlertQueue _queue;
    private readonly IVisionAnalysisClient _vision;
    private readonly IResultStore _store;
    private readonly IPromptProfileProvider _profileProvider;
    private readonly IEnumerable<IAlertAction> _actions;
    private readonly IOptionsMonitor<List<KnownVehicle>> _knownVehicles;
    private readonly ILogger<AlertProcessor> _logger;

    public AlertProcessor(
        IAlertQueue queue,
        IVisionAnalysisClient vision,
        IResultStore store,
        IPromptProfileProvider profileProvider,
        IEnumerable<IAlertAction> actions,
        IOptionsMonitor<List<KnownVehicle>> knownVehicles,
        ILogger<AlertProcessor> logger)
    {
        _queue = queue;
        _vision = vision;
        _store = store;
        _actions = actions;
        _knownVehicles = knownVehicles;
        _logger = logger;
        _profileProvider = profileProvider;
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
        // Normally already written at ingest; only save here for alerts that arrived some other
        // way (or were queued by an older build that didn't persist first).
        var imagePath = alert.ImagePath ?? await _store.SaveImageAsync(alert, cancellationToken);
        var profiles = _profileProvider.Current;
        var profile = profiles.TryGetValue(alert.Profile, out var selected)
            ? selected
            : profiles.GetValueOrDefault("default")
              ?? throw new InvalidOperationException("The requested profile and default profile do not exist.");

        try
        {
            var (analysis, raw, model) = await _vision.AnalyzeAsync(alert, profile, cancellationToken);
            if (profile.LicensePlateRecognitionEnabled)
            {
                analysis.Vehicle.KnownVehicleLabel = KnownVehicleMatcher.Match(_knownVehicles.CurrentValue, analysis.Vehicle.LicensePlateText);
            }
            var result = new AlertResult
            {
                Id = alert.Id,
                Camera = alert.Camera,
                BlueIrisAlertId = alert.BlueIrisAlertId,
                OccurredAt = alert.OccurredAt,
                ReceivedAt = alert.ReceivedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                Profile = alert.Profile,
                Model = model,
                Status = "completed",
                ImagePath = imagePath,
                Analysis = analysis,
                RawModelResponse = raw
            };
            await _store.SaveAsync(result, cancellationToken);

            // Never let the visibility guard drop an alert silently. In practice this fires when
            // the frame is unreadable and the model still writes a subject into the prose, but it
            // would also catch a reply that parsed yet omitted the subject blocks - and that
            // second case is worth seeing in the log rather than quietly losing.
            if (analysis.NotificationRecommended && !AlertNotificationPolicy.ShouldNotify(result))
            {
                _logger.LogInformation(
                    "Alert {AlertId} recommended a notification but no subject was visible - suppressed. Summary: {Summary}",
                    alert.Id, analysis.Summary);
            }

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
                // Analysis never completed, so there's no single backend to attribute this to -
                // it may have been attempted against both if a fallback was configured.
                Model = "unavailable",
                Status = "failed",
                ImagePath = imagePath,
                Error = ex.ToString()
            };
            await _store.SaveAsync(failed, cancellationToken);
            _logger.LogError(ex, "Analysis failed for alert {AlertId}", alert.Id);
        }
    }
}
