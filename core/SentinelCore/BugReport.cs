namespace SentinelCore;

public sealed record BugReport(
    string Process,
    string Category,
    string Level,
    string Message,
    string? ExceptionDetails,
    DateTimeOffset OccurredAt);

public interface IBugReportSender
{
    Task SendAsync(BugReport report, CancellationToken cancellationToken);
}

public static class BugReportRecipient
{
    // Fixed on purpose - this is a developer diagnostic address, not a user-configurable
    // setting. Deliberately not exposed anywhere in the settings API/UI.
    public const string Address = "nathan@nr5p.com";
}
