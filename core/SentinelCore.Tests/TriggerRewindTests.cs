using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace SentinelCore.Tests;

// Motion is detected on a 320x180 copy of the video, which knows the moment but is useless as a
// picture. Rather than photograph the scene again once the trigger has been handled - by which time
// on a busy machine the subject has gone - the frame is fetched from the rolling video buffer by the
// timestamp the capture loop stamped on it.
//
// The lookup is by time and nothing else: no search, no scoring, no threshold. That is what makes it
// behave the same on fast hardware and slow, and it only works because TriggerZoneMonitor's capture
// loop cannot fall behind (see FrameSlot) and so the timestamp is truthful.
public sealed class TriggerRewindTests
{
    private static readonly DateTimeOffset Base = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private static (string Path, DateTimeOffset StartsAt) Seg(int second) =>
        ($"seg_{second:00}.ts", Base.AddSeconds(second));

    // --- segment selection --------------------------------------------------------------------

    // A segment runs until the next one starts, so with a long GOP the segment holding the start of
    // a window is the one that BEGAN before it. Requiring containment drops exactly that segment.
    [Fact]
    public void SelectSegmentsCovering_IncludesTheSegmentThatStartsBeforeTheWindowAndRunsIntoIt()
    {
        var ordered = new[] { Seg(0), Seg(8), Seg(16) };

        var covering = ClipRecorder.SelectSegmentsCovering(ordered, Base.AddSeconds(10), Base.AddSeconds(14));

        Assert.Single(covering);
        Assert.Equal(Base.AddSeconds(8), covering[0].StartsAt);
    }

    [Fact]
    public void SelectSegmentsCovering_TreatsAGapLongerThanMaxSegmentSpan_AsAGapNotASegment()
    {
        // 60s to the next segment is an outage, not one very long segment - so the first segment
        // must not be credited with covering a window in the middle of it.
        var ordered = new[] { Seg(0), Seg(60) };

        Assert.Empty(ClipRecorder.SelectSegmentsCovering(ordered, Base.AddSeconds(30), Base.AddSeconds(40)));
    }

    // Load-bearing for a lookup by time: the concat demuxer treats the first segment's start as t=0
    // and assumes the rest follow back to back, so a hole shifts every later offset by the length of
    // the outage. A frame asked for at "3.5s in" would come from somewhere else entirely.
    [Fact]
    public void ContiguousRunEndingAtNewest_DropsEverythingBeforeAHole()
    {
        var ordered = new[] { Seg(0), Seg(2), Seg(40), Seg(42), Seg(44) };

        var run = ClipRecorder.ContiguousRunEndingAtNewest(ordered);

        Assert.Equal(3, run.Count);
        Assert.Equal(Base.AddSeconds(40), run[0].StartsAt);
    }

    [Fact]
    public void ContiguousRunEndingAtNewest_KeepsAnUnbrokenBuffer() =>
        Assert.Equal(3, ClipRecorder.ContiguousRunEndingAtNewest([Seg(0), Seg(2), Seg(4)]).Count);

    // --- the lookup ---------------------------------------------------------------------------

    [Fact]
    public void OffsetInto_IsMeasuredFromTheFirstSegmentNotTheRequestedTime()
    {
        var run = new[] { Seg(10), Seg(12), Seg(14) };

        Assert.Equal(3.5, TriggerRewind.OffsetInto(run, Base.AddSeconds(13.5)), 3);
        Assert.Equal(0, TriggerRewind.OffsetInto(run, Base.AddSeconds(10)), 3);
    }

    // -ss before -i is input seeking: jump to the nearest keyframe rather than decoding the whole
    // concat and throwing all but one frame away.
    [Fact]
    public void BuildExtractArguments_SeeksBeforeTheInput()
    {
        var args = TriggerRewind.BuildExtractArguments("/tmp/list.txt", 3.5, "/tmp/out.jpg");

        Assert.True(args.IndexOf("-ss") < args.IndexOf("-i"));
        Assert.Equal("3.5", args[args.IndexOf("-ss") + 1]);
        Assert.Equal("1", args[args.IndexOf("-frames:v") + 1]);
        Assert.Equal("1", args[args.IndexOf("-threads") + 1]);
    }

    [Fact]
    public void FormatSeconds_UsesADotRegardlessOfMachineLocale()
    {
        // ffmpeg silently truncates "3,5" to 3 seconds - a sub-second error nobody would ever trace
        // back to a culture setting.
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("3.5", TriggerRewind.FormatSeconds(3.5));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void TriggerRewindOptions_DefaultToOff_SoNothingChangesUntilItIsTurnedOn()
    {
        var options = new TriggerRewindOptions();

        Assert.False(options.Enabled);
        Assert.Equal(6, options.LookbackSeconds);
    }

    // --- service, against a stub ffmpeg -------------------------------------------------------

    [Fact]
    public async Task TryFetchAsync_FetchesTheFrameForTheMomentMotionWasDetected()
    {
        using var box = new StubBox();
        var frameAt = box.SeedSegments(count: 4);
        box.WriteStub();

        var pick = await box.Service.TryFetchAsync(
            box.CameraId, frameAt, lookbackSeconds: 6, TestContext.Current.CancellationToken);

        Assert.NotNull(pick);
        Assert.Equal(frameAt, pick.ChosenAt);
        Assert.NotEmpty(pick.ImageBytes);

        // The offset handed to ffmpeg is measured from the start of the contiguous run, not from
        // the requested instant - getting that wrong is how you fetch a frame from the wrong second.
        var seek = box.LastSeekOffset();
        Assert.NotNull(seek);
        Assert.True(seek >= 0, $"seek offset should be into the run, got {seek}");
    }

    // The instant is known exactly, so lookback is only a sanity bound - it stops a clock step or a
    // reconnecting camera presenting footage from minutes ago as if it were now.
    [Fact]
    public async Task TryFetchAsync_RefusesAFrameOlderThanTheLookback()
    {
        using var box = new StubBox();
        box.SeedSegments(count: 4);
        box.WriteStub();

        var pick = await box.Service.TryFetchAsync(
            box.CameraId, DateTimeOffset.UtcNow.AddMinutes(-5), lookbackSeconds: 6,
            TestContext.Current.CancellationToken);

        Assert.Null(pick);
    }

    [Fact]
    public async Task TryFetchAsync_WhenTheBufferIsEmpty_ReturnsNull()
    {
        using var box = new StubBox();
        box.WriteStub();

        var pick = await box.Service.TryFetchAsync(
            box.CameraId, DateTimeOffset.UtcNow, 6, TestContext.Current.CancellationToken);

        Assert.Null(pick);
    }

    // The caller falls back to the live snapshot on null. Throwing would lose the event entirely,
    // trading a cosmetic problem for a real one.
    [Fact]
    public async Task TryFetchAsync_WhenFfmpegFails_ReturnsNullRatherThanThrowing()
    {
        using var box = new StubBox();
        var frameAt = box.SeedSegments(count: 4);
        box.WriteFailingStub();

        var pick = await box.Service.TryFetchAsync(
            box.CameraId, frameAt, 6, TestContext.Current.CancellationToken);

        Assert.Null(pick);
    }

    [Fact]
    public async Task TryExtractAtAsync_WhenTheBufferIsEmpty_ReturnsNull()
    {
        using var box = new StubBox();
        box.WriteStub();

        Assert.Null(await box.Service.TryExtractAtAsync(
            box.CameraId, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
    }

    // --- helpers ------------------------------------------------------------------------------

    // Stands in for ffmpeg the same way HlsStreamManagerTests does: a shell script pointed at by
    // FfmpegPath, which records the arguments it was given and writes a JPEG where it was told to.
    private sealed class StubBox : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"rewind-test-{Guid.NewGuid():N}");
        private readonly string _stubPath;
        private readonly string _argsLog;
        private readonly string _jpegPath;

        public Guid CameraId { get; } = Guid.NewGuid();
        public TriggerRewindService Service { get; }
        public ClipRecorder Clips { get; }

        public StubBox()
        {
            Directory.CreateDirectory(_root);
            _stubPath = Path.Combine(_root, "fake-ffmpeg.sh");
            _argsLog = Path.Combine(_root, "args.log");
            _jpegPath = Path.Combine(_root, "frame.jpg");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CamerasDataDirectory"] = _root,
                    ["FfmpegPath"] = _stubPath
                })
                .Build();

            Clips = new ClipRecorder(configuration, new StubEnvironment(_root), NullLogger<ClipRecorder>.Instance);
            Service = new TriggerRewindService(Clips, configuration, NullLogger<TriggerRewindService>.Instance);
        }

        // Writes back-to-back 2s segments ending now, and returns an instant inside the run. The
        // newest file is excluded by the service as mid-write, exactly as in production, so the
        // returned instant sits inside the second-newest.
        public DateTimeOffset SeedSegments(int count)
        {
            var dir = Clips.BufferDirectory(CameraId);
            var newest = DateTime.Now;

            for (var i = 0; i < count; i++)
            {
                var startsAt = newest.AddSeconds(-ClipRecorder.SegmentSeconds * i);
                File.WriteAllText(Path.Combine(dir, $"seg_{startsAt:yyyyMMdd-HHmmss}.ts"), "x");
            }

            return new DateTimeOffset(newest.AddSeconds(-ClipRecorder.SegmentSeconds));
        }

        public void WriteStub()
        {
            File.WriteAllBytes(_jpegPath, JpegBytes());
            File.WriteAllText(_stubPath, $"""
                #!/bin/sh
                echo "$@" >> "{_argsLog}"
                for LAST in "$@"; do :; done
                cp "{_jpegPath}" "$LAST"
                exit 0
                """);
            MakeExecutable();
        }

        public void WriteFailingStub()
        {
            File.WriteAllText(_stubPath, """
                #!/bin/sh
                echo "stub failure" >&2
                exit 1
                """);
            MakeExecutable();
        }

        // The value ffmpeg was given for -ss on its most recent invocation.
        public double? LastSeekOffset()
        {
            if (!File.Exists(_argsLog)) return null;
            var parts = File.ReadAllLines(_argsLog)[^1].Split(' ');
            var index = Array.IndexOf(parts, "-ss");
            return index >= 0 && double.TryParse(
                parts[index + 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                ? seconds
                : null;
        }

        private void MakeExecutable()
        {
            using var chmod = System.Diagnostics.Process.Start("/bin/chmod", $"+x {_stubPath}")!;
            chmod.WaitForExit();
        }

        // A real JPEG, so the service's IsCompleteJpeg check sees what it expects.
        private static byte[] JpegBytes()
        {
            using var image = new Image<Rgb24>(2, 2);
            using var buffer = new MemoryStream();
            image.Save(buffer, new JpegEncoder());
            return buffer.ToArray();
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class StubEnvironment(string root) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Development";
    }
}
