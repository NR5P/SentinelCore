extern alias RelayHost;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using SentinelCore.Licensing;
using Xunit;
using RelayProgram = RelayHost::Program;

namespace SentinelCore.Tests;

// The relay's update endpoints only hand files out - the signature that makes an update
// trustworthy is checked by the install, not here. What the relay owns is who gets them (a valid,
// unblocked license) and that no request can reach anything outside the updates folder.
public sealed class RelayUpdateEndpointTests : IDisposable
{
    private readonly string _root;
    private readonly string _updates;
    private readonly RSA _signingKey = RSA.Create(2048);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public RelayUpdateEndpointTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"relay-updates-{Guid.NewGuid():N}");
        _updates = Path.Combine(_root, "updates");
        var release = Path.Combine(_updates, "stable", "linux-x64");
        Directory.CreateDirectory(release);
        File.WriteAllText(Path.Combine(release, "manifest.json"), """{"payload":"x","signature":"y"}""");
        File.WriteAllBytes(Path.Combine(release, "SentinelCore-linux-x64-b11.zip"), Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray());
        File.WriteAllText(Path.Combine(_root, "secret.zip"), "not for you");
    }

    public void Dispose()
    {
        _signingKey.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { TempDirectories.Delete(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Manifest_WithoutALicense_Is401()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/updates/stable/linux-x64", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Manifest_WithALicense_IsServedUncached()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Licensed("/v1/updates/stable/linux-x64"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"signature\"", await response.Content.ReadAsStringAsync(Ct));
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Manifest_ForAChannelWithNothingPublished_Is404()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Licensed("/v1/updates/beta/linux-x64"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Bundle_IsServed_AndResumesFromARange()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var whole = await client.SendAsync(Licensed("/v1/updates/stable/linux-x64/SentinelCore-linux-x64-b11.zip"), Ct);
        Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
        Assert.Equal(1000, (await whole.Content.ReadAsByteArrayAsync(Ct)).Length);

        var resume = Licensed("/v1/updates/stable/linux-x64/SentinelCore-linux-x64-b11.zip");
        resume.Headers.Range = new RangeHeaderValue(900, null);
        using var rest = await client.SendAsync(resume, Ct);
        Assert.Equal(HttpStatusCode.PartialContent, rest.StatusCode);
        var tail = await rest.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal(100, tail.Length);
        Assert.Equal(unchecked((byte)900), tail[0]);
    }

    [Theory]
    [InlineData("/v1/updates/stable/linux-x64/manifest.json")]
    [InlineData("/v1/updates/stable/linux-x64/..%2F..%2Fsecret.zip")]
    [InlineData("/v1/updates/%2E%2E/%2E%2E/secret.zip")]
    [InlineData("/v1/updates/Stable/linux-x64")]
    public async Task AnythingButAPlainNameInsideTheFolder_Is404(string path)
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Licensed(path), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ABlockedLicense_GetsNoUpdates()
    {
        var id = Guid.NewGuid();
        using var factory = CreateFactory(blocked: id);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Licensed("/v1/updates/stable/linux-x64", id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private WebApplicationFactory<RelayProgram> CreateFactory(Guid? blocked = null) =>
        new WebApplicationFactory<RelayProgram>().WithWebHostBuilder(builder =>
        {
            builder
                .UseSetting("Relay:DataDirectory", Path.Combine(_root, "data"))
                .UseSetting("Relay:UpdatesDirectory", _updates)
                .UseSetting("Relay:LicensePublicKeyPem", _signingKey.ExportSubjectPublicKeyInfoPem());
            if (blocked is { } id) builder.UseSetting("Relay:BlockedLicenseIds:0", id.ToString());
        });

    private HttpRequestMessage Licensed(string path, Guid? licenseId = null)
    {
        var license = LicenseSigner.Sign(
            new LicenseInfo("Jane Doe", "jane@example.com", "SentinelCore", licenseId ?? Guid.NewGuid(), DateTimeOffset.UtcNow), _signingKey);
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("X-Sentinel-License", Convert.ToBase64String(Encoding.UTF8.GetBytes(license)));
        return request;
    }
}
