using System.Net.Mail;
using SentinelCore;

namespace SentinelCore.Web;

// A plain message to the owner over every channel they have set up - email, Telegram, and push
// to the phone apps - for things that are about the system rather than about a camera event.
// The first such thing is PublisherWatchdog telling them a webcam device has stopped streaming.
//
// Same shape as LoginAlertSender and NuisanceAdvisorWorker: each channel is tried independently
// out of the main app's own configuration, one failing never blocks another, and nothing at all
// goes out on an install with no channel configured - the caller logs regardless. Deliberately
// not gated on Email:Enabled / Telegram:Enabled: those toggles route camera alerts, and someone
// may well want those off while still wanting to hear that a camera has gone dark.
public sealed class OwnerNotifier(
    AppSettingsFile settings,
    IHttpClientFactory httpClientFactory,
    PushNotificationDispatcher push,
    ILogger<OwnerNotifier> logger)
{
    private const string TelegramApiBaseUrl = "https://api.telegram.org";

    // True when at least one channel accepted the message.
    public async Task<bool> SendAsync(string subject, string text, CancellationToken cancellationToken)
    {
        var sent = false;
        sent |= await TrySendEmailAsync(subject, text, cancellationToken);
        sent |= await TrySendTelegramAsync(subject, text, cancellationToken);
        sent |= await TrySendPushAsync(subject, text, cancellationToken);
        return sent;
    }

    private async Task<bool> TrySendEmailAsync(string subject, string text, CancellationToken cancellationToken)
    {
        var email = await settings.GetSectionAsync<EmailOptions>("Email", cancellationToken);
        if (email is null || string.IsNullOrWhiteSpace(email.Username) ||
            string.IsNullOrWhiteSpace(email.AppPassword) || string.IsNullOrWhiteSpace(email.ToAddress))
            return false;

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(SmtpMailer.FromAddressFor(email), email.FromName),
                Subject = subject,
                IsBodyHtml = false,
                Body = text
            };
            message.To.Add(email.ToAddress);
            await SmtpMailer.SendAsync(email, message, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to email the owner: {Subject}", subject);
            return false;
        }
    }

    private async Task<bool> TrySendTelegramAsync(string subject, string text, CancellationToken cancellationToken)
    {
        var telegram = await settings.GetSectionAsync<TelegramOptions>("Telegram", cancellationToken);
        if (telegram is null || string.IsNullOrWhiteSpace(telegram.BotToken) || string.IsNullOrWhiteSpace(telegram.ChatId))
            return false;

        try
        {
            var client = httpClientFactory.CreateClient(nameof(OwnerNotifier));
            using var response = await client.PostAsync(
                $"{TelegramApiBaseUrl}/bot{telegram.BotToken}/sendMessage",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["chat_id"] = telegram.ChatId,
                    ["text"] = $"{subject}\n\n{text}"
                }),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning("Telegram message to the owner failed: {StatusCode} {Body}", (int)response.StatusCode, body);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send the owner a Telegram message: {Subject}", subject);
            return false;
        }
    }

    private async Task<bool> TrySendPushAsync(string subject, string text, CancellationToken cancellationToken)
    {
        var options = await settings.GetSectionAsync<PushNotificationOptions>(PushNotificationOptions.SectionName, cancellationToken);
        if (options is not { Enabled: true }) return false;

        try
        {
            // EventType "system" rather than a camera event type: the apps use it to decide what
            // tapping the notification opens, and there is no event here to open.
            var report = await push.SendAsync(options, new PushMessage(subject, text, null, null, "system"), cancellationToken);
            return report.Delivered > 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to push a message to the owner's devices: {Subject}", subject);
            return false;
        }
    }
}
