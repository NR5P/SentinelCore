extern alias WebHost;

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// MOVIE DOWNLOADS -> Mullvad: the owner hears once a week before the account runs out and once
// when it has, adding time sets both up again, and Mullvad is not asked more than it needs to be.
public sealed class MullvadAccountWatchTests : IDisposable
{
    private const string Account = "1234567812345678";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mullvad-account-{Guid.NewGuid():N}");
    private readonly ManualTime _clock = new(new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero));
    private readonly FakeMullvad _mullvad = new();
    private readonly FakeNotifier _notifier = new() { Accept = true };

    public void Dispose()
    {
        try { TempDirectories.Delete(_directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MediaDownloadsSettingsDto Options(string provider = MediaVpnProviders.Mullvad, bool useVpn = true, bool enabled = true,
        string account = "1234 5678 1234 5678") => new()
    {
        Enabled = enabled, UseVpn = useVpn, VpnProvider = provider, MullvadAccountNumber = account, MullvadLocation = "se"
    };

    private MullvadAccountWatch Watch() => new(
        new MediaVpnConfig(new SingleClientFactory(_mullvad), _directory, NullLogger<MediaVpnConfig>.Instance),
        _notifier, _directory, NullLogger<MullvadAccountWatch>.Instance, _clock);

    [Fact]
    public async Task WarnsOnceAWeekBefore_OnceWhenItRunsOut_AndAgainAfterTimeIsAdded()
    {
        _mullvad.PaidUntil = _clock.Now.AddDays(26);
        var watch = Watch();

        await watch.CheckAsync(Options(), Ct);
        Assert.Empty(_notifier.Sent);
        Assert.Equal(1, _mullvad.Lookups);

        // Twelve hours on: asked again, still nothing to say.
        _clock.Now = _clock.Now.AddHours(1);
        await watch.CheckAsync(Options(), Ct);
        Assert.Equal(1, _mullvad.Lookups);
        _clock.Now = _clock.Now.AddHours(12);
        await watch.CheckAsync(Options(), Ct);
        Assert.Equal(2, _mullvad.Lookups);
        Assert.Empty(_notifier.Sent);

        // Six days left: one warning, however many passes follow.
        _clock.Now = _mullvad.PaidUntil.AddDays(-6).AddHours(-3);
        await watch.CheckAsync(Options(), Ct);
        await watch.CheckAsync(Options(), Ct);
        _clock.Now = _clock.Now.AddDays(1);
        await watch.CheckAsync(Options(), Ct);
        var soon = Assert.Single(_notifier.Sent);
        Assert.Equal("Mullvad VPN runs out in 6 days", soon.Subject);
        Assert.Contains("mullvad.net", soon.Text);

        // Run out: one more.
        _clock.Now = _mullvad.PaidUntil.AddMinutes(5);
        await watch.CheckAsync(Options(), Ct);
        await watch.CheckAsync(Options(), Ct);
        Assert.Equal(2, _notifier.Sent.Count);
        Assert.Equal("Mullvad VPN has run out", _notifier.Sent[1].Subject);
        Assert.Contains("ran out of time on", _notifier.Sent[1].Text);

        // A month added: quiet until that one is nearly up, then warned afresh - even by a new
        // watch, as after a restart.
        _mullvad.PaidUntil = _clock.Now.AddDays(30);
        _clock.Now = _clock.Now.AddHours(2);
        await watch.CheckAsync(Options(), Ct);
        Assert.Equal(2, _notifier.Sent.Count);
        _clock.Now = _mullvad.PaidUntil.AddDays(-1).AddHours(-1);
        await Watch().CheckAsync(Options(), Ct);
        Assert.Equal(3, _notifier.Sent.Count);
        Assert.Equal("Mullvad VPN runs out tomorrow", _notifier.Sent[2].Subject);
    }

    [Fact]
    public async Task TimeAddedBetweenChecks_IsNoticedBeforeWarning()
    {
        _mullvad.PaidUntil = _clock.Now.AddDays(7).AddHours(3);
        var watch = Watch();
        await watch.CheckAsync(Options(), Ct);

        // Six hours on, the saved answer says under a week is left - but a year was bought in the
        // meantime, and Mullvad is asked again before anyone is told.
        _mullvad.PaidUntil = _clock.Now.AddDays(365);
        _clock.Now = _clock.Now.AddHours(6);
        await watch.CheckAsync(Options(), Ct);
        Assert.Equal(2, _mullvad.Lookups);
        Assert.Empty(_notifier.Sent);
    }

    [Fact]
    public async Task NoChannelToSayItOn_TriesAgainNextPass()
    {
        _mullvad.PaidUntil = _clock.Now.AddDays(3);
        _notifier.Accept = false;
        var watch = Watch();
        await watch.CheckAsync(Options(), Ct);
        _notifier.Accept = true;
        await watch.CheckAsync(Options(), Ct);
        Assert.Equal(2, _notifier.Sent.Count);
        await watch.CheckAsync(Options(), Ct);
        Assert.Equal(2, _notifier.Sent.Count);
    }

    [Fact]
    public async Task OnlyForMullvadWithTheVpnOn_AndOnlyForTheAccountInSettings()
    {
        _mullvad.PaidUntil = _clock.Now.AddDays(2);
        var watch = Watch();
        await watch.CheckAsync(Options(MediaVpnProviders.WireGuard), Ct);
        await watch.CheckAsync(Options(useVpn: false), Ct);
        await watch.CheckAsync(Options(enabled: false), Ct);
        Assert.Equal(0, _mullvad.Lookups);
        Assert.Null(watch.Dto(Options()));

        await watch.CheckAsync(Options(), Ct);
        Assert.NotNull(watch.Current(Options()));
        Assert.Null(watch.Current(Options(account: "9999888877776666")));
        Assert.Null(watch.Current(Options(MediaVpnProviders.Installed)));
    }

    [Fact]
    public async Task MullvadUnreachable_KeepsTheLastAnswer()
    {
        _mullvad.PaidUntil = _clock.Now.AddDays(20);
        var watch = Watch();
        var first = await watch.RefreshAsync(Options(), null, Ct);
        _mullvad.Down = true;
        _clock.Now = _clock.Now.AddDays(1);
        var second = await watch.RefreshAsync(Options(), null, Ct);
        Assert.Equal(first, second);
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeNotifier : IOwnerNotifier
    {
        public bool Accept { get; set; }
        public List<(string Subject, string Text)> Sent { get; } = [];

        public Task<bool> SendAsync(string subject, string text, CancellationToken cancellationToken)
        {
            Sent.Add((subject, text));
            return Task.FromResult(Accept);
        }
    }

    private sealed class FakeMullvad : HttpMessageHandler
    {
        public DateTimeOffset PaidUntil;
        public int Lookups;
        public bool Down;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down) throw new HttpRequestException("Name or service not known");
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/auth/v1/token")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                return body.Contains(Account) ? Json("""{ "access_token": "tok" }""") : Json("""{ "code": "INVALID_ACCOUNT" }""", HttpStatusCode.BadRequest);
            }
            if (path == "/accounts/v1/accounts/me" && request.Headers.Authorization?.Parameter == "tok")
            {
                Lookups++;
                return Json($$"""{ "id": "acc", "expiry": "{{PaidUntil:yyyy-MM-ddTHH:mm:ssK}}", "max_devices": 5, "can_add_devices": true }""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
