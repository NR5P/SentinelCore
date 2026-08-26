namespace SentinelCore.Client.Networking;

/// <summary>
/// Runs PTZ commands one at a time, in the order they were issued.
/// </summary>
/// <remarks>
/// A press and a release used to start independent tasks, which is two HTTP requests with no
/// ordering between them. On a quick tap both are in flight at once, and whenever the stop won that
/// race the camera was sent stop-then-move and kept turning - the intermittent "sticks and spins"
/// this exists to prevent. It hides on a long press, where the release lands long after the move,
/// which is exactly what makes it so easy to ship.
///
/// The asymmetry is the whole point: a lost move means the camera didn't budge, which is
/// self-evident from the video. A lost - or overtaken - stop means a camera that keeps turning
/// until something else stops it.
///
/// Chaining each command onto the previous one rather than holding a queue of closures keeps this
/// small, and it is self-limiting: an already-finished task completes immediately, so a burst of
/// taps drains as fast as the requests do rather than accumulating.
/// </remarks>
public sealed class PtzCommandQueue
{
    private readonly object _gate = new();
    private Task _pending = Task.CompletedTask;

    /// <summary>Queues a command behind everything already issued.</summary>
    public void Enqueue(Func<Task> command)
    {
        lock (_gate)
        {
            var previous = _pending;
            _pending = RunAfterAsync(previous, command);
        }
    }

    private static async Task RunAfterAsync(Task previous, Func<Task> command)
    {
        // Failures are contained per command: one that throws must not break the chain, or the
        // next press would await a faulted task and the stop behind it would never run.
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The previous command already reported itself; this one still has to run.
        }

        try
        {
            await command().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // PTZ is fire-and-forget by design - a move that didn't land is obvious from the
            // picture. Callers that need to say something wrap their own command.
        }
    }

    /// <summary>Waits for everything queued so far. The app fires and forgets; the tests wait.</summary>
    public Task WaitForIdleAsync()
    {
        lock (_gate)
        {
            return _pending;
        }
    }
}
