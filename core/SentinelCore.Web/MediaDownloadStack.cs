using System.Diagnostics;
using System.Text.Json;

namespace SentinelCore.Web;

// What the VPN tunnel says about itself: the public address the torrent services are seen from,
// as looked up from inside the tunnel by sentinelcore-vpn-watch every 45 seconds.
public sealed record MediaVpnStatus(
    string State,
    string? Ip = null,
    string? City = null,
    string? Region = null,
    string? Country = null,
    string? Org = null,
    DateTimeOffset? CheckedAt = null,
    string? Error = null)
{
    public const string Connected = "connected";
    public const string Connecting = "connecting";
    public const string Down = "down";
    // No tunnel is being watched: the services are stopped, or someone else runs them.
    public const string Off = "off";
    public const string Unknown = "unknown";

    public string? Location => string.Join(", ", new[] { City, Country }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } l ? l : null;
}

public static class MediaDownloadServiceStates
{
    public const string Running = "running";
    public const string Starting = "starting";
    public const string Stopped = "stopped";
    public const string Failed = "failed";
    // install-downloads.sh has not been run on this machine.
    public const string NotInstalled = "notInstalled";
    // ManageServices is off: qBittorrent and Prowlarr are someone else's to run.
    public const string External = "external";
}

// The VPN, qBittorrent and Prowlarr as one thing that is on or off. The real one drives the
// systemd units install-downloads.sh installed; tests swap in a fake.
public interface IMediaDownloadStack
{
    Task<string> StateAsync(CancellationToken cancellationToken);
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task<MediaVpnStatus> VpnStatusAsync(CancellationToken cancellationToken);
}

public sealed class SystemdMediaDownloadStack(AppSettingsFile settings, IConfiguration configuration, ILogger<SystemdMediaDownloadStack> logger) : IMediaDownloadStack
{
    public const string Target = "sentinelcore-downloads.target";
    private static readonly string[] Units = ["sentinelcore-vpn.service", "sentinelcore-qbittorrent.service", "sentinelcore-prowlarr.service"];

    // A status older than this means the watcher stopped writing - the tunnel is not answering.
    private static readonly TimeSpan StatusFreshFor = TimeSpan.FromSeconds(150);

    private string StatusPath => configuration["MediaDownloads:VpnStatusPath"] ?? "/run/sentinelcore-vpn/status.json";

    public async Task<string> StateAsync(CancellationToken cancellationToken)
    {
        if (!(await MediaDownloadManager.ReadOptionsAsync(settings, cancellationToken)).ManageServices) return MediaDownloadServiceStates.External;
        if (!OperatingSystem.IsLinux()) return MediaDownloadServiceStates.NotInstalled;
        var (loadCode, load) = await RunAsync(["show", "-p", "LoadState", "--value", Target], cancellationToken);
        if (loadCode != 0 || load.Trim() != "loaded") return MediaDownloadServiceStates.NotInstalled;
        var (_, output) = await RunAsync(["is-active", .. Units], cancellationToken);
        var states = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (states.Length == Units.Length && states.All(s => s == "active")) return MediaDownloadServiceStates.Running;
        if (states.Any(s => s == "failed")) return MediaDownloadServiceStates.Failed;
        if (states.Any(s => s is "activating" or "active" or "reloading")) return MediaDownloadServiceStates.Starting;
        return MediaDownloadServiceStates.Stopped;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!(await MediaDownloadManager.ReadOptionsAsync(settings, cancellationToken)).ManageServices) return;
        // A unit that failed last time stays failed until reset, and start alone will not retry it.
        await RunAsync(["reset-failed", .. Units], cancellationToken);
        var (code, output) = await RunAsync(["start", "--no-block", Target], cancellationToken);
        if (code != 0)
            throw new InvalidOperationException(output.Contains("Access denied", StringComparison.OrdinalIgnoreCase) || output.Contains("authentication", StringComparison.OrdinalIgnoreCase)
                ? "This server is not allowed to start the download services. Run install-downloads.sh again; it installs the permission."
                : $"Could not start the download services: {output.Trim()}");
        logger.LogInformation("Movie downloads: starting the VPN and torrent services");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!(await MediaDownloadManager.ReadOptionsAsync(settings, cancellationToken)).ManageServices) return;
        var (code, output) = await RunAsync(["stop", "--no-block", Target], cancellationToken);
        if (code != 0) logger.LogWarning("Movie downloads: could not stop the services: {Output}", output.Trim());
        else logger.LogInformation("Movie downloads: stopped the VPN and torrent services");
    }

    public Task<MediaVpnStatus> VpnStatusAsync(CancellationToken cancellationToken) => Task.FromResult(ReadStatus(StatusPath, DateTimeOffset.UtcNow));

    // The watcher writes ipinfo.io's answer as-is when the tunnel works, or { "error": "..." }
    // when it does not; the file's age says how current either is.
    public static MediaVpnStatus ReadStatus(string path, DateTimeOffset now)
    {
        FileInfo file = new(path);
        if (!file.Exists) return new MediaVpnStatus(MediaVpnStatus.Off);
        var checkedAt = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            string? Str(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (now - checkedAt > StatusFreshFor)
                return new MediaVpnStatus(MediaVpnStatus.Down, CheckedAt: checkedAt, Error: "The VPN has not answered for a while.");
            if (Str("error") is { } error)
                return new MediaVpnStatus(root.TryGetProperty("connecting", out var c) && c.ValueKind == JsonValueKind.True ? MediaVpnStatus.Connecting : MediaVpnStatus.Down,
                    CheckedAt: checkedAt, Error: error);
            return Str("ip") is { Length: > 0 } ip
                ? new MediaVpnStatus(MediaVpnStatus.Connected, ip, Str("city"), Str("region"), Str("country"), Str("org") ?? Str("organization"), checkedAt)
                : new MediaVpnStatus(MediaVpnStatus.Unknown, CheckedAt: checkedAt);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new MediaVpnStatus(MediaVpnStatus.Connecting, CheckedAt: checkedAt);
        }
    }

    private static async Task<(int Code, string Output)> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("systemctl") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // Never ask for a password on a terminal nobody is at; polkit either allows it or not.
        info.ArgumentList.Add("--no-ask-password");
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(info)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await stdout + await stderr);
        }
        catch (System.ComponentModel.Win32Exception ex) { return (-1, ex.Message); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return (-1, "systemctl did not answer."); }
    }
}
