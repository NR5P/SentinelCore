using System.Text.Json;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The rules behind the MSE transcode, without a process or a sidecar. What is worth pinning here
// is the encoder's arguments and the idle rule: the arguments carry two traps that have already
// cost real debugging time once each (see the comments in PublishedMseTranscoder), and the idle
// rule decides whether an encode runs against an empty room.
public class PublishedMseTranscoderTests
{
    private const string Input = "rtsp://127.0.0.1:18554/aaaabbbbccccddddeeeeffff00001111";
    private const string Output = "rtsp://127.0.0.1:18554/aaaabbbbccccddddeeeeffff00001111-mse";

    [Fact]
    public void TheMseStreamIsANameOfItsOwn()
    {
        var id = Guid.Parse("3b77492a-9441-411e-b53d-9a24a673b4fb");

        // Distinct from the published stream itself, which is what makes this safe: nothing here
        // re-PUTs or otherwise disturbs the stream the browser is publishing into.
        Assert.Equal("3b77492a9441411eb53d9a24a673b4fb-mse", PublishedMseTranscoder.StreamNameFor(id));
        Assert.NotEqual(id.ToString("N"), PublishedMseTranscoder.StreamNameFor(id));
    }

    // The whole point of the class. If these ever become a copy, the stream stops being decodable
    // again and the tile goes back to being black with no error anywhere.
    [Fact]
    public void VideoIsReEncoded_NotCopied()
    {
        var args = PublishedMseTranscoder.BuildFfmpegArguments(Input, Output, includeAudio: false);

        Assert.Contains("libx264", args);
        Assert.DoesNotContain("copy", args);
    }

    // A browser's WebRTC encoder emits an IDR roughly every 30 seconds. Without a closed one-second
    // GOP here, a viewer joining mid-stream waits that long for a first picture even though the
    // parameter sets are now correct - which would look exactly like the bug this fixes.
    [Fact]
    public void TheGopIsClosedAndOneSecond()
    {
        var args = PublishedMseTranscoder.BuildFfmpegArguments(Input, Output, includeAudio: false);

        Assert.Equal("30", ValueAfter(args, "-g"));
        Assert.Equal("30", ValueAfter(args, "-keyint_min"));
        Assert.Equal("0", ValueAfter(args, "-sc_threshold"));
        Assert.Equal("0", ValueAfter(args, "-bf"));
    }

    // The trap HlsStreamManager carries the same warning about: -skip_frame is a decoder option,
    // harmless while the output is a copy and ruinous when it is an encode, because the encoder
    // would then be fed keyframes only - roughly one frame per second of video.
    [Fact]
    public void TheDecoderIsNeverAskedToSkipNonKeyframes()
    {
        var args = PublishedMseTranscoder.BuildFfmpegArguments(Input, Output, includeAudio: true);

        Assert.DoesNotContain("-skip_frame", args);
        Assert.DoesNotContain("nokey", args);
    }

    // An iOS MSE handshake offers only mp4a.40.2, while a publishing browser sends Opus, so audio
    // has to be re-encoded here or it never arrives on an Apple client at all.
    [Fact]
    public void AudioIsTranscodedToAacWhenTheCameraHasIt_AndDroppedWhenItDoesNot()
    {
        var withAudio = PublishedMseTranscoder.BuildFfmpegArguments(Input, Output, includeAudio: true);
        Assert.Equal("aac", ValueAfter(withAudio, "-c:a"));
        Assert.DoesNotContain("-an", withAudio);

        var without = PublishedMseTranscoder.BuildFfmpegArguments(Input, Output, includeAudio: false);
        Assert.Contains("-an", without);
        Assert.DoesNotContain("-c:a", without);
    }

    [Fact]
    public void ItReadsThePublishedStreamAndWritesTheMseOne()
    {
        var args = PublishedMseTranscoder.BuildFfmpegArguments(Input, Output, includeAudio: false);

        Assert.Equal(Input, ValueAfter(args, "-i"));
        Assert.Equal(Output, args[^1]);
        Assert.Equal("rtsp", ValueAfter(args, "-f"));
    }

    // One idle sweep is a tile being dragged or a viewer moving between grid and fullscreen; two
    // is nobody watching.
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(5, true)]
    public void AnEncodeStopsOnlyAfterTwoIdleSweeps(int idleSweeps, bool expected)
        => Assert.Equal(expected, PublishedMseTranscoder.ShouldStop(idleSweeps));

    [Fact]
    public void ConsumersAreCountedFromGo2RtcsOwnView()
    {
        var withViewers = JsonDocument.Parse(
            """
            {"producers":[{"url":"rtsp://127.0.0.1:1/awaiting-publisher"}],
             "consumers":[{"id":1,"format_name":"mse/fmp4"},{"id":2,"format_name":"mse/fmp4"}]}
            """).RootElement;
        Assert.Equal(2, PublishedMseTranscoder.CountConsumers(withViewers));

        var none = JsonDocument.Parse("""{"producers":[],"consumers":[]}""").RootElement;
        Assert.Equal(0, PublishedMseTranscoder.CountConsumers(none));

        // A stream go2rtc has only just been told about has no consumers key at all; that is an
        // empty room, not a parse failure.
        var absent = JsonDocument.Parse("""{"producers":[]}""").RootElement;
        Assert.Equal(0, PublishedMseTranscoder.CountConsumers(absent));
    }

    private static string? ValueAfter(List<string> args, string flag)
    {
        var index = args.IndexOf(flag);
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }
}
