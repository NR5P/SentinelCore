using System.Net;

namespace SentinelCore.Web;

// Notices when a request arrives from outside the local network, which is direct evidence this
// dashboard is reachable further than intended - both services bind to 0.0.0.0 by design (that's
// what makes them reachable from a phone on the same Wi-Fi), so the app can't tell from its own
// configuration whether it's LAN-only or facing the internet. An off-LAN request is the first
// hard proof either way.
//
// Deliberately observational: it never blocks, never rate-limits, never changes a response. A
// false positive would be someone reaching the dashboard over a VPN that hands out public-range
// addresses, and misreporting that is a far better failure than refusing their request.
public sealed class ExposureTracker
{
    private readonly object _gate = new();

    public bool HasSeenExternalRequest { get; private set; }
    public string? LastExternalAddress { get; private set; }
    public DateTimeOffset? LastExternalAt { get; private set; }

    public void Record(IPAddress address)
    {
        lock (_gate)
        {
            HasSeenExternalRequest = true;
            LastExternalAddress = address.ToString();
            LastExternalAt = DateTimeOffset.Now;
        }
    }

    // Loopback, RFC1918, RFC6598 carrier-grade NAT, link-local, and their IPv6 equivalents.
    // Anything else is treated as off-LAN.
    public static bool IsLocalNetwork(IPAddress? address)
    {
        if (address is null) return true;

        if (IPAddress.IsLoopback(address)) return true;

        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            // fe80::/10 link-local, fc00::/7 unique-local - the IPv6 analogues of the ranges below.
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || IsIPv6UniqueLocal(address);
        }

        var octets = address.GetAddressBytes();
        if (octets.Length != 4) return true;

        return octets[0] switch
        {
            10 => true,                                          // 10.0.0.0/8
            127 => true,                                         // 127.0.0.0/8
            169 when octets[1] == 254 => true,                   // 169.254.0.0/16 link-local
            172 when octets[1] >= 16 && octets[1] <= 31 => true,  // 172.16.0.0/12
            192 when octets[1] == 168 => true,                    // 192.168.0.0/16
            100 when octets[1] >= 64 && octets[1] <= 127 => true, // 100.64.0.0/10 CGNAT
            _ => false
        };
    }

    private static bool IsIPv6UniqueLocal(IPAddress address) => (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
}
