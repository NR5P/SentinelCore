using System.Net.Mail;
using SentinelCore;

namespace SentinelCore.Web;

public sealed record AutomationSendResult(bool Ok, string Message);

public sealed record AutomationChannels(bool PushReady, bool EmailReady);

// How a board automation reaches people: a push to their phones, or an email. Behind an interface
// so the engine's tests can see exactly what would have gone out without a phone or a mail server.
public interface IOrganizerAutomationNotifier
{
    Task<AutomationChannels> ChannelsAsync(CancellationToken cancellationToken);

    // `usernames` null means every registered phone.
    Task<AutomationSendResult> PushAsync(
        IReadOnlyCollection<string>? usernames, string title, string body, string? organizerItemId, CancellationToken cancellationToken);

    Task<AutomationSendResult> EmailAsync(
        IReadOnlyCollection<string> usernames, IReadOnlyCollection<string> addresses, string subject, string body, CancellationToken cancellationToken);
}

// The real thing: the same push setup and mail server the camera alerts use (Settings → Push
// and Settings → Email). Like OwnerNotifier it doesn't look at Email:Enabled, which switches
// camera alert emails on and off - someone can want those off and still want "bins out tonight".
public sealed class OrganizerAutomationNotifier(
    AppSettingsFile settings,
    PushNotificationDispatcher push,
    IAccountStore accounts,
    ILogger<OrganizerAutomationNotifier> logger) : IOrganizerAutomationNotifier
{
    public const int MaxEmailRecipients = 20;
    public const string FromName = "Sentinel Core Organizer";

    public async Task<AutomationChannels> ChannelsAsync(CancellationToken cancellationToken)
    {
        var pushOptions = await PushOptionsAsync(cancellationToken);
        var email = await settings.GetSectionAsync<EmailOptions>(EmailOptions.SectionName, cancellationToken);
        return new AutomationChannels(
            pushOptions.Enabled && PushProviderSelection.AnyConfigured(pushOptions),
            EmailReady(email));
    }

    // Enough to send a message somewhere: a server and a real From address, plus a password if
    // there's a login. The ToAddress camera alerts go to doesn't matter here.
    public static bool EmailReady(EmailOptions? options) =>
        options is not null
        && !string.IsNullOrWhiteSpace(options.SmtpHost)
        && SmtpMailer.LooksLikeEmailAddress(SmtpMailer.FromAddressFor(options))
        && (string.IsNullOrWhiteSpace(options.Username) || !string.IsNullOrWhiteSpace(options.AppPassword));

    public async Task<AutomationSendResult> PushAsync(
        IReadOnlyCollection<string>? usernames, string title, string body, string? organizerItemId, CancellationToken cancellationToken)
    {
        var options = await PushOptionsAsync(cancellationToken);
        if (!options.Enabled || !PushProviderSelection.AnyConfigured(options))
            return new AutomationSendResult(false, "Push notifications aren't set up on this server (Settings → Push), so nothing was sent.");

        var wanted = usernames?.Select(name => name.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var message = new PushMessage(title, body, null, null, PushMessage.OrganizerType, OrganizerItemId: organizerItemId);
        PushDeliveryReport report;
        try
        {
            report = await push.SendAsync(options, message, cancellationToken,
                wanted is null ? null : device => device.Username is { } owner && wanted.Contains(owner));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "A board automation's push failed");
            return new AutomationSendResult(false, $"The notification couldn't be sent: {exception.Message}");
        }

        var who = wanted is null ? "everyone" : Names(wanted);
        if (report.Attempted == 0)
        {
            return new AutomationSendResult(false, wanted is null
                ? "No phones are signed up for notifications yet."
                : $"No phone belonging to {who} is signed up for notifications yet - open the app once while signed in as them.");
        }
        if (report.Delivered == 0)
            return new AutomationSendResult(false, $"The notification to {who} didn't go through ({report.Failed} of {report.Attempted} phones refused it).");

        var phones = report.Delivered == 1 ? "1 phone" : $"{report.Delivered} phones";
        var partial = report.Failed > 0 ? $", {report.Failed} didn't take it" : "";
        return new AutomationSendResult(true, $"Notified {who} ({phones}{partial}).");
    }

    public async Task<AutomationSendResult> EmailAsync(
        IReadOnlyCollection<string> usernames, IReadOnlyCollection<string> addresses, string subject, string body, CancellationToken cancellationToken)
    {
        var options = await settings.GetSectionAsync<EmailOptions>(EmailOptions.SectionName, cancellationToken);
        if (!EmailReady(options))
            return new AutomationSendResult(false, "Email isn't set up on this server (Settings → Email), so nothing was sent.");

        var recipients = new List<string>();
        var missing = new List<string>();
        foreach (var username in usernames)
        {
            var account = await accounts.GetByUsernameAsync(username, cancellationToken);
            if (account?.Email is { Length: > 0 } email) recipients.Add(email);
            else missing.Add(username);
        }
        recipients.AddRange(addresses.Where(address => !string.IsNullOrWhiteSpace(address)).Select(address => address.Trim()));
        recipients = [.. recipients.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxEmailRecipients)];

        var missingNote = missing.Count == 0 ? "" : $" {Names(missing)} {(missing.Count == 1 ? "has" : "have")} no email address on their account.";
        if (recipients.Count == 0)
            return new AutomationSendResult(false, $"Nobody to email.{missingNote}");

        // One message each, so nobody sees who else it went to.
        var sent = new List<string>();
        var failed = new List<string>();
        foreach (var recipient in recipients)
        {
            try
            {
                using var message = new MailMessage
                {
                    From = new MailAddress(SmtpMailer.FromAddressFor(options!), FromName),
                    Subject = subject,
                    IsBodyHtml = false,
                    Body = body,
                };
                message.To.Add(recipient);
                await SmtpMailer.SendAsync(options!, message, cancellationToken);
                sent.Add(recipient);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "A board automation's email to {Recipient} failed", recipient);
                failed.Add($"{recipient} ({exception.Message})");
            }
        }

        if (sent.Count == 0)
            return new AutomationSendResult(false, $"The email couldn't be sent: {string.Join("; ", failed)}.{missingNote}");
        var failedNote = failed.Count == 0 ? "" : $" Couldn't reach {string.Join("; ", failed)}.";
        return new AutomationSendResult(failed.Count == 0, $"Emailed {string.Join(", ", sent)}.{failedNote}{missingNote}");
    }

    private async Task<PushNotificationOptions> PushOptionsAsync(CancellationToken cancellationToken) =>
        await settings.GetSectionAsync<PushNotificationOptions>(PushNotificationOptions.SectionName, cancellationToken)
        ?? new PushNotificationOptions();

    private static string Names(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count <= 1 ? string.Join("", list) : $"{string.Join(", ", list.Take(list.Count - 1))} and {list[^1]}";
    }
}
