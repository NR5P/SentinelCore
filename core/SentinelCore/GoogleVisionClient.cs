using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace SentinelCore;

// Google's Gemini generateContent API. Two things differ from every other backend here: the
// model is part of the URL path (not a body field), and structured output uses a Google-specific
// schema dialect - a Schema object with UPPERCASE type names and no additionalProperties concept,
// not standard JSON Schema like OpenAI's.
public sealed class GoogleVisionClient : IVisionAnalysisClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<CloudVisionOptions> _optionsMonitor;
    private readonly IImageOptimizer _imageOptimizer;
    private readonly ILogger<GoogleVisionClient> _logger;

    public GoogleVisionClient(
        HttpClient httpClient,
        IOptionsMonitor<CloudVisionOptions> options,
        IImageOptimizer imageOptimizer,
        ILogger<GoogleVisionClient> logger)
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
            throw new InvalidOperationException("Google vision was used but CloudVision:ApiKey is not configured.");

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
            "Sending alert {AlertId} to Google ({Model}) with prompt length {PromptCharacters:N0} characters",
            alert.Id,
            options.Model,
            prompt.Length);

        var request = BuildRequest(profile, prompt, imageFrames);
        var path = $"models/{Uri.EscapeDataString(options.Model)}:generateContent";

        Exception? lastException = null;
        for (var attempt = 0; attempt <= options.MaximumRetries; attempt++)
        {
            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, path)
                {
                    Content = JsonContent.Create(request, request.GetType(), options: JsonOptions)
                };
                httpRequest.Headers.Add("x-goog-api-key", options.ApiKey);

                using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
                var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Google returned {(int)response.StatusCode}: {responseText}");

                var (analysis, raw) = ParseResponse(responseText);
                ValidateAnalysis(analysis);

                _logger.LogInformation(
                    "Google completed alert {AlertId} in {ElapsedSeconds:F1}s ({Model})",
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
                    "Google attempt {Attempt} failed; retrying in {Delay}",
                    attempt + 1,
                    delay);

                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            "Google analysis failed after all retry attempts.",
            lastException);
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        if (string.IsNullOrWhiteSpace(options.ApiKey)) return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "models");
            request.Headers.Add("x-goog-api-key", options.ApiKey);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // Public so tests can confirm the request shape without a live API call.
    public static object BuildRequest(PromptProfile profile, string prompt, IReadOnlyList<byte[]> imageFrames)
    {
        var parts = new List<object> { new { text = prompt } };
        foreach (var frame in imageFrames)
            parts.Add(new { inlineData = new { mimeType = "image/jpeg", data = Convert.ToBase64String(frame) } });

        return new
        {
            systemInstruction = new { parts = new[] { new { text = profile.SystemPrompt } } },
            contents = new object[]
            {
                new { parts }
            },
            generationConfig = new
            {
                temperature = 0,
                responseMimeType = "application/json",
                responseSchema = BuildJsonSchema(profile)
            }
        };
    }

    // Gemini's Schema dialect: UPPERCASE type names, no additionalProperties concept (there's no
    // way to forbid extra fields the way OpenAI's strict mode does). Otherwise the same shape as
    // OpenAiVisionClient.BuildJsonSchema - see that for the reasoning behind which fields are
    // conditional on LicensePlateRecognitionEnabled.
    public static JsonObject BuildJsonSchema(PromptProfile profile)
    {
        var vehicleProps = new List<(string Name, JsonNode Type)>
        {
            ("visible", Bool()), ("location", Str()), ("orientation", Str()), ("activity", Str()),
            ("type", Str()), ("color", Str()), ("description", Str()),
            ("delivery_company", Str()), ("delivery_company_confidence", Num())
        };
        if (profile.LicensePlateRecognitionEnabled)
            vehicleProps.Add(("license_plate", Str()));

        return Obj(
            ("event_type", Str()),
            ("summary", Str()),
            ("gate", Obj(("state", Str()))),
            ("person", Obj(
                ("visible", Bool()), ("location", Str()), ("activity", Str()),
                ("outside_vehicle", Bool()), ("description", Str()))),
            ("vehicle", Obj(vehicleProps.ToArray())),
            ("package", Obj(("visible", Bool()), ("status", Str()), ("location", Str()))),
            ("animal", Obj(("visible", Bool()), ("description", Str()))),
            ("confidence", Num()),
            ("notification_recommended", Bool()),
            ("notification_reason", Str()));
    }

    private static JsonObject Obj(params (string Name, JsonNode Type)[] properties)
    {
        var propertiesNode = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, type) in properties)
        {
            propertiesNode[name] = type;
            required.Add(name);
        }

        return new JsonObject
        {
            ["type"] = "OBJECT",
            ["properties"] = propertiesNode,
            ["required"] = required
        };
    }

    private static JsonNode Str() => new JsonObject { ["type"] = "STRING" };
    private static JsonNode Bool() => new JsonObject { ["type"] = "BOOLEAN" };
    private static JsonNode Num() => new JsonObject { ["type"] = "NUMBER" };

    // Public so tests can exercise response parsing against a canned Gemini-shaped response body
    // without a live API call.
    public static (VisionAnalysis Analysis, string Raw) ParseResponse(string responseText)
    {
        using var document = JsonDocument.Parse(responseText);
        var raw = document.RootElement
                      .GetProperty("candidates")[0]
                      .GetProperty("content")
                      .GetProperty("parts")[0]
                      .GetProperty("text")
                      .GetString()
                  ?? throw new JsonException("Google response did not contain candidates[0].content.parts[0].text.");

        var analysis = JsonSerializer.Deserialize<VisionAnalysis>(raw, JsonOptions)
                       ?? throw new JsonException("Google returned an empty analysis.");

        return (analysis, raw);
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
