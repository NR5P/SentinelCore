using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace SentinelCore.Web;

// A single WS-Discovery Probe, sent either unicast to one host or multicast to the whole LAN
// segment. This is the actual standard IP cameras use for discovery - every ONVIF-compliant
// camera (Hikvision, Dahua, Reolink, Amcrest, TP-Link, and most others) answers it, so this is
// what makes "scan my network for cameras" and "find this camera's real stream URL" both work
// without guessing. Cameras that don't speak ONVIF at all (or have it disabled) never reply -
// CameraDiscoveryService falls back to guessing common RTSP paths for those.
public static class WsDiscoveryClient
{
    private const int DiscoveryPort = 3702;
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.255.250");

    private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Wsa = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
    private static readonly XNamespace Wsdd = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
    private static readonly XNamespace Dn = "http://www.onvif.org/ver10/network/wsdl";

    public sealed record ProbeMatch(string EndpointAddress, IReadOnlyList<string> XAddrs, IReadOnlyList<string> Scopes);

    // What a camera volunteers about itself in its Probe reply, before any authentication.
    // Worth having because GetDeviceInformation - the "proper" way to ask the same question -
    // requires WS-Security credentials on most firmware (every Reolink tested here answers it
    // with ter:NotAuthorized), so a scan that only has ONVIF to go on can do no better than
    // "Camera at 192.168.1.50" for a camera that is perfectly happy to say it's an RLC-823S1.
    public sealed record ScopeInfo(string? Name, string? Hardware, string? Location);

    // Scope URIs look like onvif://www.onvif.org/hardware/RLC-823S1 - a category path plus a
    // value, percent-encoded per the ONVIF spec (a camera named "Front Door" sends Front%20Door).
    //
    // Cameras really do send the same category twice - the RLC-823S1 advertises both
    // name/IPC-BO and name/IPC - so first-wins rather than last-wins or concatenating, which
    // matches how the value is used: a display hint, not an identity.
    public static ScopeInfo ParseScopes(IEnumerable<string> scopes)
    {
        string? name = null, hardware = null, location = null;

        foreach (var scope in scopes)
        {
            if (!Uri.TryCreate(scope, UriKind.Absolute, out var uri)) continue;

            // AbsolutePath keeps the leading slash: "/hardware/RLC-823S1". Location is
            // deliberately allowed to keep inner slashes ("country/china") - it's the one
            // category the spec defines as a hierarchy rather than a single token.
            var path = uri.AbsolutePath.TrimStart('/');
            var separator = path.IndexOf('/');
            if (separator <= 0 || separator == path.Length - 1) continue;

            var category = path[..separator];
            var value = Uri.UnescapeDataString(path[(separator + 1)..]).Trim();
            if (value.Length == 0) continue;

            if (name is null && category.Equals("name", StringComparison.OrdinalIgnoreCase)) name = value;
            else if (hardware is null && category.Equals("hardware", StringComparison.OrdinalIgnoreCase)) hardware = value;
            else if (location is null && category.Equals("location", StringComparison.OrdinalIgnoreCase)) location = value;
        }

        return new ScopeInfo(name, hardware, location);
    }

    // Straight to one host, no multicast join required. Most ONVIF cameras answer a unicast
    // Probe exactly like a multicast one - unicast vs multicast only affects how the request
    // reaches the device, not how it replies - so a single-IP lookup gets the camera's real
    // ONVIF service address instead of guessing the conventional /onvif/device_service path.
    public static async Task<ProbeMatch?> ProbeUnicastAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var client = new UdpClient(AddressFamily.InterNetwork);
        var probe = BuildProbeMessage(out var messageId);
        await client.SendAsync(probe, probe.Length, new IPEndPoint(address, DiscoveryPort));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var result = await client.ReceiveAsync(timeoutCts.Token);
                if (TryParseProbeMatch(result.Buffer, messageId, out var match))
                    return match;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // One send per qualifying local NIC - a single unbound UdpClient only egresses via the
    // default route, which would miss cameras reachable only through a second interface.
    public static async Task<IReadOnlyList<ProbeMatch>> ProbeMulticastAsync(
        IReadOnlyList<IPAddress> localAddresses, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var perInterface = await Task.WhenAll(
            localAddresses.Select(local => ProbeMulticastFromAsync(local, timeout, cancellationToken)));

        var seen = new HashSet<string>();
        var matches = new List<ProbeMatch>();
        foreach (var match in perInterface.SelectMany(r => r))
        {
            if (seen.Add(match.EndpointAddress))
                matches.Add(match);
        }
        return matches;
    }

    private static async Task<List<ProbeMatch>> ProbeMulticastFromAsync(IPAddress localAddress, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var results = new List<ProbeMatch>();
        try
        {
            using var client = new UdpClient(AddressFamily.InterNetwork);
            client.Client.Bind(new IPEndPoint(localAddress, 0));
            client.JoinMulticastGroup(MulticastAddress, localAddress);

            var probe = BuildProbeMessage(out var messageId);
            await client.SendAsync(probe, probe.Length, new IPEndPoint(MulticastAddress, DiscoveryPort));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            while (true)
            {
                UdpReceiveResult result;
                try { result = await client.ReceiveAsync(timeoutCts.Token); }
                catch (OperationCanceledException) { break; }

                if (TryParseProbeMatch(result.Buffer, messageId, out var match))
                    results.Add(match!);
            }
        }
        catch (SocketException)
        {
            // This interface can't be used for discovery (no multicast support, permission
            // denied, etc.) - skip it rather than failing the whole scan over one bad NIC.
        }
        return results;
    }

    // Public so tests can build a probe and check its shape without a socket.
    public static byte[] BuildProbeMessage(out string messageId)
    {
        messageId = $"uuid:{Guid.NewGuid()}";

        var envelope = new XElement(Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "e", Soap.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "w", Wsa.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "d", Wsdd.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "dn", Dn.NamespaceName),
            new XElement(Soap + "Header",
                new XElement(Wsa + "MessageID", messageId),
                new XElement(Wsa + "To", "urn:schemas-xmlsoap-org:ws:2005:04:discovery"),
                new XElement(Wsa + "Action", "http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe")),
            new XElement(Soap + "Body",
                new XElement(Wsdd + "Probe",
                    new XElement(Wsdd + "Types", "dn:NetworkVideoTransmitter"))));

        return Encoding.UTF8.GetBytes(envelope.ToString(SaveOptions.DisableFormatting));
    }

    // Public so tests can feed a canned ProbeMatch reply through the same parser used against
    // real cameras, without needing hardware or a socket.
    public static bool TryParseProbeMatch(byte[] datagram, string expectedRelatesTo, out ProbeMatch? match)
    {
        match = null;
        try
        {
            var doc = XDocument.Parse(Encoding.UTF8.GetString(datagram));

            var relatesTo = doc.Descendants(Wsa + "RelatesTo").FirstOrDefault()?.Value;
            if (relatesTo != expectedRelatesTo) return false;

            var probeMatch = doc.Descendants(Wsdd + "ProbeMatch").FirstOrDefault();
            if (probeMatch is null) return false;

            var address = probeMatch.Element(Wsa + "EndpointReference")?.Element(Wsa + "Address")?.Value;
            var xAddrsRaw = probeMatch.Element(Wsdd + "XAddrs")?.Value;
            if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(xAddrsRaw)) return false;

            var xAddrs = xAddrsRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (xAddrs.Length == 0) return false;

            // Scopes are optional in the spec and absent on some firmware - a missing element is
            // an empty list, never a parse failure, since XAddrs alone is enough to use the camera.
            var scopesRaw = probeMatch.Element(Wsdd + "Scopes")?.Value;
            var scopes = string.IsNullOrWhiteSpace(scopesRaw)
                ? []
                : scopesRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            match = new ProbeMatch(address, xAddrs, scopes);
            return true;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or FormatException)
        {
            return false;
        }
    }
}
