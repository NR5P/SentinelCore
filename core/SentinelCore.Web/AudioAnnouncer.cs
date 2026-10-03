using SentinelCore;

namespace SentinelCore.Web;

// The speakers' part in the rest of the house. When the home alarm goes off every speaker
// pauses (and says why, where announcements are allowed); with "announce on speakers" on, a
// camera alert that notified someone - a person at the door, a car in the drive - is spoken on
// the speakers that allow announcements, at most once a minute per camera. Both switches live
// in Settings → MUSIC.
public sealed class AudioAnnouncer(
    SqliteAudioStore store,
    AudioSpeakerHub hub,
    AlarmEngine alarm,
    IEventStore events,
    AppSettingsFile settings,
    TimeProvider clock,
    ILogger<AudioAnnouncer> logger) : BackgroundService
{
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan PerCameraGap = TimeSpan.FromMinutes(1);
    // A summary longer than this is cut at a word: an announcement is a heads-up, not a report.
    private const int MaxSpoken = 140;

    private readonly Dictionary<string, DateTimeOffset> _lastByCamera = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _announced = [];
    private DateTimeOffset _since;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        alarm.Triggered += OnAlarmTriggered;
        _since = clock.GetUtcNow();
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        alarm.Triggered -= OnAlarmTriggered;
        return base.StopAsync(cancellationToken);
    }

    private void OnAlarmTriggered(string reason) => _ = Task.Run(() => AlarmTriggeredAsync(reason, CancellationToken.None));

    public async Task AlarmTriggeredAsync(string reason, CancellationToken cancellationToken)
    {
        try
        {
            var options = await AudioSpeakerOptions.ReadAsync(settings, cancellationToken);
            if (!options.PauseSpeakersOnAlarm) return;
            var now = clock.GetUtcNow();
            foreach (var speaker in await store.GetSpeakersAsync(cancellationToken))
            {
                if (!speaker.IsOnline(now)) continue;
                hub.Send(speaker.DeviceId, new AudioSpeakerCommand { Action = AudioSpeakerActions.Pause, From = "Home alarm" });
                if (speaker.AllowAnnouncements)
                    hub.Send(speaker.DeviceId, new AudioSpeakerCommand
                    {
                        Action = AudioSpeakerActions.Announce,
                        Text = $"The alarm is going off. {Sentence(reason)}",
                        From = "Home alarm"
                    });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't tell the speakers the alarm went off");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CheckCameraAlertsAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "Checking camera alerts for the speakers failed"); }
            try { await Task.Delay(Poll, clock, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task CheckCameraAlertsAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var since = _since;
        // An alert's analysis takes a few seconds to finish, so the window looks back a little
        // over what was already seen and relies on _announced to say each one once.
        _since = now - TimeSpan.FromMinutes(1);
        var options = await AudioSpeakerOptions.ReadAsync(settings, cancellationToken);
        if (!options.AnnounceOnSpeakers) return;

        var listeners = (await store.GetSpeakersAsync(cancellationToken)).Where(s => s.AllowAnnouncements && s.IsOnline(now)).ToList();
        if (listeners.Count == 0) return;

        var recent = await events.GetEventsAsync(since, now.AddSeconds(1), cancellationToken);
        foreach (var alert in recent)
        {
            if (alert.NotificationRecommended != true || alert.Status != "completed" || !_announced.Add(alert.Id)) continue;
            if (_lastByCamera.TryGetValue(alert.Camera, out var last) && now - last < PerCameraGap) continue;
            _lastByCamera[alert.Camera] = now;

            var text = SpokenAlert(alert.Camera, alert.Summary);
            foreach (var speaker in listeners)
                hub.Send(speaker.DeviceId, new AudioSpeakerCommand { Action = AudioSpeakerActions.Announce, Text = text, From = alert.Camera });
        }
        if (_announced.Count > 2000) _announced.Clear();
    }

    // "Front Door: A person in a red jacket is walking up to the porch."
    public static string SpokenAlert(string camera, string? summary)
    {
        var text = string.IsNullOrWhiteSpace(summary) ? "Someone is there." : Sentence(summary);
        if (text.Length > MaxSpoken)
        {
            var cut = text.LastIndexOf(' ', MaxSpoken);
            text = text[..(cut > 40 ? cut : MaxSpoken)].TrimEnd(',', ';', ':') + ".";
        }
        return $"{camera}: {text}";
    }

    private static string Sentence(string text)
    {
        text = text.Trim();
        return text.Length == 0 || text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') ? text : text + ".";
    }
}

// The two speaker switches, read from the Music section of Settings.
public sealed class AudioSpeakerOptions
{
    public const string SectionName = "Music";

    public bool PauseSpeakersOnAlarm { get; set; } = true;
    public bool AnnounceOnSpeakers { get; set; }

    public static async Task<AudioSpeakerOptions> ReadAsync(AppSettingsFile settings, CancellationToken cancellationToken) =>
        await settings.GetSectionAsync<AudioSpeakerOptions>(SectionName, cancellationToken) ?? new AudioSpeakerOptions();
}
