using System.Net;
using System.Net.Sockets;

namespace SentinelCore.Web;

// "What address do I type into the phone app?" - answered by the server itself, under the
// SERVER ADDRESS settings section, instead of leaving someone to find it with ifconfig or by
// reading their router's client list. Purely informational: nothing here is a setting, and
// nothing here changes what the server listens on.
//
// The addresses come from the same interface enumeration the camera network scan uses
// (CameraDiscoveryService.GetCandidateInterfaces), so an adapter that looks virtual is labelled
// as such in both places rather than judged twice by two different rules.
public static class LocalAddressEndpoints
{
    // Url is built here rather than in the browser because only the server knows which port it
    // actually bound and whether HTTPS came up - see DiscoveryEndpoint, which is resolved from
    // the same values Kestrel was configured with and is what mDNS advertises too.
    public sealed record LocalAddress(string Address, string Interface, bool LooksVirtual, string Url);

    public sealed record LocalAddressesResponse(
        string HostName,
        string Scheme,
        int Port,
        IReadOnlyList<LocalAddress> Addresses);

    public static void MapLocalAddressEndpoints(this WebApplication app)
    {
        // Admin-only to match the settings page it's shown on. Not a secret - anyone already
        // talking to this server knows one of these addresses - but there's no reason for a
        // Viewer's session to be able to map every interface on the box.
        app.MapGet("/api/system/addresses", (DiscoveryEndpoint endpoint) =>
            Results.Ok(Describe(endpoint, CameraDiscoveryService.GetCandidateInterfaces())))
            .RequireAuthorization("AdminOnly");
    }

    // Pure, and takes the interface list as an argument, so the ordering and filtering rules
    // below are testable - .NET gives no supported way to fabricate a NetworkInterface.
    public static LocalAddressesResponse Describe(DiscoveryEndpoint endpoint, IReadOnlyList<ScanInterface> interfaces)
    {
        var scheme = endpoint.UseHttps ? "https" : "http";

        var addresses = interfaces
            // 169.254.x.x means the interface asked for a DHCP lease and never got one. It's
            // never an address anyone can reach this machine on, so listing it would only be
            // one more number to work out isn't the right one.
            .Where(candidate => !IsLinkLocal(candidate.Address))
            .GroupBy(candidate => candidate.Address)
            .Select(group => group.First())
            .OrderBy(Rank)
            .ThenBy(candidate => candidate.Address, StringComparer.Ordinal)
            .Select(candidate => new LocalAddress(
                candidate.Address,
                candidate.Name,
                candidate.LooksVirtual,
                $"{scheme}://{candidate.Address}:{endpoint.Port}"))
            .ToList();

        return new LocalAddressesResponse(HostName(), scheme, endpoint.Port, addresses);
    }

    // The one a home user wants is a plain LAN address on a real adapter, so that sorts first
    // and the dashboard can present it as "this machine's address" without further guessing.
    // Docker bridges and the like still get listed, just last - on a box where the only address
    // is a virtual one, a labelled maybe beats an empty panel.
    private static int Rank(ScanInterface candidate) =>
        (candidate.LooksVirtual ? 2 : 0) + (IsPrivate(candidate.Address) ? 0 : 1);

    private static bool IsLinkLocal(string address) =>
        IPAddress.TryParse(address, out var parsed)
        && parsed.AddressFamily == AddressFamily.InterNetwork
        && parsed.GetAddressBytes() is [169, 254, ..];

    // RFC 1918 - the three ranges a home router hands out from.
    private static bool IsPrivate(string address) =>
        IPAddress.TryParse(address, out var parsed)
        && parsed.AddressFamily == AddressFamily.InterNetwork
        && parsed.GetAddressBytes() switch
        {
            [10, ..] => true,
            [172, var second, ..] => second is >= 16 and <= 31,
            [192, 168, ..] => true,
            _ => false
        };

    // Short name only: on a domain-joined or search-suffixed machine Dns.GetHostName can come
    // back fully qualified, and it's the short name that "<host>.local" is built from - the same
    // shape AutoCertificateProvisioner puts in the certificate's subject alternative names, so
    // the name shown here is one the HTTPS certificate actually covers.
    private static string HostName()
    {
        try
        {
            return Dns.GetHostName().Split('.')[0];
        }
        catch (SocketException)
        {
            return Environment.MachineName;
        }
    }
}
