namespace SentinelCore.Web;

// How an event snapshot is handed back, shared by the two endpoints that serve one (an alert's,
// under /api/events, and a trigger's, under /api/triggers) so they cannot drift apart in caching
// or in what "size=card" means.
public static class EventImageResults
{
    // The value the Events list asks for. A word rather than a pixel count: the size a card wants
    // is this server's business, and a number in the query string would be a promise to keep
    // serving that number - and an invitation to ask for 4000 and have us re-encode on demand.
    public const string CardSize = "card";

    // An event's snapshot is written once, under a filename carrying the instant it was taken, and
    // never rewritten - so a client that already has it never needs to ask again. Without this
    // there was no Cache-Control at all and a browser was left to guess, which meant re-fetching
    // megabytes of unchanged JPEG on every visit to the tab.
    //
    // private, not public: these are authenticated, and Internet Exposed Mode puts a Cloudflare
    // Tunnel in this path (see InternetExposedSettings). A shared cache holding one household's
    // camera snapshots is exactly the thing that must not happen.
    private const string CacheControl = "private, max-age=604800, immutable";

    public static async Task<IResult> ServeAsync(
        string originalPath, string? size, EventImageThumbnails thumbnails, HttpContext http, CancellationToken cancellationToken)
    {
        var path = originalPath;
        if (string.Equals(size, CardSize, StringComparison.OrdinalIgnoreCase))
        {
            // Falls back to the original rather than failing: a snapshot that cannot be decoded
            // still deserves to appear, and that is exactly what every card did before this.
            path = await thumbnails.GetOrCreateAsync(originalPath, cancellationToken) ?? originalPath;
        }

        http.Response.Headers.CacheControl = CacheControl;

        // The physical-path overload rather than ReadAllBytes: it streams from disk and fills in
        // Last-Modified, so a conditional request can be answered 304. Reading whole multi-megabyte
        // frames into memory to write them straight out again was fifty allocations of that size
        // per load of the Events tab.
        return Results.File(path, "image/jpeg", lastModified: File.GetLastWriteTimeUtc(path), entityTag: null);
    }
}
