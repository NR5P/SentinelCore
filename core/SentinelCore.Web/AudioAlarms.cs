using System.Globalization;
using SentinelCore;

namespace SentinelCore.Web;

// When a wake-up alarm next goes off, in the household's time zone. Pure, for the tests.
public static class AudioAlarmSchedule
{
    public static bool TryParseTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text?.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    // Null when the alarm is off or its time cannot be read. No days = once, at the next time.
    public static DateTimeOffset? Next(AudioWakeAlarm alarm, DateTimeOffset after, TimeZoneInfo zone)
    {
        if (!alarm.Enabled || !TryParseTime(alarm.Time, out var time)) return null;
        var days = alarm.Days.Select(d => Array.IndexOf(AudioWakeAlarm.DayNames, d)).Where(i => i >= 0).ToHashSet();
        var localNow = TimeZoneInfo.ConvertTime(after, zone);
        for (var offset = 0; offset <= 8; offset++)
        {
            var date = DateOnly.FromDateTime(localNow.DateTime).AddDays(offset);
            // Monday = 0, as DayNames is.
            if (days.Count > 0 && !days.Contains(((int)date.DayOfWeek + 6) % 7)) continue;
            var local = date.ToDateTime(time);
            // 2:30 on the night the clocks go forward does not exist; that morning it rings at 3:30.
            if (zone.IsInvalidTime(local)) local = local.AddHours(1);
            var at = new DateTimeOffset(local, zone.GetUtcOffset(local));
            if (at > after) return at.ToUniversalTime();
        }
        return null;
    }
}

// Tells one person something about their audio (an alarm that missed its speaker). A push to
// that person's phones only, through the same push setup as everything else.
public interface IAudioNotifier
{
    Task PushAsync(string userKey, string title, string body, CancellationToken cancellationToken);
}

public sealed class AudioNotifier(AppSettingsFile settings, PushNotificationDispatcher push, ILogger<AudioNotifier> logger) : IAudioNotifier
{
    public const string PushType = "audio";

    public async Task PushAsync(string userKey, string title, string body, CancellationToken cancellationToken)
    {
        var options = await settings.GetSectionAsync<PushNotificationOptions>(PushNotificationOptions.SectionName, cancellationToken)
            ?? new PushNotificationOptions();
        if (!options.Enabled || !PushProviderSelection.AnyConfigured(options)) return;
        try
        {
            // With login off everyone shares one view, so everyone's phones are "this person's".
            await push.SendAsync(options, new PushMessage(title, body, null, null, PushType), cancellationToken,
                userKey.Length == 0 ? null : device => string.Equals(device.Username, userKey, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "An audio push to {User} failed", userKey);
        }
    }
}

public sealed record AudioPlayResult(bool Ok, string? Error, long? Seq = null)
{
    public static AudioPlayResult Failed(string error) => new(false, error);
}

// Puts something on a speaker: works out the queue for a source (a playlist, a show, the
// briefing...) and sends the speaker a play command. Used by wake-up alarms and their Test
// button.
public sealed class AudioDirector(
    SqliteAudioStore store,
    AudioSpeakerHub hub,
    IMusicAudioSource music,
    IPodcastAudioSource podcasts,
    AudioBriefingBuilder briefing,
    TimeProvider clock)
{
    public async Task<AudioPlayResult> PlaySourceAsync(
        string userKey, string speakerId, AudioAlarmSource source, double volume, double rampSeconds, string from, CancellationToken cancellationToken)
    {
        var speaker = (await store.GetSpeakersAsync(cancellationToken)).FirstOrDefault(s => s.DeviceId == speakerId);
        if (speaker is null) return AudioPlayResult.Failed("That speaker isn't set up any more.");
        if (!speaker.IsOnline(clock.GetUtcNow())) return AudioPlayResult.Failed($"{speaker.Name} is offline.");

        List<AudioQueueEntry> queue;
        string? announcement = null;
        if (source.Type == AudioSourceTypes.Briefing)
        {
            var built = await briefing.BuildAsync(userKey, cancellationToken);
            announcement = built.SpokenText;
            queue = built.Episodes.Select(e => new AudioQueueEntry(AudioItemTypes.Episode, e.Id)).ToList();
        }
        else if (source.Type == AudioSourceTypes.Podcast)
        {
            if (!await podcasts.EnabledAsync(cancellationToken)) return AudioPlayResult.Failed("Podcasts are turned off in Settings.");
            queue = (await podcasts.QueueForSourceAsync(userKey, source.Type, source.Id, cancellationToken))
                .Select(id => new AudioQueueEntry(AudioItemTypes.Episode, id)).ToList();
        }
        else if (AudioSourceTypes.IsMusic(source.Type))
        {
            if (!await music.EnabledAsync(cancellationToken)) return AudioPlayResult.Failed("Music is turned off in Settings.");
            queue = (await music.QueueForSourceAsync(userKey, source.Type, source.Id, cancellationToken))
                .Select(id => new AudioQueueEntry(AudioItemTypes.Track, id)).ToList();
        }
        else return AudioPlayResult.Failed($"\"{source.Type}\" is not something an alarm can play.");

        if (queue.Count == 0 && announcement is null)
            return AudioPlayResult.Failed($"{source.Name ?? "That"} has nothing to play.");

        // Speakers apply commands in order and finish speaking before the next one, so the
        // briefing is read first and the episodes follow it.
        if (announcement is not null)
            hub.Send(speakerId, new AudioSpeakerCommand { Action = AudioSpeakerActions.Announce, Text = announcement, From = from, Volume = volume });
        if (queue.Count == 0) return new AudioPlayResult(true, null, hub.LatestSeq);

        var seq = hub.Send(speakerId, new AudioSpeakerCommand
        {
            Action = AudioSpeakerActions.Play,
            Queue = queue,
            Index = 0,
            PositionSeconds = 0,
            Shuffle = source.Type == AudioSourceTypes.Shuffle,
            Volume = volume,
            RampSeconds = rampSeconds,
            From = from,
            Context = new AudioContext(source.Type, source.Id, source.Name)
        });
        return new AudioPlayResult(true, null, seq);
    }
}

// Rings wake-up alarms. Checks every ten seconds; an alarm that was due while the server was
// down is skipped (a 6:45 alarm going off at 9:10 because the server restarted helps no one),
// unless it is less than ten minutes late.
public sealed class AudioAlarmWorker(
    SqliteAudioStore store,
    AudioDirector director,
    IAudioNotifier notifier,
    TimeProvider clock,
    ILogger<AudioAlarmWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Checking wake-up alarms failed"); }
            try { await Task.Delay(Tick, clock, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        foreach (var alarm in await store.GetAlarmsAsync(null, cancellationToken))
        {
            if (!alarm.Enabled) continue;
            if (alarm.NextFireAt is null)
            {
                alarm.NextFireAt = AudioAlarmSchedule.Next(alarm, now, TimeZone);
                await store.SaveAlarmAsync(alarm, cancellationToken);
                continue;
            }
            if (alarm.NextFireAt > now) continue;

            var late = now - alarm.NextFireAt.Value;
            if (late <= Grace) await FireAsync(alarm, cancellationToken);
            else logger.LogInformation("Skipped wake-up alarm {Name} that was due {Late} ago", alarm.Name, late);

            if (alarm.Days.Count == 0)
            {
                alarm.Enabled = false;
                alarm.NextFireAt = null;
            }
            else alarm.NextFireAt = AudioAlarmSchedule.Next(alarm, now, TimeZone);
            await store.SaveAlarmAsync(alarm, cancellationToken);
        }
    }

    private async Task FireAsync(AudioWakeAlarm alarm, CancellationToken cancellationToken)
    {
        alarm.LastFiredAt = clock.GetUtcNow();
        AudioPlayResult result;
        try
        {
            result = await director.PlaySourceAsync(alarm.UserKey, alarm.SpeakerId, alarm.Source, alarm.Volume, alarm.RampSeconds,
                $"Alarm: {alarm.Name}", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Wake-up alarm {Name} failed", alarm.Name);
            result = AudioPlayResult.Failed("Something went wrong starting it.");
        }
        if (result.Ok) return;

        logger.LogInformation("Wake-up alarm {Name} could not play: {Error}", alarm.Name, result.Error);
        var time = AudioAlarmSchedule.TryParseTime(alarm.Time, out var t) ? t.ToString("h:mm tt", CultureInfo.InvariantCulture) : alarm.Time;
        await notifier.PushAsync(alarm.UserKey, "⏰ Wake-up alarm", $"Your {time} alarm couldn't play: {result.Error}", cancellationToken);
    }
}
