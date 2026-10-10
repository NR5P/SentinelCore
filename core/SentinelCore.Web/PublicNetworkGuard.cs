using System.Net;
using System.Net.Sockets;

namespace SentinelCore.Web;

// Keeps fetches of user-supplied URLs (podcast feeds, enclosures, artwork, transcripts) on the
// public internet.
//
// Any signed-in account can subscribe to a feed, and the server then fetches every URL in it and
// relays the audio back. Without this, a feed whose enclosure is http://127.0.0.1:11984/api/streams
// streams go2rtc's camera list - every camera's rtsp://user:password@ URL - to whoever asked, and
// the same trick reaches the router, the cameras' own admin pages and anything else on the LAN.
//
// The check runs in the connect step rather than on the URL text, so it sees the address DNS
// actually returned (a public-looking name can resolve to 127.0.0.1) and applies to every hop of a
// redirect, which a check of the first URL would not.
//
// Podcasts:AllowPrivateNetworkAddresses (dashboard config) turns it off, for someone who really
// does host their own feeds on their LAN - and for the tests, whose fake feed server is loopback.
public static class PublicNetworkGuard
{
    public const string AllowPrivateKey = "Podcasts:AllowPrivateNetworkAddresses";

    public static SocketsHttpHandler Apply(SocketsHttpHandler handler, IConfiguration configuration)
    {
        if (configuration.GetValue(AllowPrivateKey, false)) return handler;

        // A proxy would make the connect step reach the proxy, not the target, and let the proxy
        // fetch the private address on our behalf.
        handler.UseProxy = false;
        handler.ConnectCallback = ConnectPublicOnlyAsync;
        return handler;
    }

    public static async ValueTask<Stream> ConnectPublicOnlyAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken);

        var allowed = addresses.Where(IsPublic).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException(
                $"Refused to connect to {host}: it is a local or private network address, and links in podcast feeds may only point at the public internet.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // For the one fetch that doesn't go through HttpClient: ffmpeg reading a clip's source
    // straight from the publisher. Checks what the host resolves to right now.
    public static async Task<bool> PointsAtPublicInternetAsync(string url, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (configuration.GetValue(AllowPrivateKey, false)) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        try
        {
            var addresses = IPAddress.TryParse(uri.DnsSafeHost, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
            return addresses.Length > 0 && addresses.All(IsPublic);
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0                                   // "this network"
                || b[0] == 10                                    // RFC 1918
                || b[0] == 100 && (b[1] & 0xC0) == 64            // 100.64/10 CGNAT, Tailscale
                || b[0] == 127                                   // loopback
                || b[0] == 169 && b[1] == 254                    // link-local, cloud metadata
                || b[0] == 172 && (b[1] & 0xF0) == 16            // RFC 1918
                || b[0] == 192 && b[1] == 0 && b[2] is 0 or 2    // IETF protocol, TEST-NET-1
                || b[0] == 192 && b[1] == 168                    // RFC 1918
                || b[0] == 198 && (b[1] & 0xFE) == 18            // benchmarking
                || b[0] == 198 && b[1] == 51 && b[2] == 100      // TEST-NET-2
                || b[0] == 203 && b[1] == 0 && b[2] == 113       // TEST-NET-3
                || b[0] >= 224);                                 // multicast, reserved, broadcast
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any)
                || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal)
                return false;

            var b = address.GetAddressBytes();
            // 2001:db8::/32 documentation.
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false;
            // NAT64 (64:ff9b::/96) and 6to4 (2002::/16) carry an IPv4 address: judge that one.
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4..12].All(x => x == 0))
                return IsPublic(new IPAddress(b[12..16]));
            if (b[0] == 0x20 && b[1] == 0x02)
                return IsPublic(new IPAddress(b[2..6]));
            return true;
        }

        return false;
    }
}
