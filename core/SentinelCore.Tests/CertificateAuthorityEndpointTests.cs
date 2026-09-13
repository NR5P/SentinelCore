extern alias WebHost;

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using SentinelCore.Web;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// The certificate-authority download exists so a device can trust the dashboard's HTTPS without
// anyone copying ca.crt off the server - the webcam publish page depends on it, because a browser
// refuses the camera to a page whose certificate it doesn't trust. What these pin: the file
// served is the authority certificate and nothing else (never the key beside it), it is served
// without a login (a CA certificate is public by design, and the device fetching it is the one
// that can't yet get a trusted page), and an install with no local authority answers 404 rather
// than serving something else.
public sealed class CertificateAuthorityEndpointTests : IDisposable
{
    private readonly string _dataDirectory;
    private readonly string _settingsPath;

    public CertificateAuthorityEndpointTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"web-ca-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);
        _settingsPath = Path.Combine(_dataDirectory, "appsettings.json");
        File.WriteAllText(_settingsPath, "{}");
    }

    public void Dispose()
    {
        try { TempDirectories.Delete(_dataDirectory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private WebApplicationFactory<WebProgram> CreateFactory(string? authorityPath) => new WebApplicationFactory<WebProgram>()
        .WithWebHostBuilder(builder =>
        {
            builder
                .UseSetting("MainAppSettingsPath", _settingsPath)
                .UseSetting("CamerasDataDirectory", _dataDirectory)
                .UseSetting("Bridge:DataDirectory", _dataDirectory)
                // The test host has no HTTPS listener, so Program.cs never provisions an authority
                // of its own; the path is planted the way Program.cs would have recorded it.
                .UseSetting("Https:Enabled", "false");
            if (authorityPath is not null)
                builder.UseSetting(CertificateAuthorityEndpoints.ConfigurationKey, authorityPath);
        });

    [Fact]
    public async Task ServesTheAuthorityCertificate_AsADownload_WithoutALogin()
    {
        var ct = TestContext.Current.CancellationToken;
        const string pem = "-----BEGIN CERTIFICATE-----\nMIIBfake\n-----END CERTIFICATE-----\n";
        var certPath = Path.Combine(_dataDirectory, "ca.crt");
        await File.WriteAllTextAsync(certPath, pem, ct);
        // The private key lives beside the certificate on a real install; it must never be what
        // comes back, however the two are confused.
        await File.WriteAllTextAsync(Path.Combine(_dataDirectory, "ca.key"), "-----BEGIN PRIVATE KEY-----\nSECRET\n", ct);

        using var factory = CreateFactory(certPath);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/certificate-authority", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(CertificateAuthorityEndpoints.ContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(CertificateAuthorityEndpoints.DownloadFileName, response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal(pem, body);
        Assert.DoesNotContain("PRIVATE KEY", body);
    }

    [Fact]
    public async Task StaysOpen_WhenLoginIsRequired()
    {
        // Auth:Enabled is read live from the main app's settings file - see AuthGateMiddleware.
        // With it on, an unauthenticated /api call is normally a 401; this one must not be, or a
        // device that can't yet load a trusted page could never get the file that makes one.
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(_settingsPath, """{ "Auth": { "Enabled": true } }""", ct);
        var certPath = Path.Combine(_dataDirectory, "ca.crt");
        await File.WriteAllTextAsync(certPath, "-----BEGIN CERTIFICATE-----\nMIIBfake\n-----END CERTIFICATE-----\n", ct);

        using var factory = CreateFactory(certPath);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/certificate-authority", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AnswersHead_SoThePublishPageCanCheckBeforeOfferingTheLink()
    {
        var ct = TestContext.Current.CancellationToken;
        var certPath = Path.Combine(_dataDirectory, "ca.crt");
        await File.WriteAllTextAsync(certPath, "-----BEGIN CERTIFICATE-----\nMIIBfake\n-----END CERTIFICATE-----\n", ct);

        using var factory = CreateFactory(certPath);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Head, "/api/certificate-authority");
        using var response = await client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("missing")]
    public async Task IsNotFound_WhenTheInstallHasNoLocalAuthority(string? kind)
    {
        // Two ways there is nothing to serve: HTTPS on a real-domain certificate, which never
        // provisions an authority and so never records a path (null); or a recorded path whose
        // file has since been removed.
        var ct = TestContext.Current.CancellationToken;
        var authorityPath = kind is null ? null : Path.Combine(_dataDirectory, "gone.crt");

        using var factory = CreateFactory(authorityPath);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/certificate-authority", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
