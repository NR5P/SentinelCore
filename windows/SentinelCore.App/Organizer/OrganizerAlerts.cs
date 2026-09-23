using System.Windows.Threading;
using SentinelCore.Client.Organizer;

namespace SentinelCore.App.Organizer;

/// <summary>
/// Raises reminder and event alerts from this PC's own copy, so they fire with the server down.
/// The engine plans them (see AlertPlanner) and OrganizerAlertQueue holds them until they're due;
/// this checks every few seconds and shows each one as a tray notification.
/// </summary>
internal sealed class OrganizerAlerts : IDisposable
{
    private readonly OrganizerEngine _engine;
    private readonly Action<IReadOnlyList<PlannedAlert>> _show;
    private readonly DispatcherTimer _timer;
    private readonly OrganizerAlertQueue _queue = new();

    public OrganizerAlerts(OrganizerEngine engine, Action<IReadOnlyList<PlannedAlert>> show)
    {
        _engine = engine;
        _show = show;
        _engine.AlertsChanged += OnPlanChanged;
        OnPlanChanged(_engine.PlannedAlerts);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += (_, _) =>
        {
            // Together, so several due at once are one notification rather than each replacing the last.
            if (_queue.TakeDue(DateTimeOffset.Now) is { Count: > 0 } due) _show(due);
        };
        _timer.Start();
    }

    private void OnPlanChanged(IReadOnlyList<PlannedAlert> plan) => _queue.Update(plan, DateTimeOffset.Now);

    public void Dispose()
    {
        _timer.Stop();
        _engine.AlertsChanged -= OnPlanChanged;
    }
}
