using Microsoft.Extensions.Options;

namespace BlueIrisOllamaBridge;

// Routes each alert to whichever backend VisionRoutingOptions:Primary names, and - if
// FallbackEnabled - retries against the other backend when the primary's own retries are
// exhausted. This is what AlertProcessor and everything else actually depends on; nothing else
// talks to Ollama or a cloud provider directly.
// Takes both backends as IVisionAnalysisClient (not the concrete OllamaVisionClient/
// CloudVisionClient types) purely so tests can substitute fakes - the DI registration in
// Program.cs resolves the two concrete singletons explicitly and passes them in, since this is
// the only thing in the container registered as IVisionAnalysisClient itself.
public sealed class FailoverVisionClient(
    IVisionAnalysisClient ollama,
    IVisionAnalysisClient cloud,
    IOptionsMonitor<VisionRoutingOptions> routingOptions,
    IOptionsMonitor<CloudVisionOptions> cloudOptions,
    ILogger<FailoverVisionClient> logger) : IVisionAnalysisClient
{
    public async Task<(VisionAnalysis Analysis, string Raw, string Model)> AnalyzeAsync(
        IncomingAlert alert, PromptProfile profile, CancellationToken cancellationToken)
    {
        var (primary, fallback, primaryName, fallbackName) = ResolveOrder();

        try
        {
            return await primary.AnalyzeAsync(alert, profile, cancellationToken);
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
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        var (primary, fallback, _, _) = ResolveOrder();
        if (await primary.IsReadyAsync(cancellationToken)) return true;
        return fallback is not null && await fallback.IsReadyAsync(cancellationToken);
    }

    private (IVisionAnalysisClient Primary, IVisionAnalysisClient? Fallback, string PrimaryName, string? FallbackName) ResolveOrder()
    {
        var routing = routingOptions.CurrentValue;
        var cloudSettings = cloudOptions.CurrentValue;
        var cloudConfigured = cloudSettings.Enabled && !string.IsNullOrWhiteSpace(cloudSettings.ApiKey);
        var primaryIsCloud = string.Equals(routing.Primary, "cloud", StringComparison.OrdinalIgnoreCase);
        var cloudName = CloudProviderName(cloudSettings.Provider);

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
