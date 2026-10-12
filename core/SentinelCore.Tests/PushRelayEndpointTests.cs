extern alias RelayHost;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using SentinelCore.Licensing;
using Xunit;
using RelayProgram = RelayHost::Program;

namespace SentinelCore.Tests;

// The relay's whole job is the two checks around one forwarded HTTP call: is this a real license,
// and does that license own this token. Both are exercised end to end here with a throwaway
// signing key and a fake APNs, so the tests prove the real middleware, store and sender wiring
// rather than a mock of each.
public sealed class PushRelayEndpointTests : IDisposable
{
    private readonly string _dataDirectory;
    private readonly string _apnsKeyPath;
    private readonly RSA _signingKey;
    private readonly string _publicKeyPem;

    public PushRelayEndpointTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"push-relay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);

        using var apnsKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _apnsKeyPath = Path.Combine(_dataDirectory, "AuthKey_TEST.p8");
        File.WriteAllText(_apnsKeyPath, apnsKey.ExportPkcs8PrivateKeyPem());

        _signingKey = RSA.Create(2048);
        _publicKeyPem = _signingKey.ExportSubjectPublicKeyInfoPem();
    }

    public void Dispose()
    {
        _signingKey.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { TempDirectories.Delete(_dataDirectory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Health_NeedsNoLicense_AndSaysWhichPlatformsAreServed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory(new RecordingHandler(_ => (HttpStatusCode.OK, "{}")));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
        Assert.True(body.GetProperty("apple").GetBoolean());
        Assert.False(body.GetProperty("firebase").GetBoolean());
    }

    [Fact]
    public async Task V1_WithoutALicense_Is401()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, "{}"));
        using var factory = CreateFactory(handler);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/v1/push", PushBody("tok"), ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(handler.Requests);
    }

    // Signed with a different key entirely - the shape is perfect, only the signature is wrong.
    [Fact]
    public async Task V1_WithAForgedLicense_Is401()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, "{}"));
        using var factory = CreateFactory(handler);
        using var client = factory.CreateClient();
        using var otherKey = RSA.Create(2048);
        var forged = LicenseSigner.Sign(new LicenseInfo("Mallory", "m@example.com", "SentinelCore", Guid.NewGuid(), DateTimeOffset.UtcNow), otherKey);

        using var response = await client.PostAsJsonAsync("/v1/devices", RegisterBody("tok"), ct, forged);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RegisterThenPush_SignsAndForwardsToApns_InTheTokensEnvironment()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, "{}"));
        using var factory = CreateFactory(handler);
        using var client = factory.CreateClient();
        var license = Issue("Jane Doe");

        using var register = await client.PostAsJsonAsync("/v1/devices", RegisterBody("tok-1", sandbox: true), ct, license);
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);

        using var push = await client.PostAsJsonAsync("/v1/push", PushBody("tok-1"), ct, license);
        Assert.Equal(HttpStatusCode.OK, push.StatusCode);
        Assert.Equal("delivered", (await push.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("status").GetString());

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("api.sandbox.push.apple.com", sent.Uri.Host);
        Assert.Equal("/3/device/tok-1", sent.Uri.AbsolutePath);
        Assert.Equal("bearer", sent.AuthorizationScheme);
        Assert.False(string.IsNullOrEmpty(sent.AuthorizationParameter));
        Assert.Equal("com.sentinelcore.client", sent.Headers["apns-topic"]);
        Assert.Contains("Front door", sent.Body);
    }

    [Fact]
    public async Task Push_ProductionTokenAndTvVariant_UsesProductionHostAndTvTopic()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, "{}"));
        using var factory = CreateFactory(handler);
        using var client = factory.CreateClient();
        var license = Issue("Jane Doe");

        using var register = await client.PostAsJsonAsync("/v1/devices", RegisterBody("tv-1", variant: "tvos", sandbox: false), ct, license);
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        using var push = await client.PostAsJsonAsync("/v1/push", PushBody("tv-1", variant: "tvos"), ct, license);
        Assert.Equal(HttpStatusCode.OK, push.StatusCode);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("api.push.apple.com", sent.Uri.Host);
        Assert.Equal("com.sentinelcore.client.tv", sent.Headers["apns-topic"]);
    }

    // The isolation property the whole design rests on: a license can only ever push to tokens it
    // registered. Someone else's token is indistinguishable from one that doesn't exist.
    [Fact]
    public async Task Push_ToATokenAnotherLicenseRegistered_Is404AndSendsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, "{}"));
        using var factory = CreateFactory(handler);
        using var client = factory.CreateClient();
        var alice = Issue("Alice");
        var bob = Issue("Bob");

        using var register = await client.PostAsJsonAsync("/v1/devices", RegisterBody("alice-phone"), ct, alice);
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);

        using var push = await client.PostAsJsonAsync("/v1/push", PushBody("alice-phone"), ct, bob);
        Assert.Equal(HttpStatusCode.NotFound, push.StatusCode);
        Assert.Empty(handler.Requests);

        // Nor can Bob take the token over while Alice's registration is fresh.
        using var steal = await client.PostAsJsonAsync("/v1/devices", RegisterBody("alice-phone"), ct, bob);
        Assert.Equal(HttpStatusCode.Conflict, steal.StatusCode);

        // Bob unregistering it is a no-op, not a removal.
        using var remove = await client.PostAsJsonAsync("/v1/devices/unregister", new { platform = "apple", token = "alice-phone" }, ct, bob);
        Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
        using var stillAlices = await client.PostAsJsonAsync("/v1/push", PushBody("alice-phone"), ct, alice);
        Assert.Equal(HttpStatusCode.OK, stillAlices.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Push_WhenApnsSaysUnregistered_ReportsItAndForgetsTheToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(_ => (HttpStatusCode.Gone, """{"reason":"Unregistered"}"""));
        using var factory = CreateFactory(handler);
        using var client = factory.CreateClient();
        var license = Issue("Jane Doe");

        using var register = await client.PostAsJsonAsync("/v1/devices", RegisterBody("stale"), ct, license);
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);

        using var push = await client.PostAsJsonAsync("/v1/push", PushBody("stale"), ct, license);
        Assert.Equal(HttpStatusCode.OK, push.StatusCode);
        Assert.Equal("unregistered", (await push.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("status").GetString());

        using var again = await client.PostAsJsonAsync("/v1/push", PushBody("stale"), ct, license);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ABlockedLicense_IsRefusedEverything_AndNothingReachesApple()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, "{}"));
        var (blockedId, blocked) = IssueWithId("Leaked Copy");
        using var factory = CreateFactory(handler, blockedLicenseIds: [blockedId.ToString()]);
        using var client = factory.CreateClient();

        using var register = await client.PostAsJsonAsync("/v1/devices", RegisterBody("leaked-phone"), ct, blocked);
        using var push = await client.PostAsJsonAsync("/v1/push", PushBody("leaked-phone"), ct, blocked);

        // 403, not 404: Core re-registers and retries after a 404, which would loop.
        Assert.Equal(HttpStatusCode.Forbidden, register.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, push.StatusCode);
        var body = JsonDocument.Parse(await push.Content.ReadAsStringAsync(ct)).RootElement;
        Assert.Equal(RelayHost::SentinelCore.PushRelay.LicenseAuthMiddleware.BlockedMessage, body.GetProperty("error").GetString());
        Assert.Empty(handler.Requests);
    }

    // The whole point: a license that was already working stops, its phones stop receiving, and
    // the tokens it held are free at once rather than after StaleTokenTakeoverDays.
    [Fact]
    public async Task BlockingALicense_TakesEffectWithoutARestart_AndFreesItsDevices()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, "{}"));
        var (sharedId, shared) = IssueWithId("Shared Around");
        var owner = Issue("Real Owner");
        var bystander = Issue("Someone Else");
        using var factory = CreateFactory(handler);
        using var client = factory.CreateClient();

        using (var r = await client.PostAsJsonAsync("/v1/devices", RegisterBody("phone-1"), ct, shared))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var r = await client.PostAsJsonAsync("/v1/devices", RegisterBody("bystander-phone"), ct, bystander))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var taken = await client.PostAsJsonAsync("/v1/devices", RegisterBody("phone-1"), ct, owner))
            Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);

        // What editing appsettings.json on the box does: the host reloads its configuration.
        var configuration = (IConfigurationRoot)factory.Services.GetRequiredService<IConfiguration>();
        configuration.Providers.OfType<MemoryConfigurationProvider>().Last().Set("Relay:BlockedLicenseIds:0", sharedId.ToString("N"));
        configuration.Reload();

        using (var refused = await client.PostAsJsonAsync("/v1/push", PushBody("phone-1"), ct, shared))
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Empty(handler.Requests);

        // Its registration is gone, so the phone's real owner can claim it straight away...
        using (var claimed = await client.PostAsJsonAsync("/v1/devices", RegisterBody("phone-1"), ct, owner))
            Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        // ...and nobody else was touched.
        using (var unaffected = await client.PostAsJsonAsync("/v1/push", PushBody("bystander-phone"), ct, bystander))
            Assert.Equal(HttpStatusCode.OK, unaffected.StatusCode);
    }

    [Fact]
    public async Task AnUnreadableBlockedEntry_IsIgnored_RatherThanTakingTheRelayDown()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory(new RecordingHandler(_ => (HttpStatusCode.OK, "{}")), blockedLicenseIds: ["not-a-license-id"]);
        using var client = factory.CreateClient();

        using var register = await client.PostAsJsonAsync("/v1/devices", RegisterBody("fine"), ct, Issue("Jane Doe"));

        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
    }

    [Fact]
    public async Task Register_PastTheDeviceLimit_Is429()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory(new RecordingHandler(_ => (HttpStatusCode.OK, "{}")), maxDevices: 2);
        using var client = factory.CreateClient();
        var license = Issue("Jane Doe");

        using var first = await client.PostAsJsonAsync("/v1/devices", RegisterBody("d1"), ct, license);
        using var second = await client.PostAsJsonAsync("/v1/devices", RegisterBody("d2"), ct, license);
        using var third = await client.PostAsJsonAsync("/v1/devices", RegisterBody("d3"), ct, license);
        // Re-registering an existing token is a refresh, never counted against the limit.
        using var refresh = await client.PostAsJsonAsync("/v1/devices", RegisterBody("d1"), ct, license);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
    }

    [Fact]
    public async Task Register_ForAPlatformTheRelayDoesNotServe_Is503()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory(new RecordingHandler(_ => (HttpStatusCode.OK, "{}")));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/v1/devices", new { platform = "android", token = "fid-1", appVariant = "ios", apnsSandbox = false }, ct, Issue("Jane Doe"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private WebApplicationFactory<RelayProgram> CreateFactory(HttpMessageHandler handler, int maxDevices = 25, string[]? blockedLicenseIds = null) =>
        new WebApplicationFactory<RelayProgram>().WithWebHostBuilder(builder => builder
            // Last, so it outranks the committed appsettings.json; also the provider the reload
            // test edits.
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(
                (blockedLicenseIds ?? []).Select((id, index) => new KeyValuePair<string, string?>($"Relay:BlockedLicenseIds:{index}", id))))
            .UseSetting("Relay:DataDirectory", _dataDirectory)
            .UseSetting("Relay:LicensePublicKeyPem", _publicKeyPem)
            .UseSetting("Relay:MaxDevicesPerLicense", maxDevices.ToString())
            .UseSetting("Relay:Apple:Enabled", "true")
            .UseSetting("Relay:Apple:TeamId", "TEAM123456")
            .UseSetting("Relay:Apple:KeyId", "KEY1234567")
            .UseSetting("Relay:Apple:BundleId", "com.sentinelcore.client")
            .UseSetting("Relay:Apple:TvBundleId", "com.sentinelcore.client.tv")
            .UseSetting("Relay:Apple:PrivateKeyPath", _apnsKeyPath)
            .UseSetting("Relay:Firebase:Enabled", "false")
            .ConfigureTestServices(services =>
                services.AddSingleton<IHttpClientFactory>(new SingleClientFactory(handler))));

    private string Issue(string customer) => IssueWithId(customer).License;

    private (Guid Id, string License) IssueWithId(string customer)
    {
        var id = Guid.NewGuid();
        return (id, LicenseSigner.Sign(new LicenseInfo(customer, "c@example.com", "SentinelCore", id, DateTimeOffset.UtcNow), _signingKey));
    }

    private static object RegisterBody(string token, string variant = "ios", bool sandbox = false) =>
        new { platform = "apple", token, appVariant = variant, apnsSandbox = sandbox };

    private static object PushBody(string token, string variant = "ios") => new
    {
        platform = "apple",
        token,
        appVariant = variant,
        message = new { title = "Front door", body = "A person is at the door.", eventId = "evt-1", camera = "Front door", eventType = "person" }
    };

    internal sealed record RecordedRequest(
        Uri Uri, string? AuthorizationScheme, string? AuthorizationParameter, Dictionary<string, string> Headers, string Body);

    internal sealed class RecordingHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> reply) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.RequestUri!,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                headers,
                body));
            var (status, responseBody) = reply(request);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    internal sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

internal static class LicensedHttpClientExtensions
{
    // The license travels as one header, base64 of the .lic file's bytes - exactly what
    // PushRelayClient sends.
    public static Task<HttpResponseMessage> PostAsJsonAsync<T>(
        this HttpClient client, string path, T body, CancellationToken cancellationToken, string licenseFileContents)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("X-Sentinel-License", Convert.ToBase64String(Encoding.UTF8.GetBytes(licenseFileContents)));
        return client.SendAsync(request, cancellationToken);
    }
}

// The bare domain and /privacy are served by the relay because it is the vendor's only public
// process; both stores link to the privacy page, so it must never 404 or come out empty.
public sealed class PublicPagesTests
{
    [Theory]
    [InlineData("/", "Sentinel Core")]
    [InlineData("/privacy", "Privacy Policy")]
    public async Task PublicPage_RendersWithoutALicense(string path, string expectedText)
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new WebApplicationFactory<RelayProgram>()
            .WithWebHostBuilder(b => b.UseSetting("Relay:DataDirectory", Path.Combine(Path.GetTempPath(), $"relay-pages-{Guid.NewGuid():N}")));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(expectedText, await response.Content.ReadAsStringAsync(ct));
    }

    // The website is deployed into the site folder while the relay is running, so / and its
    // assets must pick it up without a restart - and /privacy must stay the repo's policy.
    [Fact]
    public async Task DeployedSite_ServesItsIndexAndAssets_ButNotPrivacy()
    {
        var ct = TestContext.Current.CancellationToken;
        var site = Path.Combine(Path.GetTempPath(), $"relay-site-{Guid.NewGuid():N}");
        using var factory = new WebApplicationFactory<RelayProgram>()
            .WithWebHostBuilder(b => b
                .UseSetting("Relay:DataDirectory", Path.Combine(Path.GetTempPath(), $"relay-pages-{Guid.NewGuid():N}"))
                .UseSetting("Relay:SiteDirectory", site));
        using var client = factory.CreateClient();
        Assert.Contains("Sentinel Core", await client.GetStringAsync("/", ct));

        await File.WriteAllTextAsync(Path.Combine(site, "index.html"), "<h1>deployed site</h1>", ct);
        await File.WriteAllTextAsync(Path.Combine(site, "style.css"), "body{}", ct);

        Assert.Contains("deployed site", await client.GetStringAsync("/", ct));
        using var css = await client.GetAsync("/style.css", ct);
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Contains("Privacy Policy", await client.GetStringAsync("/privacy", ct));
    }
}
