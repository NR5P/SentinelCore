using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// A trigger while a clip is already recording extends that clip instead of starting its own.
//
// Measured against the arrival that prompted this: with a 5s cooldown, a 5s pre-roll and a 10s
// post-roll, triggers at +0s, +5s and +17s produced three clips covering 21:46:30-21:46:46,
// 21:46:35-21:46:53 and 21:46:47-21:47:05 - about 32 seconds of an arrival stored three times over,
// and three cards in the Feed that each read like a separate visit.
public sealed class ClipExtensionTests
{
    private const int Pre = 5;
    private const int Post = 10;
    private const int MaxSeconds = 90;

    private static readonly DateTimeOffset FirstTrigger = new(2026, 8, 10, 21, 46, 35, TimeSpan.Zero);

    [Fact]
    public void TheRealArrivalBecomesOneClipCoveringAllThreeTriggers()
    {
        var clip = NewClip(FirstTrigger);

        Assert.True(clip.TryExtend(Guid.NewGuid(), FirstTrigger.AddSeconds(5), Pre, Post, MaxSeconds));
        Assert.True(clip.TryExtend(Guid.NewGuid(), FirstTrigger.AddSeconds(17), Pre, Post, MaxSeconds));

        Assert.Equal(3, clip.TriggerCount);
        Assert.Equal(FirstTrigger.AddSeconds(-Pre), clip.From);
        Assert.Equal(FirstTrigger.AddSeconds(27), clip.To);

        // 32s of one arrival, against the 52.8s of overlapping video the three separate clips came to.
        Assert.Equal(32, (clip.To - clip.From).TotalSeconds);
    }

    [Fact]
    public void TheAnchorNamesTheClipHoweverFarTheEndMoves()
    {
        var clip = NewClip(FirstTrigger);
        clip.TryExtend(Guid.NewGuid(), FirstTrigger.AddSeconds(9), Pre, Post, MaxSeconds);

        Assert.Equal(FirstTrigger, clip.AnchoredAt);
    }

    [Fact]
    public void ATriggerPastTheEndOfTheWindowStartsItsOwnClip()
    {
        // Extending to reach it would leave a gap in the middle, and a clip with a hole in it is
        // worse than two clips.
        var clip = NewClip(FirstTrigger);

        // Its pre-roll would start at +11s, a full second after the window's +10s end.
        Assert.False(clip.TryExtend(Guid.NewGuid(), FirstTrigger.AddSeconds(Post + Pre + 1), Pre, Post, MaxSeconds));
        Assert.Equal(1, clip.TriggerCount);
        Assert.Equal(FirstTrigger.AddSeconds(Post), clip.To);
    }

    [Fact]
    public void AnEarlierTriggerNeverPullsTheEndBackwards()
    {
        var clip = NewClip(FirstTrigger);
        clip.TryExtend(Guid.NewGuid(), FirstTrigger.AddSeconds(8), Pre, Post, MaxSeconds);
        var end = clip.To;

        // A companion capture borrows the triggering camera's moment, so one can legitimately
        // arrive stamped earlier than a trigger already folded in.
        Assert.True(clip.TryExtend(Guid.NewGuid(), FirstTrigger.AddSeconds(2), Pre, Post, MaxSeconds));
        Assert.Equal(end, clip.To);
        Assert.Equal(3, clip.TriggerCount);
    }

    [Fact]
    public void ExtensionStopsAtTheCeiling()
    {
        // Motion at a gate can keep firing for minutes. Without this the clip would become an
        // open-ended recording whose start the rolling buffer could no longer hold.
        var clip = NewClip(FirstTrigger);

        var at = FirstTrigger;
        for (var i = 0; i < 20; i++)
        {
            at = at.AddSeconds(5);
            if (!clip.TryExtend(Guid.NewGuid(), at, Pre, Post, MaxSeconds)) break;
        }

        Assert.True((clip.To - clip.From).TotalSeconds <= MaxSeconds);

        // And the trigger that was turned away opens a fresh clip rather than being dropped.
        Assert.False(clip.TryExtend(Guid.NewGuid(), clip.To.AddSeconds(Pre + 1), Pre, Post, MaxSeconds));
    }

    [Fact]
    public void ASealedClipTakesNoMoreTriggers()
    {
        // Once the build has stopped waiting and is about to read the buffer, the window is what
        // gets concatenated and must not move.
        var clip = NewClip(FirstTrigger);
        clip.Seal();

        Assert.False(clip.TryExtend(Guid.NewGuid(), FirstTrigger.AddSeconds(2), Pre, Post, MaxSeconds));
        Assert.Equal(1, clip.TriggerCount);
    }

    [Fact]
    public void EveryTriggerInTheWindowIsCarriedSoTheyCanAllPointAtTheOneFile()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();

        var clip = NewClip(FirstTrigger, first);
        clip.TryExtend(second, FirstTrigger.AddSeconds(5), Pre, Post, MaxSeconds);
        clip.TryExtend(third, FirstTrigger.AddSeconds(17), Pre, Post, MaxSeconds);

        Assert.Equal([first, second, third], clip.TriggerIds);
    }

    // --- Buffer retention ----------------------------------------------------------------

    [Fact]
    public void AnOpenClipKeepsItsOwnPreRollOnDisk()
    {
        // The failure this guards: pruning to pre + post while a clip is still being extended
        // deletes the front of the very footage the extension exists to keep - and silently, since
        // segments covering the tail still exist and the "no buffered video" warning never fires.
        using var temp = new TempDirectory();
        var recorder = NewRecorder(temp.Path);
        var cameraId = Guid.NewGuid();

        var now = DateTimeOffset.Now;
        var openedAt = now.AddSeconds(-40);

        var oldest = WriteSegment(recorder, cameraId, openedAt.AddSeconds(-Pre));
        var middle = WriteSegment(recorder, cameraId, now.AddSeconds(-20));
        var newest = WriteSegment(recorder, cameraId, now.AddSeconds(-2));

        recorder.PruneBuffer(cameraId, Pre, Post, openClipFrom: openedAt.AddSeconds(-Pre));

        Assert.True(File.Exists(oldest));
        Assert.True(File.Exists(middle));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void WithNoOpenClipTheBufferStillAgesOut()
    {
        // The extension must not turn the rolling buffer into an unbounded one.
        using var temp = new TempDirectory();
        var recorder = NewRecorder(temp.Path);
        var cameraId = Guid.NewGuid();

        var now = DateTimeOffset.Now;
        var ancient = WriteSegment(recorder, cameraId, now.AddSeconds(-600));
        var recent = WriteSegment(recorder, cameraId, now.AddSeconds(-2));

        recorder.PruneBuffer(cameraId, Pre, Post, openClipFrom: null);

        Assert.False(File.Exists(ancient));
        Assert.True(File.Exists(recent));
    }

    // --- Helpers -------------------------------------------------------------------------

    private static TriggerZoneMonitor.OpenClip NewClip(DateTimeOffset at, Guid? triggerId = null) =>
        new(at, at.AddSeconds(-Pre), at.AddSeconds(Post), triggerId ?? Guid.NewGuid());

    private static ClipRecorder NewRecorder(string root)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["CamerasDataDirectory"] = root })
            .Build();

        return new ClipRecorder(configuration, new FakeEnvironment(root), NullLogger<ClipRecorder>.Instance);
    }

    // ffmpeg writes these with -strftime, so the name is the segment's start time in local time -
    // which is what ClipRecorder reads back out of it.
    private static string WriteSegment(ClipRecorder recorder, Guid cameraId, DateTimeOffset startsAt)
    {
        var path = Path.Combine(recorder.BufferDirectory(cameraId), $"seg_{startsAt.LocalDateTime:yyyyMMdd-HHmmss}.ts");
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private sealed class FakeEnvironment(string root) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Development";
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"clip-extension-{Guid.NewGuid():N}");

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { TempDirectories.Delete(Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
