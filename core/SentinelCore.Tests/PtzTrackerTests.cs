using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SentinelCore;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The control law and the class table. Both are pure, and both are where this feature is either
// usable or a camera that hunts back and forth around its subject.
public class PtzTrackerTests
{
    private static Camera PtzCamera() => new(
        Guid.NewGuid(), "Ptz camera", "rtsp://cam/main", "user", "secret", IncludeAudio: false,
        PtzEnabled: true, OnvifPtzServiceUrl: "http://cam:8000/onvif/ptz_service", OnvifProfileToken: "000",
        IsBrowserPublished: false, DateTimeOffset.UtcNow);

    // Only the bookkeeping - start/abort/rest - is exercised here; nothing in these cases lets the
    // loop reach the detector or the camera, which is why the stubs can be this thin.
    private static PtzTracker BuildTracker(PtzTrackingOptions options) =>
        new(new UnusedDetector(),
            new OnvifClient(new HttpClient()),
            new PtzHomeService(new OnvifClient(new HttpClient()), new PtzActivityTracker(),
                NullLogger<PtzHomeService>.Instance),
            new PtzActivityTracker(),
            new ConfigurationBuilder().Build(),
            new StubEnvironment(),
            new StaticMonitor(options),
            NullLogger<PtzTracker>.Instance);

    private sealed class StaticMonitor(PtzTrackingOptions value) : IOptionsMonitor<PtzTrackingOptions>
    {
        public PtzTrackingOptions CurrentValue => value;
        public PtzTrackingOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<PtzTrackingOptions, string?> listener) => null;
    }

    private sealed class UnusedDetector : IFastLocalDetector
    {
        public Task<IReadOnlyList<FastLocalDetection>> DetectAsync(byte[] imageBytes, CropRegion? crop, CancellationToken cancellationToken) =>
            throw new NotSupportedException("these cases never reach the detector");

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public string ModelName => "stub";
    }

    private sealed class StubEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Production";
    }

    private static readonly PtzTrackingOptions Settings = new() { Deadzone = 0.12, Speed = 0.35 };

    private static FastLocalDetection At(double x, double y) => new(ClassId: 1, Score: 0.9, x, y, 0.1, 0.2);

    [Fact]
    public void ASubjectNearTheCentreIsLeftAlone()
    {
        // The deadzone is the whole reason this doesn't oscillate. The loop is a frame, then
        // inference, then a command over the network - the better part of a second - so correcting
        // small errors means correcting them long after they stopped being true.
        Assert.Null(PtzTracker.Correction(At(0.5, 0.5), Settings));
        Assert.Null(PtzTracker.Correction(At(0.5 + 0.11, 0.5 - 0.11), Settings));
    }

    [Fact]
    public void CorrectionsStartGentlyAtTheEdgeOfTheDeadzoneRatherThanJumpingToFullSpeed()
    {
        var justOutside = PtzTracker.Correction(At(0.5 + 0.13, 0.5), Settings);
        Assert.NotNull(justOutside);
        Assert.InRange(justOutside!.Value.Pan, 0.0001, Settings.Speed * 0.1);

        // ...and reach full speed only when the subject is at the frame edge.
        var atEdge = PtzTracker.Correction(At(1.0, 0.5), Settings);
        Assert.Equal(Settings.Speed, atEdge!.Value.Pan, precision: 6);
    }

    [Fact]
    public void ItPansTowardsTheSubjectAndTiltsInTheOppositeSenseToImageCoordinates()
    {
        var right = PtzTracker.Correction(At(0.95, 0.5), Settings)!.Value;
        Assert.True(right.Pan > 0, "a subject on the right should pan right");

        var left = PtzTracker.Correction(At(0.05, 0.5), Settings)!.Value;
        Assert.True(left.Pan < 0, "a subject on the left should pan left");

        // Image Y grows downward, ONVIF tilt grows upward. Getting this backwards drives the camera
        // away from the subject, which then leaves the frame, which ends the track - so the symptom
        // is "tracking never works" rather than anything pointing at the sign.
        var low = PtzTracker.Correction(At(0.5, 0.95), Settings)!.Value;
        Assert.True(low.Tilt < 0, "a subject low in frame should tilt down");

        var high = PtzTracker.Correction(At(0.5, 0.05), Settings)!.Value;
        Assert.True(high.Tilt > 0, "a subject high in frame should tilt up");
    }

    [Fact]
    public void CorrectionsNeverExceedTheConfiguredSpeed()
    {
        // A detection can sit outside the frame once the box is un-letterboxed, and an unclamped
        // proportional term would hand ONVIF a velocity above 1.0, which cameras reject outright.
        foreach (var (x, y) in new[] { (2.0, 2.0), (-1.5, -1.5), (0.0, 1.0) })
        {
            var correction = PtzTracker.Correction(At(x, y), Settings);
            if (correction is not var (pan, tilt)) continue;
            Assert.InRange(Math.Abs(pan), 0, Settings.Speed);
            Assert.InRange(Math.Abs(tilt), 0, Settings.Speed);
        }
    }

    [Fact]
    public void ARestPeriodStopsTheCameraPingPongingBetweenTracksAndHome()
    {
        // Found by running it, not by reading it: the first real run had the camera tracking, going
        // home, and starting another track within three seconds - because arriving home settles the
        // picture enough to fire the very zone that starts the next one. Left alone it never spends
        // any time at home actually watching, which is the job it exists to do.
        var options = new PtzTrackingOptions { Enabled = true, RestSeconds = 30 };
        var tracker = BuildTracker(options);
        var camera = PtzCamera();

        tracker.Start(camera);
        Assert.True(tracker.IsTracking(camera.Id));

        tracker.AbortForManualControl(camera.Id);
        Assert.False(tracker.IsTracking(camera.Id));
        Assert.True(tracker.IsResting(camera.Id), "a finished track must leave the camera resting");

        // A trigger arriving during the rest period is ignored rather than starting another track.
        tracker.Start(camera);
        Assert.False(tracker.IsTracking(camera.Id));
    }

    [Fact]
    public void TrackingIsOffUnlessItIsSwitchedOn()
    {
        // It moves a camera on its own. That is not something to do by default.
        var tracker = BuildTracker(new PtzTrackingOptions());
        var camera = PtzCamera();

        tracker.Start(camera);

        Assert.False(tracker.IsTracking(camera.Id));
    }

    [Fact]
    public void TrackableClassesAreConfigurableByNameOrByRawId()
    {
        var (ids, unrecognised) = PtzTrackClasses.Resolve(["person", "dog", "car"]);
        Assert.Equal([1, 3, 18], ids.OrderBy(id => id));
        Assert.Empty(unrecognised);

        // A raw id keeps a class trackable even when this table has no name for it - the model
        // knows more than the table does, and being unable to name something shouldn't put it out
        // of reach.
        var (numeric, _) = PtzTrackClasses.Resolve(["42"]);
        Assert.Equal([42], numeric);
    }

    [Fact]
    public void AnUnknownClassNameCostsThatClassRatherThanTheWholeConfiguration()
    {
        var (ids, unrecognised) = PtzTrackClasses.Resolve(["person", "sasquatch"]);

        Assert.Equal([1], ids);
        Assert.Equal(["sasquatch"], unrecognised);
    }
}
