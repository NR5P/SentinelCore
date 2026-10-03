using System.Text.Json.Serialization;

namespace SentinelCore.Client.Models;

// What the Music and Podcasts tabs share - /api/audio. The contract is
// core/docs/MUSIC_AND_PODCASTS.md §7-§9: play where you left off, speakers, wake-up alarms, the
// morning briefing, stats and ListenBrainz.

/// <summary>One entry of a queue as the server stores it: a track or an episode, by id.</summary>
public sealed class AudioQueueRef
{
    public string Type { get; init; } = "track"; // track | episode
    public string Id { get; init; } = "";
}

/// <summary>Where a queue came from - an album, a playlist, a show - so another device can say so.</summary>
public sealed class AudioContext
{
    public string Type { get; init; } = "queue"; // album | playlist | artist | mix | podcast | queue | search | radio
    public string? Id { get; init; }
    public string? Name { get; init; }
}

/// <summary>What one device is playing (§7). PUT without updatedAt; GET carries it.</summary>
public sealed class NowPlaying
{
    private List<AudioQueueRef> _queue = new();

    public string DeviceId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string Kind { get; init; } = "music"; // music | podcast
    public List<AudioQueueRef> Queue { get => _queue; init => _queue = value ?? new(); }
    public int Index { get; init; }
    public double PositionSeconds { get; init; }
    public bool Playing { get; init; }
    public bool Shuffle { get; init; }
    public string Repeat { get; init; } = "off"; // off | all | one
    public double Speed { get; init; } = 1.0;
    public AudioContext? Context { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>GET /api/audio/now-playing/{deviceId}/resolve: the queue with every item filled in.</summary>
public sealed class NowPlayingResolved
{
    private List<MusicTrack> _tracks = new();
    private List<PodcastEpisode> _episodes = new();

    public NowPlaying NowPlaying { get; init; } = new();
    public List<MusicTrack> Tracks { get => _tracks; init => _tracks = value ?? new(); }
    public List<PodcastEpisode> Episodes { get => _episodes; init => _episodes = value ?? new(); }
}

// ---------------------------------------------------------------- Speakers (§8)

public sealed class SpeakerRegistration
{
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "windows";
    public bool AllowAnnouncements { get; init; }
    public double Volume { get; init; } = 0.6;
}

public sealed class SpeakerState
{
    public bool Playing { get; init; }
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public string? ArtUrl { get; init; }
    public double PositionSeconds { get; init; }
    public double DurationSeconds { get; init; }
    public double Volume { get; init; }
    public string? Kind { get; init; }
    public string? ItemId { get; init; }
}

public sealed class Speaker
{
    public string DeviceId { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Kind { get; init; }
    public bool Online { get; init; }
    public bool AllowAnnouncements { get; init; }
    public double Volume { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
    public SpeakerState? State { get; init; }
}

/// <summary>Something sent to a speaker. Only the fields the action needs are set.</summary>
public class SpeakerCommand
{
    public string Action { get; init; } = "";
    public List<AudioQueueRef>? Queue { get; init; }
    public int? Index { get; init; }
    public double? PositionSeconds { get; init; }
    public bool? Shuffle { get; init; }
    public double? Volume { get; init; }
    public double? RampSeconds { get; init; }
    public string? Text { get; init; }
    public AudioContext? Context { get; init; }
}

/// <summary>A command as delivered by the long poll: the command plus its sequence number and sender.</summary>
public sealed class DeliveredSpeakerCommand : SpeakerCommand
{
    public long Seq { get; init; }
    public DateTimeOffset? At { get; init; }
    public string? From { get; init; }
}

public sealed class SpeakerCommandsReply
{
    private List<DeliveredSpeakerCommand> _commands = new();

    public List<DeliveredSpeakerCommand> Commands { get => _commands; init => _commands = value ?? new(); }
    public long? LatestSeq { get; init; }
}

public sealed class SpeakerCommandAccepted
{
    public long Seq { get; init; }
}

// ---------------------------------------------------------------- Wake-up alarms (§9)

public sealed class WakeAlarmSource
{
    public string Type { get; set; } = "shuffle"; // playlist | album | artist | mix | briefing | podcast | shuffle
    public string? Id { get; set; }
    public string? Name { get; set; }
}

public sealed class WakeAlarm
{
    private List<string> _days = new();

    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public string Time { get; set; } = "07:00";
    public List<string> Days { get => _days; set => _days = value ?? new(); }
    public string SpeakerId { get; set; } = "";
    public WakeAlarmSource Source { get; set; } = new();
    public double Volume { get; set; } = 0.5;
    public double RampSeconds { get; set; } = 120;
    public bool Enabled { get; set; } = true;

    // Read-only on the server; never sent back meaningfully.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public DateTimeOffset? NextFireAt { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public DateTimeOffset? LastFiredAt { get; set; }
}

// ---------------------------------------------------------------- Briefing, stats, ListenBrainz

public sealed class MorningBriefing
{
    private List<string> _speech = new();
    private List<PodcastEpisode> _episodes = new();

    public string? Greeting { get; init; }
    public List<string> Speech { get => _speech; init => _speech = value ?? new(); }
    public List<PodcastEpisode> Episodes { get => _episodes; init => _episodes = value ?? new(); }

    /// <summary>Everything to say, in order: the greeting first when there is one.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> AllLines =>
        (string.IsNullOrWhiteSpace(Greeting) ? Speech : new[] { Greeting! }.Concat(Speech))
        .Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
}

public sealed class StatsArtist
{
    public MusicArtist Artist { get; init; } = new();
    public int Plays { get; init; }
    public double Minutes { get; init; }
}

public sealed class StatsAlbum
{
    public MusicAlbum Album { get; init; } = new();
    public int Plays { get; init; }
}

public sealed class StatsTrack
{
    public MusicTrack Track { get; init; } = new();
    public int Plays { get; init; }
}

public sealed class StatsGenre
{
    public string Genre { get; init; } = "";
    public double Minutes { get; init; }
}

public sealed class StatsFirstPlay
{
    public MusicTrack Track { get; init; } = new();
    public DateTimeOffset? PlayedAt { get; init; }
}

public sealed class MusicStats
{
    private List<StatsArtist> _artists = new();
    private List<StatsAlbum> _albums = new();
    private List<StatsTrack> _tracks = new();
    private List<StatsGenre> _genres = new();
    private List<double> _byHour = new();
    private List<double> _byWeekday = new();

    public double Minutes { get; init; }
    public int Plays { get; init; }
    public int Skips { get; init; }
    public int DistinctTracks { get; init; }
    public int DistinctArtists { get; init; }
    public int NewArtists { get; init; }
    public List<StatsArtist> TopArtists { get => _artists; init => _artists = value ?? new(); }
    public List<StatsAlbum> TopAlbums { get => _albums; init => _albums = value ?? new(); }
    public List<StatsTrack> TopTracks { get => _tracks; init => _tracks = value ?? new(); }
    public List<StatsGenre> TopGenres { get => _genres; init => _genres = value ?? new(); }
    public List<double> ByHour { get => _byHour; init => _byHour = value ?? new(); }
    public List<double> ByWeekday { get => _byWeekday; init => _byWeekday = value ?? new(); }
    public int LongestStreakDays { get; init; }
    public StatsFirstPlay? FirstPlay { get; init; }
}

public sealed class StatsShow
{
    public Podcast Podcast { get; init; } = new();
    public double Minutes { get; init; }
    public int Episodes { get; init; }
}

public sealed class PodcastStats
{
    private List<StatsShow> _shows = new();
    private List<double> _byHour = new();
    private List<double> _byWeekday = new();

    public double Minutes { get; init; }
    public int EpisodesFinished { get; init; }
    public double TimeSavedMinutes { get; init; }
    public List<StatsShow> TopShows { get => _shows; init => _shows = value ?? new(); }
    public List<double> ByHour { get => _byHour; init => _byHour = value ?? new(); }
    public List<double> ByWeekday { get => _byWeekday; init => _byWeekday = value ?? new(); }
}

public sealed class ListeningStats
{
    public string Period { get; init; } = "";
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public MusicStats? Music { get; init; }
    public PodcastStats? Podcasts { get; init; }
}

public sealed class ListenBrainzStatus
{
    public bool Connected { get; init; }
    public string? Username { get; init; }
}

public sealed class ListenBrainzTokenInput
{
    public string Token { get; init; } = "";
}

internal sealed class TrackIdBody
{
    public string TrackId { get; init; } = "";
}

internal sealed class PlaylistTracksInput
{
    public List<string> TrackIds { get; init; } = new();
    public int? Position { get; init; }
}

internal sealed class PlaylistOrderInput
{
    public List<string> EntryIds { get; init; } = new();
}
