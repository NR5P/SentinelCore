extern alias WebHost;

using Microsoft.Extensions.Configuration;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// Live view serves two renditions of a camera: the main stream for a full-screen viewer, and the
// camera's own low-resolution companion for grid tiles. The split exists because a grid of tiles
// pulling main streams saturates the link it is being watched over - measured on an iPhone against
// three 2560x1920 Reolinks, each tile got ~8 Mbit/s of a ~25 Mbit/s ceiling, every tile stalled and
// reconnected, and the same camera alone ran clean at 15 Mbit/s.
//
// The rules that matter here are the ones with a wrong answer that is silent: deriving the low
// stream for a URL shape that has one, and falling back to the main stream - rather than to
// nothing, or to a second copy of the main stream - for every URL shape that doesn't.
public sealed class StreamQualityTests
{
    private static CameraSource CreateSource() =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build());

    private static Camera CameraWith(string streamUrl, bool published = false) => new(
        Guid.NewGuid(), "Test Camera", streamUrl, null, null, false, false, null, null, published,
        DateTimeOffset.UtcNow);

    [Fact]
    public void DerivesReolinksMidStreamFromItsFlvMainStream()
    {
        // ext rather than sub: 896x672 against the main stream's 2560x1920 is already an order of
        // magnitude less data, and sub (640x480) is visibly mushy for no meaningful further saving.
        Assert.Equal(
            "http://cam/flv?port=1935&app=bcs&stream=channel0_ext.bcs",
            CameraSource.TryDeriveLowResUrl("http://cam/flv?port=1935&app=bcs&stream=channel0_main.bcs"));
    }

    [Fact]
    public void DerivesReolinksSubStreamFromItsRtspMainStream()
    {
        Assert.Equal(
            "rtsp://cam:554/h264Preview_01_sub",
            CameraSource.TryDeriveLowResUrl("rtsp://cam:554/h264Preview_01_main"));
    }

    [Fact]
    public void HasNoLowStreamToOfferForAnUnrecognizedUrl()
    {
        // Guessing here would point ffmpeg at a URL the camera doesn't serve, which fails as a
        // dead tile rather than as a visibly wrong one.
        Assert.Null(CameraSource.TryDeriveLowResUrl("rtsp://cam:554/Streaming/Channels/101"));
        Assert.Null(CameraSource.TryDeriveLowResUrl("rtsp://cam/stream"));
    }

    [Fact]
    public void AskingForTheLowStreamOnAnUnrecognizedCameraServesTheMainOne()
    {
        // A preference, not a demand - a camera nobody can derive a low stream for still has to
        // show a picture.
        var camera = CameraWith("rtsp://cam:554/Streaming/Channels/101");
        Assert.Equal(
            CreateSource().ResolveUrl(camera, StreamQuality.Main),
            CreateSource().ResolveUrl(camera, StreamQuality.Sub));
    }

    [Fact]
    public void AskingForTheLowStreamOnAReolinkServesTheLowOne()
    {
        var camera = CameraWith("rtsp://cam:554/h264Preview_01_main");
        Assert.Equal("rtsp://cam:554/h264Preview_01_sub", CreateSource().ResolveUrl(camera, StreamQuality.Sub));
        Assert.Equal("rtsp://cam:554/h264Preview_01_main", CreateSource().ResolveUrl(camera, StreamQuality.Main));
    }

    [Fact]
    public void APublishedCameraHasOnlyTheOneRenditionABrowserIsPushing()
    {
        // Nothing is pulling this camera, so there is no second rendition to ask a camera for -
        // and rewriting the loopback URL would point at a go2rtc stream that doesn't exist.
        var camera = CameraWith(string.Empty, published: true);
        var source = CreateSource();
        Assert.Equal(source.ResolveUrl(camera, StreamQuality.Main), source.ResolveUrl(camera, StreamQuality.Sub));
    }

    [Fact]
    public void DefaultsToTheMainStreamWhenNoQualityIsNamed()
    {
        // The single-argument overload is what every caller predating the split still uses.
        var camera = CameraWith("rtsp://cam:554/h264Preview_01_main");
        var source = CreateSource();
        Assert.Equal(source.ResolveUrl(camera, StreamQuality.Main), source.ResolveUrl(camera));
    }
}
