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

    // --- the lookup ---------------------------------------------------------------------------

    // The newest file is never selectable - ffmpeg is still writing it - but it is what gives the
    // segment before it a real measured end rather than a nominal one.
    [Fact]
    public void IndexOfSegmentContaining_PicksTheSegmentHoldingTheInstant()
    {
        var ordered = new[] { Seg(0), Seg(2), Seg(4), Seg(6) };

        Assert.Equal(1, TriggerRewind.IndexOfSegmentContaining(ordered, Base.AddSeconds(3.5)));
        Assert.Equal(0, TriggerRewind.IndexOfSegmentContaining(ordered, Base.AddSeconds(0)));
        Assert.Equal(2, TriggerRewind.IndexOfSegmentContaining(ordered, Base.AddSeconds(5.9)));
    }

    [Fact]
    public void IndexOfSegmentContaining_RefusesTheSegmentFfmpegIsStillWriting() =>
        Assert.Equal(-1, TriggerRewind.IndexOfSegmentContaining(
            [Seg(0), Seg(2), Seg(4)], Base.AddSeconds(4.5)));

    // A frame from the wrong side of an outage is worse than no frame: the caller falls back to the
    // live snapshot, which at least doesn't claim to be the moment that fired.
    [Fact]
    public void IndexOfSegmentContaining_RefusesAnInstantInsideAHole() =>
        Assert.Equal(-1, TriggerRewind.IndexOfSegmentContaining(
            [Seg(0), Seg(2), Seg(60), Seg(62)], Base.AddSeconds(30)));

    [Fact]
    public void IndexOfSegmentContaining_WhenTheBufferIsEmptyOrHasOnlyTheOpenFile_FindsNothing()
    {
        Assert.Equal(-1, TriggerRewind.IndexOfSegmentContaining([], Base));
        Assert.Equal(-1, TriggerRewind.IndexOfSegmentContaining([Seg(0)], Base));
    }

    [Fact]
    public void OffsetInto_IsMeasuredFromItsOwnSegment_SoAHoleElsewhereCannotShiftIt()
    {
        var ordered = new[] { Seg(0), Seg(10), Seg(12), Seg(14) };

        Assert.Equal(1.5, TriggerRewind.OffsetInto(ordered, 2, Base.AddSeconds(13.5), closedAt: null), 3);
        Assert.Equal(0, TriggerRewind.OffsetInto(ordered, 1, Base.AddSeconds(10), closedAt: null), 3);
    }

    // --- sub-second segment boundaries ----------------------------------------------------------

    // strftime has no sub-second field, so a segment's name is its true start truncated to the whole
    // second. The muxer closes a segment when it opens the next, so the previous file's close time
    // supplies the fraction the name dropped. Each pair here is (segment, when that segment closed),
    // in seconds from Base - so a segment named :02 that really opened at 2.9s appears as the close
    // of the segment named :00.
    private static Func<string, DateTimeOffset> ClosedAt(params (int Second, double Closed)[] closes)
    {
        var byPath = closes.ToDictionary(c => Seg(c.Second).Path, c => Base.AddSeconds(c.Closed));
        return path => byPath.TryGetValue(path, out var at) ? at : DateTimeOffset.MinValue;
    }

    // A buffer of ~1.9s segments whose starts have drifted 0.9s past the second they are named for.
    private static Func<string, DateTimeOffset> DriftedByNinePercent() =>
        ClosedAt((0, 2.9), (2, 4.9), (4, 6.9));

    [Fact]
    public void OffsetInto_MeasuresFromWhereTheSegmentReallyOpened_NotTheWholeSecondItIsNamedAfter()
    {
        var ordered = new[] { Seg(0), Seg(2), Seg(4), Seg(6) };

        // Segment :02 really opened at 2.9s, so an instant at 3.5s is 0.6s into it - not the 1.5s
        // the filename implies. Seeking to 1.5s would land most of a second late, and past the end
        // of a ~1.94s segment it lands nowhere at all.
        Assert.Equal(
            0.6,
            TriggerRewind.OffsetInto(ordered, 1, Base.AddSeconds(3.5), DriftedByNinePercent()),
            3);
    }

    [Fact]
    public void IndexOfSegmentContaining_PutsAnInstantInsideTheTruncatedFraction_InThePrecedingSegment()
    {
        var ordered = new[] { Seg(0), Seg(2), Seg(4), Seg(6) };
        var closed = DriftedByNinePercent();

        // 2.5s falls under segment :02's name, but that segment did not open until 2.9s - so 2.5s
        // is still in the :00 file, and that is where the frame for it lives.
        Assert.Equal(0, TriggerRewind.IndexOfSegmentContaining(ordered, Base.AddSeconds(2.5), closed));

        // Past the refined start it belongs to segment :02 as usual.
        Assert.Equal(1, TriggerRewind.IndexOfSegmentContaining(ordered, Base.AddSeconds(3.5), closed));
    }

    // The refinement sharpens a boundary; it must never invent coverage across an outage.
    [Fact]
    public void IndexOfSegmentContaining_WithRefinedBoundaries_StillRefusesAnInstantInsideAHole() =>
        Assert.Equal(-1, TriggerRewind.IndexOfSegmentContaining(
            [Seg(0), Seg(2), Seg(60), Seg(62)],
            Base.AddSeconds(30),
            ClosedAt((0, 2.9), (2, 3.9), (60, 62.9))));

    // A close time that does not truncate back to the name it is supposed to belong to is not a
    // measurement of that segment's start - it is a stale file, a hole, or a filesystem that does
    // not keep the timestamp. The nominal boundary stands.
    [Fact]
    public void RefinedStart_IgnoresACloseTimeThatDoesNotTruncateToTheSegmentsOwnName()
    {
        var ordered = new[] { Seg(0), Seg(2), Seg(4) };

        Assert.Equal(Base.AddSeconds(2), TriggerRewind.RefinedStart(ordered, 1, ClosedAt((0, 0.5))));
        Assert.Equal(Base.AddSeconds(2), TriggerRewind.RefinedStart(ordered, 1, ClosedAt((0, 3.5))));
        Assert.Equal(Base.AddSeconds(2), TriggerRewind.RefinedStart(ordered, 1, closedAt: null));

        // The oldest segment has no predecessor to measure against.
        Assert.Equal(Base, TriggerRewind.RefinedStart(ordered, 0, DriftedByNinePercent()));
    }

    // --- overshooting the end of a segment ------------------------------------------------------

    // A segment's refined span is measured between muxer closes and runs a little longer than the
    // media in the file (~1.99s of span against ~1.954s of video), so an instant in that sliver is
    // inside the segment by the boundary math and past its last frame on disk. The frame that
    // actually covers it opens the next segment.
    [Fact]
    public void FallForwardIndex_OffersTheNextSegment_ForAnInstantPastThisOnesLastFrame()
    {
        var ordered = new[] { Seg(0), Seg(2), Seg(4), Seg(6) };

        Assert.Equal(2, TriggerRewind.FallForwardIndex(ordered, 1, Base.AddSeconds(3.99)));
        Assert.Equal(0.01, TriggerRewind.GapToNextStart(ordered, 1, Base.AddSeconds(3.99)), 3);
    }

    // The whole point of the -1 return. Standing in a frame from the far side of an outage would be
    // presenting a different moment as the trigger, which is the failure the live-snapshot fallback
    // at least does not lie about.
    [Fact]
    public void FallForwardIndex_RefusesToJumpAHole() =>
        Assert.Equal(-1, TriggerRewind.FallForwardIndex(
            [Seg(0), Seg(2), Seg(60), Seg(62)], 1, Base.AddSeconds(3.99)));

    // Nothing adjoins the newest file, so there is nothing to fall forward to.
    [Fact]
    public void FallForwardIndex_AtTheEndOfTheBuffer_HasNowhereToGo() =>
        Assert.Equal(-1, TriggerRewind.FallForwardIndex(
            [Seg(0), Seg(2)], 1, Base.AddSeconds(3.99)));

    // In an intact buffer every instant is within a segment-length of the next start, so the hole
    // test never fires on the case it is not for.
    [Fact]
    public void FallForwardIndex_AcceptsEveryInstantInAnIntactSegment()
    {
        var ordered = new[] { Seg(0), Seg(2), Seg(4) };

        foreach (var offset in new[] { 2.0, 2.5, 3.0, 3.5, 3.999 })
            Assert.Equal(2, TriggerRewind.FallForwardIndex(ordered, 1, Base.AddSeconds(offset)));
    }

    // -ss must come AFTER -i. Input seeking on these segments doesn't merely lose precision, it
    // returns nothing at all: a segment is commonly one GOP with a single keyframe at its head, so
    // seeking past that keyframe finds no later one and ffmpeg exits 0 having written no file.
    // Measured on a real 2.017s segment, input seeking produced no frame from 1.2s onward.
    [Fact]
    public void BuildExtractArguments_SeeksAfterTheInput_AndTakesOneSegmentNotAConcatList()
    {
        var args = TriggerRewind.BuildExtractArguments("/tmp/seg_0.ts", 1.5, "/tmp/out.jpg");

        Assert.True(args.IndexOf("-ss") > args.IndexOf("-i"));
        Assert.Equal("/tmp/seg_0.ts", args[args.IndexOf("-i") + 1]);
        Assert.Equal("1.5", args[args.IndexOf("-ss") + 1]);
        Assert.Equal("1", args[args.IndexOf("-frames:v") + 1]);
        Assert.DoesNotContain("concat", args);
    }

    // mjpeg refuses a frame that is not full-range - "Non full-range YUV is non-standard" - and
    // then never opens the encoder, so ffmpeg exits 234 having written nothing and the trigger
    // falls back to a live snapshot taken seconds after the moment that mattered. Whether that
    // happens is decided by the range the decoded frame carries, which is the camera's SPS and not
    // anything this process controls, so it presents as ~1% of fetches failing for no local reason.
    //
    // A yuvj pixel format is full-range by definition, so naming it means the conversion always
    // happens and the refusal has nothing to fire on. It is inert otherwise: yuvj420p is what the
    // negotiation already picked on all 90 healthy fetches this was measured against, byte for byte.
    [Fact]
    public void BuildExtractArguments_PinsTheJpegToAFullRangePixelFormat()
    {
        var args = TriggerRewind.BuildExtractArguments("/tmp/seg_0.ts", 1.5, "/tmp/out.jpg");

        Assert.Equal("yuvj420p", args[args.IndexOf("-pix_fmt") + 1]);

        // An output option, so it has to sit after -i - in front it would be read as a demuxer
        // hint and leave the encoder negotiating exactly as freely as before.
        Assert.True(args.IndexOf("-pix_fmt") > args.IndexOf("-i"), "-pix_fmt must come after -i to reach the encoder");
    }

    // -threads is per-context and lands in whichever context it precedes, so one copy throttles
    // half the job: before -i it reaches the h264 decoder, after it the mjpeg encoder. Only the
    // decoder was capped for a long time, leaving the encoder on ffmpeg's one-thread-per-core
    // default - nine mjpeg contexts to write one JPEG on an eight-core box, on the machine whose
    // spare CPU keeps the detection loop live. User CPU over 20 runs: 10.12s to 8.57s.
    [Fact]
    public void BuildExtractArguments_CapsThreadsOnBothTheDecoderAndTheEncoder()
    {
        var args = TriggerRewind.BuildExtractArguments("/tmp/seg_0.ts", 1.5, "/tmp/out.jpg");

        var threadFlags = args.Select((a, i) => (a, i)).Where(x => x.a == "-threads").Select(x => x.i).ToList();
        Assert.Equal(2, threadFlags.Count);
        Assert.All(threadFlags, i => Assert.Equal("1", args[i + 1]));

        // One on each side of -i, which is the whole point - two in front would cap the decoder
        // twice and leave the encoder exactly as unbounded as it was.
        var input = args.IndexOf("-i");
        Assert.True(threadFlags[0] < input, "the decoder's -threads must come before -i");
        Assert.True(threadFlags[1] > input, "the encoder's -threads must come after -i");
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

        // The offset handed to ffmpeg is measured from the start of the one segment holding the
        // instant - getting that wrong is how you fetch a frame from the wrong second. The seeded
        // instant is that segment's own start, so it is zero rather than merely non-negative;
        // asserting the exact value is what would have caught an offset measured from the start of
        // the whole buffer instead.
        var seek = box.LastSeekOffset();
        Assert.NotNull(seek);
        Assert.Equal(0, seek.Value, 3);
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

    // The regression this pair exists for: 67 fetches in the 36 hours to 2026-08-12 asked for an
    // offset a few tens of milliseconds past the end of a ~1.95s file, ffmpeg wrote nothing, and
    // every one of them put a live snapshot - a photograph taken seconds later - on the event.
    [Fact]
    public async Task TryFetchAsync_WhenTheOffsetOvershootsTheSegment_UsesTheNextSegmentsFirstFrame()
    {
        using var box = new StubBox();
        var segmentStart = box.SeedSegments(count: 4);
        box.WriteEndOfSegmentStub();

        // Just short of the boundary: inside the segment by the buffer's arithmetic, past its last
        // frame in the file.
        var frameAt = segmentStart.AddSeconds(ClipRecorder.SegmentSeconds - 0.05);

        var pick = await box.Service.TryFetchAsync(
            box.CameraId, frameAt, lookbackSeconds: 6, TestContext.Current.CancellationToken);

        Assert.NotNull(pick);
        Assert.NotEmpty(pick.ImageBytes);

        // Two runs: the overshooting one, then the head of the next segment.
        Assert.Equal(2, box.InvocationCount());
        Assert.Equal(0, box.LastSeekOffset()!.Value, 3);

        // The instant it reports is still the moment motion was detected - the stand-in frame is
        // milliseconds away, and claiming a different time would corrupt the diagnostic.
        Assert.Equal(frameAt, pick.ChosenAt);
    }

    // Falling forward must not become a way to paper over a real gap in the buffer.
    [Fact]
    public async Task TryFetchAsync_WhenTheOvershootWouldHaveToCrossAHole_StillReturnsNull()
    {
        using var box = new StubBox();
        var segmentStart = box.SeedSegments(count: 4, holeBeforeNewest: true);
        box.WriteEndOfSegmentStub();

        var pick = await box.Service.TryFetchAsync(
            box.CameraId, segmentStart.AddSeconds(ClipRecorder.SegmentSeconds - 0.05),
            lookbackSeconds: 60, TestContext.Current.CancellationToken);

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

    // --- companion cameras (TriggerZoneMonitor.CompanionCaptureAt) ----------------------------
    //
    // Only one camera in a group actually detects the motion; the rest have nothing of their own to
    // date the event by, so they borrow the instant from the one that fired. Every established
    // system solves this by cross-triggering a RECORDING on the other cameras and leaning on their
    // own pre-event buffers to cover the past - ZoneMinder's linked monitors, Blue Iris's trigger
    // groups - and in both, the linked camera's footage is reported as landing seconds off the
    // trigger's. Having an exact instant and a buffer to look it up in is what makes that gap
    // closable here, and it is only closable for as long as the instant is used unchanged.

    private static RewindPick PickAt(DateTimeOffset chosenAt, double captureDelaySeconds) =>
        new([0xFF, 0xD8], captureDelaySeconds, chosenAt);

    // The regression this pins: CaptureDelaySeconds is how long the FETCH took, not how far back
    // the fetch reached, and subtracting it put every companion frame that far in front of the
    // moment. On this install that averaged 2.32s, which is a person's whole walk past a gate.
    [Fact]
    public void CompanionCaptureAt_IsTheFrameInstantItself_NotOffsetByHowLongTheFetchTook()
    {
        var pick = PickAt(Base, captureDelaySeconds: 2.32);

        Assert.Equal(Base, TriggerZoneMonitor.CompanionCaptureAt(pick, applyToCompanions: true));
    }

    // Same instant however slow the fetch was - the number must not reach the lookup at all.
    [Theory]
    [InlineData(0.64)]
    [InlineData(2.32)]
    [InlineData(22.86)]
    public void CompanionCaptureAt_IgnoresTheFetchDelayEntirely(double captureDelaySeconds)
    {
        var pick = PickAt(Base, captureDelaySeconds);

        Assert.Equal(Base, TriggerZoneMonitor.CompanionCaptureAt(pick, applyToCompanions: true));
    }

    [Fact]
    public void CompanionCaptureAt_WhenTheSettingIsOff_LeavesCompanionsOnTheLiveSnapshot() =>
        Assert.Null(TriggerZoneMonitor.CompanionCaptureAt(PickAt(Base, 2.0), applyToCompanions: false));

    // No pick means the triggering camera's own instant was never validated against the lookback -
    // that check lives in TryFetchAsync, and TryExtractAtAsync has no equivalent. A clock step
    // would otherwise let a companion pull footage from minutes ago and file it as now.
    [Fact]
    public void CompanionCaptureAt_WithoutAPick_ReturnsNullRatherThanTrustingTheTimestamp() =>
        Assert.Null(TriggerZoneMonitor.CompanionCaptureAt(null, applyToCompanions: true));

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

        // Long enough to be an outage rather than one long segment - see
        // ClipRecorder.MaxSegmentSpanSeconds.
        private const int HoleSeconds = 30;

        // Writes back-to-back 2s segments ending now, and returns the start of the newest one the
        // service will actually select. The newest file of all is excluded as mid-write, exactly as
        // in production, so that is the second-newest.
        //
        // holeBeforeNewest pushes the run back so the gap between it and the mid-write file is an
        // outage: the same shape, but with nothing legitimate to fall forward onto.
        public DateTimeOffset SeedSegments(int count, bool holeBeforeNewest = false)
        {
            var dir = Clips.BufferDirectory(CameraId);

            // Truncated to a whole second because that is all a segment's filename carries, so the
            // instant returned below lands exactly on a segment boundary. Seeding from a raw
            // DateTime.Now instead left the sub-second remainder as a phantom seek offset.
            var now = DateTime.Now;
            var newest = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
            var runEnd = newest.AddSeconds(-(holeBeforeNewest ? HoleSeconds : ClipRecorder.SegmentSeconds));

            File.WriteAllText(Path.Combine(dir, $"seg_{newest:yyyyMMdd-HHmmss}.ts"), "x");

            for (var i = 0; i < count - 1; i++)
            {
                var startsAt = runEnd.AddSeconds(-ClipRecorder.SegmentSeconds * i);
                File.WriteAllText(Path.Combine(dir, $"seg_{startsAt:yyyyMMdd-HHmmss}.ts"), "x");
            }

            return new DateTimeOffset(runEnd);
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

        // ffmpeg as it behaves at the end of a segment: a seek past the last frame writes no file
        // and still exits 0, so only a seek to the head of a file produces anything.
        public void WriteEndOfSegmentStub()
        {
            File.WriteAllBytes(_jpegPath, JpegBytes());
            File.WriteAllText(_stubPath, $"""
                #!/bin/sh
                echo "$@" >> "{_argsLog}"
                for LAST in "$@"; do :; done
                PREV=""
                SS=""
                for A in "$@"; do
                  if [ "$PREV" = "-ss" ]; then SS="$A"; fi
                  PREV="$A"
                done
                if [ "$SS" = "0" ]; then
                  cp "{_jpegPath}" "$LAST"
                fi
                exit 0
                """);
            MakeExecutable();
        }

        public int InvocationCount() => File.Exists(_argsLog) ? File.ReadAllLines(_argsLog).Length : 0;

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
            PosixShellStubs.SkipIfUnavailable();
            PosixShellStubs.MakeExecutable(_stubPath);
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
            try { TempDirectories.Delete(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
