using System.Net;
using Microsoft.AspNetCore.Http;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The per-IP login lockout keys off ClientIpResolver. Forwarded-for headers may only stand in for
// the caller when they came from the local proxy - otherwise each request could claim a new
// address and never be locked out.
public sealed class ClientIpResolverTests
{
    private static readonly InternetExposedSettings Exposed = new(Enabled: true, Port: 3444);

    private static HttpContext Request(string peer, int localPort, string? cfConnectingIp = null, string? forwardedFor = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Connection.LocalPort = localPort;
        if (cfConnectingIp is not null) context.Request.Headers["CF-Connecting-IP"] = cfConnectingIp;
        if (forwardedFor is not null) context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        return context;
    }

    [Fact]
    public void The_local_proxy_on_the_exposed_port_is_believed()
    {
        Assert.Equal("203.0.113.9", ClientIpResolver.Resolve(Request("127.0.0.1", 3444, cfConnectingIp: "203.0.113.9"), Exposed));
        Assert.Equal("198.51.100.4", ClientIpResolver.Resolve(Request("::1", 3444, forwardedFor: "198.51.100.4, 10.0.0.1"), Exposed));
    }

    [Fact]
    public void A_direct_connection_to_the_exposed_port_cannot_choose_its_own_address()
    {
        Assert.Equal("192.168.50.50", ClientIpResolver.Resolve(Request("192.168.50.50", 3444, cfConnectingIp: "1.2.3.4"), Exposed));
        Assert.Equal("192.168.50.50", ClientIpResolver.Resolve(Request("192.168.50.50", 3444, forwardedFor: "1.2.3.4"), Exposed));
    }

    [Fact]
    public void The_lan_port_never_believes_the_headers()
    {
        Assert.Equal("127.0.0.1", ClientIpResolver.Resolve(Request("127.0.0.1", 3320, cfConnectingIp: "1.2.3.4"), Exposed));
    }
}
