namespace SentinelCore.Client;

public enum DiagnosticLevel
{
    Info,
    Notice,
    Error,
}

public sealed record DiagnosticEntry(DateTimeOffset Timestamp, DiagnosticLevel Level, string Message)
{
    public Guid Id { get; } = Guid.NewGuid();

    public string LevelLabel => Level switch
    {
        DiagnosticLevel.Error => "ERROR",
        DiagnosticLevel.Notice => "NOTICE",
        _ => "INFO",
    };
}

/// <summary>
/// Where the client library writes its connection trail. An interface rather than a direct call
/// into the view model so <see cref="Networking.SentinelClient"/> can be exercised in tests with
/// nothing but a list behind it.
/// </summary>
/// <remarks>
/// Request bodies, passwords, cookies, and server response contents are deliberately never written
/// here - only methods, URLs, status codes and timings.
/// </remarks>
public interface ISentinelLog
{
    void Log(string message, DiagnosticLevel level = DiagnosticLevel.Info);
}

/// <summary>A log that keeps nothing, for tests and for construction before the UI exists.</summary>
public sealed class NullSentinelLog : ISentinelLog
{
    public static NullSentinelLog Instance { get; } = new();

    public void Log(string message, DiagnosticLevel level = DiagnosticLevel.Info)
    {
    }
}
