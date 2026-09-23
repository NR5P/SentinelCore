namespace SentinelCore.Web;

// Tells every registered phone that the Organizer changed, with a silent push, so each syncs in
// the background and its Home Screen widgets stay current without the app being opened.
//
// Coalesced: a burst of edits (typing into a note syncs every few seconds) sends one push a few
// seconds after it settles, and never more than one a minute - Apple drops background pushes
// that come too often, and a phone woken every few seconds would drain for nothing.
public sealed class OrganizerPushNudger
{
    public static readonly TimeSpan DefaultSettle = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan DefaultMinimumGap = TimeSpan.FromSeconds(60);

    private readonly Func<Task> _send;
    private readonly TimeSpan _settle;
    private readonly TimeSpan _minimumGap;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private bool _scheduled;
    private DateTimeOffset _lastSent = DateTimeOffset.MinValue;

    public OrganizerPushNudger(AppSettingsFile settings, PushNotificationDispatcher dispatcher, ILogger<OrganizerPushNudger> logger)
        : this(() => SendAsync(settings, dispatcher, logger), DefaultSettle, DefaultMinimumGap, logger)
    {
    }

    // Public for the tests: this project has no InternalsVisibleTo.
    public OrganizerPushNudger(Func<Task> send, TimeSpan settle, TimeSpan minimumGap, ILogger logger)
    {
        _send = send;
        _settle = settle;
        _minimumGap = minimumGap;
        _logger = logger;
    }

    // Fire and forget; the push goes out on its own time.
    public void Changed()
    {
        TimeSpan wait;
        lock (_lock)
        {
            if (_scheduled) return;
            _scheduled = true;
            var now = DateTimeOffset.UtcNow;
            var earliest = _lastSent == DateTimeOffset.MinValue ? now : _lastSent + _minimumGap;
            wait = earliest > now + _settle ? earliest - now : _settle;
        }
        _ = SendAfterAsync(wait);
    }

    private async Task SendAfterAsync(TimeSpan wait)
    {
        try
        {
            await Task.Delay(wait);
            // Cleared before sending, so an edit made while this push is out schedules the next.
            lock (_lock)
            {
                _scheduled = false;
                _lastSent = DateTimeOffset.UtcNow;
            }
            await _send();
        }
        catch (Exception ex)
        {
            lock (_lock) _scheduled = false;
            _logger.LogWarning(ex, "Could not send the Organizer change push");
        }
    }

    private static async Task SendAsync(AppSettingsFile settings, PushNotificationDispatcher dispatcher, ILogger logger)
    {
        var options = await settings.GetSectionAsync<PushNotificationOptions>(PushNotificationOptions.SectionName, CancellationToken.None)
            ?? new PushNotificationOptions();
        if (!options.Enabled || !PushProviderSelection.AnyConfigured(options)) return;
        var report = await dispatcher.SendAsync(options, PushMessage.OrganizerChanged(), CancellationToken.None);
        logger.LogDebug("Organizer change push sent to {Delivered}/{Attempted} devices", report.Delivered, report.Attempted);
    }
}
