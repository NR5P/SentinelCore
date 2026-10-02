using System.Globalization;
using System.Text.Json;

namespace SentinelCore.Web;

// What Mullvad last said about the account: paid until when, and when that was asked.
public sealed record MediaVpnAccount(DateTimeOffset PaidUntil, DateTimeOffset CheckedAt);

// Keeps an eye on how long the Mullvad account has left (VPN Provider = Mullvad only), for the
// VPN light, a plain "out of time" error instead of "the WireGuard server never answered", and a
// warning to the owner a week before and again when it runs out. Mullvad stops accepting the
// server's key the moment the time is used up; the kill switch means nothing then leaks over the
// home connection, but nothing downloads either.
public sealed class MullvadAccountWatch(
    MediaVpnConfig vpn,
    IOwnerNotifier notifier,
    string dataDirectory,
    ILogger<MullvadAccountWatch> logger,
    TimeProvider? clock = null)
{
    public const int WarnDays = 7;
    // How stale the paid-until date may get before it is asked for again. Adding time on
    // mullvad.net shows up within this, or straight away on the next search or download.
    public static readonly TimeSpan RecheckEvery = TimeSpan.FromHours(12);
    // While a queued download is held up by an account that has run out, the background pass
    // (every 3 s) re-asks no more often than this - each ask is two Mullvad API calls, and
    // Mullvad rate-limits sign-ins. Adding time shows up within this; a search, a download or
    // Test VPN asks straight away.
    public static readonly TimeSpan WorkerRecheckEvery = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private State? _state;

    private sealed class State
    {
        public string AccountHash { get; set; } = "";
        public DateTimeOffset PaidUntil { get; set; }
        public DateTimeOffset CheckedAt { get; set; }
        // The paid-until date each warning was sent for, so each goes once per date and adding
        // time (a new date) sets both up again.
        public DateTimeOffset? WarnedSoonFor { get; set; }
        public DateTimeOffset? WarnedExpiredFor { get; set; }
    }

    private string StatePath => Path.Combine(dataDirectory, "media-downloads-mullvad-account.json");

    public DateTimeOffset Now => _clock.GetUtcNow();

    private static string? AccountOf(MediaDownloadsSettingsDto options) =>
        options.UseVpn && options.VpnProvider == MediaVpnProviders.Mullvad ? MediaVpnConfig.NormalizeAccount(options.MullvadAccountNumber) : null;

    // The last answer for the account in Settings, without asking Mullvad. Null when the VPN is
    // not Mullvad, or this account has not been looked up yet.
    public MediaVpnAccount? Current(MediaDownloadsSettingsDto options)
    {
        if (AccountOf(options) is not { } account) return null;
        var state = Load();
        return state is not null && state.AccountHash == MediaVpnConfig.AccountHash(account) ? new(state.PaidUntil, state.CheckedAt) : null;
    }

    public bool IsExpired(MediaVpnAccount account) => account.PaidUntil <= Now;

    // Asks Mullvad unless the last answer is younger than maxAge (null: always ask). When Mullvad
    // cannot be reached the previous answer stands.
    public async Task<MediaVpnAccount?> RefreshAsync(MediaDownloadsSettingsDto options, TimeSpan? maxAge, CancellationToken cancellationToken)
    {
        if (AccountOf(options) is not { } account) return null;
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var hash = MediaVpnConfig.AccountHash(account);
            var state = Load();
            if (state?.AccountHash != hash) state = null;
            if (state is not null && maxAge is { } age && Now - state.CheckedAt < age) return new(state.PaidUntil, state.CheckedAt);
            try
            {
                var paidUntil = await vpn.MullvadPaidUntilAsync(account, cancellationToken);
                state = new State
                {
                    AccountHash = hash, PaidUntil = paidUntil, CheckedAt = Now,
                    WarnedSoonFor = state?.WarnedSoonFor, WarnedExpiredFor = state?.WarnedExpiredFor
                };
                Save(state);
            }
            catch (Exception ex) when (ex is HttpRequestException or MediaDownloadException or JsonException
                                       || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                logger.LogWarning("Movie downloads: could not ask Mullvad how long the account has left: {Message}", ex.Message);
            }
            return state is null ? null : new(state.PaidUntil, state.CheckedAt);
        }
        finally { _lock.Release(); }
    }

    // One pass of the background check: refreshes twice a day, and tells the owner once when a
    // week or less is left and once when it has run out.
    public async Task CheckAsync(MediaDownloadsSettingsDto options, CancellationToken cancellationToken)
    {
        if (!options.Enabled) return;
        var account = await RefreshAsync(options, RecheckEvery, cancellationToken);
        if (account is null || account.PaidUntil - Now > TimeSpan.FromDays(WarnDays)) return;
        // About to warn: make sure time has not just been added.
        account = await RefreshAsync(options, TimeSpan.FromHours(1), cancellationToken);
        if (account is null) return;

        var expired = IsExpired(account);
        if (!expired && account.PaidUntil - Now > TimeSpan.FromDays(WarnDays)) return;
        var state = Load()!;
        if (expired ? state.WarnedExpiredFor == account.PaidUntil : state.WarnedSoonFor == account.PaidUntil) return;

        var (subject, text) = expired
            ? ("Mullvad VPN has run out",
               $"Your Mullvad account ran out of time on {DateText(account.PaidUntil)}, so movie downloads can't connect to the VPN. " +
               "Nothing downloads over your home connection in the meantime. Add time at mullvad.net and downloads work again.")
            : ($"Mullvad VPN runs out {InDays(account.PaidUntil)}",
               $"Your Mullvad account is paid until {DateText(account.PaidUntil)}. After that, movie downloads can't connect to the VPN " +
               "(they never fall back to your home connection). Add time at mullvad.net to keep them working.");
        if (!await notifier.SendAsync(subject, text, cancellationToken))
        {
            logger.LogWarning("Movie downloads: {Subject}, but no email, Telegram or push is set up to say so", subject);
            return;
        }
        logger.LogInformation("Movie downloads: told the owner \"{Subject}\"", subject);
        if (expired) state.WarnedExpiredFor = account.PaidUntil;
        else state.WarnedSoonFor = account.PaidUntil;
        Save(state);
    }

    // The account part of GET /api/media/downloads/status, or null when there is nothing to show.
    public object? Dto(MediaDownloadsSettingsDto options)
    {
        if (Current(options) is not { } account) return null;
        var left = account.PaidUntil - Now;
        return new
        {
            provider = MediaVpnProviders.Mullvad,
            paidUntil = account.PaidUntil,
            daysLeft = Math.Max(0, (int)Math.Floor(left.TotalDays)),
            runningOut = left > TimeSpan.Zero && left <= TimeSpan.FromDays(WarnDays),
            expired = left <= TimeSpan.Zero,
            checkedAt = account.CheckedAt
        };
    }

    public static string OutOfTimeMessage(MediaVpnAccount account) =>
        $"Your Mullvad account ran out of time on {DateText(account.PaidUntil)}. Add time at mullvad.net, then try again.";

    public static string DateText(DateTimeOffset at) =>
        TimeZoneInfo.ConvertTime(at, TimeZoneInfo.Local).ToString("dddd, MMMM d", CultureInfo.InvariantCulture);

    private string InDays(DateTimeOffset paidUntil)
    {
        var days = (int)Math.Floor((paidUntil - Now).TotalDays);
        return days switch { <= 0 => "today", 1 => "tomorrow", _ => $"in {days} days" };
    }

    private State? Load()
    {
        if (_state is not null) return _state;
        try { _state = File.Exists(StatePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath), JsonOptions) : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { _state = null; }
        return _state;
    }

    private void Save(State state)
    {
        _state = state;
        try
        {
            Directory.CreateDirectory(dataDirectory);
            var temporary = StatePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporary, StatePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Movie downloads: could not save the Mullvad account check: {Message}", ex.Message);
        }
    }
}

public sealed class MullvadAccountWorker(MullvadAccountWatch watch, AppSettingsFile settings, ILogger<MullvadAccountWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await watch.CheckAsync(await MediaDownloadManager.ReadOptionsAsync(settings, stoppingToken), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Movie downloads: the Mullvad account check failed"); }
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
