using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SentinelCore;

// Anthropic's Messages API. Auth is an "x-api-key" header (not Bearer) plus a required
// "anthropic-version" header - both different from OpenAI's convention. There's no strict
// JSON-schema enforcement mode like OpenAI's, so this relies on the same prompt-level "return
// ONLY valid JSON" instruction Ollama already uses (OllamaVisionClient.BuildPrompt), which
// Claude follows reliably in practice.
public sealed class AnthropicVisionClient : IVisionAnalysisClient
{
    private const string AnthropicVersion = "2023-06-01";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<CloudVisionOptions> _optionsMonitor;
    private readonly IImageOptimizer _imageOptimizer;
    private readonly ILogger<AnthropicVisionClient> _logger;

    public AnthropicVisionClient(
        HttpClient httpClient,
        IOptionsMonitor<CloudVisionOptions> options,
        IImageOptimizer imageOptimizer,
        ILogger<AnthropicVisionClient> logger)
    {
        _httpClient = httpClient;
        _optionsMonitor = options;
        _imageOptimizer = imageOptimizer;
        _logger = logger;
    }

    public async Task<(VisionAnalysis Analysis, string Raw, string Model)> AnalyzeAsync(
        IncomingAlert alert,
        PromptProfile profile,
        CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new InvalidOperationException("Anthropic vision was used but CloudVision:ApiKey is not configured.");

        var startedAt = DateTimeOffset.UtcNow;
        var optimizedImage = await _imageOptimizer.OptimizeForInferenceAsync(
            alert.ImageBytes,
            profile.Crop,
            cancellationToken);

        // Empty for every alert except a burst-capturing trigger zone with MultiFrameAnalysis
        // enabled - see OllamaVisionClient.AnalyzeAsync's identical handling.
        var imageFrames = new List<byte[]> { optimizedImage.Bytes };
        foreach (var frame in alert.ExtraFrames)
            imageFrames.Add((await _imageOptimizer.OptimizeForInferenceAsync(frame, profile.Crop, cancellationToken)).Bytes);

        var prompt = OllamaVisionClient.BuildPrompt(alert, profile);

        _logger.LogInformation(
            "Sending alert {AlertId} to Anthropic ({Model}) with prompt length {PromptCharacters:N0} characters",
            alert.Id,
            options.Model,
            prompt.Length);

        var request = BuildRequest(options.Model, profile, prompt, imageFrames);

        Exception? lastException = null;
        for (var attempt = 0; attempt <= options.MaximumRetries; attempt++)
        {
            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "messages")
                {
                    Content = JsonContent.Create(request, request.GetType(), options: JsonOptions)
                };
                httpRequest.Headers.Add("x-api-key", options.ApiKey);
                httpRequest.Headers.Add("anthropic-version", AnthropicVersion);

                using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
                var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Anthropic returned {(int)response.StatusCode}: {responseText}");

                var (analysis, raw) = ParseResponse(responseText);
                ValidateAnalysis(analysis);

                _logger.LogInformation(
                    "Anthropic completed alert {AlertId} in {ElapsedSeconds:F1}s ({Model})",
                    alert.Id,
                    (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
                    options.Model);

                return (analysis, raw, options.Model);
            }
            catch (Exception ex) when (
                ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                lastException = ex;

                if (attempt >= options.MaximumRetries)
                    break;

                var delay = TimeSpan.FromSeconds(options.RetryDelaySeconds * Math.Pow(2, attempt));

                _logger.LogWarning(
                    ex,
                    "Anthropic attempt {Attempt} failed; retrying in {Delay}",
                    attempt + 1,
                    delay);

                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            "Anthropic analysis failed after all retry attempts.",
            lastException);
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        if (string.IsNullOrWhiteSpace(options.ApiKey)) return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "models");
            request.Headers.Add("x-api-key", options.ApiKey);
            request.Headers.Add("anthropic-version", AnthropicVersion);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // Public so tests can confirm the request shape without a live API call.
    public static object BuildRequest(string model, PromptProfile profile, string prompt, IReadOnlyList<byte[]> imageFrames)
    {
        var content = new List<object> { new { type = "text", text = prompt } };
        foreach (var frame in imageFrames)
        {
            content.Add(new
            {
                type = "image",
                source = new
                {
                    type = "base64",
                    media_type = "image/jpeg",
                    data = Convert.ToBase64String(frame)
                }
            });
        }

        return new
        {
            model,
            // The Messages API requires max_tokens - there's no "use the model's default" like
            // OpenAI/Ollama. The analysis JSON is compact; this is generous headroom against
            // truncation without being a meaningful cost concern.
            max_tokens = 1024,
            system = profile.SystemPrompt,
            messages = new object[]
            {
                new { role = "user", content }
            }
        };
    }

    // Public so tests can exercise response parsing against a canned Anthropic-shaped response
    // body without a live API call.
    public static (VisionAnalysis Analysis, string Raw) ParseResponse(string responseText)
    {
        using var document = JsonDocument.Parse(responseText);
        var raw = document.RootElement
                      .GetProperty("content")[0]
                      .GetProperty("text")
                      .GetString()
                  ?? throw new JsonException("Anthropic response did not contain content[0].text.");

        var clean = StripCodeFence(raw);
        var analysis = JsonSerializer.Deserialize<VisionAnalysis>(clean, JsonOptions)
                       ?? throw new JsonException("Anthropic returned an empty analysis.");

        return (analysis, raw);
    }

    private static string StripCodeFence(string value)
    {
        var trimmed = value.Trim();

        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstNewLine = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);

        return firstNewLine >= 0 && lastFence > firstNewLine
            ? trimmed[(firstNewLine + 1)..lastFence].Trim()
            : trimmed;
    }

    private static void ValidateAnalysis(VisionAnalysis analysis)
    {
        if (analysis.Confidence is < 0 or > 1)
            throw new JsonException(
                "Analysis confidence must be between 0 and 1.");

        if (analysis.Vehicle.DeliveryCompanyConfidence is < 0 or > 1)
            throw new JsonException(
                "Delivery-company confidence must be between 0 and 1.");

        if (string.IsNullOrWhiteSpace(analysis.EventType))
            throw new JsonException(
                "Analysis event_type is required.");

        if (string.IsNullOrWhiteSpace(analysis.Summary))
            throw new JsonException(
                "Analysis summary is required.");
    }
}
