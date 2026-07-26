using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace BlueIrisOllamaBridge;

// Lets the camera's owner text the Telegram bot questions ("what happened last night?")
// and get an answer built from the SQLite event log plus a local Ollama model. Long-polls
// Telegram (outbound only, so no inbound port/exposure is needed) and only ever answers
// the configured owner chat - anyone else who messages the bot is ignored.
public sealed class TelegramChatWorker : BackgroundService
{
    private const string ApiBaseUrl = "https://api.telegram.org";

    private readonly HttpClient _httpClient;
    private readonly ChatAnswerService _chatAnswerService;
    private readonly IBlueIrisSnapshotClient _snapshotClient;
    private readonly IOptionsMonitor<TelegramOptions> _telegramOptionsMonitor;
    private readonly IOptionsMonitor<TelegramChatOptions> _chatOptionsMonitor;
    private readonly ILogger<TelegramChatWorker> _logger;

    public TelegramChatWorker(
        IHttpClientFactory httpClientFactory,
        ChatAnswerService chatAnswerService,
        IBlueIrisSnapshotClient snapshotClient,
        IOptionsMonitor<TelegramOptions> telegramOptions,
        IOptionsMonitor<TelegramChatOptions> chatOptions,
        ILogger<TelegramChatWorker> logger)
    {
        _httpClient = httpClientFactory.CreateClient(nameof(TelegramChatWorker));
        _chatAnswerService = chatAnswerService;
        _snapshotClient = snapshotClient;
        _telegramOptionsMonitor = telegramOptions;
        _chatOptionsMonitor = chatOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long offset = 0;
        var backlogCleared = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            var telegramOptions = _telegramOptionsMonitor.CurrentValue;
            var chatOptions = _chatOptionsMonitor.CurrentValue;

            if (!chatOptions.Enabled || !telegramOptions.Enabled ||
                string.IsNullOrWhiteSpace(telegramOptions.BotToken) || string.IsNullOrWhiteSpace(telegramOptions.ChatId))
            {
                backlogCleared = false;
                if (!await WaitAsync(TimeSpan.FromSeconds(30), stoppingToken)) break;
                continue;
            }

            if (!backlogCleared)
            {
                offset = await SkipBacklogAsync(telegramOptions.BotToken, stoppingToken);
                backlogCleared = true;
            }

            List<TelegramUpdate> updates;
            try
            {
                updates = await GetUpdatesAsync(telegramOptions.BotToken, offset, chatOptions.PollTimeoutSeconds, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to poll Telegram for updates; retrying shortly.");
                if (!await WaitAsync(TimeSpan.FromSeconds(5), stoppingToken)) break;
                continue;
            }

            foreach (var update in updates)
            {
                offset = update.UpdateId + 1;
                await HandleUpdateAsync(update, stoppingToken);
            }
        }
    }

    private static async Task<bool> WaitAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<long> SkipBacklogAsync(string botToken, CancellationToken cancellationToken)
    {
        try
        {
            var pending = await GetUpdatesAsync(botToken, offset: 0, timeoutSeconds: 0, cancellationToken);
            if (pending.Count == 0) return 0;

            _logger.LogInformation("Skipping {Count} pending Telegram update(s) from before startup.", pending.Count);
            return pending.Max(u => u.UpdateId) + 1;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear the Telegram backlog on startup; starting from the beginning.");
            return 0;
        }
    }

    private async Task<List<TelegramUpdate>> GetUpdatesAsync(string botToken, long offset, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var url = $"{ApiBaseUrl}/bot{botToken}/getUpdates?timeout={timeoutSeconds}&offset={offset}";
        var payload = await _httpClient.GetFromJsonAsync<TelegramUpdatesResponse>(url, cancellationToken);
        return payload?.Result ?? [];
    }

    private async Task HandleUpdateAsync(TelegramUpdate update, CancellationToken cancellationToken)
    {
        var message = update.Message;
        if (string.IsNullOrWhiteSpace(message?.Text)) return;

        var incomingChatId = message.Chat.Id.ToString();
        if (!IsAuthorizedChat(incomingChatId, _telegramOptionsMonitor.CurrentValue.ChatId))
        {
            _logger.LogWarning("Ignoring Telegram message from unauthorized chat {ChatId}", incomingChatId);
            return;
        }

        try
        {
            if (IsPhotoCommand(message.Text))
            {
                await HandlePhotoRequestAsync(cancellationToken);
                return;
            }

            var answer = await _chatAnswerService.AnswerAsync(message.Text, cancellationToken);
            await SendMessageAsync(answer, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to answer a Telegram chat question");
            try { await SendMessageAsync("Sorry, something went wrong answering that.", cancellationToken); }
            catch (Exception sendEx) { _logger.LogError(sendEx, "Failed to send the fallback error reply"); }
        }
    }

    private async Task HandlePhotoRequestAsync(CancellationToken cancellationToken)
    {
        var image = await _snapshotClient.GetSnapshotAsync(cancellationToken);
        if (image is null)
        {
            await SendMessageAsync("Couldn't get a live photo from Blue Iris just now.", cancellationToken);
            return;
        }

        await SendPhotoAsync(image, cancellationToken);
    }

    private Task SendMessageAsync(string text, CancellationToken cancellationToken)
    {
        var telegramOptions = _telegramOptionsMonitor.CurrentValue;
        return _httpClient.PostAsync(
            $"{ApiBaseUrl}/bot{telegramOptions.BotToken}/sendMessage",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["chat_id"] = telegramOptions.ChatId,
                ["text"] = text
            }),
            cancellationToken);
    }

    private async Task SendPhotoAsync(byte[] imageBytes, CancellationToken cancellationToken)
    {
        var telegramOptions = _telegramOptionsMonitor.CurrentValue;
        using var content = new MultipartFormDataContent
        {
            { new StringContent(telegramOptions.ChatId), "chat_id" }
        };
        using var imageContent = new ByteArrayContent(imageBytes);
        imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        content.Add(imageContent, "photo", "snapshot.jpg");

        using var response = await _httpClient.PostAsync(
            $"{ApiBaseUrl}/bot{telegramOptions.BotToken}/sendPhoto", content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Failed to send the Blue Iris snapshot to Telegram: {StatusCode} {Body}", (int)response.StatusCode, body);
        }
    }

    // Recognizes an explicit /photo or /snapshot command (optionally with a Telegram
    // "@botname" suffix) rather than trying to infer intent from free text, so a request for
    // a live image is never confused with a question about history.
    public static bool IsPhotoCommand(string text)
    {
        var firstToken = text.Trim().Split([' ', '\n'], 2)[0];
        var atIndex = firstToken.IndexOf('@');
        if (atIndex >= 0) firstToken = firstToken[..atIndex];

        return string.Equals(firstToken, "/photo", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(firstToken, "/snapshot", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAuthorizedChat(string incomingChatId, string configuredChatId) =>
        !string.IsNullOrWhiteSpace(configuredChatId) &&
        string.Equals(incomingChatId, configuredChatId, StringComparison.Ordinal);

    public static string BuildEventLog(IReadOnlyList<AlertEventRecord> events)
    {
        if (events.Count == 0) return "No camera events have been recorded in this period.";

        var log = new StringBuilder();
        foreach (var e in events)
        {
            // Same day-of-week + ISO-date + 24h-time format the chat system prompt uses for
            // "today's date" (see ChatAnswerService) - a small local model comparing "did this
            // happen today" against two differently-formatted dates (e.g. this line's old
            // "7/25/2026 1:43 PM" against a prompt's "Sunday, July 26, 2026") is prone to
            // getting the day wrong; matching formats turns it into a direct string comparison.
            log.Append(
                $"{e.OccurredAt:dddd yyyy-MM-dd HH:mm} | {e.Camera} | {e.EventType ?? "unknown"} | notified={(e.NotificationRecommended == true ? "yes" : "no")} | {e.Summary}\n");
        }

        return log.ToString();
    }

    private sealed class TelegramUpdatesResponse
    {
        [JsonPropertyName("result")]
        public List<TelegramUpdate> Result { get; init; } = [];
    }

    private sealed class TelegramUpdate
    {
        [JsonPropertyName("update_id")]
        public long UpdateId { get; init; }

        [JsonPropertyName("message")]
        public TelegramMessage? Message { get; init; }
    }

    private sealed class TelegramMessage
    {
        [JsonPropertyName("chat")]
        public TelegramChatInfo Chat { get; init; } = new();

        [JsonPropertyName("text")]
        public string? Text { get; init; }
    }

    private sealed class TelegramChatInfo
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }
    }
}
