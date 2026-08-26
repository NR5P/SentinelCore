namespace SentinelCore.Web;

// Where the main app's appsettings.json lives, as seen from the dashboard.
//
// The dashboard and the main app are installed as siblings on every platform - see the systemd
// units, the LaunchDaemon plists and install-windows.ps1, all of which lay out
// <install>/SentinelCore and <install>/SentinelCore.Web - so the default is written relative to
// this app's content root and resolves to the same place on all three.
//
// An absolute value still wins, for an install that puts them somewhere else.
public static class MainAppSettingsPath
{
    public const string ConfigurationKey = "MainAppSettingsPath";

    /// <summary>Resolves the configured path against this app's content root.</summary>
    /// <remarks>
    /// Deliberately not Path.GetFullPath on its own: that resolves a relative path against the
    /// current working directory, which is the app's own folder under systemd and launchd but is
    /// System32 for a Windows service. Same configuration, silently different file.
    /// </remarks>
    public static string Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration[ConfigurationKey]
            ?? throw new InvalidOperationException($"{ConfigurationKey} is not configured.");

        return Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured));
    }
}
