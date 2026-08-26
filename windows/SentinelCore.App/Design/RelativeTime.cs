namespace SentinelCore.App.Design;

/// <summary>
/// "29m ago", the same one-unit past-tense phrasing the web dashboard uses.
/// </summary>
/// <remarks>
/// Worth having rather than a built-in: .NET's humanizing options render a running two-unit duration
/// ("29 minutes, 42 seconds") with a seconds field that ticks. For an event feed that reads like a
/// countdown rather than a timestamp, and it doesn't match the dashboard.
/// </remarks>
public static class RelativeTime
{
    public static string Describe(DateTimeOffset moment) => Describe(moment, DateTimeOffset.Now);

    public static string Describe(DateTimeOffset moment, DateTimeOffset now)
    {
        var elapsed = now - moment;

        // Clock skew between the server and this PC can put a just-received event a second or two
        // into the future, which would otherwise read "in 2s". Anything that close is "now".
        if (elapsed < TimeSpan.Zero)
        {
            return elapsed > TimeSpan.FromMinutes(-1) ? "now" : $"in {Describe(-elapsed)}";
        }

        return elapsed < TimeSpan.FromSeconds(5) ? "now" : $"{Describe(elapsed)} ago";
    }

    private static string Describe(TimeSpan span)
    {
        if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds}s";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d";
        if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 7)}w";
        return $"{(int)(span.TotalDays / 365)}y";
    }
}
