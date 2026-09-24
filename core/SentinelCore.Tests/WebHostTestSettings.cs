extern alias WebHost;

using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;

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
    // The main appsettings.json of an install whose admin turned the dashboard login off - every
    // request is a synthetic Admin. A missing Auth section means login required (and, with no
    // accounts yet, a server that does nothing but offer to create the first admin), so a test
    // measuring an endpoint's own behavior rather than the login has to opt out explicitly.
    public const string LoginOffSettings = """{ "Auth": { "Enabled": false } }""";

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
            .UseSetting("InternetExposed:Enabled", "false")
            // Several tests assert that a forward to the main app fails as unreachable. The
            // default main app address is 127.0.0.1:5080, which is unreachable on a development
            // machine and very much reachable on a server that is actually running SentinelCore -
            // there the forward succeeded, the real main app rejected the test's fake API key, and
            // the proxy passed that 401 back, so the tests failed on the one machine that matters.
            .UseSetting("MainAppBaseUrl", $"http://127.0.0.1:{ClosedPort()}");

    // A port nothing is listening on: bound briefly to have the OS name a free one, then released.
    private static int ClosedPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
