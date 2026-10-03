using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SentinelCore.App.Audio;
using SentinelCore.Client;
using SentinelCore.Client.Audio;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views.Audio;

/// <summary>
/// The Music section: Home rows, the library (Artists, Albums, Songs, Genres, Playlists, Folders),
/// search, Shuffle all, "+ Add" when the server can download albums, and the extras (stats,
/// alarms, briefing, ListenBrainz). The contract is core/docs/MUSIC_AND_PODCASTS.md §3-§4.
/// </summary>
public sealed class MusicView : AudioPage
{
    private static readonly (string Key, string Label)[] TabList =
    {
        ("home", "Home"), ("artists", "Artists"), ("albums", "Albums"), ("songs", "Songs"),
        ("genres", "Genres"), ("playlists", "Playlists"), ("folders", "Folders"),
    };

    private const int PageSize = 200;

    private readonly TextBox _search = new() { Width = 220, ToolTip = "Search artists, albums, songs and playlists" };
    private readonly ContentControl _tabs = new();
    private readonly TextBlock _status = AudioUi.Caption("Loading the library…");
    private readonly Button _add;
    private readonly Button _downloads;
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private string _tab = "home";
    private string _sort = "";
    private string _order = "asc";
    private bool _filterFlag;
    private int _generation;
    private bool _loaded;
    private MusicDownloadStatus _downloadStatus = MusicDownloadStatus.Off;

    public MusicView(AppModel model, INavigator navigator) : base(model, navigator)
    {
        _add = AudioUi.TextButton("+ Add", OpenAdd, "Find an album and download it into the library", primary: true);
        _add.Visibility = Visibility.Collapsed;
        _downloads = AudioUi.TextButton("Downloads", () => Navigator.Push(new MusicDownloadsView(Model, Navigator), "Album downloads"), "Albums downloading now, and the ones that arrived");
        _downloads.Visibility = Visibility.Collapsed;

        var title = new StackPanel();
        title.Children.Add(new TextBlock { Text = "Music", Style = AudioUi.StyleOf("SectionTitle") });
        title.Children.Add(_status);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(AudioUi.TextButton("Shuffle all", () => _ = Player.ShuffleAllAsync(), "Shuffle the whole library, favouring what you haven't heard lately"));
        actions.Children.Add(_add);
        actions.Children.Add(_downloads);
        actions.Children.Add(MoreButton());
        actions.Children.Add(_search);
        actions.Children.Add(AudioUi.GlyphButton(AudioUi.RefreshGlyph, "Refresh", () => _ = ReloadAsync()));

        var top = new Grid { Margin = new Thickness(20, 16, 20, 8) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(title);
        Grid.SetColumn(actions, 1);
        top.Children.Add(actions);

        var header = new StackPanel();
        header.Children.Add(top);
        _tabs.Margin = new Thickness(20, 0, 20, 4);
        header.Children.Add(_tabs);
        SetHeader(header);
        DrawTabs();

        _search.TextChanged += (_, _) =>
        {
            _searchDelay.Stop();
            _searchDelay.Start();
        };
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            _ = ReloadAsync();
        };

        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            _ = ReloadAsync();
            _ = RefreshDownloadStatusAsync();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && _loaded && _tab == "home" && Query.Length == 0) _ = ReloadAsync();
        };
    }

    private string Query => _search.Text.Trim();

    private Button MoreButton()
    {
        var menu = new ContextMenu();
        menu.Items.Add(AudioUi.Item("Stats & Wrapped", () => Navigator.Push(new AudioStatsView(Model, Navigator), "Listening stats")));
        menu.Items.Add(AudioUi.Item("Wake-up alarms…", () => new WakeAlarmsWindow(Model) { Owner = Window.GetWindow(this) }.ShowDialog()));
        menu.Items.Add(AudioUi.Item("Morning Briefing", () => _ = Player.PlayBriefingAsync()));
        menu.Items.Add(AudioUi.Item("ListenBrainz…", () => new ListenBrainzWindow(Model) { Owner = Window.GetWindow(this) }.ShowDialog()));
        menu.Items.Add(new Separator());
        menu.Items.Add(AudioUi.Item("New playlist…", () => OpenPlaylistEditor(smart: false)));
        menu.Items.Add(AudioUi.Item("New smart playlist…", () => OpenPlaylistEditor(smart: true)));
        if (Model.IsAdmin)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(AudioUi.Item("Music libraries…", () =>
            {
                new MusicLibrariesWindow(Model) { Owner = Window.GetWindow(this) }.ShowDialog();
                _ = ReloadAsync();
            }));
        }
        var button = AudioUi.TextButton("More ▾", () => { }, "Stats, alarms, the briefing and more");
        button.Click += (_, _) =>
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        };
        return button;
    }

    private void DrawTabs() => _tabs.Content = AudioUi.Tabs(TabList, _tab, key =>
    {
        if (key == _tab && Query.Length == 0) return;
        _tab = key;
        _sort = "";
        _order = "asc";
        _filterFlag = false;
        _search.Text = "";
        DrawTabs();
        _ = ReloadAsync();
    });

    private void OpenPlaylistEditor(bool smart)
    {
        var editor = new PlaylistEditorWindow(Model, null, smart) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() == true && editor.Saved is { } saved) AudioNav.OpenPlaylist(Model, Navigator, saved.Id, saved.Name);
    }

    // ---------------------------------------------------------------- + Add

    private async Task RefreshDownloadStatusAsync()
    {
        try
        {
            _downloadStatus = await Model.Client.MusicDownloadStatusAsync();
        }
        catch (Exception)
        {
            _downloadStatus = MusicDownloadStatus.Off;
        }
        var can = _downloadStatus.CanDownload;
        _add.Visibility = can ? Visibility.Visible : Visibility.Collapsed;
        _downloads.Visibility = can ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenAdd()
    {
        var window = new MusicAddWindow(Model, _downloadStatus) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) Navigator.Push(new MusicDownloadsView(Model, Navigator), "Album downloads");
    }

    // ---------------------------------------------------------------- Loading

    private async Task ReloadAsync()
    {
        var generation = ++_generation;
        Notice(null);
        try
        {
            if (Query.Length > 0)
            {
                Loading("Searching…");
                var results = await Model.Client.MusicSearchAsync(Query);
                if (generation == _generation) RenderSearch(results);
                return;
            }
            switch (_tab)
            {
                case "home":
                {
                    Loading();
                    var home = await Model.Client.MusicHomeAsync();
                    if (generation != _generation) return;
                    _status.Text = $"{AudioUi.Count(home.ArtistCount, "artist", "artists")} · {AudioUi.Count(home.AlbumCount, "album", "albums")} · {AudioUi.Count(home.TrackCount, "song", "songs")}";
                    RenderHome(home);
                    _ = ScanLineAsync(generation);
                    break;
                }
                case "artists":
                    await LoadArtistsAsync(generation, 0);
                    break;
                case "albums":
                    await LoadAlbumsAsync(generation, 0);
                    break;
                case "songs":
                    await LoadSongsAsync(generation, 0);
                    break;
                case "genres":
                {
                    Loading();
                    var genres = await Model.Client.MusicGenresAsync();
                    if (generation == _generation) RenderGenres(genres);
                    break;
                }
                case "playlists":
                {
                    Loading();
                    var playlists = await Model.Client.MusicPlaylistsAsync();
                    if (generation == _generation) RenderPlaylists(playlists);
                    break;
                }
                case "folders":
                    Host.Children.Clear();
                    Host.Children.Add(new MusicFolderBrowser(Model, Navigator));
                    break;
            }
        }
        catch (Exception error)
        {
            if (generation != _generation) return;
            Host.Children.Clear();
            Host.Children.Add(AudioUi.Title("Couldn't load the library", 16));
            Host.Children.Add(AudioUi.Caption(AppModel.UserFacingMessage(error), 6));
        }
    }

    private async Task ScanLineAsync(int generation)
    {
        try
        {
            var status = await Model.Client.MusicStatusAsync();
            if (generation != _generation || !status.Scanning) return;
            var scan = status.Scan;
            _status.Text += $" · scanning{(scan?.LibraryName is { Length: > 0 } name ? $" {name}" : "")}{(scan is { Total: > 0 } ? $" ({scan.Processed:N0} of {scan.Total:N0})" : "")}";
        }
        catch (Exception)
        {
            // Only the caption depends on it.
        }
    }

    // ---------------------------------------------------------------- Home

    private void RenderHome(MusicHome home)
    {
        Host.Children.Clear();
        if (home.IsEmpty)
        {
            Host.Children.Add(AudioUi.Title("No music yet", 16));
            Host.Children.Add(AudioUi.Caption(Model.IsAdmin
                ? "Add the folders your music is in under Settings → Server Settings → Music, or More ▾ → Music libraries. They can be on the server or shared by another computer."
                : "An admin adds the music folders in Settings.", 6));
            if (Model.IsAdmin)
            {
                Host.Children.Add(AudioUi.TextButton("Music libraries…", () =>
                {
                    new MusicLibrariesWindow(Model) { Owner = Window.GetWindow(this) }.ShowDialog();
                    _ = ReloadAsync();
                }, primary: true, margin: new Thickness(0, 10, 0, 0)));
            }
            return;
        }

        // The order §4 suggests; empty rows are left out.
        Add(AudioUi.Shelf("Pinned", home.Pinned.Select(PlaylistCard), Scroller));
        Add(AudioUi.Shelf("Recently played", home.RecentlyPlayed.Select(RecentCard).OfType<UIElement>(), Scroller));
        Add(AudioUi.Shelf("Mixes for you", home.Mixes.Select(MixCard), Scroller));
        Add(AudioUi.Shelf("Recently added", home.RecentlyAdded.Select(AlbumCard), Scroller));
        Add(AudioUi.Shelf("Heavy rotation", home.HeavyRotation.Select(AlbumCard), Scroller));
        if (home.OnThisDay is { Tracks.Count: > 0 } day)
        {
            Add(AudioUi.Shelf($"On this day · {day.Label}", TrackCards(day.Tracks, "On this day"), Scroller));
        }
        Add(AudioUi.Shelf("Forgotten favorites", TrackCards(home.ForgottenFavorites, "Forgotten favorites"), Scroller));
        Add(AudioUi.Shelf("Discover", home.Discover.Select(AlbumCard), Scroller));
        Add(AudioUi.Shelf("Loved", TrackCards(home.LovedTracks, "Loved"), Scroller));
        Add(AudioUi.Shelf("Decades", home.Decades.Select(decade => (UIElement)AudioUi.Card(
            DecadeTile(decade.Label), decade.Label, AudioUi.Count(decade.TrackCount, "song", "songs"),
            () => Navigator.Push(new MusicTrackListView(Model, Navigator, $"The {decade.Label}", decade: decade.Decade), $"The {decade.Label}"), 120)), Scroller));
    }

    private void Add(FrameworkElement? element)
    {
        if (element is not null) Host.Children.Add(element);
    }

    private static FrameworkElement DecadeTile(string label) => new Border
    {
        Width = 120,
        Height = 120,
        CornerRadius = new CornerRadius(8),
        Background = AudioUi.Resource("CardBackground"),
        BorderBrush = AudioUi.Resource("CardBorder"),
        BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = label, FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = Palette.Cyan, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };

    private IEnumerable<UIElement> TrackCards(List<MusicTrack> tracks, string name) =>
        tracks.Select((track, index) => (UIElement)AudioUi.Card(AudioUi.Art(track.ArtUrl, 130), track.Title, track.ArtistLine,
            () => _ = Player.PlayTracksAsync(tracks, index, false, new AudioContext { Type = "queue", Name = name }), 130,
            AudioUi.TrackMenu(track, Navigator)));

    private UIElement AlbumCard(MusicAlbum album) =>
        AudioUi.Card(AudioUi.Art(album.ArtUrl, 150), album.Title, album.Artist,
            () => AudioNav.OpenAlbum(Model, Navigator, album.Id, album.Title), 150, AlbumMenu(album));

    private ContextMenu AlbumMenu(MusicAlbum album)
    {
        var menu = new ContextMenu();
        menu.Items.Add(AudioUi.Item("Play", () => _ = AudioNav.PlayAlbumAsync(Model, album.Id, false)));
        menu.Items.Add(AudioUi.Item("Shuffle", () => _ = AudioNav.PlayAlbumAsync(Model, album.Id, true)));
        menu.Items.Add(AudioUi.Item("Start radio", () => _ = Player.StartRadioAsync("album", album.Id, album.Title)));
        if (!string.IsNullOrEmpty(album.ArtistId)) menu.Items.Add(AudioUi.Item("Go to artist", () => AudioNav.OpenArtist(Model, Navigator, album.ArtistId!, album.Artist ?? "")));
        return menu;
    }

    private UIElement ArtistCard(MusicArtist artist) =>
        AudioUi.Card(AudioUi.Art(artist.ArtUrl, 140, AudioUi.MusicGlyph, round: true), artist.Name,
            AudioUi.Count(artist.AlbumCount, "album", "albums"), () => AudioNav.OpenArtist(Model, Navigator, artist.Id, artist.Name), 140);

    private UIElement PlaylistCard(MusicPlaylist playlist) =>
        AudioUi.Card(AudioUi.Mosaic(playlist.ArtUrls, 150), playlist.Name,
            playlist.Smart ? "Smart playlist" : AudioUi.Count(playlist.TrackCount, "song", "songs"),
            () => AudioNav.OpenPlaylist(Model, Navigator, playlist.Id, playlist.Name), 150);

    private UIElement MixCard(MusicMix mix) =>
        AudioUi.Card(AudioUi.Mosaic(mix.ArtUrls, 150, AudioUi.ShuffleGlyph), mix.Name, mix.Description,
            () => _ = Player.PlayMixAsync(mix.Id), 150);

    private UIElement? RecentCard(MusicRecentItem item) => item.Kind switch
    {
        "album" when item.Album is { } album => AlbumCard(album),
        "playlist" when item.Playlist is { } playlist => PlaylistCard(playlist),
        "artist" when item.Artist is { } artist => ArtistCard(artist),
        "mix" when item.Mix is { } mix => MixCard(mix),
        _ => null,
    };

    // ---------------------------------------------------------------- Library tabs

    private StackPanel SortBar(IReadOnlyList<(string Value, string Label)> sorts, string? flagLabel = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        row.Children.Add(new TextBlock { Text = "Sort", Style = AudioUi.StyleOf("Caption"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var combo = new ComboBox { Width = 150, ItemsSource = sorts.Select(sort => sort.Label).ToList() };
        var index = Math.Max(0, sorts.ToList().FindIndex(sort => sort.Value == _sort));
        combo.SelectedIndex = index;
        if (_sort.Length == 0) _sort = sorts[0].Value;
        combo.SelectionChanged += (_, _) =>
        {
            _sort = sorts[Math.Max(0, combo.SelectedIndex)].Value;
            _order = _sort is "name" or "title" or "artist" or "album" ? "asc" : "desc";
            _ = ReloadAsync();
        };
        row.Children.Add(combo);
        var order = AudioUi.TextButton(_order == "asc" ? "↑" : "↓", () =>
        {
            _order = _order == "asc" ? "desc" : "asc";
            _ = ReloadAsync();
        }, "Ascending or descending", margin: new Thickness(6, 0, 0, 0));
        row.Children.Add(order);
        if (flagLabel is not null)
        {
            var flag = new CheckBox { Content = flagLabel, IsChecked = _filterFlag, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            flag.Click += (_, _) =>
            {
                _filterFlag = flag.IsChecked == true;
                _ = ReloadAsync();
            };
            row.Children.Add(flag);
        }
        return row;
    }

    private WrapPanel? _grid;
    private readonly List<MusicTrack> _songs = new();

    private Button LoadMore(Func<Task> load)
    {
        var button = AudioUi.TextButton("Load more", () => _ = load(), margin: new Thickness(0, 8, 0, 0));
        button.HorizontalAlignment = HorizontalAlignment.Left;
        return button;
    }

    private async Task LoadArtistsAsync(int generation, int offset)
    {
        if (offset == 0) Loading();
        var page = await Model.Client.MusicArtistsAsync(sort: NullIfEmpty(_sort), order: _order, albumArtistsOnly: _filterFlag, offset: offset, limit: PageSize);
        if (generation != _generation) return;
        if (offset == 0)
        {
            Host.Children.Clear();
            Host.Children.Add(SortBar(new[] { ("name", "Name"), ("plays", "Most played"), ("added", "Recently added") }, "Album artists only"));
            _grid = new WrapPanel();
            Host.Children.Add(_grid);
            _status.Text = AudioUi.Count(page.Total, "artist", "artists");
        }
        foreach (var artist in page.Items) _grid!.Children.Add(ArtistCard(artist));
        RemoveLoadMore();
        if (offset + page.Items.Count < page.Total) Host.Children.Add(LoadMore(() => LoadArtistsAsync(generation, offset + page.Items.Count)));
    }

    private async Task LoadAlbumsAsync(int generation, int offset)
    {
        if (offset == 0) Loading();
        var page = await Model.Client.MusicAlbumsAsync(sort: NullIfEmpty(_sort), order: _order, offset: offset, limit: PageSize);
        if (generation != _generation) return;
        if (offset == 0)
        {
            Host.Children.Clear();
            Host.Children.Add(SortBar(new[] { ("title", "Title"), ("artist", "Artist"), ("year", "Year"), ("added", "Recently added"), ("plays", "Most played"), ("rating", "Rating") }));
            _grid = new WrapPanel();
            Host.Children.Add(_grid);
            _status.Text = AudioUi.Count(page.Total, "album", "albums");
        }
        foreach (var album in page.Items) _grid!.Children.Add(AlbumCard(album));
        RemoveLoadMore();
        if (offset + page.Items.Count < page.Total) Host.Children.Add(LoadMore(() => LoadAlbumsAsync(generation, offset + page.Items.Count)));
    }

    private StackPanel? _songList;

    private async Task LoadSongsAsync(int generation, int offset)
    {
        if (offset == 0) Loading();
        var page = await Model.Client.MusicTracksAsync(loved: _filterFlag, sort: NullIfEmpty(_sort), order: _order, offset: offset, limit: PageSize);
        if (generation != _generation) return;
        if (offset == 0)
        {
            _songs.Clear();
            Host.Children.Clear();
            var bar = SortBar(new[] { ("title", "Title"), ("artist", "Artist"), ("album", "Album"), ("year", "Year"), ("added", "Recently added"), ("plays", "Most played"), ("lastPlayed", "Last played"), ("rating", "Rating"), ("duration", "Length") }, "Loved only");
            bar.Children.Add(AudioUi.TextButton("Play", () => _ = Player.PlayTracksAsync(_songs, 0, false, new AudioContext { Type = "queue", Name = "Songs" }), primary: true, margin: new Thickness(16, 0, 6, 0)));
            bar.Children.Add(AudioUi.TextButton("Shuffle", () => _ = Player.PlayTracksAsync(_songs, -1, true, new AudioContext { Type = "queue", Name = "Songs" })));
            Host.Children.Add(bar);
            _songList = new StackPanel();
            Host.Children.Add(_songList);
            _status.Text = AudioUi.Count(page.Total, "song", "songs");
        }
        var start = _songs.Count;
        _songs.AddRange(page.Items);
        for (var i = 0; i < page.Items.Count; i++)
        {
            var index = start + i;
            var track = page.Items[i];
            _songList!.Children.Add(AudioUi.TrackRow(track, null, () => _ = Player.PlayTracksAsync(_songs, index, false, new AudioContext { Type = "queue", Name = "Songs" }),
                AudioUi.TrackMenu(track, Navigator), showArt: true));
        }
        RemoveLoadMore();
        if (offset + page.Items.Count < page.Total) Host.Children.Add(LoadMore(() => LoadSongsAsync(generation, offset + page.Items.Count)));
    }

    private void RemoveLoadMore()
    {
        if (Host.Children.Count > 0 && Host.Children[^1] is Button { Content: "Load more" } last) Host.Children.Remove(last);
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private void RenderGenres(List<MusicGenre> genres)
    {
        Host.Children.Clear();
        _status.Text = AudioUi.Count(genres.Count, "genre", "genres");
        if (genres.Count == 0)
        {
            Host.Children.Add(AudioUi.Caption("No genres yet - the tags in the files don't name any."));
            return;
        }
        var grid = new WrapPanel();
        foreach (var genre in genres.OrderByDescending(genre => genre.TrackCount))
        {
            grid.Children.Add(AudioUi.Card(AudioUi.Mosaic(genre.ArtUrls, 140, AudioUi.MusicGlyph), genre.Name,
                $"{AudioUi.Count(genre.AlbumCount, "album", "albums")} · {AudioUi.Count(genre.TrackCount, "song", "songs")}",
                () => Navigator.Push(new MusicTrackListView(Model, Navigator, genre.Name, genre: genre.Name), genre.Name), 140));
        }
        Host.Children.Add(grid);
    }

    private void RenderPlaylists(List<MusicPlaylist> playlists)
    {
        Host.Children.Clear();
        _status.Text = AudioUi.Count(playlists.Count, "playlist", "playlists");
        Host.Children.Add(AudioUi.Row(
            AudioUi.TextButton("New playlist…", () => OpenPlaylistEditor(false), primary: true),
            AudioUi.TextButton("New smart playlist…", () => OpenPlaylistEditor(true), "A playlist that fills itself from rules, like \"rated 4+ and not played in 90 days\"")));
        var grid = new WrapPanel();
        foreach (var playlist in playlists.OrderByDescending(p => p.Pinned).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            grid.Children.Add(PlaylistCard(playlist));
        }
        Host.Children.Add(grid);
        if (playlists.Count == 0) Host.Children.Add(AudioUi.Caption("No playlists yet. Make one, or add songs to one from any song's … menu."));
    }

    // ---------------------------------------------------------------- Search

    private void RenderSearch(MusicSearchResult results)
    {
        Host.Children.Clear();
        if (results.IsEmpty)
        {
            Host.Children.Add(AudioUi.Caption($"Nothing matches “{Query}”."));
            return;
        }
        if (results.Tracks.Count > 0)
        {
            Host.Children.Add(AudioUi.Heading("Songs", 0));
            for (var i = 0; i < results.Tracks.Count; i++)
            {
                var index = i;
                var track = results.Tracks[i];
                Host.Children.Add(AudioUi.TrackRow(track, null,
                    () => _ = Player.PlayTracksAsync(results.Tracks, index, false, new AudioContext { Type = "search", Name = Query }),
                    AudioUi.TrackMenu(track, Navigator), showArt: true));
            }
        }
        Add(AudioUi.Shelf("Artists", results.Artists.Select(ArtistCard), Scroller));
        Add(AudioUi.Shelf("Albums", results.Albums.Select(AlbumCard), Scroller));
        Add(AudioUi.Shelf("Playlists", results.Playlists.Select(PlaylistCard), Scroller));
        if (results.Genres.Count > 0)
        {
            Host.Children.Add(AudioUi.Heading("Genres"));
            var row = new WrapPanel();
            foreach (var genre in results.Genres)
            {
                row.Children.Add(AudioUi.TextButton(genre, () => Navigator.Push(new MusicTrackListView(Model, Navigator, genre, genre: genre), genre), margin: new Thickness(0, 0, 8, 8)));
            }
            Host.Children.Add(row);
        }
    }
}

/// <summary>Browsing the music by folder (GET /api/music/folders): the libraries, then their folders.</summary>
public sealed class MusicFolderBrowser : StackPanel
{
    private readonly AppModel _model;
    private readonly INavigator _navigator;

    public MusicFolderBrowser(AppModel model, INavigator navigator)
    {
        _model = model;
        _navigator = navigator;
        _ = OpenAsync(null, null);
    }

    private async Task OpenAsync(string? libraryId, string? path)
    {
        Children.Clear();
        Children.Add(AudioUi.Caption("Loading…"));
        try
        {
            var listing = await _model.Client.MusicFoldersAsync(libraryId, path);
            Children.Clear();
            var crumbs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            if (libraryId is not null)
            {
                crumbs.Children.Add(AudioUi.TextButton("↑ Up", () =>
                {
                    if (string.IsNullOrEmpty(listing.Path)) _ = OpenAsync(null, null);
                    else _ = OpenAsync(libraryId, listing.Parent ?? "");
                }));
            }
            crumbs.Children.Add(new TextBlock { Text = libraryId is null ? "Libraries" : $"/{listing.Path}", Foreground = Palette.SecondaryText, VerticalAlignment = VerticalAlignment.Center });
            Children.Add(crumbs);

            foreach (var folder in listing.Folders)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock { Text = "", FontFamily = AudioUi.Icons, Foreground = Palette.Cyan, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new TextBlock { Text = folder.Name, Foreground = Palette.PrimaryText, VerticalAlignment = VerticalAlignment.Center });
                if (folder.TrackCount > 0) row.Children.Add(new TextBlock { Text = $"  {AudioUi.Count(folder.TrackCount, "song", "songs")}", Foreground = Palette.SecondaryText, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
                // At the top level each "folder" is a library; its path is the library id. Double-click or Enter opens, as in Explorer.
                var next = libraryId is null ? (Library: folder.Path, Path: "") : (Library: libraryId, Path: folder.Path);
                var clickable = AudioUi.ClickableRow(row, () => _ = OpenAsync(next.Library, next.Path), null, folder.Name);
                Children.Add(clickable);
            }
            if (listing.Tracks.Count > 0)
            {
                Children.Add(AudioUi.Row(
                    AudioUi.TextButton("Play folder", () => _ = PlayerController.Shared!.PlayTracksAsync(listing.Tracks, 0, false, new AudioContext { Type = "queue", Name = listing.Path }), primary: true),
                    AudioUi.TextButton("Shuffle", () => _ = PlayerController.Shared!.PlayTracksAsync(listing.Tracks, -1, true, new AudioContext { Type = "queue", Name = listing.Path }))));
                for (var i = 0; i < listing.Tracks.Count; i++)
                {
                    var index = i;
                    var track = listing.Tracks[i];
                    Children.Add(AudioUi.TrackRow(track, track.TrackNumber?.ToString(),
                        () => _ = PlayerController.Shared!.PlayTracksAsync(listing.Tracks, index, false, new AudioContext { Type = "queue", Name = listing.Path }),
                        AudioUi.TrackMenu(track, _navigator)));
                }
            }
            if (listing.Folders.Count == 0 && listing.Tracks.Count == 0) Children.Add(AudioUi.Caption("Nothing in this folder."));
        }
        catch (Exception error)
        {
            Children.Clear();
            Children.Add(AudioUi.Caption(AppModel.UserFacingMessage(error)));
        }
    }
}
