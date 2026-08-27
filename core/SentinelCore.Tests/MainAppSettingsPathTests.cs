using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
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

    [Fact]
    public void TheShippedDashboardConfiguration_PointsAtNoParticularMachine()
    {
        var root = RepositoryRoot();
        Assert.SkipWhen(root is null, "The repository sources aren't alongside this build.");

        var shipped = CommittedDashboardConfiguration(root!);
        Assert.SkipWhen(shipped is null, "Git can't say what was committed in this checkout.");

        using var document = JsonDocument.Parse(shipped!);

        // These two used to be absolute paths into one developer's home directory, which meant the
        // dashboard only found the main app on the machine the file was last edited on - and on
        // Windows resolved "/home/..." to C:\home\... rather than failing outright. Relative is
        // what makes the same file correct on all three platforms, so it is worth keeping a test
        // between here and the next person who pastes a working absolute path in.
        foreach (var (path, value) in ShippedPaths(document.RootElement))
        {
            Assert.False(
                Path.IsPathRooted(value),
                $"{path} in the shipped dashboard configuration is the absolute path \"{value}\", " +
                "which only resolves on the machine it was written on.");
        }
    }

    // What HEAD says core/SentinelCore.Web/appsettings.json contains - deliberately not what is
    // sitting in the working tree.
    //
    // "Shipped" means committed. On an installed machine that file is live config rather than a
    // fixture: the checkout is the one the server runs from, and its DataDirectory and
    // MainAppSettingsPath are absolute paths into the install, which is correct there and is
    // exactly what this test calls a mistake. Read from the working tree, the test therefore
    // failed permanently on the box the server runs on while passing everywhere else - and
    // deploy.sh runs under `set -euo pipefail`, so it aborted the deploy before the restart step
    // and left fresh bin/Release binaries the services never loaded. Same shape as the fix in
    // BackupCoverageTests.TheShippedConfiguration_StillExportsAndImportsCleanly.
    //
    // Asking git instead keeps the guard doing its real job - catching an absolute path on its
    // way into a commit - on the install box as well as on a developer's machine, rather than
    // being skipped there.
    private static string? CommittedDashboardConfiguration(string repositoryRoot)
    {
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", "show HEAD:core/SentinelCore.Web/appsettings.json")
            {
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (git is null) return null;

            // Read before waiting: a blocked pipe would otherwise deadlock the wait it is waiting on.
            var contents = git.StandardOutput.ReadToEnd();
            if (!git.WaitForExit(TimeSpan.FromSeconds(30))) return null;

            // Non-zero covers the cases worth tolerating rather than failing on: no git on PATH is
            // caught below, but a source export with no .git, or a repository with no commits yet,
            // land here.
            return git.ExitCode == 0 ? contents : null;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            // No git installed, or nothing usable to run it against.
            return null;
        }
    }

    private static IEnumerable<(string Path, string Value)> ShippedPaths(JsonElement root)
    {
        if (root.TryGetProperty("MainAppSettingsPath", out var settings) &&
            settings.ValueKind == JsonValueKind.String)
        {
            yield return ("MainAppSettingsPath", settings.GetString()!);
        }

        if (root.TryGetProperty("Bridge", out var bridge) &&
            bridge.ValueKind == JsonValueKind.Object &&
            bridge.TryGetProperty("DataDirectory", out var dataDirectory) &&
            dataDirectory.ValueKind == JsonValueKind.String)
        {
            yield return ("Bridge:DataDirectory", dataDirectory.GetString()!);
        }
    }

    private static string? RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "core"))) return directory.FullName;
            directory = directory.Parent;
        }
        return null;
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
