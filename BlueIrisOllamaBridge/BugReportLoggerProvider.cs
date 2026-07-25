using Microsoft.Extensions.Logging;

namespace BlueIrisOllamaBridge;

// Hooks the standard logging pipeline so any Error/Critical log from our own code - in either
// process, since the Web project references this library - becomes a candidate bug report,
// without needing every catch block to remember to report itself. Purely mechanical: it doesn't
// know or care whether reporting is actually turned on or how to send one - each IBugReportSender
// implementation decides that for itself (they read settings differently: the main app has its
// own live-reloadable config, the Web project reads the main app's appsettings.json by file).
public sealed class BugReportLoggerProvider(string processName, IBugReportSender sender) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new BugReportLogger(processName, categoryName, sender);

    public void Dispose() { }

    private sealed class BugReportLogger(string processName, string categoryName, IBugReportSender sender) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Framework/third-party noise (Kestrel, MQTTnet, HttpClient, etc.) isn't "our bug" -
            // only report errors logged from our own namespaces in either project.
            if (!IsEnabled(logLevel) || !categoryName.StartsWith("BlueIrisOllamaBridge", StringComparison.Ordinal))
                return;

            var report = new BugReport(
                processName,
                categoryName,
                logLevel.ToString(),
                formatter(state, exception),
                exception?.ToString(),
                DateTimeOffset.UtcNow);

            // Log() is synchronous - fire-and-forget the actual send, and never let a reporting
            // failure become its own unhandled exception.
            _ = SendSafelyAsync(report);
        }

        private async Task SendSafelyAsync(BugReport report)
        {
            try
            {
                await sender.SendAsync(report, CancellationToken.None);
            }
            catch
            {
                // Best effort - there's nowhere useful left to report this failure to.
            }
        }
    }
}
