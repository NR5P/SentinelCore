extern alias WebHost;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SentinelCore.Tests;

// UseSetting() alone is not enough to point a test host at a scratch settings file.
//
// The Web project ships a MainAppSettingsPath in its own appsettings.json, and WebApplicationFactory
// serves that file from the project's content root - so appsettings.json is loaded *after* the
// UseSetting values and quietly wins. The host then reads whatever main appsettings.json that path
// happens to name on this machine.
//
// That turns every such test into a test of the developer's live configuration. It showed up as
// three tests that pass on a laptop and fail on the deployed server: the server's path resolves to
// its real config, where Auth is enabled, so requests come back 401 instead of reaching the
// endpoint under test. The laptop's path pointed at a directory that doesn't exist there, no Auth
// section was found, AuthGateMiddleware substituted a synthetic Admin, and everything passed.
//
// AddInMemoryCollection during ConfigureAppConfiguration is appended last, so these values outrank
// the file for real.
internal static class WebHostTestSettings
{
    public static IWebHostBuilder UseTestSettings(
        this IWebHostBuilder builder, string mainAppSettingsPath, string dataDirectory) =>
        builder
            .UseSetting("MainAppSettingsPath", mainAppSettingsPath)
            .UseSetting("CamerasDataDirectory", dataDirectory)
            .UseSetting("Bridge:DataDirectory", dataDirectory)
            // Read from the Web project's own appsettings.json, so on a real install this is
            // whatever that machine has switched on. Requests arriving on the Internet Exposed
            // port always require login, and pinning it off here keeps these tests measuring the
            // endpoint's own policy rather than the host's deployment settings.
            .UseSetting("InternetExposed:Enabled", "false");
}
