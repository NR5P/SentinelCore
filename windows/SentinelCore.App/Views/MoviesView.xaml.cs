using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SentinelCore.App.Design;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>
/// The family film and TV library: a Home tab of shelves (continue watching, My List, recently
/// added, most played, genres) under a featured hero, Movies / TV Shows grids with search, sort,
/// genre, unwatched and My List filters, and Surprise me for when nobody can decide.
/// </summary>
/// <remarks>
/// The whole contract is core/docs/MOVIES.md. The section only exists while the server has Movies
/// &amp; TV switched on (AppModel.MoviesEnabled). A card opens the detail page; a Continue
/// watching card resumes straight away, which is what that row is for.
/// </remarks>
public partial class MoviesView : UserControl
{
    private sealed record SortOption(string Label, string Value, string DefaultOrder)
    {
        public override string ToString() => Label;
    }

    // Year, rating and plays default to newest, best and most first - nobody sorts by year to find 1931.
    private static readonly SortOption[] Sorts =
    {
        new("Title", "title", "asc"),
        new("Recently added", "added", "desc"),
        new("Year", "year", "desc"),
        new("Rating", "rating", "desc"),
        new("Most played", "plays", "desc"),
    };

    private const int PageSize = 60;
    private const string AllGenres = "All genres";

    private readonly AppModel _model;
    private readonly INavigator _navigator;
    private readonly DispatcherTimer _searchDelay;

    private string _tab = "home"; // home | movie | show
    private string _order = "asc";
    private readonly List<MediaItemSummary> _items = new();
    private int _total;
    private WrapPanel? _grid;
    private Button? _loadMore;

    // Bumped by every reload; an answer that arrives for an older one is dropped, so typing fast
    // in the search box never paints a stale page over a newer one.
    private int _generation;
    private bool _suppressFilterEvents = true;
    private bool _loadedOnce;

    public MoviesView(AppModel model, INavigator? navigator = null)
    {
        _model = model;
        _navigator = navigator ?? NullNavigator.Instance;
        InitializeComponent();

        SortPicker.ItemsSource = Sorts;
        SortPicker.SelectedIndex = 0;
        GenrePicker.ItemsSource = new List<string> { AllGenres };
        GenrePicker.SelectedIndex = 0;
        UpdateOrderButton();
        UpdateTabs();
        _suppressFilterEvents = false;

        _searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            _ = ReloadAsync();
        };

        Loaded += (_, _) =>
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            _ = ReloadAsync();
        };

        // Back from a film: Continue watching has moved on. The grids keep their scroll and pages.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && _loadedOnce && IsHome) _ = ReloadAsync();
        };
    }

    private string Query => SearchBox.Text.Trim();

    private bool IsHome => _tab == "home" && Query.Length == 0;

    // ---------------------------------------------------------------- Loading

    private async Task ReloadAsync()
    {
        var generation = ++_generation;
        FilterBar.Visibility = IsHome ? Visibility.Collapsed : Visibility.Visible;
        NoticeText.Visibility = Visibility.Collapsed;
        if (IsHome) await LoadHomeAsync(generation);
        else await LoadGridAsync(generation, reset: true);
    }

    private async Task LoadHomeAsync(int generation)
    {
        try
        {
            var statusTask = TryStatusAsync();
            var home = await _model.Client.MediaHomeAsync();
            var status = await statusTask;
            if (generation != _generation) return;
            RenderHome(home);
            SetStatus(home.MovieCount, home.ShowCount, status);
        }
        catch (Exception error)
        {
            if (generation != _generation) return;
            ShowFailure(error);
        }
    }

    private async Task<MediaStatus?> TryStatusAsync()
    {
        try
        {
            return await _model.Client.MediaStatusAsync();
        }
        catch (Exception)
        {
            // Only the caption's scan line depends on it.
            return null;
        }
    }

    private async Task LoadGridAsync(int generation, bool reset)
    {
        var kind = _tab == "home" ? null : _tab;
        var sort = (SortPicker.SelectedItem as SortOption)?.Value ?? "title";
        var genre = GenrePicker.SelectedItem is string picked && picked != AllGenres ? picked : null;
        var offset = reset ? 0 : _items.Count;

        if (_loadMore is not null) _loadMore.IsEnabled = false;
        try
        {
            var page = await _model.Client.MediaItemsAsync(
                kind, Query, genre, sort, _order, UnwatchedBox.IsChecked == true, offset, PageSize,
                inList: MyListBox.IsChecked == true);
            if (generation != _generation) return;

            if (reset)
            {
                _items.Clear();
                ContentHost.Children.Clear();
                Scroller.ScrollToTop();
                _grid = new WrapPanel();
                ContentHost.Children.Add(_grid);
                _loadMore = new Button { Content = "Load more", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
                _loadMore.Click += async (_, _) => await LoadGridAsync(_generation, reset: false);
                ContentHost.Children.Add(_loadMore);
                UpdateGenres(page.Genres);
            }

            _items.AddRange(page.Items);
            _total = page.Total;
            foreach (var item in page.Items) _grid!.Children.Add(MovieCards.PosterCard(this, item, () => Open(item)));

            _loadMore!.Visibility = _items.Count < _total ? Visibility.Visible : Visibility.Collapsed;
            _loadMore.IsEnabled = true;
            CountText.Text = _total == 1 ? "1 title" : $"{_total} titles";

            if (_total == 0)
            {
                ShowEmpty(Query.Length > 0 ? $"Nothing matches “{Query}”" : "Nothing here",
                    UnwatchedBox.IsChecked == true || MyListBox.IsChecked == true || genre is not null
                        ? "Try another genre, or turn off Unwatched only and My List."
                        : "The server has not found any titles of this kind yet.");
            }
            else
            {
                EmptyState.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception error)
        {
            if (generation != _generation) return;
            if (_loadMore is not null) _loadMore.IsEnabled = true;
            ShowFailure(error);
        }
    }

    private void UpdateGenres(IReadOnlyList<string> genres)
    {
        var current = GenrePicker.SelectedItem as string ?? AllGenres;
        var list = new List<string> { AllGenres };
        list.AddRange(genres.OrderBy(genre => genre, StringComparer.OrdinalIgnoreCase));
        _suppressFilterEvents = true;
        GenrePicker.ItemsSource = list;
        GenrePicker.SelectedItem = list.Contains(current) ? current : AllGenres;
        _suppressFilterEvents = false;
    }

    private void SetStatus(int movieCount, int showCount, MediaStatus? status)
    {
        var parts = new List<string>
        {
            movieCount == 1 ? "1 movie" : $"{movieCount} movies",
            showCount == 1 ? "1 show" : $"{showCount} shows",
        };
        if (status is { Scanning: true })
        {
            var scan = status.Scan;
            var where = string.IsNullOrWhiteSpace(scan?.LibraryName) ? "" : $" {scan!.LibraryName}";
            var count = scan is { Total: > 0 } ? $" ({scan.Processed} of {scan.Total})" : "";
            parts.Add($"scanning{where}{count}");
        }
        StatusText.Text = string.Join(" · ", parts);
    }

    private void ShowFailure(Exception error)
    {
        var message = AppModel.UserFacingMessage(error);
        StatusText.Text = "The library could not be loaded.";
        if (ContentHost.Children.Count == 0) ShowEmpty("Could not load the library", message);
        else Notice(message);
    }

    private void ShowEmpty(string title, string message, bool offerLibraries = false)
    {
        EmptyTitle.Text = title;
        EmptyMessage.Text = message;
        EmptyLibrariesButton.Visibility = offerLibraries && _model.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
    }

    private async void OnEmptyLibrariesClicked(object sender, RoutedEventArgs e)
    {
        new MediaLibrariesWindow(_model) { Owner = Window.GetWindow(this) }.ShowDialog();
        await ReloadAsync();
    }

    private void Notice(string message)
    {
        NoticeText.Text = message;
        NoticeText.Visibility = Visibility.Visible;
    }

    // ---------------------------------------------------------------- Home

    private void RenderHome(MediaHome home)
    {
        _grid = null;
        _loadMore = null;
        ContentHost.Children.Clear();

        var nothing = home.Featured is null
            && home.ContinueWatching.Count == 0 && home.RecentMovies.Count == 0
            && home.RecentShows.Count == 0 && home.RecentEpisodes.Count == 0
            && home.MyList.Count == 0 && home.MostPlayed.Count == 0 && home.RecentlyWatched.Count == 0
            && home.TopRated.Count == 0 && home.GenreShelves.All(shelf => shelf.Items.Count == 0);
        if (nothing)
        {
            if (home.Libraries.Count == 0)
            {
                ShowEmpty("No libraries yet",
                    _model.IsAdmin
                        ? "Add the folders your movies and TV shows are in under Settings → Movies & TV libraries. " +
                          "They can be on the server itself or shared by another computer on the network."
                        : "An admin adds the movie and TV folders under Settings → Movies & TV libraries.",
                    offerLibraries: true);
            }
            else
            {
                ShowEmpty("Nothing here yet",
                    "The server is still scanning, or the library folders hold no video files it recognises.");
            }
            return;
        }
        EmptyState.Visibility = Visibility.Collapsed;

        if (home.Featured is { } featured) ContentHost.Children.Add(Hero(featured));

        // The order MOVIES.md suggests; an empty row is left out, and so is every row a server
        // older than it does not send.
        AddShelf("Continue watching", home.ContinueWatching, resume: true);
        AddShelf("My List", home.MyList, resume: false);
        AddShelf("Recently added movies", home.RecentMovies, resume: false);
        AddShelf("Recently added shows", home.RecentShows, resume: false);
        AddShelf("New episodes", home.RecentEpisodes, resume: false);
        AddShelf("Most played", home.MostPlayed, resume: false);
        AddShelf("Top rated", home.TopRated, resume: false);
        foreach (var shelf in home.GenreShelves) AddShelf(shelf.Genre, shelf.Items, resume: false);
        AddShelf("Watch again", home.RecentlyWatched, resume: false);
    }

    private void AddShelf(string heading, List<MediaItemSummary> items, bool resume)
    {
        if (items.Count == 0) return;
        var cards = items.Select(item => (UIElement)MovieCards.PosterCard(this, item, () =>
        {
            // Continue watching exists to pick up where you were, so it plays; every other card
            // opens the detail page first.
            if (resume) Play(item.Id, null, item.IsEpisode ? $"{item.ShowTitle} · {item.EpisodeLabel}" : item.Title);
            else Open(item);
        }));
        ContentHost.Children.Add(MovieCards.Shelf(this, heading, cards, Scroller));
    }

    private FrameworkElement Hero(MediaItemSummary featured)
    {
        var hero = new Grid { Height = 320, Margin = new Thickness(0, 0, 0, 18) };
        hero.SizeChanged += (_, args) =>
            hero.Clip = new RectangleGeometry(new Rect(args.NewSize), 10, 10);

        hero.Children.Add(new Border { Background = (Brush)FindResource("CardBackground") });
        if (!string.IsNullOrEmpty(featured.BackdropUrl))
        {
            hero.Children.Add(new RemoteImage
            {
                DecodeWidth = 1280,
                Stretch = Stretch.UniformToFill,
                SourcePath = featured.BackdropUrl,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        // Dark to the left and the bottom, so the text reads over any picture.
        hero.Children.Add(new Rectangle
        {
            Fill = new LinearGradientBrush(Color.FromArgb(0xF0, 0x14, 0x18, 0x1C), Color.FromArgb(0x10, 0x14, 0x18, 0x1C), 0),
        });
        hero.Children.Add(new Rectangle
        {
            Fill = new LinearGradientBrush(Color.FromArgb(0x00, 0x14, 0x18, 0x1C), Color.FromArgb(0xC0, 0x14, 0x18, 0x1C), 90),
        });

        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(28, 0, 28, 26),
            MaxWidth = 620,
        };
        text.Children.Add(new TextBlock
        {
            Text = featured.Title,
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.PrimaryText,
            TextWrapping = TextWrapping.Wrap,
        });
        var meta = MediaPlaybackRules.MetaLine(featured);
        if (!string.IsNullOrEmpty(featured.Certification)) meta = meta.Length > 0 ? $"{featured.Certification} · {meta}" : featured.Certification!;
        if (featured.Genres.Count > 0) meta = meta.Length > 0 ? $"{meta} · {string.Join(", ", featured.Genres.Take(3))}" : string.Join(", ", featured.Genres.Take(3));
        text.Children.Add(new TextBlock
        {
            Text = meta,
            Foreground = Palette.SecondaryText,
            Margin = new Thickness(0, 6, 0, 0),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        var canResume = MediaPlaybackRules.CanResume(featured.Progress);
        var play = new Button
        {
            Content = featured.IsShow ? "▶  Play" : canResume ? $"▶  {MediaPlaybackRules.ResumeLabel(featured.Progress)}" : "▶  Play",
            Style = (Style)FindResource("PrimaryButton"),
            Padding = new Thickness(16, 8, 16, 8),
        };
        play.Click += (_, _) =>
        {
            if (featured.IsShow)
            {
                if (featured.NextEpisodeId is { Length: > 0 } next) Play(next, null, featured.Title);
                else Open(featured);
            }
            else
            {
                Play(featured.Id, MediaPlaybackRules.StartFor(featured.Progress, startOver: false), featured.Title);
            }
        };
        buttons.Children.Add(play);
        var info = new Button { Content = "More info", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(16, 8, 16, 8) };
        info.Click += (_, _) => Open(featured);
        buttons.Children.Add(info);
        text.Children.Add(buttons);

        hero.Children.Add(text);
        return hero;
    }

    // ---------------------------------------------------------------- Navigation

    private void Open(MediaItemSummary item)
    {
        var title = item.IsEpisode && !string.IsNullOrWhiteSpace(item.ShowTitle) ? $"{item.ShowTitle} · {item.EpisodeLabel}" : item.Title;
        _navigator.Push(new MovieDetailView(_model, _navigator, item.Id), title);
    }

    // Scoped to the tab and genre on screen, so Surprise me on TV Shows picks a show. A 404 means
    // nothing matched, and its message says so.
    private async void OnSurpriseClicked(object sender, RoutedEventArgs e)
    {
        var kind = _tab == "home" ? null : _tab;
        var genre = !IsHome && GenrePicker.SelectedItem is string picked && picked != AllGenres ? picked : null;
        SurpriseButton.IsEnabled = false;
        try
        {
            var item = await _model.Client.GetRandomMediaAsync(kind, genre: genre);
            NoticeText.Visibility = Visibility.Collapsed;
            Open(item);
        }
        catch (Exception error)
        {
            Notice(AppModel.UserFacingMessage(error));
        }
        finally
        {
            SurpriseButton.IsEnabled = true;
        }
    }

    // Home is reloaded once the player's last progress save has landed, so Continue watching shows
    // where the film was left rather than where it was before.
    private void Play(string itemId, double? start, string title) =>
        _navigator.Push(new MoviePlayerView(_model, _navigator, itemId, start, () =>
        {
            if (IsHome) _ = ReloadAsync();
        }), title);

    // ---------------------------------------------------------------- Controls

    private void UpdateTabs()
    {
        foreach (var tab in new[] { HomeTab, MoviesTab, ShowsTab })
        {
            if ((string)tab.Tag == _tab) tab.Style = (Style)FindResource("PrimaryButton");
            else tab.ClearValue(StyleProperty);
        }
    }

    private void OnTabClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tab } || tab == _tab) return;
        _tab = tab;
        UpdateTabs();
        _ = ReloadAsync();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_searchDelay is null) return;
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => _ = ReloadAsync();

    private void UpdateOrderButton()
    {
        OrderButton.Content = _order == "asc" ? "↑" : "↓";
        OrderButton.ToolTip = _order == "asc" ? "Ascending - click for descending" : "Descending - click for ascending";
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortPicker.SelectedItem is SortOption sort) _order = sort.DefaultOrder;
        UpdateOrderButton();
        if (!_suppressFilterEvents && !IsHome) _ = ReloadAsync();
    }

    private void OnOrderClicked(object sender, RoutedEventArgs e)
    {
        _order = _order == "asc" ? "desc" : "asc";
        UpdateOrderButton();
        if (!IsHome) _ = ReloadAsync();
    }

    private void OnGenreChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressFilterEvents && !IsHome) _ = ReloadAsync();
    }

    private void OnUnwatchedChanged(object sender, RoutedEventArgs e)
    {
        if (!_suppressFilterEvents && !IsHome) _ = ReloadAsync();
    }

    private void OnMyListChanged(object sender, RoutedEventArgs e)
    {
        if (!_suppressFilterEvents && !IsHome) _ = ReloadAsync();
    }
}
