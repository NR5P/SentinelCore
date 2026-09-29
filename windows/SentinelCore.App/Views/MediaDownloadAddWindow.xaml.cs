using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SentinelCore.App.Design;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>
/// "+ Add" on the Movies screen: find a movie or show on TMDB, pick a season and episode for a
/// show, then pick one of the releases the household's torrent sites offer. The server downloads it
/// through its VPN and files it into the library.
/// </summary>
/// <remarks>
/// The contract is core/docs/MOVIE_DOWNLOADS.md and the design is the web dashboard's add dialog
/// (movies.js, "downloads (+ add)"). Three stages in one window - find, the show's episodes, the
/// releases - each a panel; Back goes one stage up. A release search is started with a POST and
/// polled every second until it is done or failed, showing the server's own message meanwhile.
/// Choosing a release closes the window with DialogResult true and <see cref="Grabbed"/> set, and
/// the caller opens Downloads.
/// </remarks>
public partial class MediaDownloadAddWindow : Window
{
    private static readonly TimeSpan SearchPollInterval = TimeSpan.FromSeconds(1);

    private enum Stage
    {
        Find,
        Show,
        Releases,
    }

    private readonly AppModel _model;
    private readonly MediaDownloadStatus _status;
    private readonly DispatcherTimer _searchPoll;

    private Stage _stage = Stage.Find;
    private string _kind = "movie"; // movie | show

    // From the last lookup; starts as the status's answer. False: search the sites directly.
    private bool _tmdb;
    private int _lookupGeneration;
    private List<MediaDownloadTitle>? _results;

    private MediaDownloadTitle? _pickedShow;
    private MediaDownloadShow? _show;
    private int? _season;
    private int _showGeneration;

    private MediaDownloadSearchInput? _searchInput;
    private Stage _searchBack = Stage.Find;
    private MediaDownloadSearch? _search;
    private int _searchGeneration;
    private bool _othersOpen;
    private readonly List<Button> _downloadButtons = new();

    public MediaDownloadAddWindow(AppModel model, MediaDownloadStatus? status)
    {
        _model = model;
        _status = status ?? MediaDownloadStatus.Off;
        _tmdb = _status.HasTmdb;
        InitializeComponent();

        _searchPoll = new DispatcherTimer(DispatcherPriority.Background) { Interval = SearchPollInterval };
        _searchPoll.Tick += async (_, _) => await PollSearchAsync();
        Closed += (_, _) =>
        {
            _searchPoll.Stop();
            _searchGeneration++;
            _lookupGeneration++;
            _showGeneration++;
        };

        UpdateKind();
        ShowStage(Stage.Find);
        Loaded += (_, _) => QueryBox.Focus();
    }

    /// <summary>The download started, once the window has closed with DialogResult true.</summary>
    public MediaDownload? Grabbed { get; private set; }

    private string Query => QueryBox.Text.Trim();

    // ---------------------------------------------------------------- Stages

    private void ShowStage(Stage stage)
    {
        _stage = stage;
        FindPanel.Visibility = stage == Stage.Find ? Visibility.Visible : Visibility.Collapsed;
        ShowPanel.Visibility = stage == Stage.Show ? Visibility.Visible : Visibility.Collapsed;
        ReleasesPanel.Visibility = stage == Stage.Releases ? Visibility.Visible : Visibility.Collapsed;
        HeadingText.Text = stage switch
        {
            Stage.Show => _show?.Title ?? _pickedShow?.DisplayTitle ?? "TV show",
            Stage.Releases => "Pick a download",
            _ => "Add a movie or show",
        };
        IntroText.Text = stage switch
        {
            Stage.Show => "Get one episode, or the whole season at once.",
            Stage.Releases => "The best match is first. The server downloads it through its VPN and files it into the library.",
            _ => "Find it, pick a download, and the server fetches it through its VPN and files it into the library.",
        };
        Message("");
    }

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        _searchPoll.Stop();
        _searchGeneration++;
        if (_stage == Stage.Releases && _searchBack == Stage.Show && _show is not null)
        {
            ShowStage(Stage.Show);
            return;
        }
        _show = null;
        _pickedShow = null;
        _showGeneration++;
        ShowStage(Stage.Find);
        QueryBox.Focus();
    }

    // ---------------------------------------------------------------- 1. Find

    private void UpdateKind()
    {
        foreach (var button in new[] { MovieKindButton, ShowKindButton })
        {
            if ((string)button.Tag == _kind) button.Style = (Style)FindResource("PrimaryButton");
            else button.ClearValue(StyleProperty);
        }
        QueryBox.ToolTip = _kind == "show" ? "Show title" : "Movie title";

        var warning = MediaDownloadText.NoLibraryWarning(_status, _kind == "show");
        NoLibraryText.Text = warning ?? "";
        NoLibraryText.Visibility = warning is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateDirect();
    }

    private void UpdateDirect()
    {
        var direct = (!_status.HasTmdb || !_tmdb) && Query.Length > 0;
        DirectPanel.Visibility = direct ? Visibility.Visible : Visibility.Collapsed;
        DirectNote.Visibility = _status.HasTmdb ? Visibility.Collapsed : Visibility.Visible;
        DirectEpisodeRow.Visibility = _kind == "show" ? Visibility.Visible : Visibility.Collapsed;
        DirectSearchButton.Content = $"Search sites for “{Query}”";
    }

    private void OnKindClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string kind } || kind == _kind) return;
        _kind = kind;
        _results = null;
        ResultsHost.Children.Clear();
        UpdateKind();
        if (Query.Length > 0) _ = LookupAsync();
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        UpdateDirect();
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = LookupAsync();
    }

    private void OnFindClicked(object sender, RoutedEventArgs e) => _ = LookupAsync();

    private async Task LookupAsync()
    {
        var query = Query;
        if (query.Length == 0) return;
        var generation = ++_lookupGeneration;
        var kind = _kind;

        ResultsHost.Children.Clear();
        SetResultsStatus(Palette.SecondaryText, "Searching…");
        FindButton.IsEnabled = false;
        try
        {
            var lookup = await _model.Client.LookupMediaDownloadAsync(query, kind);
            if (generation != _lookupGeneration) return;
            _tmdb = lookup.TmdbConfigured != false;
            _results = _tmdb ? lookup.Results : new List<MediaDownloadTitle>();
            UpdateDirect();
            RenderResults();
        }
        catch (Exception error)
        {
            if (generation != _lookupGeneration) return;
            _results = null;
            SetResultsStatus(Palette.Red, AppModel.UserFacingMessage(error));
        }
        finally
        {
            if (generation == _lookupGeneration) FindButton.IsEnabled = true;
        }
    }

    private void SetResultsStatus(Brush brush, string text)
    {
        ResultsStatus.Foreground = brush;
        ResultsStatus.Text = text;
        ResultsStatus.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderResults()
    {
        ResultsHost.Children.Clear();
        ResultsScroller.ScrollToTop();
        var results = _results ?? new List<MediaDownloadTitle>();
        if (!_tmdb)
        {
            SetResultsStatus(Palette.SecondaryText, "");
            return;
        }
        if (results.Count == 0)
        {
            SetResultsStatus(Palette.SecondaryText, "Nothing on TMDB called that. Try fewer words.");
            return;
        }
        SetResultsStatus(Palette.SecondaryText, "");
        foreach (var result in results) ResultsHost.Children.Add(ResultCard(result));
    }

    private FrameworkElement ResultCard(MediaDownloadTitle result)
    {
        var poster = MovieCards.Artwork(this, result.PosterUrl, result.DisplayTitle, 64, 96, 128);

        var titleLine = new WrapPanel();
        titleLine.Children.Add(new TextBlock
        {
            Text = result.DisplayTitle,
            Foreground = Palette.PrimaryText,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (result.Year is { } year) titleLine.Children.Add(Chip(year.ToString(), Palette.SecondaryText));
        if (result.IsInLibrary) titleLine.Children.Add(Chip("In library", Palette.Green));

        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(titleLine);
        text.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(result.Overview) ? "No description." : result.Overview,
            Foreground = Palette.SecondaryText,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.WordEllipsis,
            MaxHeight = 52,
            Margin = new Thickness(0, 4, 0, 0),
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(poster);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var card = new Border
        {
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("CardBackground"),
            BorderBrush = (Brush)FindResource("CardBorder"),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
        var button = MovieCards.PlainButton(card, () => Pick(result), result.IsShow ? "Choose episodes" : "Find downloads");
        button.Margin = new Thickness(0, 0, 0, 8);
        MovieCards.Highlight(button, card, this);
        return button;
    }

    private static Border Chip(string text, Brush foreground) => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x26, 0x2C)),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(7, 1, 7, 2),
        Margin = new Thickness(0, 2, 6, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, Foreground = foreground, FontSize = 11 },
    };

    private void Pick(MediaDownloadTitle result)
    {
        if (!result.IsShow)
        {
            _ = StartSearchAsync(new MediaDownloadSearchInput
            {
                Kind = "movie",
                Title = result.DisplayTitle,
                Year = result.Year,
                TmdbId = result.TmdbId,
                PosterPath = result.PosterPath,
            }, Stage.Find);
            return;
        }
        _ = OpenShowAsync(result);
    }

    private void OnDirectSearchClicked(object sender, RoutedEventArgs e)
    {
        if (Query.Length == 0) return;
        var (season, episode) = _kind == "show"
            ? MediaDownloadText.ParseSeasonEpisode(DirectSeasonBox.Text, DirectEpisodeBox.Text)
            : (null, null);
        _ = StartSearchAsync(new MediaDownloadSearchInput
        {
            Kind = _kind,
            Title = Query,
            Season = season,
            Episode = episode,
        }, Stage.Find);
    }

    // ---------------------------------------------------------------- 2. A show's episodes

    private async Task OpenShowAsync(MediaDownloadTitle picked)
    {
        var generation = ++_showGeneration;
        _pickedShow = picked;
        _show = null;
        _season = null;
        ShowStage(Stage.Show);
        SeasonTabs.Children.Clear();
        EpisodesHost.Children.Clear();
        SeasonHead.Visibility = Visibility.Collapsed;
        ShowStatusText(Palette.SecondaryText, "Loading seasons…");

        if (picked.TmdbId is not { } tmdbId)
        {
            ShowStatusText(Palette.Red, "TMDB gave no id for this show.");
            return;
        }

        try
        {
            var show = await _model.Client.MediaDownloadShowAsync(tmdbId);
            if (generation != _showGeneration || _stage != Stage.Show) return;
            _show = show;
            _season = MediaDownloadText.FirstSeasonToOffer(show.Seasons);
            HeadingText.Text = show.Title ?? picked.DisplayTitle;
            RenderShow();
        }
        catch (Exception error)
        {
            if (generation != _showGeneration) return;
            ShowStatusText(Palette.Red, AppModel.UserFacingMessage(error));
        }
    }

    private void ShowStatusText(Brush brush, string text)
    {
        ShowStatus.Foreground = brush;
        ShowStatus.Text = text;
        ShowStatus.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderShow()
    {
        SeasonTabs.Children.Clear();
        EpisodesHost.Children.Clear();
        var show = _show;
        if (show is null) return;

        foreach (var season in show.Seasons)
        {
            var tab = new Button
            {
                Content = MediaDownloadText.SeasonTabLabel(season),
                Margin = new Thickness(0, 0, 6, 6),
                MinWidth = 44,
                Tag = season.SeasonNumber,
            };
            if (season.SeasonNumber == _season) tab.Style = (Style)FindResource("PrimaryButton");
            tab.Click += (_, _) =>
            {
                _season = season.SeasonNumber;
                RenderShow();
            };
            SeasonTabs.Children.Add(tab);
        }

        var current = show.Seasons.FirstOrDefault(s => s.SeasonNumber == _season);
        if (current is null)
        {
            SeasonHead.Visibility = Visibility.Collapsed;
            ShowStatusText(Palette.SecondaryText, "TMDB lists no seasons for this show.");
            return;
        }

        ShowStatusText(Palette.SecondaryText, "");
        SeasonHead.Visibility = Visibility.Visible;
        SeasonSummaryText.Text = MediaDownloadText.SeasonSummary(current);
        foreach (var episode in current.Episodes) EpisodesHost.Children.Add(EpisodeRow(episode));
    }

    private FrameworkElement EpisodeRow(MediaDownloadEpisode episode)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var number = new TextBlock
        {
            Text = episode.Number?.ToString() ?? "",
            Foreground = Palette.SecondaryText,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(number);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(episode.Name) ? $"Episode {episode.Number}" : episode.Name,
            Foreground = episode.IsInLibrary ? Palette.SecondaryText : Palette.PrimaryText,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var detail = MediaDownloadText.EpisodeDetail(episode);
        if (detail.Length > 0)
        {
            text.Children.Add(new TextBlock
            {
                Text = detail,
                Foreground = Palette.SecondaryText,
                FontSize = 11.5,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var get = new Button
        {
            Content = "Get",
            MinWidth = 70,
            IsEnabled = episode.IsAired,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = episode.IsAired ? "Find downloads of this episode" : "Not out yet",
        };
        get.Click += (_, _) => SearchShow(episode.Number);
        Grid.SetColumn(get, 2);
        grid.Children.Add(get);

        return new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 4),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource("CardBackground"),
            Opacity = episode.IsInLibrary ? 0.75 : 1,
            Child = grid,
        };
    }

    private void OnWholeSeasonClicked(object sender, RoutedEventArgs e) => SearchShow(null);

    private void SearchShow(int? episode)
    {
        if (_show is null || _season is not { } season) return;
        _ = StartSearchAsync(new MediaDownloadSearchInput
        {
            Kind = "show",
            Title = _show.Title ?? _pickedShow?.DisplayTitle ?? "",
            Year = _show.Year ?? _pickedShow?.Year,
            TmdbId = _show.TmdbId ?? _pickedShow?.TmdbId,
            PosterPath = _show.PosterPath ?? _pickedShow?.PosterPath,
            Season = season,
            Episode = episode,
        }, Stage.Show);
    }

    // ---------------------------------------------------------------- 3. Releases

    private async Task StartSearchAsync(MediaDownloadSearchInput input, Stage back)
    {
        _searchPoll.Stop();
        var generation = ++_searchGeneration;
        _searchInput = input;
        _searchBack = back;
        _search = null;
        _othersOpen = false;
        ShowStage(Stage.Releases);
        ReleasesBackButton.Content = back == Stage.Show ? "← Episodes" : "← Results";
        WhatText.Text = MediaDownloadText.SearchWhat(input);
        ShowSearching("connecting", "Connecting to the VPN...");

        try
        {
            var search = await _model.Client.StartMediaDownloadSearchAsync(input);
            if (generation != _searchGeneration) return;
            ApplySearch(search);
        }
        catch (Exception error)
        {
            if (generation != _searchGeneration) return;
            ShowFailed(AppModel.UserFacingMessage(error));
        }
    }

    private async Task PollSearchAsync()
    {
        _searchPoll.Stop();
        if (_search?.Id is not { Length: > 0 } id) return;
        var generation = _searchGeneration;
        try
        {
            var search = await _model.Client.MediaDownloadSearchAsync(id);
            if (generation != _searchGeneration || _stage != Stage.Releases) return;
            ApplySearch(search);
        }
        catch (Exception error)
        {
            if (generation != _searchGeneration) return;
            ShowFailed(AppModel.UserFacingMessage(error));
        }
    }

    private void ApplySearch(MediaDownloadSearch search)
    {
        _search = search;
        if (search.IsFailed)
        {
            ShowFailed(FirstText(search.Message, search.Error, "The search failed."));
            return;
        }
        if (search.IsDone)
        {
            RenderReleases(search);
            return;
        }
        ShowSearching(search.Phase, FirstText(search.Message, null, "Searching…"));
        if (!string.IsNullOrEmpty(search.Id)) _searchPoll.Start();
    }

    private static string FirstText(string? first, string? second, string fallback) =>
        !string.IsNullOrWhiteSpace(first) ? first! : !string.IsNullOrWhiteSpace(second) ? second! : fallback;

    private void ShowSearching(string? phase, string message)
    {
        SearchingPanel.Visibility = Visibility.Visible;
        FailedPanel.Visibility = Visibility.Collapsed;
        ReleasesScroller.Visibility = Visibility.Collapsed;
        SearchMessage.Text = message;
        SearchHint.Text = phase == "connecting"
            ? "The first search after a while starts the VPN; it takes a few seconds."
            : "Asking each of your sites.";
    }

    private void ShowFailed(string message)
    {
        _searchPoll.Stop();
        SearchingPanel.Visibility = Visibility.Collapsed;
        ReleasesScroller.Visibility = Visibility.Collapsed;
        FailedPanel.Visibility = Visibility.Visible;
        FailedMessage.Text = message;
    }

    private void OnTryAgainClicked(object sender, RoutedEventArgs e)
    {
        if (_searchInput is { } input) _ = StartSearchAsync(input, _searchBack);
    }

    private void RenderReleases(MediaDownloadSearch search)
    {
        SearchingPanel.Visibility = Visibility.Collapsed;
        FailedPanel.Visibility = Visibility.Collapsed;
        ReleasesScroller.Visibility = Visibility.Visible;
        ReleasesHost.Children.Clear();
        _downloadButtons.Clear();

        var now = DateTimeOffset.Now;
        if (search.Results.Count > 0)
        {
            for (var i = 0; i < search.Results.Count; i++)
            {
                ReleasesHost.Children.Add(ReleaseCard(search.Results[i], best: i == 0, now));
            }
        }
        else
        {
            ReleasesHost.Children.Add(new TextBlock
            {
                Text = search.Others.Count > 0
                    ? "None of the releases found are clearly this title with people sharing it."
                    : FirstText(search.Message, null, "Nothing found on your sites."),
                Style = (Style)FindResource("Caption"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 12),
            });
        }

        if (search.Others.Count > 0)
        {
            var others = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var release in search.Others) others.Children.Add(ReleaseCard(release, best: false, now));
            var expander = new Expander
            {
                Header = OthersHeader(search.Others.Count, _othersOpen),
                IsExpanded = _othersOpen,
                Content = others,
                Foreground = Palette.Cyan,
                Margin = new Thickness(0, 6, 0, 0),
            };
            expander.Expanded += (_, _) =>
            {
                _othersOpen = true;
                expander.Header = OthersHeader(search.Others.Count, true);
            };
            expander.Collapsed += (_, _) =>
            {
                _othersOpen = false;
                expander.Header = OthersHeader(search.Others.Count, false);
            };
            ReleasesHost.Children.Add(expander);
        }
    }

    private static string OthersHeader(int count, bool open) =>
        $"{(open ? "Hide" : "Show")} {MediaDownloadText.Plural(count, "other result")}";

    private FrameworkElement ReleaseCard(MediaRelease release, bool best, DateTimeOffset now)
    {
        var chips = new WrapPanel();
        if (best) chips.Children.Add(Chip("Best match", Palette.Green));
        foreach (var chip in MediaDownloadText.ReleaseChips(release)) chips.Children.Add(Chip(chip, Palette.Cyan));

        var body = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        if (chips.Children.Count > 0) body.Children.Add(chips);
        body.Children.Add(new TextBlock
        {
            Text = release.Title ?? "",
            Foreground = Palette.PrimaryText,
            FontWeight = best ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = release.Title,
            Margin = new Thickness(0, 2, 0, 0),
        });
        var facts = MediaDownloadText.ReleaseFacts(release, now);
        if (facts.Count > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = string.Join("  ·  ", facts),
                Foreground = Palette.SecondaryText,
                FontSize = 11.5,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }
        if (!string.IsNullOrWhiteSpace(release.Warning))
        {
            body.Children.Add(new TextBlock
            {
                Text = release.Warning,
                Foreground = Palette.Amber,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }

        var download = new Button
        {
            Content = "Download",
            MinWidth = 96,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !string.IsNullOrEmpty(release.Id),
        };
        if (best) download.Style = (Style)FindResource("PrimaryButton");
        download.Click += async (_, _) => await GrabAsync(release, download);
        _downloadButtons.Add(download);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(body);
        Grid.SetColumn(download, 1);
        grid.Children.Add(download);

        return new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 6),
            CornerRadius = new CornerRadius(8),
            Background = best ? new SolidColorBrush(Color.FromRgb(0x14, 0x24, 0x1C)) : (Brush)FindResource("CardBackground"),
            BorderBrush = best ? Palette.Green : (Brush)FindResource("CardBorder"),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
    }

    private async Task GrabAsync(MediaRelease release, Button button)
    {
        if (_search?.Id is not { Length: > 0 } searchId || string.IsNullOrEmpty(release.Id)) return;
        foreach (var other in _downloadButtons) other.IsEnabled = false;
        button.Content = "Starting…";
        Message("");
        try
        {
            Grabbed = await _model.Client.StartMediaDownloadAsync(searchId, release.Id!);
            DialogResult = true;
        }
        catch (Exception error)
        {
            foreach (var other in _downloadButtons) other.IsEnabled = true;
            button.Content = "Download";
            Message(AppModel.UserFacingMessage(error), isError: true);
        }
    }

    // ---------------------------------------------------------------- Chrome

    private void Message(string message, bool isError = false)
    {
        MessageText.Foreground = isError ? Palette.Red : Palette.SecondaryText;
        MessageText.Text = message;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
