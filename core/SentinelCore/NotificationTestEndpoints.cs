using System.Net;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SentinelCore;

// "Send a test" for the notification channels, behind POST /api/notifications/test/{channel}.
// Until this existed, the only way to find out whether an SMTP app password or a bot token was
// right was to wait for something to walk past a camera - and a channel that silently fails looks
// exactly like a quiet night.
//
// Every gate ExecuteAsync applies - Enabled, OnlyWhenNotificationRecommended, MinimumConfidence,
// the duplicate cooldown - is deliberately skipped here. A test answers one question, "do these
// credentials work?", and a test that quietly declined to send because the confidence bar was
// high would answer the opposite of what pressing it looked like.
//
// It doesn't require the channel to be enabled either: checking an app password before switching
// the channel on is exactly when this is most useful. The reply says so rather than leaving
// someone to conclude from a successful test that alerts are now flowing.
//
// The values tested are whatever is currently saved (IOptionsMonitor). Nothing is read from the
// request, so the schema's ExtraAction - an endpoint and a method, no body - is all a client
// needs, and every client gets the button without knowing what a channel is.
public static class NotificationTestEndpoints
{
    public static void MapNotificationTestEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/notifications/test");

        group.MapPost("/email", async (IOptionsMonitor<EmailOptions> options, CancellationToken ct) =>
            Respond(await NotificationTester.SendTestEmailAsync(options.CurrentValue, ct)));

        group.MapPost("/telegram", async (
            IHttpClientFactory httpClientFactory, IOptionsMonitor<TelegramOptions> options, CancellationToken ct) =>
        {
            var telegram = options.CurrentValue;
            var client = httpClientFactory.CreateClient(nameof(NotificationTestEndpoints));
            client.Timeout = TimeSpan.FromSeconds(telegram.TimeoutSeconds);
            return Respond(await NotificationTester.SendTestTelegramAsync(client, telegram, ct));
        });
    }

    // 502 rather than 400 on failure: the settings are legal, something downstream refused them.
    // The clients read "error" out of a non-2xx body (and nothing else), so that key is contract.
    private static IResult Respond((bool Success, string Detail) outcome) =>
        outcome.Success
            ? Results.Ok(new { sent = true, detail = outcome.Detail })
            : Results.Json(new { sent = false, error = outcome.Detail }, statusCode: 502);
}

// Static and free of DI on purpose: the alert actions hold per-camera cooldown state and are
// registered as IAlertAction, and resolving one of those concretely just to test-send would have
// meant either changing their lifetimes or keeping a second instance around. Neither buys
// anything here - a test send shares no state with the real one.
public static class NotificationTester
{
    public static async Task<(bool Success, string Detail)> SendTestEmailAsync(
        EmailOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.SmtpHost))
            return (false, "No SMTP Host is set. Fill the Email section in and save it, then try again.");
        if (string.IsNullOrWhiteSpace(options.ToAddress))
            return (false, "No To Address is set. Fill the Email section in and save it, then try again.");

        var fromAddress = string.IsNullOrWhiteSpace(options.FromAddress) ? options.Username : options.FromAddress;
        if (string.IsNullOrWhiteSpace(fromAddress))
            return (false, "No From Address or Username is set, so there is nothing to send from.");

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(fromAddress, options.FromName),
                Subject = "Sentinel Core test email",
                IsBodyHtml = true,
                Body = """
                       <p>This is a test from Sentinel Core's Email settings.</p>
                       <p>If you are reading it, the mail server, credentials and addresses in that
                       section are working, and camera alerts will arrive the same way.</p>
                       """
            };
            message.To.Add(options.ToAddress);

            using var client = new SmtpClient(options.SmtpHost, options.SmtpPort)
            {
                EnableSsl = options.EnableSsl,
                UseDefaultCredentials = false,
                Credentials = new NetworkCredential(options.Username, options.AppPassword),
                DeliveryMethod = SmtpDeliveryMethod.Network
            };

            await client.SendMailAsync(message).WaitAsync(cancellationToken);
            return (true, $"Test email sent to {options.ToAddress}.{DisabledNote(options.Enabled, "Email")}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, Explain(ex));
        }
    }

    public static async Task<(bool Success, string Detail)> SendTestTelegramAsync(
        HttpClient httpClient, TelegramOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.BotToken))
            return (false, "No Bot Token is set. Fill the Telegram section in and save it, then try again.");
        if (string.IsNullOrWhiteSpace(options.ChatId))
            return (false, "No Chat ID is set. Fill the Telegram section in and save it, then try again.");

        var payload = new Dictionary<string, string>
        {
            ["chat_id"] = options.ChatId,
            ["text"] = "This is a test from Sentinel Core's Telegram settings. If you are reading it, "
                + "the bot token and chat ID are right, and camera alerts will arrive the same way.",
            ["parse_mode"] = "HTML"
        };

        try
        {
            using var response = await httpClient.PostAsync(
                $"https://api.telegram.org/bot{options.BotToken}/sendMessage",
                new FormUrlEncodedContent(payload),
                cancellationToken);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return (false, DescribeTelegramFailure(response.StatusCode, body));

            return (true, $"Test message sent to chat {options.ChatId}.{DisabledNote(options.Enabled, "Telegram")}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, Explain(ex));
        }
    }

    // A test that worked while the channel is switched off is a true answer to a misleading
    // question - the credentials are fine and no alert will ever be sent. Say both.
    private static string DisabledNote(bool enabled, string channel) =>
        enabled ? string.Empty : $" Note that {channel} is currently switched off, so real alerts still won't be sent.";

    // Telegram explains itself well ("chat not found", "Unauthorized") and its own words beat
    // anything paraphrased from a status code. The raw body is the fallback, not the first choice.
    private static string DescribeTelegramFailure(HttpStatusCode status, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("description", out var description)
                && description.GetString() is { Length: > 0 } text)
            {
                return $"Telegram refused the message: {text}";
            }
        }
        catch (JsonException)
        {
            // Not JSON - fall through to the status code.
        }

        return $"Telegram answered {(int)status}: {Truncate(body)}";
    }

    // SmtpException nests the useful sentence one or two levels down ("No such host is known"),
    // and the outer message on its own is the same generic line for every possible cause.
    private static string Explain(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message) && !messages.Contains(current.Message))
                messages.Add(current.Message);
        }

        return Truncate(string.Join(" ", messages));
    }

    // These end up in a toast and under a form field, and an SMTP server having a bad day can
    // answer with a great deal of text.
    private static string Truncate(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return "The send failed without saying why.";
        return trimmed.Length <= 400 ? trimmed : trimmed[..400] + "…";
    }
}
