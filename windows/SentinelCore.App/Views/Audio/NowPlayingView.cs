using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using SentinelCore.App.Audio;
using SentinelCore.Client;
using SentinelCore.Client.Audio;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;

namespace SentinelCore.App.Views.Audio;

/// <summary>
/// The full Now Playing screen (§11): big artwork, the scrubber and every control - shuffle,
/// repeat, ±15/30 for podcasts, speed, sleep timer, volume, Sound Check, love, bookmark, clip,
/// Play on - beside a panel of synced lyrics (highlighted, click to seek), the queue (reorder,
/// remove, jump), chapters, and a transcript that follows playback and can be searched.
/// </summary>
public sealed class NowPlayingView : UserControl, IDisposable
{
    private readonly PlayerController _player;
    private readonly INavigator _navigator;
    private readonly AppModel _model;

    private readonly ContentControl _art = new();
    private readonly TextBlock _title = new() { FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Palette.PrimaryText, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly StackPanel _subtitle = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _chapterLine = new() { Foreground = Palette.Cyan, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly PlayerParts.Scrubber _scrubber;
    private readonly Button _play;
    private readonly Button _shuffle;
    private readonly Button _repeat;
    private readonly Button _back15;
    private readonly Button _forward30;
    private readonly Button _speed;
    private readonly Button _sleep;
    private readonly CheckBox _soundCheck = new() { Content = "Sound Check", ToolTip = "Even out loudness between songs using their ReplayGain tags", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 1, Width = 120, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
    private readonly StackPanel _extras = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _target = new() { Foreground = Palette.Green, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };

    private readonly ContentControl _panelTabs = new();
    private readonly ScrollViewer _panelScroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly StackPanel _panel = new() { Margin = new Thickness(0, 0, 12, 12) };
    private readonly DockPanel _panelHost = new();
    private string _tab = "";

    // What the panel was built for, so a tick only re-highlights instead of rebuilding.
    private string? _builtFor;
    private string? _artFor;
    private List<LyricsLine> _lyrics = new();
    private List<PodcastChapter> _chapters = new();
    private PodcastTranscript? _transcript;
    private readonly List<FrameworkElement> _lines = new();
    private int _active = -1;
    private DateTime _userScrolledAt = DateTime.MinValue;
    private bool _autoScrolling;
    private int _loadGeneration;
    private readonly TextBox _transcriptSearch = new() { Margin = new Thickness(0, 0, 0, 8), ToolTip = "Search this transcript" };
    private bool _disposed;

    public NowPlayingView(PlayerController player, INavigator navigator)
    {
        _player = player;
        _navigator = navigator;
        _model = player.Model;
        _scrubber = new PlayerParts.Scrubber(player);

        _play = AudioUi.GlyphButton(AudioUi.PlayGlyph, "Play", player.TogglePlay, 26, Palette.Green);
        _play.Padding = new Thickness(14, 10, 14, 10);
        _shuffle = AudioUi.GlyphButton(AudioUi.ShuffleGlyph, "Shuffle", player.ToggleShuffle, 15);
        _repeat = AudioUi.GlyphButton(AudioUi.RepeatAllGlyph, "Repeat", player.CycleRepeat, 15);
        _back15 = AudioUi.GlyphButton("", "Back 15 seconds", () => player.SkipBy(-PodcastPlayback.SkipBackSeconds), 17);
        _forward30 = AudioUi.GlyphButton("", "Forward 30 seconds", () => player.SkipBy(PodcastPlayback.SkipForwardSeconds), 17);
        _speed = AudioUi.TextButton("1×", () => { }, "Playback speed");
        _speed.Click += (_, _) => PlayerParts.Popup(_speed, PlayerParts.SpeedMenu(player));
        _sleep = AudioUi.GlyphButton(AudioUi.TimerGlyph, "Sleep timer", () => { }, 15);
        _sleep.Click += (_, _) => PlayerParts.Popup(_sleep, PlayerParts.SleepMenu(player));

        // ---- left: the player
        var left = new StackPanel { Width = 400, Margin = new Thickness(24, 20, 24, 20), VerticalAlignment = VerticalAlignment.Top };
        _art.HorizontalAlignment = HorizontalAlignment.Center;
        left.Children.Add(_art);
        _title.Margin = new Thickness(0, 16, 0, 2);
        left.Children.Add(_title);
        left.Children.Add(_subtitle);
        left.Children.Add(_chapterLine);
        var scrub = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        scrub.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        scrub.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scrub.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        scrub.Children.Add(_scrubber.Elapsed);
        _scrubber.Slider.Margin = new Thickness(8, 0, 8, 0);
        Grid.SetColumn(_scrubber.Slider, 1);
        scrub.Children.Add(_scrubber.Slider);
        Grid.SetColumn(_scrubber.Left, 2);
        scrub.Children.Add(_scrubber.Left);
        left.Children.Add(scrub);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        controls.Children.Add(_shuffle);
        controls.Children.Add(AudioUi.GlyphButton(AudioUi.PreviousGlyph, "Previous", () => _ = player.PreviousAsync(), 17));
        controls.Children.Add(_back15);
        controls.Children.Add(_play);
        controls.Children.Add(_forward30);
        controls.Children.Add(AudioUi.GlyphButton(AudioUi.NextGlyph, "Next", () => _ = player.NextAsync(), 17));
        controls.Children.Add(_repeat);
        left.Children.Add(controls);

        var settings = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        settings.Children.Add(new TextBlock { Text = AudioUi.VolumeGlyph, FontFamily = AudioUi.Icons, Foreground = Palette.SecondaryText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        AudioUi.AutomationName(_volume, "Volume");
        _volume.Value = player.UserVolume;
        _volume.PreviewMouseLeftButtonUp += (_, _) => player.UserVolume = _volume.Value;
        _volume.KeyUp += (_, _) => player.UserVolume = _volume.Value;
        settings.Children.Add(_volume);
        settings.Children.Add(_soundCheck);
        _soundCheck.IsChecked = player.SoundCheckEnabled;
        _soundCheck.Click += (_, _) => player.SoundCheckEnabled = _soundCheck.IsChecked == true;
        left.Children.Add(settings);

        var row3 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        row3.Children.Add(_speed);
        row3.Children.Add(_sleep);
        row3.Children.Add(AudioUi.GlyphButton(AudioUi.SpeakerGlyph, "Play on…", () => new SpeakerPickerWindow(_model) { Owner = Window.GetWindow(this) }.ShowDialog(), 15));
        left.Children.Add(row3);
        left.Children.Add(_extras);
        left.Children.Add(_target);

        var leftScroller = new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        // ---- right: lyrics / queue / chapters / transcript / bookmarks
        DockPanel.SetDock(_panelTabs, Dock.Top);
        _panelTabs.Margin = new Thickness(0, 20, 12, 10);
        _panelHost.Children.Add(_panelTabs);
        _panelScroller.Content = _panel;
        _panelScroller.ScrollChanged += (_, args) =>
        {
            // Following stops for a few seconds after the reader scrolls by hand.
            if (_autoScrolling)
            {
                _autoScrolling = false;
                return;
            }
            if (args.VerticalChange != 0) _userScrolledAt = DateTime.UtcNow;
        };
        _panelHost.Children.Add(_panelScroller);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(leftScroller);
        Grid.SetColumn(_panelHost, 1);
        grid.Children.Add(_panelHost);
        Content = grid;

        _transcriptSearch.TextChanged += (_, _) =>
        {
            if (_tab == "transcript") BuildPanel(force: true);
        };

        player.Changed += OnChanged;
        player.Ticked += OnTick;
        Loaded += (_, _) => OnChanged();
    }

    private void OnChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;
        Apply();
    });

    private void Apply()
    {
        if (!_player.HasItem)
        {
            _title.Text = "Nothing playing";
            _subtitle.Children.Clear();
            _panel.Children.Clear();
            return;
        }

        var remote = _player.RemoteTarget;
        var entry = _player.Current;
        string? art;
        _subtitle.Children.Clear();
        if (remote is not null)
        {
            var state = _player.RemoteState;
            _title.Text = state?.Title ?? "Nothing playing";
            _subtitle.Children.Add(new TextBlock { Text = state?.Subtitle ?? "", Foreground = Palette.SecondaryText });
            art = state?.ArtUrl;
            _target.Text = $"Playing on {remote.Name}";
            _target.Visibility = Visibility.Visible;
        }
        else
        {
            _title.Text = entry!.Title;
            art = entry.ArtUrl;
            AddSubtitleLinks(entry);
            _target.Visibility = Visibility.Collapsed;
        }
        if (art != _artFor)
        {
            _artFor = art;
            _art.Content = AudioUi.Art(art, 340, entry?.IsEpisode == true ? AudioUi.PodcastGlyph : AudioUi.MusicGlyph);
        }

        var podcast = remote is null ? entry?.IsEpisode == true : _player.RemoteState?.Kind == "podcast";
        _back15.Visibility = podcast ? Visibility.Visible : Visibility.Collapsed;
        _forward30.Visibility = podcast ? Visibility.Visible : Visibility.Collapsed;
        _speed.Visibility = remote is null ? Visibility.Visible : Visibility.Collapsed;
        _speed.Content = PodcastPlayback.SpeedLabel(_player.Speed);
        _shuffle.Opacity = _player.Queue.Shuffle ? 1 : 0.5;
        ((TextBlock)_shuffle.Content).Foreground = _player.Queue.Shuffle ? Palette.Green : Palette.PrimaryText;
        _shuffle.ToolTip = _player.Queue.Shuffle ? "Shuffle is on" : "Shuffle is off";
        ((TextBlock)_repeat.Content).Text = PlayerParts.RepeatGlyph(_player.Queue.Repeat);
        ((TextBlock)_repeat.Content).Foreground = _player.Queue.Repeat == RepeatMode.Off ? Palette.SecondaryText : Palette.Green;
        _repeat.ToolTip = PlayerParts.RepeatTip(_player.Queue.Repeat);
        _soundCheck.IsChecked = _player.SoundCheckEnabled;
        _soundCheck.Visibility = podcast ? Visibility.Collapsed : Visibility.Visible;
        if (!_volume.IsMouseCaptureWithin) _volume.Value = _player.UserVolume;
        ((TextBlock)_sleep.Content).Foreground = _player.Sleep.IsActive ? Palette.Amber : Palette.PrimaryText;

        _extras.Children.Clear();
        if (remote is null && entry?.Track is { } track)
        {
            _extras.Children.Add(AudioUi.LoveButton(track.Loved, loved => AudioUi.SetLovedAsync("tracks", track.Id, loved, value => track.Loved = value)));
            _extras.Children.Add(AudioUi.Stars(track.Rating, rating => AudioUi.RateAsync("tracks", track.Id, rating, value => track.Rating = value)));
            var more = AudioUi.GlyphButton(AudioUi.MoreGlyph, "More", () => { }, 13);
            var menu = AudioUi.TrackMenu(track, _navigator);
            more.Click += (_, _) => PlayerParts.Popup(more, menu);
            _extras.Children.Add(more);
        }
        else if (remote is null && entry?.Episode is { } episode)
        {
            _extras.Children.Add(AudioUi.TextButton("Bookmark", () => _ = BookmarkAsync(episode), "Keep this moment, with a note"));
            _extras.Children.Add(AudioUi.TextButton("Clip…", () => new PodcastClipWindow(_model, episode, _player.Position) { Owner = Window.GetWindow(this) }.ShowDialog(), "Make a short audio clip to share"));
            _extras.Children.Add(AudioUi.TextButton("Episode", () => AudioNav.OpenEpisode(_model, _navigator, episode.Id, episode.Title)));
        }

        var key = remote is not null ? $"remote:{remote.DeviceId}" : $"{entry?.Uid}";
        if (key != _builtFor)
        {
            _builtFor = key;
            _ = LoadExtrasAsync(entry, remote is not null);
        }
        else
        {
            if (_tab == "queue") BuildPanel(force: true);
        }
        DrawTabs();
        OnTick();
    }

    private void AddSubtitleLinks(QueueEntry entry)
    {
        Button Link(string text, Action open)
        {
            var link = new Button { Content = text, Style = AudioUi.StyleOf("LinkButton"), FontSize = 14, Margin = new Thickness(0) };
            link.Click += (_, _) => open();
            return link;
        }
        if (entry.Track is { } track)
        {
            if (!string.IsNullOrEmpty(track.ArtistId)) _subtitle.Children.Add(Link(track.ArtistLine, () => AudioNav.OpenArtist(_model, _navigator, track.ArtistId!, track.ArtistLine)));
            else _subtitle.Children.Add(new TextBlock { Text = track.ArtistLine, Foreground = Palette.SecondaryText, FontSize = 14 });
            if (!string.IsNullOrEmpty(track.Album) && !string.IsNullOrEmpty(track.AlbumId))
            {
                _subtitle.Children.Add(new TextBlock { Text = "  ·  ", Foreground = Palette.SecondaryText, FontSize = 14 });
                _subtitle.Children.Add(Link(track.Album!, () => AudioNav.OpenAlbum(_model, _navigator, track.AlbumId!, track.Album!)));
            }
        }
        else if (entry.Episode is { } episode && !string.IsNullOrEmpty(episode.PodcastId))
        {
            _subtitle.Children.Add(Link(episode.PodcastTitle ?? "Show", () => AudioNav.OpenShow(_model, _navigator, episode.PodcastId!, episode.PodcastTitle ?? "Show")));
        }
        if (_player.Queue.Context?.Name is { Length: > 0 } from && _player.Queue.Context.Type is not ("album" or "podcast"))
        {
            _chapterLine.Text = $"Playing from {from}";
        }
        else
        {
            _chapterLine.Text = "";
        }
    }

    // ---------------------------------------------------------------- The side panel

    private IReadOnlyList<(string Key, string Label)> AvailableTabs()
    {
        var tabs = new List<(string, string)>();
        var entry = _player.Current;
        if (_player.RemoteTarget is null && entry?.Track is not null) tabs.Add(("lyrics", "Lyrics"));
        if (_player.RemoteTarget is null && entry?.IsEpisode == true)
        {
            if (_chapters.Count > 0) tabs.Add(("chapters", "Chapters"));
            tabs.Add(("transcript", "Transcript"));
            tabs.Add(("bookmarks", "Bookmarks"));
        }
        tabs.Add(("queue", "Up next"));
        return tabs;
    }

    private void DrawTabs()
    {
        var tabs = AvailableTabs();
        if (tabs.All(t => t.Key != _tab))
        {
            _tab = tabs.Any(t => t.Key == "lyrics") && _lyrics.Count > 0 ? "lyrics"
                : tabs.Any(t => t.Key == "transcript") && _transcript is { Segments.Count: > 0 } ? "transcript"
                : tabs.Any(t => t.Key == "chapters") ? "chapters"
                : tabs[0].Key;
            BuildPanel(force: true);
        }
        _panelTabs.Content = AudioUi.Tabs(tabs, _tab, key =>
        {
            _tab = key;
            DrawTabs();
            BuildPanel(force: true);
        });
    }

    private async Task LoadExtrasAsync(QueueEntry? entry, bool remote)
    {
        var generation = ++_loadGeneration;
        _lyrics = new();
        _chapters = new();
        _transcript = null;
        _tab = "";
        BuildPanel(force: true);
        if (entry is null || remote) return;
        try
        {
            if (entry.Track is { } track)
            {
                try
                {
                    var lyrics = await _model.Client.LyricsAsync(track.Id);
                    if (generation != _loadGeneration) return;
                    _lyrics = LyricsTimeline.LinesFor(lyrics);
                }
                catch (SentinelServerException)
                {
                    // 404: none anywhere.
                }
            }
            else if (entry.Episode is { } episode)
            {
                if (episode.HasChapters || entry.Episode.Download?.IsDone == true)
                {
                    try
                    {
                        _chapters = await _model.Client.EpisodeChaptersAsync(episode.Id);
                    }
                    catch (SentinelServerException)
                    {
                    }
                }
                try
                {
                    _transcript = await _model.Client.EpisodeTranscriptAsync(episode.Id);
                }
                catch (SentinelServerException)
                {
                    // None yet - the tab offers Transcribe.
                }
            }
        }
        catch (Exception error)
        {
            _model.Log($"Now Playing extras failed: {error.Message}", DiagnosticLevel.Notice);
        }
        if (generation != _loadGeneration || _disposed) return;
        _tab = "";
        DrawTabs();
        BuildPanel(force: true);
    }

    private void BuildPanel(bool force)
    {
        if (!force) return;
        _panel.Children.Clear();
        _lines.Clear();
        _active = -1;
        switch (_tab)
        {
            case "lyrics":
                BuildLyrics();
                break;
            case "chapters":
                BuildChapters();
                break;
            case "transcript":
                BuildTranscript();
                break;
            case "bookmarks":
                _ = BuildBookmarksAsync();
                break;
            case "queue":
                BuildQueue();
                break;
        }
        OnTick();
    }

    private TextBlock Line(string text, double size, Action? seek)
    {
        var line = new TextBlock
        {
            Text = text,
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Palette.SecondaryText,
            Margin = new Thickness(0, 0, 0, size > 16 ? 12 : 6),
            Cursor = seek is null ? Cursors.Arrow : Cursors.Hand,
            Background = Brushes.Transparent,
        };
        if (seek is not null) line.MouseLeftButtonUp += (_, _) => seek();
        return line;
    }

    private void BuildLyrics()
    {
        if (_lyrics.Count == 0)
        {
            _panel.Children.Add(AudioUi.Caption(_player.Current?.Track?.HasLyrics == false ? "No lyrics found for this song." : "Looking for lyrics…"));
            return;
        }
        var synced = LyricsTimeline.IsSynced(_lyrics);
        if (!synced) _panel.Children.Add(AudioUi.Caption("These lyrics aren't timed, so they don't follow the song.", 0));
        foreach (var lyric in _lyrics)
        {
            var time = lyric.Time;
            var line = Line(string.IsNullOrWhiteSpace(lyric.Text) ? "♪" : lyric.Text, 20, time is { } t ? () => _player.Seek(t) : null);
            if (!synced) line.Foreground = Palette.PrimaryText;
            _lines.Add(line);
            _panel.Children.Add(line);
        }
    }

    private void BuildChapters()
    {
        foreach (var chapter in _chapters)
        {
            var start = chapter.StartSeconds;
            var line = Line($"{AudioTime.Clock(chapter.StartSeconds)}   {chapter.Title}", 14, () => _player.Seek(start));
            _lines.Add(line);
            _panel.Children.Add(line);
        }
        var row = AudioUi.Row(
            AudioUi.TextButton("◀ Previous chapter", () => _player.Seek(ChapterTimeline.PreviousStart(_chapters, _player.Position))),
            AudioUi.TextButton("Next chapter ▶", () =>
            {
                if (ChapterTimeline.NextStart(_chapters, _player.Position) is { } next) _player.Seek(next);
            }));
        row.Margin = new Thickness(0, 8, 0, 0);
        _panel.Children.Add(row);
    }

    private void BuildTranscript()
    {
        var episode = _player.Current?.Episode;
        if (_transcript is not { Segments.Count: > 0 } transcript)
        {
            _panel.Children.Add(AudioUi.Caption("No transcript for this episode yet."));
            if (episode is not null)
            {
                var transcribe = AudioUi.TextButton("Transcribe on the server", async () =>
                {
                    try
                    {
                        await _model.Client.TranscribeEpisodeAsync(episode.Id);
                        _panel.Children.Add(AudioUi.Caption("Queued. Open this tab again later.", 6));
                    }
                    catch (Exception error)
                    {
                        _panel.Children.Add(AudioUi.Caption(AppModel.UserFacingMessage(error), 6));
                    }
                });
                transcribe.HorizontalAlignment = HorizontalAlignment.Left;
                _panel.Children.Add(transcribe);
            }
            return;
        }
        _panel.Children.Add(_transcriptSearch);
        var query = _transcriptSearch.Text.Trim();
        var hits = query.Length > 0 ? TranscriptTimeline.Search(transcript.Segments, query).ToHashSet() : null;
        if (hits is not null) _panel.Children.Add(AudioUi.Caption(hits.Count == 0 ? "Not found." : $"{AudioUi.Count(hits.Count, "match", "matches")} - click one to play from there.", 0));
        for (var i = 0; i < transcript.Segments.Count; i++)
        {
            var segment = transcript.Segments[i];
            var start = segment.Start;
            var line = Line("", 14, () => _player.Seek(start));
            line.Inlines.Add(new Run($"{AudioTime.Clock(segment.Start)}  ") { Foreground = Palette.Cyan, FontSize = 11 });
            if (!string.IsNullOrEmpty(segment.Speaker)) line.Inlines.Add(new Run($"{segment.Speaker}: ") { FontWeight = FontWeights.SemiBold });
            line.Inlines.Add(new Run(segment.Text));
            if (hits is not null && !hits.Contains(i)) line.Visibility = Visibility.Collapsed;
            _lines.Add(line);
            _panel.Children.Add(line);
        }
    }

    private async Task BuildBookmarksAsync()
    {
        if (_player.Current?.Episode is not { } episode) return;
        var add = AudioUi.TextButton("+ Bookmark this moment", () => _ = BookmarkAsync(episode), primary: true);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 0, 0, 10);
        _panel.Children.Add(add);
        try
        {
            var bookmarks = await _model.Client.BookmarksAsync(episode.Id);
            if (_tab != "bookmarks") return;
            if (bookmarks.Count == 0) _panel.Children.Add(AudioUi.Caption("None yet."));
            foreach (var bookmark in bookmarks.OrderBy(b => b.PositionSeconds))
            {
                var at = bookmark.PositionSeconds;
                _panel.Children.Add(BookmarkUi.Row(_model, bookmark, () => _player.Seek(at), () => BuildPanel(force: true), showEpisode: false));
            }
        }
        catch (Exception error)
        {
            _panel.Children.Add(AudioUi.Caption(AppModel.UserFacingMessage(error)));
        }
    }

    private async Task BookmarkAsync(PodcastEpisode episode)
    {
        // Saved straight away, with no note - stopping to type would lose the moment. A note can
        // be added from the bookmark's own menu afterwards.
        var position = _player.Position;
        try
        {
            await _model.Client.AddBookmarkAsync(episode.Id, position, "");
            _tab = "bookmarks";
            DrawTabs();
            BuildPanel(force: true);
        }
        catch (Exception error)
        {
            MessageBox.Show(Window.GetWindow(this), AppModel.UserFacingMessage(error), "Bookmark", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BuildQueue()
    {
        var queue = _player.Queue;
        if (queue.Context?.Name is { Length: > 0 } name) _panel.Children.Add(AudioUi.Caption($"Playing from {name}{(queue.Shuffle ? " · shuffled" : "")}", 0));
        var upcoming = queue.Entries.Count - Math.Max(0, queue.Index + 1);
        var header = AudioUi.Row(new TextBlock { Text = AudioUi.Count(upcoming, "item", "items") + " up next", Foreground = Palette.SecondaryText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        if (upcoming > 0)
        {
            header.Children.Add(AudioUi.TextButton("Clear up next", () =>
            {
                for (var i = queue.Entries.Count - 1; i > queue.Index; i--) _player.RemoveAt(i);
            }));
        }
        _panel.Children.Add(header);

        for (var i = 0; i < queue.Entries.Count; i++)
        {
            var index = i;
            var entry = queue.Entries[i];
            var current = i == queue.Index;
            var grid = new Grid { Opacity = i < queue.Index ? 0.55 : 1 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(AudioUi.Art(entry.ArtUrl, 36, entry.IsEpisode ? AudioUi.PodcastGlyph : AudioUi.MusicGlyph));
            var text = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = entry.Title, Foreground = current ? Palette.Green : Palette.PrimaryText, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = $"{entry.Subtitle} · {AudioTime.Clock(entry.DurationSeconds)}", Foreground = Palette.SecondaryText, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var up = AudioUi.GlyphButton("", "Move up", () => _player.Move(index, index - 1), 10);
            up.IsEnabled = i > 0;
            var down = AudioUi.GlyphButton("", "Move down", () => _player.Move(index, index + 1), 10);
            down.IsEnabled = i < queue.Entries.Count - 1;
            buttons.Children.Add(up);
            buttons.Children.Add(down);
            buttons.Children.Add(AudioUi.GlyphButton("", "Remove from the queue", () => _player.RemoveAt(index), 10));
            Grid.SetColumn(buttons, 2);
            grid.Children.Add(buttons);
            var row = AudioUi.ClickableRow(grid, () => _ = _player.JumpToAsync(index), null, entry.Title);
            _panel.Children.Add(row);
            if (current) _lines.Add(row);
        }
        if (queue.Entries.Count == 0) _panel.Children.Add(AudioUi.Caption("The queue is empty."));
    }

    // ---------------------------------------------------------------- Following playback

    private void OnTick()
    {
        if (_disposed || !_player.HasItem) return;
        var position = _player.Position;
        var duration = _player.Duration;
        _scrubber.Update(position, duration);
        var glyph = (TextBlock)_play.Content;
        var wanted = _player.IsPlaying ? AudioUi.PauseGlyph : AudioUi.PlayGlyph;
        if (glyph.Text != wanted)
        {
            glyph.Text = wanted;
            _play.ToolTip = _player.IsPlaying ? "Pause" : "Play";
        }
        if (_player.Sleep.IsActive) _sleep.ToolTip = _player.Sleep.Label(DateTimeOffset.UtcNow, _player.IsEpisode);

        if (_player.Current?.IsEpisode == true && _chapters.Count > 0)
        {
            var chapter = ChapterTimeline.ActiveIndex(_chapters, position);
            _chapterLine.Text = chapter >= 0 ? _chapters[chapter].Title : "";
        }

        var active = _tab switch
        {
            "lyrics" => LyricsTimeline.ActiveIndex(_lyrics, position),
            "transcript" when _transcript is not null => TranscriptTimeline.ActiveIndex(_transcript.Segments, position),
            "chapters" => ChapterTimeline.ActiveIndex(_chapters, position),
            _ => -1,
        };
        if (active == _active || _tab == "queue") return;
        if (_active >= 0 && _active < _lines.Count && _lines[_active] is TextBlock old)
        {
            old.Foreground = Palette.SecondaryText;
            old.FontWeight = FontWeights.Normal;
        }
        _active = active;
        if (active < 0 || active >= _lines.Count || _lines[active] is not TextBlock line) return;
        line.Foreground = Palette.PrimaryText;
        line.FontWeight = _tab == "lyrics" ? FontWeights.SemiBold : FontWeights.Normal;
        if (_tab == "chapters") line.Foreground = Palette.Green;
        if ((DateTime.UtcNow - _userScrolledAt).TotalSeconds > 4 && line.Visibility == Visibility.Visible) KeepInView(line);
    }

    /// <summary>Scrolls the panel so the active line sits about a third of the way down.</summary>
    private void KeepInView(FrameworkElement line)
    {
        try
        {
            var top = line.TransformToAncestor(_panel).Transform(new Point(0, 0)).Y;
            var target = Math.Max(0, top - _panelScroller.ViewportHeight / 3);
            if (Math.Abs(target - _panelScroller.VerticalOffset) < 1) return;
            _autoScrolling = true;
            _panelScroller.ScrollToVerticalOffset(target);
        }
        catch (InvalidOperationException)
        {
            // Not laid out yet; the next tick tries again.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _player.Changed -= OnChanged;
        _player.Ticked -= OnTick;
    }
}
