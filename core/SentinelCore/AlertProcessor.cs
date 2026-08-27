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
    private readonly IOptionsMonitor<List<KnownFace>> _knownFaces;
    private readonly IFaceRecognitionService _faceRecognition;
    private readonly IPackageWatchStore _packageWatches;
    private readonly IOptionsMonitor<PackageTheftOptions> _packageTheftOptions;
    private readonly IImageEmbeddingService _imageEmbeddings;
    private readonly IOptionsMonitor<SemanticSearchOptions> _semanticSearchOptions;
    private readonly NotificationDeduplicator _deduplicator;
    private readonly ILogger<AlertProcessor> _logger;

    public AlertProcessor(
        IAlertQueue queue,
        IVisionAnalysisClient vision,
        IResultStore store,
        IPromptProfileProvider profileProvider,
        IEnumerable<IAlertAction> actions,
        IOptionsMonitor<List<KnownVehicle>> knownVehicles,
        IOptionsMonitor<List<KnownFace>> knownFaces,
        IFaceRecognitionService faceRecognition,
        IPackageWatchStore packageWatches,
        IOptionsMonitor<PackageTheftOptions> packageTheftOptions,
        IImageEmbeddingService imageEmbeddings,
        IOptionsMonitor<SemanticSearchOptions> semanticSearchOptions,
        NotificationDeduplicator deduplicator,
        ILogger<AlertProcessor> logger)
    {
        _queue = queue;
        _vision = vision;
        _store = store;
        _actions = actions;
        _knownVehicles = knownVehicles;
        _knownFaces = knownFaces;
        _faceRecognition = faceRecognition;
        _packageWatches = packageWatches;
        _packageTheftOptions = packageTheftOptions;
        _imageEmbeddings = imageEmbeddings;
        _semanticSearchOptions = semanticSearchOptions;
        _deduplicator = deduplicator;
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
            if (profile.FaceRecognitionEnabled)
            {
                // Runs entirely locally against the raw alert image, independent of the vision
                // backend above - see FaceRecognitionService for why this doesn't ask the VLM.
                var embeddings = _faceRecognition.DetectFaceEmbeddings(alert.ImageBytes);
                var labels = embeddings.Select(e => KnownFaceMatcher.Match(_knownFaces.CurrentValue, e)).ToList();
                analysis.Person.KnownFaceLabels = labels.OfType<string>().Distinct().ToList();
                analysis.Person.UnrecognizedFaceDetected = labels.Any(l => l is null);
            }
            if (_packageTheftOptions.CurrentValue.Enabled)
            {
                await ProcessPackageWatchAsync(alert.Camera, analysis, _packageTheftOptions.CurrentValue, cancellationToken);
            }
            // Runs entirely locally, independent of the vision backend above - same shape as
            // face recognition. Encoding failures return null (see ImageEmbeddingService) rather
            // than throwing, so a bad frame just leaves this alert out of semantic search instead
            // of failing the whole alert.
            var embedding = _semanticSearchOptions.CurrentValue.Enabled
                ? await _imageEmbeddings.EmbedImageAsync(alert.ImageBytes, cancellationToken)
                : null;

            // Last, so it sees the finished analysis - Package Theft Watch above can still change
            // what this alert is about. Here rather than in any one vision client because every
            // backend feeds this same field, and the Feed filter, automation rules and the chat
            // assistant all read it downstream. See EventTypeClassifier for why this corrects the
            // model instead of rejecting it.
            analysis.EventType = EventTypeClassifier.Normalize(analysis);

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
                RawModelResponse = raw,
                LinkedCameras = alert.LinkedCameras,
                IncidentId = alert.IncidentId,
                Embedding = embedding
            };

            // Before the save, so the stored record and all seven notifying actions below agree on
            // one decision. Asked only of alerts that would otherwise have notified, which is what
            // makes "the last thing this camera told you about" mean what it says - an alert that
            // was never going to notify can't be the thing a later one repeats.
            if (AlertNotificationPolicy.ShouldNotify(result))
                result.DuplicateOfAlertId = _deduplicator.FindRecentDuplicate(result, DateTimeOffset.UtcNow);

            await _store.SaveAsync(result, cancellationToken);

            // Never let the visibility guard drop an alert silently. In practice this fires when
            // the frame is unreadable and the model still writes a subject into the prose, but it
            // would also catch a reply that parsed yet omitted the subject blocks - and that
            // second case is worth seeing in the log rather than quietly losing.
            //
            // A repeat is excluded because it isn't this guard's doing and isn't a subject problem:
            // NotificationDeduplicator has already logged that one, with the alert it repeats.
            if (analysis.NotificationRecommended && result.DuplicateOfAlertId is null &&
                !AlertNotificationPolicy.ShouldNotify(result))
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
                LinkedCameras = alert.LinkedCameras,
                IncidentId = alert.IncidentId,
                Error = ex.ToString()
            };
            await _store.SaveAsync(failed, cancellationToken);
            _logger.LogError(ex, "Analysis failed for alert {AlertId}", alert.Id);
        }
    }

    // Reuses data already computed for every alert (Package.Status, and - if those profiles are
    // enabled - KnownFaceLabels/KnownVehicleLabel) rather than issuing any extra AI call. A
    // package showing as delivered/arriving (re)starts or extends a watch on this camera; a
    // later alert on the same camera showing it carried/removed/gone resolves that watch -
    // quietly if a recognized face/vehicle is in that alert, or by flagging suspected theft and
    // forcing a notification if not. "unknown" readings are ignored entirely, the same
    // never-overwrite-on-an-indefinite-reading philosophy GateCheckDecision already uses.
    private async Task ProcessPackageWatchAsync(string camera, VisionAnalysis analysis, PackageTheftOptions options, CancellationToken cancellationToken)
    {
        switch (analysis.Package.Status)
        {
            case "delivered" or "already_present" or "being_delivered":
                await _packageWatches.StartOrExtendAsync(camera, DateTimeOffset.UtcNow, TimeSpan.FromHours(options.WatchWindowHours), cancellationToken);
                break;

            case "being_carried" or "being_removed" or "none":
                var watch = await _packageWatches.GetActiveAsync(camera, cancellationToken);
                if (watch is null) break;

                var recognized = analysis.Person.KnownFaceLabels.Count > 0 || analysis.Vehicle.KnownVehicleLabel is not null;
                if (recognized)
                {
                    await _packageWatches.SetStatusAsync(watch.Id, PackageWatchStatus.PickedUp, cancellationToken);
                }
                else
                {
                    await _packageWatches.SetStatusAsync(watch.Id, PackageWatchStatus.TheftSuspected, cancellationToken);
                    analysis.PackageTheftSuspected = true;
                    analysis.PackageTheftReason =
                        $"A package delivered around {watch.DeliveredAt.ToLocalTime():t} is no longer there, and whoever's in this alert isn't a recognized household member or vehicle.";
                    analysis.NotificationRecommended = true;
                }
                break;
        }
    }
}
