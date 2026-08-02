using Xunit;

namespace SentinelCore.Tests;

// The install scripts have always advertised MAIN_PORT/WEB_PORT and set them through
// ASPNETCORE_URLS, and it has never worked: WebApplicationBuilder adds host configuration before
// appsettings.json, so the file's "Urls" key won and the app bound the default port while its
// service unit claimed another. It stayed invisible because the shipped defaults agree.
public sealed class HostUrlsTests
{
    [Fact]
    public void UsesTheEnvironmentVariableWhenNothingOnTheCommandLineSaysOtherwise()
    {
        Assert.Equal(
            "http://0.0.0.0:6000",
            HostUrls.ResolveOverride([], "http://0.0.0.0:6000"));
    }

    [Fact]
    public void IgnoresAnUnsetOrBlankEnvironmentVariable()
    {
        Assert.Null(HostUrls.ResolveOverride([], null));
        Assert.Null(HostUrls.ResolveOverride([], ""));
        Assert.Null(HostUrls.ResolveOverride([], "   "));
    }

    // Command line still outranks the environment - that ordering is the framework's own, it is
    // what people expect from a shell, and the release smoke test depends on it to pin a port.
    [Theory]
    [InlineData("--Urls", "http://127.0.0.1:59080")]
    [InlineData("--urls", "http://127.0.0.1:59080")]
    [InlineData("--Urls=http://127.0.0.1:59080")]
    [InlineData("/Urls=http://127.0.0.1:59080")]
    public void DefersToAnExplicitCommandLineArgument(params string[] args)
    {
        Assert.Null(HostUrls.ResolveOverride(args, "http://0.0.0.0:6000"));
    }

    // A --Urls sitting among other arguments must still be found.
    [Fact]
    public void FindsTheArgumentAnywhereInTheList()
    {
        string[] args = ["--Https:Enabled", "false", "--Urls", "http://127.0.0.1:59080"];

        Assert.Null(HostUrls.ResolveOverride(args, "http://0.0.0.0:6000"));
    }

    // Guards against matching by accident on an unrelated argument that merely starts similarly.
    [Fact]
    public void DoesNotMistakeAnUnrelatedArgumentForUrls()
    {
        string[] args = ["--UrlsAreNotThis", "x", "--MainAppSettingsPath", "/tmp/a.json"];

        Assert.Equal("http://0.0.0.0:6000", HostUrls.ResolveOverride(args, "http://0.0.0.0:6000"));
    }
}
