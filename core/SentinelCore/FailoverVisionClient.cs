using Microsoft.Extensions.Options;

namespace SentinelCore;

// Routes each alert to whichever backend VisionRoutingOptions:Primary names, and - if
// FallbackEnabled - retries against another backend when the primary's own retries are
// exhausted. This is what AlertProcessor and everything else actually depends on; nothing else
// talks to Ollama, a cloud provider, or the fast local detector directly.
//
// Two extra rules exist for the "fastlocal" primary:
//  - Gate/door open checks always go to a VLM backend regardless. A class-label detector can
//    never answer "is the gate open", so routing those to fastlocal would return "unknown"
//    forever and the check would silently stop updating state.
//  - With EscalationEnabled, an alert where the detector found a subject is re-analyzed by a
//    VLM backend for the full description. When the escalation comes back reporting an empty
//    frame, EscalationVerdictWins decides which of the two is believed - see AnalyzeAsync.
//    Escalation *failure* is not a verdict and always keeps the detector's own result, in both
//    modes, rather than failing an alert that already has a usable answer.
//
// One rule applies to every VLM result, whichever way it was routed: VisionRouting's
// MinimumConfidence floor, the VLM tier's counterpart to FastLocal:ScoreThreshold. A VLM analysis
// below the floor is rewritten to claim no subject (see WithoutVisibleSubject) so that "the model
// couldn't tell" stops reading downstream as "the model saw someone".
//
// And one for an Ollama primary whose model is still downloading (ollamaModelMissing - see
// OllamaModelProvisioner.ModelMissing): alerts go to the fast detector until it arrives, rather
// than every one failing with Ollama's 404. That is the normal state for a while after someone
// picks Ollama in the setup wizard, since a fresh install no longer pulls the model up front.
// Gate checks still go to Ollama - the detector can't answer them, so there is nothing to gain.
//
// Takes the backends as IVisionAnalysisClient (not the concrete types) purely so tests can
// substitute fakes - the DI registration in Program.cs resolves the concrete singletons
// explicitly and passes them in, since this is the only thing in the container registered as
// IVisionAnalysisClient itself.
public sealed class FailoverVisionClient(
    IVisionAnalysisClient ollama,
    IVisionAnalysisClient cloud,
    IVisionAnalysisClient fastLocal,
    IOptionsMonitor<VisionRoutingOptions> routingOptions,
    IOptionsMonitor<CloudVisionOptions> cloudOptions,
    ILogger<FailoverVisionClient> logger,
    Func<bool>? ollamaModelMissing = null) : IVisionAnalysisClient
{
    public async Task<(VisionAnalysis Analysis, string Raw, string Model)> AnalyzeAsync(
        IncomingAlert alert, PromptProfile profile, CancellationToken cancellationToken)
    {
        var isGateCheck = string.Equals(alert.Profile, GateCheckDecision.ProfileName, StringComparison.OrdinalIgnoreCase);

        // Two independent reasons to skip the detector, deliberately not folded into one flag:
        // isGateCheck ALSO exempts the result from MinimumConfidence (see ApplyConfidenceFloor), and
        // a camera that just wants the full model has no business skipping the confidence floor too.
        var (primary, fallback, primaryName, fallbackName) =
            ResolveOrder(includeFastLocal: !isGateCheck && !alert.BypassFastLocal);

        // One snapshot for every decision below. IOptionsMonitor rebinds the moment a settings save
        // lands, so reading CurrentValue separately per branch could apply half of one configuration
        // and half of the next to the same alert.
        var routing = routingOptions.CurrentValue;

        (VisionAnalysis Analysis, string Raw, string Model) result;
        try
        {
            result = await primary.AnalyzeAsync(alert, profile, cancellationToken);
        }
        catch (Exception ex) when (fallback is not null && ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "{Primary} vision backend failed for alert {AlertId}; falling back to {Fallback}",
                primaryName,
                alert.Id,
                fallbackName);
            var fallbackResult = await fallback.AnalyzeAsync(alert, profile, cancellationToken);
            return ApplyConfidenceFloor(fallbackResult, alert, routing, isGateCheck, fallbackName!);
        }

        // Anything that isn't the detector is a VLM, so its answer faces the confidence floor and
        // there is nothing to escalate to.
        if (primary != fastLocal)
            return ApplyConfidenceFloor(result, alert, routing, isGateCheck, primaryName);

        if (routing.EscalationEnabled && HasDetectedSubject(result.Analysis))
        {
            var escalated = await TryEscalateAsync(alert, profile, result.Model, routing, cancellationToken);

            // Escalation exists to put better prose around a subject the detector already found -
            // by default it is not a second opinion on whether the subject is there. Letting it
            // replace a positive detection with an empty one is how real footage disappears:
            // downstream, "no visible subject" is what marks a motion trigger Rejected (see
            // AlertNotificationPolicy.HasVisibleSubject and TriggerConfirmationService), so one
            // VLM shrugging at a dark IR frame hides a person the object detector was confident
            // about. So when the two disagree in that direction the detector wins by default, and
            // the disagreement is logged rather than buried.
            //
            // EscalationVerdictWins flips that, for installs where the disagreement is the normal
            // case rather than the rare dark frame this rule was written for. There the detector is
            // the unreliable half, and believing it means a permanent stream of events that nothing
            // ever actually confirmed.
            //
            // Either way this only runs when escalation produced a verdict. A null - target
            // unreachable, target threw, no target configured - skips the whole block and keeps the
            // detector's result, because "the VLM never answered" is not the VLM saying no.
            if (escalated is { } better)
            {
                if (HasDetectedSubject(better.Analysis)) return better;

                if (routing.EscalationVerdictWins)
                {
                    logger.LogInformation(
                        "Escalation reported nothing in frame for alert {AlertId} although the fast detector found a " +
                        "subject; EscalationVerdictWins is set, so the escalation's verdict stands and the event will " +
                        "be filed as rejected.",
                        alert.Id);
                    return better;
                }

                logger.LogInformation(
                    "Escalation reported nothing in frame for alert {AlertId} although the fast detector found a " +
                    "subject; keeping the detector's result so the event isn't filtered away.",
                    alert.Id);
            }
        }

        return result;
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        var (primary, fallback, _, _) = ResolveOrder(includeFastLocal: true);
        if (await primary.IsReadyAsync(cancellationToken)) return true;
        return fallback is not null && await fallback.IsReadyAsync(cancellationToken);
    }

    private static bool HasDetectedSubject(VisionAnalysis analysis) =>
        analysis.Person.Visible || analysis.Vehicle.Visible || analysis.Animal.Visible;

    // VisionRouting:MinimumConfidence, the VLM tier's counterpart to FastLocal:ScoreThreshold.
    // Only ever called with a result a VLM backend produced - the detector's own scores live on a
    // different number line and are already gated per-detection before they are ever mapped.
    private (VisionAnalysis Analysis, string Raw, string Model) ApplyConfidenceFloor(
        (VisionAnalysis Analysis, string Raw, string Model) result,
        IncomingAlert alert,
        VisionRoutingOptions routing,
        bool isGateCheck,
        string backendName)
    {
        // A gate check's subject is ambient state rather than anything in frame, so there is no
        // presence claim here to withdraw and a hesitant reading is still a usable answer.
        if (routing.MinimumConfidence <= 0 || isGateCheck) return result;
        if (result.Analysis.Confidence >= routing.MinimumConfidence) return result;

        // Nothing claimed, nothing to take away - and logging every ordinary empty frame would
        // bury the cases where the floor actually changed an outcome.
        if (!AlertNotificationPolicy.HasVisibleSubject(result.Analysis)) return result;

        logger.LogInformation(
            "{Backend} reported a subject in alert {AlertId} at confidence {Confidence:0.##}, under the configured " +
            "minimum of {Minimum:0.##}; treating the frame as empty. The description is kept on the event.",
            backendName,
            alert.Id,
            result.Analysis.Confidence,
            routing.MinimumConfidence);

        var reason =
            $"below the full-AI minimum confidence ({result.Analysis.Confidence:0.##} < {routing.MinimumConfidence:0.##})";

        return (WithoutVisibleSubject(result.Analysis, reason), result.Raw, result.Model);
    }

    // Withdraws every presence claim an analysis makes while keeping everything it observed: the
    // summary, all the descriptions, the gate state and the confidence it actually reported stay
    // put, so the event still reads correctly in the dashboard, the API and semantic search. Only
    // the verdict changes, and it changes with a stated reason rather than silently.
    //
    // Package.Status is the one that is easy to miss: AlertNotificationPolicy.HasVisibleSubject
    // counts "being_delivered"/"delivered"/"already_present" as a subject in their own right,
    // regardless of Package.Visible, so clearing the flag alone would leave a low-confidence
    // delivery still confirming the event.
    //
    // Public and pure for the same reason as FastLocalVisionClient.MapToAnalysis: it is the whole
    // contract of the floor, so tests pin it directly.
    public static VisionAnalysis WithoutVisibleSubject(VisionAnalysis analysis, string reason) => new()
    {
        EventType = analysis.EventType,
        Summary = analysis.Summary,
        Gate = analysis.Gate,
        Person = new PersonAnalysis
        {
            Visible = false,
            Location = analysis.Person.Location,
            Activity = analysis.Person.Activity,
            OutsideVehicle = analysis.Person.OutsideVehicle,
            Description = analysis.Person.Description,
            KnownFaceLabels = analysis.Person.KnownFaceLabels,
            UnrecognizedFaceDetected = analysis.Person.UnrecognizedFaceDetected
        },
        Vehicle = new VehicleAnalysis
        {
            Visible = false,
            Location = analysis.Vehicle.Location,
            Orientation = analysis.Vehicle.Orientation,
            Activity = analysis.Vehicle.Activity,
            Type = analysis.Vehicle.Type,
            Color = analysis.Vehicle.Color,
            Description = analysis.Vehicle.Description,
            DeliveryCompany = analysis.Vehicle.DeliveryCompany,
            DeliveryCompanyConfidence = analysis.Vehicle.DeliveryCompanyConfidence,
            LicensePlateText = analysis.Vehicle.LicensePlateText,
            KnownVehicleLabel = analysis.Vehicle.KnownVehicleLabel
        },
        Package = new PackageAnalysis
        {
            Visible = false,
            Status = "none",
            Location = analysis.Package.Location
        },
        Animal = new AnimalAnalysis
        {
            Visible = false,
            Description = analysis.Animal.Description
        },
        Confidence = analysis.Confidence,
        NotificationRecommended = false,
        NotificationReason = reason,
        CameraBlocked = analysis.CameraBlocked,
        PackageTheftSuspected = analysis.PackageTheftSuspected,
        PackageTheftReason = analysis.PackageTheftReason
    };

    // Null means "no result to prefer over the fast one": either no escalation target is
    // actually available, or the target failed - the caller keeps the detector's analysis.
    private async Task<(VisionAnalysis Analysis, string Raw, string Model)?> TryEscalateAsync(
        IncomingAlert alert, PromptProfile profile, string fastModel, VisionRoutingOptions routing,
        CancellationToken cancellationToken)
    {
        var (target, targetName) = ResolveEscalationTarget(routing.EscalationTarget);
        if (target is null)
            return null;

        logger.LogInformation(
            "Fast local backend ({FastModel}) found a subject in alert {AlertId}; escalating to {Target} for full analysis",
            fastModel,
            alert.Id,
            targetName);

        try
        {
            var escalated = await target.AnalyzeAsync(alert, profile, cancellationToken);

            // Escalation only happens under a fastlocal primary, which gate checks never take, so
            // the floor's gate-check exemption can't apply here.
            return ApplyConfidenceFloor(escalated, alert, routing, isGateCheck: false, targetName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Escalation to {Target} failed for alert {AlertId}; keeping the fast local analysis",
                targetName,
                alert.Id);
            return null;
        }
    }

    private (IVisionAnalysisClient? Target, string Name) ResolveEscalationTarget(string configured)
    {
        var cloudSettings = cloudOptions.CurrentValue;
        var cloudConfigured = cloudSettings.Enabled && !string.IsNullOrWhiteSpace(cloudSettings.ApiKey);
        var cloudName = CloudProviderName(cloudSettings.Provider);

        return configured.ToLowerInvariant() switch
        {
            "ollama" => (ollama, "Ollama"),
            // Explicitly cloud-only: if cloud isn't configured, escalating to Ollama anyway
            // would be exactly the surprise multi-minute CPU analysis this routing mode exists
            // to avoid on low-end hardware.
            "cloud" => cloudConfigured ? (cloud, cloudName) : (null, cloudName),
            _ => cloudConfigured ? (cloud, cloudName) : (ollama, "Ollama")
        };
    }

    private (IVisionAnalysisClient Primary, IVisionAnalysisClient? Fallback, string PrimaryName, string? FallbackName) ResolveOrder(
        bool includeFastLocal)
    {
        var routing = routingOptions.CurrentValue;
        var cloudSettings = cloudOptions.CurrentValue;
        var cloudConfigured = cloudSettings.Enabled && !string.IsNullOrWhiteSpace(cloudSettings.ApiKey);
        var primaryIsCloud = string.Equals(routing.Primary, "cloud", StringComparison.OrdinalIgnoreCase);
        var primaryIsFastLocal = string.Equals(routing.Primary, "fastlocal", StringComparison.OrdinalIgnoreCase);
        var cloudName = CloudProviderName(cloudSettings.Provider);

        if (includeFastLocal && !primaryIsCloud && !primaryIsFastLocal && ollamaModelMissing?.Invoke() == true)
            return (fastLocal, null, "FastLocal", null);

        if (primaryIsFastLocal)
        {
            // Gate checks and cameras with BypassFastLocal (includeFastLocal false) need a VLM: use
            // the same order the escalation path prefers - cloud when configured, Ollama otherwise -
            // so a low-end install's gate checks land on the backend it can actually afford.
            //
            // Note this returns no fallback when cloud is unconfigured, whatever FallbackEnabled
            // says: there is no second VLM to fall back TO. A bypassed camera therefore has one
            // backend and no safety net, which is the same deal gate checks have always had.
            if (!includeFastLocal)
                return cloudConfigured
                    ? (cloud, routing.FallbackEnabled ? ollama : null, cloudName, routing.FallbackEnabled ? "Ollama" : null)
                    : (ollama, null, "Ollama", null);

            if (!routing.FallbackEnabled)
                return (fastLocal, null, "FastLocal", null);

            return cloudConfigured
                ? (fastLocal, cloud, "FastLocal", cloudName)
                : (fastLocal, ollama, "FastLocal", "Ollama");
        }

        if (primaryIsCloud && !cloudConfigured)
        {
            // Misconfigured (primary set to cloud but no API key) - Ollama needs no setup beyond
            // pointing at a running server, so fall through to it rather than failing every
            // alert outright over a settings mistake.
            logger.LogWarning("VisionRouting:Primary is 'cloud' but CloudVision is not enabled/configured; using Ollama instead");
            return (ollama, null, "Ollama", null);
        }

        if (primaryIsCloud)
            return routing.FallbackEnabled
                ? (cloud, ollama, cloudName, "Ollama")
                : (cloud, null, cloudName, null);

        return routing.FallbackEnabled && cloudConfigured
            ? (ollama, cloud, "Ollama", cloudName)
            : (ollama, null, "Ollama", null);
    }

    private static string CloudProviderName(string provider) => provider switch
    {
        "anthropic" => "Anthropic",
        "google" => "Google",
        _ => "OpenAI"
    };
}
