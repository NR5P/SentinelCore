using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SentinelCore;

public sealed class OllamaVisionClient : IVisionAnalysisClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<OllamaOptions> _optionsMonitor;
    private readonly IImageOptimizer _imageOptimizer;
    private readonly ILogger<OllamaVisionClient> _logger;
    private readonly OllamaModelProvisioner? _provisioner;

    // provisioner is optional only so tests can build the client without the background service;
    // without it, alerts always go to the configured model.
    public OllamaVisionClient(
        HttpClient httpClient,
        IOptionsMonitor<OllamaOptions> options,
        IImageOptimizer imageOptimizer,
        ILogger<OllamaVisionClient> logger,
        OllamaModelProvisioner? provisioner = null)
    {
        _httpClient = httpClient;
        _optionsMonitor = options;
        _imageOptimizer = imageOptimizer;
        _logger = logger;
        _provisioner = provisioner;
    }

    // The configured model, or - while a newly picked one is still downloading - the one that was
    // working before (see OllamaModelProvisioner).
    private string ModelForAlerts => _provisioner?.ModelForAlerts ?? _optionsMonitor.CurrentValue.Model;

    public async Task<(VisionAnalysis Analysis, string Raw, string Model)> AnalyzeAsync(
        IncomingAlert alert,
        PromptProfile profile,
        CancellationToken cancellationToken)
    {
        var _options = _optionsMonitor.CurrentValue;
        var startedAt = DateTimeOffset.UtcNow;
        var optimizedImage = await _imageOptimizer.OptimizeForInferenceAsync(
            alert.ImageBytes,
            profile.Crop,
            cancellationToken);

        // Empty for every alert except a burst-capturing trigger zone with MultiFrameAnalysis
        // enabled (see MultiFrameOptions) - each extra frame gets the same crop/resize treatment
        // as the primary image before being sent to the model.
        var optimizedExtraFrames = new List<OptimizedImage>(alert.ExtraFrames.Count);
        foreach (var frame in alert.ExtraFrames)
            optimizedExtraFrames.Add(await _imageOptimizer.OptimizeForInferenceAsync(frame, profile.Crop, cancellationToken));

        var prompt = BuildPrompt(alert, profile);

        _logger.LogInformation(
            "Sending alert {AlertId} to Ollama with prompt length {PromptCharacters:N0} characters",
            alert.Id,
            prompt.Length);

        var model = ModelForAlerts;
        var request = new
        {
            model,
            stream = false,
            // Thinking models (Gemma 4, Qwen3 "thinking") otherwise write a long hidden scratchpad
            // first. On a CPU box that is over a minute per alert, and with MaximumOutputTokens at
            // 512 it used up the whole budget and returned an empty answer on most alerts (bench,
            // 2026-10-19). Models without a thinking mode accept false and ignore it.
            think = false,
            keep_alive = _options.KeepAlive,
            // A full JSON schema, not the plain "json" mode: Ollama (0.5+) constrains generation
            // to the schema, so malformed JSON, missing fields, and invented property names -
            // previously a real retry cause on small local models - can't happen at all. The
            // prompt still spells out the same structure; the schema enforces shape, the prompt
            // explains meaning. Reuses the OpenAI builder because Ollama takes the same standard
            // JSON Schema dialect (additionalProperties:false included).
            format = OpenAiVisionClient.BuildJsonSchema(profile, alert.ExtraFrames.Count > 0),
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
                    images = new[] { optimizedImage.Bytes }.Concat(optimizedExtraFrames.Select(f => f.Bytes))
                        .Select(Convert.ToBase64String)
                        .ToArray()
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

                return (analysis, raw, model);
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

    // "Ollama answers on its port" is not the same as "this service can analyse an alert".
    // Checking only reachability reported ready while the configured model wasn't pulled, so
    // every alert failed with a 404 from Ollama and nothing - not the health endpoint, not
    // SelfMonitoringWorker, not the dashboard's system status - noticed. Since api/tags already
    // returns the installed model list, confirm the configured one is actually in it.
    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                "api/tags",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                return false;

            var tags = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(
                JsonOptions,
                cancellationToken);

            var configured = ModelForAlerts;
            if (string.IsNullOrWhiteSpace(configured))
                return false;

            var installed = tags?.Models ?? [];
            if (installed.Any(m => OllamaModelProvisioner.IsSameModel(m.Name, configured)))
                return true;

            _logger.LogWarning(
                "Ollama is reachable but the configured model '{Model}' is not installed. Pull it with: ollama pull {Model}",
                configured,
                configured);
            return false;
        }
        catch
        {
            return false;
        }
    }

    private sealed record OllamaTagsResponse(IReadOnlyList<OllamaTag>? Models);

    private sealed record OllamaTag(string? Name);

    // Public so tests can confirm the license-plate schema/rule only appear when a profile
    // opts in, and are otherwise byte-identical to the prompt every existing profile already gets.
    public static string BuildPrompt(IncomingAlert alert, PromptProfile profile)
    {
        var numbered = string.Join(
            '\n',
            profile.Questions.Select((q, i) => $"{i + 1}. {q}"));

        var licensePlateField = profile.LicensePlateRecognitionEnabled
            ? ",\n            \"license_plate\": \"\""
            : "";

        var licensePlateRule = profile.LicensePlateRecognitionEnabled
            ? "\n- If a vehicle's license plate is clearly legible, transcribe it into vehicle.license_plate exactly as shown (letters and numbers only, uppercase, no state name or slogan text). If it is not clearly legible, leave vehicle.license_plate as an empty string - do not guess."
            : "";

        var cameraBlockedField = profile.CameraBlockedCheckEnabled
            ? ",\n          \"camera_blocked\": false"
            : "";

        var cameraBlockedRule = profile.CameraBlockedCheckEnabled
            ? "\n- Set camera_blocked to true if the camera's own view is obstructed - covered, spray-painted, badly out of focus, or the lens is physically blocked or moved - as opposed to merely dark or a normal nighttime IR image. A dark but otherwise normal nighttime frame is not blocked."
            : "";

        // Only non-empty when a burst-capturing trigger zone supplied extra frames (see
        // TriggerZoneMonitor/MultiFrameOptions) - Blue Iris alerts never populate ExtraFrames, so
        // this stays "" and the prompt is byte-identical to before this existed for them.
        var frameCount = 1 + alert.ExtraFrames.Count;
        var multiFrameNotice = frameCount > 1
            ? $"\nYou are being shown {frameCount} frames from this alert, captured a fraction of a second apart, in chronological order (earliest first). A real person, vehicle, or animal should appear in a consistent, physically plausible position across the frames. If a candidate subject appears in only one frame and is absent or clearly different in the others, treat it as a shadow, reflection, headlight sweep, insect near the lens, or compression artifact rather than a genuine detection - do not report it as visible or recommend a notification for it.\n"
            : "";
        var analyzeLine = frameCount > 1
            ? "Analyze the attached images together using the camera-layout rules from the system prompt."
            : "Analyze the attached image using the camera-layout rules from the system prompt.";

        // Direction of travel is the one thing a burst can answer that a single frame genuinely
        // cannot, so the field, its allowed values, and its rule are all conditional on the burst
        // existing - the same opt-in shape as license_plate and camera_blocked above. Asking a
        // single-frame alert for motion would only get pose guessed back, which vehicle.orientation
        // already covers honestly. The schema builders gate the field on the same condition; for
        // Ollama that is load-bearing, since generation is constrained to the schema.
        // Indented to match the prompt as the reader sees it, not as it looks in this file: the raw
        // string literal is de-indented against its closing delimiter, but interpolated values are
        // spliced in verbatim afterwards, so these carry the final column widths themselves.
        var vehicleMotionField = frameCount > 1
            ? "\n    \"motion\": \"unknown\","
            : "";

        var vehicleMotionValues = frameCount > 1
            ? "\n\nAllowed vehicle.motion values:\nunknown, stationary, approaching_camera, receding_from_camera, left_to_right, right_to_left"
            : "";

        var vehicleMotionRule = frameCount > 1
            ? "\n- Set vehicle.motion from how the vehicle's position and apparent size change between the frames, not from which way it is pointed: growing larger and lower in the frame is approaching_camera, shrinking and receding toward the background is receding_from_camera, and consistent sideways travel with little size change is left_to_right or right_to_left. A vehicle reversing is receding_from_camera even while it still faces the camera, so motion and orientation are allowed to disagree - report each on its own evidence. If the vehicle occupies the same place at the same size in every frame, use stationary. If the frames are too similar, too dark, or too blurred to tell, use unknown rather than guessing, and let vehicle.activity fall back to what a single frame supports."
            : "";

        // The blanket "one still image cannot prove motion" instruction is true for the single-frame
        // path and actively wrong for a burst - left in place it tells the model to discard the very
        // evidence the extra frames were captured and paid for.
        var motionEvidenceRule = frameCount > 1
            ? "- Across these frames, movement between them is real evidence: use it for vehicle.motion and to choose vehicle.activity. Do not infer motion from a subject's pose within any single frame."
            : "- One still image normally cannot prove motion. Use unknown unless the visible evidence strongly supports the activity.";

        return $$"""
        Camera: {{alert.Camera}}
        Event time: {{alert.OccurredAt:O}}
        {{multiFrameNotice}}
        {{analyzeLine}}

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
            "orientation": "unknown",{{vehicleMotionField}}
            "activity": "none",
            "type": "none",
            "color": "unknown",
            "description": "",
            "delivery_company": "none",
            "delivery_company_confidence": 0.0{{licensePlateField}}
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
          "notification_reason": "brief reason"{{cameraBlockedField}}
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
        unknown, facing_gate, facing_road, left, right{{vehicleMotionValues}}

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
        - The questions above are a checklist, not a statement of fact. A question that mentions a
          person, vehicle, or package does NOT mean one is present. Decide from the image alone
          whether each subject exists before describing anything about it.
        - If nothing can be identified - the view is obstructed, too dark, overexposed, blurred, or
          the lens is dirty or covered - set every "visible" field to false, event_type to "none",
          notification_recommended to false, confidence to 0.2 or lower, and say plainly in summary
          that the view is obstructed. Do not answer the positional questions in that case.
        - summary and notification_reason must agree with the visible flags. Never describe a
          person unless person.visible is true, or a vehicle unless vehicle.visible is true. If
          person.visible is false, summary must not mention a person at all.
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
        - Do not identify a delivery company unless visible logos, markings, colors, or uniforms support it.{{licensePlateRule}}
        - Do not call an object a package unless it reasonably appears to be a delivered parcel.
        {{motionEvidenceRule}}{{vehicleMotionRule}}
        - Describe only the important foreground event. Do not treat distant houses, parked vehicles across the road, or ordinary background objects as the alert subject.
        - Confidence values must be between 0.0 and 1.0.
        - Use unknown instead of guessing.
        - Do not include markdown, code fences, or any text outside the JSON object.{{cameraBlockedRule}}
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
