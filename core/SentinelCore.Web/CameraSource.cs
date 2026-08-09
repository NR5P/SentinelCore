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
// Which of a camera's two renditions a consumer is asking for. A grid of tiles a few hundred
// points wide wants Sub; a full-screen single camera wants Main.
//
// This exists because live view had no such choice and always served Main. Measured on an iPhone
// against three 2560x1920 Reolinks: each tile pulled ~8 Mbit/s, ~25 Mbit/s in aggregate, which
// saturated the phone's Wi-Fi - throughput per tile halved, every tile stalled and reconnected,
// and dropped frames stayed at zero throughout (starved for data, not short of decode). The same
// camera alone ran at 15 Mbit/s with no stalls at all. Detection has downshifted to the low
// rendition since it shipped, for the same reason; live view simply never got the same treatment.
public enum StreamQuality
{
    Main,
    Sub
}

public sealed class CameraSource(IConfiguration configuration)
{
    private readonly int _rtspPort = configuration.GetValue("Go2Rtc:RtspPort", Go2RtcManager.DefaultRtspPort);

    public string ResolveUrl(Camera camera) => ResolveUrl(camera, StreamQuality.Main);

    public string ResolveUrl(Camera camera, StreamQuality quality)
    {
        // A published camera is whatever the browser is pushing - there is only ever one rendition
        // of it, so asking for the low one is answered with the only one there is.
        if (camera.IsBrowserPublished) return PublishedUrl(camera.Id);

        var mainUrl = HlsStreamManager.BuildAuthenticatedUrl(camera);
        if (quality == StreamQuality.Main) return mainUrl;
        return TryDeriveLowResUrl(mainUrl) ?? mainUrl;
    }

    // The camera's low-resolution companion stream for a known main-stream URL, or null when no
    // safe derivation exists - a camera whose URL doesn't follow a recognized pattern simply keeps
    // its main stream, exactly as before this existed.
    //
    // Reolink's FLV naming: channel0_main.bcs / channel0_ext.bcs (mid) / channel0_sub.bcs (low).
    // ext is the one taken: on these cameras it is 896x672 against the main stream's 2560x1920,
    // which is an order of magnitude less data while still being worth looking at on a tile - sub
    // is 640x480 and visibly mushy for no meaningful further saving. Reolink RTSP naming offers
    // only the two: h264Preview_01_main / h264Preview_01_sub.
    //
    // Shared with TriggerZoneMonitor rather than duplicated: "the cheap rendition of this camera"
    // is one fact about the camera, and two copies of it would be two chances to disagree about
    // which stream a given camera has.
    public static string? TryDeriveLowResUrl(string mainUrl)
    {
        if (mainUrl.Contains("_main.bcs", StringComparison.OrdinalIgnoreCase))
            return mainUrl.Replace("_main.bcs", "_ext.bcs", StringComparison.OrdinalIgnoreCase);
        if (mainUrl.Contains("Preview_01_main", StringComparison.OrdinalIgnoreCase))
            return mainUrl.Replace("Preview_01_main", "Preview_01_sub", StringComparison.OrdinalIgnoreCase);
        return null;
    }

    // The go2rtc stream a publishing browser feeds, addressed as RTSP. Named by camera id for the
    // same reason Go2RtcManager registers streams that way: it is stable across renames and can't
    // collide.
    public string PublishedUrl(Guid cameraId) => $"rtsp://127.0.0.1:{_rtspPort}/{cameraId:N}";
}
