using SentinelCore.Client.Models;

namespace SentinelCore.Client.Audio;

public enum RepeatMode
{
    Off,
    All,
    One,
}

/// <summary>
/// One thing in the player's queue: a track or an episode, with what the screens need to show it
/// without a round trip.
/// </summary>
public sealed class QueueEntry
{
    private static long _next;

    public QueueEntry(MusicTrack track)
    {
        Track = track;
        Type = "track";
        Id = track.Id;
        Title = track.Title;
        Subtitle = track.Subtitle;
        ArtUrl = track.ArtUrl ?? (track.AlbumId is { Length: > 0 } album ? $"/api/music/albums/{album}/art" : null);
        DurationSeconds = track.DurationSeconds;
    }

    public QueueEntry(PodcastEpisode episode)
    {
        Episode = episode;
        Type = "episode";
        Id = episode.Id;
        Title = episode.Title;
        Subtitle = episode.PodcastTitle;
        ArtUrl = episode.ArtUrl ?? $"/api/podcasts/episodes/{episode.Id}/art";
        DurationSeconds = episode.DurationSeconds;
    }

    /// <summary>Unique per entry, so the same song twice in a queue can be told apart when reordering.</summary>
    public long Uid { get; } = Interlocked.Increment(ref _next);

    public string Type { get; }
    public string Id { get; }
    public string Title { get; }
    public string? Subtitle { get; }
    public string? ArtUrl { get; }
    public double DurationSeconds { get; }
    public MusicTrack? Track { get; }
    public PodcastEpisode? Episode { get; }

    public bool IsEpisode => Episode is not null;

    public AudioQueueRef ToRef() => new() { Type = Type, Id = Id };
}

/// <summary>
/// The player's queue as a state machine: what is playing, what comes next, shuffle, repeat
/// off/all/one, Up Next insertion and reordering. Nothing here touches audio or the network - the
/// player asks this what to play and tells it what happened.
/// </summary>
/// <remarks>
/// Shuffle keeps the original order alongside the shuffled one, so turning it off goes back to the
/// album's own order from wherever the current song is - the behaviour every music app has. The
/// current entry never moves when shuffle changes: what is playing keeps playing.
/// </remarks>
public sealed class PlayQueue
{
    private readonly Random _random;
    private List<QueueEntry> _order = new();      // what plays, in play order
    private List<QueueEntry> _original = new();   // the order it was given in, for un-shuffling

    public PlayQueue(Random? random = null) => _random = random ?? new Random();

    public IReadOnlyList<QueueEntry> Entries => _order;
    public int Index { get; private set; } = -1;
    public bool Shuffle { get; private set; }
    public RepeatMode Repeat { get; set; } = RepeatMode.Off;
    public AudioContext? Context { get; private set; }

    public QueueEntry? Current => Index >= 0 && Index < _order.Count ? _order[Index] : null;
    public bool IsEmpty => _order.Count == 0;

    /// <summary>What comes after the current entry, in play order.</summary>
    public IEnumerable<QueueEntry> Upcoming => Index < 0 ? _order : _order.Skip(Index + 1);

    /// <summary>Raised after anything about the queue changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// Whether the queue is an album playing in the album's own order: the case where Sound Check
    /// uses album gain, so the album's own loud-and-quiet survives.
    /// </summary>
    public bool IsAlbumInOrder => !Shuffle && Context?.Type == "album";

    /// <summary>Replaces the queue. With shuffle, <paramref name="startIndex"/> (when given) still plays first.</summary>
    public void Load(IEnumerable<QueueEntry> entries, int startIndex = 0, bool shuffle = false, AudioContext? context = null)
    {
        _original = entries.ToList();
        _order = _original.ToList();
        Context = context;
        Shuffle = false;
        Index = _order.Count == 0 ? -1 : Math.Clamp(startIndex, 0, _order.Count - 1);
        if (shuffle && _order.Count > 0)
        {
            // "Shuffle" from a list with no particular start picks a random first song.
            if (startIndex < 0) Index = _random.Next(_order.Count);
            ApplyShuffle();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        _order.Clear();
        _original.Clear();
        Index = -1;
        Context = null;
        Changed?.Invoke();
    }

    public void SetShuffle(bool shuffle)
    {
        if (shuffle == Shuffle) return;
        if (shuffle)
        {
            ApplyShuffle();
        }
        else
        {
            var current = Current;
            // Anything added while shuffled that the original doesn't know about stays, at the end.
            var extra = _order.Where(entry => !_original.Contains(entry)).ToList();
            _order = _original.Where(entry => _order.Contains(entry)).Concat(extra).ToList();
            Index = current is null ? (_order.Count > 0 ? 0 : -1) : _order.IndexOf(current);
            Shuffle = false;
        }
        Changed?.Invoke();
    }

    // The current entry goes first; everything else is Fisher-Yates shuffled behind it.
    private void ApplyShuffle()
    {
        var current = Current;
        var rest = _order.Where(entry => !ReferenceEquals(entry, current)).ToList();
        for (var i = rest.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (rest[i], rest[j]) = (rest[j], rest[i]);
        }
        _order = current is null ? rest : new[] { current }.Concat(rest).ToList();
        Index = _order.Count == 0 ? -1 : 0;
        Shuffle = true;
    }

    public RepeatMode CycleRepeat()
    {
        Repeat = Repeat switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off,
        };
        Changed?.Invoke();
        return Repeat;
    }

    /// <summary>
    /// Moves on. <paramref name="automatic"/> is the current one ending by itself, which is the only
    /// time Repeat One repeats - pressing Next always goes to the next song. Returns the entry to
    /// play, or null when the queue has run out (repeat off at the end).
    /// </summary>
    public QueueEntry? Next(bool automatic)
    {
        if (_order.Count == 0) return null;
        if (automatic && Repeat == RepeatMode.One) return Current;
        if (Index + 1 < _order.Count)
        {
            Index++;
        }
        else if (Repeat != RepeatMode.Off)
        {
            Index = 0;
        }
        else
        {
            return null;
        }
        Changed?.Invoke();
        return Current;
    }

    /// <summary>
    /// Previous: more than three seconds in restarts the current one (returns it with
    /// restart = true); otherwise goes back one, wrapping only with Repeat All.
    /// </summary>
    public (QueueEntry? Entry, bool Restart) Previous(double positionSeconds)
    {
        if (_order.Count == 0) return (null, false);
        if (positionSeconds > 3 || (Index == 0 && Repeat != RepeatMode.All)) return (Current, true);
        Index = Index > 0 ? Index - 1 : _order.Count - 1;
        Changed?.Invoke();
        return (Current, false);
    }

    public QueueEntry? JumpTo(int index)
    {
        if (index < 0 || index >= _order.Count) return null;
        Index = index;
        Changed?.Invoke();
        return Current;
    }

    /// <summary>Puts entries straight after the current one ("Play next").</summary>
    public void PlayNext(IEnumerable<QueueEntry> entries)
    {
        var list = entries.ToList();
        if (list.Count == 0) return;
        var at = Index < 0 ? 0 : Index + 1;
        _order.InsertRange(at, list);
        InsertIntoOriginalAfterCurrent(list);
        if (Index < 0) Index = 0;
        Changed?.Invoke();
    }

    /// <summary>Puts entries at the end ("Add to queue").</summary>
    public void AddLast(IEnumerable<QueueEntry> entries)
    {
        var list = entries.ToList();
        if (list.Count == 0) return;
        _order.AddRange(list);
        _original.AddRange(list);
        if (Index < 0) Index = 0;
        Changed?.Invoke();
    }

    private void InsertIntoOriginalAfterCurrent(List<QueueEntry> list)
    {
        var current = Current;
        var at = current is null ? 0 : _original.IndexOf(current) + 1;
        if (at <= 0 || at > _original.Count) at = _original.Count;
        _original.InsertRange(at, list);
    }

    /// <summary>Moves an entry within the play order; the current one stays current wherever it lands.</summary>
    public void Move(int from, int to)
    {
        if (from < 0 || from >= _order.Count || to < 0 || to >= _order.Count || from == to) return;
        var current = Current;
        var entry = _order[from];
        _order.RemoveAt(from);
        _order.Insert(to, entry);
        if (current is not null) Index = _order.IndexOf(current);
        if (!Shuffle) _original = _order.ToList();
        Changed?.Invoke();
    }

    /// <summary>
    /// Removes an entry. Removing the current one makes the next one current (returned so the
    /// player can start it); removing anything else returns null.
    /// </summary>
    public QueueEntry? RemoveAt(int index)
    {
        if (index < 0 || index >= _order.Count) return null;
        var entry = _order[index];
        var wasCurrent = index == Index;
        _order.RemoveAt(index);
        _original.Remove(entry);
        if (_order.Count == 0)
        {
            Index = -1;
        }
        else if (index < Index)
        {
            Index--;
        }
        else if (wasCurrent && Index >= _order.Count)
        {
            Index = Repeat == RepeatMode.All ? 0 : _order.Count - 1;
        }
        Changed?.Invoke();
        return wasCurrent ? Current : null;
    }

    /// <summary>The queue as §7's NowPlaying wants it: at most 500 entries around the current one.</summary>
    public (List<AudioQueueRef> Queue, int Index) ToWire(int max = 500)
    {
        if (_order.Count <= max) return (_order.Select(entry => entry.ToRef()).ToList(), Math.Max(0, Index));
        var start = Math.Clamp(Index - max / 2, 0, _order.Count - max);
        return (_order.Skip(start).Take(max).Select(entry => entry.ToRef()).ToList(), Math.Max(0, Index - start));
    }

    public static string RepeatWire(RepeatMode mode) => mode switch
    {
        RepeatMode.All => "all",
        RepeatMode.One => "one",
        _ => "off",
    };

    public static RepeatMode ParseRepeat(string? value) => value switch
    {
        "all" => RepeatMode.All,
        "one" => RepeatMode.One,
        _ => RepeatMode.Off,
    };
}
