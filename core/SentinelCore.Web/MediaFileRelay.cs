using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SentinelCore.Web;

// ffmpeg and ffprobe cannot read SMB, so a video on a network share reaches them as
// http://127.0.0.1:<port>/<secret>/<path> served from here: a tiny HTTP/1.1 server that answers
// GET and HEAD with byte ranges (ffmpeg seeks with them) and closes after every response.
//
// Only loopback can connect, every URL carries a random per-process secret, and only network
// paths are served - so it cannot become a way to read the server's own disk.
public sealed class MediaFileRelay(MediaNetworkFileSystem network, ILogger<MediaFileRelay> logger) : IDisposable
{
    private readonly string _secret = RandomNumberGenerator.GetHexString(32, lowercase: true);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private TcpListener? _listener;

    public string UrlFor(string path)
    {
        var port = EnsureStarted();
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(path)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        // A readable name last, so ffmpeg's format guess and its log lines see "video.mkv".
        var name = Uri.EscapeDataString(Path.GetFileName(path));
        return $"http://127.0.0.1:{port}/{_secret}/{token}/{name}";
    }

    private int EnsureStarted()
    {
        lock (_gate)
        {
            if (_listener is not null) return ((IPEndPoint)_listener.LocalEndpoint).Port;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _listener = listener;
            _ = AcceptLoopAsync(listener, _shutdown.Token);
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cancellationToken); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex) { logger.LogWarning(ex, "Media relay accept failed"); continue; }
            _ = Task.Run(() => HandleAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        try
        {
            var request = await ReadHeadAsync(stream, cancellationToken);
            if (request is null) return;
            var (method, target, headers) = request.Value;
            if (method is not ("GET" or "HEAD")) { await WriteStatusAsync(stream, 405, "Method Not Allowed", cancellationToken); return; }

            var parts = target.Split('?', 2)[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
            string? path = null;
            if (parts.Length >= 2 && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[0]), Encoding.ASCII.GetBytes(_secret)))
            {
                try
                {
                    var base64 = parts[1].Replace('-', '+').Replace('_', '/');
                    base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
                    path = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                }
                catch (FormatException) { path = null; }
            }
            if (path is null || !MediaNetworkPath.IsNetwork(path)) { await WriteStatusAsync(stream, 404, "Not Found", cancellationToken); return; }

            Stream file;
            try { file = network.OpenRead(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Media relay could not open {Path}: {Message}", path, ex.Message);
                await WriteStatusAsync(stream, 404, "Not Found", cancellationToken);
                return;
            }

            await using (file)
            {
                var length = file.Length;
                long start = 0, end = length - 1;
                var partial = false;
                if (headers.TryGetValue("range", out var range) && TryParseRange(range, length, out var rangeStart, out var rangeEnd))
                {
                    (start, end, partial) = (rangeStart, rangeEnd, true);
                }
                else if (headers.ContainsKey("range"))
                {
                    await WriteAsync(stream, $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{length}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", cancellationToken);
                    return;
                }

                var count = Math.Max(0, end - start + 1);
                var head = new StringBuilder();
                head.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
                head.Append("Content-Type: application/octet-stream\r\nAccept-Ranges: bytes\r\nConnection: close\r\n");
                head.Append("Content-Length: ").Append(count.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                if (partial) head.Append($"Content-Range: bytes {start}-{end}/{length}\r\n");
                head.Append("\r\n");
                await WriteAsync(stream, head.ToString(), cancellationToken);
                if (method == "HEAD" || count == 0) return;

                file.Position = start;
                var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
                try
                {
                    var remaining = count;
                    while (remaining > 0)
                    {
                        var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                        if (read == 0) break;
                        await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        remaining -= read;
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
        }
        // ffmpeg hangs up mid-body whenever it seeks; that is the normal end of a request here.
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception ex) { logger.LogWarning(ex, "Media relay request failed"); }
    }

    public static bool TryParseRange(string header, long length, out long start, out long end)
    {
        start = 0;
        end = length - 1;
        var value = header.Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || value.Contains(',')) return false;
        var spec = value[6..].Split('-', 2);
        if (spec.Length != 2) return false;
        if (spec[0].Length == 0)
        {
            // "bytes=-500": the last 500 bytes.
            if (!long.TryParse(spec[1], NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, length - suffix);
            return length > 0;
        }
        if (!long.TryParse(spec[0], NumberStyles.None, CultureInfo.InvariantCulture, out start) || start >= length) return false;
        if (spec[1].Length > 0)
        {
            if (!long.TryParse(spec[1], NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start) return false;
            end = Math.Min(end, length - 1);
        }
        return true;
    }

    private static async Task<(string Method, string Target, Dictionary<string, string> Headers)?> ReadHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(1024);
        var one = new byte[1];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (bytes.Count < 16 * 1024)
        {
            var read = await stream.ReadAsync(one, timeout.Token);
            if (read == 0) return null;
            bytes.Add(one[0]);
            var n = bytes.Count;
            if (n >= 4 && bytes[n - 4] == '\r' && bytes[n - 3] == '\n' && bytes[n - 2] == '\r' && bytes[n - 1] == '\n') break;
        }
        var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return (first[0], first[1], headers);
    }

    private static Task WriteStatusAsync(NetworkStream stream, int code, string reason, CancellationToken cancellationToken) =>
        WriteAsync(stream, $"HTTP/1.1 {code} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", cancellationToken);

    private static async Task WriteAsync(NetworkStream stream, string text, CancellationToken cancellationToken) =>
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), cancellationToken);

    public void Dispose()
    {
        _shutdown.Cancel();
        lock (_gate) { _listener?.Stop(); _listener = null; }
        _shutdown.Dispose();
    }
}
