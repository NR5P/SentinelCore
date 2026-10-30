extern alias WebHost;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// MOVIE DOWNLOADS -> Mullvad: when the saved server stops answering (Mullvad still lists it as
// active), the next try moves to another server at the same location instead of retrying the dead
// one forever - "VPN connecting" for good, 2026-10-30.
public sealed class MullvadRelaySwitchTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mullvad-relay-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { TempDirectories.Delete(_directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MediaVpnConfig Config() =>
        new(new SingleClientFactory(new FakeRelays()), _directory, NullLogger<MediaVpnConfig>.Instance);

    private void SaveState(string relay)
    {
        Directory.CreateDirectory(_directory);
        var state = new MediaVpnConfig.MullvadState
        {
            AccountHash = "h", DeviceId = "d", PrivateKey = "k", Ipv4 = "10.64.0.2/32",
            Location = "se-sto", RelayHostname = relay, RelayIp = "1.1.1.1", RelayPublicKey = "pk"
        };
        File.WriteAllText(Path.Combine(_directory, "media-downloads-mullvad.json"),
            JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public async Task MovesOffTheDeadServer_AndNeverBackUntilEveryOneHasFailed()
    {
        SaveState("se-sto-wg-208");
        var config = Config();

        var tried = new List<string> { "se-sto-wg-208" };
        for (var i = 0; i < 2; i++)
        {
            var next = await config.SwitchMullvadRelayAsync(Ct);
            Assert.NotNull(next);
            Assert.DoesNotContain(next, tried);
            Assert.StartsWith("se-sto-", next);
            tried.Add(next);
            Assert.Equal(next, config.ReadState()!.RelayHostname);
        }

        // All three of Mullvad's own servers have failed: the rented one that has not, then the
        // round starts again rather than giving up. Never another city.
        Assert.Equal("se-sto-wg-900", await config.SwitchMullvadRelayAsync(Ct));
        var again = await config.SwitchMullvadRelayAsync(Ct);
        Assert.NotNull(again);
        Assert.DoesNotContain(again, new[] { "se-sto-wg-900", "se-got-wg-001" });
    }

    [Fact]
    public async Task NothingSaved_NothingToSwitch()
    {
        Assert.Null(await Config().SwitchMullvadRelayAsync(Ct));
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeRelays : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != "/public/relays/wireguard/v2")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            const string body = """
            { "locations": { "se-sto": { "country": "Sweden", "city": "Stockholm" } },
              "wireguard": { "relays": [
                { "hostname": "se-sto-wg-001", "location": "se-sto", "active": true, "owned": true, "ipv4_addr_in": "185.195.233.76", "public_key": "a" },
                { "hostname": "se-sto-wg-206", "location": "se-sto", "active": true, "owned": true, "ipv4_addr_in": "170.62.100.66", "public_key": "b" },
                { "hostname": "se-sto-wg-208", "location": "se-sto", "active": true, "owned": true, "ipv4_addr_in": "170.62.100.170", "public_key": "c" },
                { "hostname": "se-sto-wg-900", "location": "se-sto", "active": true, "owned": false, "ipv4_addr_in": "9.9.9.9", "public_key": "d" },
                { "hostname": "se-got-wg-001", "location": "se-got", "active": true, "owned": true, "ipv4_addr_in": "8.8.8.8", "public_key": "e" }
              ] } }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
