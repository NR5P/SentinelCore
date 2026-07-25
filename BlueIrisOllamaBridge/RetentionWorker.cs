using Microsoft.Extensions.Options;

namespace BlueIrisOllamaBridge;

public sealed class RetentionWorker : BackgroundService
{
    private readonly IOptionsMonitor<BridgeOptions> _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<RetentionWorker> _logger;

    public RetentionWorker(IOptionsMonitor<BridgeOptions> options, IHostEnvironment environment, ILogger<RetentionWorker> logger)
    {
        _options = options;
        _environment = environment;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { DeleteExpiredFiles(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Retention cleanup failed"); }
            await Task.Delay(TimeSpan.FromHours(12), stoppingToken);
        }
    }

    private void DeleteExpiredFiles()
    {
        var options = _options.CurrentValue;
        var configured = options.DataDirectory;
        var root = Path.IsPathRooted(configured) ? configured : Path.Combine(_environment.ContentRootPath, configured);
        if (!Directory.Exists(root)) return;
        var cutoff = DateTime.UtcNow.AddDays(-options.RetentionDays);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try { if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
