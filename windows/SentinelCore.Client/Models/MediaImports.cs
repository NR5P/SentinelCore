using System.Text.Json.Serialization;

namespace SentinelCore.Client.Models;

// "Import a list" on Music (/api/music/downloads/...) and Movies (/api/media/downloads/...): pick a
// CSV/JSON exported from Spotify, Letterboxd, IMDb and the like, check what the server found in it,
// and the server works through the chosen albums or films in the background. The contract is
// core/docs/MOVIE_DOWNLOADS.md "Import a list (Music and Movies)"; the web dashboard's version is
// core/SentinelCore.Web/wwwroot/media-import.js. Like the rest of the downloads models every field
// is nullable and every list survives an explicit null.

/// <summary>Which tab's lists: the two share one shape and differ only in their base route and words.</summary>
public enum MediaImportKind
{
    Music,
    Movies,
}

/// <summary>One file as the client read it, for POST {base}/import/preview.</summary>
public sealed class MediaImportFile
{
    public string Name { get; init; } = "";
    public string Text { get; init; } = "";
}

/// <summary>The body of POST {base}/import/preview.</summary>
public sealed class MediaImportPreviewInput
{
    public List<MediaImportFile> Files { get; init; } = new();
}

/// <summary>What the server found in the files. Nothing is saved yet.</summary>
public sealed class MediaImportPreview
{
    private List<string> _sources = new();
    private List<string> _problems = new();
    private List<MediaImportAlbum> _albums = new();
    private List<MediaImportMovie> _movies = new();

    /// <summary>music | movies</summary>
    public string? Kind { get; init; }

    /// <summary>The exports recognised ("Exportify", "Letterboxd").</summary>
    public List<string> Sources { get => _sources; init => _sources = value ?? new(); }

    /// <summary>Songs read (music only).</summary>
    public int? Tracks { get; init; }

    /// <summary>Songs with no album, left out.</summary>
    public int? NoAlbum { get; init; }

    /// <summary>TV shows left out (movies only): they need seasons picked, so they go through "+ Add".</summary>
    public int? Shows { get; init; }

    /// <summary>Rows that could not be read (podcast episodes, local files, blank titles).</summary>
    public int? Unreadable { get; init; }

    /// <summary>A sentence per file the server could not use.</summary>
    public List<string> Problems { get => _problems; init => _problems = value ?? new(); }
    public List<MediaImportAlbum> Albums { get => _albums; init => _albums = value ?? new(); }
    public List<MediaImportMovie> Movies { get => _movies; init => _movies = value ?? new(); }

    /// <summary>The checklist rows for this kind, in the server's order.</summary>
    public List<MediaImportChoice> Choices(MediaImportKind kind) => kind == MediaImportKind.Music
        ? Albums.Select(a => new MediaImportChoice(a.Artist, a.Title ?? "", a.Year, a.TrackCount ?? 0, a.Single == true, a.InLibrary == true)).ToList()
        : Movies.Select(m => new MediaImportChoice(null, m.Title ?? "", m.Year, 0, false, m.InLibrary == true)).ToList();
}

public sealed class MediaImportAlbum
{
    private List<string> _tracks = new();

    public string? Artist { get; init; }
    public string? Title { get; init; }
    public int? Year { get; init; }

    /// <summary>Songs of this album on the list.</summary>
    public int? TrackCount { get; init; }
    public List<string> Tracks { get => _tracks; init => _tracks = value ?? new(); }

    /// <summary>A one-song release named after the song: rarely shared on its own.</summary>
    public bool? Single { get; init; }
    public bool? InLibrary { get; init; }
}

public sealed class MediaImportMovie
{
    public string? Title { get; init; }
    public int? Year { get; init; }
    public string? ImdbId { get; init; }
    public bool? InLibrary { get; init; }
}

/// <summary>One row of the preview's checklist: an album or a film, the same for both kinds.</summary>
public sealed record MediaImportChoice(string? Artist, string Title, int? Year, int TrackCount, bool Single, bool InLibrary)
{
    /// <summary>What tells two rows apart (the web uses the same pair).</summary>
    public string Key => Artist is null ? $"{Title}\u0001{Year}" : $"{Artist}\u0001{Title}";

    /// <summary>Whether a filter box's text matches it: artist, title or year, any case.</summary>
    public bool Matches(string? filter)
    {
        var q = filter?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return $"{Artist} {Title} {Year}".Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    public MediaImportStartItem ToStartItem() => new() { Artist = Artist, Title = Title, Year = Year, TrackCount = TrackCount };
}

/// <summary>The body of POST {base}/imports.</summary>
public sealed class MediaImportStartInput
{
    public string Name { get; init; } = "";
    public string? LibraryId { get; init; }
    public List<MediaImportStartItem> Items { get; init; } = new();
}

public sealed class MediaImportStartItem
{
    public string? Artist { get; init; }
    public string Title { get; init; } = "";
    public int? Year { get; init; }
    public int TrackCount { get; init; }
}

/// <summary>The body of POST {base}/imports/{id}/pause.</summary>
public sealed class MediaImportPauseInput
{
    public bool Paused { get; init; }
}

/// <summary>GET {base}/imports: newest first, without their items.</summary>
public sealed class MediaImportList
{
    private List<MediaImportJob> _imports = new();

    public List<MediaImportJob> Imports { get => _imports; init => _imports = value ?? new(); }
}

/// <summary>An imported list the server is working through.</summary>
public sealed class MediaImportJob
{
    private List<MediaImportItem> _items = new();

    public string? Id { get; init; }

    /// <summary>music | movies</summary>
    public string? Kind { get; init; }
    public string? Name { get; init; }
    public string? RequestedBy { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public bool? Paused { get; init; }

    /// <summary>Why it is waiting ("Waiting to try again: …"), or null.</summary>
    public string? Message { get; init; }
    public bool? Finished { get; init; }
    public MediaImportCounts? Counts { get; init; }

    /// <summary>Empty in the list; filled by GET {base}/imports/{id} and every change.</summary>
    public List<MediaImportItem> Items { get => _items; init => _items = value ?? new(); }

    [JsonIgnore] public bool IsPaused => Paused == true;
    [JsonIgnore] public bool IsFinished => Finished == true;
    [JsonIgnore] public MediaImportCounts CountsOrEmpty => Counts ?? new MediaImportCounts();

    /// <summary>Pause/resume belongs on a list still working, or one that is paused.</summary>
    [JsonIgnore] public bool CanPause => !IsFinished || IsPaused;

    /// <summary>"search missing again" belongs on a list with something not found or failed.</summary>
    [JsonIgnore] public bool CanRetry => CountsOrEmpty.NotFound + CountsOrEmpty.Failed > 0;

    /// <summary>Working and not paused: worth asking again soon.</summary>
    [JsonIgnore] public bool IsBusy => !IsFinished && !IsPaused;
}

public sealed class MediaImportCounts
{
    public int Total { get; init; }
    public int Waiting { get; init; }
    public int Downloading { get; init; }
    public int Done { get; init; }
    public int InLibrary { get; init; }
    public int NotFound { get; init; }
    public int Failed { get; init; }
    public int Canceled { get; init; }

    /// <summary>Everything that will not change any more.</summary>
    [JsonIgnore] public int Settled => Done + InLibrary + NotFound + Failed + Canceled;

    /// <summary>0-1 for the progress bar; an empty list counts as done.</summary>
    [JsonIgnore] public double Fraction => Total <= 0 ? 1 : Math.Clamp((double)Settled / Total, 0, 1);
}

public sealed class MediaImportItem
{
    public string? Id { get; init; }
    public string? Artist { get; init; }
    public string? Title { get; init; }
    public int? Year { get; init; }
    public int? TrackCount { get; init; }

    /// <summary>waiting | searching | queued | downloading | seeding | importing | done | inLibrary | notFound | failed | canceled</summary>
    public string? State { get; init; }
    public string? Message { get; init; }

    /// <summary>0-1 while downloading, else null.</summary>
    public double? Progress { get; init; }
    public string? DownloadId { get; init; }
    public string? ReleaseTitle { get; init; }

    /// <summary>The server lets one be left out only while nothing is downloading for it.</summary>
    [JsonIgnore] public bool CanLeaveOut => State is "waiting" or "notFound" or "failed";
}
