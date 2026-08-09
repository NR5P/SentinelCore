extern alias WebHost;

using Microsoft.Extensions.Configuration;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// A browser-published camera is one whose video is pushed to this server by a laptop or phone
// rather than pulled from an address. The whole design rests on making that difference invisible
// below one seam: the camera has no URL of its own, so CameraSource hands every ffmpeg consumer
// the go2rtc sidecar's loopback RTSP address instead, and clips, trigger zones and analysis carry
// on unaware.
//
// These tests pin the two halves of that seam. They matter because both halves fail silently:
// resolve the wrong URL and ffmpeg simply connects to nothing, and register the wrong go2rtc
// source and the sidecar quietly dials the camera's own address in a loop.
public sealed class BrowserPublishedCameraTests
{
    private static Camera Published(Guid? id = null) => new(
        id ?? Guid.NewGuid(), "Living room laptop", StreamUrl: "", Username: null, Password: null,
        IncludeAudio: false, PtzEnabled: false, OnvifPtzServiceUrl: null, OnvifProfileToken: null,
        IsBrowserPublished: true, DateTimeOffset.UtcNow);

    private static Camera Pulled() => new(
        Guid.NewGuid(), "Front gate", "rtsp://cam.local/live", "user", "secret",
        IncludeAudio: false, PtzEnabled: false, OnvifPtzServiceUrl: null, OnvifProfileToken: null,
        IsBrowserPublished: false, DateTimeOffset.UtcNow);

    private static CameraSource SourceWith(params (string Key, string Value)[] settings) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build());

    [Fact]
    public void APublishedCamera_ResolvesToTheSidecarsLoopbackRtspStream()
    {
        var camera = Published(Guid.Parse("9aba2489-c47e-4dd4-a05f-ed5e0ad49902"));

        var url = SourceWith().ResolveUrl(camera);

        // Loopback, because the sidecar's RTSP port is deliberately not exposed to the LAN, and
        // named by the camera's id in "N" form because that is how Go2RtcManager names streams.
        Assert.Equal($"rtsp://127.0.0.1:{Go2RtcManager.DefaultRtspPort}/9aba2489c47e4dd4a05fed5e0ad49902", url);
    }

    [Fact]
    public void APublishedCamerasUrl_FollowsTheConfiguredSidecarPort()
    {
        // The port is configurable, and a published camera's URL is built rather than stored - so
        // changing it must not leave every published camera pointing at a port nothing listens on.
        var url = SourceWith(("Go2Rtc:RtspPort", "28554")).ResolveUrl(Published());

        Assert.StartsWith("rtsp://127.0.0.1:28554/", url);
    }

    [Fact]
    public void AnOrdinaryCamera_StillResolvesToItsOwnUrlWithCredentials()
    {
        var url = SourceWith().ResolveUrl(Pulled());

        Assert.Equal("rtsp://user:secret@cam.local/live", url);
    }

    // go2rtc will not accept a publisher for a stream it doesn't know about, and has no way to
    // create an empty one: a PUT with no source (or an empty source) returns 200 and creates
    // nothing. So a published camera is registered with a source that exists only to bring the
    // stream into being, and must never be something go2rtc could actually connect to - least of
    // all the stream's own address.
    [Fact]
    public void APublishedCamera_IsRegisteredWithAPlaceholderSourceRatherThanAnythingDialable()
    {
        var camera = Published();

        var sources = Go2RtcManager.BuildStreamSources(camera);

        var only = Assert.Single(sources);
        Assert.StartsWith("rtsp://127.0.0.1:", only);
        // Port 1 is reserved and unused, so an accidental dial fails instantly instead of hanging.
        Assert.Contains(":1/", only);
        Assert.DoesNotContain(camera.Id.ToString("N"), only);
    }

    // Audio for a published camera arrives inside the browser's WebRTC stream, already in a codec
    // browsers speak. The ffmpeg audio transcode that ordinary cameras need would have nothing to
    // read, and adding it would make go2rtc start a process per camera for no reason.
    [Fact]
    public void APublishedCameraWithAudio_DoesNotGetTheAudioTranscodeSource()
    {
        var camera = Published() with { IncludeAudio = true };

        var sources = Go2RtcManager.BuildStreamSources(camera);

        Assert.Single(sources);
        Assert.DoesNotContain(sources, s => s.StartsWith("ffmpeg:"));
    }

    [Fact]
    public void AnOrdinaryCameraWithAudio_IsUnaffected()
    {
        var camera = Pulled() with { IncludeAudio = true };

        var sources = Go2RtcManager.BuildStreamSources(camera);

        Assert.Equal(2, sources.Count);
        Assert.Equal("rtsp://user:secret@cam.local/live", sources[0]);
        Assert.StartsWith("ffmpeg:", sources[1]);
    }
}
