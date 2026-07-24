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
    private readonly IEventStore _eventStore;
    private readonly IOllamaChatClient _chatClient;
    private readonly TelegramOptions _telegramOptions;
    private readonly TelegramChatOptions _chatOptions;
    private readonly ILogger<TelegramChatWorker> _logger;

    public TelegramChatWorker(
        IHttpClientFactory httpClientFactory,
        IEventStore eventStore,
        IOllamaChatClient chatClient,
        IOptions<TelegramOptions> telegramOptions,
        IOptions<TelegramChatOptions> chatOptions,
        ILogger<TelegramChatWorker> logger)
    {
        _httpClient = httpClientFactory.CreateClient(nameof(TelegramChatWorker));
        _eventStore = eventStore;
        _chatClient = chatClient;
        _telegramOptions = telegramOptions.Value;
        _chatOptions = chatOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_chatOptions.Enabled || !_telegramOptions.Enabled ||
            string.IsNullOrWhiteSpace(_telegramOptions.BotToken) || string.IsNullOrWhiteSpace(_telegramOptions.ChatId))
        {
            _logger.LogInformation("TelegramChat is disabled or Telegram is not configured; the chat listener will not start.");
            return;
        }

        var offset = await SkipBacklogAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            List<TelegramUpdate> updates;
            try
            {
                updates = await GetUpdatesAsync(offset, _chatOptions.PollTimeoutSeconds, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to poll Telegram for updates; retrying shortly.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            foreach (var update in updates)
            {
                offset = update.UpdateId + 1;
                await HandleUpdateAsync(update, stoppingToken);
            }
        }
    }

    private async Task<long> SkipBacklogAsync(CancellationToken cancellationToken)
    {
        try
        {
            var pending = await GetUpdatesAsync(offset: 0, timeoutSeconds: 0, cancellationToken);
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

    private async Task<List<TelegramUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var url = $"{ApiBaseUrl}/bot{_telegramOptions.BotToken}/getUpdates?timeout={timeoutSeconds}&offset={offset}";
        var payload = await _httpClient.GetFromJsonAsync<TelegramUpdatesResponse>(url, cancellationToken);
        return payload?.Result ?? [];
    }

    private async Task HandleUpdateAsync(TelegramUpdate update, CancellationToken cancellationToken)
    {
        var message = update.Message;
        if (string.IsNullOrWhiteSpace(message?.Text)) return;

        var incomingChatId = message.Chat.Id.ToString();
        if (!IsAuthorizedChat(incomingChatId, _telegramOptions.ChatId))
        {
            _logger.LogWarning("Ignoring Telegram message from unauthorized chat {ChatId}", incomingChatId);
            return;
        }

        try
        {
            var answer = await AnswerQuestionAsync(message.Text, cancellationToken);
            await SendMessageAsync(answer, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to answer a Telegram chat question");
            try { await SendMessageAsync("Sorry, something went wrong answering that.", cancellationToken); }
            catch (Exception sendEx) { _logger.LogError(sendEx, "Failed to send the fallback error reply"); }
        }
    }

    private async Task<string> AnswerQuestionAsync(string question, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var events = await _eventStore.GetEventsAsync(now.AddDays(-_chatOptions.LookbackDays), now, cancellationToken);

        var recent = events
            .Where(e => string.Equals(e.Status, "completed", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.OccurredAt)
            .Take(_chatOptions.MaxEventsInContext)
            .OrderBy(e => e.OccurredAt)
            .ToList();

        var systemPrompt = $"""
            You are an assistant for a home security gate-camera system. The current date and time is
            {now:dddd, MMMM d, yyyy h:mm tt} ({now:zzz} offset). Answer the user's question using ONLY
            the event log below, which covers the last {_chatOptions.LookbackDays} day(s) up to now.
            Mention every relevant event in the log, not just the first one. Reply like a short text
            message: concise and conversational, no markdown. If the log doesn't contain the answer,
            say so plainly instead of guessing.

            Event log:
            {BuildEventLog(recent)}
            """;

        var answer = await _chatClient.AskAsync(systemPrompt, question, cancellationToken);
        return answer.Length > 4000 ? answer[..4000] : answer;
    }

    private Task SendMessageAsync(string text, CancellationToken cancellationToken) =>
        _httpClient.PostAsync(
            $"{ApiBaseUrl}/bot{_telegramOptions.BotToken}/sendMessage",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["chat_id"] = _telegramOptions.ChatId,
                ["text"] = text
            }),
            cancellationToken);

    public static bool IsAuthorizedChat(string incomingChatId, string configuredChatId) =>
        !string.IsNullOrWhiteSpace(configuredChatId) &&
        string.Equals(incomingChatId, configuredChatId, StringComparison.Ordinal);

    public static string BuildEventLog(IReadOnlyList<AlertEventRecord> events)
    {
        if (events.Count == 0) return "No camera events have been recorded in this period.";

        var log = new StringBuilder();
        foreach (var e in events)
        {
            log.Append(
                $"{e.OccurredAt:g} | {e.Camera} | {e.EventType ?? "unknown"} | notified={(e.NotificationRecommended == true ? "yes" : "no")} | {e.Summary}\n");
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
