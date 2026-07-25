using System.Collections.Concurrent;
using System.Diagnostics;

namespace BlueIrisOllamaBridge.Web;

// Starts an ffmpeg process per camera on first request, remuxing whatever RTSP/HTTP source
// the camera speaks into HLS segments a browser can actually play (browsers have no native
// RTSP support). Video is copied, not re-encoded, so this is cheap as long as the source is
// H.264 - true of the overwhelming majority of IP cameras. Streams nobody is watching are
// killed after a short idle period so this doesn't burn CPU on cameras left unwatched.
public sealed class HlsStreamManager : IDisposable
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    // ffmpeg can only start an HLS segment on a keyframe boundary when copying video
    // (not re-encoding), so startup latency depends on the camera's keyframe interval -
    // some cameras/encoders use long GOPs, so this stays generous rather than flaky.
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<Guid, StreamSession> _sessions = new();
    private readonly string _ffmpegPath;
    private readonly string _workingRoot;
    private readonly ILogger<HlsStreamManager> _logger;
    private readonly Timer _cleanupTimer;

    public HlsStreamManager(IConfiguration configuration, IHostEnvironment environment, ILogger<HlsStreamManager> logger)
    {
        _ffmpegPath = configuration["FfmpegPath"] ?? "ffmpeg";
        var configured = configuration["CamerasDataDirectory"] ?? "data";
        var root = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
        _workingRoot = Path.Combine(root, "hls");
        Directory.CreateDirectory(_workingRoot);
        _logger = logger;
        _cleanupTimer = new Timer(_ => CleanupIdleSessions(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public async Task<string> EnsureStreamStartedAsync(Camera camera, CancellationToken cancellationToken)
    {
        var session = _sessions.GetOrAdd(camera.Id, _ => StartSession(camera));
        session.LastAccessed = DateTimeOffset.UtcNow;

        var playlistPath = Path.Combine(session.OutputDirectory, "stream.m3u8");
        var deadline = DateTimeOffset.UtcNow + StartupTimeout;
        while (!File.Exists(playlistPath) && DateTimeOffset.UtcNow < deadline)
        {
            if (session.Process.HasExited)
            {
                _sessions.TryRemove(camera.Id, out _);
                throw new InvalidOperationException(
                    $"ffmpeg exited before producing a stream - check the camera URL/credentials. Recent log:\n{session.RecentLog}");
            }

            await Task.Delay(200, cancellationToken);
        }

        if (!File.Exists(playlistPath))
        {
            _sessions.TryRemove(camera.Id, out _);
            KillSession(session);
            throw new TimeoutException($"Timed out waiting for the stream to start. Recent log:\n{session.RecentLog}");
        }

        return playlistPath;
    }

    public void Touch(Guid cameraId)
    {
        if (_sessions.TryGetValue(cameraId, out var session))
            session.LastAccessed = DateTimeOffset.UtcNow;
    }

    public string? GetSegmentPath(Guid cameraId, string fileName)
    {
        if (!_sessions.TryGetValue(cameraId, out var session)) return null;
        var safeName = Path.GetFileName(fileName); // guards against path traversal
        return Path.Combine(session.OutputDirectory, safeName);
    }

    public void StopStream(Guid cameraId)
    {
        if (_sessions.TryRemove(cameraId, out var session))
            KillSession(session);
    }

    private StreamSession StartSession(Camera camera)
    {
        var outputDir = Path.Combine(_workingRoot, camera.Id.ToString("N"));
        if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        Directory.CreateDirectory(outputDir);

        var sourceUrl = BuildAuthenticatedUrl(camera);
        var args = BuildFfmpegArguments(sourceUrl, camera.IncludeAudio, outputDir);

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        _logger.LogInformation("Started HLS stream for camera {CameraId} ({Name}), pid {Pid}", camera.Id, camera.Name, process.Id);

        var session = new StreamSession(process, outputDir) { LastAccessed = DateTimeOffset.UtcNow };
        _ = DrainAsync(process.StandardOutput, session);
        _ = DrainAsync(process.StandardError, session);

        return session;
    }

    // ffmpeg writes its progress/diagnostic log to stderr; if nothing reads the pipe it fills
    // up and blocks the process. This drains it and keeps a bounded tail for error messages.
    private static async Task DrainAsync(StreamReader reader, StreamSession session)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
                session.AppendLog(line);
        }
        catch
        {
            // process exited; nothing to do
        }
    }

    public static List<string> BuildFfmpegArguments(string sourceUrl, bool includeAudio, string outputDir)
    {
        var args = new List<string>
        {
            "-rtsp_transport", "tcp",
            "-i", sourceUrl,
            "-c:v", "copy"
        };

        if (includeAudio)
            args.AddRange(["-c:a", "aac", "-ac", "1"]);
        else
            args.Add("-an");

        args.AddRange([
            "-f", "hls",
            "-hls_time", "2",
            "-hls_list_size", "4",
            "-hls_flags", "delete_segments+omit_endlist",
            "-hls_segment_filename", Path.Combine(outputDir, "seg_%03d.ts"),
            Path.Combine(outputDir, "stream.m3u8")
        ]);

        return args;
    }

    public static string BuildAuthenticatedUrl(Camera camera)
    {
        if (string.IsNullOrWhiteSpace(camera.Username)) return camera.StreamUrl;

        var builder = new UriBuilder(camera.StreamUrl)
        {
            UserName = camera.Username,
            Password = camera.Password ?? string.Empty
        };
        return builder.Uri.ToString();
    }

    private void CleanupIdleSessions()
    {
        var cutoff = DateTimeOffset.UtcNow - IdleTimeout;
        foreach (var (id, session) in _sessions)
        {
            if (session.LastAccessed < cutoff && _sessions.TryRemove(id, out var removed))
            {
                _logger.LogInformation("Stopping idle HLS stream for camera {CameraId}", id);
                KillSession(removed);
            }
        }
    }

    private void KillSession(StreamSession session)
    {
        try
        {
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
            try { Directory.Delete(session.OutputDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        foreach (var session in _sessions.Values) KillSession(session);
        _sessions.Clear();
    }

    private sealed class StreamSession(Process process, string outputDirectory)
    {
        private const int MaxLogLines = 20;
        private readonly object _logLock = new();
        private readonly Queue<string> _log = new();

        public Process Process { get; } = process;
        public string OutputDirectory { get; } = outputDirectory;
        public DateTimeOffset LastAccessed { get; set; }

        public void AppendLog(string line)
        {
            lock (_logLock)
            {
                _log.Enqueue(line);
                while (_log.Count > MaxLogLines) _log.Dequeue();
            }
        }

        public string RecentLog
        {
            get
            {
                lock (_logLock) return string.Join('\n', _log);
            }
        }
    }
}
