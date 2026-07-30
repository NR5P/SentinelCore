using System.Collections.Concurrent;

namespace SentinelCore.Web;

// Serves the markdown docs shipped in docs/ (see scripts/publish-release.sh) as PDFs for the
// dashboard's Docs tab - see DocsPdfRenderer for how the conversion itself works. Gated by
// .RequireAuthorization() like the rest of this project's /api endpoints (images, events, ...)
// rather than sitting under wwwroot as a static file, since static files aren't covered by
// AuthGateMiddleware/the login system at all.
public static class DocsEndpoints
{
    // USER_GUIDE.md and DEPLOYMENT.md are the two docs everything else points readers at - keep
    // them first regardless of filename sort order. Anything else (PUSH_NOTIFICATIONS.md today,
    // whatever gets added to docs/ later) follows alphabetically by title.
    private static readonly string[] PreferredOrder = ["USER_GUIDE", "DEPLOYMENT"];

    // Keyed by "{slug}:{lastWriteTimeUtc.Ticks}" so an edited doc (during development, or a
    // future release with updated docs) naturally invalidates its old entry instead of serving
    // a stale render - there are only a handful of docs, so this never needs eviction.
    private static readonly ConcurrentDictionary<string, byte[]> PdfCache = new();

    public static void MapDocsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/docs", (IHostEnvironment environment) =>
            Results.Ok(GetDocs(environment).Select(d => new { d.Slug, d.Title }))
        ).RequireAuthorization();

        app.MapGet("/api/docs/{slug}/pdf", (string slug, IHostEnvironment environment) =>
        {
            var doc = GetDocs(environment).FirstOrDefault(d => d.Slug == slug);
            if (doc is null)
                return Results.NotFound(new { error = "No such doc." });

            var cacheKey = $"{doc.Slug}:{doc.LastWriteTimeUtc.Ticks}";
            var pdf = PdfCache.GetOrAdd(cacheKey, _ =>
                DocsPdfRenderer.RenderToPdf(doc.Title, File.ReadAllText(doc.Path)));

            return Results.File(pdf, "application/pdf");
        }).RequireAuthorization();
    }

    private sealed record DocInfo(string Slug, string Title, string Path, DateTime LastWriteTimeUtc);

    private static List<DocInfo> GetDocs(IHostEnvironment environment)
    {
        var docsDirectory = System.IO.Path.Combine(environment.ContentRootPath, "..", "docs");
        if (!Directory.Exists(docsDirectory))
            return [];

        var docs = Directory.GetFiles(docsDirectory, "*.md")
            .Select(path =>
            {
                var stem = System.IO.Path.GetFileNameWithoutExtension(path);
                var slug = stem.ToLowerInvariant().Replace('_', '-');
                var title = ExtractTitle(path) ?? Prettify(stem);
                return new DocInfo(slug, title, path, File.GetLastWriteTimeUtc(path));
            })
            .ToList();

        return docs
            .OrderBy(d => Array.IndexOf(PreferredOrder, System.IO.Path.GetFileNameWithoutExtension(d.Path)) is var i && i >= 0 ? i : PreferredOrder.Length)
            .ThenBy(d => d.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ExtractTitle(string path)
    {
        using var reader = new StreamReader(path);
        for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
                return trimmed[2..].Trim();
            if (trimmed.Length > 0)
                return null; // content starts before any H1 - fall back to the filename
        }
        return null;
    }

    private static string Prettify(string stem) =>
        string.Join(' ', stem.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
}
