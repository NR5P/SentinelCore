using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>
/// One movie, show or episode: backdrop and poster, the facts, Play / Resume / Start over, Mark
/// watched, My List, the overview and cast, and what the file is. A show adds season buttons and the
/// episode list, with Play for the next episode to watch.
/// </summary>
/// <remarks>
/// Reloads every time it comes back on screen, and again once a player it opened has saved its
/// last position - the Resume button has to name the point the film was actually left at.
/// </remarks>
public partial class MovieDetailView : UserControl
{
    private readonly AppModel _model;
    private readonly INavigator _navigator;
    private readonly string _itemId;

    private MediaItemDetail? _detail;
    private int? _selectedSeason;
    private int _generation;

    // What the My List button shows. Flipped before the server answers and put back if it refuses,
    // so the button responds at once; a reload takes the server's word again.
    private bool _inList;

    // Told after this title is deleted, before the view closes, so the list it came from reloads.
    private readonly Action? _onDeleted;

    public MovieDetailView(AppModel model, INavigator? navigator, string itemId, Action? onDeleted = null)
    {
        _model = model;
        _navigator = navigator ?? NullNavigator.Instance;
        _itemId = itemId;
        _onDeleted = onDeleted;
        InitializeComponent();

        Loaded += (_, _) => _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var generation = ++_generation;
        try
        {
            var detail = await _model.Client.MediaItemAsync(_itemId);
            if (generation != _generation) return;
            _detail = detail;
            _inList = detail.InList;
            LoadingText.Visibility = Visibility.Collapsed;
            NoticeText.Visibility = Visibility.Collapsed;
            Render(detail);
        }
        catch (Exception error)
        {
            if (generation != _generation) return;
            var message = AppModel.UserFacingMessage(error);
            if (_detail is null)
            {
                LoadingText.Text = message;
                LoadingText.Visibility = Visibility.Visible;
            }
            else
            {
                Notice(message);
            }
        }
    }

    private void Notice(string message)
    {
        NoticeText.Text = message;
        NoticeText.Visibility = Visibility.Visible;
    }

    // ---------------------------------------------------------------- Layout

    private void Render(MediaItemDetail detail)
    {
        BackdropImage.SourcePath = detail.BackdropUrl ?? (detail.IsEpisode ? detail.ThumbUrl : null);
        ContentHost.Children.Clear();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Episodes are landscape stills; everything else a 2:3 poster.
        var artwork = detail.IsEpisode
            ? MovieCards.Artwork(this, detail.ThumbUrl ?? detail.PosterUrl, detail.Title, 320, 180, 640)
            : MovieCards.Artwork(this, detail.PosterUrl, detail.Title, 200, 300, 400);
        artwork.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(artwork, 0);
        header.Children.Add(artwork);

        var info = BuildInfo(detail);
        Grid.SetColumn(info, 1);
        header.Children.Add(info);
        ContentHost.Children.Add(header);

        if (detail.IsShow && detail.Seasons is { Count: > 0 } seasons) ContentHost.Children.Add(BuildSeasons(detail, seasons));
        if (detail.Cast.Count > 0) ContentHost.Children.Add(BuildCast(detail.Cast));
        if (detail.File is { } file) ContentHost.Children.Add(BuildFile(detail, file));
    }

    private StackPanel BuildInfo(MediaItemDetail detail)
    {
        var info = new StackPanel { Margin = new Thickness(28, 0, 0, 0), MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left };

        if (detail.IsEpisode && !string.IsNullOrWhiteSpace(detail.ShowTitle))
        {
            var show = new Button { Content = detail.ShowTitle, Style = (Style)FindResource("LinkButton"), FontSize = 14 };
            show.Click += (_, _) =>
            {
                if (detail.ShowId is { Length: > 0 } showId)
                {
                    _navigator.Push(new MovieDetailView(_model, _navigator, showId), detail.ShowTitle!);
                }
            };
            info.Children.Add(show);
        }

        info.Children.Add(new TextBlock
        {
            Text = detail.Title,
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.PrimaryText,
            TextWrapping = TextWrapping.Wrap,
        });

        var facts = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var meta = detail.IsEpisode ? EpisodeMeta(detail) : MediaPlaybackRules.MetaLine(detail);
        if (MediaPlaybackRules.PlayCountText(detail.PlayCount) is { Length: > 0 } plays)
        {
            meta = meta.Length > 0 ? $"{meta} · {plays}" : plays;
        }
        if (meta.Length > 0)
        {
            facts.Children.Add(new TextBlock
            {
                Text = meta,
                Foreground = Palette.SecondaryText,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 8),
            });
        }
        if (!string.IsNullOrWhiteSpace(detail.Certification)) facts.Children.Add(Chip(detail.Certification!, Palette.Amber));
        foreach (var genre in detail.Genres) facts.Children.Add(Chip(genre, Palette.PrimaryText));
        info.Children.Add(facts);

        if (!string.IsNullOrWhiteSpace(detail.Tagline))
        {
            info.Children.Add(new TextBlock
            {
                Text = detail.Tagline,
                FontStyle = FontStyles.Italic,
                Foreground = Palette.SecondaryText,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });
        }

        info.Children.Add(BuildActions(detail));

        if (!detail.IsShow && MediaPlaybackRules.CanResume(detail.Progress) && detail.Progress is { } progress)
        {
            var remaining = progress.DurationSeconds > 0 ? progress.DurationSeconds - progress.PositionSeconds : 0;
            info.Children.Add(new TextBlock
            {
                Text = remaining > 60
                    ? $"{MediaPlaybackRules.FormatRuntime((int)Math.Round(remaining / 60))} left"
                    : $"Stopped at {MediaPlaybackRules.FormatClock(progress.PositionSeconds)}",
                Style = (Style)FindResource("Caption"),
                Margin = new Thickness(0, 8, 0, 0),
            });
        }

        if (!string.IsNullOrWhiteSpace(detail.Overview))
        {
            info.Children.Add(new TextBlock
            {
                Text = detail.Overview,
                Style = (Style)FindResource("BodyText"),
                LineHeight = 20,
                Margin = new Thickness(0, 16, 0, 0),
                MaxWidth = 760,
                HorizontalAlignment = HorizontalAlignment.Left,
            });
        }

        if (!string.IsNullOrWhiteSpace(detail.TmdbUrl))
        {
            var tmdb = new Button
            {
                Content = "The Movie Database ↗",
                Style = (Style)FindResource("LinkButton"),
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            tmdb.Click += (_, _) => OpenLink(detail.TmdbUrl!);
            info.Children.Add(tmdb);
        }

        return info;
    }

    private static string EpisodeMeta(MediaItemDetail episode)
    {
        var parts = new List<string>();
        if (episode.EpisodeCode.Length > 0) parts.Add(episode.EpisodeCode);
        if (!string.IsNullOrWhiteSpace(episode.AirDate) &&
            DateTime.TryParse(episode.AirDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var aired))
        {
            parts.Add(aired.ToString("d MMM yyyy", CultureInfo.CurrentCulture));
        }
        if (MediaPlaybackRules.RuntimeText(episode) is { Length: > 0 } runtime) parts.Add(runtime);
        if (episode.Rating is { } rating && rating > 0) parts.Add($"★ {rating.ToString("0.0", CultureInfo.InvariantCulture)}");
        return string.Join(" · ", parts);
    }

    private Border Chip(string text, Brush foreground) => new()
    {
        Style = (Style)FindResource("Chip"),
        Child = new TextBlock { Text = text, Foreground = foreground, FontSize = 11.5 },
    };

    private WrapPanel BuildActions(MediaItemDetail detail)
    {
        var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };

        Button AddAction(string text, bool primary, Func<Task> run)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(16, 8, 16, 8) };
            if (primary) button.Style = (Style)FindResource("PrimaryButton");
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                try { await run(); }
                finally { button.IsEnabled = true; }
            };
            actions.Children.Add(button);
            return button;
        }

        if (detail.IsShow)
        {
            var next = AllEpisodes(detail).FirstOrDefault(episode => episode.Id == detail.NextEpisodeId);
            if (next is not null)
            {
                var verb = MediaPlaybackRules.CanResume(next.Progress) ? "Resume" : "Play";
                AddAction($"▶  {verb} {next.EpisodeCode}", primary: true, () =>
                {
                    Play(next.Id, null, $"{detail.Title} · {next.EpisodeLabel}");
                    return Task.CompletedTask;
                });
            }

            var allWatched = detail.EpisodeCount is { } total && total > 0 && detail.WatchedEpisodeCount >= total;
            AddAction(allWatched ? "Mark unwatched" : "Mark all watched", primary: false,
                () => SetWatchedAsync(detail.Id, !allWatched));
            actions.Children.Add(MyListButton(detail));
            return actions;
        }

        var title = detail.IsEpisode && !string.IsNullOrWhiteSpace(detail.ShowTitle)
            ? $"{detail.ShowTitle} · {detail.EpisodeLabel}"
            : detail.Title;
        if (MediaPlaybackRules.CanResume(detail.Progress))
        {
            AddAction($"▶  {MediaPlaybackRules.ResumeLabel(detail.Progress)}", primary: true, () =>
            {
                Play(detail.Id, MediaPlaybackRules.StartFor(detail.Progress, startOver: false), title);
                return Task.CompletedTask;
            });
            AddAction("Start over", primary: false, () =>
            {
                Play(detail.Id, MediaPlaybackRules.StartOver, title);
                return Task.CompletedTask;
            });
        }
        else
        {
            AddAction("▶  Play", primary: true, () =>
            {
                Play(detail.Id, MediaPlaybackRules.StartOver, title);
                return Task.CompletedTask;
            });
        }

        AddAction(detail.IsWatched ? "Mark unwatched" : "Mark watched", primary: false,
            () => SetWatchedAsync(detail.Id, !detail.IsWatched));
        // Episodes are never on My List themselves - their show is.
        if (!detail.IsEpisode) actions.Children.Add(MyListButton(detail));

        // Admins only, and only a movie on the server's own disks - the server decides.
        if (detail.CanDelete)
        {
            var delete = AddAction("Delete", primary: false, () => DeleteAsync(detail));
            delete.Foreground = Palette.Red;
        }
        return actions;
    }

    private async Task DeleteAsync(MediaItemDetail detail)
    {
        var answer = MessageBox.Show(
            Window.GetWindow(this),
            "The movie's file is deleted from the server's disk for everyone. This can't be undone.",
            $"Delete {detail.Title}?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            await _model.Client.DeleteMediaItemAsync(detail.Id);
            _onDeleted?.Invoke();
            _navigator.Pop();
        }
        catch (Exception error)
        {
            Notice(AppModel.UserFacingMessage(error));
        }
    }

    private Button MyListButton(MediaItemDetail detail)
    {
        var button = new Button { Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(16, 8, 16, 8) };
        void Show()
        {
            button.Content = _inList ? "✓ My List" : "+ My List";
            button.Foreground = _inList ? Palette.Green : Palette.PrimaryText;
        }
        Show();

        button.Click += async (_, _) =>
        {
            var wanted = !_inList;
            _inList = wanted;
            Show();
            button.IsEnabled = false;
            try
            {
                var saved = await _model.Client.SetInMyListAsync(detail.Id, wanted);
                _inList = saved.InList;
                NoticeText.Visibility = Visibility.Collapsed;
            }
            catch (Exception error)
            {
                _inList = !wanted;
                Notice(AppModel.UserFacingMessage(error));
            }
            finally
            {
                Show();
                button.IsEnabled = true;
            }
        };
        return button;
    }

    private static IEnumerable<MediaItemSummary> AllEpisodes(MediaItemDetail show) =>
        show.Seasons?.SelectMany(season => season.Episodes) ?? Enumerable.Empty<MediaItemSummary>();

    // ---------------------------------------------------------------- Seasons

    private StackPanel BuildSeasons(MediaItemDetail show, List<MediaSeason> seasons)
    {
        // The season holding the episode to watch next, unless one was picked by hand.
        if (_selectedSeason is null || seasons.All(season => season.Number != _selectedSeason))
        {
            var next = AllEpisodes(show).FirstOrDefault(episode => episode.Id == show.NextEpisodeId);
            _selectedSeason = next?.SeasonNumber ?? seasons[0].Number;
        }
        var selected = seasons.FirstOrDefault(season => season.Number == _selectedSeason) ?? seasons[0];

        var panel = new StackPanel { Margin = new Thickness(0, 30, 0, 0) };

        var picker = new WrapPanel();
        foreach (var season in seasons)
        {
            var button = new Button { Content = season.Label, Margin = new Thickness(0, 0, 8, 8) };
            if (season.Number == selected.Number) button.Style = (Style)FindResource("PrimaryButton");
            button.Click += (_, _) =>
            {
                _selectedSeason = season.Number;
                if (_detail is { } detail) Render(detail);
            };
            picker.Children.Add(button);
        }
        if (seasons.Count > 1) panel.Children.Add(picker);

        var heading = new Grid { Margin = new Thickness(0, 10, 0, 10) };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var count = selected.EpisodeCount == 1 ? "1 episode" : $"{selected.EpisodeCount} episodes";
        heading.Children.Add(new TextBlock
        {
            Text = $"{selected.Label.ToUpperInvariant()} · {count} · {selected.WatchedCount} watched",
            Style = (Style)FindResource("GroupHeading"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0),
        });
        var seasonWatched = selected.EpisodeCount > 0 && selected.WatchedCount >= selected.EpisodeCount;
        var markSeason = new Button { Content = seasonWatched ? "Mark season unwatched" : "Mark season watched", Padding = new Thickness(10, 5, 10, 5) };
        markSeason.Click += async (_, _) =>
        {
            markSeason.IsEnabled = false;
            try
            {
                await _model.Client.SetMediaSeasonWatchedAsync(show.Id, selected.Number, !seasonWatched);
                await LoadAsync();
            }
            catch (Exception error)
            {
                Notice(AppModel.UserFacingMessage(error));
                markSeason.IsEnabled = true;
            }
        };
        Grid.SetColumn(markSeason, 1);
        heading.Children.Add(markSeason);
        panel.Children.Add(heading);

        foreach (var episode in selected.Episodes) panel.Children.Add(EpisodeRow(show, episode));
        return panel;
    }

    private FrameworkElement EpisodeRow(MediaItemDetail show, MediaItemSummary episode)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Left };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // The still and the text are one big play target; the buttons at the end are separate.
        var still = MovieCards.Artwork(this, episode.ThumbUrl, episode.EpisodeNumber is { } number ? $"E{number}" : "", 176, 99, 352);
        var stillLayer = new Grid();
        stillLayer.Children.Add(still);
        if (episode.IsWatched)
        {
            stillLayer.Children.Add(MovieCards.Badge("✓", Palette.Green, new SolidColorBrush(Color.FromRgb(0x04, 0x12, 0x0A)),
                HorizontalAlignment.Right, VerticalAlignment.Top));
        }
        if (MediaPlaybackRules.Fraction(episode.Progress) is { } fraction && fraction > 0)
        {
            stillLayer.Children.Add(MovieCards.ProgressStrip(fraction, 176));
        }

        var text = new StackPanel { Margin = new Thickness(16, 4, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        var name = episode.EpisodeNumber is { } episodeNumber ? $"{episodeNumber}. {episode.Title}" : episode.Title;
        text.Children.Add(new TextBlock
        {
            Text = name,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.PrimaryText,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var facts = new List<string>();
        if (!string.IsNullOrWhiteSpace(episode.AirDate) &&
            DateTime.TryParse(episode.AirDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var aired))
        {
            facts.Add(aired.ToString("d MMM yyyy", CultureInfo.CurrentCulture));
        }
        if (MediaPlaybackRules.RuntimeText(episode) is { Length: > 0 } runtime) facts.Add(runtime);
        if (MediaPlaybackRules.CanResume(episode.Progress)) facts.Add(MediaPlaybackRules.ResumeLabel(episode.Progress));
        else if (episode.IsWatched) facts.Add("Watched");
        if (episode.Id == show.NextEpisodeId) facts.Add("Up next");
        text.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", facts),
            Foreground = episode.Id == show.NextEpisodeId ? Palette.Cyan : Palette.SecondaryText,
            FontSize = 11.5,
            Margin = new Thickness(0, 4, 0, 0),
        });

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(stillLayer);
        content.Children.Add(text);

        var title = $"{show.Title} · {episode.EpisodeLabel}";
        var play = MovieCards.PlainButton(content, () => Play(episode.Id, MediaPlaybackRules.StartFor(episode.Progress, startOver: false), title),
            MediaPlaybackRules.CanResume(episode.Progress) ? MediaPlaybackRules.ResumeLabel(episode.Progress) : "Play");
        MovieCards.Highlight(play, still, this);
        Grid.SetColumn(play, 0);
        row.Children.Add(play);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var details = new Button { Content = "Details", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 0) };
        details.Click += (_, _) => _navigator.Push(new MovieDetailView(_model, _navigator, episode.Id), title);
        buttons.Children.Add(details);
        var watched = new Button
        {
            Content = episode.IsWatched ? "✓ Watched" : "Mark watched",
            Padding = new Thickness(10, 5, 10, 5),
            Foreground = episode.IsWatched ? Palette.Green : Palette.PrimaryText,
        };
        watched.Click += async (_, _) =>
        {
            watched.IsEnabled = false;
            await SetWatchedAsync(episode.Id, !episode.IsWatched);
            watched.IsEnabled = true;
        };
        buttons.Children.Add(watched);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(buttons);

        return row;
    }

    // ---------------------------------------------------------------- Cast and file

    private StackPanel BuildCast(List<MediaCastMember> cast)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 30, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "CAST", Style = (Style)FindResource("GroupHeading") });
        var chips = new WrapPanel { MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var member in cast.Take(24))
        {
            var text = new TextBlock { FontSize = 12 };
            text.Inlines.Add(new Run(member.Name) { Foreground = Palette.PrimaryText });
            if (!string.IsNullOrWhiteSpace(member.Character))
            {
                text.Inlines.Add(new Run($"  {member.Character}") { Foreground = Palette.SecondaryText });
            }
            chips.Children.Add(new Border { Style = (Style)FindResource("Chip"), Child = text });
        }
        panel.Children.Add(chips);
        return panel;
    }

    private StackPanel BuildFile(MediaItemDetail detail, MediaFileInfo file)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 26, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "FILE", Style = (Style)FindResource("GroupHeading") });

        void Line(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var text = new TextBlock { Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 0, 0, 4) };
            text.Inlines.Add(new Run($"{label}  ") { Foreground = Palette.SecondaryText });
            text.Inlines.Add(new Run(value) { Foreground = Palette.PrimaryText });
            panel.Children.Add(text);
        }

        Line("Video", Join(Upper(file.VideoCodec), file.VideoProfile,
            file.Width is { } width && file.Height is { } height ? $"{width}×{height}" : null));
        Line("Audio", Join(Upper(file.AudioCodec), Channels(file.AudioChannels)));
        Line("File", Join(Upper(file.Container), Size(file.SizeBytes), Bitrate(file.Bitrate)));

        if (detail.AudioTracks is { Count: > 1 } tracks)
        {
            Line("Audio tracks", string.Join(", ", tracks.Select(track =>
                (track.Label ?? track.Language ?? $"Track {track.Index + 1}") + (track.IsDefault ? " (default)" : ""))));
        }
        if (detail.Subtitles is { Count: > 0 } subtitles)
        {
            Line("Subtitles", string.Join(", ", subtitles.Select(track =>
                (track.Label ?? track.Language ?? $"Track {track.Index + 1}") + (track.Forced ? " (forced)" : ""))));
        }
        if (!string.IsNullOrWhiteSpace(file.Path))
        {
            panel.Children.Add(new TextBlock { Text = file.Path, Style = (Style)FindResource("Monospace"), Margin = new Thickness(0, 4, 0, 0) });
        }
        return panel;
    }

    private static string Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));

    private static string? Upper(string? codec) => string.IsNullOrWhiteSpace(codec) ? null : codec.ToUpperInvariant();

    private static string? Channels(int? channels) => channels switch
    {
        null or <= 0 => null,
        1 => "mono",
        2 => "stereo",
        6 => "5.1",
        8 => "7.1",
        _ => $"{channels} channels",
    };

    private static string? Size(long? bytes)
    {
        if (bytes is not { } value || value <= 0) return null;
        return value >= 1_000_000_000
            ? $"{(value / 1_000_000_000d).ToString("0.0", CultureInfo.CurrentCulture)} GB"
            : $"{(value / 1_000_000d).ToString("0", CultureInfo.CurrentCulture)} MB";
    }

    private static string? Bitrate(long? bitsPerSecond) =>
        bitsPerSecond is { } value && value > 0
            ? $"{(value / 1_000_000d).ToString("0.0", CultureInfo.CurrentCulture)} Mb/s"
            : null;

    // ---------------------------------------------------------------- Actions

    private void Play(string itemId, double? start, string title) =>
        _navigator.Push(new MoviePlayerView(_model, _navigator, itemId, start, () => _ = LoadAsync()), title);

    private async Task SetWatchedAsync(string itemId, bool watched)
    {
        try
        {
            await _model.Client.SetMediaWatchedAsync(itemId, watched);
            await LoadAsync();
        }
        catch (Exception error)
        {
            Notice(AppModel.UserFacingMessage(error));
        }
    }

    private void OpenLink(string url)
    {
        // Only ever a web page: this string comes from the server, and the shell will run anything.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            _model.Log($"Could not open {url}: {error.Message}", DiagnosticLevel.Notice);
        }
    }
}
