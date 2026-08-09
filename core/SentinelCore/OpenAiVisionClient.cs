using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace SentinelCore;

// OpenAI's Chat Completions API with structured outputs (response_format: json_schema,
// strict: true). Unlike Ollama's prompt-level "format: json", OpenAI enforces the exact shape
// server-side, so there's no code-fence stripping or malformed-JSON retry needed on the happy
// path - the prompt text itself is still shared with Ollama (OllamaVisionClient.BuildPrompt) so
// both backends are told the same rules, just enforced differently.
public sealed class OpenAiVisionClient : IVisionAnalysisClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<CloudVisionOptions> _optionsMonitor;
    private readonly IImageOptimizer _imageOptimizer;
    private readonly ILogger<OpenAiVisionClient> _logger;

    public OpenAiVisionClient(
        HttpClient httpClient,
        IOptionsMonitor<CloudVisionOptions> options,
        IImageOptimizer imageOptimizer,
        ILogger<OpenAiVisionClient> logger)
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
            throw new InvalidOperationException("Cloud vision was used but CloudVision:ApiKey is not configured.");

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
            "Sending alert {AlertId} to OpenAI ({Model}) with prompt length {PromptCharacters:N0} characters",
            alert.Id,
            options.Model,
            prompt.Length);

        var request = BuildRequest(options.Model, profile, prompt, imageFrames);

        Exception? lastException = null;
        for (var attempt = 0; attempt <= options.MaximumRetries; attempt++)
        {
            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
                {
                    // request is statically typed `object` (it's an anonymous type, which can't
                    // be named), so JsonContent.Create must be given the runtime type explicitly -
                    // otherwise it serializes as typeof(object) and the body comes out as "{}".
                    Content = JsonContent.Create(request, request.GetType(), options: JsonOptions)
                };
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

                using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
                var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"OpenAI returned {(int)response.StatusCode}: {responseText}");

                var (analysis, raw) = ParseResponse(responseText);
                ValidateAnalysis(analysis);

                _logger.LogInformation(
                    "OpenAI completed alert {AlertId} in {ElapsedSeconds:F1}s ({Model})",
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
                    "OpenAI attempt {Attempt} failed; retrying in {Delay}",
                    attempt + 1,
                    delay);

                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            "OpenAI analysis failed after all retry attempts.",
            lastException);
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        if (string.IsNullOrWhiteSpace(options.ApiKey)) return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // Public so tests can confirm the request shape without a live API call - particularly that
    // license_plate only appears in the schema when the profile opts in, mirroring
    // OllamaVisionClient.BuildPrompt's own conditional field.
    public static object BuildRequest(string model, PromptProfile profile, string prompt, IReadOnlyList<byte[]> imageFrames)
    {
        var content = new List<object> { new { type = "text", text = prompt } };
        foreach (var frame in imageFrames)
        {
            var dataUri = $"data:image/jpeg;base64,{Convert.ToBase64String(frame)}";
            content.Add(new { type = "image_url", image_url = new { url = dataUri } });
        }

        return new
        {
            model,
            temperature = 0,
            messages = new object[]
            {
                new { role = "system", content = profile.SystemPrompt },
                new { role = "user", content }
            },
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "alert_analysis",
                    strict = true,
                    schema = BuildJsonSchema(profile, imageFrames.Count > 1)
                }
            }
        };
    }

    // Strict-mode OpenAI JSON schemas require every declared property to be listed in "required"
    // (there's no concept of an optional property in strict mode) and "additionalProperties":
    // false on every object level. Every VisionAnalysis field already defaults to a non-null
    // value, so plain string/boolean/number types cover everything without needing nullable unions.
    // multiFrame must match whether BuildPrompt was given a burst: strict mode forbids any property
    // the schema does not declare, so declaring motion for a single-frame alert would demand an
    // answer the prompt never asks for, and omitting it for a burst would silently drop the answer
    // it does ask for. Ollama reuses this builder to constrain generation, so there the mismatch
    // isn't merely wasteful - the field becomes unrepresentable.
    public static JsonObject BuildJsonSchema(PromptProfile profile, bool multiFrame = false)
    {
        var vehicleProps = new List<(string Name, JsonNode Type)>
        {
            ("visible", Bool()), ("location", Str()), ("orientation", Str()), ("activity", Str()),
            ("type", Str()), ("color", Str()), ("description", Str()),
            ("delivery_company", Str()), ("delivery_company_confidence", Num())
        };
        if (profile.LicensePlateRecognitionEnabled)
            vehicleProps.Add(("license_plate", Str()));
        if (multiFrame)
            vehicleProps.Add(("motion", Str()));

        var rootProps = new List<(string Name, JsonNode Type)>
        {
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
            ("notification_reason", Str())
        };

        // Without this, strict mode's additionalProperties:false actively forbade the model
        // from answering the camera-blocked question the prompt asks when a gate check has
        // CheckBlocked on - the field just silently never came back through this backend.
        if (profile.CameraBlockedCheckEnabled)
            rootProps.Add(("camera_blocked", Bool()));

        return Obj(rootProps.ToArray());
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
            ["type"] = "object",
            ["properties"] = propertiesNode,
            ["required"] = required,
            ["additionalProperties"] = false
        };
    }

    private static JsonNode Str() => new JsonObject { ["type"] = "string" };
    private static JsonNode Bool() => new JsonObject { ["type"] = "boolean" };
    private static JsonNode Num() => new JsonObject { ["type"] = "number" };

    // Public so tests can exercise response parsing against a canned OpenAI-shaped response body
    // without a live API call.
    public static (VisionAnalysis Analysis, string Raw) ParseResponse(string responseText)
    {
        using var document = JsonDocument.Parse(responseText);
        var raw = document.RootElement
                      .GetProperty("choices")[0]
                      .GetProperty("message")
                      .GetProperty("content")
                      .GetString()
                  ?? throw new JsonException("OpenAI response did not contain choices[0].message.content.");

        var analysis = JsonSerializer.Deserialize<VisionAnalysis>(raw, JsonOptions)
                       ?? throw new JsonException("OpenAI returned an empty analysis.");

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
