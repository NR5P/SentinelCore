namespace SentinelCore.Web;

// Commands on their way to speakers. A speaker long-polls for anything newer than the last
// sequence number it saw; a command is held for two minutes at most, because "pause" arriving
// much later than it was pressed is worse than not arriving. In memory on purpose: a restart
// loses at most a few seconds of button presses, and the speakers simply poll again.
public sealed class AudioSpeakerHub(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _lock = new();
    private readonly Dictionary<string, List<AudioSpeakerCommand>> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _waiters = new(StringComparer.Ordinal);
    // One counter across every speaker: a speaker that is renamed or re-registered can never see
    // a sequence number go backwards.
    private long _seq;

    public long LatestSeq
    {
        get { lock (_lock) return _seq; }
    }

    public long Send(string deviceId, AudioSpeakerCommand command)
    {
        TaskCompletionSource? wake;
        lock (_lock)
        {
            command.Seq = ++_seq;
            command.At = _clock.GetUtcNow();
            if (!_pending.TryGetValue(deviceId, out var list)) _pending[deviceId] = list = [];
            list.Add(command);
            Prune(list);
            _waiters.Remove(deviceId, out wake);
        }
        wake?.TrySetResult();
        return command.Seq;
    }

    // Everything for this speaker after `after`, waiting up to `wait` for something to arrive.
    // `after` below zero means "only what is sent from now on" - a speaker that just started does
    // not replay the last two minutes.
    public async Task<(IReadOnlyList<AudioSpeakerCommand> Commands, long LatestSeq)> WaitAsync(
        string deviceId, long after, TimeSpan wait, CancellationToken cancellationToken)
    {
        var deadline = _clock.GetUtcNow() + wait;
        if (after < 0) after = LatestSeq;
        while (true)
        {
            Task signal;
            lock (_lock)
            {
                if (_pending.TryGetValue(deviceId, out var list))
                {
                    Prune(list);
                    var due = list.Where(c => c.Seq > after).ToList();
                    if (due.Count > 0) return (due, _seq);
                }
                if (!_waiters.TryGetValue(deviceId, out var waiter))
                    _waiters[deviceId] = waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                signal = waiter.Task;
            }

            var remaining = deadline - _clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) return ([], LatestSeq);
            try
            {
                await signal.WaitAsync(remaining, _clock, cancellationToken);
            }
            catch (TimeoutException)
            {
                return ([], LatestSeq);
            }
        }
    }

    private void Prune(List<AudioSpeakerCommand> list)
    {
        var cutoff = _clock.GetUtcNow() - AudioSpeakerCommand.Expires;
        list.RemoveAll(c => c.At < cutoff);
    }
}
