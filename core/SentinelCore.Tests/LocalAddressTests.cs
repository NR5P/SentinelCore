using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The panel under SERVER ADDRESS exists so nobody has to work out which of a machine's addresses
// is the one to type into a phone. That judgement is made here, in Describe's ordering, so these
// tests pin the order rather than just the contents.
public sealed class LocalAddressTests
{
    private static ScanInterface Nic(string address, string name = "eth0", bool virtualAdapter = false) =>
        new(name, name, address, 24, virtualAdapter);

    [Fact]
    public void Describe_PutsThePlainLanAddressFirst()
    {
        var described = LocalAddressEndpoints.Describe(
            new DiscoveryEndpoint(UseHttps: true, Port: 3443),
            [
                Nic("172.17.0.1", "docker0", virtualAdapter: true),
                Nic("203.0.113.7", "eth1"),
                Nic("192.168.50.5", "eth0")
            ]);

        Assert.Equal(["192.168.50.5", "203.0.113.7", "172.17.0.1"], described.Addresses.Select(a => a.Address));
        Assert.Equal("https://192.168.50.5:3443", described.Addresses[0].Url);
        Assert.True(described.Addresses[2].LooksVirtual);
    }

    [Fact]
    public void Describe_UsesTheSchemeAndPortTheServerActuallyBound()
    {
        var described = LocalAddressEndpoints.Describe(
            new DiscoveryEndpoint(UseHttps: false, Port: 3320),
            [Nic("10.0.0.4")]);

        Assert.Equal("http", described.Scheme);
        Assert.Equal(3320, described.Port);
        Assert.Equal("http://10.0.0.4:3320", described.Addresses[0].Url);
    }

    [Fact]
    public void Describe_DropsAddressesNothingCanBeReachedOn()
    {
        // 169.254.x.x is what an interface gets when its DHCP request went unanswered - listing
        // it would only be one more number to work out isn't the right one.
        var described = LocalAddressEndpoints.Describe(
            new DiscoveryEndpoint(UseHttps: true, Port: 3443),
            [Nic("169.254.99.1", "eth1"), Nic("192.168.50.5")]);

        Assert.Equal(["192.168.50.5"], described.Addresses.Select(a => a.Address));
    }

    [Fact]
    public void Describe_ListsOneEntryPerAddress()
    {
        // The same address on two interface entries (a NIC in two enumerations, an alias) would
        // otherwise be shown twice, which reads as two different machines.
        var described = LocalAddressEndpoints.Describe(
            new DiscoveryEndpoint(UseHttps: true, Port: 3443),
            [Nic("192.168.50.5", "eth0"), Nic("192.168.50.5", "eth0:1")]);

        Assert.Single(described.Addresses);
    }

    [Fact]
    public void Describe_WithNoUsableInterfaces_StillReportsTheHostName()
    {
        var described = LocalAddressEndpoints.Describe(new DiscoveryEndpoint(UseHttps: true, Port: 3443), []);

        Assert.Empty(described.Addresses);
        Assert.False(string.IsNullOrWhiteSpace(described.HostName));
    }
}
