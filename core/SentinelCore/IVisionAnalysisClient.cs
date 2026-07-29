namespace SentinelCore;

// Implemented by each vision backend (Ollama, cloud providers) and by FailoverVisionClient, which
// picks between them per VisionRoutingOptions. AlertProcessor and everything else in the app only
// ever depends on this - they don't know or care which concrete backend served a given alert.
public interface IVisionAnalysisClient
{
    // Model is whichever backend/model string actually produced Analysis - not necessarily the
    // configured primary, since FailoverVisionClient may have fallen back to the other backend.
    Task<(VisionAnalysis Analysis, string Raw, string Model)> AnalyzeAsync(IncomingAlert alert, PromptProfile profile, CancellationToken cancellationToken);
    Task<bool> IsReadyAsync(CancellationToken cancellationToken);
}
