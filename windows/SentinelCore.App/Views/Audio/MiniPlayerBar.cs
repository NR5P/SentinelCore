using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SentinelCore.App.Audio;
using SentinelCore.Client;
using SentinelCore.Client.Audio;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views.Audio;

/// <summary>
/// The mini player along the bottom of the window, visible from every section while something is
/// playing (or another device offers "continue here"). Clicking the title opens Now Playing.
/// </summary>
public sealed class MiniPlayerBar : UserControl
{
    private readonly PlayerController _player;
    private readonly INavigator _navigator;
    private readonly ContentControl _art = new() { Width = 46, Height = 46 };
    private readonly TextBlock _title = new() { Foreground = Palette.PrimaryText, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _subtitle = new() { Foreground = Palette.SecondaryText, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _play;
    private readonly Button _back15;
    private readonly Button _forward30;
    private readonly Button _target;
    private readonly TextBlock _sleep = new() { Foreground = Palette.Amber, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 1, Width = 90, VerticalAlignment = VerticalAlignment.Center };
    private readonly PlayerParts.Scrubber _scrubber;
    private readonly Border _playerRow;
    private readonly Border _banner;
    private readonly TextBlock _bannerText = new() { Foreground = Palette.PrimaryText, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _notice = new() { Foreground = Palette.Amber, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 4, 16, 0), Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _continuePoll = new() { Interval = TimeSpan.FromSeconds(60) };
    private NowPlaying? _offer;
    private string? _dismissedOffer;
    private string? _artFor;

    public MiniPlayerBar(PlayerController player, INavigator navigator)
    {
        _player = player;
        _navigator = navigator;
        _scrubber = new PlayerParts.Scrubber(player);

        _play = AudioUi.GlyphButton(AudioUi.PlayGlyph, "Play", player.TogglePlay, 16, Palette.Green);
        _back15 = AudioUi.GlyphButton("", "Back 15 seconds", () => player.SkipBy(-PodcastPlayback.SkipBackSeconds));
        _forward30 = AudioUi.GlyphButton("", "Forward 30 seconds", () => player.SkipBy(PodcastPlayback.SkipForwardSeconds));
        _target = AudioUi.GlyphButton(AudioUi.SpeakerGlyph, "Play on…", () => new SpeakerPickerWindow(player.Model) { Owner = Window.GetWindow(this) }.ShowDialog());

        // ---- the player row
        var grid = new Grid { Margin = new Thickness(12, 8, 12, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 180 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new Grid { Cursor = Cursors.Hand, Background = System.Windows.Media.Brushes.Transparent, ToolTip = "Open Now Playing" };
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        info.Children.Add(_art);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
        text.Children.Add(_title);
        text.Children.Add(_subtitle);
        Grid.SetColumn(text, 1);
        info.Children.Add(text);
        info.MouseLeftButtonUp += (_, _) => OpenNowPlaying();
        grid.Children.Add(info);

        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(AudioUi.GlyphButton(AudioUi.PreviousGlyph, "Previous", () => _ = player.PreviousAsync()));
        buttons.Children.Add(_back15);
        buttons.Children.Add(_play);
        buttons.Children.Add(_forward30);
        buttons.Children.Add(AudioUi.GlyphButton(AudioUi.NextGlyph, "Next", () => _ = player.NextAsync()));
        center.Children.Add(buttons);
        var scrub = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        scrub.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        scrub.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scrub.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        scrub.Children.Add(_scrubber.Elapsed);
        _scrubber.Slider.Margin = new Thickness(8, 0, 8, 0);
        Grid.SetColumn(_scrubber.Slider, 1);
        scrub.Children.Add(_scrubber.Slider);
        Grid.SetColumn(_scrubber.Left, 2);
        scrub.Children.Add(_scrubber.Left);
        center.Children.Add(scrub);
        Grid.SetColumn(center, 1);
        grid.Children.Add(center);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        right.Children.Add(_sleep);
        right.Children.Add(_target);
        right.Children.Add(new TextBlock { Text = AudioUi.VolumeGlyph, FontFamily = AudioUi.Icons, Foreground = Palette.SecondaryText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) });
        AudioUi.AutomationName(_volume, "Volume");
        _volume.Value = player.UserVolume;
        _volume.ValueChanged += (_, args) =>
        {
            if (Math.Abs(args.NewValue - player.UserVolume) > 0.001 && _volume.IsMouseCaptureWithin | _volume.IsKeyboardFocusWithin) player.UserVolume = args.NewValue;
        };
        _volume.PreviewMouseLeftButtonUp += (_, _) => player.UserVolume = _volume.Value;
        _volume.IsMoveToPointEnabled = true;
        right.Children.Add(_volume);
        right.Children.Add(AudioUi.GlyphButton(AudioUi.ExpandGlyph, "Now Playing", OpenNowPlaying));
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        _playerRow = new Border { Child = grid };

        // ---- the "continue from another device" banner (§7)
        var bannerGrid = new Grid { Margin = new Thickness(16, 6, 12, 6) };
        bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var bannerLead = new StackPanel { Orientation = Orientation.Horizontal };
        bannerLead.Children.Add(new TextBlock { Text = "", FontFamily = AudioUi.Icons, Foreground = Palette.Cyan, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        bannerLead.Children.Add(_bannerText);
        bannerGrid.Children.Add(bannerLead);
        var bannerButtons = new StackPanel { Orientation = Orientation.Horizontal };
        bannerButtons.Children.Add(AudioUi.TextButton("Continue here", () =>
        {
            if (_offer is null) return;
            var offer = _offer;
            _offer = null;
            Apply();
            _ = _player.ContinueFromAsync(offer);
        }, primary: true));
        bannerButtons.Children.Add(AudioUi.GlyphButton("", "Not now", () =>
        {
            _dismissedOffer = OfferKey(_offer);
            _offer = null;
            Apply();
        }, 10));
        Grid.SetColumn(bannerButtons, 1);
        bannerGrid.Children.Add(bannerButtons);
        _banner = new Border { Child = bannerGrid, Background = AudioUi.Resource("CardBackground"), BorderBrush = AudioUi.Resource("CardBorder"), BorderThickness = new Thickness(0, 1, 0, 0), Visibility = Visibility.Collapsed };

        var stack = new StackPanel();
        stack.Children.Add(_banner);
        stack.Children.Add(_notice);
        stack.Children.Add(_playerRow);
        Content = new Border
        {
            Background = AudioUi.Resource("SidebarBackground"),
            BorderBrush = AudioUi.Resource("CardBorder"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = stack,
        };

        player.Changed += () => Dispatcher.BeginInvoke(Apply);
        player.Ticked += OnTick;
        _continuePoll.Tick += async (_, _) => await CheckContinueAsync();
        Loaded += async (_, _) =>
        {
            Apply();
            _continuePoll.Start();
            await CheckContinueAsync();
        };
    }

    private void OpenNowPlaying()
    {
        if (!_player.HasItem) return;
        _navigator.Push(new NowPlayingView(_player, _navigator), "Now Playing");
    }

    private static string? OfferKey(NowPlaying? offer) =>
        offer is null ? null : $"{offer.DeviceId}|{offer.Queue.ElementAtOrDefault(offer.Index)?.Id}";

    /// <summary>Asks for another device worth continuing from - only while nothing plays here.</summary>
    public async Task CheckContinueAsync()
    {
        if (_player.IsPlaying || _player.RemoteTarget is not null)
        {
            _offer = null;
            Apply();
            return;
        }
        var offer = await _player.FindContinueAsync();
        _offer = offer is not null && OfferKey(offer) != _dismissedOffer && !(_player.Current?.Id == offer.Queue.ElementAtOrDefault(offer.Index)?.Id && _player.Current is not null) ? offer : null;
        Apply();
    }

    private void Apply()
    {
        var hasItem = _player.HasItem;
        _playerRow.Visibility = hasItem ? Visibility.Visible : Visibility.Collapsed;
        _banner.Visibility = _offer is not null ? Visibility.Visible : Visibility.Collapsed;
        if (_offer is not null)
        {
            var current = _offer.Queue.ElementAtOrDefault(_offer.Index);
            _bannerText.Text = $"{ContinueListening.Banner(_offer)} · continue here?";
        }
        _notice.Text = _player.Notice ?? _player.SpeakerHealth ?? "";
        _notice.Visibility = string.IsNullOrEmpty(_notice.Text) ? Visibility.Collapsed : Visibility.Visible;
        Visibility = hasItem || _offer is not null || _notice.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        if (!hasItem) return;

        string title, subtitle;
        string? art;
        if (_player.RemoteTarget is { } speaker)
        {
            var state = _player.RemoteState;
            title = state?.Title ?? "Nothing playing";
            subtitle = $"On {speaker.Name}{(string.IsNullOrEmpty(state?.Subtitle) ? "" : $" · {state!.Subtitle}")}";
            art = state?.ArtUrl;
            _target.ToolTip = $"Playing on {speaker.Name} - click to change";
            ((TextBlock)_target.Content).Foreground = Palette.Green;
        }
        else
        {
            var entry = _player.Current!;
            title = entry.Title;
            subtitle = entry.Subtitle ?? "";
            art = entry.ArtUrl;
            _target.ToolTip = "Play on another speaker…";
            ((TextBlock)_target.Content).Foreground = Palette.PrimaryText;
        }
        _title.Text = title;
        _subtitle.Text = subtitle;
        if (art != _artFor)
        {
            _artFor = art;
            _art.Content = AudioUi.Art(art, 46, _player.IsEpisode ? AudioUi.PodcastGlyph : AudioUi.MusicGlyph);
        }
        var podcast = _player.IsEpisode || _player.RemoteState?.Kind == "podcast";
        _back15.Visibility = podcast ? Visibility.Visible : Visibility.Collapsed;
        _forward30.Visibility = podcast ? Visibility.Visible : Visibility.Collapsed;
        ((TextBlock)_play.Content).Text = _player.IsPlaying ? AudioUi.PauseGlyph : AudioUi.PlayGlyph;
        _play.ToolTip = _player.IsPlaying ? "Pause" : "Play";
        AudioUi.AutomationName(_play, _player.IsPlaying ? "Pause" : "Play");
        if (!_volume.IsMouseCaptureWithin) _volume.Value = _player.UserVolume;
        OnTick();
    }

    private void OnTick()
    {
        if (!_player.HasItem) return;
        _scrubber.Update(_player.Position, _player.Duration);
        _sleep.Text = _player.Sleep.Label(DateTimeOffset.UtcNow, _player.IsEpisode);
        var playing = _player.IsPlaying;
        var glyph = (TextBlock)_play.Content;
        var wanted = playing ? AudioUi.PauseGlyph : AudioUi.PlayGlyph;
        if (glyph.Text != wanted) glyph.Text = wanted;
    }
}
