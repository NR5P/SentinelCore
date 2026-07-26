using Microsoft.Extensions.Options;

namespace BlueIrisOllamaBridge;

// Dispatches to whichever cloud provider CloudVisionOptions:Provider names. This is what
// FailoverVisionClient's "cloud" slot actually is - it doesn't know or care which provider is
// behind it, same way FailoverVisionClient itself doesn't know or care whether it's talking to
// Ollama or the cloud.
public sealed class CloudVisionClient(
    IVisionAnalysisClient openAi,
    IVisionAnalysisClient anthropic,
    IVisionAnalysisClient google,
    IOptionsMonitor<CloudVisionOptions> options,
    ILogger<CloudVisionClient> logger) : IVisionAnalysisClient
{
    public Task<(VisionAnalysis Analysis, string Raw, string Model)> AnalyzeAsync(
        IncomingAlert alert, PromptProfile profile, CancellationToken cancellationToken) =>
        Resolve().AnalyzeAsync(alert, profile, cancellationToken);

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken) =>
        Resolve().IsReadyAsync(cancellationToken);

    private IVisionAnalysisClient Resolve() => options.CurrentValue.Provider switch
    {
        "anthropic" => anthropic,
        "google" => google,
        "openai" => openAi,
        var other => Fallback(other)
    };

    // Reached only if CloudVision:Provider somehow holds something other than the three values
    // DataAnnotations already restricts it to - defend anyway rather than crash mid-alert.
    private IVisionAnalysisClient Fallback(string other)
    {
        logger.LogWarning("Unrecognized CloudVision:Provider {Provider}; defaulting to openai", other);
        return openAi;
    }
}
