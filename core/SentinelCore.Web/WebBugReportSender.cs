using System.Collections.Concurrent;
using System.Net.Mail;
using SentinelCore;

namespace SentinelCore.Web;

// The Web project has no SMTP configuration of its own - it reads BugReports and Email straight
// out of the main app's appsettings.json, same as everything else it shares with that process.
public sealed class WebBugReportSender(AppSettingsFile settings, ILogger<WebBugReportSender> logger) : IBugReportSender
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(60);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new();

    public async Task SendAsync(BugReport report, CancellationToken cancellationToken)
    {
        var bugReportOptions = await settings.GetSectionAsync<BugReportOptions>("BugReports", cancellationToken);
        if (bugReportOptions?.Enabled != true) return;

        var key = $"{report.Category}|{BugReportEmail.ExceptionTypeName(report)}";
        var now = DateTimeOffset.UtcNow;
        if (_lastSent.TryGetValue(key, out var last) && now - last < Cooldown) return;

        var email = await settings.GetSectionAsync<EmailOptions>("Email", cancellationToken);
        if (email is null || string.IsNullOrWhiteSpace(email.Username) || string.IsNullOrWhiteSpace(email.AppPassword))
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
