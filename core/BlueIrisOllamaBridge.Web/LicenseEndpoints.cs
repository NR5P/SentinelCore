using BlueIrisOllamaBridge.Licensing;

namespace BlueIrisOllamaBridge.Web;

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
        app.MapGet("/api/license/status", async (AppSettingsFile settings, IConfiguration configuration, CancellationToken ct) =>
        {
            var licensePath = await ResolveLicensePathAsync(settings, configuration, ct);
            var license = LicenseStatus.Check(licensePath);
            return Results.Ok(license is null
                ? new { valid = false }
                : new { valid = true, customerName = license.CustomerName });
        });

        // Takes the raw .lic file contents (uploaded from the browser, not a path - this app
        // has no way to know what's on the customer's own computer). Validated with the same
        // embedded public key everything else uses before it's ever written to disk, so a
        // garbled upload can't silently clobber a license that was already working.
        app.MapPost("/api/license/install", async (HttpRequest request, AppSettingsFile settings, IConfiguration configuration, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var contents = await reader.ReadToEndAsync(ct);

            var license = LicenseStatus.Verify(contents);
            if (license is null)
                return Results.BadRequest(new { error = "That file doesn't look like a valid license - nothing was changed." });

            var licensePath = await ResolveLicensePathAsync(settings, configuration, ct);
            await File.WriteAllTextAsync(licensePath, contents, ct);

            return Results.Ok(new { valid = true, customerName = license.CustomerName });
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
