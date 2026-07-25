using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace BlueIrisOllamaBridge;

// Free-text Q&A against Ollama, distinct from IOllamaVisionClient which always
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
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            }
        };

        using var response = await _httpClient.PostAsJsonAsync("api/chat", request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
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
