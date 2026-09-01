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
        try { TempDirectories.Delete(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // Stands in for ffmpeg: records that it was started, writes a playlist and one segment into
    // the output directory the real arguments name, then either stays alive like a healthy
    // session or exits like a camera that dropped.
    // refuseUrlContaining stands in for a camera that advertises a rendition it won't actually
    // serve: real ffmpeg refuses an input it can't open and quits before writing anything, so the
    // stub has to quit before the playlist too - exiting *after* writing one is a working stream
    // that merely stopped, which is a different case entirely.
    // announceCodec stands in for the line real ffmpeg prints on stderr as it opens the input.
    // HlsStreamManager reads the codec out of that line to decide whether the session it just
    // started is packaging the stream the way this camera actually needs - so a stub that never
    // prints one is a camera whose codec is never established, which is the existing behaviour
    // every other test here relies on. The pause before the playlist mirrors real ffmpeg, which
    // announces the input well before it has cut a first segment.
    // refusalMessage is what the stub prints on stderr as it refuses. A silent refusal is the
    // original behaviour and still means "ffmpeg quit and said nothing useful"; a refusal that
    // speaks is how a real camera answers, and it is the only thing that tells a rejected password
    // apart from a rendition that isn't there.
    private void WriteFfmpegStub(
        bool stayAlive, bool emptySegment = false, string? refuseUrlContaining = null,
        string? announceCodec = null, string? refusalMessage = null)
    {
        PosixShellStubs.SkipIfUnavailable();

        var complaint = refusalMessage is null ? "" : $"echo \"{refusalMessage}\" >&2; ";
        var refusal = refuseUrlContaining is null
            ? ""
            : $"""
              case "$*" in *{refuseUrlContaining}*) {complaint}exit 1;; esac
              """;
        var segmentBody = emptySegment ? "" : "printf '\\x47fake-segment-payload' > \"$OUTDIR/seg_000.ts\"";
        var announcement = announceCodec is null
            ? ""
            : $"echo \"  Stream #0:0: Video: {announceCodec} (Main), yuv420p(tv), 3840x2160, 25 fps\" >&2\nsleep 0.3";
        // The final argument is the thumbnail, not the playlist (BuildFfmpegArguments appends the
        // poster-frame output last), so the output directory is derived from it and the playlist
        // written alongside by name.
        File.WriteAllText(_stubPath, $"""
            #!/bin/sh
            echo "$@" >> "{_invocationLog}"
            {refusal}
            for LAST in "$@"; do :; done
            OUTDIR=$(dirname "$LAST")
            {announcement}
            {segmentBody}
            printf '#EXTM3U\nseg_000.ts\n' > "$OUTDIR/stream.m3u8"
            {(stayAlive ? "while true; do sleep 1; done" : "exit 0")}
            """);

        PosixShellStubs.MakeExecutable(_stubPath);
    }

    private int InvocationCount() =>
        File.Exists(_invocationLog) ? File.ReadAllLines(_invocationLog).Length : 0;

    // Kills the stub processes this test started, standing in for a camera dropping out from
    // under a session that was running perfectly well a moment ago.
    private void KillStubProcesses()
    {
        PosixShellStubs.Kill(_stubPath);
    }

    // StartedAt is deliberately read-only on the session, so the "ran for a while then died" case
    // is set up by reaching in rather than by making the production type mutable for a test.
    //
    // Sessions are keyed by camera *and* rendition since live view gained a low-resolution stream
    // for grid tiles, so the key has to be built rather than passed as a bare id. Main is the one
    // these tests drive: they use a stub ffmpeg and a URL with no derivable low stream, so that is
    // also what a Sub request would resolve to.
    private static void SetSessionStartedAt(
        HlsStreamManager manager, Guid cameraId, DateTimeOffset startedAt,
        StreamQuality quality = StreamQuality.Main)
    {
        var sessionsField = typeof(HlsStreamManager)
            .GetField("_sessions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var sessions = sessionsField.GetValue(manager)!;
        var keyType = typeof(HlsStreamManager)
            .GetNestedType("SessionKey", System.Reflection.BindingFlags.NonPublic)!;
        var key = Activator.CreateInstance(keyType, cameraId, quality)!;
        var indexer = sessions.GetType().GetProperty("Item")!;
        var session = indexer.GetValue(sessions, [key])!;
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
                ["CamerasDataDirectory"] = _root,
                // Same reason as Go2RtcManagerTests: the default 11984 is the live go2rtc on a
                // machine running SentinelCore. Nothing here is expected to call the sidecar's
                // API, but the manager below is constructed with this configuration, and a test
                // that only reaches the network when someone later changes it is the kind that
                // starts quietly mutating a running install. Pin it somewhere unreachable.
                ["Go2Rtc:ApiPort"] = "1"
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

    // A camera whose URL names a rendition ffmpeg can be pointed at separately. TestCamera's plain
    // "rtsp://127.0.0.1/stream" deliberately has no derivable low stream, which is the other half
    // of this pair.
    private static Camera ReolinkCamera() => new(
        Guid.NewGuid(), "Gate", "rtsp://127.0.0.1/h264Preview_01_main", null, null, false, false, null, null,
        false, DateTimeOffset.UtcNow);

    // The load-bearing guarantee of the whole split. A camera with no low stream must be served
    // from the session it already has, not from a second identical one: two ffmpegs pulling the
    // same main stream would double both the process count and the connections against a Reolink's
    // small budget, which is the exact opposite of what serving a low rendition is for.
    [Fact]
    public async Task ACameraWithNoLowStream_ServesGridTilesFromItsMainSession_RatherThanASecondCopyOfIt()
    {
        WriteFfmpegStub(stayAlive: true);
        using var manager = CreateManager();
        var camera = TestCamera();

        var main = await manager.EnsureStreamStartedAsync(
            camera, StreamQuality.Main, TestContext.Current.CancellationToken);
        var sub = await manager.EnsureStreamStartedAsync(
            camera, StreamQuality.Sub, TestContext.Current.CancellationToken);

        Assert.Equal(main, sub);
        Assert.Equal(1, InvocationCount());
        // And the segments a player reads out of that playlist have to resolve under the name it
        // asked for, since it follows them relative to the URL it was handed.
        Assert.Equal(
            Path.Combine(_root, "hls", camera.Id.ToString("N")),
            Path.GetDirectoryName(sub));
    }

    [Fact]
    public async Task ACameraWithALowStream_RunsItAsItsOwnSession_SoBothRenditionsCanBeWatchedAtOnce()
    {
        WriteFfmpegStub(stayAlive: true);
        using var manager = CreateManager();
        var camera = ReolinkCamera();

        var main = await manager.EnsureStreamStartedAsync(
            camera, StreamQuality.Main, TestContext.Current.CancellationToken);
        var sub = await manager.EnsureStreamStartedAsync(
            camera, StreamQuality.Sub, TestContext.Current.CancellationToken);

        // Someone watching one camera full-screen while a grid elsewhere shows tiles is the
        // ordinary case, not a corner one - neither may evict or reuse the other's segments.
        Assert.NotEqual(main, sub);
        Assert.Equal(2, InvocationCount());
        Assert.Equal(Path.Combine(_root, "hls", $"{camera.Id:N}-sub"), Path.GetDirectoryName(sub));
    }

    [Fact]
    public async Task ACameraThatDoesntActuallyServeItsDerivedLowStream_FallsBackToTheMainOne()
    {
        // ffmpeg refuses an input it can't open and quits immediately, which is what a camera whose
        // low stream is switched off looks like from here. A dead tile would be the wrong answer:
        // the picture is available, just not at the size that was asked for.
        WriteFfmpegStub(stayAlive: true, refuseUrlContaining: "_sub");
        using var manager = CreateManager();
        var camera = ReolinkCamera();

        var sub = await manager.EnsureStreamStartedAsync(
            camera, StreamQuality.Sub, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_root, "hls", camera.Id.ToString("N")), Path.GetDirectoryName(sub));

        // And the camera is remembered, so the next tile goes straight to the main stream instead
        // of spawning another doomed ffmpeg to rediscover the same thing.
        var invocationsAfterFallback = InvocationCount();
        await manager.EnsureStreamStartedAsync(camera, StreamQuality.Sub, TestContext.Current.CancellationToken);
        Assert.Equal(invocationsAfterFallback, InvocationCount());
    }

    // The other half of that: the memory has to expire. "Never produced a playlist" is not only
    // ever "the rendition is switched off" - a Reolink refusing one more connection looks exactly
    // the same from here, and on these cameras it is the likely reading, since detection already
    // holds the ext stream and the clip buffer holds the main one. Remembering such a camera for
    // the life of the process would put every grid tile back on the full-resolution stream for
    // good, which is the thing the low rendition exists to prevent.
    [Fact]
    public async Task ACameraWhoseLowStreamFailedOnce_IsAskedAgainOnceTheRetryIntervalIsUp()
    {
        WriteFfmpegStub(stayAlive: true, refuseUrlContaining: "_sub");
        using var manager = CreateManager();
        var camera = ReolinkCamera();

        var fellBack = await manager.EnsureStreamStartedAsync(
            camera, StreamQuality.Sub, TestContext.Current.CancellationToken);
        Assert.Equal(Path.Combine(_root, "hls", camera.Id.ToString("N")), Path.GetDirectoryName(fellBack));

        // The camera frees up - whatever was holding the connection lets go - and the interval
        // passes.
        WriteFfmpegStub(stayAlive: true);
        ExpireLowResRetry(manager, camera.Id);

        var sub = await manager.EnsureStreamStartedAsync(
            camera, StreamQuality.Sub, TestContext.Current.CancellationToken);
        Assert.Equal(Path.Combine(_root, "hls", $"{camera.Id:N}-sub"), Path.GetDirectoryName(sub));
    }

    // The regression this pair exists for. On 2026-08-27 an edit blanked the gate camera's login,
    // ffmpeg answered every DESCRIBE with 401, and that was read as "this camera has no low stream"
    // - so a wrong password put every grid tile onto a 4K/25fps main stream, ten minutes at a time,
    // while the log asserted a cause that the ffmpeg output printed directly underneath it
    // contradicted.
    [Fact]
    public async Task ACameraThatRejectsTheLogin_IsNotRecordedAsHavingNoLowStream()
    {
        WriteFfmpegStub(
            stayAlive: true, refuseUrlContaining: "_sub",
            refusalMessage: "Error opening input: Server returned 401 Unauthorized (authorization failed)");
        using var manager = CreateManager();
        var camera = ReolinkCamera();

        await Assert.ThrowsAnyAsync<Exception>(() => manager.EnsureStreamStartedAsync(
            camera, StreamQuality.Sub, TestContext.Current.CancellationToken));

        // No ten-minute latch: the next request is free to ask for the low stream again.
        Assert.False(LowResRetryIsRemembered(manager, camera.Id));

        // And no fallback was attempted. The main stream is reached with the same login, so trying
        // it buys a second failure and a second startup budget - the sub attempt is the only one.
        Assert.Equal(1, InvocationCount());
    }

    // The other half: not latching must not turn into a doomed ffmpeg per request either.
    // /api/cameras warms every camera on each fetch, so an unrationed retry would put one behind
    // every camera every few seconds for as long as the password stayed wrong.
    [Fact]
    public async Task ARejectedLogin_IsRetriedInSecondsRatherThanPerRequestOrPerTenMinutes()
    {
        WriteFfmpegStub(
            stayAlive: true, refuseUrlContaining: "_sub",
            refusalMessage: "Error opening input: Server returned 401 Unauthorized (authorization failed)");
        using var manager = CreateManager();
        var camera = ReolinkCamera();

        await Assert.ThrowsAnyAsync<Exception>(() => manager.EnsureStreamStartedAsync(
            camera, StreamQuality.Sub, TestContext.Current.CancellationToken));
        var afterFirstAttempt = InvocationCount();

        await Assert.ThrowsAnyAsync<Exception>(() => manager.EnsureStreamStartedAsync(
            camera, StreamQuality.Sub, TestContext.Current.CancellationToken));
        Assert.Equal(afterFirstAttempt, InvocationCount());

        // Seconds, not minutes - the whole point of refusing the ten-minute latch.
        Assert.True(LowResHoldOffRemaining(manager, camera.Id) <= TimeSpan.FromSeconds(10));

        // The password is put back, the hold-off lapses, and the low stream is simply asked for
        // again - no interval to wait out, because none was ever recorded.
        WriteFfmpegStub(stayAlive: true);
        ClearHoldOff(manager);

        var sub = await manager.EnsureStreamStartedAsync(
            camera, StreamQuality.Sub, TestContext.Current.CancellationToken);
        Assert.Equal(Path.Combine(_root, "hls", $"{camera.Id:N}-sub"), Path.GetDirectoryName(sub));
    }

    // A camera that has dropped off the network says nothing about its renditions either, and its
    // main stream is just as unreachable - so it gets the same treatment.
    [Fact]
    public async Task ACameraThatCannotBeReached_IsNotRecordedAsHavingNoLowStream()
    {
        WriteFfmpegStub(
            stayAlive: true, refuseUrlContaining: "_sub",
            refusalMessage: "Error opening input: No route to host");
        using var manager = CreateManager();
        var camera = ReolinkCamera();

        await Assert.ThrowsAnyAsync<Exception>(() => manager.EnsureStreamStartedAsync(
            camera, StreamQuality.Sub, TestContext.Current.CancellationToken));

        Assert.False(LowResRetryIsRemembered(manager, camera.Id));
        Assert.Equal(1, InvocationCount());
    }

    private static bool LowResRetryIsRemembered(HlsStreamManager manager, Guid cameraId)
    {
        var field = typeof(HlsStreamManager)
            .GetField("_lowResRetryAfter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var map = (System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset>)field.GetValue(manager)!;
        return map.ContainsKey(cameraId);
    }

    // Read through the non-generic view: both the key and the value are private nested types, and
    // naming them from a test would mean making them visible for no other reason.
    private static TimeSpan LowResHoldOffRemaining(HlsStreamManager manager, Guid cameraId)
    {
        var blocks = HoldOffMap(manager);
        Assert.Single(blocks.Keys.Cast<object>());

        foreach (System.Collections.DictionaryEntry entry in blocks)
        {
            var until = (DateTimeOffset)entry.Value!.GetType().GetProperty("Until")!.GetValue(entry.Value)!;
            return until - DateTimeOffset.UtcNow;
        }

        throw new InvalidOperationException("no hold-off was recorded");
    }

    private static void ClearHoldOff(HlsStreamManager manager) => HoldOffMap(manager).Clear();

    private static System.Collections.IDictionary HoldOffMap(HlsStreamManager manager)
    {
        var field = typeof(HlsStreamManager)
            .GetField("_restartBlockedUntil", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (System.Collections.IDictionary)field.GetValue(manager)!;
    }

    // Winds the retry deadline back into the past, the same reach-in approach SetSessionStartedAt
    // takes - the alternative is a test that waits out a real ten-minute interval.
    private static void ExpireLowResRetry(HlsStreamManager manager, Guid cameraId)
    {
        var field = typeof(HlsStreamManager)
            .GetField("_lowResRetryAfter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var map = (System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset>)field.GetValue(manager)!;
        Assert.True(map.ContainsKey(cameraId), "the failed low-resolution attempt should have been remembered");
        map[cameraId] = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1);
    }

    // The bug this covers: an H.265 camera streamed as MPEG-TS plays nowhere on Apple's players.
    // HEVC only ever entered HLS alongside fragmented MP4 and AVFoundation supports it only there,
    // so iOS and macOS answer a TS-wrapped HEVC stream with a black tile, no error and no log line
    // - which is indistinguishable from a broken camera until someone probes the segments by hand.
    [Fact]
    public async Task RestartsAsFragmentedMp4WhenTheCameraTurnsOutToSendHevc()
    {
        WriteFfmpegStub(stayAlive: true, announceCodec: "hevc");
        using var manager = CreateManager();
        var camera = TestCamera();

        await manager.EnsureStreamStartedAsync(camera, TestContext.Current.CancellationToken);

        var invocations = File.ReadAllLines(_invocationLog);
        Assert.Equal(2, invocations.Length);

        // The first attempt is the ordinary one - nothing about a camera is known before ffmpeg
        // has opened it, so nobody pays for H.265 packaging on the guess that they might need it.
        Assert.Contains("seg_%03d.ts", invocations[0]);
        Assert.DoesNotContain("fmp4", invocations[0]);

        // The second is the corrected one.
        Assert.Contains("-hls_segment_type fmp4", invocations[1]);
        Assert.Contains("-hls_fmp4_init_filename init.mp4", invocations[1]);
        Assert.Contains("seg_%03d.m4s", invocations[1]);
        Assert.Contains("-tag:v hvc1", invocations[1]);
    }

    // ...and settles there. The replacement is started from the corrected entry, so its own codec
    // line agrees with it - if it did not, every request for an H.265 camera would restart ffmpeg.
    [Fact]
    public async Task TheHevcRestartHappensOnceRatherThanOnEveryRequest()
    {
        WriteFfmpegStub(stayAlive: true, announceCodec: "hevc");
        using var manager = CreateManager();
        var camera = TestCamera();

        await manager.EnsureStreamStartedAsync(camera, TestContext.Current.CancellationToken);
        await manager.EnsureStreamStartedAsync(camera, TestContext.Current.CancellationToken);
        await manager.EnsureStreamStartedAsync(camera, TestContext.Current.CancellationToken);

        Assert.Equal(2, InvocationCount());
    }

    // The mirror image, and the reason the correction is driven by what ffmpeg reports rather than
    // by a remembered flag: change the encoder back to H.264 in the camera's own web UI and
    // nothing tells this server about it, so a session started from the stale entry has to notice
    // it is now wrapping H.264 in an hvc1 tag and undo itself.
    [Fact]
    public async Task RestartsBackToMpegTsWhenAnHevcCameraIsSwitchedToH264()
    {
        WriteFfmpegStub(stayAlive: true, announceCodec: "h264");
        using var manager = CreateManager();
        var camera = TestCamera();
        RememberHevc(manager, camera.Id);

        await manager.EnsureStreamStartedAsync(camera, TestContext.Current.CancellationToken);

        var invocations = File.ReadAllLines(_invocationLog);
        Assert.Equal(2, invocations.Length);
        Assert.Contains("-hls_segment_type fmp4", invocations[0]);
        Assert.Contains("seg_%03d.ts", invocations[1]);
        Assert.DoesNotContain("hvc1", invocations[1]);
    }

    // A session that dies before saying anything, or a future ffmpeg that words the line
    // differently, leaves the codec unestablished. That has to mean "keep what you have" rather
    // than "assume H.264": flipping the packaging on no evidence would restart every stream on
    // every request, including the H.265 ones this exists to serve.
    [Fact]
    public async Task LeavesPackagingAloneWhenFfmpegNeverNamesTheCodec()
    {
        WriteFfmpegStub(stayAlive: true);
        using var manager = CreateManager();
        var camera = TestCamera();
        RememberHevc(manager, camera.Id);

        await manager.EnsureStreamStartedAsync(camera, TestContext.Current.CancellationToken);

        Assert.Equal(1, InvocationCount());
        Assert.Contains("-hls_segment_type fmp4", File.ReadAllLines(_invocationLog)[0]);
    }

    [Theory]
    // Real lines, as ffmpeg prints them for the cameras on this install.
    [InlineData("  Stream #0:0: Video: hevc (Main), yuv420p(tv), 3840x2160 [SAR 1:1 DAR 16:9], 25 fps, 25 tbr", "hevc")]
    [InlineData("    Stream #0:0: Video: h264 (High), yuv420p(progressive), 2560x1920, 30 fps, 30 tbr, 90k tbn", "h264")]
    // The poster-frame output ffmpeg also announces - parsed the same, and it is AppendLog's
    // first-match-wins rule rather than this method that keeps it from being mistaken for the
    // input's codec.
    [InlineData("  Stream #0:1: Video: mjpeg, yuvj420p(pc), 480x270, q=2-31, 200 kb/s", "mjpeg")]
    [InlineData("  Stream #0:1: Audio: aac (LC), 16000 Hz, mono, fltp", null)]
    [InlineData("frame=  120 fps= 25 q=-1.0 size=N/A time=00:00:04.80 bitrate=N/A speed=1.0x", null)]
    public void ParsesTheInputVideoCodecOutOfFfmpegsLog(string line, string? expected)
    {
        Assert.Equal(expected, HlsStreamManager.TryParseSourceVideoCodec(line));
    }

    [Fact]
    public void BuildsMpegTsArgumentsForAnOrdinaryH264Camera()
    {
        var args = HlsStreamManager.BuildFfmpegArguments("rtsp://camera/stream", includeAudio: false, "/out");

        Assert.Contains(Path.Combine("/out", "seg_%03d.ts"), args);
        Assert.DoesNotContain("-hls_segment_type", args);
        Assert.DoesNotContain("-tag:v", args);
    }

    [Fact]
    public void BuildsFragmentedMp4ArgumentsForAnHevcCamera()
    {
        var args = HlsStreamManager.BuildFfmpegArguments(
            "rtsp://camera/stream", includeAudio: false, "/out", hevcSource: true);

        Assert.Contains(Path.Combine("/out", "seg_%03d.m4s"), args);
        Assert.DoesNotContain(Path.Combine("/out", "seg_%03d.ts"), args);
        Assert.Equal("fmp4", args[args.IndexOf("-hls_segment_type") + 1]);

        // Bare, not a path: ffmpeg copies this string straight into the playlist's #EXT-X-MAP URI,
        // where a player resolves it against the playlist's own URL. An absolute path would be
        // published to every client as a server path none of them can fetch.
        Assert.Equal("init.mp4", args[args.IndexOf("-hls_fmp4_init_filename") + 1]);

        // hev1 - ffmpeg's default tag - is the one tag AVFoundation refuses.
        Assert.Equal("hvc1", args[args.IndexOf("-tag:v") + 1]);

        // The thumbnail stays the last argument overall - the stub ffmpeg in these tests derives
        // its output directory from it, as does the comment in WriteFfmpegStub.
        Assert.EndsWith("thumb.jpg", args[^1]);
    }

    // Seeds the remembered-codec cache the way a previous session would have, so the tests above
    // can start from a camera this server already believes it knows the packaging for.
    private static void RememberHevc(HlsStreamManager manager, Guid cameraId)
    {
        var field = typeof(HlsStreamManager)
            .GetField("_hevcSources", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var map = field.GetValue(manager)!;
        var keyType = typeof(HlsStreamManager)
            .GetNestedType("SessionKey", System.Reflection.BindingFlags.NonPublic)!;
        var key = Activator.CreateInstance(keyType, cameraId, StreamQuality.Main)!;
        map.GetType().GetProperty("Item")!.SetValue(map, true, [key]);
    }

    private sealed class StubEnvironment(string contentRoot) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Test";
    }
}
