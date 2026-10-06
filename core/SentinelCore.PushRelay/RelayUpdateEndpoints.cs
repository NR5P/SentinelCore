using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SentinelCore.PushRelay;

// Serves signed Core update bundles to licensed installs (docs/AUTO_UPDATE.md). The relay only
// hands files out: the manifest is signed on the maintainer's Mac by publish-update.sh with a key
// that never comes near this box, and Core verifies that signature and the bundle's hash itself.
// So a compromise here can withhold updates but can't forge one - which is the point of keeping
// the signing key off the machine that's reachable from the internet.
//
// Under /v1, so LicenseAuthMiddleware has already verified the caller's license (and refused a
// blocked one) before anything here runs.
public static partial class RelayUpdateEndpoints
{
    public const string ManifestFileName = "manifest.json";

    public static void MapRelayUpdateEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/updates/{channel}/{platform}", (
            string channel,
            string platform,
            HttpContext context,
            IOptions<RelayOptions> options,
            IHostEnvironment environment) =>
        {
            var folder = ReleaseFolder(options.Value, environment, channel, platform);
            if (folder is null) return Results.NotFound(new { error = "Unknown channel or platform." });

            var manifest = Path.Combine(folder, ManifestFileName);
            // No caching anywhere between here and the install: Cloudflare in particular would
            // otherwise keep answering with yesterday's manifest after a publish.
            context.Response.Headers.CacheControl = "no-store";
            return File.Exists(manifest)
                ? Results.File(manifest, "application/json")
                : Results.NotFound(new { error = "No release has been published on this channel yet." });
        });

        app.MapGet("/v1/updates/{channel}/{platform}/{fileName}", (
            string channel,
            string platform,
            string fileName,
            HttpContext context,
            IOptions<RelayOptions> options,
            IHostEnvironment environment,
            ILogger<RelayOptions> logger) =>
        {
            var folder = ReleaseFolder(options.Value, environment, channel, platform);
            if (folder is null || !BundleName().IsMatch(fileName))
                return Results.NotFound(new { error = "No such file." });

            var path = Path.Combine(folder, fileName);
            if (!File.Exists(path)) return Results.NotFound(new { error = "No such file." });

            // Range requests let an interrupted nightly download resume instead of starting over,
            // which matters for a 150 MB file coming through a home upload link. Logged once per
            // request that starts at the beginning, so a resumed download isn't counted twice.
            if (!context.Request.Headers.Range.Any())
            {
                var license = context.License();
                logger.LogInformation(
                    "{Customer} ({LicenseId}) downloading {Channel}/{Platform}/{File}",
                    license.CustomerName, license.LicenseId, channel, platform, fileName);
            }
            return Results.File(path, "application/zip", fileDownloadName: fileName, enableRangeProcessing: true);
        });
    }

    // Null for anything that isn't a plain lower-case name, so no request can walk out of the
    // updates folder however it's encoded.
    private static string? ReleaseFolder(RelayOptions options, IHostEnvironment environment, string channel, string platform)
    {
        if (!SegmentName().IsMatch(channel) || !SegmentName().IsMatch(platform)) return null;
        var root = Path.IsPathRooted(options.UpdatesDirectory)
            ? options.UpdatesDirectory
            : Path.Combine(environment.ContentRootPath, options.UpdatesDirectory);
        return Path.Combine(root, channel, platform);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex SegmentName();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\.zip$")]
    private static partial Regex BundleName();
}
