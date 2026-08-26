using SentinelCore.Client.Networking;
using Xunit;

namespace SentinelCore.Client.Tests;

// These decide whether the app talks to the server at all: an address that normalizes wrong shows
// "Enter a valid server address" on a perfectly good one, and a fallback that guesses wrong sends
// a password to the wrong port. Worth pinning down exactly.
public class ServerAddressTests
{
    [Theory]
    [InlineData("http://192.168.1.50:3320", "http://192.168.1.50:3320")]
    [InlineData("https://sentinel.example.com", "https://sentinel.example.com")]
    [InlineData("  http://192.168.1.50:3320  ", "http://192.168.1.50:3320")]
    [InlineData("http://192.168.1.50:3320/", "http://192.168.1.50:3320")]
    public void Normalize_keeps_usable_addresses(string input, string expected) =>
        Assert.Equal(expected, ServerAddress.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("192.168.1.50:3320")]     // no scheme - AddSchemeIfMissing's job, not this one
    [InlineData("ftp://192.168.1.50")]    // a scheme this app will never speak
    [InlineData("http://")]               // no host
    [InlineData("not a url at all")]
    public void Normalize_rejects_unusable_addresses(string input) =>
        Assert.Null(ServerAddress.Normalize(input));

    [Theory]
    [InlineData("192.168.1.50:3320", "http", "http://192.168.1.50:3320")]
    [InlineData("sentinel.example.com", "https", "https://sentinel.example.com")]
    [InlineData("http://192.168.1.50", "https", "http://192.168.1.50")] // an explicit scheme wins
    [InlineData("192.168.1.50/", "http", "http://192.168.1.50")]
    public void AddSchemeIfMissing_only_fills_in_what_is_missing(string input, string scheme, string expected) =>
        Assert.Equal(expected, ServerAddress.AddSchemeIfMissing(input, scheme));

    [Fact]
    public void HttpFallback_points_at_the_shipped_plain_http_port()
    {
        // A custom HTTP port can't be derived from an HTTPS address, so the shipped default is the
        // only sensible guess - and the caller probes it before switching.
        Assert.Equal("http://192.168.1.50:3320", ServerAddress.HttpFallback("https://192.168.1.50"));
        Assert.Equal("http://192.168.1.50:3320", ServerAddress.HttpFallback("https://192.168.1.50:3443"));
    }

    [Fact]
    public void HttpFallback_declines_when_there_is_nothing_to_fall_back_from()
    {
        // Already plain HTTP: there is no downgrade to make.
        Assert.Null(ServerAddress.HttpFallback("http://192.168.1.50:3320"));
        Assert.Null(ServerAddress.HttpFallback(""));
        Assert.Null(ServerAddress.HttpFallback("nonsense"));
    }

    [Fact]
    public void HttpsEquivalent_guesses_only_when_no_port_is_pinned()
    {
        Assert.Equal("https://192.168.1.50", ServerAddress.HttpsEquivalent("http://192.168.1.50"));

        // On http://host:3320 the https counterpart is a *different* listener on a different port
        // (3443), not the same one with TLS - so this gets no guess rather than a wrong one.
        Assert.Null(ServerAddress.HttpsEquivalent("http://192.168.1.50:3320"));
        Assert.Null(ServerAddress.HttpsEquivalent("https://192.168.1.50"));
    }

    [Fact]
    public void WebSocketUri_swaps_the_scheme_and_keeps_host_and_port()
    {
        var ws = ServerAddress.WebSocketUri("http://192.168.1.50:3320", "/api/cameras/abc/mse/sub");
        Assert.Equal("ws://192.168.1.50:3320/api/cameras/abc/mse/sub", ws?.ToString());

        var wss = ServerAddress.WebSocketUri("https://sentinel.example.com", "/api/cameras/abc/mse");
        Assert.Equal("wss://sentinel.example.com/api/cameras/abc/mse", wss?.ToString());
    }

    [Fact]
    public void WebSocketUri_declines_an_address_it_cannot_parse() =>
        Assert.Null(ServerAddress.WebSocketUri("nonsense", "/api/cameras/abc/mse"));
}
