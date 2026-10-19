using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SentinelCore.Client;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;

namespace SentinelCore.App.Views.Audio;

/// <summary>
/// "+ Add" for Music (MUSIC_AND_PODCASTS.md §4 "Getting music"): find an album on MusicBrainz,
/// search the sites for it in the background (the VPN can take a minute to come up), and grab a
/// release. Same flow as Movies' + Add.
/// </summary>
public sealed class MusicAddWindow : AudioDialog
{
    private readonly MusicDownloadStatus _status;
    private readonly TextBox _query = new() { ToolTip = "Type an artist and album for the best results, like \"fleetwood mac rumours\"" };
    private readonly StackPanel _results = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private MusicReleaseGroup? _album;
    private string? _searchId;
    private bool _showOthers;

    public MusicAddWindow(AppModel model, MusicDownloadStatus status)
        : base(model, "Add an album", "Find an album, then pick a release to download. It is filed into the music library and shows up under Recently added.", 760, 640)
    {
        _status = status;
        if (!status.MusicLibrary)
        {
            Body.Children.Add(new TextBlock { Text = "Add a music library on this server's own disk first.", Foreground = Palette.Amber, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        }
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(_query);
        var find = AudioUi.TextButton("Find", () => _ = LookupAsync(), primary: true, margin: new Thickness(8, 0, 0, 0));
        Grid.SetColumn(find, 1);
        row.Children.Add(find);
        Body.Children.Add(row);
        Body.Children.Add(_results);
        _query.KeyDown += (_, args) =>
        {
            if (args.Key != System.Windows.Input.Key.Enter) return;
            args.Handled = true;
            _ = LookupAsync();
        };
        _poll.Tick += async (_, _) => await PollAsync();
        Closed += (_, _) => _poll.Stop();
        AddFooterButton("Close", Close, isCancel: true);
        Loaded += (_, _) => _query.Focus();
    }

    private async Task LookupAsync()
    {
        var q = _query.Text.Trim();
        if (q.Length == 0) return;
        _poll.Stop();
        _results.Children.Clear();
        Message("Looking it up…");
        try
        {
            var albums = await Model.Client.MusicDownloadLookupAsync(q);
            Message(albums.Count == 0 ? "Nothing found. Try the artist and album together." : null);
            _results.Children.Add(AudioUi.Heading("Albums", 12));
            foreach (var album in albums)
            {
                var facts = new List<string>();
                if (!string.IsNullOrEmpty(album.Artist)) facts.Add(album.Artist!);
                if (album.Year is { } year) facts.Add(year.ToString());
                if (!string.IsNullOrEmpty(album.Type)) facts.Add(album.Type!);
                if (album.TrackCount is { } count) facts.Add(AudioUi.Count(count, "track", "tracks"));
                if (album.InLibrary) facts.Add("already in the library");
                var line = new Grid();
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                // Cover Art Archive pictures may 404; the placeholder glyph stays underneath.
                line.Children.Add(AudioUi.Art(album.ArtUrl, 56));
                var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = album.Title, Foreground = Palette.PrimaryText, FontWeight = FontWeights.SemiBold });
                text.Children.Add(new TextBlock { Text = string.Join(" · ", facts), Foreground = album.InLibrary ? Palette.Amber : Palette.SecondaryText, FontSize = 11.5 });
                Grid.SetColumn(text, 1);
                line.Children.Add(text);
                var pick = album;
                var button = new Button { Content = line, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 6) };
                button.Click += (_, _) => _ = SearchAsync(pick);
                _results.Children.Add(button);
            }
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private async Task SearchAsync(MusicReleaseGroup album)
    {
        if (album.InLibrary && !AudioUi.Confirm("Already in the library", $"\"{album.Title}\" by {album.Artist} looks like it's already in the library. Search for it anyway?")) return;
        _album = album;
        _showOthers = false;
        _results.Children.Clear();
        _results.Children.Add(AudioUi.Caption($"Searching for {album.Title} by {album.Artist}…", 12));
        try
        {
            var search = await Model.Client.StartMusicDownloadSearchAsync(new MusicReleaseSearchInput
            {
                Artist = album.Artist ?? "",
                Album = album.Title,
                Year = album.Year,
                Mbid = album.Mbid,
                ArtUrl = album.ArtUrl,
            });
            _searchId = search.Id;
            Render(search);
            if (!search.IsFinished) _poll.Start();
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private async Task PollAsync()
    {
        if (_searchId is null) return;
        try
        {
            var search = await Model.Client.MusicDownloadSearchStateAsync(_searchId);
            Render(search);
            if (search.IsFinished) _poll.Stop();
        }
        catch (Exception error)
        {
            _poll.Stop();
            Fail(error);
        }
    }

    private void Render(MusicReleaseSearch search)
    {
        _results.Children.Clear();
        _results.Children.Add(AudioUi.Heading($"{search.Album ?? _album?.Title} · {search.Artist ?? _album?.Artist}", 12));
        if (!search.IsFinished)
        {
            _results.Children.Add(AudioUi.Caption(search.Message ?? "Searching…"));
            _results.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 6, 0, 0) });
            return;
        }
        if (search.Phase == "failed")
        {
            _results.Children.Add(new TextBlock { Text = search.Error ?? search.Message ?? "The search failed.", Foreground = Palette.Red, TextWrapping = TextWrapping.Wrap });
            return;
        }
        if (search.Results.Count == 0 && search.Others.Count == 0)
        {
            _results.Children.Add(AudioUi.Caption("Nobody is sharing this album right now."));
            return;
        }
        foreach (var release in search.Results) _results.Children.Add(ReleaseRow(search.Id, release));
        if (search.Others.Count > 0)
        {
            if (_showOthers)
            {
                _results.Children.Add(AudioUi.Heading("Other releases"));
                foreach (var release in search.Others) _results.Children.Add(ReleaseRow(search.Id, release));
            }
            else
            {
                var more = new Button { Content = $"Show {search.Others.Count} more", Style = AudioUi.StyleOf("LinkButton"), Margin = new Thickness(0, 6, 0, 0) };
                more.Click += (_, _) =>
                {
                    _showOthers = true;
                    Render(search);
                };
                _results.Children.Add(more);
            }
        }
    }

    private FrameworkElement ReleaseRow(string searchId, MusicRelease release)
    {
        var facts = new List<string>();
        if (!string.IsNullOrEmpty(release.Format)) facts.Add(release.Format!);
        facts.Add(MediaDownloadText.SizeLabel(release.SizeBytes));
        facts.Add($"{release.Seeders} seeding");
        if (!string.IsNullOrEmpty(release.Site)) facts.Add(release.Site!);
        if (!release.Matches) facts.Add("may not match");
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = release.Title, Foreground = Palette.PrimaryText, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = string.Join(" · ", facts), Foreground = release.Lossless ? Palette.Green : Palette.SecondaryText, FontSize = 11.5 });
        if (!string.IsNullOrEmpty(release.Warning)) text.Children.Add(new TextBlock { Text = release.Warning, Foreground = Palette.Amber, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        grid.Children.Add(text);
        var grab = AudioUi.TextButton("Download", () => _ = GrabAsync(searchId, release), primary: true, margin: new Thickness(10, 0, 0, 0));
        grab.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(grab, 1);
        grid.Children.Add(grab);
        return grid;
    }

    private async Task GrabAsync(string searchId, MusicRelease release, bool downloadAgain = false)
    {
        try
        {
            await Model.Client.MusicDownloadGrabAsync(new MusicGrabInput { SearchId = searchId, ReleaseId = release.Id, DownloadAgain = downloadAgain ? true : null });
            Done();
        }
        // Already in the library, maybe as another edition: ask before fetching it again.
        catch (SentinelServerException error) when (error.Status == 409 && error.Code == "alreadyInLibrary" && !downloadAgain)
        {
            var answer = MessageBox.Show(this, error.Message + "\n\nDownload it again anyway?", "Already in your library",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) await GrabAsync(searchId, release, downloadAgain: true);
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }
}

/// <summary>The album downloads: what is on its way, and what arrived lately.</summary>
public sealed class MusicDownloadsView : AudioPage
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };

    // The downloads, rebuilt on every poll, then the imported lists, which keep themselves current.
    private readonly StackPanel _list = new();
    private readonly ImportedListsPanel _imports;

    public MusicDownloadsView(AppModel model, INavigator navigator, string? startedMessage = null) : base(model, navigator)
    {
        _imports = new ImportedListsPanel(model, MediaImportKind.Music);
        _imports.Say(startedMessage);
        Host.Children.Add(_list);
        Host.Children.Add(_imports);
        _poll.Tick += async (_, _) => await LoadAsync();
        Loaded += async (_, _) =>
        {
            await LoadAsync();
            _poll.Start();
        };
        Unloaded += (_, _) => _poll.Stop();
    }

    private async Task LoadAsync()
    {
        try
        {
            var list = await Model.Client.MusicDownloadsAsync();
            _list.Children.Clear();
            _list.Children.Add(AudioUi.Title("Album downloads"));
            if (MediaDownloadText.VpnLook(list.Status) is { } look) _list.Children.Add(AudioUi.Caption(look.Text, 4));
            _list.Children.Add(AudioUi.Heading("Downloading"));
            if (list.Active.Count == 0) _list.Children.Add(AudioUi.Caption("Nothing downloading."));
            foreach (var download in list.Active) _list.Children.Add(Row(download));
            _list.Children.Add(AudioUi.Heading("Recent"));
            if (list.Recent.Count == 0) _list.Children.Add(AudioUi.Caption("Nothing yet."));
            foreach (var download in list.Recent) _list.Children.Add(Row(download));
            Notice(null);
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private FrameworkElement Row(MediaDownload download)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(AudioUi.Art(download.PosterUrl, 56));
        var text = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = download.Title ?? "Album", Foreground = Palette.PrimaryText, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { download.Subtitle, MediaDownloadText.StateWord(download.State) }.Concat(MediaDownloadText.DownloadFacts(download, DateTimeOffset.UtcNow)).Where(s => !string.IsNullOrEmpty(s))), Foreground = Palette.SecondaryText, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(download.Error)) text.Children.Add(new TextBlock { Text = download.Error, Foreground = download.IsFailed ? Palette.Red : Palette.Amber, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        if (download.IsActive) text.Children.Add(new ProgressBar { Maximum = 1, Value = MediaDownloadText.BarFraction(download), Height = 4, Margin = new Thickness(0, 6, 0, 0) });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (download.IsFailed && download.Id is { } failedId)
        {
            buttons.Children.Add(AudioUi.TextButton("Retry", () => _ = Try(async () =>
            {
                await Model.Client.RetryMusicDownloadAsync(failedId);
                await LoadAsync();
            })));
        }
        if (download.Id is { } id)
        {
            buttons.Children.Add(AudioUi.TextButton(download.IsActive ? "Cancel" : "Clear", () => _ = Try(async () =>
            {
                if (download.IsActive && !AudioUi.Confirm("Cancel download", $"Stop downloading {download.Title}?")) return;
                await Model.Client.DeleteMusicDownloadAsync(id);
                await LoadAsync();
            })));
        }
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);
        return grid;
    }
}
