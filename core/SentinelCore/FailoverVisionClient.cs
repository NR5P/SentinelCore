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
//    VLM backend for the full description; escalation failure keeps the detector's own result
//    rather than failing an alert that already has a usable answer.
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
    ILogger<FailoverVisionClient> logger) : IVisionAnalysisClient
{
    public async Task<(VisionAnalysis Analysis, string Raw, string Model)> AnalyzeAsync(
        IncomingAlert alert, PromptProfile profile, CancellationToken cancellationToken)
    {
        var isGateCheck = string.Equals(alert.Profile, GateCheckDecision.ProfileName, StringComparison.OrdinalIgnoreCase);
        var (primary, fallback, primaryName, fallbackName) = ResolveOrder(includeFastLocal: !isGateCheck);

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
            return await fallback.AnalyzeAsync(alert, profile, cancellationToken);
        }

        if (primary == fastLocal && routingOptions.CurrentValue.EscalationEnabled && HasDetectedSubject(result.Analysis))
        {
            var escalated = await TryEscalateAsync(alert, profile, result.Model, cancellationToken);
            if (escalated is not null)
                return escalated.Value;
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

    // Null means "no result to prefer over the fast one": either no escalation target is
    // actually available, or the target failed - the caller keeps the detector's analysis.
    private async Task<(VisionAnalysis Analysis, string Raw, string Model)?> TryEscalateAsync(
        IncomingAlert alert, PromptProfile profile, string fastModel, CancellationToken cancellationToken)
    {
        var routing = routingOptions.CurrentValue;
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
            return await target.AnalyzeAsync(alert, profile, cancellationToken);
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

        if (primaryIsFastLocal)
        {
            // Gate checks (includeFastLocal false) need a VLM: use the same order the escalation
            // path prefers - cloud when configured, Ollama otherwise - so a low-end install's
            // gate checks land on the backend it can actually afford.
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
