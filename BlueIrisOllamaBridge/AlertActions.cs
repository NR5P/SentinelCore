using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace BlueIrisOllamaBridge;

public interface IAlertAction
{
    Task ExecuteAsync(AlertResult result, CancellationToken cancellationToken);
}

public sealed class WebhookAlertAction : IAlertAction
{
    private readonly HttpClient _httpClient;
    private readonly WebhookOptions _options;
    private readonly ILogger<WebhookAlertAction> _logger;

    public WebhookAlertAction(HttpClient httpClient, IOptions<WebhookOptions> options, ILogger<WebhookAlertAction> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ExecuteAsync(AlertResult result, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.Url)) return;
        if (_options.OnlyWhenNotificationRecommended && result.Analysis?.NotificationRecommended != true) return;

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Url)
        {
            Content = JsonContent.Create(result)
        };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            request.Headers.TryAddWithoutValidation(_options.ApiKeyHeaderName, _options.ApiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Webhook returned {StatusCode}: {Body}", (int)response.StatusCode, body);
        }
    }
}
