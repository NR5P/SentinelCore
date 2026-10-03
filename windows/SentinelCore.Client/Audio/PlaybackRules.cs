using System.Globalization;
using SentinelCore.Client.Models;

namespace SentinelCore.Client.Audio;

/// <summary>
/// Sound Check and the rest of the volume arithmetic. The player's element volume is the product
/// of four independent things: the user's own volume, Sound Check's gain for this track, a duck
/// (an announcement, a remote "duck"), and a fade (sleep timer, alarm ramp). Keeping them apart
/// means one never undoes another - an announcement ending mid-fade unducks to the faded level.
/// </summary>
public static class SoundCheck
{
    /// <summary>
    /// MUSIC_AND_PODCASTS.md §11: volume = 10^(gain/20), clamped to 1.0 (Sound Check only ever
    /// turns loud tracks down - there is no headroom to turn quiet ones up without clipping). Album
    /// gain when an album plays in order, so its own dynamics survive; track gain otherwise. Each
    /// falls back to the other when missing, and no ReplayGain at all is 1.0.
    /// </summary>
    public static double Factor(ReplayGainInfo? gain, bool albumMode)
    {
        if (gain is null) return 1.0;
        var db = albumMode ? gain.AlbumGain ?? gain.TrackGain : gain.TrackGain ?? gain.AlbumGain;
        if (db is not { } value || double.IsNaN(value)) return 1.0;
        return Math.Min(1.0, Math.Pow(10, value / 20.0));
    }

    /// <summary>The level a duck drops to (§8: "drops the volume to 20%").</summary>
    public const double DuckLevel = 0.2;

    /// <summary>The element volume: every factor multiplied, kept within 0..1.</summary>
    public static double Effective(double userVolume, double soundCheck, bool ducked, double fade) =>
        Math.Clamp(Math.Clamp(userVolume, 0, 1) * Math.Clamp(soundCheck, 0, 1) * (ducked ? DuckLevel : 1) * Math.Clamp(fade, 0, 1), 0, 1);

    /// <summary>An alarm's ramp: 0 at the start, the target after rampSeconds (§8 rampSeconds).</summary>
    public static double Ramp(double elapsedSeconds, double rampSeconds) =>
        rampSeconds <= 0 ? 1.0 : Math.Clamp(elapsedSeconds / rampSeconds, 0, 1);
}

public enum SleepTimerMode
{
    Off,
    Minutes,
    EndOfItem,
}

/// <summary>
/// The sleep timer (§11: minutes, or the end of the track or episode, with a 10 s fade). Pure:
/// every question takes "now" (and the playing position) so it can be tested without waiting.
/// </summary>
public sealed class SleepTimer
{
    public const double FadeSeconds = 10;

    public SleepTimerMode Mode { get; private set; } = SleepTimerMode.Off;
    public DateTimeOffset? EndsAt { get; private set; }

    public bool IsActive => Mode != SleepTimerMode.Off;

    public void StartMinutes(double minutes, DateTimeOffset now)
    {
        Mode = SleepTimerMode.Minutes;
        EndsAt = now.AddMinutes(Math.Max(0.1, minutes));
    }

    public void StartEndOfItem()
    {
        Mode = SleepTimerMode.EndOfItem;
        EndsAt = null;
    }

    public void Cancel()
    {
        Mode = SleepTimerMode.Off;
        EndsAt = null;
    }

    /// <summary>Seconds until it stops playback, or null for end-of-item / off.</summary>
    public double? RemainingSeconds(DateTimeOffset now) =>
        Mode == SleepTimerMode.Minutes && EndsAt is { } end ? Math.Max(0, (end - now).TotalSeconds) : null;

    /// <summary>
    /// The fade multiplier now: 1 until the last ten seconds, then down to 0. For end-of-item the
    /// last ten seconds are the item's own (position against duration).
    /// </summary>
    public double Fade(DateTimeOffset now, double positionSeconds, double durationSeconds)
    {
        double left;
        switch (Mode)
        {
            case SleepTimerMode.Minutes:
                left = RemainingSeconds(now) ?? double.MaxValue;
                break;
            case SleepTimerMode.EndOfItem:
                if (durationSeconds <= 0) return 1;
                left = durationSeconds - positionSeconds;
                break;
            default:
                return 1;
        }
        return left >= FadeSeconds ? 1 : Math.Clamp(left / FadeSeconds, 0, 1);
    }

    /// <summary>Whether playback should stop now (the minutes ran out).</summary>
    public bool IsDue(DateTimeOffset now) => Mode == SleepTimerMode.Minutes && EndsAt is { } end && now >= end;

    /// <summary>"Sleep in 14:59" / "Sleep at end of episode" / "".</summary>
    public string Label(DateTimeOffset now, bool episode) => Mode switch
    {
        SleepTimerMode.Minutes => $"Sleep in {AudioTime.Clock(RemainingSeconds(now) ?? 0)}",
        SleepTimerMode.EndOfItem => episode ? "Sleep at end of episode" : "Sleep at end of track",
        _ => "",
    };
}

/// <summary>Podcast playback speed, skip intervals and intro/outro skipping.</summary>
public static class PodcastPlayback
{
    public const double SkipBackSeconds = 15;
    public const double SkipForwardSeconds = 30;
    public const double DefaultSpeed = 1.0;

    /// <summary>The speeds offered, the contract's 0.5-3.0 range.</summary>
    public static IReadOnlyList<double> Speeds { get; } = new[] { 0.5, 0.75, 0.8, 0.9, 1.0, 1.1, 1.2, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0 };

    public static double ClampSpeed(double speed) => double.IsNaN(speed) ? 1.0 : Math.Clamp(speed, 0.5, 3.0);

    /// <summary>"1.5×" / "1×".</summary>
    public static string SpeedLabel(double speed) =>
        ClampSpeed(speed).ToString("0.##", CultureInfo.InvariantCulture) + "×";

    /// <summary>The next speed up the list, wrapping to the slowest - what a single speed button cycles through.</summary>
    public static double NextSpeed(double current)
    {
        var next = Speeds.FirstOrDefault(speed => speed > current + 0.001);
        return next == 0 ? Speeds[0] : next;
    }

    /// <summary>The show's remembered speed, or the app's default.</summary>
    public static double SpeedFor(PodcastMySettings? settings, double appDefault) =>
        ClampSpeed(settings?.PlaybackSpeed ?? appDefault);

    /// <summary>
    /// Where an episode starts: the saved spot when it is part-way through, otherwise past the
    /// show's intro when "skip intro" is set.
    /// </summary>
    public static double StartPosition(PodcastEpisode episode, PodcastMySettings? settings)
    {
        if (episode.IsInProgress) return episode.Progress!.PositionSeconds;
        var intro = settings?.SkipIntroSeconds ?? 0;
        return intro > 0 && (episode.DurationSeconds <= 0 || intro < episode.DurationSeconds) ? intro : 0;
    }

    /// <summary>Where an episode counts as over: the duration less the outro to skip, or null for no early end.</summary>
    public static double? EarlyEnd(double durationSeconds, PodcastMySettings? settings)
    {
        var outro = settings?.SkipOutroSeconds ?? 0;
        if (outro <= 0 || durationSeconds <= 0 || outro >= durationSeconds) return null;
        return durationSeconds - outro;
    }

    /// <summary>A seek target kept inside the episode.</summary>
    public static double Skip(double position, double delta, double duration) =>
        Math.Clamp(position + delta, 0, duration > 0 ? Math.Max(0, duration - 0.5) : double.MaxValue);
}

/// <summary>
/// Listening reports: how much of a track was actually heard (not where the scrubber ended up - a
/// seek is not listening), and whether to send a report yet.
/// </summary>
public sealed class ListenMeter
{
    private DateTimeOffset? _resumedAt;
    private double _accumulated;

    public double Seconds(DateTimeOffset now) =>
        _accumulated + (_resumedAt is { } since ? Math.Max(0, (now - since).TotalSeconds) : 0);

    public void Resume(DateTimeOffset now) => _resumedAt ??= now;

    public void Pause(DateTimeOffset now)
    {
        if (_resumedAt is { } since) _accumulated += Math.Max(0, (now - since).TotalSeconds);
        _resumedAt = null;
    }

    /// <summary>Starts again from nothing, for the next track.</summary>
    public void Reset(DateTimeOffset now, bool playing)
    {
        _accumulated = 0;
        _resumedAt = playing ? now : null;
    }

    /// <summary>
    /// The §4 played body. Speed counts: a minute of 1.5× listening covered 90 seconds of the track,
    /// which is what "secondsListened" is compared against.
    /// </summary>
    public static MusicPlayedInput Report(double secondsListened, bool completed, bool skipped, DateTimeOffset playedAt, string deviceName) =>
        new()
        {
            SecondsListened = Math.Round(Math.Max(0, secondsListened), 1),
            Completed = completed,
            Skipped = skipped && !completed,
            PlayedAt = playedAt,
            DeviceName = deviceName,
        };

    /// <summary>The server's own rule (§4), for showing whether a play will count: ≥ 30 s and (≥ 50% or ≥ 240 s).</summary>
    public static bool CountsAsPlay(double secondsListened, double durationSeconds) =>
        secondsListened >= 30 && (secondsListened >= 240 || (durationSeconds > 0 && secondsListened >= durationSeconds * 0.5));

    /// <summary>Worth telling the server at all - anything under a couple of seconds is a mis-click.</summary>
    public static bool WorthReporting(double secondsListened) => secondsListened >= 2;
}

/// <summary>
/// "Send at most every N seconds" - the 15 s podcast progress and the 30 s now-playing heartbeat.
/// </summary>
public sealed class IntervalGate
{
    private readonly TimeSpan _interval;
    private DateTimeOffset? _last;

    public IntervalGate(TimeSpan interval) => _interval = interval;

    /// <summary>True (and remembers now) when the interval has passed since the last time it said true.</summary>
    public bool TryPass(DateTimeOffset now)
    {
        if (_last is { } last && now - last < _interval) return false;
        _last = now;
        return true;
    }

    /// <summary>Counts an out-of-band send (a pause, a seek) so the next tick waits a full interval.</summary>
    public void Mark(DateTimeOffset now) => _last = now;

    public void Reset() => _last = null;
}

/// <summary>"Playing on Kitchen iPad - continue here?"</summary>
public static class ContinueListening
{
    /// <summary>How old another device's report can be and still be offered.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    /// <summary>
    /// The other device worth offering: not this one, something in its queue, reported within
    /// <see cref="MaxAge"/>. One that is playing right now wins over one that is paused; then the
    /// newest.
    /// </summary>
    public static NowPlaying? Pick(IEnumerable<NowPlaying> devices, string myDeviceId, DateTimeOffset now) =>
        devices
            .Where(device => device.DeviceId != myDeviceId && device.Queue.Count > 0)
            .Where(device => device.UpdatedAt is { } updated && now - updated <= MaxAge)
            .OrderByDescending(device => device.Playing)
            .ThenByDescending(device => device.UpdatedAt)
            .FirstOrDefault();

    /// <summary>Where the other device is now, allowing for time passing while it plays.</summary>
    public static double EstimatedPosition(NowPlaying device, DateTimeOffset now)
    {
        if (!device.Playing || device.UpdatedAt is not { } updated) return device.PositionSeconds;
        return device.PositionSeconds + Math.Max(0, (now - updated).TotalSeconds) * (device.Speed <= 0 ? 1 : device.Speed);
    }

    public static string Banner(NowPlaying device) =>
        $"{(device.Playing ? "Playing" : "Paused")} on {(string.IsNullOrWhiteSpace(device.DeviceName) ? "another device" : device.DeviceName)}" +
        (device.Context?.Name is { Length: > 0 } name ? $" · {name}" : "");
}

/// <summary>This PC's identity as an audio device: a stable id and a readable name.</summary>
public static class AudioDevice
{
    private const string IdKey = "audio.deviceId";
    private const string NameKey = "audio.deviceName";

    public static string Id(PreferenceStore preferences)
    {
        var id = preferences.GetString(IdKey);
        if (id.Length > 0) return id;
        id = "windows-" + Guid.NewGuid().ToString("N")[..12];
        preferences.Set(IdKey, id);
        return id;
    }

    public static string Name(PreferenceStore preferences)
    {
        var name = preferences.GetString(NameKey);
        return name.Length > 0 ? name : DefaultName();
    }

    public static void SetName(PreferenceStore preferences, string name) => preferences.Set(NameKey, name.Trim());

    public static string DefaultName()
    {
        var machine = Environment.MachineName;
        return string.IsNullOrWhiteSpace(machine) ? "Windows PC" : machine;
    }
}
