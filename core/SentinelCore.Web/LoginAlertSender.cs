using System.Collections.Concurrent;
using System.Net.Mail;
using SentinelCore;

namespace SentinelCore.Web;

public interface ILoginAlertSender
{
    Task NotifyInvalidLoginAsync(string attemptedUsername, string ip, string via, int failuresInWindow, CancellationToken cancellationToken);
}

// Alerts the homeowner - not the developer, see WebBugReportSender for that - when someone gets
// a login wrong. Follows the same shape as InternetOutageWorker's NotifyAsync (try Email,
// Telegram and push to the phone apps independently, one failing doesn't block the other) but lives here rather than in the
// main app because that's where AuthEndpoints and the login attempt itself live. Like
// WebBugReportSender, it has no SMTP/Telegram configuration of its own - Email and Telegram are
// read straight out of the main app's appsettings.json via the shared AppSettingsFile.
public sealed class LoginAlertSender(
    AppSettingsFile settings,
    IHttpClientFactory httpClientFactory,
    PushNotificationDispatcher push,
    ILogger<LoginAlertSender> logger) : ILoginAlertSender
{
    private const string TelegramApiBaseUrl = "https://api.telegram.org";

    // Keyed by IP only, not IP+username - a brute-force burst tries many usernames from one
    // address, and that whole burst should still cost one alert, not one per guess.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new();

    public async Task NotifyInvalidLoginAsync(string attemptedUsername, string ip, string via, int failuresInWindow, CancellationToken cancellationToken)
    {
        var options = await settings.GetSectionAsync<LoginAlertOptions>("LoginAlerts", cancellationToken);
        if (options is not { Enabled: true }) return;

        var now = DateTimeOffset.UtcNow;
        if (_lastSent.TryGetValue(ip, out var last) && now - last < TimeSpan.FromMinutes(options.CooldownMinutes))
            return;

        var report = new LoginAlertReport(attemptedUsername, ip, via, failuresInWindow, now);

        var sent = false;
        sent |= await TrySendEmailAsync(report, cancellationToken);
        sent |= await TrySendTelegramAsync(report, cancellationToken);
        sent |= await TrySendPushAsync(report, cancellationToken);

        // Only starts the cooldown once something was actually delivered - an alert that
        // couldn't be sent (misconfigured SMTP, Telegram unreachable) shouldn't also suppress
        // the next attempt's chance to try again.
        if (sent) _lastSent[ip] = now;
    }

    // Deliberately doesn't require Email:Enabled / Telegram:Enabled - those toggles are about
    // routing camera alerts, and someone may well want those off while still wanting security
    // pings. This only needs the underlying account to be configured (same reasoning as
    // WebBugReportSender reusing the Email account regardless of Email:Enabled), gated instead
    // by LoginAlerts:Enabled above.
    private async Task<bool> TrySendEmailAsync(LoginAlertReport report, CancellationToken cancellationToken)
    {
        var email = await settings.GetSectionAsync<EmailOptions>("Email", cancellationToken);
        if (email is null || string.IsNullOrWhiteSpace(email.Username) ||
            string.IsNullOrWhiteSpace(email.AppPassword) || string.IsNullOrWhiteSpace(email.ToAddress))
        {
            logger.LogDebug("Skipping the invalid-login email - Email isn't enabled/configured");
            return false;
        }

        try
        {
            using var message = LoginAlertNotification.BuildEmail(email, report);
            await SmtpMailer.SendAsync(email, message, cancellationToken);
            logger.LogInformation("Sent an invalid-login email for {Ip}", report.Ip);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send the invalid-login email");
            return false;
        }
    }

    private async Task<bool> TrySendTelegramAsync(LoginAlertReport report, CancellationToken cancellationToken)
    {
        var telegram = await settings.GetSectionAsync<TelegramOptions>("Telegram", cancellationToken);
        if (telegram is null || string.IsNullOrWhiteSpace(telegram.BotToken) || string.IsNullOrWhiteSpace(telegram.ChatId))
        {
            logger.LogDebug("Skipping the invalid-login Telegram message - Telegram isn't enabled/configured");
            return false;
        }

        try
        {
            var client = httpClientFactory.CreateClient(nameof(LoginAlertSender));
            using var response = await client.PostAsync(
                $"{TelegramApiBaseUrl}/bot{telegram.BotToken}/sendMessage",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["chat_id"] = telegram.ChatId,
                    ["text"] = LoginAlertNotification.BuildTelegramText(report)
                }),
                cancellationToken);

            // Telegram answers 200 with ok:false for a bad token or chat id, so only a
            // non-success status is worth logging - there's nothing to retry from here.
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning("Invalid-login Telegram message failed: {StatusCode} {Body}", (int)response.StatusCode, body);
                return false;
            }

            logger.LogInformation("Sent an invalid-login Telegram message for {Ip}", report.Ip);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send the invalid-login Telegram message");
            return false;
        }
    }
    // Same gate as the alarm's push: push turned on and at least one provider set up.
    private async Task<bool> TrySendPushAsync(LoginAlertReport report, CancellationToken cancellationToken)
    {
        var options = await settings.GetSectionAsync<PushNotificationOptions>(PushNotificationOptions.SectionName, cancellationToken);
        if (options is null || !options.Enabled || !PushProviderSelection.AnyConfigured(options))
        {
            logger.LogDebug("Skipping the invalid-login push - push notifications aren't enabled/configured");
            return false;
        }

        try
        {
            // EventType "system", as OwnerNotifier uses: there is no camera event to open on tap.
            var result = await push.SendAsync(options, LoginAlertNotification.BuildPush(report), cancellationToken);
            logger.LogInformation("Invalid-login push for {Ip}: {Delivered}/{Attempted} delivered", report.Ip, result.Delivered, result.Attempted);
            return result.Delivered > 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to push the invalid-login alert");
            return false;
        }
    }
}

public sealed record LoginAlertReport(string AttemptedUsername, string Ip, string Via, int FailuresInWindow, DateTimeOffset OccurredAt);

public static class LoginAlertNotification
{
    public static MailMessage BuildEmail(EmailOptions email, LoginAlertReport report)
    {
        var fromAddress = SmtpMailer.FromAddressFor(email);

        var message = new MailMessage
        {
            From = new MailAddress(fromAddress, email.FromName),
            Subject = "Invalid login attempt on Sentinel Core",
            IsBodyHtml = false,
            Body = $"""
                Someone entered an incorrect password for the Sentinel Core dashboard.

                Attempted username: {report.AttemptedUsername}
                Source address:     {report.Ip} ({report.Via})
                Time:                {report.OccurredAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}
                Failed attempts from this address in the current window: {report.FailuresInWindow}

                Further alerts from this same address are suppressed for a while so repeated
                guesses don't flood your inbox - check the server log for the full history.
                """
        };

        message.To.Add(email.ToAddress);
        return message;
    }

    // Short on purpose: a lock-screen banner shows a line or two, and the email/Telegram copies
    // carry the full detail.
    public static PushMessage BuildPush(LoginAlertReport report) => new(
        "Invalid login attempt",
        $"Wrong password for '{report.AttemptedUsername}' from {report.Ip} ({report.Via}).",
        null,
        null,
        "system");

    public static string BuildTelegramText(LoginAlertReport report) =>
        $"""
        Invalid login attempt on Sentinel Core.

        Attempted username: {report.AttemptedUsername}
        Source address: {report.Ip} ({report.Via})
        Time: {report.OccurredAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}
        Failed attempts from this address in the current window: {report.FailuresInWindow}

        Further alerts from this address are suppressed for a while - check the server log for the full history.
        """;
}
