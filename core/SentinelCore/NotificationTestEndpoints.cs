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

        group.MapPost("/webhook", async (
            IHttpClientFactory httpClientFactory, IOptionsMonitor<WebhookOptions> options, CancellationToken ct) =>
        {
            var webhook = options.CurrentValue;
            var client = httpClientFactory.CreateClient(nameof(NotificationTestEndpoints));
            client.Timeout = TimeSpan.FromSeconds(webhook.TimeoutSeconds);
            return Respond(await NotificationTester.SendTestWebhookAsync(client, webhook, ct));
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
        // Checked against saved settings rather than the boxes on screen, and the section can be
        // saved with Enabled off - so this can legitimately be reached half-filled-in.
        if (SmtpMailer.ValidateDeliverable(options) is { } problem)
            return (false, $"{problem} Fill the Email section in and save it, then try again.");

        var fromAddress = SmtpMailer.FromAddressFor(options);

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

            await SmtpMailer.SendAsync(options, message, cancellationToken);
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

    // Unlike email and Telegram, there is no service on the far end that knows what a test is -
    // whatever is listening gets a real event and may act on it, so the payload says plainly what
    // it is in every field a receiver is likely to read (camera name and summary both), and the
    // camera is a name no real camera has. An automation that fires on this fires visibly.
    //
    // The body is built by WebhookAlertAction.BuildRequest, not assembled here: the whole point of
    // the button is to show the receiving end the exact shape real alerts arrive in, and two
    // separately-maintained payloads would eventually disagree about that.
    public static async Task<(bool Success, string Detail)> SendTestWebhookAsync(
        HttpClient httpClient, WebhookOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Url))
            return (false, "No URL is set. Fill the Webhook section in and save it, then try again.");
        if (!Uri.TryCreate(options.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return (false, $"\"{Truncate(options.Url)}\" is not a valid http:// or https:// URL.");
        }

        var now = DateTimeOffset.UtcNow;
        var result = new AlertResult
        {
            Id = Guid.NewGuid(),
            Camera = "Sentinel Core test camera",
            OccurredAt = now,
            ReceivedAt = now,
            CompletedAt = now,
            Profile = "test",
            Model = "test",
            Status = "complete",
            Analysis = new VisionAnalysis
            {
                NotificationRecommended = true,
                Confidence = 1,
                EventType = "test",
                Summary = "This is a test event from Sentinel Core's Webhook settings, not something a "
                    + "camera saw. Real alerts arrive in exactly this shape.",
                NotificationReason = "Someone pressed the send test webhook button."
            }
        };

        try
        {
            using var request = WebhookAlertAction.BuildRequest(options, result);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // A webhook receiver is usually something the person reading this wrote, so its own
                // reply is the most useful thing on offer - but plenty answer a bare status code
                // with an empty body, and "answered 404:" trailing off says less than nothing.
                var explanation = string.IsNullOrWhiteSpace(body)
                    ? $"{uri.Host} answered {(int)response.StatusCode} ({response.ReasonPhrase}) with an empty body."
                    : $"{uri.Host} answered {(int)response.StatusCode}: {Truncate(body)}";
                return (false, explanation);
            }

            return (true, $"Test event posted to {uri.Host}, which answered {(int)response.StatusCode}."
                + DisabledNote(options.Enabled, "Webhook"));
        }
        // HttpClient reports its own timeout as a cancellation, so this has to be told apart from
        // the caller actually giving up. A wrong host on the LAN doesn't refuse the connection, it
        // just never answers - the most likely way to mistype this setting, and without this the
        // button would sit there for the timeout and then fail with no explanation at all.
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, $"No answer from {uri.Host} within {options.TimeoutSeconds} seconds. "
                + "Check the host and port are right and that it is reachable from this machine.");
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
        // The two failures worth naming, because the server's own wording for them is useless to
        // someone who has never configured SMTP before. Everything else gets reported verbatim.
        if (HintFor(exception) is { } hint) return Truncate(hint);

        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message) && !messages.Contains(current.Message))
                messages.Add(current.Message);
        }

        return Truncate(string.Join(" ", messages));
    }

    // A timeout here is nearly always the port: a provider that wants 465 leaves a connection to
    // 587 hanging with no reply at all, and vice versa. An auth rejection is nearly always the
    // password: the major consumer providers all refuse the normal account password over SMTP.
    private static string? HintFor(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException or System.Net.Sockets.SocketException)
                return "The mail server never answered. That usually means the wrong SMTP Port - " +
                       "587 and 465 are the two in common use, and providers differ on which they want - " +
                       "or an SMTP Host with a typo in it.";
            if (current is MailKit.Security.AuthenticationException)
                return "The mail server rejected the username and password. Gmail, Outlook, iCloud and " +
                       "Yahoo all refuse your normal account password here: generate an app password in " +
                       "the provider's security settings and use that instead. " + current.Message;
        }

        return null;
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
