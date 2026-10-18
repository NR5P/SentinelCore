using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views.Audio;

// "Suggested for you" (core/docs/MUSIC_AND_PODCASTS.md "### Suggestions"): albums by artists like
// the ones this person listens to, not in the library yet. Only while music downloads say
// canDownload. Two pieces:
//   MusicSuggestionsShelf  the row on Music's Home, with "See all"
//   MusicSuggestionsView   every suggestion with tick boxes, Download, Not interested, Refresh
// While the server is still building (state working) both show what there is and ask again every
// 3 seconds. Download starts an imported list named "Suggested for you", which then shows under
// Downloads → Imported lists; the albums are marked "On its way" here straight away.

/// <summary>What both pieces do with one album: download it, or say not interested.</summary>
internal static class MusicSuggestionActions
{
    public static async Task<bool> DownloadAsync(AppModel model, IReadOnlyList<MusicSuggestedAlbum> albums, Window? owner)
    {
        var wanted = albums.Where(a => !a.IsQueued).ToList();
        if (wanted.Count == 0) return false;
        try
        {
            await model.Client.DownloadMusicSuggestionsAsync(wanted);
            foreach (var album in wanted) album.Queued = true;
            return true;
        }
        catch (Exception error)
        {
            MessageBox.Show(owner ?? Application.Current.MainWindow, AppModel.UserFacingMessage(error), MusicSuggestionText.Heading, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    /// <summary>"Not interested": the album with a title, the whole artist without.</summary>
    public static async Task<bool> DismissAsync(AppModel model, MusicSuggestedAlbum album, bool wholeArtist, Window? owner)
    {
        if (string.IsNullOrWhiteSpace(album.Artist)) return false;
        try
        {
            await model.Client.DismissMusicSuggestionAsync(album.Artist!, wholeArtist ? null : album.Title);
            return true;
        }
        catch (Exception error)
        {
            MessageBox.Show(owner ?? Application.Current.MainWindow, AppModel.UserFacingMessage(error), MusicSuggestionText.Heading, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    /// <summary>The right-click menu on a card or row.</summary>
    public static ContextMenu Menu(MusicSuggestedAlbum album, Action download, Action notAlbum, Action notArtist)
    {
        var menu = new ContextMenu();
        menu.Items.Add(AudioUi.Item(album.IsQueued ? "On its way" : "Download", download, enabled: !album.IsQueued));
        menu.Items.Add(new Separator());
        menu.Items.Add(AudioUi.Item("Not interested", notAlbum));
        if (!string.IsNullOrWhiteSpace(album.Artist)) menu.Items.Add(AudioUi.Item($"Not interested in {album.Artist}", notArtist));
        return menu;
    }

    public static string TitleLine(MusicSuggestedAlbum album) =>
        album.Year is { } year ? $"{album.Title} ({year})" : album.Title ?? "";
}

/// <summary>The "Suggested for you" row on Music's Home.</summary>
public sealed class MusicSuggestionsShelf : StackPanel
{
    private const int CardsShown = 12;

    private readonly AppModel _model;
    private readonly INavigator _navigator;
    private readonly ScrollViewer _page;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private MusicSuggestions? _suggestions;

    public MusicSuggestionsShelf(AppModel model, INavigator navigator, ScrollViewer page)
    {
        _model = model;
        _navigator = navigator;
        _page = page;
        _poll.Tick += async (_, _) => await LoadAsync();
        Loaded += async (_, _) => await LoadAsync();
        Unloaded += (_, _) => _poll.Stop();
    }

    private async Task LoadAsync()
    {
        _poll.Stop();
        try
        {
            _suggestions = await _model.Client.MusicSuggestionsAsync();
        }
        catch (Exception)
        {
            // Home carries on without the row; the full view says what went wrong.
            Children.Clear();
            return;
        }
        Draw();
        if (_suggestions.IsWorking && IsLoaded) _poll.Start();
    }

    private void Draw()
    {
        Children.Clear();
        if (_suggestions is not { } s) return;

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 0) };
        var seeAll = new Button { Content = "See all", Style = AudioUi.StyleOf("LinkButton"), VerticalAlignment = VerticalAlignment.Center };
        seeAll.Click += (_, _) => OpenAll();
        DockPanel.SetDock(seeAll, Dock.Right);
        head.Children.Add(seeAll);
        var heading = AudioUi.Heading(MusicSuggestionText.Heading, 6);
        if (s.IsWorking) heading.Text += "  ·  FINDING MORE…";
        head.Children.Add(heading);

        if (s.Albums.Count == 0)
        {
            Children.Add(head);
            Children.Add(AudioUi.Caption(s.IsWorking ? "Finding albums you might like…" : s.IsError ? s.Message ?? "Suggestions could not be built." : MusicSuggestionText.Empty));
            Children.Add(new Border { Height = 12 });
            return;
        }

        var cards = s.Albums.Take(CardsShown).Select(album => (UIElement)Card(album)).ToList();
        var shelf = AudioUi.Shelf(MusicSuggestionText.Heading, cards, _page);
        // The shared shelf draws its own heading; swap it for one with "See all".
        if (shelf is StackPanel section && section.Children.Count > 0 && section.Children[0] is TextBlock)
        {
            section.Children.RemoveAt(0);
            section.Children.Insert(0, head);
        }
        if (shelf is not null) Children.Add(shelf);
    }

    private Button Card(MusicSuggestedAlbum album)
    {
        const double width = 150;
        var art = AudioUi.Art(album.ArtUrl, width);
        var body = new StackPanel { Width = width };
        body.Children.Add(art);
        body.Children.Add(new TextBlock { Text = album.Title ?? "", Foreground = Palette.PrimaryText, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 8, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        body.Children.Add(new TextBlock { Text = album.Artist ?? "", Foreground = Palette.SecondaryText, FontSize = 11.5, Margin = new Thickness(2, 2, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        var because = album.IsQueued ? "On its way" : album.BecauseLine;
        if (!string.IsNullOrEmpty(because))
        {
            body.Children.Add(new TextBlock { Text = because, Foreground = album.IsQueued ? Palette.Green : Palette.SecondaryText, FontSize = 11, Margin = new Thickness(2, 2, 2, 0), TextWrapping = TextWrapping.Wrap, MaxHeight = 30, TextTrimming = TextTrimming.CharacterEllipsis });
        }
        var tip = string.Join("\n", new[] { MusicSuggestionActions.TitleLine(album), album.Artist, album.BecauseLine }.Where(t => !string.IsNullOrEmpty(t)));
        var button = MovieCards.PlainButton(body, OpenAll, tip);
        button.Margin = new Thickness(0, 0, 16, 16);
        button.VerticalAlignment = VerticalAlignment.Top;
        button.ContextMenu = MusicSuggestionActions.Menu(album,
            () => _ = DownloadAsync(album),
            () => _ = DismissAsync(album, wholeArtist: false),
            () => _ = DismissAsync(album, wholeArtist: true));
        MovieCards.Highlight(button, art, button);
        return button;
    }

    private async Task DownloadAsync(MusicSuggestedAlbum album)
    {
        if (await MusicSuggestionActions.DownloadAsync(_model, new[] { album }, Window.GetWindow(this))) Draw();
    }

    private async Task DismissAsync(MusicSuggestedAlbum album, bool wholeArtist)
    {
        if (await MusicSuggestionActions.DismissAsync(_model, album, wholeArtist, Window.GetWindow(this))) await LoadAsync();
    }

    private void OpenAll() => _navigator.Push(new MusicSuggestionsView(_model, _navigator), MusicSuggestionText.Heading);
}

/// <summary>Every suggestion: tick and download several, download one, or say not interested.</summary>
public sealed class MusicSuggestionsView : AudioPage
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly HashSet<string> _ticked = new();
    private readonly TextBlock _basedOn = AudioUi.Caption("", 2);
    private readonly TextBlock _state = AudioUi.Caption("");
    private readonly ProgressBar _spinner = new() { IsIndeterminate = true, Height = 3, Width = 160, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Button _downloadTicked;
    private readonly Button _refresh;
    private MusicSuggestions? _suggestions;
    private int _generation;

    public MusicSuggestionsView(AppModel model, INavigator navigator) : base(model, navigator)
    {
        _downloadTicked = AudioUi.TextButton(MusicSuggestionText.DownloadTicked(0), () => _ = DownloadTickedAsync(), "Download every ticked album as one imported list", primary: true);
        _refresh = AudioUi.TextButton("Refresh", () => _ = LoadAsync(refresh: true), "Build the suggestions again from what you listen to now");

        var title = new StackPanel();
        title.Children.Add(new TextBlock { Text = MusicSuggestionText.Heading, Style = AudioUi.StyleOf("SectionTitle") });
        title.Children.Add(_basedOn);
        var stateRow = new StackPanel { Orientation = Orientation.Horizontal };
        stateRow.Children.Add(_state);
        stateRow.Children.Add(_spinner);
        title.Children.Add(stateRow);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(Link("all", () => Tick(all: true)));
        actions.Children.Add(Link("none", () => Tick(all: false)));
        actions.Children.Add(_downloadTicked);
        actions.Children.Add(_refresh);

        var top = new Grid { Margin = new Thickness(20, 16, 20, 8) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(title);
        Grid.SetColumn(actions, 1);
        top.Children.Add(actions);
        SetHeader(top);

        _poll.Tick += async (_, _) => await LoadAsync(refresh: false);
        Loaded += async (_, _) => await LoadAsync(refresh: false);
        Unloaded += (_, _) => _poll.Stop();
        Loading();
    }

    private static Button Link(string text, Action click)
    {
        var button = new Button { Content = text, Style = AudioUi.StyleOf("LinkButton"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => click();
        return button;
    }

    private async Task LoadAsync(bool refresh)
    {
        _poll.Stop();
        var generation = ++_generation;
        if (refresh)
        {
            _state.Text = "Building them again…";
            _spinner.Visibility = Visibility.Visible;
        }
        try
        {
            var suggestions = await Model.Client.MusicSuggestionsAsync(refresh);
            if (generation != _generation) return;
            _suggestions = suggestions;
            Notice(null);
            Render();
            if (suggestions.IsWorking && IsLoaded) _poll.Start();
        }
        catch (Exception error)
        {
            if (generation != _generation) return;
            _spinner.Visibility = Visibility.Collapsed;
            _state.Text = "";
            if (_suggestions is null) Host.Children.Clear();
            Fail(error);
        }
    }

    private void Render()
    {
        if (_suggestions is not { } s) return;
        _basedOn.Text = s.BasedOnLine ?? "";
        _basedOn.Visibility = string.IsNullOrEmpty(_basedOn.Text) ? Visibility.Collapsed : Visibility.Visible;
        _spinner.Visibility = s.IsWorking ? Visibility.Visible : Visibility.Collapsed;
        _state.Text = s.IsWorking ? "Finding albums you might like…" : "";
        _state.Foreground = Palette.SecondaryText;
        if (s.IsError)
        {
            _state.Text = s.Message ?? "The suggestions could not be built.";
            _state.Foreground = Palette.Amber;
        }

        // Ticks only for albums still on the list and not on their way.
        var keys = s.Albums.Where(a => !a.IsQueued).Select(a => a.DisplayKey).ToHashSet();
        _ticked.IntersectWith(keys);

        Host.Children.Clear();
        if (s.Albums.Count == 0)
        {
            if (!s.IsWorking) Host.Children.Add(AudioUi.Caption(s.IsError ? "Nothing to show yet." : MusicSuggestionText.Empty, 6));
        }
        foreach (var album in s.Albums) Host.Children.Add(Row(album));
        UpdateTickedButton();
    }

    private FrameworkElement Row(MusicSuggestedAlbum album)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var key = album.DisplayKey;
        var tick = new CheckBox { IsChecked = _ticked.Contains(key), IsEnabled = !album.IsQueued, VerticalAlignment = VerticalAlignment.Center, ToolTip = album.IsQueued ? "On its way" : "Tick to download with the others" };
        AudioUi.AutomationName(tick, $"Tick {album.Title}");
        tick.Checked += (_, _) =>
        {
            _ticked.Add(key);
            UpdateTickedButton();
        };
        tick.Unchecked += (_, _) =>
        {
            _ticked.Remove(key);
            UpdateTickedButton();
        };
        grid.Children.Add(tick);

        var art = AudioUi.Art(album.ArtUrl, 64);
        Grid.SetColumn(art, 1);
        grid.Children.Add(art);

        var text = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = MusicSuggestionActions.TitleLine(album), Foreground = Palette.PrimaryText, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = album.Artist ?? "", Foreground = Palette.SecondaryText, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        if (album.BecauseLine is { } because) text.Children.Add(new TextBlock { Text = because, Foreground = Palette.SecondaryText, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        var links = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        links.Children.Add(Link("Not interested", () => _ = DismissAsync(album, wholeArtist: false)));
        if (!string.IsNullOrWhiteSpace(album.Artist)) links.Children.Add(Link($"Not interested in {album.Artist}", () => _ = DismissAsync(album, wholeArtist: true)));
        text.Children.Add(links);
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);

        FrameworkElement right = album.IsQueued
            ? new TextBlock { Text = "On its way", Foreground = Palette.Green, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }
            : AudioUi.TextButton("Download", () => _ = DownloadAsync(new[] { album }), "Search for this album and download it");
        right.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(right, 3);
        grid.Children.Add(right);

        grid.Background = System.Windows.Media.Brushes.Transparent; // so the whole row takes a right-click
        grid.ContextMenu = MusicSuggestionActions.Menu(album,
            () => _ = DownloadAsync(new[] { album }),
            () => _ = DismissAsync(album, wholeArtist: false),
            () => _ = DismissAsync(album, wholeArtist: true));
        return grid;
    }

    private void Tick(bool all)
    {
        _ticked.Clear();
        if (all && _suggestions is { } s)
            foreach (var album in s.Albums.Where(a => !a.IsQueued)) _ticked.Add(album.DisplayKey);
        Render();
    }

    private void UpdateTickedButton()
    {
        _downloadTicked.Content = MusicSuggestionText.DownloadTicked(_ticked.Count);
        _downloadTicked.IsEnabled = _ticked.Count > 0;
    }

    private Task DownloadTickedAsync() =>
        _suggestions is { } s ? DownloadAsync(s.Albums.Where(a => _ticked.Contains(a.DisplayKey)).ToList()) : Task.CompletedTask;

    private async Task DownloadAsync(IReadOnlyList<MusicSuggestedAlbum> albums)
    {
        var count = albums.Count(a => !a.IsQueued);
        if (!await MusicSuggestionActions.DownloadAsync(Model, albums, Window.GetWindow(this))) return;
        foreach (var album in albums) _ticked.Remove(album.DisplayKey);
        Render();
        Notice($"Working through {MediaImportText.CountOf(MediaImportKind.Music, count)}. Watch it under Downloads → Imported lists.");
    }

    private async Task DismissAsync(MusicSuggestedAlbum album, bool wholeArtist)
    {
        if (!await MusicSuggestionActions.DismissAsync(Model, album, wholeArtist, Window.GetWindow(this)) || _suggestions is null) return;
        // Gone from the next read at once; take it (or the artist) off here without waiting.
        _suggestions.Albums.RemoveAll(a => wholeArtist
            ? string.Equals(a.Artist, album.Artist, StringComparison.OrdinalIgnoreCase)
            : a.DisplayKey == album.DisplayKey);
        Render();
    }
}
