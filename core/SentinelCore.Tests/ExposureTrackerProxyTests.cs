using System.Net;
using Microsoft.AspNetCore.Http;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// A Cloudflare Tunnel or other reverse proxy on the same machine connects from loopback, so the
// peer address alone would make the whole internet look like the LAN. These pin how the proxy's
// headers change that: never trusted to make a request look local, only to make it look external.
public sealed class ExposureTrackerProxyTests
{
    private static HttpContext Request(string? peer, params (string Header, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = peer is null ? null : IPAddress.Parse(peer);
        foreach (var (header, value) in headers) context.Request.Headers.Append(header, value);
        return context;
    }

    [Fact]
    public void A_direct_lan_connection_counts_as_local()
    {
        Assert.True(ExposureTracker.IsDirectFromLocalNetwork(Request("192.168.1.20")));
        Assert.True(ExposureTracker.IsDirectFromLocalNetwork(Request("127.0.0.1")));
    }

    [Theory]
    [InlineData("CF-Connecting-IP", "203.0.113.9")]
    [InlineData("CF-Ray", "8c1f2e3d4a5b6c7d-DFW")]
    [InlineData("X-Forwarded-For", "203.0.113.9")]
    [InlineData("X-Real-IP", "203.0.113.9")]
    [InlineData("Forwarded", "for=203.0.113.9;proto=https")]
    [InlineData("True-Client-IP", "203.0.113.9")]
    // A forged "I'm on the LAN" still disqualifies: no proxy header ever makes a request local.
    [InlineData("X-Forwarded-For", "192.168.1.5")]
    public void Any_proxy_header_on_a_loopback_connection_is_not_local(string header, string value) =>
        Assert.False(ExposureTracker.IsDirectFromLocalNetwork(Request("127.0.0.1", (header, value))));

    [Fact]
    public void The_exposure_warning_names_the_client_the_proxy_reports()
    {
        Assert.Equal(IPAddress.Parse("203.0.113.9"),
            ExposureTracker.ForwardedExternalClient(Request("127.0.0.1", ("CF-Connecting-IP", "203.0.113.9")).Request));
        Assert.Equal(IPAddress.Parse("198.51.100.4"),
            ExposureTracker.ForwardedExternalClient(Request("127.0.0.1", ("X-Forwarded-For", "10.0.0.7, 198.51.100.4")).Request));
        Assert.Equal(IPAddress.Parse("2001:db8::1"),
            ExposureTracker.ForwardedExternalClient(Request("127.0.0.1", ("Forwarded", "for=\"[2001:db8::1]\";proto=https")).Request));
    }

    [Fact]
    public void A_lan_proxy_relaying_a_lan_device_is_not_an_exposure()
    {
        Assert.Null(ExposureTracker.ForwardedExternalClient(Request("127.0.0.1", ("X-Forwarded-For", "192.168.1.20")).Request));
        Assert.Null(ExposureTracker.ForwardedExternalClient(Request("192.168.1.20").Request));
    }

    [Fact]
    public void A_proxy_that_names_no_client_still_counts_as_external()
    {
        Assert.Equal(IPAddress.Loopback,
            ExposureTracker.ForwardedExternalClient(Request("127.0.0.1", ("CF-Ray", "8c1f2e3d4a5b6c7d-DFW")).Request));
    }
}
