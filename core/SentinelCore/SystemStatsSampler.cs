using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace SentinelCore;

public sealed record SystemStatusSnapshot(
    DateTimeOffset TimestampUtc,
    double CpuUsagePercent,
    int ProcessorCount,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    long DiskFreeBytes,
    long DiskTotalBytes,
    int QueuedAlerts,
    double UptimeSeconds,
    bool VisionBackendHealthy);

// Samples the machine's resource usage on a fixed interval and keeps only the latest snapshot in
// memory - cheap enough for the web dashboard and both mobile apps to poll without each request
// doing its own CPU-time sampling, which needs two measurements spaced apart to mean anything.
// Mirrors SelfMonitoringWorker's shape but exists to report state, not to alert.
//
// "The machine", not this process: see HostResources.cs. This process is nearly idle while the
// ffmpeg workers it starts and the vision backend it calls do the actual work, so reporting its
// own CPU and working set under a heading called Server Health described almost nothing. The
// process-scoped numbers remain as a fallback for a platform where the host readings can't be
// taken, since a number about the wrong thing still beats an empty card.
public sealed class SystemStatsSampler(
    IAlertQueue queue,
    IVisionAnalysisClient vision,
    IOptionsMonitor<BridgeOptions> bridgeOptions,
    IHostEnvironment environment,
    ILogger<SystemStatsSampler> logger) : BackgroundService
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);

    // Vision reachability is a network call - checked far less often than everything else here
    // so this loop doesn't hammer the backend every 5 seconds just to report a status flag.
    private static readonly TimeSpan VisionCheckInterval = TimeSpan.FromSeconds(30);

    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public SystemStatusSnapshot? Latest { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var process = Process.GetCurrentProcess();
        var lastCpuTime = process.TotalProcessorTime;
        var lastSampledAt = DateTimeOffset.UtcNow;
        var visionHealthy = true;
        var lastVisionCheck = DateTimeOffset.MinValue;

        var host = HostResourceReader.ForCurrentPlatform();
        var lastHostCpu = host?.ReadCpuTicks();
        if (host is null || lastHostCpu is null)
            logger.LogInformation(
                "Host-wide CPU and memory aren't readable on this platform; Server Health will report this process's own usage instead.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                process.Refresh();
                var now = DateTimeOffset.UtcNow;
                var cpuTime = process.TotalProcessorTime;
                var elapsedSeconds = (now - lastSampledAt).TotalSeconds;
                var cpuPercent = elapsedSeconds > 0
                    ? Math.Clamp((cpuTime - lastCpuTime).TotalSeconds / (elapsedSeconds * Environment.ProcessorCount) * 100.0, 0, 100)
                    : 0;
                lastCpuTime = cpuTime;
                lastSampledAt = now;

                // Busy ticks over total ticks across the whole interval - already averaged over
                // every core, so unlike the process figure above it needs no ProcessorCount
                // division. A counter that went backwards means a wrap or a bad read, so the
                // previous reading is replaced and this round keeps the process fallback.
                var hostCpu = host?.ReadCpuTicks();
                if (hostCpu is { } current && lastHostCpu is { } previous &&
                    current.Total > previous.Total && current.Busy >= previous.Busy)
                {
                    cpuPercent = Math.Clamp(
                        (current.Busy - previous.Busy) / (double)(current.Total - previous.Total) * 100.0, 0, 100);
                }

                if (hostCpu is not null) lastHostCpu = hostCpu;

                if (now - lastVisionCheck >= VisionCheckInterval)
                {
                    try
                    {
                        visionHealthy = await vision.IsReadyAsync(stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        visionHealthy = false;
                    }
                    lastVisionCheck = now;
                }

                var gcInfo = GC.GetGCMemoryInfo();
                var (diskFree, diskTotal) = GetDiskSpace();

                // Used and total have to come from the same place. The old pairing put this
                // process's working set over the machine's physical RAM, which is why the bar sat
                // near zero however loaded the box was.
                var memory = host?.ReadMemory() ??
                    new HostMemoryUsage(Environment.WorkingSet, gcInfo.TotalAvailableMemoryBytes);

                Latest = new SystemStatusSnapshot(
                    TimestampUtc: now,
                    CpuUsagePercent: Math.Round(cpuPercent, 1),
                    ProcessorCount: Environment.ProcessorCount,
                    MemoryUsedBytes: memory.UsedBytes,
                    MemoryTotalBytes: memory.TotalBytes,
                    DiskFreeBytes: diskFree,
                    DiskTotalBytes: diskTotal,
                    QueuedAlerts: queue.ApproximateCount,
                    UptimeSeconds: (now - _startedAt).TotalSeconds,
                    VisionBackendHealthy: visionHealthy);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "System stats sampling failed unexpectedly");
            }

            try
            {
                await Task.Delay(SampleInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private (long Free, long Total) GetDiskSpace()
    {
        try
        {
            var configured = bridgeOptions.CurrentValue.DataDirectory;
            var root = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
            var driveRoot = Path.GetPathRoot(Path.GetFullPath(root));
            if (string.IsNullOrEmpty(driveRoot)) return (0, 0);

            var drive = new DriveInfo(driveRoot);
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch
        {
            return (0, 0);
        }
    }
}
