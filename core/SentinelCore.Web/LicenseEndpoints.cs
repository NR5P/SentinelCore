using SentinelCore.Licensing;

namespace SentinelCore.Web;

// Re-reads and re-verifies the license file fresh on every call rather than caching - it's one
// small file read plus one RSA verify, cheap enough that dropping in a valid license and
// refreshing the dashboard clears the nag immediately, no restart or hot-reload machinery needed.
public static class LicenseEndpoints
{
    private sealed class LicenseSettingsDto
    {
        public string FilePath { get; set; } = "license.lic";
    }

    public static void MapLicenseEndpoints(this WebApplication app)
    {
        var license = app.MapGroup("/api/license").RequireAuthorization("AdminOnly");

        license.MapGet("/status", async (AppSettingsFile settings, IConfiguration configuration, CancellationToken ct) =>
        {
            var licensePath = await ResolveLicensePathAsync(settings, configuration, ct);
            var status = LicenseStatus.Check(licensePath);
            return Results.Ok(status is null
                ? new { valid = false }
                : new { valid = true, customerName = status.CustomerName });
        });

        // Takes the raw .lic file contents (uploaded from the browser, not a path - this app
        // has no way to know what's on the customer's own computer). Validated with the same
        // embedded public key everything else uses before it's ever written to disk, so a
        // garbled upload can't silently clobber a license that was already working.
        license.MapPost("/install", async (HttpRequest request, AppSettingsFile settings, IConfiguration configuration, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var contents = await reader.ReadToEndAsync(ct);

            var verified = LicenseStatus.Verify(contents);
            if (verified is null)
                return Results.BadRequest(new { error = "That file doesn't look like a valid license - nothing was changed." });

            var licensePath = await ResolveLicensePathAsync(settings, configuration, ct);
            await File.WriteAllTextAsync(licensePath, contents, ct);

            return Results.Ok(new { valid = true, customerName = verified.CustomerName });
        });
    }

    private static async Task<string> ResolveLicensePathAsync(AppSettingsFile settings, IConfiguration configuration, CancellationToken ct)
    {
        var licenseSettings = await settings.GetSectionAsync<LicenseSettingsDto>("License", ct) ?? new LicenseSettingsDto();
        var mainAppSettingsPath = configuration["MainAppSettingsPath"]
            ?? throw new InvalidOperationException("MainAppSettingsPath is not configured.");
        var mainAppDirectory = Path.GetDirectoryName(Path.GetFullPath(mainAppSettingsPath))!;
        return Path.IsPathRooted(licenseSettings.FilePath)
            ? licenseSettings.FilePath
            : Path.Combine(mainAppDirectory, licenseSettings.FilePath);
    }
}
