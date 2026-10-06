using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SentinelCore;

namespace SentinelCore.Web;

// Automatic Core updates for installs made from a release bundle (docs/AUTO_UPDATE.md).
//
// The work is split by privilege. Everything that talks to the internet, parses anything, or
// decides anything happens here, in the dashboard, as the unprivileged app user: fetch the signed
// manifest from the relay, verify it, download the bundle, check its hash, unpack it into
// <install>/updates/staged/<build>/, and finally drop updates/pending.json. A systemd path unit
// sees that file and runs sentinelcore-apply-update as root, which only stops the services,
// copies the staged files over the install (as the app user), restarts, health-checks, and puts
// the previous files back if the new build doesn't come up. It writes updates/result.json, which
// the restarted dashboard reports to the owner.
//
// A git checkout (the maintainer's own box) never takes part: it has no BuildNumber stamped in
// and no updater installed, and says so in Settings instead of offering anything.

// The signed manifest's payload - exactly the bytes publish-update.sh signed, decoded only after
// the signature over them has been checked.
public sealed record UpdateManifest(
    int SchemaVersion,
    string Product,
    string Channel,
    string Platform,
    string Version,
    int Build,
    string Commit,
    DateTimeOffset PublishedAt,
    string File,
    long Size,
    string Sha256,
    int MinimumBuild,
    string Notes);

public static class UpdateManifestVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record Envelope(string? Payload, string? Signature);

    // Null with a reason for anything that isn't a correctly signed manifest for exactly this
    // product, channel and platform. The channel/platform check stops a genuine manifest being
    // replayed where it doesn't belong - an arm64 bundle offered to an x64 box would install
    // cleanly and then never start.
    public static UpdateManifest? Verify(string manifestJson, string publicKeyPem, string channel, string platform, out string error)
    {
        error = "";
        byte[] payload, signature;
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(manifestJson, JsonOptions);
            if (string.IsNullOrEmpty(envelope?.Payload) || string.IsNullOrEmpty(envelope.Signature))
            {
                error = "The update manifest is incomplete.";
                return null;
            }
            payload = Convert.FromBase64String(envelope.Payload);
            signature = Convert.FromBase64String(envelope.Signature);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            error = "The update manifest is unreadable.";
            return null;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            // openssl dgst -sign writes DER, not the fixed-width form .NET defaults to.
            if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            {
                error = "The update's signature doesn't match. It was not installed.";
                return null;
            }
        }
        catch (CryptographicException)
        {
            error = "The update's signature doesn't match. It was not installed.";
            return null;
        }

        UpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            manifest = null;
        }

        if (manifest is null || manifest.SchemaVersion != 1 || manifest.Product != "SentinelCore")
            error = "The update manifest is for something else.";
        else if (manifest.Channel != channel || manifest.Platform != platform)
            error = $"The server offered a {manifest.Channel}/{manifest.Platform} update to a {channel}/{platform} install.";
        else if (manifest.Build <= 0 || manifest.Size <= 0 || manifest.Sha256.Length != 64
                 || !SafeFileName(manifest.File))
            error = "The update manifest is malformed.";
        else
            return manifest;
        return null;
    }

    public static bool SafeFileName(string name) =>
        name.Length is > 0 and <= 128
        && name.EndsWith(".zip", StringComparison.Ordinal)
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
        && name[0] != '.';
}

// Settings for the host side of updating - not in the Settings page. The defaults are what every
// real install uses; the rest exist so tests can point the service at a temp folder and a fake
// server with a throwaway key.
public sealed class AutoUpdateHostOptions
{
    public const string SectionName = "Updates";
    public const string DefaultServerUrl = "https://updates.nr5p.com";

    public string ServerUrl { get; set; } = DefaultServerUrl;
    // Written by install.sh (root-owned) when it sets up the updater. Its presence is how the
    // dashboard knows the root half exists to act on pending.json.
    public string ConfigFile { get; set; } = "/etc/sentinelcore/update.conf";
    // Defaults to the parent of the dashboard's content root: a bundle install is
    // <install>/SentinelCore and <install>/SentinelCore.Web side by side.
    public string? InstallRoot { get; set; }
    public string PublicKeyPem { get; set; } = UpdateSigningPublicKey.Pem;
    // 0 = this binary's own stamped build. Tests only.
    public int CurrentBuild { get; set; }
    // Null = worked out from the OS and CPU. Setting it also skips the Linux-only check. Tests only.
    public string? Platform { get; set; }
}

public sealed record UpdateInstallInfo(
    bool Supported,
    string? UnsupportedReason,
    string InstallRoot,
    string UpdatesDirectory,
    string Platform,
    int CurrentBuild);

// What sentinelcore-apply-update writes when it finishes, whichever way it went.
public sealed record UpdateInstallResult(int Build, int FromBuild, string Outcome, string Message, DateTimeOffset FinishedAt)
{
    public const string Installed = "installed";
    public const string RolledBack = "rolledBack";
    public const string Failed = "failed";
}

public sealed record AutoUpdateState
{
    public DateTimeOffset? LastCheckUtc { get; init; }
    // upToDate | available | failed
    public string? LastCheckOutcome { get; init; }
    public string? LastMessage { get; init; }
    public int? AvailableBuild { get; init; }
    public string? AvailableVersion { get; init; }
    public string? AvailableNotes { get; init; }
    public DateTimeOffset? AvailablePublishedAt { get; init; }
    // The local date (yyyy-MM-dd) of the last overnight window an install was attempted in, so
    // one window makes one attempt however many ticks land inside it.
    public string? LastNightlyWindow { get; init; }
    // A build that was installed and rolled back. The nightly run won't try it again - it would
    // only roll back again, every night - but "install now" still can.
    public int? SkippedBuild { get; init; }
    public DateTimeOffset? ReportedResultAt { get; init; }
}

public sealed record UpdateActionResult(bool Ok, string Message, bool Deferred = false);

public interface IUpdateBusyProbe
{
    // Null when an update may restart the server now, otherwise why not, in a few words.
    Task<string?> WhyBusyAsync(CancellationToken cancellationToken);
}

// Only what a restart would actually break. Live viewing is deliberately not here: a wall
// kiosk shows cameras around the clock, and it reconnects by itself after a restart anyway.
// Torrents aren't either - qBittorrent runs in its own service and keeps downloading; only the
// copy into the library (Importing) happens in this process. An armed alarm doesn't block (night
// mode is on at exactly the hour updates run), but one counting down or going off does.
public sealed class UpdateBusyProbe(IServiceProvider services) : IUpdateBusyProbe
{
    public async Task<string?> WhyBusyAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<AlarmEngine>() is { } alarm)
        {
            var status = (await alarm.GetStatusAsync(cancellationToken)).Status;
            if (status is AlarmStatuses.Arming or AlarmStatuses.Pending or AlarmStatuses.Triggered)
                return $"the alarm is {status}";
        }
        if (services.GetService<ManualRecordingService>() is { } recordings && recordings.ActiveCameraIds().Count > 0)
            return "a camera is being recorded by hand";
        if (services.GetService<MediaHlsManager>() is { } movies && movies.ActiveSessionCount(TimeSpan.FromMinutes(2)) > 0)
            return "someone is watching a movie";
        if (services.GetService<AutoExportRunner>() is { IsRunning: true })
            return "the nightly backup is running";
        if (services.GetService<MediaDownloadStore>() is { } downloads
            && (await downloads.AllAsync(cancellationToken)).Any(d => d.State == MediaDownloadStates.Importing))
            return "a download is being copied into the library";
        return null;
    }
}

public sealed class AutoUpdateService
{
    public const string HttpClientName = "AutoUpdate";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly AutoUpdateHostOptions _options;
    private readonly AppSettingsFile _settings;
    private readonly IRelayLicenseSource _license;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IUpdateBusyProbe _busy;
    private readonly TimeProvider _time;
    private readonly ILogger<AutoUpdateService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    public AutoUpdateService(
        AutoUpdateHostOptions options,
        IHostEnvironment environment,
        AppSettingsFile settings,
        IRelayLicenseSource license,
        IHttpClientFactory httpClientFactory,
        IUpdateBusyProbe busy,
        TimeProvider time,
        ILogger<AutoUpdateService> logger)
    {
        _options = options;
        _settings = settings;
        _license = license;
        _httpClientFactory = httpClientFactory;
        _busy = busy;
        _time = time;
        _logger = logger;
        Install = Detect(options, environment);
    }

    public UpdateInstallInfo Install { get; }

    // idle | checking | downloading | staging | installing. In memory only: a restart is the end
    // of an install either way, and result.json says how it went.
    public string Phase { get; private set; } = "idle";

    private string StatePath => Path.Combine(Install.UpdatesDirectory, "state.json");
    private string PendingPath => Path.Combine(Install.UpdatesDirectory, "pending.json");
    private string ResultPath => Path.Combine(Install.UpdatesDirectory, "result.json");
    private string StagedRoot => Path.Combine(Install.UpdatesDirectory, "staged");
    private string DownloadsRoot => Path.Combine(Install.UpdatesDirectory, "downloads");

    private static UpdateInstallInfo Detect(AutoUpdateHostOptions options, IHostEnvironment environment)
    {
        var root = options.InstallRoot is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Path.GetDirectoryName(Path.GetFullPath(environment.ContentRootPath)) ?? environment.ContentRootPath;
        var updates = Path.Combine(root, "updates");
        var build = options.CurrentBuild > 0 ? options.CurrentBuild : BuildInfo.Current.Build;
        var platform = options.Platform ?? DetectPlatform();

        string? reason = null;
        if (platform is null)
            reason = "Automatic updates are for Linux servers. On this computer, install a new release by hand.";
        else if (build <= 0)
            reason = "This server runs from source code (a git checkout), so it updates through git and deploy.sh rather than automatically.";
        else if (!File.Exists(options.ConfigFile))
            reason = "The updater service isn't set up on this machine yet. Run install.sh from the latest release once (sudo ./install.sh) and it will be.";

        return new UpdateInstallInfo(reason is null, reason, root, updates, platform ?? "", build);
    }

    private static string? DetectPlatform()
    {
        if (!OperatingSystem.IsLinux()) return null;
        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "linux-x64",
            Architecture.Arm64 => "linux-arm64",
            _ => null
        };
    }

    public async Task<AutoUpdateSettingsDto> GetSettingsAsync(CancellationToken ct) =>
        await _settings.GetSectionAsync<AutoUpdateSettingsDto>(AutoUpdateSettingsDto.SectionName, ct) ?? new AutoUpdateSettingsDto();

    // ---- state -------------------------------------------------------------------------------

    public async Task<AutoUpdateState> LoadStateAsync(CancellationToken ct)
    {
        if (!Install.Supported) return new AutoUpdateState();
        await _stateLock.WaitAsync(ct);
        try
        {
            if (!File.Exists(StatePath)) return new AutoUpdateState();
            await using var stream = File.OpenRead(StatePath);
            return await JsonSerializer.DeserializeAsync<AutoUpdateState>(stream, JsonOptions, ct) ?? new AutoUpdateState();
        }
        catch (JsonException)
        {
            return new AutoUpdateState();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task SaveStateAsync(AutoUpdateState state, CancellationToken ct)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            await WriteJsonAtomicallyAsync(StatePath, state, ct);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task UpdateStateAsync(Func<AutoUpdateState, AutoUpdateState> change, CancellationToken ct) =>
        await SaveStateAsync(change(await LoadStateAsync(ct)), ct);

    public async Task<UpdateInstallResult?> ReadResultAsync(CancellationToken ct)
    {
        if (!Install.Supported || !File.Exists(ResultPath)) return null;
        try
        {
            await using var stream = File.OpenRead(ResultPath);
            return await JsonSerializer.DeserializeAsync<UpdateInstallResult>(stream, JsonOptions, ct);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    // True while pending.json is waiting for (or being worked on by) the root half.
    public bool InstallPending => Install.Supported && File.Exists(PendingPath);

    // The root half deletes pending.json the moment it starts, and stops this process soon after.
    // A request still sitting there half an hour later means nothing is watching for it - the path
    // unit is disabled or was never installed - and left alone it would block every future night
    // and show "installing" forever. True when one was cleared.
    public async Task<bool> ClearStalePendingAsync(CancellationToken ct)
    {
        if (!InstallPending) return false;
        if (_time.GetUtcNow() - File.GetLastWriteTimeUtc(PendingPath) < TimeSpan.FromMinutes(30)) return false;

        File.Delete(PendingPath);
        if (Phase == "installing") Phase = "idle";
        const string message = "The update was downloaded but the installer never picked it up. Check that sentinelcore-update.path is running (sudo systemctl enable --now sentinelcore-update.path), or re-run install.sh.";
        _logger.LogWarning("Cleared an update request nobody picked up: {Message}", message);
        await UpdateStateAsync(s => s with { LastCheckOutcome = "failed", LastMessage = message }, ct);
        return true;
    }

    // ---- check -------------------------------------------------------------------------------

    private sealed record FetchOutcome(UpdateManifest? Manifest, string Message);

    public async Task<UpdateActionResult> CheckAsync(CancellationToken ct)
    {
        if (!Install.Supported) return new UpdateActionResult(false, Install.UnsupportedReason!);
        await _gate.WaitAsync(ct);
        try
        {
            var outcome = await CheckLockedAsync(ct);
            return new UpdateActionResult(outcome.Manifest is not null || outcome.Message.StartsWith("Up to date", StringComparison.Ordinal), outcome.Message);
        }
        finally
        {
            Phase = "idle";
            _gate.Release();
        }
    }

    private async Task<FetchOutcome> CheckLockedAsync(CancellationToken ct)
    {
        Phase = "checking";
        var settings = await GetSettingsAsync(ct);
        var fetched = await FetchManifestAsync(settings.Channel, ct);
        var now = _time.GetUtcNow();
        var manifest = fetched.Manifest;

        if (manifest is null)
        {
            await UpdateStateAsync(s => s with { LastCheckUtc = now, LastCheckOutcome = "failed", LastMessage = fetched.Message }, ct);
            return fetched;
        }

        if (manifest.Build <= Install.CurrentBuild)
        {
            var message = $"Up to date - build {Install.CurrentBuild} is the latest on the {settings.Channel} channel.";
            await UpdateStateAsync(s => s with
            {
                LastCheckUtc = now, LastCheckOutcome = "upToDate", LastMessage = message,
                AvailableBuild = null, AvailableVersion = null, AvailableNotes = null, AvailablePublishedAt = null
            }, ct);
            return new FetchOutcome(null, message);
        }

        var available = $"Build {manifest.Build} ({manifest.Version}) is available - this server is on build {Install.CurrentBuild}.";
        await UpdateStateAsync(s => s with
        {
            LastCheckUtc = now, LastCheckOutcome = "available", LastMessage = available,
            AvailableBuild = manifest.Build, AvailableVersion = manifest.Version,
            AvailableNotes = manifest.Notes, AvailablePublishedAt = manifest.PublishedAt
        }, ct);
        return new FetchOutcome(manifest, available);
    }

    private async Task<FetchOutcome> FetchManifestAsync(string channel, CancellationToken ct)
    {
        var license = await _license.ReadAsync(ct);
        if (license is null)
            return new FetchOutcome(null, "Updates need a license. Install one under Settings -> LICENSE.");

        var url = $"{_options.ServerUrl.TrimEnd('/')}/v1/updates/{Uri.EscapeDataString(channel)}/{Install.Platform}";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation(PushRelayClient.LicenseHeader, Convert.ToBase64String(Encoding.UTF8.GetBytes(license)));
            using var response = await _httpClientFactory.CreateClient(HttpClientName).SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return new FetchOutcome(null, $"No release has been published on the {channel} channel yet.");
            if (!response.IsSuccessStatusCode)
                return new FetchOutcome(null, $"The update server said no (HTTP {(int)response.StatusCode}){ErrorSuffix(body)}");

            var manifest = UpdateManifestVerifier.Verify(body, _options.PublicKeyPem, channel, Install.Platform, out var error);
            if (manifest is null) _logger.LogWarning("Refused the update manifest from {Url}: {Error}", url, error);
            return new FetchOutcome(manifest, error);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new FetchOutcome(null, $"Couldn't reach the update server ({ex.Message}).");
        }
    }

    private static string ErrorSuffix(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var error) && error.GetString() is { Length: > 0 } text ? $": {text}" : ".";
        }
        catch (JsonException)
        {
            return ".";
        }
    }

    // ---- install -----------------------------------------------------------------------------

    // nightly: true from the overnight window, which skips a build that already rolled back and
    // defers (rather than fails) when the box is busy.
    public async Task<UpdateActionResult> InstallAsync(bool nightly, CancellationToken ct)
    {
        if (!Install.Supported) return new UpdateActionResult(false, Install.UnsupportedReason!);
        if (InstallPending) return new UpdateActionResult(false, "An update is already being installed.");
        if (!await _gate.WaitAsync(0, ct)) return new UpdateActionResult(false, "An update check is already running.");
        try
        {
            var outcome = await CheckLockedAsync(ct);
            if (outcome.Manifest is not { } manifest)
                return new UpdateActionResult(outcome.Message.StartsWith("Up to date", StringComparison.Ordinal), outcome.Message);

            var state = await LoadStateAsync(ct);
            if (nightly && state.SkippedBuild == manifest.Build)
                return new UpdateActionResult(false, $"Build {manifest.Build} rolled back last time, so it won't be retried overnight. Use 'install now' to try it again.");
            if (Install.CurrentBuild < manifest.MinimumBuild)
                return new UpdateActionResult(false, $"Build {manifest.Build} can't be installed automatically over build {Install.CurrentBuild}. Download the release and run its install.sh once.");

            if (await _busy.WhyBusyAsync(ct) is { } busy)
                return new UpdateActionResult(false, $"Waiting - {busy}.", Deferred: true);

            var bundle = await DownloadAsync(manifest, ct);
            if (bundle.Error is not null)
            {
                await UpdateStateAsync(s => s with { LastCheckOutcome = "failed", LastMessage = bundle.Error }, ct);
                return new UpdateActionResult(false, bundle.Error);
            }

            Phase = "staging";
            var staged = Stage(bundle.Path!, manifest.Build);
            if (staged is not null)
            {
                await UpdateStateAsync(s => s with { LastCheckOutcome = "failed", LastMessage = staged }, ct);
                return new UpdateActionResult(false, staged);
            }

            // The last step, and the one the root half watches for. Everything it names has been
            // verified; it only ever reads the build number out of this.
            await WriteJsonAtomicallyAsync(PendingPath, new
            {
                build = manifest.Build,
                version = manifest.Version,
                commit = manifest.Commit,
                fromBuild = Install.CurrentBuild,
                requestedAt = _time.GetUtcNow()
            }, ct);
            Phase = "installing";
            _logger.LogInformation("Update to build {Build} staged; handing over to the installer", manifest.Build);
            return new UpdateActionResult(true, $"Build {manifest.Build} downloaded and verified. Installing now - the server restarts in a minute or two.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogWarning(ex, "Update install failed before handing over");
            var message = $"The update couldn't be prepared: {ex.Message}";
            await UpdateStateAsync(s => s with { LastCheckOutcome = "failed", LastMessage = message }, ct);
            return new UpdateActionResult(false, message);
        }
        finally
        {
            if (Phase != "installing") Phase = "idle";
            _gate.Release();
        }
    }

    private sealed record DownloadOutcome(string? Path, string? Error);

    private async Task<DownloadOutcome> DownloadAsync(UpdateManifest manifest, CancellationToken ct)
    {
        Phase = "downloading";
        Directory.CreateDirectory(DownloadsRoot);
        var final = Path.Combine(DownloadsRoot, manifest.File);
        var part = final + ".part";

        // A bundle, its unpacked copy, and the backup of the files it replaces all exist at once.
        var free = new DriveInfo(Install.InstallRoot).AvailableFreeSpace;
        if (free < manifest.Size * 4)
            return new(null, $"Not enough disk space for the update (needs about {manifest.Size * 4 / 1_000_000} MB free, has {free / 1_000_000} MB).");

        if (!System.IO.File.Exists(final))
        {
            var license = await _license.ReadAsync(ct);
            if (license is null) return new(null, "Updates need a license. Install one under Settings -> LICENSE.");

            long have = System.IO.File.Exists(part) ? new FileInfo(part).Length : 0;
            if (have > manifest.Size) { System.IO.File.Delete(part); have = 0; }

            var url = $"{_options.ServerUrl.TrimEnd('/')}/v1/updates/{Uri.EscapeDataString(manifest.Channel)}/{manifest.Platform}/{manifest.File}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation(PushRelayClient.LicenseHeader, Convert.ToBase64String(Encoding.UTF8.GetBytes(license)));
            // Resumes a download an earlier night (or a dropped connection) left half done.
            if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);

            try
            {
                using var response = await _httpClientFactory.CreateClient(HttpClientName)
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                    return new(null, $"The update download was refused (HTTP {(int)response.StatusCode}).");

                var append = response.StatusCode == HttpStatusCode.PartialContent;
                await using (var source = await response.Content.ReadAsStreamAsync(ct))
                await using (var target = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write))
                {
                    await source.CopyToAsync(target, ct);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // The .part file stays, so the next attempt picks up where this one stopped.
                return new(null, $"The update download was interrupted ({ex.Message}). It will resume next time.");
            }

            var length = new FileInfo(part).Length;
            if (length != manifest.Size)
            {
                if (length > manifest.Size) System.IO.File.Delete(part);
                return new(null, $"The update download is incomplete ({length} of {manifest.Size} bytes). It will resume next time.");
            }
            System.IO.File.Move(part, final, overwrite: true);
        }

        string actual;
        await using (var stream = System.IO.File.OpenRead(final))
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            System.IO.File.Delete(final);
            _logger.LogWarning("Update bundle {File} failed its hash check (expected {Expected}, got {Actual})", manifest.File, manifest.Sha256, actual);
            return new(null, "The downloaded update didn't match its signed checksum, so it was deleted and not installed.");
        }
        return new(final, null);
    }

    // Unpacks into staged/<build>/ with the bundle's top-level <platform>/ folder stripped, so the
    // staged tree mirrors the install root exactly. Null on success, otherwise why not.
    private string? Stage(string zipPath, int build)
    {
        Directory.CreateDirectory(StagedRoot);
        foreach (var old in Directory.EnumerateDirectories(StagedRoot)) Directory.Delete(old, recursive: true);

        var unpack = Path.Combine(StagedRoot, $".unpack-{build}");
        // ExtractToDirectory refuses entries that would land outside the folder.
        ZipFile.ExtractToDirectory(zipPath, unpack);

        var top = unpack;
        if (!Directory.Exists(Path.Combine(top, "SentinelCore"))
            && Directory.GetDirectories(unpack) is [var only] && Directory.GetFiles(unpack).Length == 0)
            top = only;

        foreach (var required in new[] { "SentinelCore/SentinelCore", "SentinelCore.Web/SentinelCore.Web" })
        {
            if (!System.IO.File.Exists(Path.Combine(top, required)))
            {
                Directory.Delete(unpack, recursive: true);
                return $"The update bundle is missing {required}. It was not installed.";
            }
        }
        // Nothing carrying user state may be in a bundle (publish-release.sh promises as much);
        // refuse one that does rather than let it overwrite the live configuration.
        foreach (var forbidden in new[] { "SentinelCore/appsettings.json", "SentinelCore.Web/appsettings.json", "SentinelCore/data", "SentinelCore.Web/data", "updates" })
        {
            var path = Path.Combine(top, forbidden);
            if (System.IO.File.Exists(path) || Directory.Exists(path))
            {
                Directory.Delete(unpack, recursive: true);
                return $"The update bundle contains {forbidden}, which would overwrite this server's own data. It was not installed.";
            }
        }

        Directory.Move(top, Path.Combine(StagedRoot, build.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (Directory.Exists(unpack)) Directory.Delete(unpack, recursive: true);
        return null;
    }

    // After a result has been reported: the staged tree and the downloaded zip are only needed
    // until the installer has run.
    public void CleanUpAfterInstall()
    {
        if (!Install.Supported) return;
        try
        {
            if (Directory.Exists(StagedRoot))
                foreach (var dir in Directory.EnumerateDirectories(StagedRoot)) Directory.Delete(dir, recursive: true);
            if (Directory.Exists(DownloadsRoot))
                foreach (var file in Directory.EnumerateFiles(DownloadsRoot, "*.zip")) System.IO.File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't clear out the previous update's files");
        }
    }

    private async Task WriteJsonAtomicallyAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await using (var stream = System.IO.File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, ct);
        AtomicFile.Replace(temporary, path);
    }
}

public static class AutoUpdateWindow
{
    public const int LengthHours = 2;

    // Spreads installs across the first hour of the window, so every box doesn't hit the relay at
    // 04:00 sharp. Stable per install (it's from the install path and machine name), so one box
    // always lands at the same minute.
    public static int OffsetMinutes(string installRoot) =>
        (int)(BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName + "|" + installRoot)), 0) % 60);

    // The local date of the window `nowLocal` falls in, or null when it's outside every window.
    // Handles windows that run past midnight (Hour 23).
    public static DateOnly? Current(DateTime nowLocal, int hour, int offsetMinutes)
    {
        foreach (var day in new[] { DateOnly.FromDateTime(nowLocal), DateOnly.FromDateTime(nowLocal).AddDays(-1) })
        {
            var start = day.ToDateTime(new TimeOnly(hour, 0)).AddMinutes(offsetMinutes);
            if (nowLocal >= start && nowLocal < start.AddHours(LengthHours)) return day;
        }
        return null;
    }

    public static DateTimeOffset NextStartUtc(DateTimeOffset nowUtc, TimeZoneInfo zone, int hour, int offsetMinutes)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var start = local.Date.AddHours(hour).AddMinutes(offsetMinutes);
        if (start <= local.DateTime) start = start.AddDays(1);
        return new DateTimeOffset(start, zone.GetUtcOffset(start)).ToUniversalTime();
    }
}

public sealed class AutoUpdateWorker(
    AutoUpdateService updates,
    AppSettingsFile settings,
    IOwnerNotifier notifier,
    TimeProvider time,
    ILogger<AutoUpdateWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!updates.Install.Supported)
        {
            logger.LogInformation("Automatic updates off: {Reason}", updates.Install.UnsupportedReason);
            return;
        }

        try { await Task.Delay(TimeSpan.FromSeconds(20), time, stoppingToken); }
        catch (OperationCanceledException) { return; }

        try { await ReportResultAsync(stoppingToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Couldn't report the last update result"); }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Automatic update check failed"); }

            try { await Task.Delay(Tick, time, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    // Tells the owner how the last install went, once. Runs in whichever process came up after
    // it: the new build after a good install, the old one after a rollback.
    public async Task ReportResultAsync(CancellationToken ct)
    {
        var result = await updates.ReadResultAsync(ct);
        if (result is null) return;
        var state = await updates.LoadStateAsync(ct);
        if (state.ReportedResultAt is { } reported && result.FinishedAt <= reported) return;

        var (subject, text) = result.Outcome switch
        {
            UpdateInstallResult.Installed => ("Sentinel Core updated", $"The server installed build {result.Build} overnight and is running normally."),
            UpdateInstallResult.RolledBack => ("Sentinel Core update rolled back",
                $"Build {result.Build} didn't start properly, so the server went back to build {result.FromBuild}. Nothing was lost. It won't retry that build overnight. {result.Message}".Trim()),
            _ => ("Sentinel Core update failed", $"Installing build {result.Build} failed: {result.Message}")
        };
        logger.LogInformation("Update result: {Outcome} build {Build} (from {FromBuild}): {Message}", result.Outcome, result.Build, result.FromBuild, result.Message);
        await notifier.SendAsync(subject, text, ct);

        await updates.SaveStateAsync(state with
        {
            ReportedResultAt = result.FinishedAt,
            SkippedBuild = result.Outcome == UpdateInstallResult.Installed ? state.SkippedBuild : result.Build,
            AvailableBuild = result.Outcome == UpdateInstallResult.Installed ? null : state.AvailableBuild,
            LastMessage = text
        }, ct);
        updates.CleanUpAfterInstall();
    }

    public async Task TickAsync(CancellationToken ct)
    {
        if (await updates.ClearStalePendingAsync(ct))
            await notifier.SendAsync("Sentinel Core update didn't install",
                "An update was downloaded but the server's installer never picked it up. Settings -> AUTOMATIC UPDATES says how to fix it.", ct);

        var options = await updates.GetSettingsAsync(ct);
        if (!options.Enabled || updates.InstallPending) return;

        var zone = RecordingScheduleWorker.ResolveTimeZone(await settings.GetSectionAsync<ScheduleOptions>(ScheduleOptions.SectionName, ct));
        var nowLocal = TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime;
        if (AutoUpdateWindow.Current(nowLocal, options.Hour, AutoUpdateWindow.OffsetMinutes(updates.Install.InstallRoot)) is not { } window)
            return;

        var key = window.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var state = await updates.LoadStateAsync(ct);
        if (state.LastNightlyWindow == key) return;

        var result = await updates.InstallAsync(nightly: true, ct);
        logger.LogInformation("Overnight update: {Message}", result.Message);
        // A busy box is tried again on the next tick, for as long as the window lasts.
        if (!result.Deferred)
            await updates.SaveStateAsync((await updates.LoadStateAsync(ct)) with { LastNightlyWindow = key }, ct);
    }
}

public static class AutoUpdateEndpoints
{
    public static void MapAutoUpdateEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/updates").RequireAuthorization("AdminOnly");

        group.MapGet("/status", async (AutoUpdateService updates, AppSettingsFile settings, TimeProvider time, CancellationToken ct) =>
        {
            var install = updates.Install;
            var options = await updates.GetSettingsAsync(ct);
            var state = await updates.LoadStateAsync(ct);
            var result = await updates.ReadResultAsync(ct);
            DateTimeOffset? next = null;
            if (install.Supported && options.Enabled)
            {
                var zone = RecordingScheduleWorker.ResolveTimeZone(await settings.GetSectionAsync<ScheduleOptions>(ScheduleOptions.SectionName, ct));
                next = AutoUpdateWindow.NextStartUtc(time.GetUtcNow(), zone, options.Hour, AutoUpdateWindow.OffsetMinutes(install.InstallRoot));
            }
            return Results.Ok(new
            {
                supported = install.Supported,
                unsupportedReason = install.UnsupportedReason,
                current = new { build = install.CurrentBuild, BuildInfo.Current.Version, BuildInfo.Current.Commit },
                platform = install.Platform,
                enabled = options.Enabled,
                channel = options.Channel,
                phase = updates.InstallPending ? "installing" : updates.Phase,
                nextWindowUtc = next,
                state.LastCheckUtc,
                state.LastCheckOutcome,
                state.LastMessage,
                available = state.AvailableBuild is { } build
                    ? new { build, version = state.AvailableVersion, notes = state.AvailableNotes, publishedAt = state.AvailablePublishedAt }
                    : null,
                state.SkippedBuild,
                lastResult = result
            });
        });

        // The settings section's "check now" button: `detail` on success, 400 `error` on failure,
        // which is what every client's action handler shows.
        group.MapPost("/check", async (AutoUpdateService updates, CancellationToken ct) =>
        {
            var result = await updates.CheckAsync(ct);
            return result.Ok ? Results.Ok(new { detail = result.Message }) : Results.BadRequest(new { error = result.Message });
        });

        // Starts an install and answers straight away - the download alone can take minutes. The
        // status endpoint shows the phase, and the server restarting is the end of it.
        group.MapPost("/install", (AutoUpdateService updates, IHostApplicationLifetime lifetime, ILogger<AutoUpdateService> logger) =>
        {
            if (!updates.Install.Supported) return Results.BadRequest(new { error = updates.Install.UnsupportedReason });
            if (updates.InstallPending || updates.Phase != "idle") return Results.Conflict(new { error = "An update is already in progress." });

            _ = Task.Run(async () =>
            {
                var result = await updates.InstallAsync(nightly: false, lifetime.ApplicationStopping);
                logger.LogInformation("Install now: {Message}", result.Message);
            }, CancellationToken.None);
            return Results.Accepted(value: new { detail = "Checking for the update and downloading it. This page shows how it's going; the server restarts once it's ready." });
        });
    }
}
