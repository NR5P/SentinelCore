extern alias WebHost;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// Version stamping exists so that "which build is this?" is never a guess during support. These
// tests cover the two ways that can silently break: the parser misreading what the build stamps
// into AssemblyInformationalVersion, and the /api/health JSON contract drifting away from the
// field names the dashboard footer reads.
public sealed class BuildInfoTests : IDisposable
{
    private readonly string _dataDirectory;
    private readonly string _settingsPath;

    public BuildInfoTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"build-info-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);
        _settingsPath = Path.Combine(_dataDirectory, "appsettings.json");
        File.WriteAllText(_settingsPath, "{}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Parse_VersionWithCommit_SplitsBoth()
    {
        var build = BuildVersion.Parse("1.4.2+a1b2c3d4e5f6");

        Assert.Equal("1.4.2", build.Version);
        Assert.Equal("a1b2c3d4e5f6", build.Commit);
        Assert.False(build.IsDirty);
        Assert.True(build.HasCommit);
        Assert.Equal("1.4.2 (a1b2c3d4e5f6)", build.Display);
    }

    [Fact]
    public void Parse_DirtyBuild_KeepsCommitAndFlagsIt()
    {
        var build = BuildVersion.Parse("1.4.2+a1b2c3d4e5f6.dirty");

        // The commit still has to come back cleanly - the flag says the working tree had edits
        // on top of it, not that the commit itself is unknown.
        Assert.Equal("a1b2c3d4e5f6", build.Commit);
        Assert.True(build.IsDirty);
        Assert.Equal("1.4.2 (a1b2c3d4e5f6, modified)", build.Display);
    }

    [Fact]
    public void Parse_NoBuildMetadata_ReportsUnknownCommit()
    {
        // What a build from an extracted source tarball looks like: Directory.Build.props skips
        // the git stamp when there's no .git, so the version arrives with no "+" section at all.
        var build = BuildVersion.Parse("1.4.2");

        Assert.Equal("1.4.2", build.Version);
        Assert.Equal(BuildVersion.UnknownCommit, build.Commit);
        Assert.False(build.HasCommit);
        Assert.False(build.IsDirty);
        Assert.Equal("1.4.2 (commit unknown)", build.Display);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+a1b2c3d4e5f6")]
    public void Parse_MissingOrMalformed_DegradesInsteadOfThrowing(string? informationalVersion)
    {
        // A diagnostic aid must never be the thing that stops a process starting, so anything
        // unparseable has to come back as a usable record rather than an exception.
        var build = BuildVersion.Parse(informationalVersion);

        Assert.False(string.IsNullOrWhiteSpace(build.Version));
        Assert.False(string.IsNullOrWhiteSpace(build.Commit));
        Assert.False(string.IsNullOrWhiteSpace(build.Display));
    }

    [Fact]
    public void Current_IsPopulated()
    {
        // Deliberately not asserting a specific version: under the test host the entry assembly
        // is the runner, not either of the two apps, so the value here isn't the shipped one.
        // What matters is that reading the attribute back works at all and never returns null.
        Assert.NotNull(BuildInfo.Current);
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.Current.Version));
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.Current.Display));
    }

    [Fact]
    public async Task Health_ReportsBuild_WithTheFieldNamesTheFooterReads()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new WebApplicationFactory<WebProgram>()
            .WithWebHostBuilder(builder => builder
                .UseSetting("MainAppSettingsPath", _settingsPath)
                .UseSetting("CamerasDataDirectory", _dataDirectory)
                .UseSetting("Bridge:DataDirectory", _dataDirectory));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/health", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        var build = payload["build"];

        // app.js reads build.display and build.isDirty by these exact camelCase names. Renaming
        // a property on BuildVersion would blank the dashboard footer with nothing failing
        // anywhere else, so the contract is pinned here.
        Assert.NotNull(build);
        Assert.False(string.IsNullOrWhiteSpace(build!["display"]?.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(build["version"]?.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(build["commit"]?.GetValue<string>()));
        Assert.NotNull(build["isDirty"]);

        // Excluded via [JsonIgnore] - internal branching helper, not part of the wire contract.
        Assert.Null(build["hasCommit"]);
    }
}
