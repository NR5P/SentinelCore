using System.Collections.Concurrent;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace SentinelCore;

// Reuses whichever SMTP account is already configured for Email alerts - asking for a second
// set of mail credentials just for bug reports would be a lot to ask for a feature this small.
public sealed class SmtpBugReportSender(
    IOptionsMonitor<BugReportOptions> bugReportOptions,
    IOptionsMonitor<EmailOptions> emailOptions,
    ILogger<SmtpBugReportSender> logger) : IBugReportSender
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(60);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new();

    public async Task SendAsync(BugReport report, CancellationToken cancellationToken)
    {
        if (!bugReportOptions.CurrentValue.Enabled) return;

        var key = $"{report.Category}|{BugReportEmail.ExceptionTypeName(report)}";
        var now = DateTimeOffset.UtcNow;
        if (_lastSent.TryGetValue(key, out var last) && now - last < Cooldown) return;

        var email = emailOptions.CurrentValue;
        if (string.IsNullOrWhiteSpace(email.Username) || string.IsNullOrWhiteSpace(email.AppPassword))
        {
            logger.LogWarning("Bug report suppressed - Email settings aren't configured, so there's no SMTP account to send it with");
            return;
        }

        _lastSent[key] = now;

        try
        {
            using var message = BugReportEmail.Build(email, report);
            await SmtpMailer.SendAsync(email, message, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send bug report email");
        }
    }
}

// Shared email-building logic so the main app and Web project produce an identical-looking
// report regardless of which process caught the error.
public static class BugReportEmail
{
    public static MailMessage Build(EmailOptions email, BugReport report)
    {
        var fromAddress = SmtpMailer.FromAddressFor(email);
        var message = new MailMessage
        {
            From = new MailAddress(fromAddress, "Sentinel Core Bug Reports"),
            Subject = $"[Sentinel Core Bug Report] {report.Process}: {report.Category}",
            IsBodyHtml = false,
            // Build line added here rather than on the BugReport record: the version is a
            // constant of the process, so threading it through every place a report is
            // constructed would be a lot of churn to say the same thing each time. Both
            // processes build their mail through here, so each stamps its own version.
            Body = $"""
                Process: {report.Process}
                Build: {BuildInfo.Current.Display}
                Category: {report.Category}
                Level: {report.Level}
                Occurred at: {report.OccurredAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}

                {report.Message}

                {report.ExceptionDetails ?? "(no exception attached)"}
                """
        };
        message.To.Add(BugReportRecipient.Address);
        return message;
    }

    // Dedup key component: groups repeats of "the same" error together (by category + exception
    // type) without being so granular that trivial message differences (an id, a timestamp) split
    // what's really one recurring issue into endless separate emails.
    public static string ExceptionTypeName(BugReport report) =>
        report.ExceptionDetails is null ? "NoException" : report.ExceptionDetails.Split(':', 2)[0];
}
