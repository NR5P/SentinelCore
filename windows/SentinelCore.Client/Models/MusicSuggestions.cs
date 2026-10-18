using System.Text.Json.Serialization;

namespace SentinelCore.Client.Models;

// "Suggested for you" on Music: albums by artists like the ones this person listens to, not in the
// library yet. Behind the same canDownload check as "+ Add". The contract is
// core/docs/MUSIC_AND_PODCASTS.md "### Suggestions"; downloading one is an imported list
// (MediaImports.cs). Every field nullable, every list null-safe, like the other downloads models.

/// <summary>GET /api/music/downloads/suggestions[?refresh=true].</summary>
public sealed class MusicSuggestions
{
    private List<MusicSuggestionSeed> _seeds = new();
    private List<MusicSuggestedAlbum> _albums = new();

    /// <summary>ready | working | error</summary>
    public string? State { get; init; }
    public DateTimeOffset? BuiltAt { get; init; }

    /// <summary>Why the last build failed ("Couldn't reach ListenBrainz…"), or null.</summary>
    public string? Message { get; init; }
    public List<MusicSuggestionSeed> Seeds { get => _seeds; init => _seeds = value ?? new(); }

    /// <summary>At most 40, two per artist. While working or after an error: the last good list.</summary>
    public List<MusicSuggestedAlbum> Albums { get => _albums; init => _albums = value ?? new(); }

    /// <summary>Still building: show what there is with a spinner and ask again in a few seconds.</summary>
    [JsonIgnore] public bool IsWorking => State == "working";
    [JsonIgnore] public bool IsError => State == "error";

    /// <summary>"Based on: Fleetwood Mac, Eagles, Steely Dan…", or null with no seeds.</summary>
    [JsonIgnore] public string? BasedOnLine => MusicSuggestionText.BasedOn(Seeds);
}

public sealed class MusicSuggestionSeed
{
    public string? Artist { get; init; }

    /// <summary>loved | rated | played | playlists | imported | library</summary>
    public string? Reason { get; init; }
}

public sealed class MusicSuggestedAlbum
{
    private List<string> _because = new();

    /// <summary>"steely dan|aja": what tells two apart.</summary>
    public string? Key { get; init; }
    public string? Artist { get; init; }
    public string? Title { get; init; }
    public int? Year { get; init; }

    /// <summary>The MusicBrainz release group.</summary>
    public string? Mbid { get; init; }

    /// <summary>Cover Art Archive; may 404, so keep a placeholder under it.</summary>
    public string? ArtUrl { get; init; }

    /// <summary>The seeds that led here, best first.</summary>
    public List<string> Because { get => _because; init => _because = value ?? new(); }
    public long? Listens { get; init; }

    /// <summary>Already on one of this person's imported lists or downloading. Settable: marked locally after Download.</summary>
    public bool? Queued { get; set; }

    [JsonIgnore] public bool IsQueued => Queued == true;
    [JsonIgnore] public string DisplayKey => Key ?? $"{Artist}|{Title}".ToLowerInvariant();

    /// <summary>"Because you like Fleetwood Mac and Eagles", or null.</summary>
    [JsonIgnore] public string? BecauseLine => MusicSuggestionText.Because(Because);

    /// <summary>The item to send to POST /api/music/downloads/imports.</summary>
    public MediaImportStartItem ToImportItem() => new() { Artist = Artist, Title = Title ?? "", Year = Year };
}

/// <summary>The body of POST /api/music/downloads/suggestions/dismiss.</summary>
public sealed class MusicSuggestionDismissInput
{
    public string Artist { get; init; } = "";

    /// <summary>Null for the whole artist (left out of the body).</summary>
    public string? Title { get; init; }

    /// <summary>False takes a "not interested" back.</summary>
    public bool Dismissed { get; init; } = true;
}

public static class MusicSuggestionText
{
    /// <summary>The imported list's name when suggestions are downloaded.</summary>
    public const string ListName = "Suggested for you";

    public const string Heading = "Suggested for you";

    public const string Empty =
        "Love some songs or albums, or import your Spotify likes, and suggestions will show up here.";

    /// <summary>How many seeds the "Based on" line names before trailing off.</summary>
    public const int SeedsNamed = 5;

    /// <summary>"Because you like A", "… A and B", "… A, B and C".</summary>
    public static string? Because(IReadOnlyList<string> names)
    {
        var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (list.Count == 0) return null;
        return $"Because you like {JoinAnd(list)}";
    }

    /// <summary>"Based on: Fleetwood Mac, Eagles…" (the first five seeds, "…" when there are more).</summary>
    public static string? BasedOn(IReadOnlyList<MusicSuggestionSeed> seeds)
    {
        var names = seeds.Select(s => s.Artist).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList();
        if (names.Count == 0) return null;
        var shown = string.Join(", ", names.Take(SeedsNamed));
        return $"Based on: {shown}{(names.Count > SeedsNamed ? "…" : "")}";
    }

    public static string JoinAnd(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
    };

    /// <summary>"Download all ticked (3)".</summary>
    public static string DownloadTicked(int n) => n == 0 ? "Download all ticked" : $"Download all ticked ({n})";
}
