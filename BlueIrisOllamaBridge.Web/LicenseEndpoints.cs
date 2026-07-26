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
            var licenseSettings = await settings.GetSectionAsync<LicenseSettingsDto>("License", ct) ?? new LicenseSettingsDto();
            var mainAppSettingsPath = configuration["MainAppSettingsPath"]
                ?? throw new InvalidOperationException("MainAppSettingsPath is not configured.");
            var mainAppDirectory = Path.GetDirectoryName(Path.GetFullPath(mainAppSettingsPath))!;
            var licensePath = Path.IsPathRooted(licenseSettings.FilePath)
                ? licenseSettings.FilePath
                : Path.Combine(mainAppDirectory, licenseSettings.FilePath);

            var license = LicenseStatus.Check(licensePath);
            return Results.Ok(license is null
                ? new { valid = false }
                : new { valid = true, customerName = license.CustomerName });
        });
    }
}
