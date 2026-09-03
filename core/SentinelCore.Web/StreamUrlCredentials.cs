namespace SentinelCore.Web;

// Puts a camera's username and password into its stream URL, which is the only way ffmpeg and
// ffprobe can be given them - neither takes credentials as a separate argument for RTSP.
//
// Shared rather than duplicated: this used to exist twice, as HlsStreamManager.BuildAuthenticatedUrl
// and CameraDiscoveryService.EmbedCredentials, and both copies carried the same bug (see below).
// "How a credential is attached to a stream URL" is one fact about this system, and two copies of
// it are two chances to disagree about it.
public static class StreamUrlCredentials
{
    // A blank password is a real configuration, not a missing one - a camera can have an account
    // with no password set, and Reolink ships at least one model (RLC-510A) that way. Such a camera
    // still requires the username: dialed anonymously it answers 401, exactly like a wrong password.
    //
    // UriBuilder cannot express that case. Its ToString() emits the ':' separator only when the
    // password is non-empty, so a blank password yields "rtsp://admin@host" - which ffmpeg reads as
    // "no credentials supplied" and the camera answers 401. The form that authenticates is
    // "rtsp://admin:@host", with the colon present and nothing after it. Measured against an
    // RLC-510A with a blank admin password: "admin@" gives 401, "admin:@" streams h264 2560x1920.
    //
    // (go2rtc parses the colon-less form correctly and is unaffected either way, so the live WebRTC
    // path never showed this. It is the ffmpeg consumers - the discovery probe, HLS, snapshots and
    // trigger detection - that see the 401.)
    //
    // The non-empty-password path is left to UriBuilder unchanged, byte for byte, because that is
    // the path every working camera already uses and its escaping is not worth reimplementing.
    public static string Embed(string url, string? username, string? password)
    {
        // No username means nothing to attach - the camera is dialed as the URL already stands,
        // which is correct for a camera that needs no authentication and for one whose credentials
        // are already written into the URL by hand.
        if (string.IsNullOrWhiteSpace(username)) return url;

        var builder = new UriBuilder(url) { UserName = username, Password = password ?? string.Empty };
        if (!string.IsNullOrEmpty(password)) return builder.Uri.ToString();

        // Rebuilt from parsed components rather than by patching the string UriBuilder returned.
        // A patch has to find the userinfo it means to change, and the obvious way to look for it -
        // searching for the raw username - silently does nothing when the username needed escaping.
        // Authority is host and port only; userinfo is not part of it, so there is nothing to strip.
        var uri = builder.Uri;
        return $"{uri.Scheme}://{Uri.EscapeDataString(username)}:@{uri.Authority}{uri.PathAndQuery}{uri.Fragment}";
    }
}
