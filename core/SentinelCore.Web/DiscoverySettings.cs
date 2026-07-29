namespace SentinelCore.Web;

// Local-network discovery (mDNS/DNS-SD), on by default - lets the mobile apps find this server
// by searching the LAN instead of the user typing an IP address. See MdnsAdvertiserService for
// the actual advertisement. Purely a convenience: manual address entry in both apps keeps
// working exactly as before whether or not this is enabled or reachable.
public sealed record DiscoverySettings(bool Enabled, string? InstanceName, string? InstanceId)
{
    public static DiscoverySettings FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Discovery");
        return new DiscoverySettings(
            section["Enabled"] is { } raw ? bool.Parse(raw) : true,
            section["InstanceName"],
            section["InstanceId"]);
    }
}

// What MdnsAdvertiserService should actually advertise - resolved once at startup from the same
// httpsSettings/httpsEnabled/httpPort values Kestrel itself was configured with, so discovery
// always points at a port the server is actually listening on.
public sealed record DiscoveryEndpoint(bool UseHttps, int Port);
