using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SentinelCore;

// Discord incoming webhooks are deliberately used instead of a bot account. The user creates a
// webhook on the destination channel and pastes one URL; no Discord application, bot token,
// public callback URL, or inbound firewall rule is required.
public sealed class DiscordAlertAction : IAlertAction
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<DiscordOptions> _optionsMonitor;
    private readonly IOptionsMonitor<QuietHoursOptions> _quietHoursMonitor;
    private readonly ILogger<DiscordAlertAction> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent =
        new(StringComparer.OrdinalIgnoreCase);

    public DiscordAlertAction(
        HttpClient httpClient,
        IOptionsMonitor<DiscordOptions> options,
        IOptionsMonitor<QuietHoursOptions> quietHoursOptions,
        ILogger<DiscordAlertAction> logger)
    {
        _httpClient = httpClient;
        _optionsMonitor = options;
        _quietHoursMonitor = quietHoursOptions;
        _logger = logger;
    }

    public async Task ExecuteAsync(AlertResult result, CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        if (!options.Enabled || !DiscordOptions.IsValidWebhookUrl(options.WebhookUrl)) return;
        if (options.OnlyWhenNotificationRecommended && !AlertNotificationPolicy.ShouldNotify(result)) return;

        var minimumConfidence = QuietHoursPolicy.EffectiveMinimumConfidence(
            _quietHoursMonitor.CurrentValue,
            options.MinimumConfidence,
            result.OccurredAt);
        if (result.Analysis is not null && result.Analysis.Confidence < minimumConfidence) return;

        string? cooldownKey = null;
        if (result.Analysis is not null && AlertCategorizer.Categorize(result.Analysis) is { } category)
        {
            cooldownKey = $"{result.Camera}|{category}";
            if (IsInCooldown(options, cooldownKey))
            {
                _logger.LogInformation(
                    "Duplicate Discord category {Category} suppressed for camera {Camera}",
                    category,
                    result.Camera);
                return;
            }
        }

        var imageExists =
            options.AttachImage &&
            !string.IsNullOrWhiteSpace(result.ImagePath) &&
            File.Exists(result.ImagePath);
        var attachmentName = imageExists ? AttachmentName(result.ImagePath!) : null;
        var payloadJson = BuildPayloadJson(result, attachmentName);

        using var response = imageExists
            ? await SendWithImageAsync(
                options.WebhookUrl,
                payloadJson,
                result.ImagePath!,
                attachmentName!,
                cancellationToken)
            : await SendWithoutImageAsync(options.WebhookUrl, payloadJson, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Discord send failed for {AlertId}: {StatusCode} {Body}",
                result.Id,
                (int)response.StatusCode,
                body);
            return;
        }

        if (cooldownKey is not null) _lastSent[cooldownKey] = DateTimeOffset.UtcNow;
        _logger.LogInformation("Discord alert sent for {AlertId}", result.Id);
    }

    public static string BuildPayloadJson(AlertResult result, string? attachmentName = null)
    {
        var analysis = result.Analysis;
        var description = analysis is null
            ? "Camera alert received."
            : analysis.Summary;

        if (!string.IsNullOrWhiteSpace(analysis?.NotificationReason))
            description += $"\n\n{analysis.NotificationReason}";
        if (analysis is not null && AlertCategorizer.DescribeVehiclePlate(analysis.Vehicle) is { } plateLine)
            description += $"\n\n{plateLine}";

        var embed = new Dictionary<string, object?>
        {
            ["title"] = Truncate(result.Camera, 256),
            ["description"] = Truncate(description, 4096),
            ["color"] = 0x2EE59D,
            ["timestamp"] = result.OccurredAt.ToUniversalTime().ToString("O")
        };

        if (analysis is not null)
        {
            embed["footer"] = new
            {
                text = $"{analysis.EventType} · confidence {analysis.Confidence:P0}"
            };
        }

        if (attachmentName is not null)
            embed["image"] = new { url = $"attachment://{attachmentName}" };

        var payload = new
        {
            username = "Sentinel Core",
            // AI-generated text must never be able to ping @everyone, roles, or arbitrary users.
            allowed_mentions = new { parse = Array.Empty<string>() },
            embeds = new[] { embed }
        };

        return JsonSerializer.Serialize(payload);
    }

    private async Task<HttpResponseMessage> SendWithImageAsync(
        string webhookUrl,
        string payloadJson,
        string imagePath,
        string attachmentName,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent
        {
            { new StringContent(payloadJson, Encoding.UTF8, "application/json"), "payload_json" }
        };

        var imageBytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
        var imageContent = new ByteArrayContent(imageBytes);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue(ContentType(imagePath));
        content.Add(imageContent, "files[0]", attachmentName);

        return await _httpClient.PostAsync(webhookUrl, content, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendWithoutImageAsync(
        string webhookUrl,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        return await _httpClient.PostAsync(webhookUrl, content, cancellationToken);
    }

    private bool IsInCooldown(DiscordOptions options, string key)
    {
        if (options.DuplicateCooldownMinutes <= 0) return false;
        if (!_lastSent.TryGetValue(key, out var lastSent)) return false;
        return DateTimeOffset.UtcNow - lastSent < TimeSpan.FromMinutes(options.DuplicateCooldownMinutes);
    }

    private static string AttachmentName(string imagePath)
    {
        var extension = Path.GetExtension(imagePath).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp")) extension = ".jpg";
        return $"snapshot{extension}";
    }

    private static string ContentType(string imagePath) =>
        Path.GetExtension(imagePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];
}
