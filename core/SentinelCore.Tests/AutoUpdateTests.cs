extern alias WebHost;

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// Automatic updates, dashboard half: everything up to dropping pending.json for the root
// installer. The promises tested are the ones that matter on a stranger's box at 4am - nothing
// unsigned or corrupted is ever staged, nothing is staged while the box is busy, a build that
// rolled back isn't retried every night, and the owner hears about each install exactly once.
public sealed class AutoUpdateTests : IDisposable
{
    private const string Channel = "stable";
    private const string Platform = "linux-x64";
    private const int CurrentBuild = 10;

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _root;
    private readonly string _installRoot;
    private readonly string _configFile;
    private readonly string _settingsPath;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public AutoUpdateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"auto-update-{Guid.NewGuid():N}");
        _installRoot = Path.Combine(_root, "install");
        Directory.CreateDirectory(Path.Combine(_installRoot, "SentinelCore.Web"));
        _configFile = Path.Combine(_root, "update.conf");
        File.WriteAllText(_configFile, "INSTALL_DIR=x\n");
        _settingsPath = Path.Combine(_root, "appsettings.json");
        File.WriteAllText(_settingsPath, "{}");
    }

    public void Dispose()
    {
        _key.Dispose();
        try { TempDirectories.Delete(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- manifest verification ---------------------------------------------------------------

    [Fact]
    public void Verify_AcceptsACorrectlySignedManifest()
    {
        var manifest = UpdateManifestVerifier.Verify(SignedManifest(Payload(build: 11)), PublicKey(), Channel, Platform, out var error);

        Assert.NotNull(manifest);
        Assert.Equal("", error);
        Assert.Equal(11, manifest.Build);
    }

    [Fact]
    public void Verify_RefusesAPayloadEditedAfterSigning()
    {
        var payload = Payload(build: 11);
        var signed = JsonSerializer.Deserialize<JsonElement>(SignedManifest(payload));
        var edited = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(payload).Replace("\"build\":11", "\"build\":99"));
        var forged = JsonSerializer.Serialize(new { payload = Convert.ToBase64String(edited), signature = signed.GetProperty("signature").GetString() });

        Assert.Null(UpdateManifestVerifier.Verify(forged, PublicKey(), Channel, Platform, out var error));
        Assert.Contains("signature", error);
    }

    [Fact]
    public void Verify_RefusesAManifestSignedWithAnotherKey()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = Payload(build: 11);
        var manifest = JsonSerializer.Serialize(new
        {
            payload = Convert.ToBase64String(payload),
            signature = Convert.ToBase64String(other.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
        });

        Assert.Null(UpdateManifestVerifier.Verify(manifest, PublicKey(), Channel, Platform, out _));
    }

    // A genuine arm64 manifest would install cleanly on an x64 box and then never start.
    [Fact]
    public void Verify_RefusesAGenuineManifestForAnotherPlatform()
    {
        var manifest = SignedManifest(Payload(build: 11, platform: "linux-arm64"));

        Assert.Null(UpdateManifestVerifier.Verify(manifest, PublicKey(), Channel, Platform, out var error));
        Assert.Contains("linux-arm64", error);
    }

    [Theory]
    [InlineData("../evil.zip")]
    [InlineData("a/b.zip")]
    [InlineData(".hidden.zip")]
    [InlineData("bundle.tar")]
    public void Verify_RefusesAFileNameThatIsNotAPlainZipName(string file)
    {
        var manifest = SignedManifest(Payload(build: 11, file: file));

        Assert.Null(UpdateManifestVerifier.Verify(manifest, PublicKey(), Channel, Platform, out _));
    }

    // ---- install detection ---------------------------------------------------------------------

    [Fact]
    public void Install_FromSource_IsNotSupported_AndSaysWhy()
    {
        var service = CreateService(new FakeUpdateServer(), currentBuild: 0);

        Assert.False(service.Install.Supported);
        Assert.Contains("git", service.Install.UnsupportedReason);
    }

    [Fact]
    public void Install_WithoutTheRootUpdater_IsNotSupported_AndSaysHowToFixIt()
    {
        File.Delete(_configFile);
        var service = CreateService(new FakeUpdateServer());

        Assert.False(service.Install.Supported);
        Assert.Contains("install.sh", service.Install.UnsupportedReason);
    }

    // ---- check ---------------------------------------------------------------------------------

    [Fact]
    public async Task Check_FindsANewerBuild_SendingTheLicense()
    {
        var server = ServerWithRelease(build: 11);
        var service = CreateService(server);

        var result = await service.CheckAsync(Ct);

        Assert.True(result.Ok);
        Assert.Contains("Build 11", result.Message);
        var request = Assert.Single(server.Requests);
        Assert.Equal("/v1/updates/stable/linux-x64", request.Path);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("LICENSE")), request.License);
        var state = await service.LoadStateAsync(Ct);
        Assert.Equal("available", state.LastCheckOutcome);
        Assert.Equal(11, state.AvailableBuild);
        Assert.Equal("Bug fixes", state.AvailableNotes);
    }

    [Fact]
    public async Task Check_SameBuild_IsUpToDate()
    {
        var service = CreateService(ServerWithRelease(build: CurrentBuild));

        var result = await service.CheckAsync(Ct);

        Assert.True(result.Ok);
        Assert.StartsWith("Up to date", result.Message);
        Assert.Null((await service.LoadStateAsync(Ct)).AvailableBuild);
    }

    [Fact]
    public async Task Check_WithoutALicense_AsksForOne_AndCallsNobody()
    {
        var server = ServerWithRelease(build: 11);
        var service = CreateService(server, license: null);

        var result = await service.CheckAsync(Ct);

        Assert.False(result.Ok);
        Assert.Contains("license", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(server.Requests);
    }

    // ---- install -------------------------------------------------------------------------------

    [Fact]
    public async Task Install_DownloadsVerifiesStagesAndHandsOver()
    {
        var service = CreateService(ServerWithRelease(build: 11));

        var result = await service.InstallAsync(nightly: true, Ct);

        Assert.True(result.Ok, result.Message);
        var staged = Path.Combine(_installRoot, "updates", "staged", "11");
        // The bundle's top-level linux-x64/ folder is stripped: staged mirrors the install root.
        Assert.True(File.Exists(Path.Combine(staged, "SentinelCore", "SentinelCore")));
        Assert.True(File.Exists(Path.Combine(staged, "SentinelCore.Web", "SentinelCore.Web")));
        Assert.True(File.Exists(Path.Combine(staged, "SentinelCore", "appsettings.defaults.json")));
        var pending = JsonDocument.Parse(File.ReadAllText(Path.Combine(_installRoot, "updates", "pending.json"))).RootElement;
        Assert.Equal(11, pending.GetProperty("build").GetInt32());
        Assert.Equal(CurrentBuild, pending.GetProperty("fromBuild").GetInt32());
        Assert.True(service.InstallPending);
    }

    [Fact]
    public async Task Install_RefusesABundleThatDoesNotMatchItsSignedChecksum()
    {
        var server = ServerWithRelease(build: 11);
        // Same length, different bytes: gets past the size check to the hash check.
        server.Bundle = Bundle(extra: "tampered");
        var service = CreateService(server);

        var result = await service.InstallAsync(nightly: true, Ct);

        Assert.False(result.Ok);
        Assert.Contains("checksum", result.Message);
        Assert.False(service.InstallPending);
        Assert.False(Directory.Exists(Path.Combine(_installRoot, "updates", "staged", "11")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_installRoot, "updates", "downloads")));
    }

    [Fact]
    public async Task Install_WhileBusy_IsDeferred_WithoutDownloadingAnything()
    {
        var server = ServerWithRelease(build: 11);
        var service = CreateService(server, busy: "someone is watching a movie");

        var result = await service.InstallAsync(nightly: true, Ct);

        Assert.False(result.Ok);
        Assert.True(result.Deferred);
        Assert.Contains("watching a movie", result.Message);
        Assert.DoesNotContain(server.Requests, r => r.Path.EndsWith(".zip", StringComparison.Ordinal));
        Assert.False(service.InstallPending);
    }

    // Retrying a build that rolled back would only roll back again, every single night.
    [Fact]
    public async Task Install_Overnight_SkipsABuildThatRolledBack_ButInstallNowStillTriesIt()
    {
        var service = CreateService(ServerWithRelease(build: 11));
        await service.SaveStateAsync(new AutoUpdateState { SkippedBuild = 11 }, Ct);

        var nightly = await service.InstallAsync(nightly: true, Ct);
        Assert.False(nightly.Ok);
        Assert.Contains("rolled back", nightly.Message);
        Assert.False(service.InstallPending);

        var manual = await service.InstallAsync(nightly: false, Ct);
        Assert.True(manual.Ok, manual.Message);
        Assert.True(service.InstallPending);
    }

    // publish-release.sh promises a bundle never carries live configuration; one that does would
    // overwrite the owner's cameras and credentials, so it's refused rather than trusted.
    [Fact]
    public async Task Install_RefusesABundleCarryingLiveConfiguration()
    {
        var server = new FakeUpdateServer();
        server.Bundle = Bundle(extraEntry: "linux-x64/SentinelCore/appsettings.json");
        server.Manifest = SignedManifest(Payload(build: 11, bundle: server.Bundle));
        var service = CreateService(server);

        var result = await service.InstallAsync(nightly: true, Ct);

        Assert.False(result.Ok);
        Assert.Contains("appsettings.json", result.Message);
        Assert.False(service.InstallPending);
    }

    [Fact]
    public async Task Install_ResumesAHalfFinishedDownload()
    {
        var server = ServerWithRelease(build: 11);
        var downloads = Path.Combine(_installRoot, "updates", "downloads");
        Directory.CreateDirectory(downloads);
        File.WriteAllBytes(Path.Combine(downloads, "SentinelCore-linux-x64-b11.zip.part"), server.Bundle[..100]);
        var service = CreateService(server);

        var result = await service.InstallAsync(nightly: true, Ct);

        Assert.True(result.Ok, result.Message);
        var download = Assert.Single(server.Requests, r => r.Path.EndsWith(".zip", StringComparison.Ordinal));
        Assert.Equal(100, download.RangeFrom);
    }

    // Nothing watching pending.json (the path unit off) must not block every future night.
    [Fact]
    public async Task APendingRequestNobodyPickedUp_IsClearedAfterHalfAnHour_ButAFreshOneIsNot()
    {
        var service = CreateService(ServerWithRelease(build: 11));
        Assert.True((await service.InstallAsync(nightly: true, Ct)).Ok);
        var pending = Path.Combine(_installRoot, "updates", "pending.json");

        Assert.False(await service.ClearStalePendingAsync(Ct));
        Assert.True(File.Exists(pending));

        File.SetLastWriteTimeUtc(pending, DateTime.UtcNow.AddMinutes(-31));
        Assert.True(await service.ClearStalePendingAsync(Ct));
        Assert.False(service.InstallPending);
        Assert.Contains("sentinelcore-update.path", (await service.LoadStateAsync(Ct)).LastMessage);
    }

    // ---- reporting -----------------------------------------------------------------------------

    [Fact]
    public async Task Report_TellsTheOwnerOnce_AndClearsTheStagedFiles()
    {
        var service = CreateService(ServerWithRelease(build: 11));
        Assert.True((await service.InstallAsync(nightly: true, Ct)).Ok);
        WriteResult(new UpdateInstallResult(11, CurrentBuild, UpdateInstallResult.Installed, "Build 11 is running.", DateTimeOffset.UtcNow));
        var notifier = new FakeNotifier();
        var worker = new AutoUpdateWorker(service, new AppSettingsFile(_settingsPath), notifier, TimeProvider.System, NullLogger<AutoUpdateWorker>.Instance);

        await worker.ReportResultAsync(Ct);
        await worker.ReportResultAsync(Ct);

        var sent = Assert.Single(notifier.Sent);
        Assert.Equal("Sentinel Core updated", sent.Subject);
        Assert.Contains("build 11", sent.Text);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_installRoot, "updates", "staged")));
    }

    [Fact]
    public async Task Report_ARollback_MarksTheBuildToSkipOvernight()
    {
        var service = CreateService(ServerWithRelease(build: 11));
        Directory.CreateDirectory(Path.Combine(_installRoot, "updates"));
        WriteResult(new UpdateInstallResult(11, CurrentBuild, UpdateInstallResult.RolledBack, "The previous build is running again.", DateTimeOffset.UtcNow));
        var notifier = new FakeNotifier();
        var worker = new AutoUpdateWorker(service, new AppSettingsFile(_settingsPath), notifier, TimeProvider.System, NullLogger<AutoUpdateWorker>.Instance);

        await worker.ReportResultAsync(Ct);

        Assert.Equal("Sentinel Core update rolled back", Assert.Single(notifier.Sent).Subject);
        Assert.Equal(11, (await service.LoadStateAsync(Ct)).SkippedBuild);
    }

    // ---- the overnight window ------------------------------------------------------------------

    [Theory]
    [InlineData("2026-10-06T04:10", 4, 10, "2026-10-06")]
    [InlineData("2026-10-06T06:09", 4, 10, "2026-10-06")]
    [InlineData("2026-10-06T06:10", 4, 10, null)]
    [InlineData("2026-10-06T04:09", 4, 10, null)]
    // A window starting at 23:30 runs past midnight and still belongs to the day it started.
    [InlineData("2026-10-07T00:45", 23, 30, "2026-10-06")]
    public void Window_CoversTwoHoursFromTheInstallsOwnMinute(string now, int hour, int offset, string? expected)
    {
        var window = AutoUpdateWindow.Current(DateTime.Parse(now, System.Globalization.CultureInfo.InvariantCulture), hour, offset);

        Assert.Equal(expected, window?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("19", 19)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("-3", 0)]
    [InlineData("abc", 0)]
    public void BuildNumber_IsZeroUnlessAPositiveNumberWasStamped(string? stamped, int expected) =>
        Assert.Equal(expected, BuildInfo.ParseBuildNumber(stamped));

    // ---- through the real host -----------------------------------------------------------------

    // A test host is a source build (no BuildNumber stamped), which is exactly the maintainer's
    // own box: the status must say why updating is off rather than offer anything.
    [Fact]
    public async Task StatusEndpoint_OnASourceBuild_ExplainsWhyUpdatesAreOff()
    {
        File.WriteAllText(_settingsPath, WebHostTestSettings.LoginOffSettings);
        using var factory = new WebApplicationFactory<WebProgram>()
            .WithWebHostBuilder(builder => builder.UseTestSettings(_settingsPath, _root));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/updates/status", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.False(body.GetProperty("supported").GetBoolean());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("unsupportedReason").GetString()));

        using var check = await client.PostAsync("/api/updates/check", null, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, check.StatusCode);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private AutoUpdateService CreateService(FakeUpdateServer server, int currentBuild = CurrentBuild, string? license = "LICENSE", string? busy = null) =>
        new(
            new AutoUpdateHostOptions
            {
                ServerUrl = "http://updates.test",
                ConfigFile = _configFile,
                InstallRoot = _installRoot,
                PublicKeyPem = PublicKey(),
                CurrentBuild = currentBuild,
                Platform = Platform
            },
            new FakeEnvironment(Path.Combine(_installRoot, "SentinelCore.Web")),
            new AppSettingsFile(_settingsPath),
            new FakeLicense(license),
            new SingleClientFactory(server),
            new FakeBusy(busy),
            TimeProvider.System,
            NullLogger<AutoUpdateService>.Instance);

    private FakeUpdateServer ServerWithRelease(int build)
    {
        var server = new FakeUpdateServer { Bundle = Bundle() };
        server.Manifest = SignedManifest(Payload(build, bundle: server.Bundle));
        return server;
    }

    private void WriteResult(UpdateInstallResult result) =>
        File.WriteAllText(Path.Combine(_installRoot, "updates", "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private string PublicKey() => _key.ExportSubjectPublicKeyInfoPem();

    // Shaped exactly like publish-update.sh's payload.
    private static byte[] Payload(int build, string platform = Platform, string? file = null, byte[]? bundle = null)
    {
        bundle ??= Bundle();
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            product = "SentinelCore",
            channel = Channel,
            platform,
            version = "1.0.0",
            build,
            commit = "abc123def456",
            publishedAt = DateTimeOffset.UtcNow,
            file = file ?? $"SentinelCore-{platform}-b{build}.zip",
            size = bundle.LongLength,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(bundle)),
            minimumBuild = 0,
            notes = "Bug fixes"
        });
    }

    private string SignedManifest(byte[] payload) => JsonSerializer.Serialize(new
    {
        payload = Convert.ToBase64String(payload),
        // DER, like `openssl dgst -sign`.
        signature = Convert.ToBase64String(_key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
    });

    // A release zip as publish-release.sh lays it out: one top-level <platform>/ folder.
    private static byte[] Bundle(string extra = "original", string? extraEntry = null)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                // A fixed timestamp, so two bundles with the same content are byte-identical.
                entry.LastWriteTime = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
            Add($"{Platform}/SentinelCore/SentinelCore", "bridge binary" + extra);
            Add($"{Platform}/SentinelCore/appsettings.defaults.json", "{}");
            Add($"{Platform}/SentinelCore.Web/SentinelCore.Web", "dashboard binary");
            Add($"{Platform}/install.sh", "#!/bin/sh");
            if (extraEntry is not null) Add(extraEntry, "{}");
        }
        return buffer.ToArray();
    }

    private sealed record SeenRequest(string Path, string? License, long? RangeFrom);

    private sealed class FakeUpdateServer : HttpMessageHandler
    {
        public string? Manifest { get; set; }
        public byte[] Bundle { get; set; } = [];
        public List<SeenRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var license = request.Headers.TryGetValues("X-Sentinel-License", out var values) ? values.Single() : null;
            var from = request.Headers.Range?.Ranges.Single().From;
            Requests.Add(new SeenRequest(path, license, from));

            if (path == $"/v1/updates/{Channel}/{Platform}")
            {
                return Task.FromResult(Manifest is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Manifest) });
            }
            if (path.EndsWith(".zip", StringComparison.Ordinal))
            {
                if (from is { } start)
                {
                    var partial = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Bundle[(int)start..]) };
                    partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, Bundle.Length - 1, Bundle.Length);
                    return Task.FromResult(partial);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bundle) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeLicense(string? contents) : IRelayLicenseSource
    {
        public Task<string?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(contents);
    }

    private sealed class FakeBusy(string? reason) : IUpdateBusyProbe
    {
        public Task<string?> WhyBusyAsync(CancellationToken cancellationToken) => Task.FromResult(reason);
    }

    private sealed class FakeNotifier : IOwnerNotifier
    {
        public List<(string Subject, string Text)> Sent { get; } = [];

        public Task<bool> SendAsync(string subject, string text, CancellationToken cancellationToken)
        {
            Sent.Add((subject, text));
            return Task.FromResult(true);
        }
    }

    private sealed class FakeEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "SentinelCore.Web";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
