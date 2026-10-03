using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SentinelCore.Web;

// Scrobbling to ListenBrainz (listenbrainz.org - free, open, run by MetaBrainz), for the people
// who pasted their user token into the app. Every counted play goes into audio.db first and is
// sent from there, so a ListenBrainz outage or a server without internet loses nothing for a day.
public sealed class AudioListenBrainz(
    SqliteAudioStore store,
    IHttpClientFactory httpClientFactory,
    TimeProvider clock,
    ILogger<AudioListenBrainz> logger) : BackgroundService, IListenBrainzSubmitter
{
    public const string HttpClientName = "ListenBrainz";
    public const string ApiBase = "https://api.listenbrainz.org/1/";
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(1);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private HashSet<string>? _connected;

    // Null token problems come back as the message to show; a working token returns its user name.
    public async Task<(string? Username, string? Error)> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + "validate-token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            var body = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
            if (body?["valid"]?.GetValue<bool>() == true && body["user_name"]?.GetValue<string>() is { Length: > 0 } name) return (name, null);
            return (null, "ListenBrainz didn't accept that token. Copy it again from listenbrainz.org/settings (\"User token\").");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, $"Couldn't reach ListenBrainz to check the token: {ex.Message}");
        }
    }

    public async Task ConnectedChangedAsync(CancellationToken cancellationToken) =>
        _connected = (await store.AllListenBrainzAsync(cancellationToken)).Select(a => a.UserKey).ToHashSet(StringComparer.Ordinal);

    public void Listened(string userKey, AudioListen listen)
    {
        if (!IsConnected(userKey)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await store.EnqueueListenAsync(userKey, listen, clock.GetUtcNow(), CancellationToken.None);
                _wake.Release();
            }
            catch (Exception ex) { logger.LogWarning(ex, "Couldn't queue a ListenBrainz listen"); }
        });
    }

    // "Playing now" is a hint, not a record: sent once, never queued or retried.
    public void PlayingNow(string userKey, AudioListen listen)
    {
        if (!IsConnected(userKey)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var account = await store.GetListenBrainzAsync(userKey, CancellationToken.None);
                if (account is null) return;
                await SubmitAsync(account.Token, "playing_now", [listen], CancellationToken.None);
            }
            catch (Exception ex) { logger.LogDebug(ex, "ListenBrainz playing-now failed"); }
        });
    }

    private bool IsConnected(string userKey)
    {
        // Until the first lookup finishes, assume connected: the queue drops listens for people
        // without an account anyway.
        var connected = _connected;
        return connected is null || connected.Contains(userKey);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await ConnectedChangedAsync(stoppingToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Reading ListenBrainz accounts failed"); }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SendDueAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Sending listens to ListenBrainz failed"); }
            try { await _wake.WaitAsync(Tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task SendDueAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var due = await store.DueListensAsync(now, 200, cancellationToken);
        if (due.Count == 0) return;
        var accounts = (await store.AllListenBrainzAsync(cancellationToken)).ToDictionary(a => a.UserKey, StringComparer.Ordinal);

        foreach (var group in due.GroupBy(l => l.UserKey))
        {
            var batch = group.ToList();
            if (!accounts.TryGetValue(group.Key, out var account))
            {
                foreach (var listen in batch) await store.CompleteListenAsync(listen.Id, cancellationToken);
                continue;
            }

            var outcome = await SubmitAsync(account.Token, batch.Count == 1 ? "single" : "import", batch.Select(l => l.Listen).ToList(), cancellationToken);
            foreach (var listen in batch)
            {
                if (outcome == SubmitOutcome.Sent || outcome == SubmitOutcome.Rejected || now - listen.CreatedAt > GiveUpAfter)
                {
                    await store.CompleteListenAsync(listen.Id, cancellationToken);
                    continue;
                }
                // 1, 2, 4 ... minutes, up to an hour.
                var wait = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, listen.Attempts)));
                await store.RetryListenAsync(listen.Id, now + wait, cancellationToken);
            }
        }
    }

    private enum SubmitOutcome { Sent, Retry, Rejected }

    private async Task<SubmitOutcome> SubmitAsync(string token, string listenType, IReadOnlyList<AudioListen> listens, CancellationToken cancellationToken)
    {
        var payload = new JsonArray();
        foreach (var listen in listens)
        {
            var info = new JsonObject();
            if (listen.DurationSeconds is > 0) info["duration_ms"] = (long)(listen.DurationSeconds.Value * 1000);
            if (listen.TrackNumber is { } number) info["tracknumber"] = number;
            info["media_player"] = "SentinelCore";
            info["submission_client"] = "SentinelCore";
            var metadata = new JsonObject
            {
                ["artist_name"] = listen.Artist,
                ["track_name"] = listen.Title,
                ["additional_info"] = info
            };
            if (!string.IsNullOrWhiteSpace(listen.Album)) metadata["release_name"] = listen.Album;
            var item = new JsonObject { ["track_metadata"] = metadata };
            if (listenType != "playing_now") item["listened_at"] = listen.PlayedAt.ToUnixTimeSeconds();
            payload.Add(item);
        }
        var body = new JsonObject { ["listen_type"] = listenType, ["payload"] = payload };

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + "submit-listens")
        {
            Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) return SubmitOutcome.Sent;
            // A bad token or a listen ListenBrainz refuses will be refused again; anything else
            // (rate limits, 5xx) is worth another try.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
            {
                logger.LogInformation("ListenBrainz refused {Count} listens: {Status}", listens.Count, (int)response.StatusCode);
                return SubmitOutcome.Rejected;
            }
            return SubmitOutcome.Retry;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return SubmitOutcome.Retry;
        }
    }
}
