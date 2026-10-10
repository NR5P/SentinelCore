namespace SentinelCore.Web;

// How to serve a file someone uploaded (Family Files, Organizer attachments) back to a browser.
//
// Any account can upload, and these files are served from the dashboard's own origin - so an SVG
// or HTML file with a <script> in it, opened by an Admin, would run with that Admin's session and
// could read every camera password through the API. Only types a browser renders without running
// anything are served inline; everything else is a download. An <img> or <video> tag ignores
// Content-Disposition, so SVG previews in the page still work - only opening the file directly
// changes, from "runs it" to "saves it".
public static class UploadedContent
{
    private static readonly HashSet<string> InlineSafe = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp", "image/bmp", "image/heic", "image/tiff", "image/avif",
        "video/mp4", "video/quicktime", "video/webm",
        "audio/mpeg", "audio/mp4", "audio/wav", "audio/ogg", "audio/flac", "audio/aac", "audio/webm",
        "application/pdf",
        "text/plain", "text/csv",
    };

    public static bool IsInlineSafe(string? contentType) =>
        contentType is not null && InlineSafe.Contains(contentType.Split(';')[0].Trim());

    // The file name to pass to Results.File: null keeps it inline, a name makes it a download.
    // Always set nosniff, so a browser can't decide an inline-safe "text/plain" is really HTML.
    public static string? DownloadNameFor(HttpContext context, string contentType, string fileName, bool downloadRequested)
    {
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return downloadRequested || !IsInlineSafe(contentType) ? fileName : null;
    }
}
