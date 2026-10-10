using System.Net;
using Microsoft.Extensions.Configuration;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

public sealed class PublicNetworkGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.50.8")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2002:c0a8:0101::1")]
    [InlineData("2001:db8::1")]
    public void Local_and_private_addresses_are_not_public(string address) =>
        Assert.False(PublicNetworkGuard.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")]
    public void Public_addresses_are_public(string address) =>
        Assert.True(PublicNetworkGuard.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task A_url_naming_a_private_address_is_refused_unless_allowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var strict = new ConfigurationBuilder().Build();
        var relaxed = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [PublicNetworkGuard.AllowPrivateKey] = "true" }).Build();

        Assert.False(await PublicNetworkGuard.PointsAtPublicInternetAsync("http://127.0.0.1:11984/api/streams", strict, ct));
        Assert.False(await PublicNetworkGuard.PointsAtPublicInternetAsync("http://[::1]/x.mp3", strict, ct));
        Assert.False(await PublicNetworkGuard.PointsAtPublicInternetAsync("file:///etc/passwd", strict, ct));
        Assert.True(await PublicNetworkGuard.PointsAtPublicInternetAsync("http://127.0.0.1/x.mp3", relaxed, ct));
    }
}
