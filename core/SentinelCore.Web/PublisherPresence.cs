namespace SentinelCore.Web;

// What PublisherWatchdog knows about each webcam device, and the rule for when its absence is
// worth telling the owner about. Kept apart from the watchdog so the rule can be tested without
// a clock, a sidecar or a notifier.
//
// The rule: a camera has to have been seen live at least once before its absence means anything -
// otherwise every webcam device that was saved and never started would raise an alert the moment
// the server came up. After that, it has to stay gone for the whole grace period, because a
// publishing browser reconnects on its own after a network blip and that takes seconds, not
// minutes. One alert per outage; the recovery is reported too, so the owner knows without
// having to look.
public sealed class PublisherPresence(TimeSpan gracePeriod)
{
    public enum Transition { None, Dropped, Recovered }

    public sealed record Status(bool Live, DateTimeOffset? LastLiveAt, DateTimeOffset? MissingSince, bool Alerted);

    private readonly Dictionary<Guid, Status> _cameras = new();
    private readonly object _lock = new();

    public Transition Observe(Guid cameraId, bool live, DateTimeOffset now)
    {
        lock (_lock)
        {
            _cameras.TryGetValue(cameraId, out var previous);

            if (live)
            {
                var recovered = previous is { Alerted: true };
                _cameras[cameraId] = new Status(Live: true, LastLiveAt: now, MissingSince: null, Alerted: false);
                return recovered ? Transition.Recovered : Transition.None;
            }

            if (previous?.LastLiveAt is null)
            {
                // Never seen streaming: nothing has been lost. Remembered so the status endpoint
                // can say "never" rather than "unknown".
                _cameras[cameraId] = new Status(Live: false, LastLiveAt: null, MissingSince: null, Alerted: false);
                return Transition.None;
            }

            var missingSince = previous.MissingSince ?? now;
            var alerted = previous.Alerted;
            var transition = Transition.None;
            if (!alerted && now - missingSince >= gracePeriod)
            {
                alerted = true;
                transition = Transition.Dropped;
            }

            _cameras[cameraId] = previous with { Live = false, MissingSince = missingSince, Alerted = alerted };
            return transition;
        }
    }

    public Status? Get(Guid cameraId)
    {
        lock (_lock)
        {
            return _cameras.TryGetValue(cameraId, out var status) ? status : null;
        }
    }

    // A camera that no longer exists must not go on being reported missing.
    public void Forget(Guid cameraId)
    {
        lock (_lock)
        {
            _cameras.Remove(cameraId);
        }
    }

    public IReadOnlyCollection<Guid> Known()
    {
        lock (_lock)
        {
            return _cameras.Keys.ToArray();
        }
    }
}
