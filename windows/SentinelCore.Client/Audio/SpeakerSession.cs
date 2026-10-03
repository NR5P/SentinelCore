using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;

namespace SentinelCore.Client.Audio;

/// <summary>
/// "Let others play here" (MUSIC_AND_PODCASTS.md §8): registers this PC as a speaker, keeps the
/// registration alive with a heartbeat every 60 s, and holds the command long poll open, handing
/// each command to the player strictly in seq order.
/// </summary>
/// <remarks>
/// <para>
/// One command at a time, awaited: the handler for an <c>announce</c> only returns once the voice
/// has finished, so an alarm's spoken briefing is heard in full before its <c>play</c> starts the
/// episodes - the ordering §8 requires.
/// </para>
/// <para>
/// The first poll asks with after = -1 ("only new ones"), so commands sent while this PC was off
/// are never replayed. After that the cursor is the newest seq seen, or latestSeq when the server
/// sends it. Failures back off rather than spin: a few seconds for a blip, a minute when the
/// feature is off (404).
/// </para>
/// </remarks>
public sealed class SpeakerSession : IAsyncDisposable
{
    private readonly SentinelClient _client;
    private readonly string _deviceId;
    private readonly Func<SpeakerRegistration> _registration;
    private readonly Func<DeliveredSpeakerCommand, Task> _handle;
    private readonly ISentinelLog _log;
    private readonly TimeSpan _heartbeat;
    private readonly int _waitSeconds;
    private readonly TimeSpan _retryDelay;
    private CancellationTokenSource? _stop;
    private Task? _poll;
    private Task? _beat;

    public SpeakerSession(
        SentinelClient client,
        string deviceId,
        Func<SpeakerRegistration> registration,
        Func<DeliveredSpeakerCommand, Task> handle,
        ISentinelLog log,
        TimeSpan? heartbeat = null,
        int waitSeconds = 25,
        TimeSpan? retryDelay = null)
    {
        _client = client;
        _deviceId = deviceId;
        _registration = registration;
        _handle = handle;
        _log = log;
        _heartbeat = heartbeat ?? TimeSpan.FromSeconds(60);
        _waitSeconds = waitSeconds;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>The poll cursor: -1 until the first answer.</summary>
    public long After { get; private set; } = -1;

    public bool IsRunning => _stop is { IsCancellationRequested: false };

    /// <summary>Raised (off the UI thread) when a heartbeat or poll fails or recovers: null means healthy.</summary>
    public event Action<string?>? HealthChanged;

    public void Start()
    {
        if (IsRunning) return;
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        After = -1;
        _beat = Task.Run(() => HeartbeatLoopAsync(token));
        _poll = Task.Run(() => PollLoopAsync(token));
    }

    /// <summary>Re-sends the registration now - after a name, volume or announcements change.</summary>
    public Task HeartbeatNowAsync() => _client.RegisterSpeakerAsync(_deviceId, _registration());

    private async Task HeartbeatLoopAsync(CancellationToken token)
    {
        var failing = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _client.RegisterSpeakerAsync(_deviceId, _registration(), token).ConfigureAwait(false);
                failing = false;
                HealthChanged?.Invoke(null);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                // Logged when it starts failing, not every minute it goes on failing.
                if (!failing) _log.Log($"Speaker heartbeat failed: {error.Message}", DiagnosticLevel.Notice);
                failing = true;
                HealthChanged?.Invoke(error is SentinelServerException { Status: 404 }
                    ? null // Music and Podcasts are off: nothing for the user to fix here.
                    : $"\"Let others play here\" can't reach the server: {error.Message}");
            }

            try
            {
                await Task.Delay(_heartbeat, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// How long to wait after the <paramref name="failures"/>-th failure in a row: the retry delay
    /// doubling up to a minute, so a server that is down (or not signed in to yet) is asked about
    /// once a minute rather than every few seconds. A 404 (Music and Podcasts both off) waits the
    /// full minute straight away.
    /// </summary>
    internal static TimeSpan Backoff(TimeSpan retryDelay, int failures, bool off)
    {
        var cap = TimeSpan.FromSeconds(Math.Max(60, retryDelay.TotalSeconds));
        if (off) return cap;
        var seconds = retryDelay.TotalSeconds * Math.Pow(2, Math.Clamp(failures - 1, 0, 10));
        return TimeSpan.FromSeconds(Math.Min(seconds, cap.TotalSeconds));
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        var failures = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var reply = await _client.SpeakerCommandsAsync(_deviceId, After, _waitSeconds, token).ConfigureAwait(false);
                if (failures > 0) _log.Log("Speaker command poll recovered");
                failures = 0;
                await ApplyAsync(reply, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                failures++;
                var off = error is SentinelServerException { Status: 404 };
                // Logged on the first failure of a run and then every tenth, not on every retry.
                if (failures == 1 || failures % 10 == 0)
                {
                    _log.Log($"Speaker command poll failed ({failures}x): {error.Message}", DiagnosticLevel.Notice);
                }
                try
                {
                    await Task.Delay(Backoff(_retryDelay, failures, off), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Runs a reply's commands in seq order, each finished before the next, and moves the cursor
    /// past them. Internal so the ordering and cursor rules can be tested without a long poll.
    /// </summary>
    internal async Task ApplyAsync(SpeakerCommandsReply reply, CancellationToken token = default)
    {
        foreach (var command in reply.Commands.OrderBy(command => command.Seq))
        {
            if (token.IsCancellationRequested) return;
            // A command already applied (a reply repeated after a dropped connection) is skipped.
            if (After >= 0 && command.Seq <= After) continue;
            After = command.Seq;
            try
            {
                await _handle(command).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // One bad command must never stop the speaker hearing the next.
                _log.Log($"Speaker command {command.Action} failed: {error.Message}", DiagnosticLevel.Notice);
            }
        }
        if (reply.LatestSeq is { } latest && latest > After) After = latest;
    }

    /// <summary>Stops polling and, when <paramref name="unregister"/>, tells the server this PC is no longer a speaker.</summary>
    public async Task StopAsync(bool unregister = true)
    {
        if (_stop is null) return;
        _stop.Cancel();
        try
        {
            if (_poll is not null) await _poll.ConfigureAwait(false);
            if (_beat is not null) await _beat.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Loops end on cancellation; nothing else to report.
        }
        _stop.Dispose();
        _stop = null;
        if (!unregister) return;
        try
        {
            await _client.UnregisterSpeakerAsync(_deviceId).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _log.Log($"Could not unregister the speaker: {error.Message}", DiagnosticLevel.Notice);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(unregister: false).ConfigureAwait(false);
}
