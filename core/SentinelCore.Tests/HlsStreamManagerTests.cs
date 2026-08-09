extern alias WebHost;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The live stream is the one feature where a transient camera problem used to become a permanent
// one. When ffmpeg died - a camera reboot, a network blip, an RTSP connection limit reached
// because something else grabbed a snapshot - its playlist and segments stayed on disk, so every
// later request was answered 200 with a stream frozen at the instant it died, on every client at
// once. Nothing recovered from that on its own, because each request refreshed the session's
// LastAccessed and the idle sweep never reclaimed it; only restarting the server fixed it.
//
// These tests drive HlsStreamManager with a stub standing in for ffmpeg, so they assert the
// recovery behaviour itself rather than needing a real camera or a real ffmpeg on the machine.
public sealed class HlsStreamManagerTests : IDisposable
{
    private readonly string _root;
    private readonly string _stubPath;
    private readonly string _invocationLog;

    public HlsStreamManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"hls-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _stubPath = Path.Combine(_root, "fake-ffmpeg.sh");
        _invocationLog = Path.Combine(_root, "invocations.txt");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // Stands in for ffmpeg: records that it was started, writes a playlist and one segment into
    // the output directory the real arguments name, then either stays alive like a healthy
    // session or exits like a camera that dropped.
    private void WriteFfmpegStub(bool stayAlive, bool emptySegment = false)
    {
        var segmentBody = emptySegment ? "" : "printf '\\x47fake-segment-payload' > \"$OUTDIR/seg_000.ts\"";
        // The final argument is the thumbnail, not the playlist (BuildFfmpegArguments appends the
        // poster-frame output last), so the output directory is derived from it and the playlist
        // written alongside by name.
        File.WriteAllText(_stubPath, $"""
            #!/bin/sh
            echo started >> "{_invocationLog}"
            for LAST in "$@"; do :; done
            OUTDIR=$(dirname "$LAST")
            {segmentBody}
            printf '#EXTM3U\nseg_000.ts\n' > "$OUTDIR/stream.m3u8"
            {(stayAlive ? "while true; do sleep 1; done" : "exit 0")}
            """);

        var chmod = System.Diagnostics.Process.Start("/bin/chmod", $"+x {_stubPath}")!;
        chmod.WaitForExit();
    }

    private int InvocationCount() =>
        File.Exists(_invocationLog) ? File.ReadAllLines(_invocationLog).Length : 0;

    // Kills the stub processes this test started, standing in for a camera dropping out from
    // under a session that was running perfectly well a moment ago.
    private void KillStubProcesses()
    {
        var pkill = System.Diagnostics.Process.Start("/usr/bin/pkill", $"-f {_stubPath}");
        pkill?.WaitForExit();
    }

    // StartedAt is deliberately read-only on the session, so the "ran for a while then died" case
    // is set up by reaching in rather than by making the production type mutable for a test.
    private static void SetSessionStartedAt(HlsStreamManager manager, Guid cameraId, DateTimeOffset startedAt)
    {
        var sessionsField = typeof(HlsStreamManager)
            .GetField("_sessions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var sessions = sessionsField.GetValue(manager)!;
        var indexer = sessions.GetType().GetProperty("Item")!;
        var session = indexer.GetValue(sessions, [cameraId])!;
        var backing = session.GetType()
            .GetField("<StartedAt>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        backing.SetValue(session, startedAt);
    }

    private HlsStreamManager CreateManager()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FfmpegPath"] = _stubPath,
                ["CamerasDataDirectory"] = _root
            })
            .Build();

        // The sidecar manager is only reached for browser-published cameras, which these tests
        // don't use - it is here to satisfy the constructor, not to be exercised.
        var go2rtc = new Go2RtcManager(
            configuration, new StubEnvironment(_root), new PlainHttpClientFactory(),
            NullLogger<Go2RtcManager>.Instance);

        return new HlsStreamManager(
            configuration, new StubEnvironment(_root), new CameraSource(configuration), go2rtc,
            NullLogger<HlsStreamManager>.Instance);
    }

    // Local rather than shared: Go2RtcManagerTests has its own, and a two-line stub is cheaper to
    // duplicate than to hoist into a helper both files then depend on.
    private sealed class PlainHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(2) };
    }

    private static Camera TestCamera() => new(
        Guid.NewGuid(), "Test Camera", "rtsp://127.0.0.1/stream", null, null, false, false, null, null, false, DateTimeOffset.UtcNow);

    // The regression this file exists for. A session that ran for a while and then dropped - the
    // camera rebooted, the network blipped - has to be noticed and replaced.
    [Fact]
    public async Task ASessionWhoseFfmpegDied_IsReplaced_RatherThanServingAFrozenStreamForever()
    {
        WriteFfmpegStub(stayAlive: true);
        using var manager = CreateManager();
        var camera = TestCamera();

        var playlist = await manager.EnsureStreamStartedAsync(camera, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(playlist));
        Assert.Equal(1, InvocationCount());

        // Kill it the way a camera dropping out does, and age the session past the restart
        // backoff so this is the "ran fine, then died" case rather than "never worked".
        KillStubProcesses();
        SetSessionStartedAt(manager, camera.Id, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5));
        await Task.Delay(300, TestContext.Current.CancellationToken);

        // Before the fix this returned the same stale playlist without ever noticing the process
        // was gone, because the liveness check only ran inside the "wait for the playlist to
        // appear" loop - which is skipped entirely once the file exists.
        await manager.EnsureStreamStartedAsync(camera, TestContext.Current.CancellationToken);

        Assert.True(
            InvocationCount() >= 2,
            $"a dead session should have been replaced with a fresh ffmpeg, but ffmpeg was only started {InvocationCount()} time(s)");
    }

    // The other side of that: a camera that is simply broken must not have a new ffmpeg spawned
    // for it on every request. /api/cameras warms up every camera at once, so an unbounded retry
    // here would let a downed camera spawn processes faster than they die.
    [Fact]
    public async Task ACameraWhoseStreamKeepsDyingImmediately_IsReportedRatherThanRestartedEndlessly()
    {
        WriteFfmpegStub(stayAlive: false);
        using var manager = CreateManager();
        var camera = TestCamera();

        // First attempt produces a playlist and exits straight away.
        await manager.EnsureStreamStartedAsync(camera, TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var afterFirst = InvocationCount();

        // Several more requests arrive while it is still inside the backoff window.
        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => manager.EnsureStreamStartedAsync(camera, TestContext.Current.CancellationToken));
        }

        Assert.True(
            InvocationCount() <= afterFirst + 1,
            $"a persistently-failing camera should not respawn ffmpeg per request, but it was started {InvocationCount()} time(s)");
    }

    [Fact]
    public async Task AHealthySession_IsReused_RatherThanRestartedOnEveryRequest()
    {
        WriteFfmpegStub(stayAlive: true);
        using var manager = CreateManager();
        var camera = TestCamera();

        await manager.EnsureStreamStartedAsync(camera, TestContext.Current.CancellationToken);
        await manager.EnsureStreamStartedAsync(camera, TestContext.Current.CancellationToken);
        await manager.EnsureStreamStartedAsync(camera, TestContext.Current.CancellationToken);

        // Restarting a live stream per request would drop every viewer's playback and pay a full
        // RTSP reconnect each time - the opposite failure from the one above.
        Assert.Equal(1, InvocationCount());
    }

    // A player handed zero bytes where it expects video generally treats it as a fatal decode
    // error rather than something to retry, so an incomplete segment has to read as missing.
    [Fact]
    public async Task AnEmptySegment_IsReportedMissing_RatherThanServedAsAnEmptyBody()
    {
        WriteFfmpegStub(stayAlive: true, emptySegment: true);
        using var manager = CreateManager();
        var camera = TestCamera();

        await manager.EnsureStreamStartedAsync(camera, TestContext.Current.CancellationToken);

        // The stub wrote no segment at all; create the empty file ffmpeg leaves behind when it has
        // opened a segment but not yet written to it.
        var outputDirectory = Path.Combine(_root, "hls", camera.Id.ToString("N"));
        await File.WriteAllBytesAsync(
            Path.Combine(outputDirectory, "seg_000.ts"), [], TestContext.Current.CancellationToken);

        Assert.Null(manager.GetSegmentPath(camera.Id, "seg_000.ts"));
    }

    [Fact]
    public async Task ACompleteSegment_IsStillServed()
    {
        WriteFfmpegStub(stayAlive: true);
        using var manager = CreateManager();
        var camera = TestCamera();

        await manager.EnsureStreamStartedAsync(camera, TestContext.Current.CancellationToken);

        var segment = manager.GetSegmentPath(camera.Id, "seg_000.ts");
        Assert.NotNull(segment);
        Assert.True(new FileInfo(segment).Length > 0);
    }

    // Path traversal guard - a segment name is attacker-controlled in the sense that it comes
    // straight off the URL.
    [Fact]
    public async Task ASegmentNameCannotEscapeTheSessionDirectory()
    {
        WriteFfmpegStub(stayAlive: true);
        using var manager = CreateManager();
        var camera = TestCamera();

        await manager.EnsureStreamStartedAsync(camera, TestContext.Current.CancellationToken);

        var escaped = manager.GetSegmentPath(camera.Id, "../../../../etc/passwd");
        // Either rejected outright, or flattened to a name inside the session directory.
        if (escaped is not null)
        {
            var sessionDirectory = Path.Combine(_root, "hls", camera.Id.ToString("N"));
            Assert.StartsWith(sessionDirectory, Path.GetFullPath(escaped), StringComparison.Ordinal);
        }
    }

    private sealed class StubEnvironment(string contentRoot) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Test";
    }
}
