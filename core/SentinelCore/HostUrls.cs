namespace SentinelCore;

// Makes ASPNETCORE_URLS actually decide the listening address.
//
// Every shipped service definition sets the port this way - the systemd units, the launchd
// plists, the Windows service registry entry, and the MAIN_PORT/WEB_PORT options the install
// scripts advertise. None of it worked. WebApplicationBuilder adds the ASPNETCORE_-prefixed host
// configuration *before* the appsettings.json provider, so the "Urls" key in appsettings.json
// silently outranked the environment variable, and the app bound whatever the file said. It went
// unnoticed because the shipped defaults agree: appsettings says 5080/3320 and so do the units.
// Anyone who moved a port - because something else already had 5080, the usual reason - got a
// service unit claiming one port, a server listening on another, and a failing health check.
//
// Command-line arguments still win over both, which is how the release smoke test pins a port and
// how anyone can override this from a shell without editing anything.
public static class HostUrls
{
    public const string ConfigurationKey = "Urls";
    public const string EnvironmentVariable = "ASPNETCORE_URLS";

    // Returns the value to force into configuration, or null to leave configuration alone.
    public static string? ResolveOverride(string[] args, string? environmentValue)
    {
        if (string.IsNullOrWhiteSpace(environmentValue)) return null;
        // Covers "--Urls http://...", "--Urls=http://..." and the "/Urls=..." form the
        // command-line configuration provider also accepts.
        if (args.Any(IsUrlsArgument)) return null;
        return environmentValue;
    }

    private static bool IsUrlsArgument(string argument) =>
        MatchesKey(argument, "--") || MatchesKey(argument, "/");

    // Exact match or "key=value" - deliberately not a bare prefix test, which would also swallow
    // any future argument that merely happens to begin with the same letters.
    private static bool MatchesKey(string argument, string prefix) =>
        argument.Equals($"{prefix}{ConfigurationKey}", StringComparison.OrdinalIgnoreCase)
        || argument.StartsWith($"{prefix}{ConfigurationKey}=", StringComparison.OrdinalIgnoreCase);
}
