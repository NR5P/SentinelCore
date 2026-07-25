using System.Collections.Concurrent;
using System.Diagnostics;

namespace BlueIrisOllamaBridge.Web;

// Runs one continuous low-res ffmpeg capture per camera that has an enabled trigger zone,
// independent of whether anyone is watching the LIVE tab. Every ~2 frames/sec gets diffed
// (see MotionDetector); a big-enough blob inside the zone writes a TriggerEvent and saves a
// full-res snapshot, subject to per-camera cooldown/rate-limit settings.
public sealed class TriggerZoneMonitor(
    ICameraStore cameras,
    ITriggerConfigStore configStore,
    ITriggerEventStore eventStore,
    HlsStreamManager streams,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<TriggerZoneMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(10);

    private readonly string _ffmpegPath = configuration["FfmpegPath"] ?? "ffmpeg";
    private readonly string _imagesRoot = ResolveImagesRoot(configuration, environment);
    private readonly ConcurrentDictionary<Guid, DetectionSession> _sessions = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_imagesRoot);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Trigger zone reconcile failed");
            }

            try
            {
                await Task.Delay(ReconcileInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        foreach (var session in _sessions.Values) StopSession(session);
    }

    private async Task ReconcileAsync(CancellationToken stoppingToken)
    {
        var enabled = await configStore.GetEnabledAsync(stoppingToken);
        var enabledIds = enabled.Select(c => c.CameraId).ToHashSet();

        foreach (var (cameraId, session) in _sessions)
        {
            if (!enabledIds.Contains(cameraId))
            {
                _sessions.TryRemove(cameraId, out _);
                StopSession(session);
            }
        }

        foreach (var config in enabled)
        {
            if (_sessions.TryGetValue(config.CameraId, out var existing))
            {
                if (ConfigsEquivalent(existing.Config, config)) continue;
                _sessions.TryRemove(config.CameraId, out _);
                StopSession(existing);
            }

            var camera = await cameras.GetAsync(config.CameraId, stoppingToken);
            if (camera is null) continue;

            var session = StartSession(camera, config);
            _sessions[config.CameraId] = session;
        }
    }

    private DetectionSession StartSession(Camera camera, TriggerConfig config)
    {
        var sourceUrl = HlsStreamManager.BuildAuthenticatedUrl(camera);
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[]
        {
            "-rtsp_transport", "tcp",
            "-i", sourceUrl,
            "-vf", $"fps=2,scale={MotionDetector.GridWidth}:{MotionDetector.GridHeight},format=rgb24",
            "-f", "rawvideo",
            "-"
        }) psi.ArgumentList.Add(arg);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        logger.LogInformation("Started trigger zone detection for camera {CameraId} ({Name}), pid {Pid}", camera.Id, camera.Name, process.Id);

        var cts = new CancellationTokenSource();
        var session = new DetectionSession(process, config, cts, new MotionDetector(config.ZonePolygon));
        _ = RunDetectionLoopAsync(camera, session, cts.Token);
        _ = DrainAsync(process.StandardError);

        return session;
    }

    private async Task RunDetectionLoopAsync(Camera camera, DetectionSession session, CancellationToken cancellationToken)
    {
        var stream = session.Process.StandardOutput.BaseStream;
        var buffer = new byte[MotionDetector.FrameSizeBytes];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await ReadExactAsync(stream, buffer, cancellationToken)) break;

                var blobFraction = session.Detector.ProcessFrame((byte[])buffer.Clone());
                if (blobFraction is null || blobFraction < session.Config.MinTriggerArea) continue;

                await MaybeTriggerAsync(camera, session, blobFraction.Value, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Trigger zone detection loop ended for camera {CameraId}", camera.Id);
        }
    }

    private async Task MaybeTriggerAsync(Camera camera, DetectionSession session, double areaFraction, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        lock (session.TriggerLock)
        {
            if (session.LastTriggerAt is { } last && (now - last).TotalSeconds < session.Config.CooldownSeconds)
                return;

            while (session.RecentTriggers.Count > 0 && (now - session.RecentTriggers.Peek()).TotalMinutes >= 1)
                session.RecentTriggers.Dequeue();

            if (session.RecentTriggers.Count >= session.Config.MaxTriggersPerMinute)
                return;

            session.LastTriggerAt = now;
            session.RecentTriggers.Enqueue(now);
        }

        try
        {
            var bytes = await streams.CaptureSnapshotAsync(camera, cancellationToken);
            if (bytes is null) return;

            var cameraDir = Path.Combine(_imagesRoot, camera.Id.ToString("N"));
            Directory.CreateDirectory(cameraDir);
            var imagePath = Path.Combine(cameraDir, $"{now:yyyyMMdd-HHmmss-fff}.jpg");
            await File.WriteAllBytesAsync(imagePath, bytes, cancellationToken);

            await eventStore.AddAsync(camera.Id, imagePath, areaFraction, cancellationToken);
            logger.LogInformation("Trigger zone fired for camera {CameraId} ({Name}), area {Area:P1}", camera.Id, camera.Name, areaFraction);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save trigger snapshot for camera {CameraId}", camera.Id);
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0) return false;
            offset += read;
        }

        return true;
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() != null) { }
        }
        catch
        {
            // process exited
        }
    }

    private void StopSession(DetectionSession session)
    {
        try
        {
            session.Cts.Cancel();
            if (!session.Process.HasExited)
            {
                session.Process.Kill(entireProcessTree: true);
                session.Process.WaitForExit(3000);
            }
        }
        catch
        {
            // best effort
        }
        finally
        {
            session.Process.Dispose();
            session.Cts.Dispose();
        }
    }

    private static bool ConfigsEquivalent(TriggerConfig a, TriggerConfig b)
    {
        if (a.Enabled != b.Enabled) return false;
        if (Math.Abs(a.MinTriggerArea - b.MinTriggerArea) > 0.0001) return false;
        if (a.CooldownSeconds != b.CooldownSeconds) return false;
        if (a.MaxTriggersPerMinute != b.MaxTriggersPerMinute) return false;
        if (a.ZonePolygon.Count != b.ZonePolygon.Count) return false;

        for (var i = 0; i < a.ZonePolygon.Count; i++)
        {
            if (Math.Abs(a.ZonePolygon[i].X - b.ZonePolygon[i].X) > 0.0001) return false;
            if (Math.Abs(a.ZonePolygon[i].Y - b.ZonePolygon[i].Y) > 0.0001) return false;
        }

        return true;
    }

    private static string ResolveImagesRoot(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["CamerasDataDirectory"] ?? "data";
        var root = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
        return Path.Combine(root, "trigger-images");
    }

    private sealed class DetectionSession(Process process, TriggerConfig config, CancellationTokenSource cts, MotionDetector detector)
    {
        public Process Process { get; } = process;
        public TriggerConfig Config { get; } = config;
        public CancellationTokenSource Cts { get; } = cts;
        public MotionDetector Detector { get; } = detector;

        public readonly object TriggerLock = new();
        public DateTimeOffset? LastTriggerAt;
        public readonly Queue<DateTimeOffset> RecentTriggers = new();
    }
}
