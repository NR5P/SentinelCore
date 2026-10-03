using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SentinelCore.App.Audio;
using SentinelCore.Client;
using SentinelCore.Client.Audio;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views.Audio;

/// <summary>Opening Music and Podcasts screens on top of the current section.</summary>
public static class AudioNav
{
    public static void OpenAlbum(AppModel model, INavigator navigator, string id, string title) =>
        navigator.Push(new AlbumView(model, navigator, id), title);

    public static void OpenArtist(AppModel model, INavigator navigator, string id, string name) =>
        navigator.Push(new ArtistView(model, navigator, id), name);

    public static void OpenPlaylist(AppModel model, INavigator navigator, string id, string name) =>
        navigator.Push(new PlaylistView(model, navigator, id), name);

    public static void OpenShow(AppModel model, INavigator navigator, string id, string title) =>
        navigator.Push(new PodcastShowView(model, navigator, id), title);

    public static void OpenEpisode(AppModel model, INavigator navigator, string id, string title) =>
        navigator.Push(new PodcastEpisodeView(model, navigator, id), title);

    /// <summary>Plays an album from a card's menu, without opening it.</summary>
    public static async Task PlayAlbumAsync(AppModel model, string id, bool shuffle)
    {
        try
        {
            var album = await model.Client.MusicAlbumAsync(id);
            await PlayerController.Shared!.PlayTracksAsync(album.Tracks, shuffle ? -1 : 0, shuffle,
                new AudioContext { Type = "album", Id = album.Id, Name = album.Title });
        }
        catch (Exception error)
        {
            model.Log($"Could not play the album: {error.Message}", DiagnosticLevel.Notice);
        }
    }
}

/// <summary>The big header every album, artist and playlist page has: artwork, title, facts, actions.</summary>
internal static class DetailHeader
{
    public static Grid Build(FrameworkElement art, string kind, string title, string? line1, string? line2, params UIElement[] actions)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(art);
        var text = new StackPanel { Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
        text.Children.Add(new TextBlock { Text = kind.ToUpperInvariant(), Style = AudioUi.StyleOf("GroupHeading"), Margin = new Thickness(0, 0, 0, 4) });
        text.Children.Add(AudioUi.Title(title, 26));
        if (!string.IsNullOrWhiteSpace(line1)) text.Children.Add(new TextBlock { Text = line1, Foreground = Palette.PrimaryText, FontSize = 14, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(line2)) text.Children.Add(AudioUi.Caption(line2, 4));
        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var action in actions)
        {
            if (action is FrameworkElement element) element.Margin = new Thickness(0, 0, 8, 6);
            row.Children.Add(action);
        }
        text.Children.Add(row);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }
}

/// <summary>One album: its tracks by disc, love and rating, play/shuffle/radio, and more by the artist.</summary>
public sealed class AlbumView : AudioPage
{
    private readonly string _id;

    public AlbumView(AppModel model, INavigator navigator, string id) : base(model, navigator)
    {
        _id = id;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Loading();
        try
        {
            Render(await Model.Client.MusicAlbumAsync(_id));
        }
        catch (Exception error)
        {
            Host.Children.Clear();
            Fail(error);
        }
    }

    private void Render(MusicAlbumDetail album)
    {
        Host.Children.Clear();
        var context = new AudioContext { Type = "album", Id = album.Id, Name = album.Title };
        var facts = new List<string>();
        if (album.Year is { } year) facts.Add(year.ToString());
        if (album.Genres.Count > 0) facts.Add(string.Join(", ", album.Genres.Take(3)));
        facts.Add(AudioUi.Count(album.Tracks.Count, "song", "songs"));
        facts.Add(AudioTime.Length(album.DurationSeconds > 0 ? album.DurationSeconds : album.Tracks.Sum(t => t.DurationSeconds)));
        if (album.Formats.Count > 0) facts.Add(string.Join(", ", album.Formats));

        var artist = new Button { Content = album.Artist ?? "Unknown artist", Style = AudioUi.StyleOf("LinkButton"), FontSize = 14 };
        artist.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(album.ArtistId)) AudioNav.OpenArtist(Model, Navigator, album.ArtistId!, album.Artist ?? "");
        };

        Host.Children.Add(DetailHeader.Build(
            AudioUi.Art(album.ArtUrl, 220), album.Compilation ? "Compilation" : "Album", album.Title, null, string.Join(" · ", facts),
            artist,
            AudioUi.TextButton("▶  Play", () => _ = Player.PlayTracksAsync(album.Tracks, 0, false, context), primary: true),
            AudioUi.TextButton("Shuffle", () => _ = Player.PlayTracksAsync(album.Tracks, -1, true, context)),
            AudioUi.TextButton("Radio", () => _ = Player.StartRadioAsync("album", album.Id, album.Title), "Endless music like this album"),
            AudioUi.TextButton("Play next", () => Player.PlayNext(album.Tracks.Select(t => new QueueEntry(t)))),
            AudioUi.TextButton("Add to queue", () => Player.AddToQueue(album.Tracks.Select(t => new QueueEntry(t)))),
            AudioUi.TextButton("Add to playlist…", () => _ = AudioUi.AddToPlaylistAsync(album.Tracks.Select(t => t.Id).ToList())),
            AudioUi.LoveButton(album.Loved, loved => AudioUi.SetLovedAsync("albums", album.Id, loved, value => album.Loved = value)),
            AudioUi.Stars(album.Rating, rating => AudioUi.RateAsync("albums", album.Id, rating, value => album.Rating = value))));

        var discs = album.Tracks.GroupBy(track => track.DiscNumber ?? 1).OrderBy(group => group.Key).ToList();
        for (var i = 0; i < album.Tracks.Count; i++)
        {
            var track = album.Tracks[i];
            var index = i;
            if (discs.Count > 1 && (i == 0 || album.Tracks[i - 1].DiscNumber != track.DiscNumber))
            {
                Host.Children.Add(AudioUi.Heading($"Disc {track.DiscNumber ?? 1}", i == 0 ? 0 : 14));
            }
            Host.Children.Add(AudioUi.TrackRow(track, track.TrackNumber?.ToString() ?? (i + 1).ToString(),
                () => _ = Player.PlayTracksAsync(album.Tracks, index, false, context),
                AudioUi.TrackMenu(track, Navigator), showAlbum: false, current: Player.Current?.Id == track.Id));
        }

        if (album.TotalBytes is { } bytes && bytes > 0)
        {
            Host.Children.Add(AudioUi.Caption($"{Design.ByteSizeConverter.FormatBytes(bytes)} on the server", 10));
        }

        var more = AudioUi.Shelf($"More by {album.Artist}", album.MoreByArtist.Select(other => (UIElement)AudioUi.Card(
            AudioUi.Art(other.ArtUrl, 140), other.Title, other.Year?.ToString(), () => AudioNav.OpenAlbum(Model, Navigator, other.Id, other.Title), 140)), Scroller);
        if (more is not null) Host.Children.Add(more);
    }
}

/// <summary>One artist: top songs, albums, appears on, similar artists, radio.</summary>
public sealed class ArtistView : AudioPage
{
    private readonly string _id;

    public ArtistView(AppModel model, INavigator navigator, string id) : base(model, navigator)
    {
        _id = id;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Loading();
        try
        {
            Render(await Model.Client.MusicArtistAsync(_id));
        }
        catch (Exception error)
        {
            Host.Children.Clear();
            Fail(error);
        }
    }

    private void Render(MusicArtistDetail artist)
    {
        Host.Children.Clear();
        var facts = $"{AudioUi.Count(artist.AlbumCount, "album", "albums")} · {AudioUi.Count(artist.TrackCount, "song", "songs")}" +
                    (artist.Genres.Count > 0 ? $" · {string.Join(", ", artist.Genres.Take(3))}" : "");
        var context = new AudioContext { Type = "artist", Id = artist.Id, Name = artist.Name };

        Host.Children.Add(DetailHeader.Build(
            AudioUi.Art(artist.ArtUrl, 200, AudioUi.MusicGlyph, round: true), "Artist", artist.Name, null, facts,
            AudioUi.TextButton("▶  Play top songs", () => _ = Player.PlayTracksAsync(artist.TopTracks, 0, false, context), primary: true),
            AudioUi.TextButton("Shuffle", () => _ = ShuffleArtistAsync(artist)),
            AudioUi.TextButton("Radio", () => _ = Player.StartRadioAsync("artist", artist.Id, artist.Name), "Endless music like this artist"),
            AudioUi.LoveButton(artist.Loved, loved => AudioUi.SetLovedAsync("artists", artist.Id, loved, value => artist.Loved = value))));

        if (artist.TopTracks.Count > 0)
        {
            Host.Children.Add(AudioUi.Heading("Top songs", 0));
            for (var i = 0; i < artist.TopTracks.Count; i++)
            {
                var index = i;
                var track = artist.TopTracks[i];
                Host.Children.Add(AudioUi.TrackRow(track, (i + 1).ToString(), () => _ = Player.PlayTracksAsync(artist.TopTracks, index, false, context),
                    AudioUi.TrackMenu(track, Navigator)));
            }
        }

        UIElement AlbumCard(MusicAlbum album) => AudioUi.Card(AudioUi.Art(album.ArtUrl, 150), album.Title, album.Year?.ToString(),
            () => AudioNav.OpenAlbum(Model, Navigator, album.Id, album.Title), 150);

        if (artist.Albums.Count > 0)
        {
            Host.Children.Add(AudioUi.Heading("Albums"));
            var grid = new WrapPanel();
            foreach (var album in artist.Albums) grid.Children.Add(AlbumCard(album));
            Host.Children.Add(grid);
        }
        var appears = AudioUi.Shelf("Appears on", artist.AppearsOn.Select(AlbumCard), Scroller);
        if (appears is not null) Host.Children.Add(appears);
        var similar = AudioUi.Shelf("Similar artists", artist.Similar.Select(other => (UIElement)AudioUi.Card(
            AudioUi.Art(other.ArtUrl, 130, AudioUi.MusicGlyph, round: true), other.Name, null, () => AudioNav.OpenArtist(Model, Navigator, other.Id, other.Name), 130)), Scroller);
        if (similar is not null) Host.Children.Add(similar);
    }

    private async Task ShuffleArtistAsync(MusicArtistDetail artist) => await Try(async () =>
    {
        var page = await Model.Client.MusicTracksAsync(artistId: artist.Id, limit: 1000);
        await Player.PlayTracksAsync(page.Items, -1, true, new AudioContext { Type = "artist", Id = artist.Id, Name = artist.Name });
    });
}

/// <summary>
/// One playlist: its songs, with remove and drag-free reorder (move up/down), pin, share, edit,
/// duplicate, export, delete. A smart playlist shows its rules instead of being edited by hand.
/// </summary>
public sealed class PlaylistView : AudioPage
{
    private readonly string _id;
    private MusicPlaylist? _playlist;

    public PlaylistView(AppModel model, INavigator navigator, string id) : base(model, navigator)
    {
        _id = id;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Loading();
        try
        {
            _playlist = await Model.Client.MusicPlaylistAsync(_id);
            Render(_playlist);
        }
        catch (Exception error)
        {
            Host.Children.Clear();
            Fail(error);
        }
    }

    private void Render(MusicPlaylist playlist)
    {
        Host.Children.Clear();
        var context = new AudioContext { Type = "playlist", Id = playlist.Id, Name = playlist.Name };
        var who = playlist.Mine ? "Yours" : $"By {playlist.OwnerName}";
        var sharing = playlist.Collaborative ? "collaborative" : playlist.Shared ? "shared with the household" : "private";
        var facts = $"{who} · {sharing} · {AudioUi.Count(playlist.Tracks.Count, "song", "songs")} · {AudioTime.Length(playlist.Tracks.Sum(t => t.DurationSeconds))}";

        var actions = new List<UIElement>
        {
            AudioUi.TextButton("▶  Play", () => _ = Player.PlayTracksAsync(playlist.Tracks, 0, false, context), primary: true),
            AudioUi.TextButton("Shuffle", () => _ = Player.PlayTracksAsync(playlist.Tracks, -1, true, context)),
            AudioUi.TextButton("Radio", () => _ = Player.StartRadioAsync("playlist", playlist.Id, playlist.Name)),
            AudioUi.TextButton(playlist.Pinned ? "Unpin from Home" : "Pin to Home", () => _ = Try(async () =>
            {
                await Model.Client.PinPlaylistAsync(playlist.Id, !playlist.Pinned);
                await LoadAsync();
            })),
        };
        var menu = new ContextMenu();
        if (playlist.Mine || Model.IsAdmin)
        {
            menu.Items.Add(AudioUi.Item("Edit…", () =>
            {
                var editor = new PlaylistEditorWindow(Model, playlist, playlist.Smart) { Owner = Window.GetWindow(this) };
                if (editor.ShowDialog() == true) _ = LoadAsync();
            }));
        }
        menu.Items.Add(AudioUi.Item("Duplicate", () => _ = Try(async () =>
        {
            var copy = await Model.Client.DuplicatePlaylistAsync(playlist.Id);
            AudioNav.OpenPlaylist(Model, Navigator, copy.Id, copy.Name);
        })));
        menu.Items.Add(AudioUi.Item("Export .m3u8…", () => _ = ExportAsync(playlist)));
        if (playlist.Mine || Model.IsAdmin)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(AudioUi.Item("Delete…", () => _ = Try(async () =>
            {
                if (!AudioUi.Confirm("Delete playlist", $"Delete \"{playlist.Name}\"? The songs themselves are not touched.")) return;
                await Model.Client.DeleteMusicPlaylistAsync(playlist.Id);
                Navigator.Pop();
            })));
        }
        var more = AudioUi.TextButton("More ▾", () => { });
        more.Click += (_, _) =>
        {
            menu.PlacementTarget = more;
            menu.IsOpen = true;
        };
        actions.Add(more);

        Host.Children.Add(DetailHeader.Build(AudioUi.Mosaic(playlist.ArtUrls, 220), playlist.Smart ? "Smart playlist" : "Playlist",
            playlist.Name, playlist.Description, facts, actions.ToArray()));

        if (playlist.Smart && playlist.Rules is { } rules)
        {
            Host.Children.Add(AudioUi.Caption(SmartRuleCatalog.Summary(rules), 0));
            Host.Children.Add(AudioUi.Caption("Worked out fresh each time it is opened.", 0));
        }

        for (var i = 0; i < playlist.Tracks.Count; i++)
        {
            var index = i;
            var track = playlist.Tracks[i];
            var extra = new List<object>();
            if (playlist.CanEditTracks && track.EntryId is { } entryId)
            {
                if (i > 0) extra.Add(AudioUi.Item("Move up", () => _ = MoveAsync(playlist, index, index - 1)));
                if (i < playlist.Tracks.Count - 1) extra.Add(AudioUi.Item("Move down", () => _ = MoveAsync(playlist, index, index + 1)));
                extra.Add(AudioUi.Item("Move to top", () => _ = MoveAsync(playlist, index, 0)));
                extra.Add(AudioUi.Item("Remove from this playlist", () => _ = Try(async () =>
                {
                    await Model.Client.RemoveFromPlaylistAsync(playlist.Id, entryId);
                    await LoadAsync();
                })));
            }
            Host.Children.Add(AudioUi.TrackRow(track, (i + 1).ToString(), () => _ = Player.PlayTracksAsync(playlist.Tracks, index, false, context),
                AudioUi.TrackMenu(track, Navigator, extra)));
        }
        if (playlist.Tracks.Count == 0)
        {
            Host.Children.Add(AudioUi.Caption(playlist.Smart
                ? "No songs match these rules right now."
                : "Empty. Add songs from any song's … menu → Add to playlist."));
        }
    }

    private async Task MoveAsync(MusicPlaylist playlist, int from, int to) => await Try(async () =>
    {
        var order = playlist.Tracks.Select(track => track.EntryId!).ToList();
        var entry = order[from];
        order.RemoveAt(from);
        order.Insert(to, entry);
        await Model.Client.ReorderPlaylistAsync(playlist.Id, order);
        await LoadAsync();
    });

    private async Task ExportAsync(MusicPlaylist playlist) => await Try(async () =>
    {
        var dialog = new SaveFileDialog { FileName = $"{playlist.Name}.m3u8", Filter = "Playlist|*.m3u8|All files|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var bytes = await Model.Client.PlaylistM3uAsync(playlist.Id);
        await File.WriteAllBytesAsync(dialog.FileName, bytes);
        Notice($"Saved {Path.GetFileName(dialog.FileName)}.");
    });
}

/// <summary>A list of songs by genre or decade (or loved), with play, shuffle and a radio for the genre.</summary>
public sealed class MusicTrackListView : AudioPage
{
    private readonly string _title;
    private readonly string? _genre;
    private readonly int? _decade;
    private readonly List<MusicTrack> _tracks = new();

    public MusicTrackListView(AppModel model, INavigator navigator, string title, string? genre = null, int? decade = null) : base(model, navigator)
    {
        _title = title;
        _genre = genre;
        _decade = decade;
        Loaded += async (_, _) =>
        {
            if (_tracks.Count == 0) await LoadAsync();
        };
    }

    private AudioContext Context => _genre is not null
        ? new AudioContext { Type = "queue", Id = _genre, Name = _genre }
        : new AudioContext { Type = "queue", Id = _decade?.ToString(), Name = _title };

    private async Task LoadAsync()
    {
        Loading();
        try
        {
            var albums = _genre is not null ? await Model.Client.MusicAlbumsAsync(genre: _genre, sort: "plays", order: "desc", limit: 30) : null;
            var page = await Model.Client.MusicTracksAsync(genre: _genre, decade: _decade, sort: "plays", order: "desc", limit: 500);
            _tracks.Clear();
            _tracks.AddRange(page.Items);
            Host.Children.Clear();
            var actions = new List<UIElement>
            {
                AudioUi.TextButton("▶  Play", () => _ = Player.PlayTracksAsync(_tracks, 0, false, Context), primary: true),
                AudioUi.TextButton("Shuffle", () => _ = Player.PlayTracksAsync(_tracks, -1, true, Context)),
            };
            if (_genre is not null) actions.Add(AudioUi.TextButton("Radio", () => _ = Player.StartRadioAsync("genre", _genre, _genre)));
            if (_decade is { } decade) actions.Add(AudioUi.TextButton("Radio", () => _ = Player.StartRadioAsync("decade", decade.ToString(), _title)));
            Host.Children.Add(DetailHeader.Build(AudioUi.Art(_tracks.FirstOrDefault()?.ArtUrl, 160), _genre is not null ? "Genre" : "Decade", _title, null,
                AudioUi.Count(page.Total, "song", "songs"), actions.ToArray()));
            if (albums is { Items.Count: > 0 })
            {
                var shelf = AudioUi.Shelf("Albums", albums.Items.Select(album => (UIElement)AudioUi.Card(AudioUi.Art(album.ArtUrl, 140), album.Title, album.Artist,
                    () => AudioNav.OpenAlbum(Model, Navigator, album.Id, album.Title), 140)), Scroller);
                if (shelf is not null) Host.Children.Add(shelf);
                Host.Children.Add(AudioUi.Heading("Most played songs"));
            }
            for (var i = 0; i < _tracks.Count; i++)
            {
                var index = i;
                var track = _tracks[i];
                Host.Children.Add(AudioUi.TrackRow(track, null, () => _ = Player.PlayTracksAsync(_tracks, index, false, Context), AudioUi.TrackMenu(track, Navigator), showArt: true));
            }
        }
        catch (Exception error)
        {
            Host.Children.Clear();
            Fail(error);
        }
    }
}
