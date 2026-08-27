using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The transcripts below are not invented. Each was produced by running ffmpeg against the real
// cameras on this network on 2026-08-27, because the distinction this class draws is only worth
// anything if it matches what these cameras actually do:
//
//   .23 off the network      exits, "No route to host"
//   .22 port 9999 closed     exits, "Connection refused"
//   .22 wrong password       exits, "End of file"
//   .22 nonexistent stream   BLOCKS - never exits, never prints, killed at 15s
//
// That last one is the finding that motivated all of this. TriggerZoneMonitor concluded "this
// camera has no substream" from an immediate exit, but a missing substream is the one case that
// does not exit - so every time that inference fired, it was wrong.
public sealed class FfmpegExitReasonTests
{
    private static readonly string[] HostOffNetwork =
    [
        "[tcp @ 0x61ac55431040] Connection to tcp://192.168.50.23:80 failed: No route to host",
        "[in#0 @ 0x61ac5542dc00] Error opening input: No route to host",
        "Error opening input file http://192.168.50.23/flv?port=1935&app=bcs&stream=channel0_ext.bcs&user=admin&password=hunter2.",
        "Error opening input files: No route to host",
    ];

    private static readonly string[] PortClosed =
    [
        "[tcp @ 0x56216faa7fc0] Connection to tcp://192.168.50.22:9999 failed: Connection refused",
        "[in#0 @ 0x56216faa4c00] Error opening input: Connection refused",
        "Error opening input file http://192.168.50.22:9999/flv.",
        "Error opening input files: Connection refused",
    ];

    private static readonly string[] WrongPassword =
    [
        "[in#0 @ 0x6195380c4c00] Error opening input: End of file",
        "Error opening input file http://192.168.50.22/flv?port=1935&app=bcs&stream=channel0_ext.bcs&user=admin&password=WRONGPASS.",
        "Error opening input files: End of file",
    ];

    // Straight out of the journal on 2026-08-27, when the gate camera's login had been blanked by
    // an edit and every DESCRIBE came back 401. This is the transcript that showed "Server returned
    // 4" swallowing an auth failure and reporting it as a rendition that isn't there.
    private static readonly string[] RtspWrongPassword =
    [
        "[rtsp @ 0x5f5d264fc1c0] method DESCRIBE failed: 401 Unauthorized",
        "[in#0 @ 0x5f5d264fbf00] Error opening input: Server returned 401 Unauthorized (authorization failed)",
        "Error opening input file rtsp://192.168.50.146:554/Preview_01_sub.",
        "Error opening input files: Server returned 401 Unauthorized (authorization failed)",
    ];

    private static readonly string[] StreamRefusedByServer =
    [
        "[http @ 0x5621000] Server returned 404 Not Found",
        "[in#0 @ 0x5621111] Error opening input: Server returned 404 Not Found",
        "Error opening input files: Server returned 404 Not Found",
    ];

    [Fact]
    public void AnUnreachableCameraIsNotEvidenceAboutItsSubstream()
    {
        Assert.True(FfmpegExitReason.IndicatesSourceUnreachable(HostOffNetwork));
        Assert.False(FfmpegExitReason.IndicatesSubstreamMissing(HostOffNetwork));
    }

    [Fact]
    public void ARefusedPortIsNotEvidenceAboutTheSubstreamEither()
    {
        Assert.True(FfmpegExitReason.IndicatesSourceUnreachable(PortClosed));
        Assert.False(FfmpegExitReason.IndicatesSubstreamMissing(PortClosed));
    }

    // The important half of "requires positive evidence": an exit whose cause says nothing about
    // stream availability must not latch the camera onto its main stream.
    [Fact]
    public void AnUninformativeExitConcludesNothing()
    {
        Assert.False(FfmpegExitReason.IndicatesSubstreamMissing(WrongPassword));
        Assert.False(FfmpegExitReason.IndicatesSubstreamMissing([]));
        Assert.False(FfmpegExitReason.IndicatesSubstreamMissing(["frame= 12 fps=2.0 q=-1.0 size=N/A"]));
    }

    // ...and the half that keeps the original behaviour alive for a camera that really does answer
    // and refuse. This is not how the Reolinks here fail, but it is how the latch was meant to fire.
    [Fact]
    public void AServerThatAnswersAndRefusesTheStreamDoesCount()
    {
        Assert.True(FfmpegExitReason.IndicatesSubstreamMissing(StreamRefusedByServer));
        Assert.False(FfmpegExitReason.IndicatesSourceUnreachable(StreamRefusedByServer));
    }

    // The regression that cost the gate camera its low-resolution stream: a 401 is a 4xx, so
    // "Server returned 4" matched it, and a wrong password was read as proof that the substream
    // did not exist - promoting every grid tile onto a 4K stream for ten minutes at a time.
    [Fact]
    public void ARejectedPasswordIsNotEvidenceThatTheSubstreamIsMissing()
    {
        Assert.True(FfmpegExitReason.IndicatesAuthenticationFailure(RtspWrongPassword));
        Assert.False(FfmpegExitReason.IndicatesSubstreamMissing(RtspWrongPassword));
        Assert.False(FfmpegExitReason.IndicatesSourceUnreachable(RtspWrongPassword));
    }

    // The line ffmpeg derives from the DESCRIBE failure runs the two words together, and it is the
    // only line some sessions get, so matching has to survive it.
    [Fact]
    public void TheRunTogetherSpellingOfUnauthorizedIsRecognisedToo()
    {
        Assert.True(FfmpegExitReason.IndicatesAuthenticationFailure(
            ["[rtsp @ 0x1] method DESCRIBE failed: 401Unauthorized"]));
    }

    // The failures that are genuinely about the stream must still count - excluding auth is a
    // carve-out, not a repeal.
    [Fact]
    public void ExcludingAuthDoesNotDisarmTheOrdinaryRefusal()
    {
        Assert.True(FfmpegExitReason.IndicatesSubstreamMissing(StreamRefusedByServer));
        Assert.False(FfmpegExitReason.IndicatesAuthenticationFailure(StreamRefusedByServer));
        Assert.False(FfmpegExitReason.IndicatesAuthenticationFailure(HostOffNetwork));
        Assert.False(FfmpegExitReason.IndicatesAuthenticationFailure([]));
    }

    [Fact]
    public void DescribeNamesARejectedPasswordAsWhatItIs()
    {
        Assert.Equal(
            "Error opening input: Server returned 401 Unauthorized (authorization failed)",
            FfmpegExitReason.Describe(RtspWrongPassword));
    }

    [Fact]
    public void DescribeNamesTheCauseWithoutFfmpegsHeapAddresses()
    {
        Assert.Equal("Error opening input: No route to host", FfmpegExitReason.Describe(HostOffNetwork));
        Assert.Equal("Error opening input: Connection refused", FfmpegExitReason.Describe(PortClosed));
    }

    // The reason this is not just a nicety: ffmpeg echoes the input URL into its error text, and
    // every Reolink here carries the admin password in that URL. Logging the line unmasked would
    // put the camera password in the journal.
    [Fact]
    public void DescribeNeverLeaksTheCameraPasswordFromTheEchoedUrl()
    {
        var urlOnly = new[]
        {
            "Error opening input file http://192.168.50.23/flv?port=1935&app=bcs&stream=channel0_ext.bcs&user=admin&password=hunter2.",
        };

        var described = FfmpegExitReason.Describe(urlOnly);

        Assert.NotNull(described);
        Assert.DoesNotContain("hunter2", described);
        // Still recognisable as the camera it is - the host is not the secret.
        Assert.Contains("192.168.50.23", described);
    }

    [Fact]
    public void DescribeMasksACredentialInTheUserinfoFormToo()
    {
        var described = FfmpegExitReason.Describe(
            ["[in#0 @ 0x1] Error opening input: rtsp://admin:hunter2@192.168.50.21:554/ failed"]);

        Assert.NotNull(described);
        Assert.DoesNotContain("hunter2", described);
    }

    [Fact]
    public void DescribeFallsBackToTheLastLineAndHandlesNoOutputAtAll()
    {
        Assert.Equal("something unfamiliar went wrong", FfmpegExitReason.Describe(
            ["Input #0, flv, from 'http://cam/':", "something unfamiliar went wrong"]));

        Assert.Null(FfmpegExitReason.Describe([]));
        Assert.Null(FfmpegExitReason.Describe(["", "   "]));
    }

    // A message that legitimately begins with a bracket keeps it; only ffmpeg's "[component @ 0x..]"
    // prefix is noise.
    [Fact]
    public void DescribeLeavesABracketThatIsNotAComponentPrefixAlone()
    {
        Assert.Equal("[whatever] the camera said", FfmpegExitReason.Describe(["[whatever] the camera said"]));
    }

    [Theory]
    // A single failure retries at once, so an ordinary blip costs nothing it did not cost before.
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    // Capped, so a camera that comes back after a night away is picked up within a minute rather
    // than backing off toward never.
    [InlineData(4, 60)]
    [InlineData(500, 60)]
    public void RestartBackoffSlowsRepeatedFailuresButAlwaysKeepsTrying(int failures, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), TriggerZoneMonitor.RestartDelayFor(failures));
    }
}
