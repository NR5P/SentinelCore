using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SentinelCore;
using Xunit;

namespace SentinelCore.Tests;

// AlertQueue keeps the newest frame of a burst, always, decided at ingest on nothing but the camera
// name. These pin the one case that gets wrong - an empty frame standing down one with a subject in
// it - and, just as importantly, that everything else still behaves as it did.
//
// The numbers behind this: 852 superseded alerts on the live database, all from the gate camera,
// and a burst observed on 2026-08-25 where three frames containing a person were discarded for a
// later empty one that then analyzed in 0.26s as "No person, vehicle, or animal detected."
public sealed class BurstFramePickerTests
{
    // COCO-91, which is what rf-detr-nano emits and what FastLocalVisionClient matches on - person
    // is 1 there, not the 0 of the 80-class indexings.
    private const int PersonClass = 1;

    // The marker byte is what the fake detector keys on - DetectAsync only ever sees the image, so
    // the two frames of a burst have to be distinguishable by their bytes, as real ones are.
    private const byte Empty = 0;
    private const byte HasPerson = 1;

    private static IncomingAlert Alert(byte marker, string profile = "gate") =>
        new(Guid.NewGuid(), "gate camera front door", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            profile, "image/jpeg", "frame.jpg", [marker]);

    private static BurstFramePicker Picker(FakeDetector detector) => new(
        detector,
        new FakeProfiles(),
        new StaticOptionsMonitor<FastLocalOptions>(new FastLocalOptions()),
        NullLogger<BurstFramePicker>.Instance);

    // The regression. Three of these in a row is what happened during a real walk past the gate.
    [Fact]
    public async Task AnEmptyFrame_DoesNotStandDownAFrameWithAPersonInIt() =>
        Assert.False(await Picker(new FakeDetector()).ShouldSupersedeAsync(
            Alert(HasPerson), Alert(Empty), TestContext.Current.CancellationToken));

    // AlertQueue's original reasoning, which stays intact: "the truck arrives, then someone gets
    // out of it". Both frames have something, so the newer one is still the better spend.
    [Fact]
    public async Task WhenBothFramesHaveASubject_TheNewerOneStillWins() =>
        Assert.True(await Picker(new FakeDetector()).ShouldSupersedeAsync(
            Alert(HasPerson), Alert(HasPerson), TestContext.Current.CancellationToken));

    // An empty frame displacing another empty frame costs nothing and keeps the queue on the live
    // edge of the event, which is the whole point of superseding.
    [Fact]
    public async Task WhenNeitherFrameHasASubject_TheNewerOneWins() =>
        Assert.True(await Picker(new FakeDetector()).ShouldSupersedeAsync(
            Alert(Empty), Alert(Empty), TestContext.Current.CancellationToken));

    [Fact]
    public async Task AFrameWithASubject_StandsDownAnEmptyOne() =>
        Assert.True(await Picker(new FakeDetector()).ShouldSupersedeAsync(
            Alert(Empty), Alert(HasPerson), TestContext.Current.CancellationToken));

    [Fact]
    public async Task WithNothingWaiting_ThereIsNothingToDecide()
    {
        var detector = new FakeDetector();

        Assert.True(await Picker(detector).ShouldSupersedeAsync(null, Alert(HasPerson), TestContext.Current.CancellationToken));
        Assert.Equal(0, detector.Calls);
    }

    // Every failure path has to land on the old behaviour: a burst is the worst moment to start
    // failing ingest over an optional refinement.
    [Fact]
    public async Task WhenTheDetectorIsNotReady_ItFallsBackToKeepingTheNewerFrame()
    {
        var detector = new FakeDetector { Ready = false };

        Assert.True(await Picker(detector).ShouldSupersedeAsync(
            Alert(HasPerson), Alert(Empty), TestContext.Current.CancellationToken));
        Assert.Equal(0, detector.Calls);
    }

    [Fact]
    public async Task WhenTheDetectorThrows_ItFallsBackToKeepingTheNewerFrame() =>
        Assert.True(await Picker(new FakeDetector { Throw = true }).ShouldSupersedeAsync(
            Alert(HasPerson), Alert(Empty), TestContext.Current.CancellationToken));

    // The cheap half of the check runs first: if the waiting frame is empty it is going to be stood
    // down whatever the arriving frame holds, so the second pass is never paid for.
    [Fact]
    public async Task AnEmptyWaitingFrame_IsDecidedWithoutProbingTheArrivingOne()
    {
        var detector = new FakeDetector();

        Assert.True(await Picker(detector).ShouldSupersedeAsync(
            Alert(Empty), Alert(HasPerson), TestContext.Current.CancellationToken));
        Assert.Equal(1, detector.Calls);
    }

    // An unknown profile must not throw - it just means no crop, same as a profile without one.
    [Fact]
    public async Task AnUnknownProfile_StillDecides() =>
        Assert.False(await Picker(new FakeDetector()).ShouldSupersedeAsync(
            Alert(HasPerson, "not-a-configured-profile"),
            Alert(Empty, "not-a-configured-profile"),
            TestContext.Current.CancellationToken));

    private sealed class FakeDetector : IFastLocalDetector
    {
        public bool Ready { get; init; } = true;
        public bool Throw { get; init; }
        public int Calls { get; private set; }

        public Task<IReadOnlyList<FastLocalDetection>> DetectAsync(
            byte[] imageBytes, CropRegion? crop, CancellationToken cancellationToken)
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("ONNX session unavailable");

            IReadOnlyList<FastLocalDetection> detections = imageBytes[0] == HasPerson
                ? [new FastLocalDetection(PersonClass, 0.9, 0.5, 0.5, 0.2, 0.4)]
                : [];
            return Task.FromResult(detections);
        }

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(Ready);
        public string ModelName => "fake";
    }

    private sealed class FakeProfiles : IPromptProfileProvider
    {
        public IReadOnlyDictionary<string, PromptProfile> Current { get; } =
            new Dictionary<string, PromptProfile> { ["gate"] = new() };
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
