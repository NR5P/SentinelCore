using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace SentinelCore.Tests;

// Measures the companion-capture change against real ffmpeg and a real segment buffer, rather than
// asserting that the new code does what the new code does.
//
// The buffer is video whose every frame states its own time: luminance rises linearly with the
// frame number, so a frame pulled out of it reads back as "this is the picture from t=12.4s"
// without OCR or any knowledge of how it was fetched. That turns "was the right moment fetched"
// into an arithmetic question with an answer in seconds.
//
// Skipped where there is no ffmpeg, so the suite still runs on a machine without one.
public sealed class CompanionRewindMeasurementTests(ITestOutputHelper output, CompanionRewindMeasurementTests.TimecodedBuffer box)
    : IClassFixture<CompanionRewindMeasurementTests.TimecodedBuffer>
{
    // The fetch delays actually recorded on this install over the week to 2026-08-25, across the
    // 1,708 triggers that carried one: the minimum, the two modal buckets, the mean, and the worst.
    public static TheoryData<double> ObservedFetchDelays() => [0.64, 1.5, 2.32, 3.0, 22.86];

    // --- does the new instant fetch the right frame -------------------------------------------

    [Theory]
    [MemberData(nameof(ObservedFetchDelays))]
    public async Task TheOldOffsetMissedTheMomentByTheFetchDelay_TheNewOneLandsOnIt(double fetchDelaySeconds)
    {
        if (!box.Available) Assert.Skip("needs a real ffmpeg on PATH");

        // Not on a segment boundary, and late enough that even the 22.86s delay stays inside the
        // buffer - so the old path is measured fetching the WRONG frame rather than failing
        // outright, which is the comparison worth making.
        const double momentSeconds = 25.4;
        var camera = box.MaterializeCamera(liveEdgeSeconds: 40);
        var moment = box.Base.AddSeconds(momentSeconds);

        var fresh = await box.FetchVideoSecondsAt(camera, moment);
        var old = await box.FetchVideoSecondsAt(camera, moment - TimeSpan.FromSeconds(fetchDelaySeconds));

        output.WriteLine(
            $"delay {fetchDelaySeconds,5:0.00}s | wanted t={momentSeconds:0.00}s | " +
            $"new -> t={Show(fresh)} (err {ShowErr(fresh, momentSeconds)}) | " +
            $"old -> t={Show(old)} (err {ShowErr(old, momentSeconds)})");

        // 10fps, so a fetch can only ever be as exact as one frame.
        Assert.NotNull(fresh);
        Assert.InRange(fresh!.Value, momentSeconds - FrameTolerance, momentSeconds + FrameTolerance);

        // And the old path was wrong by the whole fetch delay - the thing the fix removes.
        Assert.NotNull(old);
        Assert.InRange(
            momentSeconds - old!.Value, fetchDelaySeconds - FrameTolerance, fetchDelaySeconds + FrameTolerance);
    }

    // The same measurement driven through the code that actually decides, rather than through a
    // hand-computed instant.
    [Fact]
    public async Task CompanionCaptureAt_DrivesTheFetchToTheFrameTheTriggerImageCameFrom()
    {
        if (!box.Available) Assert.Skip("needs a real ffmpeg on PATH");

        const double momentSeconds = 20.7;
        var camera = box.MaterializeCamera(liveEdgeSeconds: 40);
        var moment = box.Base.AddSeconds(momentSeconds);

        // What TryFetchAsync hands back for the camera that did detect the motion: the frame is the
        // one captured at `moment`, and getting hold of it took 2.32s.
        var pick = new RewindPick([0xFF, 0xD8], CaptureDelaySeconds: 2.32, ChosenAt: moment);

        var at = TriggerZoneMonitor.CompanionCaptureAt(pick, applyToCompanions: true);
        Assert.NotNull(at);

        var companion = await box.FetchVideoSecondsAt(camera, at!.Value);
        output.WriteLine(
            $"trigger frame t={momentSeconds:0.00}s, fetch took {pick.CaptureDelaySeconds:0.00}s -> " +
            $"companion fetched t={Show(companion)} (err {ShowErr(companion, momentSeconds)})");

        Assert.NotNull(companion);
        Assert.InRange(companion!.Value, momentSeconds - FrameTolerance, momentSeconds + FrameTolerance);
    }

    // --- what the change costs ----------------------------------------------------------------

    // The one way this change can be a step backwards, measured rather than argued.
    //
    // Asking for the moment itself asks for something NEARER the live edge than asking for the
    // moment minus a couple of seconds did, and the segment at the live edge is the one ffmpeg is
    // still writing - which the lookup refuses, by design, because reading a half-written file
    // hands the decoder a truncated stream. So some companions that used to get a (wrong) frame now
    // get none and fall back to the live snapshot.
    //
    // Whether that happens turns on how far `moment` sits from the end of its own segment against
    // how long the triggering camera's fetch took, so this sweeps every position within a segment
    // against the real delay distribution and weights the result by how often each delay actually
    // occurs. The assertion is the comparison itself: mean error must improve by more than a
    // segment length, which no amount of fallback can fake.
    [Fact]
    public async Task AcrossTheRealDelayDistribution_TheNewInstantIsBetterEvenCountingItsFallbacks()
    {
        if (!box.Available) Assert.Skip("needs a real ffmpeg on PATH");

        // Delay buckets as observed, with the count of triggers in each. The 11 triggers beyond 4s
        // are left out: they all exceed a segment length, where both paths behave identically.
        (double Delay, int Weight)[] delays = [(0.5, 33), (1.5, 652), (2.5, 691), (3.5, 318)];

        // Where in its own segment the moment lands. This is what decides whether the segment
        // holding it has been closed by the time the companions are captured.
        var positions = Enumerable.Range(0, 10).Select(i => 20.0 + i * 0.2).ToList();

        double newErrorTotal = 0, oldErrorTotal = 0;
        int weightTotal = 0, newFellBack = 0, newExact = 0;

        foreach (var (delay, weight) in delays)
        {
            foreach (var moment in positions)
            {
                // The buffer as it stands when the companions are captured: everything up to the
                // moment plus however long the triggering camera's own fetch took.
                var camera = box.MaterializeCamera(moment + delay);
                var at = box.Base.AddSeconds(moment);

                var fresh = await box.FetchVideoSecondsAt(camera, at);
                var old = await box.FetchVideoSecondsAt(camera, at - TimeSpan.FromSeconds(delay));

                // A miss is not a free pass: it falls back to the live snapshot, which is the
                // camera's current view, so its error is how late the capture is happening.
                // TryReadLatestSnapshotAsync reads a file rewritten once a second, so the frame it
                // returns is up to a second older than that again - counted at the delay itself,
                // the optimistic end.
                var newError = fresh is { } f ? Math.Abs(f - moment) : delay;
                var oldError = old is { } o ? Math.Abs(o - moment) : delay;

                if (fresh is null) newFellBack += weight; else newExact += weight;

                newErrorTotal += newError * weight;
                oldErrorTotal += oldError * weight;
                weightTotal += weight;
            }
        }

        var newMean = newErrorTotal / weightTotal;
        var oldMean = oldErrorTotal / weightTotal;
        var fallbackRate = (double)newFellBack / (newFellBack + newExact);

        output.WriteLine($"weighted over {weightTotal} trigger-equivalents, {positions.Count} positions per delay");
        output.WriteLine($"  new: mean error {newMean:0.000}s, exact frame for {1 - fallbackRate:P1}, fell back {fallbackRate:P1}");
        output.WriteLine($"  old: mean error {oldMean:0.000}s, exact frame for 0.0%");
        output.WriteLine($"  improvement: {oldMean - newMean:0.000}s ({(oldMean - newMean) / oldMean:P1} less error)");

        // The old path never once fetched the right frame; the new one usually does.
        Assert.True(newMean < oldMean - ClipRecorder.SegmentSeconds,
            $"expected the mean error to fall by more than a segment length, got {oldMean:0.000}s -> {newMean:0.000}s");
        Assert.True(fallbackRate < 0.25, $"fallback rate {fallbackRate:P1} is higher than expected");
    }

    // --- the instrument -----------------------------------------------------------------------

    // If reading a frame's own timestamp back out of it were unreliable, every number above would
    // be meaningless.
    [Fact]
    public async Task TheTimecodedBufferReadsBackTheTimeItWasAskedFor()
    {
        if (!box.Available) Assert.Skip("needs a real ffmpeg on PATH");
        var camera = box.MaterializeCamera(liveEdgeSeconds: 40);

        foreach (var t in new[] { 0.5, 2.5, 6.0, 9.5, 14.2, 21.9, 27.0, 35.4, 37.5 })
        {
            var read = await box.FetchVideoSecondsAt(camera, box.Base.AddSeconds(t));
            output.WriteLine($"asked t={t:0.00}s -> read t={Show(read)}");
            Assert.NotNull(read);
            Assert.InRange(read!.Value, t - FrameTolerance, t + FrameTolerance);
        }
    }

    private const double FrameTolerance = 0.15;

    private static string Show(double? seconds) => seconds is { } s ? $"{s:0.00}s" : "none";

    private static string ShowErr(double? seconds, double wanted) =>
        seconds is { } s ? $"{s - wanted:+0.00;-0.00;0.00}s" : "n/a";

    private static bool HasFfmpeg()
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            probe!.WaitForExit();
            return probe.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }

    // Video in which every frame states its own time, cut into the same 2s mpegts segments
    // TriggerZoneMonitor writes, named the way ffmpeg's -strftime names them.
    //
    // Built once for the whole class rather than once per test. That is not only about speed: an
    // x264 encode per test method, running alongside the rest of the suite, put enough load on the
    // box to make DetectionFrameSlotTests miss its 20ms window and fail - a test failing because of
    // what another test was doing to the CPU. One encode, capped to a single thread, keeps this
    // measurement from disturbing what it shares a run with.
    public sealed class TimecodedBuffer : IAsyncLifetime
    {
        private const int Fps = 10;
        private const int DurationSeconds = 40;

        // Luma per frame index, and the level frame zero starts at.
        //
        // Both ends matter. The decoder hands back limited-range luma (16-235) and the JPEG encoder
        // writes full range, so anything stored below 16 clamps to black and anything above 235
        // clamps to white - and a clamped frame reads back as a time it is not, silently. Starting
        // at 20 and rising 0.53 a frame puts all 400 frames inside 20-232, clear of both. Found the
        // hard way: a ramp from zero made every instant before t=2.7s read as t=2.7s, which showed
        // up as the 22.86s delay case missing by 0.17s while every other case was exact.
        private const double LumaPerFrame = 0.53;
        private const double LumaAtFrameZero = 20;

        private readonly string _root = Path.Combine(Path.GetTempPath(), $"companion-measure-{Guid.NewGuid():N}");
        private readonly string _master;
        private readonly string _source;

        private double _slope;
        private double _intercept;

        public DateTimeOffset Base { get; private set; }
        public TriggerRewindService Service { get; }
        public ClipRecorder Clips { get; }
        public bool Available { get; private set; }

        public TimecodedBuffer()
        {
            Directory.CreateDirectory(_root);
            _master = Path.Combine(_root, "master");
            Directory.CreateDirectory(_master);
            _source = Path.Combine(_root, "src.mp4");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CamerasDataDirectory"] = _root,
                    ["FfmpegPath"] = "ffmpeg"
                })
                .Build();

            Clips = new ClipRecorder(configuration, new MeasurementEnvironment(_root), NullLogger<ClipRecorder>.Instance);
            Service = new TriggerRewindService(Clips, configuration, NullLogger<TriggerRewindService>.Instance);
        }

        public async ValueTask InitializeAsync()
        {
            Available = HasFfmpeg();
            if (!Available) return;

            // geq paints each frame a flat grey rising with the frame index, so the picture is its
            // own timestamp. A fixed GOP of one segment length keeps -c copy able to cut where it is
            // asked to, which is what the cameras give production.
            await RunAsync("ffmpeg",
            [
                "-v", "error",
                "-f", "lavfi", "-i", $"color=c=black:s=320x180:r={Fps}:d={DurationSeconds}",
                "-vf", $"geq=lum='{LumaAtFrameZero.ToString(CultureInfo.InvariantCulture)}" +
                       $"+N*{LumaPerFrame.ToString(CultureInfo.InvariantCulture)}':cb=128:cr=128",
                "-c:v", "libx264",
                "-g", (Fps * ClipRecorder.SegmentSeconds).ToString(),
                "-keyint_min", (Fps * ClipRecorder.SegmentSeconds).ToString(),
                "-sc_threshold", "0",
                "-pix_fmt", "yuv420p",
                // Capped for the same reason the rewind caps its own: this must never be the reason
                // something else on the box misses a deadline.
                "-threads", "1",
                "-y", _source
            ]);

            // Byte-for-byte the segment output TriggerZoneMonitor hangs off the detection ffmpeg.
            await RunAsync("ffmpeg",
            [
                "-v", "error",
                "-i", _source,
                "-c", "copy",
                "-f", "segment",
                "-segment_time", ClipRecorder.SegmentSeconds.ToString(),
                "-segment_format", "mpegts",
                "-reset_timestamps", "1",
                "-y", Path.Combine(_master, "raw_%03d.ts")
            ]);

            // A whole second, because that is all -strftime can put in a name.
            var now = DateTime.Now;
            Base = new DateTimeOffset(now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond)).AddSeconds(-DurationSeconds));

            var raw = Directory.GetFiles(_master, "raw_*.ts").OrderBy(p => p).ToList();
            Assert.Equal(DurationSeconds / ClipRecorder.SegmentSeconds, raw.Count);

            for (var i = 0; i < raw.Count; i++)
            {
                var startsAt = Base.AddSeconds(i * ClipRecorder.SegmentSeconds).LocalDateTime;
                File.Move(raw[i], Path.Combine(_master, $"seg_{startsAt:yyyyMMdd-HHmmss}.ts"));
            }

            await CalibrateAsync();
        }

        // A camera whose buffer holds exactly the segments that had been STARTED by the given
        // moment - so the newest of them is the one ffmpeg would still be writing, which is what
        // makes the near-live-edge behaviour real rather than assumed.
        public Guid MaterializeCamera(double liveEdgeSeconds)
        {
            var cameraId = Guid.NewGuid();
            var dir = Clips.BufferDirectory(cameraId);

            var started = (int)Math.Floor(liveEdgeSeconds / ClipRecorder.SegmentSeconds) + 1;
            foreach (var segment in Directory.GetFiles(_master, "seg_*.ts").OrderBy(p => p).Take(started))
                File.Copy(segment, Path.Combine(dir, Path.GetFileName(segment)));

            return cameraId;
        }

        // Frames at known times, read straight out of the source, fix the luma-to-time line - so the
        // readings are anchored to this machine's own ffmpeg rather than to an assumed colour range
        // conversion (the decoder hands back limited-range luma and the JPEG encoder writes full,
        // which is a real ~1.16x scaling this absorbs without having to model it).
        //
        // Least squares over the whole span rather than a line through two endpoints: luma is
        // quantised to whole numbers, so any single probe carries up to half a level of rounding,
        // which through the inverse is nearly a frame. Two such errors at the ends tilt the line and
        // the tilt then grows with t - measured at -0.16s by 25s, which is enough to swamp the
        // one-frame precision these tests claim. Averaging the noise across nine probes removes it.
        private async Task CalibrateAsync()
        {
            var probes = new List<(double Time, double Luma)>();
            for (var t = 1.0; t <= DurationSeconds - 2; t += 3)
                probes.Add((t, await SourceLumaAtAsync(t)));

            var meanLuma = probes.Average(p => p.Luma);
            var meanTime = probes.Average(p => p.Time);

            _slope = probes.Sum(p => (p.Luma - meanLuma) * (p.Time - meanTime))
                / probes.Sum(p => (p.Luma - meanLuma) * (p.Luma - meanLuma));
            _intercept = meanTime - _slope * meanLuma;
        }

        private async Task<double> SourceLumaAtAsync(double atSeconds)
        {
            var jpeg = Path.Combine(_root, $"cal-{atSeconds.ToString("0.00", CultureInfo.InvariantCulture)}.jpg");
            await RunAsync("ffmpeg",
            [
                "-v", "error",
                "-i", _source,
                "-ss", atSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-frames:v", "1", "-q:v", "2", "-pix_fmt", "yuvj420p",
                "-y", jpeg
            ]);
            return MeanLuma(await File.ReadAllBytesAsync(jpeg));
        }

        // Fetches through the real TriggerRewindService - the same lookup, the same ffmpeg
        // arguments, the same buffer - and returns what time the returned picture says it is, or
        // null where the service refused to produce one.
        public async Task<double?> FetchVideoSecondsAt(Guid cameraId, DateTimeOffset at)
        {
            var bytes = await Service.TryExtractAtAsync(cameraId, at, TestContext.Current.CancellationToken);
            return bytes is null ? null : _slope * MeanLuma(bytes) + _intercept;
        }

        private static double MeanLuma(byte[] jpeg)
        {
            using var image = Image.Load<L8>(jpeg);

            double total = 0;
            var count = 0;
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    foreach (var pixel in accessor.GetRowSpan(y))
                    {
                        total += pixel.PackedValue;
                        count++;
                    }
                }
            });

            return total / count;
        }

        private static async Task RunAsync(string file, string[] arguments)
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in arguments) psi.ArgumentList.Add(argument);

            using var process = Process.Start(psi)!;
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{file} exited {process.ExitCode}: {stderr}");
        }

        public ValueTask DisposeAsync()
        {
            try { TempDirectories.Delete(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MeasurementEnvironment(string root) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Development";
    }
}
