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
    private readonly ILogger<OllamaVisionClient> _logger;

    public OllamaVisionClient(HttpClient httpClient, IOptions<OllamaOptions> options, ILogger<OllamaVisionClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<(VisionAnalysis Analysis, string Raw)> AnalyzeAsync(
        IncomingAlert alert,
        PromptProfile profile,
        CancellationToken cancellationToken)
    {
        var prompt = BuildPrompt(alert, profile);
        var request = new
        {
            model = _options.Model,
            stream = false,
            keep_alive = _options.KeepAlive,
            format = "json",
            options = new { temperature = _options.Temperature },
            messages = new object[]
            {
                new { role = "system", content = profile.SystemPrompt },
                new
                {
                    role = "user",
                    content = prompt,
                    images = new[] { Convert.ToBase64String(alert.ImageBytes) }
                }
            }
        };

        Exception? lastException = null;
        for (var attempt = 0; attempt <= _options.MaximumRetries; attempt++)
        {
            try
            {
                using var response = await _httpClient.PostAsJsonAsync("api/chat", request, JsonOptions, cancellationToken);
                var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Ollama returned {(int)response.StatusCode}: {responseText}");

                using var document = JsonDocument.Parse(responseText);
                var raw = document.RootElement.GetProperty("message").GetProperty("content").GetString()
                          ?? throw new JsonException("Ollama response did not contain message.content.");
                var clean = StripCodeFence(raw);
                var analysis = JsonSerializer.Deserialize<VisionAnalysis>(clean, JsonOptions)
                               ?? throw new JsonException("Ollama returned an empty analysis.");
                return (analysis, raw);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                lastException = ex;
                if (attempt >= _options.MaximumRetries) break;
                var delay = TimeSpan.FromSeconds(_options.RetryDelaySeconds * Math.Pow(2, attempt));
                _logger.LogWarning(ex, "Ollama attempt {Attempt} failed; retrying in {Delay}", attempt + 1, delay);
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException("Ollama analysis failed after all retry attempts.", lastException);
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync("api/tags", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildPrompt(IncomingAlert alert, PromptProfile profile)
    {
        var numbered = string.Join('\n', profile.Questions.Select((q, i) => $"{i + 1}. {q}"));
        return $$"""
        Camera: {{alert.Camera}}
        Event time: {{alert.OccurredAt:O}}

        Analyze the attached image and answer these questions:
        {{numbered}}

        Return ONLY valid JSON with exactly this shape:
        {
          "summary": "brief factual summary",
          "person_visible": false,
          "vehicle_visible": false,
          "animal_visible": false,
          "package_visible": false,
          "notification_recommended": false,
          "notification_reason": "brief reason",
          "answers": [
            { "question": "question text", "answer": "answer text" }
          ]
        }
        Include one answers entry for every question in the same order. Use false when an item is not clearly visible.
        """;
    }

    private static string StripCodeFence(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var firstNewLine = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewLine >= 0 && lastFence > firstNewLine
            ? trimmed[(firstNewLine + 1)..lastFence].Trim()
            : trimmed;
    }
}
