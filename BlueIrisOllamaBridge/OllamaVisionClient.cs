using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace BlueIrisOllamaBridge;

public interface IOllamaVisionClient
{
    Task<(VisionAnalysis Analysis, string Raw)> AnalyzeAsync(IncomingAlert alert, PromptProfile profile, CancellationToken cancellationToken);
    Task<bool> IsReadyAsync(CancellationToken cancellationToken);
}

public sealed class OllamaVisionClient : IOllamaVisionClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly OllamaOptions _options;
    private readonly IImageOptimizer _imageOptimizer;
    private readonly ILogger<OllamaVisionClient> _logger;

    public OllamaVisionClient(
        HttpClient httpClient,
        IOptions<OllamaOptions> options,
        IImageOptimizer imageOptimizer,
        ILogger<OllamaVisionClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _imageOptimizer = imageOptimizer;
        _logger = logger;
    }

    public async Task<(VisionAnalysis Analysis, string Raw)> AnalyzeAsync(
        IncomingAlert alert,
        PromptProfile profile,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var optimizedImage = await _imageOptimizer.OptimizeForInferenceAsync(
            alert.ImageBytes,
            profile.Crop,
            cancellationToken);

        var prompt = BuildPrompt(alert, profile);

        _logger.LogInformation(
            "Sending alert {AlertId} to Ollama with prompt length {PromptCharacters:N0} characters",
            alert.Id,
            prompt.Length);

        var request = new
        {
            model = _options.Model,
            stream = false,
            keep_alive = _options.KeepAlive,
            format = "json",
            options = new
            {
                temperature = _options.Temperature,
                num_predict = _options.MaximumOutputTokens,
                top_k = _options.TopK,
                top_p = _options.TopP
            },
            messages = new object[]
            {
                new { role = "system", content = profile.SystemPrompt },
                new
                {
                    role = "user",
                    content = prompt,
                    images = new[] { Convert.ToBase64String(optimizedImage.Bytes) }
                }
            }
        };

        Exception? lastException = null;
        for (var attempt = 0; attempt <= _options.MaximumRetries; attempt++)
        {
            try
            {
                using var response = await _httpClient.PostAsJsonAsync(
                    "api/chat",
                    request,
                    JsonOptions,
                    cancellationToken);

                var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"Ollama returned {(int)response.StatusCode}: {responseText}");

                using var document = JsonDocument.Parse(responseText);
                var root = document.RootElement;

                var loadDuration = root.TryGetProperty("load_duration", out var load)
                    ? load.GetInt64()
                    : 0;

                var promptEvalDuration = root.TryGetProperty("prompt_eval_duration", out var promptEval)
                    ? promptEval.GetInt64()
                    : 0;

                var evalDuration = root.TryGetProperty("eval_duration", out var eval)
                    ? eval.GetInt64()
                    : 0;

                var totalDuration = root.TryGetProperty("total_duration", out var total)
                    ? total.GetInt64()
                    : 0;

                var promptEvalCount = root.TryGetProperty("prompt_eval_count", out var promptTokens)
                    ? promptTokens.GetInt32()
                    : 0;

                var evalCount = root.TryGetProperty("eval_count", out var outputTokens)
                    ? outputTokens.GetInt32()
                    : 0;

                var raw = root
                              .GetProperty("message")
                              .GetProperty("content")
                              .GetString()
                          ?? throw new JsonException(
                              "Ollama response did not contain message.content.");

                var clean = StripCodeFence(raw);
                var analysis = JsonSerializer.Deserialize<VisionAnalysis>(clean, JsonOptions)
                               ?? throw new JsonException(
                                   "Ollama returned an empty analysis.");

                ValidateAnalysis(analysis);

                var promptTokensPerSecond = promptEvalDuration > 0
                    ? promptEvalCount / (promptEvalDuration / 1_000_000_000.0)
                    : 0;

                var outputTokensPerSecond = evalDuration > 0
                    ? evalCount / (evalDuration / 1_000_000_000.0)
                    : 0;

                _logger.LogInformation(
                    """
                    Ollama completed alert {AlertId}
                      Application elapsed: {ElapsedSeconds:F1}s
                      Ollama total: {OllamaTotalSeconds:F2}s
                      Image: {Width}x{Height}
                      Model load: {LoadSeconds:F2}s
                      Prompt evaluation: {PromptEvalSeconds:F2}s ({PromptTokens} tokens, {PromptTokensPerSecond:F2} tokens/s)
                      Response generation: {EvalSeconds:F2}s ({OutputTokens} tokens, {OutputTokensPerSecond:F2} tokens/s)
                    """,
                    alert.Id,
                    (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
                    totalDuration / 1_000_000_000.0,
                    optimizedImage.Width,
                    optimizedImage.Height,
                    loadDuration / 1_000_000_000.0,
                    promptEvalDuration / 1_000_000_000.0,
                    promptEvalCount,
                    promptTokensPerSecond,
                    evalDuration / 1_000_000_000.0,
                    evalCount,
                    outputTokensPerSecond);

                return (analysis, raw);
            }
            catch (Exception ex) when (
                ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                lastException = ex;

                if (attempt >= _options.MaximumRetries)
                    break;

                var delay = TimeSpan.FromSeconds(
                    _options.RetryDelaySeconds * Math.Pow(2, attempt));

                _logger.LogWarning(
                    ex,
                    "Ollama attempt {Attempt} failed; retrying in {Delay}",
                    attempt + 1,
                    delay);

                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            "Ollama analysis failed after all retry attempts.",
            lastException);
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                "api/tags",
                cancellationToken);

            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildPrompt(IncomingAlert alert, PromptProfile profile)
    {
        var numbered = string.Join(
            '\n',
            profile.Questions.Select((q, i) => $"{i + 1}. {q}"));

        return $$"""
        Camera: {{alert.Camera}}
        Event time: {{alert.OccurredAt:O}}

        Analyze the attached image using the camera-layout rules from the system prompt.

        Questions to consider:
        {{numbered}}

        Return ONLY one valid JSON object with exactly this structure:
        {
          "event_type": "none",
          "summary": "brief factual description of the important activity",
          "gate": {
            "state": "unknown"
          },
          "person": {
            "visible": false,
            "location": "none",
            "activity": "none",
            "outside_vehicle": false,
            "description": ""
          },
          "vehicle": {
            "visible": false,
            "location": "none",
            "orientation": "unknown",
            "activity": "none",
            "type": "none",
            "color": "unknown",
            "description": "",
            "delivery_company": "none",
            "delivery_company_confidence": 0.0
          },
          "package": {
            "visible": false,
            "status": "none",
            "location": "none"
          },
          "animal": {
            "visible": false,
            "description": ""
          },
          "confidence": 0.0,
          "notification_recommended": false,
          "notification_reason": "brief reason"
        }

        Allowed event_type values:
        none, vehicle_on_road, vehicle_at_gate, vehicle_entering_property,
        vehicle_inside_property, person_at_gate, person_outside_vehicle,
        person_on_property, delivery_in_progress, package_delivered, animal, unknown

        Allowed gate.state values:
        open, closed, unknown

        Allowed person.location values:
        none, public_road, outside_gate, at_gate, inside_driveway,
        inside_property, near_vehicle, unknown

        Allowed person.activity values:
        none, standing, walking, approaching_gate, leaving_gate,
        entering_property, leaving_property, carrying_package,
        placing_package, removing_package, unknown

        Allowed vehicle.location values:
        none, public_road, outside_gate_entrance, passing_gate,
        inside_driveway, parked_inside_property, unknown

        Allowed vehicle.orientation values:
        unknown, facing_gate, facing_road, left, right

        Allowed vehicle.activity values:
        none, passing, stopped, entering, leaving, parked, unknown

        Allowed vehicle.type values:
        none, car, pickup, suv, van, delivery_van, delivery_truck,
        box_truck, semi, motorcycle, other, unknown

        Allowed vehicle.delivery_company values:
        none, ups, fedex, usps, amazon, dhl, other, unknown

        Allowed package.status values:
        none, being_carried, being_delivered, delivered,
        being_removed, already_present, unknown

        Allowed package.location values:
        none, with_person, at_gate, inside_property, near_vehicle, unknown

        Rules:
        - Ordinary traffic traveling along the public road is not notification-worthy.
        - A vehicle stopped in the entrance immediately outside the gate is notification-worthy.
        - A vehicle in the driveway (inside_driveway) is notification-worthy. A single still frame
          normally cannot prove whether a vehicle is still moving, so use inside_driveway (not
          parked_inside_property) whenever you cannot confirm from this one frame that it is long
          settled - do not withhold notification just because motion cannot be confirmed.
        - Use parked_inside_property, which is not notification-worthy merely for being there, only
          when the vehicle is clearly in a long-term resting spot (e.g. under a carport, backed into
          a usual parking spot, well away from the gate) with nothing suggesting recent arrival.
        - For this fixed gate camera, use the person's feet/contact point to determine which side of the gate they occupy. Below the gate, closer to the camera, is inside_driveway or inside_property. Above/behind the gate, in the narrow strip beside the road, is outside_gate. Do not decide from the person's torso or bounding-box center because the person may overlap the gate in the image.
        - When the gate is closed, a person who is wholly on the camera/driveway side is not notification-worthy merely for standing or walking there. Set notification_recommended to false for that person-only event.
        - A person on the road side of the gate (outside_gate), or interacting with the gate from that side (at_gate), is notification-worthy even when the gate is closed.
        - Do not use at_gate for a person merely near the gate on the camera side; use inside_driveway or inside_property.
        - A person outside a vehicle in the gate entrance is notification-worthy.
        - Delivery activity and a package newly left at the gate are notification-worthy.
        - Do not identify a delivery company unless visible logos, markings, colors, or uniforms support it.
        - Do not call an object a package unless it reasonably appears to be a delivered parcel.
        - One still image normally cannot prove motion. Use unknown unless the visible evidence strongly supports the activity.
        - Describe only the important foreground event. Do not treat distant houses, parked vehicles across the road, or ordinary background objects as the alert subject.
        - Confidence values must be between 0.0 and 1.0.
        - Use unknown instead of guessing.
        - Do not include markdown, code fences, or any text outside the JSON object.
        """;
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
}
