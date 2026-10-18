using System.Text.RegularExpressions;

namespace SentinelCore.Client.Models;

/// <summary>
/// The words of "Import a list", the same as the web dashboard's media-import.js, kept here so they
/// are unit tested rather than buried in the WPF views.
/// </summary>
public static class MediaImportText
{
    public static string Thing(MediaImportKind kind) => kind == MediaImportKind.Music ? "album" : "movie";
    public static string Things(MediaImportKind kind) => kind == MediaImportKind.Music ? "albums" : "movies";
    public static string Tab(MediaImportKind kind) => kind == MediaImportKind.Music ? "Music" : "Movies";

    public static string Plural(int n, string word, string? words = null) => $"{n} {(n == 1 ? word : words ?? word + "s")}";

    public static string CountOf(MediaImportKind kind, int n) => Plural(n, Thing(kind), Things(kind));

    /// <summary>An item's state as people read it; a download's percentage while it is downloading.</summary>
    public static string StateWord(MediaImportItem item)
    {
        if (item.State == "downloading" && item.Progress is { } progress) return $"{Math.Round(progress * 100, MidpointRounding.AwayFromZero).ToString(System.Globalization.CultureInfo.InvariantCulture)}%";
        return item.State switch
        {
            "waiting" => "waiting",
            "searching" => "searching…",
            "queued" => "queued",
            "downloading" => "downloading",
            "seeding" => "sharing back",
            "importing" => "adding to library",
            "done" => "in the library",
            "inLibrary" => "already had it",
            "notFound" => "not found",
            "failed" => "failed",
            "canceled" => "left out",
            null or "" => "",
            var other => other,
        };
    }

    /// <summary>The help above the file picker, one line per bullet.</summary>
    public static IReadOnlyList<string> Help(MediaImportKind kind) => kind == MediaImportKind.Music
        ? new[]
        {
            "Spotify, quickest: open exportify.net, sign in with Spotify, and press Export next to Liked Songs or any playlist. Choose the .csv here.",
            "Spotify, everything: Spotify → Account → Privacy settings → Download your data (takes a few days). Choose YourLibrary.json and the Playlist*.json files here.",
            "Also reads TuneMyMusic and Soundiiz exports, or any spreadsheet saved as CSV with Artist, Album and Track columns.",
        }
        : new[]
        {
            "Letterboxd: Settings → Data → Export your data; choose watchlist.csv (or watched.csv) here.",
            "IMDb: open your Watchlist or a list → ⋯ → Export.",
            "Also reads Trakt JSON, or any CSV with Title and Year columns. TV shows are left out: add those with + Add so you can pick seasons.",
        };

    /// <summary>The paragraph under the help, or null.</summary>
    public static string? HelpNote(MediaImportKind kind) => kind == MediaImportKind.Music
        ? "Torrent sites share whole albums, not songs, so songs are grouped into the albums they come from and each album is downloaded once."
        : null;

    /// <summary>What the list is called until it is renamed: the file's name, or "Music import" for several.</summary>
    public static string DefaultName(MediaImportKind kind, IReadOnlyList<string> fileNames)
    {
        if (fileNames.Count != 1) return $"{Tab(kind)} import";
        // Either separator, so a Windows path reads the same whichever OS the tests run on.
        var name = fileNames[0].Split('/', '\\')[^1];
        name = Regex.Replace(name, @"\.(csv|json|txt|tsv)$", "", RegexOptions.IgnoreCase);
        return Regex.Replace(name, "_+", " ");
    }

    /// <summary>"From Exportify · 312 songs → 40 albums · 3 already in the library".</summary>
    public static IReadOnlyList<string> Facts(MediaImportKind kind, MediaImportPreview preview)
    {
        var items = preview.Choices(kind);
        var have = items.Count(i => i.InLibrary);
        var facts = new List<string>();
        if (preview.Sources.Count > 0) facts.Add($"From {string.Join(" + ", preview.Sources)}");
        facts.Add(kind == MediaImportKind.Music
            ? $"{Plural(preview.Tracks ?? 0, "song")} → {Plural(items.Count, "album")}"
            : Plural(items.Count, "movie"));
        if (have > 0) facts.Add($"{have} already in the library");
        return facts;
    }

    /// <summary>The amber notes: what was left out, files that could not be used, singles.</summary>
    public static IReadOnlyList<string> Notes(MediaImportKind kind, MediaImportPreview preview)
    {
        var notes = new List<string>();
        if (preview.NoAlbum is > 0 and var noAlbum)
            notes.Add($"{Plural(noAlbum, "song")} had no album, so {(noAlbum == 1 ? "it was" : "they were")} left out.");
        if (preview.Shows is > 0 and var shows)
            notes.Add($"{Plural(shows, "TV show")} left out: add {(shows == 1 ? "it" : "them")} with + Add to pick seasons.");
        if (preview.Unreadable is > 0 and var unreadable)
            notes.Add($"{unreadable} row{(unreadable == 1 ? "" : "s")} could not be read (podcast episodes, local files, blank titles).");
        notes.AddRange(preview.Problems.Where(p => !string.IsNullOrWhiteSpace(p)));
        var singles = preview.Choices(kind).Count(i => i.Single && !i.InLibrary);
        if (singles > 0)
            notes.Add($"{Plural(singles, "single")}: one-song releases are rarely shared on their own, so expect some of these to come back not found.");
        return notes;
    }

    /// <summary>The start button: "download 12 albums".</summary>
    public static string StartLabel(MediaImportKind kind, int chosen) => $"download {CountOf(kind, chosen)}";

    /// <summary>The line after starting.</summary>
    public static string Started(MediaImportKind kind, int chosen) =>
        $"Working through {CountOf(kind, chosen)}. Watch it under Downloads.";

    /// <summary>paused | finished | working</summary>
    public static string Status(MediaImportJob job) => job.IsPaused ? "paused" : job.IsFinished ? "finished" : "working";

    /// <summary>"12 downloaded · 2 downloading · 30 to search · 1 not found".</summary>
    public static IReadOnlyList<string> Progress(MediaImportCounts c)
    {
        var bits = new List<string>();
        if (c.Done > 0) bits.Add($"{c.Done} downloaded");
        if (c.Downloading > 0) bits.Add($"{c.Downloading} downloading");
        if (c.Waiting > 0) bits.Add($"{c.Waiting} to search");
        if (c.InLibrary > 0) bits.Add($"{c.InLibrary} already had");
        if (c.NotFound > 0) bits.Add($"{c.NotFound} not found");
        if (c.Failed > 0) bits.Add($"{c.Failed} failed");
        return bits;
    }

    /// <summary>The detail view's filters: key, label, and which item states they keep.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> Filters = new[]
    {
        ("all", "all"), ("active", "in progress"), ("done", "done"), ("missing", "not found"),
    };

    public static bool InFilter(string filter, string? state) => filter switch
    {
        "active" => state is "waiting" or "searching" or "queued" or "downloading" or "seeding" or "importing",
        "done" => state is "done" or "inLibrary",
        "missing" => state is "notFound" or "failed",
        _ => true,
    };

    public const string RemoveQuestion =
        "Remove this list? Nothing more on it is searched for. Downloads it already started carry on under Downloads.";

    public const string Explainer =
        "The server searches your sites for each one through the VPN, picks the best release with people sharing it, and keeps a few downloading at a time. You can close this window; it carries on, and the list shows under Downloads.";

    public static string EmptyLine(MediaImportKind kind) =>
        $"Got a {(kind == MediaImportKind.Movies ? "Letterboxd or IMDb" : "Spotify")} list? Import a list downloads everything on it.";
}
