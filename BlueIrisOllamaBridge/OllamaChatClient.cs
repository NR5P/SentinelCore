using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace BlueIrisOllamaBridge;

// Free-text Q&A against Ollama, distinct from IVisionAnalysisClient which always
// sends an image and expects a fixed structured-JSON schema back.
public interface IOllamaChatClient
{
    Task<string> AskAsync(string systemPrompt, string userMessage, CancellationToken cancellationToken);
}

public sealed class OllamaChatClient : IOllamaChatClient
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<TelegramChatOptions> _optionsMonitor;
    private readonly ILogger<OllamaChatClient> _logger;

    public OllamaChatClient(HttpClient httpClient, IOptionsMonitor<TelegramChatOptions> options, ILogger<OllamaChatClient> logger)
    {
        _httpClient = httpClient;
        _optionsMonitor = options;
        _logger = logger;
    }

    public async Task<string> AskAsync(string systemPrompt, string userMessage, CancellationToken cancellationToken)
    {
        var request = new
        {
            model = _optionsMonitor.CurrentValue.Model,
            stream = false,
            // Ollama defaults to a small context window (often 2-4k tokens) unless told
            // otherwise, silently truncating whatever doesn't fit - confirmed the hard way once
            // the system prompt started including the full app documentation alongside the
            // event log: the model answered from the docs alone, having quietly lost the event
            // log entirely. This gives real headroom for both at once.
            options = new { num_ctx = 16384 },
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            }
        };

        HttpResponseMessage response;
        string responseText;
        try
        {
            response = await _httpClient.PostAsJsonAsync("api/chat", request, cancellationToken);
            responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // A large event log or a large documentation excerpt can genuinely take longer than
            // this client's own configured timeout (TelegramChatOptions.TimeoutSeconds) to
            // process on modest CPU hardware - that's a timeout, not a bad response, so it never
            // reaches the status-code check below. Previously this exception went unhandled all
            // the way up to a raw 500 from the /api/chat/ask endpoint; a plain answer that says
            // so is a much better outcome for a feature that's explicitly "nag/inform, never
            // break" everywhere else it touches the UI.
            _logger.LogWarning(ex, "Ollama chat request timed out or failed to connect.");
            return "That took too long to answer - try a more specific question, or a shorter time range if you asked about history.";
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Ollama chat request failed: {StatusCode} {Body}", (int)response.StatusCode, responseText);
                return "I couldn't reach the analysis model just now.";
            }

            using var document = JsonDocument.Parse(responseText);
            var content = document.RootElement.GetProperty("message").GetProperty("content").GetString();
            return string.IsNullOrWhiteSpace(content) ? "I couldn't come up with an answer for that." : content.Trim();
        }
    }
}
