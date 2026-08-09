namespace SentinelCore.Web;

// Where a camera's video actually comes from, for everything that points ffmpeg at one - HLS
// live view, snapshots, and the trigger-zone monitor's frame grabs.
//
// For an ordinary camera that is just its own StreamUrl with any credentials folded in. A
// browser-published camera (see CameraPublishEndpoints) has no address of its own to dial: a
// laptop or phone pushes its webcam into the go2rtc sidecar over WebRTC, and the media is read
// back out of that sidecar's loopback RTSP server. Resolving both here is what lets every
// downstream consumer stay completely unaware of the difference - a published camera records
// clips, fires trigger zones and gets analyzed exactly like a real one.
//
// Loopback by construction: the sidecar's RTSP port is bound to 127.0.0.1 (see
// Go2RtcManager.BuildConfigYaml), so this URL is only reachable from this process.
public sealed class CameraSource(IConfiguration configuration)
{
    private readonly int _rtspPort = configuration.GetValue("Go2Rtc:RtspPort", Go2RtcManager.DefaultRtspPort);

    public string ResolveUrl(Camera camera) =>
        camera.IsBrowserPublished
            ? PublishedUrl(camera.Id)
            : HlsStreamManager.BuildAuthenticatedUrl(camera);

    // The go2rtc stream a publishing browser feeds, addressed as RTSP. Named by camera id for the
    // same reason Go2RtcManager registers streams that way: it is stable across renames and can't
    // collide.
    public string PublishedUrl(Guid cameraId) => $"rtsp://127.0.0.1:{_rtspPort}/{cameraId:N}";
}
