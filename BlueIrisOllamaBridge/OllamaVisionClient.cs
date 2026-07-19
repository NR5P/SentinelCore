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
            cancellationToken);

        var prompt = BuildPrompt(alert, profile);
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
                var raw = document.RootElement
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

                _logger.LogInformation(
                    "Ollama completed alert {AlertId} in {ElapsedSeconds:F1}s using {Width}x{Height} inference image",
                    alert.Id,
                    (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
                    optimizedImage.Width,
                    optimizedImage.Height);

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
        - A vehicle entering through the gate or unexpectedly present in the inside driveway is notification-worthy.
        - A person standing or walking at or near the gate is notification-worthy.
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