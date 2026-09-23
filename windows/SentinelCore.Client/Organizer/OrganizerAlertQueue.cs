namespace SentinelCore.Client.Organizer;

/// <summary>
/// The alerts this PC still has to show, fed by the engine's plan and drained as each comes due.
/// </summary>
/// <remarks>
/// Kept as its own list rather than read from the engine's plan when a timer ticks: the plan only
/// ever holds alerts still in the future, so a sync landing a second after one came due would
/// replan it away before it had been shown. Here an alert stays until it's taken, and only one the
/// plan drops while still in the future - a reminder ticked off or deleted - is forgotten.
/// </remarks>
public sealed class OrganizerAlertQueue
{
    // Past this, an alert that couldn't be shown on time - the PC was asleep - is no longer worth
    // interrupting anyone with.
    public static readonly TimeSpan Stale = TimeSpan.FromHours(2);

    private readonly object _gate = new();
    private readonly Dictionary<string, PlannedAlert> _waiting = new();

    public void Update(IReadOnlyList<PlannedAlert> plan, DateTimeOffset now)
    {
        lock (_gate)
        {
            var planned = plan.Select(alert => alert.Id).ToHashSet();
            foreach (var id in _waiting.Where(pair => pair.Value.FireAt > now && !planned.Contains(pair.Key)).Select(pair => pair.Key).ToList())
            {
                _waiting.Remove(id);
            }
            foreach (var alert in plan) _waiting[alert.Id] = alert;
        }
    }

    /// <summary>Everything due by <paramref name="now"/>, oldest first, each only once. Ones too
    /// old to be worth showing are dropped rather than returned.</summary>
    public List<PlannedAlert> TakeDue(DateTimeOffset now)
    {
        List<PlannedAlert> due;
        lock (_gate)
        {
            due = _waiting.Values.Where(alert => alert.FireAt <= now).OrderBy(alert => alert.FireAt).ToList();
            foreach (var alert in due) _waiting.Remove(alert.Id);
        }
        return due.Where(alert => now - alert.FireAt < Stale).ToList();
    }
}
