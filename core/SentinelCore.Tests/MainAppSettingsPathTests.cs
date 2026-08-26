using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The dashboard has to find the main app's appsettings.json, and the two are installed as
// siblings on all three platforms. What makes this worth its own tests is that the obvious
// implementation - Path.GetFullPath on the configured value - happens to work on Linux and macOS
// for the wrong reason: the current working directory is the app's own folder under systemd and
// launchd, and System32 for a Windows service.
public class MainAppSettingsPathTests
{
    [Fact]
    public void Resolve_WithARelativeValue_ResolvesAgainstTheContentRootNotTheWorkingDirectory()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), $"web-{Guid.NewGuid():N}");

        var resolved = MainAppSettingsPath.Resolve(
            Configuration("../SentinelCore/appsettings.json"), new FakeHostEnvironment(contentRoot));

        var expected = Path.GetFullPath(Path.Combine(contentRoot, "../SentinelCore/appsettings.json"));
        Assert.Equal(expected, resolved);

        // The distinction the whole helper exists for: the answer must not depend on where the
        // process happens to have been started from.
        Assert.NotEqual(Path.GetFullPath("../SentinelCore/appsettings.json"), resolved);
    }

    [Fact]
    public void Resolve_WithAnAbsoluteValue_LeavesItAlone()
    {
        // An install that puts the two components somewhere other than side by side configures an
        // absolute path, and that has to keep overriding the default.
        var absolute = OperatingSystem.IsWindows()
            ? @"D:\sentinel\SentinelCore\appsettings.json"
            : "/opt/sentinel/SentinelCore/appsettings.json";

        var resolved = MainAppSettingsPath.Resolve(
            Configuration(absolute), new FakeHostEnvironment(Path.GetTempPath()));

        Assert.Equal(absolute, resolved);
    }

    [Fact]
    public void Resolve_WithNothingConfigured_SaysWhichSettingIsMissing()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            MainAppSettingsPath.Resolve(Configuration(null), new FakeHostEnvironment(Path.GetTempPath())));

        Assert.Contains("MainAppSettingsPath", error.Message);
    }

    private static IConfiguration Configuration(string? value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MainAppSettingsPath"] = value })
            .Build();

    private sealed class FakeHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Test";
    }
}
