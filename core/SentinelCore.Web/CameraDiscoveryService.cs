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

    // RTSP, and the port most ONVIF implementations put the device service on. Checked in that
    // order and the host accepted on the first hit, so a camera normally costs one connect.
    //
    // Port 80 is deliberately NOT here, though nearly every camera has it open. Measured against
    // this LAN, adding it took a sweep from 4 hits to 13: the extra nine were the router, the
    // gateway, and assorted laptops and appliances with a web page on them. A list where two
    // thirds of the "cameras" are a printer is worse than a list that misses an HTTP-only camera -
    // especially as a camera exposing neither RTSP nor ONVIF is one this app couldn't stream anyway.
    private static readonly int[] DeepScanPorts = [RtspDefaultPort, 8000];
    private static readonly TimeSpan DeepScanPortTimeout = TimeSpan.FromMilliseconds(500);
    private const int DeepScanMaxHosts = 1024;
    private const int DeepScanConcurrency = 64;

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
    // Two passes. The default is WS-Discovery multicast only: a single UDP broadcast per interface
    // that doesn't touch individual hosts at all, so it needs no size ceiling and stays a good
    // citizen on someone else's LAN. The opt-in deep scan (RunDeepScanAsync) adds a bounded TCP
    // sweep for cameras that can't answer a multicast Probe at all - see the note there for why
    // it's a checkbox and not the default.
    //
    // Neither ever runs on its own - only in direct response to StartScan being called, which
    // only happens from a button click via CameraDiscoveryEndpoints. No timer, no background loop.

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
    public Guid StartScan(IReadOnlyList<IPAddress> interfaceAddresses, bool deepScan = false)
    {
        PruneStaleJobs();

        var jobId = Guid.NewGuid();
        var job = new ScanJob();
        _scans[jobId] = job;

        _ = RunScanAsync(job, interfaceAddresses, deepScan);

        return jobId;
    }

    public ScanJobSnapshot? GetScanStatus(Guid jobId) =>
        _scans.TryGetValue(jobId, out var job) ? new ScanJobSnapshot(job.Status, job.Snapshot()) : null;

    private async Task RunScanAsync(ScanJob job, IReadOnlyList<IPAddress> interfaceAddresses, bool deepScan)
    {
        try
        {
            var cameras = await _cameraStore.GetAllAsync(CancellationToken.None);
            var existingHosts = cameras
                .Select(camera => TryExtractHost(camera.StreamUrl))
                .Where(host => host is not null)
                .Select(host => host!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Credentials the admin has already proven work on this LAN. A second camera from the
            // same vendor is overwhelmingly likely to share them, and trying them turns an
            // anonymous "needs credentials" row into a fully identified one. Anonymous first, so
            // a camera that needs no auth never gets a stray failed login attempt against it.
            var knownCredentials = CollectKnownCredentials(cameras);

            var matches = await WsDiscoveryClient.ProbeMulticastAsync(interfaceAddresses, WsDiscoveryTimeout, CancellationToken.None);

            var seenHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var match in matches)
            {
                var deviceServiceUrl = match.XAddrs.FirstOrDefault();
                if (deviceServiceUrl is null || !Uri.TryCreate(deviceServiceUrl, UriKind.Absolute, out var deviceUri))
                    continue;

                var ip = deviceUri.Host;
                if (!seenHosts.Add(ip)) continue;

                job.AddResult(await DescribeOnvifCameraAsync(
                    ip, deviceServiceUrl, match.Scopes, knownCredentials, existingHosts));
            }

            if (deepScan)
            {
                await RunDeepScanAsync(job, interfaceAddresses, seenHosts, knownCredentials, existingHosts);
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

    // Turns a known ONVIF device address into a display row, preferring what the camera says
    // when asked properly (GetDeviceInformation) and falling back to what it already volunteered
    // in its unauthenticated Probe reply (Scopes).
    private async Task<ScanResultCamera> DescribeOnvifCameraAsync(
        string ip,
        string deviceServiceUrl,
        IReadOnlyList<string> scopes,
        IReadOnlyList<(string User, string? Password)> knownCredentials,
        HashSet<string> existingHosts)
    {
        var deviceInfo = await _onvif.GetDeviceInformationAsync(deviceServiceUrl, null, null, CancellationToken.None);
        foreach (var (user, password) in knownCredentials)
        {
            if (deviceInfo is not null) break;
            deviceInfo = await _onvif.GetDeviceInformationAsync(deviceServiceUrl, user, password, CancellationToken.None);
        }

        var scopeInfo = WsDiscoveryClient.ParseScopes(scopes);

        // "Unknown" is what GetDeviceInformationAsync substitutes for a field the camera omitted,
        // so it must not shadow a real value the scopes carry.
        var manufacturer = Prefer(deviceInfo?.Manufacturer, scopeInfo.Name);
        var model = Prefer(deviceInfo?.Model, scopeInfo.Hardware);

        return new ScanResultCamera(
            ip, manufacturer, model, BuildSuggestedName(manufacturer, model, ip),
            NeedsCredentials: deviceInfo is null, existingHosts.Contains(ip));
    }

    private static string? Prefer(string? authoritative, string? fallback) =>
        string.IsNullOrWhiteSpace(authoritative) || authoritative == "Unknown" ? fallback : authoritative;

    // Public so the naming rules are testable without a camera - "Camera at <ip>" is the only
    // acceptable output when nothing identifying is known, and a half-known camera should still
    // show the half it knows rather than falling all the way back to the address.
    public static string BuildSuggestedName(string? manufacturer, string? model, string ip)
    {
        var name = string.Join(' ', new[] { manufacturer, model }
            .Where(part => !string.IsNullOrWhiteSpace(part) && part != "Unknown"))
            .Trim();
        return name.Length == 0 ? $"Camera at {ip}" : name;
    }

    private static List<(string User, string? Password)> CollectKnownCredentials(IReadOnlyList<Camera> cameras)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<(string, string?)>();
        foreach (var camera in cameras)
        {
            if (string.IsNullOrWhiteSpace(camera.Username)) continue;
            if (!seen.Add($"{camera.Username}\n{camera.Password}")) continue;
            results.Add((camera.Username!, camera.Password));
        }
        return results;
    }

    // --- Deep scan --------------------------------------------------------------------------
    //
    // The opt-in second pass, for the cameras WS-Discovery structurally cannot find: firmware
    // shipped with ONVIF switched off, and - more commonly on a real home network - a camera on
    // a WiFi segment or VLAN whose AP does not forward multicast, which makes the Probe simply
    // never arrive. Neither case is a bug in the multicast pass; there is no reply to miss.
    //
    // This does touch every host in the subnet, which the multicast pass deliberately avoids, so
    // it stays behind an explicit checkbox rather than becoming the default. A single TCP connect
    // to a camera port is the same thing any camera app does, but it is still a port scan, and on
    // a network that isn't yours that is a rude thing to do unasked.
    private async Task RunDeepScanAsync(
        ScanJob job,
        IReadOnlyList<IPAddress> interfaceAddresses,
        HashSet<string> seenHosts,
        IReadOnlyList<(string User, string? Password)> knownCredentials,
        HashSet<string> existingHosts)
    {
        var interfaces = GetCandidateInterfaces();
        var localAddresses = interfaceAddresses.Select(a => a.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var address in interfaceAddresses)
        {
            var prefixLength = interfaces
                .FirstOrDefault(i => string.Equals(i.Address, address.ToString(), StringComparison.OrdinalIgnoreCase))
                ?.PrefixLength ?? 0;

            var hosts = EnumerateSubnetHosts(address, prefixLength, DeepScanMaxHosts);
            if (hosts.Count == 0)
            {
                _logger.LogInformation(
                    "Deep scan skipped {Address}/{Prefix} - not a scannable IPv4 subnet, or larger than {Max} hosts",
                    address, prefixLength, DeepScanMaxHosts);
                continue;
            }

            var open = await FindHostsWithCameraPortsAsync(hosts, localAddresses, seenHosts);

            foreach (var host in open)
            {
                var ip = host.ToString();
                if (!seenHosts.Add(ip)) continue;

                // Multicast may simply not have reached us; a unicast Probe straight at the host
                // is the same question asked in a way no AP can drop, and it still yields the
                // camera's real ONVIF address and scopes when it works.
                var match = await WsDiscoveryClient.ProbeUnicastAsync(host, WsDiscoveryTimeout, CancellationToken.None);
                var deviceServiceUrl = match?.XAddrs.FirstOrDefault();

                var result = deviceServiceUrl is not null
                    ? await DescribeOnvifCameraAsync(ip, deviceServiceUrl, match!.Scopes, knownCredentials, existingHosts)
                    // No ONVIF at all - all that's really known is "something is listening on a
                    // camera port here". Reported rather than dropped so the user can point Test
                    // Connection at it, which is the only thing that can identify it from here.
                    : new ScanResultCamera(
                        ip, null, null, $"Camera at {ip}",
                        NeedsCredentials: true, existingHosts.Contains(ip));

                job.AddResult(result);
            }
        }
    }

    private static async Task<List<IPAddress>> FindHostsWithCameraPortsAsync(
        IReadOnlyList<IPAddress> hosts, HashSet<string> localAddresses, HashSet<string> seenHosts)
    {
        var open = new List<IPAddress>();
        using var gate = new SemaphoreSlim(DeepScanConcurrency);

        var probes = hosts.Select(async host =>
        {
            var ip = host.ToString();
            if (localAddresses.Contains(ip) || seenHosts.Contains(ip)) return;

            await gate.WaitAsync();
            try
            {
                foreach (var port in DeepScanPorts)
                {
                    if (!await IsPortOpenAsync(host, port, DeepScanPortTimeout, CancellationToken.None)) continue;
                    lock (open) open.Add(host);
                    return;
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(probes);
        return open;
    }

    // Public so the subnet arithmetic - the part with the off-by-one and the sizing guard - is
    // testable without a NIC. Returns empty rather than throwing for anything not worth
    // sweeping: IPv6, a nonsensical prefix, a /31 or /32 with no host range, or a subnet wider
    // than maxHosts (a /16 is 65k connects, which is a different feature with different consent).
    public static IReadOnlyList<IPAddress> EnumerateSubnetHosts(IPAddress address, int prefixLength, int maxHosts)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return [];
        if (prefixLength is < 1 or > 30) return [];

        var bytes = address.GetAddressBytes();
        var host = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        var mask = uint.MaxValue << (32 - prefixLength);
        var network = host & mask;
        var broadcast = network | ~mask;

        var count = broadcast - network - 1;
        if (count == 0 || count > (uint)maxHosts) return [];

        var results = new List<IPAddress>((int)count);
        for (var current = network + 1; current < broadcast; current++)
        {
            results.Add(new IPAddress(new[]
            {
                (byte)(current >> 24), (byte)(current >> 16), (byte)(current >> 8), (byte)current
            }));
        }
        return results;
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

    // Results are appended by the scan and read by the UI's 1s poll at the same time, and the deep
    // scan appends from several probe tasks at once - so the list is never touched outside the
    // lock. A plain List<T> read via ToList() mid-Add can throw or return a torn snapshot.
    private sealed class ScanJob
    {
        private readonly List<ScanResultCamera> _results = [];

        public ScanStatus Status { get; set; } = ScanStatus.Running;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        public void AddResult(ScanResultCamera result)
        {
            lock (_results) _results.Add(result);
        }

        public IReadOnlyList<ScanResultCamera> Snapshot()
        {
            lock (_results) return _results.ToList();
        }
    }
}
