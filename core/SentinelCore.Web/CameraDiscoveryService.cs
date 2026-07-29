using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace SentinelCore.Web;

public enum CameraProbeStatus
{
    // A working stream was found and verified - StreamUrl is real and playable.
    Found,

    // An ONVIF camera answered but rejected the supplied credentials (or none were given) -
    // distinct from NotFound so the UI can ask for a username/password rather than suggesting
    // the address doesn't have a camera at all.
    NeedsCredentials,

    NotFound
}

public enum ScanStatus { Running, Completed }

// One candidate local interface the network scan could send its WS-Discovery multicast Probe
// out of. LooksVirtual is a heuristic (name/description substring match against common virtual
// adapter naming) and deliberately not trusted on its own - the scan endpoint asks the user to
// confirm which interfaces to use rather than silently guessing, since a wrong guess here means
// either scanning nothing useful or sweeping a VPN-attached subnet by accident.
public sealed record ScanInterface(string Name, string Description, string Address, int PrefixLength, bool LooksVirtual);

public sealed record ScanResultCamera(
    string IpAddress,
    string? Manufacturer,
    string? Model,
    string SuggestedName,
    bool NeedsCredentials,
    bool AlreadySaved);

public sealed record ScanJobSnapshot(ScanStatus Status, IReadOnlyList<ScanResultCamera> Results);

public sealed record CameraProbeResult(
    CameraProbeStatus Status,
    string? StreamUrl,
    string? SuggestedName,
    string? Manufacturer,
    string? Model,
    string? Resolution,
    string Message,
    // Only ever populated on the ONVIF success path (TryOnvifAsync) - the RTSP-template fallback
    // has no ONVIF service to ask, so PTZ simply isn't detectable for those cameras. A missing
    // PTZ service is the normal case, not a failure - most cameras don't have one.
    bool SupportsPtz = false,
    string? OnvifPtzServiceUrl = null,
    string? OnvifProfileToken = null);

// Turns "an IP address, maybe with credentials" into a working camera StreamUrl, the same
// information the manual Add Camera form asks the user to already know. Tries the real ONVIF
// discovery/media APIs first - which hand back the camera's actual configured stream URI rather
// than guessing - and only falls back to a curated table of common vendor RTSP paths when ONVIF
// is absent or the credentials don't work.
public sealed class CameraDiscoveryService
{
    private static readonly TimeSpan WsDiscoveryTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FfprobeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TcpProbeTimeout = TimeSpan.FromMilliseconds(800);
    private const int RtspDefaultPort = 554;

    // Common vendor RTSP path conventions, tried in rough order of how many cameras use them.
    // Not exhaustive - this is the fallback for cameras that don't speak ONVIF at all, not the
    // primary mechanism.
    private static readonly string[] RtspPathTemplates =
    [
        "/Streaming/Channels/101",              // Hikvision and many Hikvision-OEM cameras
        "/cam/realmonitor?channel=1&subtype=0", // Dahua, Amcrest (Dahua OEM)
        "/live",
        "/stream1",
        "/videoMain",
        "/h264/ch1/main/av_stream",
        "/live/ch0"
    ];

    private static readonly TimeSpan JobRetention = TimeSpan.FromMinutes(5);

    // Naming/description substrings for common virtual adapters (Docker, VPN clients, WSL,
    // Hyper-V's internal switch). Necessarily incomplete - VPN client adapter names vary widely -
    // which is exactly why this only informs LooksVirtual rather than silently excluding anything.
    private static readonly string[] VirtualAdapterHints =
        ["docker", "veth", "br-", "virbr", "vmnet", "vboxnet", "tun", "tap", "wsl", "hyper-v virtual switch"];

    private readonly OnvifClient _onvif;
    private readonly ICameraStore _cameraStore;
    private readonly string _ffprobePath;
    private readonly ILogger<CameraDiscoveryService> _logger;
    private readonly ConcurrentDictionary<Guid, ScanJob> _scans = new();

    public CameraDiscoveryService(
        OnvifClient onvif, ICameraStore cameraStore, IConfiguration configuration, ILogger<CameraDiscoveryService> logger)
    {
        _onvif = onvif;
        _cameraStore = cameraStore;
        _ffprobePath = configuration["CameraDiscovery:FfprobePath"] ?? DeriveFfprobePath(configuration["FfmpegPath"] ?? "ffmpeg");
        _logger = logger;
    }

    public async Task<CameraProbeResult> ProbeCameraAsync(
        IPAddress ip, string? username, string? password, CancellationToken cancellationToken)
    {
        var wsMatch = await WsDiscoveryClient.ProbeUnicastAsync(ip, WsDiscoveryTimeout, cancellationToken);
        var deviceServiceUrl = wsMatch?.XAddrs.FirstOrDefault();

        if (deviceServiceUrl is not null)
        {
            var onvifResult = await TryOnvifAsync(ip, deviceServiceUrl, username, password, cancellationToken);
            if (onvifResult is not null) return onvifResult;
        }

        // ONVIF absent, or present but the credentials didn't work and it's worth checking
        // whether the camera is reachable by a plain guessed RTSP path anyway.
        return await TryRtspTemplatesAsync(ip, username, password, cancellationToken);
    }

    private async Task<CameraProbeResult?> TryOnvifAsync(
        IPAddress ip, string deviceServiceUrl, string? username, string? password, CancellationToken cancellationToken)
    {
        var deviceInfo = await _onvif.GetDeviceInformationAsync(deviceServiceUrl, username, password, cancellationToken);
        if (deviceInfo is null)
        {
            // A camera that answers WS-Discovery but rejects GetDeviceInformation is a real
            // ONVIF camera that still needs credentials - worth reporting distinctly rather than
            // falling straight through to guessing RTSP paths, which would very likely also fail
            // for the same reason (wrong/missing auth) and just be a confusing dead end.
            return string.IsNullOrWhiteSpace(username)
                ? new CameraProbeResult(CameraProbeStatus.NeedsCredentials, null, null, null, null, null,
                    "An ONVIF camera was found at this address, but it needs a username and password.")
                : null;
        }

        var mediaServiceUrl = await _onvif.GetMediaServiceUrlAsync(deviceServiceUrl, username, password, cancellationToken)
            ?? deviceServiceUrl;
        var profiles = await _onvif.GetProfileTokensAsync(mediaServiceUrl, username, password, cancellationToken);
        if (profiles.Count == 0) return null;

        var streamUrl = await _onvif.GetStreamUriAsync(mediaServiceUrl, profiles[0], username, password, cancellationToken);
        if (string.IsNullOrWhiteSpace(streamUrl)) return null;

        // Verify with credentials embedded (the same way HlsStreamManager.BuildAuthenticatedUrl
        // does for saved cameras) but report the bare URL back for storage - Camera keeps
        // StreamUrl and Username/Password as separate fields, embedding only happens at
        // ffmpeg-invocation time.
        var urlToVerify = string.IsNullOrWhiteSpace(username) ? streamUrl : EmbedCredentials(streamUrl, username!, password);
        var verified = await VerifyStreamAsync(urlToVerify, cancellationToken);
        if (verified is null) return null;

        var suggestedName = deviceInfo is { Manufacturer: "Unknown", Model: "Unknown" }
            ? $"Camera at {ip}"
            : $"{deviceInfo.Manufacturer} {deviceInfo.Model}".Trim();

        // PTZ is optional and most cameras don't have it - a null service URL here just means
        // "no PTZ," which is not treated as a probe failure.
        var ptzServiceUrl = await _onvif.GetPtzServiceUrlAsync(deviceServiceUrl, username, password, cancellationToken);
        var supportsPtz = !string.IsNullOrWhiteSpace(ptzServiceUrl);

        return new CameraProbeResult(
            CameraProbeStatus.Found, streamUrl, suggestedName, deviceInfo.Manufacturer, deviceInfo.Model,
            verified.Resolution, "Found via ONVIF.",
            SupportsPtz: supportsPtz,
            OnvifPtzServiceUrl: supportsPtz ? ptzServiceUrl : null,
            OnvifProfileToken: supportsPtz ? profiles[0] : null);
    }

    private async Task<CameraProbeResult> TryRtspTemplatesAsync(
        IPAddress ip, string? username, string? password, CancellationToken cancellationToken)
    {
        // Every template below targets RTSP's default port 554 - check it's even open once,
        // rather than paying for up to seven ffprobe process spawns (each with its own multi-
        // second timeout) against a host that plainly isn't listening. Without this, testing a
        // typo'd or genuinely-unreachable IP - the single most common thing a user will actually
        // do with this button - would take the better part of a minute instead of a few seconds.
        if (!await IsPortOpenAsync(ip, RtspDefaultPort, TcpProbeTimeout, cancellationToken))
        {
            return new CameraProbeResult(
                CameraProbeStatus.NotFound, null, null, null, null, null,
                "No camera found at this address - nothing is listening on the standard RTSP port.");
        }

        foreach (var path in RtspPathTemplates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bareUrl = $"rtsp://{ip}{path}";
            var urlToVerify = string.IsNullOrWhiteSpace(username) ? bareUrl : EmbedCredentials(bareUrl, username!, password);

            var verified = await VerifyStreamAsync(urlToVerify, cancellationToken);
            if (verified is not null)
            {
                return new CameraProbeResult(
                    CameraProbeStatus.Found, bareUrl, $"Camera at {ip}", null, null,
                    verified.Resolution, "Found by testing common camera URL patterns - this camera doesn't appear to support ONVIF.");
            }
        }

        return new CameraProbeResult(
            CameraProbeStatus.NotFound, null, null, null, null, null,
            "No camera found at this address. It may not support ONVIF, or use a stream path this app doesn't know to try.");
    }

    private static async Task<bool> IsPortOpenAsync(IPAddress ip, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(ip, port, timeoutCts.Token);
            return client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static string EmbedCredentials(string url, string username, string? password)
    {
        var builder = new UriBuilder(url) { UserName = username, Password = password ?? string.Empty };
        return builder.Uri.ToString();
    }

    // Public alongside ParseFfprobeOutput, which returns it and is itself public so tests can
    // exercise the parsing logic against canned ffprobe output without spawning a process.
    public sealed record StreamProbeResult(int? Width, int? Height, string? Codec)
    {
        public string? Resolution => Width is int w && Height is int h ? $"{w}x{h}" : null;
    }

    // Verifies a candidate URL actually carries a video stream, without ever writing anything to
    // disk - the JSON comes off stdout directly, unlike HlsStreamManager.CaptureSnapshotAsync's
    // frame-grab, which needs a temp file because a JPEG isn't something you can meaningfully
    // read off stdout as text. -rtsp_transport tcp is forced unconditionally, matching
    // HlsStreamManager's own always-tcp behavior even for non-RTSP URLs - that's the one
    // configuration already proven against real cameras in this app.
    private async Task<StreamProbeResult?> VerifyStreamAsync(string url, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[]
        {
            "-rtsp_transport", "tcp",
            "-rw_timeout", "5000000",
            "-v", "error",
            "-print_format", "json",
            "-show_streams",
            url
        })
            psi.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffprobe.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Could not start ffprobe at {Path}", _ffprobePath);
            return null;
        }

        using (process)
        {
            _ = DiscardAsync(process.StandardError);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(FfprobeTimeout);

            string stdout;
            try
            {
                var readTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                await process.WaitForExitAsync(timeoutCts.Token);
                stdout = await readTask;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout)) return null;
            return ParseFfprobeOutput(stdout);
        }
    }

    // Public so tests can check the JSON-parsing logic against canned ffprobe output without
    // spawning a process.
    public static StreamProbeResult? ParseFfprobeOutput(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("streams", out var streams)) return null;

            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("codec_type", out var codecType) || codecType.GetString() != "video")
                    continue;

                var width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : (int?)null;
                var height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : (int?)null;
                var codec = stream.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                return new StreamProbeResult(width, height, codec);
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task DiscardAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() != null) { }
        }
        catch
        {
            // process exited; nothing to do
        }
    }

    private static string DeriveFfprobePath(string ffmpegPath)
    {
        var directory = Path.GetDirectoryName(ffmpegPath);
        if (string.IsNullOrEmpty(directory)) return "ffprobe"; // bare "ffmpeg" resolves via PATH - same for ffprobe
        var fileName = Path.GetFileName(ffmpegPath).Replace("ffmpeg", "ffprobe", StringComparison.OrdinalIgnoreCase);
        return Path.Combine(directory, fileName);
    }

    // --- Network scan -----------------------------------------------------------------------
    //
    // Deliberately WS-Discovery multicast only, no TCP subnet sweep - a single UDP broadcast per
    // interface doesn't touch individual hosts at all, so it needs no size ceiling, no per-host
    // consent, and stays a good citizen on someone else's LAN. A TCP-sweep "deep scan" for
    // non-ONVIF cameras is real future work, deliberately left out of this first pass.
    //
    // This never runs on its own - only in direct response to StartScan being called, which only
    // happens from a button click via CameraDiscoveryEndpoints. No timer, no background loop.

    // Public so the heuristic itself is directly testable without needing to fabricate a real
    // NetworkInterface, which .NET gives no supported way to construct outside the OS.
    public static bool IsLikelyVirtualAdapter(string name, string description) =>
        VirtualAdapterHints.Any(hint =>
            name.Contains(hint, StringComparison.OrdinalIgnoreCase) ||
            description.Contains(hint, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<ScanInterface> GetCandidateInterfaces()
    {
        var results = new List<ScanInterface>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
                continue;

            var looksVirtual = IsLikelyVirtualAdapter(nic.Name, nic.Description);

            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                results.Add(new ScanInterface(nic.Name, nic.Description, address.Address.ToString(), address.PrefixLength, looksVirtual));
            }
        }
        return results;
    }

    // Fire-and-forget by design: the caller gets a job id back immediately and polls
    // GetScanStatus for progress, the same job-tracking shape HlsStreamManager already uses for
    // its per-camera sessions.
    public Guid StartScan(IReadOnlyList<IPAddress> interfaceAddresses)
    {
        PruneStaleJobs();

        var jobId = Guid.NewGuid();
        var job = new ScanJob();
        _scans[jobId] = job;

        _ = RunScanAsync(job, interfaceAddresses);

        return jobId;
    }

    public ScanJobSnapshot? GetScanStatus(Guid jobId) =>
        _scans.TryGetValue(jobId, out var job) ? new ScanJobSnapshot(job.Status, job.Results.ToList()) : null;

    private async Task RunScanAsync(ScanJob job, IReadOnlyList<IPAddress> interfaceAddresses)
    {
        try
        {
            var matches = await WsDiscoveryClient.ProbeMulticastAsync(interfaceAddresses, WsDiscoveryTimeout, CancellationToken.None);
            var existingHosts = (await _cameraStore.GetAllAsync(CancellationToken.None))
                .Select(camera => TryExtractHost(camera.StreamUrl))
                .Where(host => host is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var match in matches)
            {
                var deviceServiceUrl = match.XAddrs.FirstOrDefault();
                if (deviceServiceUrl is null || !Uri.TryCreate(deviceServiceUrl, UriKind.Absolute, out var deviceUri))
                    continue;

                var ip = deviceUri.Host;

                // No credentials at scan time - there's no way to ask for them per-camera before
                // even knowing how many cameras are out there. Many cameras still answer
                // GetDeviceInformation without auth even when GetStreamUri requires it, which is
                // enough to suggest a name; NeedsCredentials tells the UI to ask before the user
                // tries to add it.
                var deviceInfo = await _onvif.GetDeviceInformationAsync(deviceServiceUrl, null, null, CancellationToken.None);
                var suggestedName = deviceInfo is not null && (deviceInfo.Manufacturer != "Unknown" || deviceInfo.Model != "Unknown")
                    ? $"{deviceInfo.Manufacturer} {deviceInfo.Model}".Trim()
                    : $"Camera at {ip}";

                job.Results.Add(new ScanResultCamera(
                    ip, deviceInfo?.Manufacturer, deviceInfo?.Model, suggestedName,
                    NeedsCredentials: deviceInfo is null, existingHosts.Contains(ip)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Camera network scan failed");
        }
        finally
        {
            job.Status = ScanStatus.Completed;
        }
    }

    private static string? TryExtractHost(string? streamUrl) =>
        Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri) ? uri.Host : null;

    private void PruneStaleJobs()
    {
        var cutoff = DateTimeOffset.UtcNow - JobRetention;
        foreach (var (id, job) in _scans)
        {
            if (job.Status == ScanStatus.Completed && job.StartedAt < cutoff)
                _scans.TryRemove(id, out _);
        }
    }

    private sealed class ScanJob
    {
        public ScanStatus Status { get; set; } = ScanStatus.Running;
        public List<ScanResultCamera> Results { get; } = [];
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    }
}
