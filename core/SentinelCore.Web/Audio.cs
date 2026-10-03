using System.Text.Json.Serialization;

namespace SentinelCore.Web;

// What the Music and Podcasts tabs share: where each person is in their queue on each device
// (play where you left off), the household's speakers, wake-up alarms and ListenBrainz
// accounts. Storage is SqliteAudioStore (audio.db), the HTTP surface AudioEndpoints, and the
// wire contract core/docs/MUSIC_AND_PODCASTS.md §7-§9.

public sealed record AudioQueueEntry(string Type, Guid Id);

public sealed record AudioContext(string? Type, string? Id, string? Name);

// One device's player, as that device last reported it. The queue is ids only; a device taking
// over asks /resolve for the tracks and episodes behind them.
public sealed class AudioNowPlaying
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Kind { get; set; } = "music";
    public List<AudioQueueEntry> Queue { get; set; } = [];
    public int Index { get; set; }
    public double PositionSeconds { get; set; }
    public bool Playing { get; set; }
    public bool Shuffle { get; set; }
    public string Repeat { get; set; } = "off";
    public double Speed { get; set; } = 1;
    public AudioContext? Context { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public const int MaxQueue = 500;
    public static readonly TimeSpan Forgotten = TimeSpan.FromDays(7);
}

// What a speaker is playing, as it reports it. Passed through to whoever looks at the speaker.
public sealed class AudioSpeakerState
{
    public bool Playing { get; set; }
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public string? ArtUrl { get; set; }
    public double? PositionSeconds { get; set; }
    public double? DurationSeconds { get; set; }
    public double? Volume { get; set; }
    public string? Kind { get; set; }
    public string? ItemId { get; set; }
}

public sealed class AudioSpeaker
{
    public string DeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "web";
    public bool AllowAnnouncements { get; set; }
    public double Volume { get; set; } = 0.6;
    public DateTimeOffset LastSeenAt { get; set; }
    public AudioSpeakerState? State { get; set; }

    // A speaker heartbeats every minute; three missed and it is offline.
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(3);
    public static readonly string[] Kinds = ["tv", "kiosk", "phone", "tablet", "mac", "windows", "web"];

    public bool IsOnline(DateTimeOffset now) => now - LastSeenAt <= OfflineAfter;
}

public static class AudioSpeakerActions
{
    public const string Play = "play";
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Next = "next";
    public const string Previous = "previous";
    public const string Seek = "seek";
    public const string Volume = "volume";
    public const string Stop = "stop";
    public const string Duck = "duck";
    public const string Unduck = "unduck";
    public const string Announce = "announce";

    public static readonly string[] All = [Play, Pause, Resume, Next, Previous, Seek, Volume, Stop, Duck, Unduck, Announce];
}

public sealed class AudioSpeakerCommand
{
    public long Seq { get; set; }
    public DateTimeOffset At { get; set; }
    public string? From { get; set; }
    public string Action { get; set; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AudioQueueEntry>? Queue { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Index { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PositionSeconds { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Shuffle { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Volume { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RampSeconds { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AudioContext? Context { get; set; }

    // A command nobody picked up in this long is stale: "pause" two minutes late is a surprise.
    public static readonly TimeSpan Expires = TimeSpan.FromMinutes(2);
}

public sealed record AudioAlarmSource(string Type, string? Id, string? Name);

public sealed class AudioWakeAlarm
{
    public Guid Id { get; set; }
    public string UserKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    // "HH:mm", the server's local time.
    public string Time { get; set; } = "07:00";
    public List<string> Days { get; set; } = [];
    public string SpeakerId { get; set; } = string.Empty;
    public AudioAlarmSource Source { get; set; } = new(AudioSourceTypes.Shuffle, null, null);
    public double Volume { get; set; } = 0.5;
    public double RampSeconds { get; set; } = 120;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? NextFireAt { get; set; }
    public DateTimeOffset? LastFiredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public static readonly string[] DayNames = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];
}

public sealed record AudioListenBrainzAccount(string UserKey, string Token, string Username);

// The audio.db part of a backup: everyone's alarms and ListenBrainz connections. The rest of
// audio.db (now-playing, speakers) is a few minutes old at best and comes back on its own.
public sealed record AudioBackup(
    IReadOnlyList<AudioWakeAlarm>? Alarms = null,
    IReadOnlyList<AudioListenBrainzAccount>? ListenBrainz = null);

public sealed class AudioNowPlayingInput
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Kind { get; set; } = "music";
    public List<AudioQueueEntry>? Queue { get; set; }
    public int Index { get; set; }
    public double PositionSeconds { get; set; }
    public bool Playing { get; set; }
    public bool Shuffle { get; set; }
    public string? Repeat { get; set; }
    public double? Speed { get; set; }
    public AudioContext? Context { get; set; }
}

public sealed class AudioSpeakerInput
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "web";
    public bool AllowAnnouncements { get; set; }
    public double? Volume { get; set; }
}

public sealed class AudioSpeakerCommandInput
{
    public string Action { get; set; } = string.Empty;
    public List<AudioQueueEntry>? Queue { get; set; }
    public int? Index { get; set; }
    public double? PositionSeconds { get; set; }
    public bool? Shuffle { get; set; }
    public double? Volume { get; set; }
    public double? RampSeconds { get; set; }
    public string? Text { get; set; }
    public AudioContext? Context { get; set; }
}

public sealed class AudioWakeAlarmInput
{
    public string Name { get; set; } = string.Empty;
    public string Time { get; set; } = string.Empty;
    public List<string>? Days { get; set; }
    public string SpeakerId { get; set; } = string.Empty;
    public AudioAlarmSource? Source { get; set; }
    public double? Volume { get; set; }
    public double? RampSeconds { get; set; }
    public bool? Enabled { get; set; }
}

public sealed class AudioListenBrainzInput
{
    public string? Token { get; set; }
}
