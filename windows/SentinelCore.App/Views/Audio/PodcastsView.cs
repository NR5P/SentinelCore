using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using SentinelCore.App.Audio;
using SentinelCore.Client;
using SentinelCore.Client.Audio;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views.Audio;

/// <summary>
/// The Podcasts section: Listen Now rows, Up Next, the Library of shows, Discover (search, top
/// charts, recommendations, add by URL), transcript search and bookmarks, plus OPML, Fill my
/// drive and the Morning Briefing. The contract is core/docs/MUSIC_AND_PODCASTS.md §5-§6.
/// </summary>
public sealed class PodcastsView : AudioPage
{
    private static readonly (string Key, string Label)[] TabList =
    {
        ("home", "Listen Now"), ("upnext", "Up Next"), ("library", "Library"), ("discover", "Discover"),
        ("search", "Search transcripts"), ("bookmarks", "Bookmarks"),
    };

    private readonly ContentControl _tabs = new();
    private readonly TextBlock _status = AudioUi.Caption("Loading…");
    private readonly TextBox _search = new() { Width = 260 };
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private string _tab = "home";
    private int _generation;
    private bool _loaded;
    private long? _genre;

    public PodcastsView(AppModel model, INavigator navigator) : base(model, navigator)
    {
        var title = new StackPanel();
        title.Children.Add(new TextBlock { Text = "Podcasts", Style = AudioUi.StyleOf("SectionTitle") });
        title.Children.Add(_status);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(AudioUi.TextButton("Add by URL…", () => _ = AddByUrlAsync(), "Follow a show from its RSS feed address"));
        actions.Children.Add(AudioUi.TextButton("Fill my drive…", () => _ = FillAsync(), "Queue unplayed episodes that fit the time you have"));
        actions.Children.Add(AudioUi.TextButton("Morning Briefing", () => _ = Player.PlayBriefingAsync(), "Today's weather, calendar and reminders read out, then your briefing shows"));
        actions.Children.Add(MoreButton());
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
            _ = SearchAsync();
        };

        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            _ = ReloadAsync();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && _loaded && _tab is "home" or "upnext") _ = ReloadAsync();
        };
    }

    private Button MoreButton()
    {
        var menu = new ContextMenu();
        menu.Items.Add(AudioUi.Item("Import OPML…", () => _ = ImportOpmlAsync(Model, Window.GetWindow(this), () => _ = ReloadAsync())));
        menu.Items.Add(AudioUi.Item("Export OPML…", () => _ = ExportOpmlAsync(Model, Window.GetWindow(this))));
        menu.Items.Add(AudioUi.Item("Refresh every show", () => _ = Try(async () =>
        {
            await Model.Client.RefreshAllPodcastsAsync();
            Notice("Checking every feed for new episodes…");
        })));
        menu.Items.Add(new Separator());
        menu.Items.Add(AudioUi.Item("My clips", () => Navigator.Push(new PodcastClipsView(Model, Navigator), "Clips")));
        menu.Items.Add(AudioUi.Item("Stats & Wrapped", () => Navigator.Push(new AudioStatsView(Model, Navigator), "Listening stats")));
        menu.Items.Add(AudioUi.Item("Wake-up alarms…", () => new WakeAlarmsWindow(Model) { Owner = Window.GetWindow(this) }.ShowDialog()));
        var button = AudioUi.TextButton("More ▾", () => { });
        button.Click += (_, _) =>
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        };
        return button;
    }

    private void DrawTabs() => _tabs.Content = AudioUi.Tabs(TabList, _tab, key =>
    {
        _tab = key;
        DrawTabs();
        _ = ReloadAsync();
    });

    private async Task ReloadAsync()
    {
        var generation = ++_generation;
        Notice(null);
        try
        {
            switch (_tab)
            {
                case "home":
                {
                    Loading();
                    var home = await Model.Client.PodcastHomeAsync();
                    if (generation == _generation) RenderHome(home);
                    break;
                }
                case "upnext":
                {
                    Loading();
                    var queue = await Model.Client.PodcastQueueAsync();
                    if (generation == _generation) RenderUpNext(queue);
                    break;
                }
                case "library":
                {
                    Loading();
                    var shows = await Model.Client.PodcastsAsync();
                    if (generation == _generation) RenderLibrary(shows);
                    break;
                }
                case "discover":
                    await RenderDiscoverAsync(generation);
                    break;
                case "search":
                    RenderSearchBox("Search every transcript for words that were said - then play from that moment.");
                    break;
                case "bookmarks":
                {
                    Loading();
                    var bookmarks = await Model.Client.BookmarksAsync();
                    if (generation == _generation) RenderBookmarks(bookmarks);
                    break;
                }
            }
        }
        catch (Exception error)
        {
            if (generation != _generation) return;
            Host.Children.Clear();
            Host.Children.Add(AudioUi.Title("Couldn't load podcasts", 16));
            Host.Children.Add(AudioUi.Caption(AppModel.UserFacingMessage(error), 6));
        }
    }

    private void Reload() => _ = ReloadAsync();

    private Task PlayAsync(IReadOnlyList<PodcastEpisode> list, int index, string name) =>
        Player.PlayEpisodesAsync(list, index, new AudioContext { Type = "queue", Name = name });

    // ---------------------------------------------------------------- Listen Now

    private void RenderHome(PodcastHome home)
    {
        Host.Children.Clear();
        _status.Text = $"{AudioUi.Count(home.Subscriptions.Count, "show", "shows")}" +
                       (home.TimeSavedSeconds > 60 ? $" · {AudioTime.Length(home.TimeSavedSeconds)} saved by speed and skips" : "");
        if (home.Subscriptions.Count == 0 && home.UpNext.Count == 0 && home.NewEpisodes.Count == 0)
        {
            Host.Children.Add(AudioUi.Title("Nothing to listen to yet", 16));
            Host.Children.Add(AudioUi.Caption("Find shows under Discover, add one by its feed address, or import an OPML file from another podcast app.", 6));
            Host.Children.Add(AudioUi.Row(
                AudioUi.TextButton("Discover", () =>
                {
                    _tab = "discover";
                    DrawTabs();
                    Reload();
                }, primary: true, margin: new Thickness(0, 10, 8, 0)),
                AudioUi.TextButton("Import OPML…", () => _ = ImportOpmlAsync(Model, Window.GetWindow(this), Reload), margin: new Thickness(0, 10, 0, 0))));
            return;
        }

        EpisodeSection("Up Next", home.UpNext, 5);
        EpisodeSection("Continue listening", home.ContinueListening, 5);
        EpisodeSection("New episodes", home.NewEpisodes, 10);
        var shorts = AudioUi.Shelf("Short ones (under 20 minutes)", home.ShortOnes.Select((episode, index) => (UIElement)AudioUi.Card(
            AudioUi.Art(episode.ArtUrl, 130, AudioUi.PodcastGlyph), episode.Title, $"{episode.PodcastTitle} · {AudioTime.Length(episode.DurationSeconds)}",
            () => _ = PlayAsync(home.ShortOnes, index, "Short ones"), 130, PodcastUi.EpisodeMenu(Model, Navigator, episode, Reload))), Scroller);
        if (shorts is not null) Host.Children.Add(shorts);
        EpisodeSection("Downloaded", home.Downloaded, 5);
        var shows = AudioUi.Shelf("Your shows", home.Subscriptions.Select(show => (UIElement)PodcastUi.ShowCard(Model, Navigator, show, 130)), Scroller);
        if (shows is not null) Host.Children.Add(shows);
        if (home.Bookmarks.Count > 0)
        {
            Host.Children.Add(AudioUi.Heading("Recent bookmarks"));
            foreach (var bookmark in home.Bookmarks.Take(5)) Host.Children.Add(BookmarkRow(bookmark));
        }
    }

    private void EpisodeSection(string heading, List<PodcastEpisode> episodes, int show)
    {
        if (episodes.Count == 0) return;
        Host.Children.Add(AudioUi.Heading(heading));
        for (var i = 0; i < Math.Min(show, episodes.Count); i++)
        {
            var index = i;
            Host.Children.Add(PodcastUi.EpisodeRow(Model, Navigator, episodes[i], () => _ = PlayAsync(episodes, index, heading), Reload));
        }
        if (episodes.Count > show)
        {
            var all = new Button { Content = $"See all {episodes.Count}", Style = AudioUi.StyleOf("LinkButton") };
            all.Click += (_, _) =>
            {
                var index = Host.Children.IndexOf(all);
                Host.Children.Remove(all);
                for (var i = show; i < episodes.Count; i++)
                {
                    var n = i;
                    Host.Children.Insert(index++, PodcastUi.EpisodeRow(Model, Navigator, episodes[i], () => _ = PlayAsync(episodes, n, heading), Reload));
                }
            };
            Host.Children.Add(all);
        }
    }

    // ---------------------------------------------------------------- Up Next

    private void RenderUpNext(List<PodcastEpisode> queue)
    {
        Host.Children.Clear();
        _status.Text = $"{AudioUi.Count(queue.Count, "episode", "episodes")} · {AudioTime.Length(queue.Sum(e => e.RemainingSeconds))}";
        if (queue.Count == 0)
        {
            Host.Children.Add(AudioUi.Caption("Up Next is empty. Add episodes from any episode's … menu, or turn on \"Add new episodes to Up Next\" in a show's settings."));
            return;
        }
        Host.Children.Add(AudioUi.Row(
            AudioUi.TextButton("▶  Play Up Next", () => _ = PlayAsync(queue, 0, "Up Next"), primary: true),
            AudioUi.TextButton("Clear", () => _ = Try(async () =>
            {
                if (!AudioUi.Confirm("Clear Up Next", "Take every episode out of Up Next?")) return;
                await Model.Client.SetPodcastQueueAsync(Array.Empty<string>());
                Reload();
            }))));
        for (var i = 0; i < queue.Count; i++)
        {
            var index = i;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(PodcastUi.EpisodeRow(Model, Navigator, queue[i], () => _ = PlayAsync(queue, index, "Up Next"), Reload));
            var moves = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var up = AudioUi.GlyphButton("", "Move up", () => _ = MoveAsync(queue, index, index - 1), 11);
            up.IsEnabled = i > 0;
            var down = AudioUi.GlyphButton("", "Move down", () => _ = MoveAsync(queue, index, index + 1), 11);
            down.IsEnabled = i < queue.Count - 1;
            moves.Children.Add(up);
            moves.Children.Add(down);
            Grid.SetColumn(moves, 1);
            row.Children.Add(moves);
            Host.Children.Add(row);
        }
    }

    private async Task MoveAsync(List<PodcastEpisode> queue, int from, int to) => await Try(async () =>
    {
        var ids = queue.Select(e => e.Id).ToList();
        var id = ids[from];
        ids.RemoveAt(from);
        ids.Insert(to, id);
        RenderUpNext(await Model.Client.SetPodcastQueueAsync(ids));
    });

    // ---------------------------------------------------------------- Library

    private void RenderLibrary(List<Podcast> shows)
    {
        Host.Children.Clear();
        _status.Text = AudioUi.Count(shows.Count, "show", "shows");
        if (shows.Count == 0)
        {
            Host.Children.Add(AudioUi.Caption("You don't follow any shows yet."));
            return;
        }
        var grid = new WrapPanel();
        foreach (var show in shows) grid.Children.Add(PodcastUi.ShowCard(Model, Navigator, show));
        Host.Children.Add(grid);
        if (Model.IsAdmin)
        {
            var all = new Button { Content = "Every show the household has…", Style = AudioUi.StyleOf("LinkButton"), Margin = new Thickness(0, 8, 0, 0) };
            all.Click += async (_, _) => await Try(async () =>
            {
                var everything = await Model.Client.PodcastsAsync(all: true);
                Host.Children.Remove(all);
                Host.Children.Add(AudioUi.Heading("Everyone's shows"));
                var others = new WrapPanel();
                foreach (var show in everything.Where(s => !s.Subscribed)) others.Children.Add(PodcastUi.ShowCard(Model, Navigator, show, 130));
                Host.Children.Add(others);
            });
            Host.Children.Add(all);
        }
    }

    // ---------------------------------------------------------------- Discover

    private async Task RenderDiscoverAsync(int generation)
    {
        Host.Children.Clear();
        var box = new TextBox { Width = 320, ToolTip = "Search the Apple Podcasts directory" };
        var results = new StackPanel();
        async Task Find()
        {
            var q = box.Text.Trim();
            if (q.Length == 0) return;
            results.Children.Clear();
            results.Children.Add(AudioUi.Caption("Searching…"));
            try
            {
                var shows = await Model.Client.PodcastDirectorySearchAsync(q);
                results.Children.Clear();
                if (shows.Count == 0) results.Children.Add(AudioUi.Caption("No shows found."));
                var grid = new WrapPanel();
                foreach (var show in shows) grid.Children.Add(PodcastUi.DirectoryCard(Model, Navigator, show));
                results.Children.Add(grid);
            }
            catch (Exception error)
            {
                results.Children.Clear();
                results.Children.Add(AudioUi.Caption(AppModel.UserFacingMessage(error)));
            }
        }
        box.KeyDown += (_, args) =>
        {
            if (args.Key != System.Windows.Input.Key.Enter) return;
            args.Handled = true;
            _ = Find();
        };
        Host.Children.Add(AudioUi.Row(box, AudioUi.TextButton("Search", () => _ = Find(), primary: true, margin: new Thickness(8, 0, 0, 0))));
        Host.Children.Add(results);

        try
        {
            var recommended = await Model.Client.PodcastRecommendedAsync();
            if (generation != _generation) return;
            var shelf = AudioUi.Shelf("Recommended for the household", recommended.Select(show => (UIElement)PodcastUi.DirectoryCard(Model, Navigator, show, 130)), Scroller);
            if (shelf is not null) Host.Children.Add(shelf);
        }
        catch (Exception)
        {
            // Recommendations are an extra.
        }

        try
        {
            var genres = await Model.Client.PodcastGenresAsync();
            var top = await Model.Client.PodcastTopShowsAsync(_genre);
            if (generation != _generation) return;
            Host.Children.Add(AudioUi.Heading("Top shows"));
            var options = new List<string> { "All genres" };
            options.AddRange(genres.Select(g => g.Name));
            var picker = new ComboBox { Width = 220, ItemsSource = options, Margin = new Thickness(0, 0, 0, 12), HorizontalAlignment = HorizontalAlignment.Left };
            picker.SelectedIndex = _genre is { } g ? Math.Max(0, genres.FindIndex(x => x.Id == g) + 1) : 0;
            picker.SelectionChanged += (_, _) =>
            {
                _genre = picker.SelectedIndex <= 0 ? null : genres[picker.SelectedIndex - 1].Id;
                Reload();
            };
            Host.Children.Add(picker);
            var grid = new WrapPanel();
            foreach (var (show, i) in top.Select((s, i) => (s, i)))
            {
                var card = PodcastUi.DirectoryCard(Model, Navigator, show, 130);
                card.ToolTip = $"#{i + 1} {show.Title}";
                grid.Children.Add(card);
            }
            Host.Children.Add(grid);
        }
        catch (Exception error)
        {
            if (generation == _generation) Notice(AppModel.UserFacingMessage(error));
        }
    }

    private async Task AddByUrlAsync()
    {
        var url = AudioUi.Prompt("Follow a show", "The show's RSS feed address");
        if (url is null) return;
        await Try(async () =>
        {
            var show = await Model.Client.SubscribeAsync(url);
            if (show.Id is { } id) AudioNav.OpenShow(Model, Navigator, id, show.Title);
        });
    }

    // ---------------------------------------------------------------- Transcript search

    private void RenderSearchBox(string hint)
    {
        Host.Children.Clear();
        Host.Children.Add(AudioUi.Caption(hint));
        Host.Children.Add(AudioUi.Row(_search));
        Host.Children.Add(_searchResults);
        _search.Focus();
    }

    private readonly StackPanel _searchResults = new();

    private async Task SearchAsync()
    {
        var q = _search.Text.Trim();
        _searchResults.Children.Clear();
        if (q.Length < 2) return;
        try
        {
            var hits = await Model.Client.SearchTranscriptsAsync(q);
            if (_search.Text.Trim() != q) return;
            if (hits.Count == 0) _searchResults.Children.Add(AudioUi.Caption("Nobody said that in any transcript."));
            foreach (var hit in hits)
            {
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = $"{hit.PodcastTitle} · {hit.EpisodeTitle}", Foreground = Palette.PrimaryText, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock { Text = $"{AudioTime.Clock(hit.StartSeconds)}  “{hit.Snippet}”", Foreground = Palette.SecondaryText, TextWrapping = TextWrapping.Wrap });
                var target = hit;
                _searchResults.Children.Add(AudioUi.ClickableRow(text, () => _ = PlayAtAsync(target.EpisodeId, target.StartSeconds), null, hit.EpisodeTitle ?? "Episode"));
            }
            if (hits.Count > 0) _searchResults.Children.Add(AudioUi.Caption("Double-click a line to play from that moment.", 6));
        }
        catch (Exception error)
        {
            _searchResults.Children.Add(AudioUi.Caption(AppModel.UserFacingMessage(error)));
        }
    }

    private async Task PlayAtAsync(string episodeId, double seconds) => await Try(async () =>
    {
        var episode = await Model.Client.PodcastEpisodeAsync(episodeId);
        await Player.PlayEpisodesAsync(new[] { episode }, 0, new AudioContext { Type = "podcast", Id = episode.PodcastId, Name = episode.PodcastTitle }, seconds);
    });

    // ---------------------------------------------------------------- Bookmarks

    private void RenderBookmarks(List<PodcastBookmark> bookmarks)
    {
        Host.Children.Clear();
        _status.Text = AudioUi.Count(bookmarks.Count, "bookmark", "bookmarks");
        if (bookmarks.Count == 0) Host.Children.Add(AudioUi.Caption("No bookmarks yet. Press the bookmark button while an episode plays to keep a moment."));
        foreach (var bookmark in bookmarks) Host.Children.Add(BookmarkRow(bookmark));
    }

    private FrameworkElement BookmarkRow(PodcastBookmark bookmark) =>
        BookmarkUi.Row(Model, bookmark, () => _ = PlayAtAsync(bookmark.EpisodeId, bookmark.PositionSeconds), Reload, showEpisode: true);

    // ---------------------------------------------------------------- Fill my drive

    private async Task FillAsync()
    {
        var answer = AudioUi.Prompt("Fill my drive", "How many minutes do you have?", "30");
        if (answer is null) return;
        if (!int.TryParse(answer.Trim(), out var minutes) || minutes <= 0)
        {
            Notice("Type a number of minutes, like 45.");
            return;
        }
        await Try(async () =>
        {
            var fill = await Model.Client.PodcastFillAsync(minutes);
            if (fill.Episodes.Count == 0)
            {
                Notice("Nothing unplayed fits in that time.");
                return;
            }
            await Player.PlayEpisodesAsync(fill.Episodes, 0, new AudioContext { Type = "queue", Name = $"Fill my drive · {minutes} min" });
            Notice($"Playing {AudioUi.Count(fill.Episodes.Count, "episode", "episodes")} · {AudioTime.Length(fill.TotalSeconds)}.");
        });
    }

    // ---------------------------------------------------------------- OPML (also used by Settings)

    public static async Task ImportOpmlAsync(AppModel model, Window? owner, Action? done)
    {
        var dialog = new OpenFileDialog { Filter = "OPML files|*.opml;*.xml|All files|*.*", Title = "Import podcast subscriptions" };
        if (dialog.ShowDialog(owner) != true) return;
        try
        {
            var text = await File.ReadAllTextAsync(dialog.FileName);
            var result = await model.Client.ImportOpmlAsync(text);
            var message = $"Added {result.Added}, already following {result.Existing}." +
                          (result.Failed.Count > 0 ? $"\n\n{result.Failed.Count} couldn't be added:\n" + string.Join("\n", result.Failed.Take(10).Select(f => $"{f.FeedUrl}: {f.Error}")) : "") +
                          "\n\nThe feeds are fetched in the background, so the shows appear over the next minute or two.";
            MessageBox.Show(owner, message, "OPML import", MessageBoxButton.OK, MessageBoxImage.Information);
            done?.Invoke();
        }
        catch (Exception error)
        {
            MessageBox.Show(owner, AppModel.UserFacingMessage(error), "OPML import", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static async Task ExportOpmlAsync(AppModel model, Window? owner)
    {
        var dialog = new SaveFileDialog { FileName = "Podcasts.opml", Filter = "OPML files|*.opml|All files|*.*", Title = "Export podcast subscriptions" };
        if (dialog.ShowDialog(owner) != true) return;
        try
        {
            await File.WriteAllBytesAsync(dialog.FileName, await model.Client.ExportOpmlAsync());
        }
        catch (Exception error)
        {
            MessageBox.Show(owner, AppModel.UserFacingMessage(error), "OPML export", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

/// <summary>A bookmark's row: when, the note, the transcript snippet, and its actions.</summary>
public static class BookmarkUi
{
    public static FrameworkElement Row(AppModel model, PodcastBookmark bookmark, Action play, Action changed, bool showEpisode)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        var head = AudioTime.Clock(bookmark.PositionSeconds) + (showEpisode ? $"  {bookmark.PodcastTitle} · {bookmark.EpisodeTitle}" : "");
        text.Children.Add(new TextBlock { Text = head, Foreground = Palette.Cyan, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrWhiteSpace(bookmark.Note)) text.Children.Add(new TextBlock { Text = bookmark.Note, Foreground = Palette.PrimaryText, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(bookmark.TranscriptSnippet)) text.Children.Add(new TextBlock { Text = $"“{bookmark.TranscriptSnippet}”", Foreground = Palette.SecondaryText, FontStyle = FontStyles.Italic, TextWrapping = TextWrapping.Wrap });
        grid.Children.Add(text);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        buttons.Children.Add(AudioUi.GlyphButton(AudioUi.PlayGlyph, "Play from here", play, 12, Palette.Green));
        var menu = new ContextMenu();
        menu.Items.Add(AudioUi.Item("Edit note…", async () =>
        {
            var note = AudioUi.Prompt("Bookmark note", "Note", bookmark.Note ?? "");
            if (note is null) return;
            await Run(model, () => model.Client.UpdateBookmarkAsync(bookmark.Id, note));
            changed();
        }));
        menu.Items.Add(AudioUi.Item("Save to Note", async () =>
        {
            if (await Run(model, () => model.Client.BookmarkToNoteAsync(bookmark.Id)))
            {
                MessageBox.Show(Application.Current.MainWindow, "Saved to a note in the Organizer.", "Bookmark", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(AudioUi.Item("Delete", async () =>
        {
            await Run(model, () => model.Client.DeleteBookmarkAsync(bookmark.Id));
            changed();
        }));
        var more = AudioUi.GlyphButton(AudioUi.MoreGlyph, "More", () => { }, 12);
        more.Click += (_, _) =>
        {
            menu.PlacementTarget = more;
            menu.IsOpen = true;
        };
        buttons.Children.Add(more);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        return AudioUi.ClickableRow(grid, play, menu, $"Bookmark at {AudioTime.Clock(bookmark.PositionSeconds)}");
    }

    private static async Task<bool> Run(AppModel model, Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception error)
        {
            // 404 on to-note means the Organizer is off; the server's sentence says so.
            MessageBox.Show(Application.Current.MainWindow, AppModel.UserFacingMessage(error), "Bookmark", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }
}

/// <summary>The clips you made: play, save, delete.</summary>
public sealed class PodcastClipsView : AudioPage
{
    public PodcastClipsView(AppModel model, INavigator navigator) : base(model, navigator)
    {
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Loading();
        try
        {
            var clips = await Model.Client.ClipsAsync();
            Host.Children.Clear();
            Host.Children.Add(AudioUi.Title("Clips"));
            if (clips.Count == 0) Host.Children.Add(AudioUi.Caption("No clips yet. Make one from the Now Playing screen while an episode plays.", 6));
            foreach (var clip in clips)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                row.Children.Add(new TextBlock { Text = $"{(string.IsNullOrWhiteSpace(clip.Title) ? "Clip" : clip.Title)} · {AudioTime.Clock(clip.DurationSeconds)}", Foreground = Palette.PrimaryText, VerticalAlignment = VerticalAlignment.Center, Width = 360, TextTrimming = TextTrimming.CharacterEllipsis });
                row.Children.Add(AudioUi.TextButton("Save…", () => _ = AudioUi.SaveFileAsync(clip.Url, string.IsNullOrWhiteSpace(clip.Title) ? "Clip" : clip.Title!, ".m4a")));
                var target = clip;
                var delete = AudioUi.TextButton("Delete", () => _ = Try(async () =>
                {
                    await Model.Client.DeleteClipAsync(target.Id);
                    await LoadAsync();
                }));
                delete.Style = AudioUi.StyleOf("DangerButton");
                row.Children.Add(delete);
                Host.Children.Add(row);
            }
        }
        catch (Exception error)
        {
            Host.Children.Clear();
            Fail(error);
        }
    }
}
