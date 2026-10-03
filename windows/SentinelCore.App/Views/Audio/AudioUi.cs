using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SentinelCore.App.Audio;
using SentinelCore.App.Design;
using SentinelCore.Client;
using SentinelCore.Client.Audio;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;

namespace SentinelCore.App.Views.Audio;

/// <summary>
/// A Music or Podcasts screen: a scrolling column the screen fills in code, with a busy line, an
/// error line and a notice at the bottom. The Music &amp; Podcasts screens are built in code, the
/// same way the Organizer's are, rather than in XAML.
/// </summary>
public abstract class AudioPage : UserControl
{
    protected AudioPage(AppModel model, INavigator navigator)
    {
        Model = model;
        Navigator = navigator;
        Scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = Host,
        };
        Host.Margin = new Thickness(20, 16, 20, 24);
        NoticeText = new TextBlock
        {
            Foreground = Palette.Amber,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 6, 20, 8),
            Visibility = Visibility.Collapsed,
        };
        DockPanel.SetDock(NoticeText, Dock.Bottom);
        _dock.Children.Add(NoticeText);
        _dock.Children.Add(Scroller);
        Content = _dock;
    }

    private readonly DockPanel _dock = new();

    /// <summary>A fixed bar above the scrolling content (a section's title, tabs and search).</summary>
    protected void SetHeader(UIElement header)
    {
        DockPanel.SetDock(header, Dock.Top);
        _dock.Children.Insert(0, header);
    }

    protected AppModel Model { get; }
    protected INavigator Navigator { get; }
    protected ScrollViewer Scroller { get; }
    protected StackPanel Host { get; } = new();
    protected TextBlock NoticeText { get; }
    protected static PlayerController Player => PlayerController.Shared!;

    protected void Notice(string? message)
    {
        NoticeText.Text = message ?? "";
        NoticeText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    protected void Fail(Exception error) => Notice(AppModel.UserFacingMessage(error));

    /// <summary>Runs an action that talks to the server, turning a failure into the notice line.</summary>
    protected async Task Try(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    protected void Loading(string what = "Loading…")
    {
        Host.Children.Clear();
        Host.Children.Add(AudioUi.Caption(what));
    }
}

/// <summary>The pieces every Music and Podcasts screen is made of.</summary>
public static class AudioUi
{
    public static readonly FontFamily Icons = new("Segoe MDL2 Assets");

    // Segoe MDL2 glyphs.
    public const string PlayGlyph = "";
    public const string PauseGlyph = "";
    public const string NextGlyph = "";
    public const string PreviousGlyph = "";
    public const string ShuffleGlyph = "";
    public const string RepeatAllGlyph = "";
    public const string RepeatOneGlyph = "";
    public const string HeartGlyph = "";
    public const string HeartFillGlyph = "";
    public const string MoreGlyph = "";
    public const string BackGlyph = "";
    public const string QueueGlyph = "";
    public const string SpeakerGlyph = "";
    public const string VolumeGlyph = "";
    public const string TimerGlyph = "";
    public const string SearchGlyph = "";
    public const string RefreshGlyph = "";
    public const string MusicGlyph = "";
    public const string PodcastGlyph = "";
    public const string ExpandGlyph = "";
    public const string BookmarkGlyph = "";
    public const string DownloadGlyph = "";
    public const string CloudGlyph = "";
    public const string StarGlyph = "";
    public const string StarFillGlyph = "";

    public static Brush Resource(string key) => (Brush)Application.Current.FindResource(key);

    public static Style StyleOf(string key) => (Style)Application.Current.FindResource(key);

    public static TextBlock Title(string text, double size = 20) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = FontWeights.SemiBold,
        Foreground = Palette.PrimaryText,
        TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Caption(string text, double top = 0) => new()
    {
        Text = text,
        Style = StyleOf("Caption"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, top, 0, 4),
    };

    public static TextBlock Heading(string text, double top = 14) => new()
    {
        Text = text.ToUpperInvariant(),
        Style = StyleOf("GroupHeading"),
        Margin = new Thickness(2, top, 0, 8),
    };

    public static TextBlock Body(string text) => new()
    {
        Text = text,
        Style = StyleOf("BodyText"),
        TextWrapping = TextWrapping.Wrap,
    };

    public static Button TextButton(string text, Action click, string? tip = null, bool primary = false, Thickness? margin = null)
    {
        var button = new Button { Content = text, ToolTip = tip, Margin = margin ?? new Thickness(0, 0, 8, 0) };
        if (primary) button.Style = StyleOf("PrimaryButton");
        button.Click += (_, _) => click();
        return button;
    }

    public static Button GlyphButton(string glyph, string tip, Action click, double size = 14, Brush? foreground = null)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = size, Foreground = foreground ?? Palette.PrimaryText },
            ToolTip = tip,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationName(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    public static void AutomationName(FrameworkElement element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    public static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    public static WrapPanel Wrap(params UIElement[] children)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var child in children)
        {
            if (child is FrameworkElement element && element.Margin == default) element.Margin = new Thickness(0, 0, 8, 6);
            row.Children.Add(child);
        }
        return row;
    }

    // ---------------------------------------------------------------- Artwork

    /// <summary>Square artwork with a placeholder glyph underneath, over the app's session.</summary>
    public static Border Art(string? artPath, double size, string placeholderGlyph = MusicGlyph, bool round = false)
    {
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(new TextBlock
        {
            Text = placeholderGlyph,
            FontFamily = Icons,
            FontSize = Math.Max(14, size / 3),
            Foreground = Palette.SecondaryText,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (!string.IsNullOrEmpty(artPath))
        {
            var pixels = (int)Math.Clamp(size * 2, 64, 1200);
            grid.Children.Add(new RemoteImage
            {
                DecodeWidth = pixels,
                Stretch = Stretch.UniformToFill,
                SourcePath = SentinelClient.ArtPath(artPath, pixels),
            });
        }
        var radius = round ? size / 2 : Math.Min(8, size / 8);
        grid.Clip = new RectangleGeometry(new Rect(0, 0, size, size), radius, radius);
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(radius),
            Background = Resource("CardBackground"),
            BorderBrush = Resource("CardBorder"),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
    }

    /// <summary>A playlist's 2×2 mosaic of up to four album covers (one cover when there is only one).</summary>
    public static FrameworkElement Mosaic(IReadOnlyList<string> arts, double size, string placeholderGlyph = QueueGlyph)
    {
        if (arts.Count < 4) return Art(arts.FirstOrDefault(), size, placeholderGlyph);
        var grid = new UniformGrid { Rows = 2, Columns = 2, Width = size, Height = size };
        foreach (var art in arts.Take(4))
        {
            var pixels = (int)Math.Clamp(size, 64, 600);
            grid.Children.Add(new RemoteImage { DecodeWidth = pixels, Stretch = Stretch.UniformToFill, SourcePath = SentinelClient.ArtPath(art, pixels) });
        }
        grid.Clip = new RectangleGeometry(new Rect(0, 0, size, size), 8, 8);
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(8), Background = Resource("CardBackground"), Child = grid };
    }

    /// <summary>A card for a shelf or grid: artwork, a title and a line under it.</summary>
    public static Button Card(FrameworkElement art, string title, string? subtitle, Action open, double width = 150, ContextMenu? menu = null)
    {
        var body = new StackPanel { Width = width };
        body.Children.Add(art);
        body.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Palette.PrimaryText,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 8, 2, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            body.Children.Add(new TextBlock
            {
                Text = subtitle,
                Foreground = Palette.SecondaryText,
                FontSize = 11.5,
                Margin = new Thickness(2, 2, 2, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        var button = MovieCards.PlainButton(body, open, subtitle is null ? title : $"{title}\n{subtitle}");
        button.Margin = new Thickness(0, 0, 16, 16);
        button.VerticalAlignment = VerticalAlignment.Top;
        if (menu is not null) button.ContextMenu = menu;
        if (art is Border frame) MovieCards.Highlight(button, frame, button);
        return button;
    }

    /// <summary>A heading and a sideways strip of cards (the Home rows). Nothing when there are no cards.</summary>
    public static FrameworkElement? Shelf(string heading, IEnumerable<UIElement> cards, ScrollViewer page)
    {
        var list = cards.ToList();
        if (list.Count == 0) return null;
        return MovieCards.Shelf(page, heading, list, page);
    }

    // ---------------------------------------------------------------- Rows

    /// <summary>
    /// One song in a list: number or artwork, title and artist, album, love, length and a "…"
    /// menu. Double-click (or Enter) plays it; right-click opens the same menu.
    /// </summary>
    public static FrameworkElement TrackRow(
        MusicTrack track, string? number, Action play, ContextMenu menu, bool showArt = false, bool showAlbum = true,
        bool current = false)
    {
        var grid = new Grid { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(showArt ? 48 : 36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = showAlbum ? new GridLength(2, GridUnitType.Star) : new GridLength(0) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement lead = showArt
            ? Art(track.ArtUrl, 38)
            : new TextBlock { Text = number ?? "", Foreground = current ? Palette.Green : Palette.SecondaryText, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 12, 0) };
        lead.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(lead);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(showArt ? 8 : 0, 0, 8, 0) };
        text.Children.Add(new TextBlock { Text = track.Title, Foreground = current ? Palette.Green : Palette.PrimaryText, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = track.ArtistLine, Foreground = Palette.SecondaryText, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (showAlbum)
        {
            var album = new TextBlock { Text = track.Album ?? "", Foreground = Palette.SecondaryText, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(album, 2);
            grid.Children.Add(album);
        }

        var heart = LoveButton(track.Loved, loved => SetLovedAsync("tracks", track.Id, loved, value => track.Loved = value));
        Grid.SetColumn(heart, 3);
        grid.Children.Add(heart);

        var length = new TextBlock { Text = AudioTime.Clock(track.DurationSeconds), Foreground = Palette.SecondaryText, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(length, 4);
        grid.Children.Add(length);

        var more = GlyphButton(MoreGlyph, "More", () => { }, 12);
        more.Click += (_, _) =>
        {
            menu.PlacementTarget = more;
            menu.IsOpen = true;
        };
        more.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(more, 5);
        grid.Children.Add(more);

        return ClickableRow(grid, play, menu, $"{track.Title} by {track.ArtistLine}");
    }

    /// <summary>Wraps a row so it highlights, takes focus, plays on double-click or Enter, and has a context menu.</summary>
    public static Border ClickableRow(FrameworkElement content, Action activate, ContextMenu? menu, string name)
    {
        var border = new Border
        {
            Child = content,
            Padding = new Thickness(6, 5, 6, 5),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Focusable = true,
            FocusVisualStyle = null,
            ContextMenu = menu,
            Cursor = Cursors.Hand,
        };
        AutomationName(border, name);
        var hover = Resource("CardHoverBackground");
        void Update() => border.Background = border.IsMouseOver || border.IsKeyboardFocusWithin ? hover : Brushes.Transparent;
        border.MouseEnter += (_, _) => Update();
        border.MouseLeave += (_, _) => Update();
        border.GotKeyboardFocus += (_, _) => Update();
        border.LostKeyboardFocus += (_, _) => Update();
        border.MouseLeftButtonDown += (_, args) =>
        {
            border.Focus();
            if (args.ClickCount == 2)
            {
                args.Handled = true;
                activate();
            }
        };
        border.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true;
            activate();
        };
        return border;
    }

    /// <summary>A heart that toggles. The change shows at once and is undone if the server refuses.</summary>
    public static Button LoveButton(bool loved, Func<bool, Task<bool>> set)
    {
        var state = loved;
        var glyph = new TextBlock { FontFamily = Icons, FontSize = 13 };
        var button = new Button { Content = glyph, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(4, 0, 0, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        void Paint()
        {
            glyph.Text = state ? HeartFillGlyph : HeartGlyph;
            glyph.Foreground = state ? Palette.Red : Palette.SecondaryText;
            button.ToolTip = state ? "Loved - click to unlove" : "Love";
            AutomationName(button, state ? "Unlove" : "Love");
        }
        Paint();
        button.Click += async (_, _) =>
        {
            state = !state;
            Paint();
            state = await set(state);
            Paint();
        };
        return button;
    }

    /// <summary>Loves or unloves; returns what the server holds now (the old value on failure).</summary>
    public static async Task<bool> SetLovedAsync(string kind, string id, bool loved, Action<bool>? apply = null)
    {
        try
        {
            var result = await PlayerController.Shared!.Model.Client.SetLovedAsync(kind, id, loved);
            apply?.Invoke(result);
            return result;
        }
        catch (Exception error)
        {
            PlayerController.Shared!.Model.Log($"Could not change love: {error.Message}", DiagnosticLevel.Notice);
            return !loved;
        }
    }

    /// <summary>Five stars; clicking the lit last star clears the rating.</summary>
    public static FrameworkElement Stars(int rating, Func<int, Task<int>> set)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var current = rating;
        var buttons = new List<Button>();
        void Paint()
        {
            for (var i = 0; i < buttons.Count; i++)
            {
                var glyph = (TextBlock)buttons[i].Content;
                glyph.Text = i < current ? StarFillGlyph : StarGlyph;
                glyph.Foreground = i < current ? Palette.Amber : Palette.SecondaryText;
            }
        }
        for (var i = 1; i <= 5; i++)
        {
            var value = i;
            var button = new Button
            {
                Content = new TextBlock { FontFamily = Icons, FontSize = 13 },
                Padding = new Thickness(2),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ToolTip = $"{value} star{(value == 1 ? "" : "s")}",
            };
            AutomationName(button, $"Rate {value} stars");
            button.Click += async (_, _) =>
            {
                var target = current == value ? 0 : value;
                current = target;
                Paint();
                current = await set(target);
                Paint();
            };
            buttons.Add(button);
            panel.Children.Add(button);
        }
        Paint();
        return panel;
    }

    // ---------------------------------------------------------------- Menus

    public static MenuItem Item(string header, Action click, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>Everything a song can do, for its "…" button and its right-click.</summary>
    public static ContextMenu TrackMenu(MusicTrack track, INavigator navigator, IEnumerable<object>? extra = null)
    {
        var player = PlayerController.Shared!;
        var model = player.Model;
        var menu = new ContextMenu();
        menu.Items.Add(Item("Play next", () => player.PlayNext(new[] { new QueueEntry(track) })));
        menu.Items.Add(Item("Add to queue", () => player.AddToQueue(new[] { new QueueEntry(track) })));
        menu.Items.Add(Item("Add to playlist…", () => _ = AddToPlaylistAsync(new[] { track.Id })));
        menu.Items.Add(Item("Start radio", () => _ = player.StartRadioAsync("track", track.Id, track.Title)));
        menu.Items.Add(new Separator());
        if (!string.IsNullOrEmpty(track.AlbumId))
        {
            menu.Items.Add(Item($"Go to album", () => AudioNav.OpenAlbum(model, navigator, track.AlbumId!, track.Album ?? "Album")));
        }
        if (!string.IsNullOrEmpty(track.ArtistId))
        {
            menu.Items.Add(Item($"Go to artist", () => AudioNav.OpenArtist(model, navigator, track.ArtistId!, track.ArtistLine)));
        }
        menu.Items.Add(Item(track.Loved ? "Unlove" : "Love", () => _ = SetLovedAsync("tracks", track.Id, !track.Loved, value => track.Loved = value)));
        var rate = new MenuItem { Header = "Rate" };
        for (var stars = 0; stars <= 5; stars++)
        {
            var value = stars;
            var item = Item(value == 0 ? "No rating" : new string('★', value), () => _ = RateAsync("tracks", track.Id, value, v => track.Rating = v));
            item.IsChecked = track.Rating == value;
            rate.Items.Add(item);
        }
        menu.Items.Add(rate);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Save file…", () => _ = SaveFileAsync(SentinelClient.TrackDownloadPath(track.Id), $"{track.ArtistLine} - {track.Title}", ExtensionFor(track.Codec))));
        if (extra is not null)
        {
            menu.Items.Add(new Separator());
            foreach (var item in extra) menu.Items.Add(item);
        }
        return menu;
    }

    public static async Task<int> RateAsync(string kind, string id, int rating, Action<int>? apply = null)
    {
        try
        {
            var result = await PlayerController.Shared!.Model.Client.SetRatingAsync(kind, id, rating);
            apply?.Invoke(result);
            return result;
        }
        catch (Exception error)
        {
            PlayerController.Shared!.Model.Log($"Could not rate: {error.Message}", DiagnosticLevel.Notice);
            return rating;
        }
    }

    /// <summary>Picks one of the playlists the user can add to (or makes a new one) and adds the tracks.</summary>
    public static async Task AddToPlaylistAsync(IReadOnlyCollection<string> trackIds)
    {
        var model = PlayerController.Shared!.Model;
        var owner = Application.Current.MainWindow;
        try
        {
            var playlists = (await model.Client.MusicPlaylistsAsync()).Where(playlist => playlist.CanEditTracks).ToList();
            var labels = new List<string> { "New playlist…" };
            labels.AddRange(playlists.Select(playlist => playlist.Mine ? playlist.Name : $"{playlist.Name} ({playlist.OwnerName})"));
            var picker = new ListPickerWindow("Add to playlist", labels) { Owner = owner };
            if (picker.ShowDialog() != true) return;
            if (picker.SelectedIndex == 0)
            {
                var prompt = new TextPromptWindow("New playlist", "Name") { Owner = owner };
                if (prompt.ShowDialog() != true) return;
                await model.Client.CreateMusicPlaylistAsync(new MusicPlaylistInput { Name = prompt.Value, TrackIds = trackIds.ToList() });
                return;
            }
            await model.Client.AddToPlaylistAsync(playlists[picker.SelectedIndex - 1].Id, trackIds);
        }
        catch (Exception error)
        {
            MessageBox.Show(owner, AppModel.UserFacingMessage(error), "Add to playlist", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Asks where to save, then downloads a server path to that file.</summary>
    public static async Task SaveFileAsync(string serverPath, string suggestedName, string extension)
    {
        var model = PlayerController.Shared!.Model;
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(suggestedName.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        var dialog = new SaveFileDialog
        {
            FileName = (clean.Length == 0 ? "audio" : clean) + extension,
            Filter = "Audio files|*.m4a;*.mp3;*.flac;*.ogg;*.opus;*.wav|All files|*.*",
            AddExtension = true,
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        try
        {
            var bytes = await model.Client.DownloadLargeAsync(serverPath);
            await File.WriteAllBytesAsync(dialog.FileName, bytes);
        }
        catch (Exception error)
        {
            MessageBox.Show(Application.Current.MainWindow, AppModel.UserFacingMessage(error), "Save file", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static string? Prompt(string title, string prompt, string initial = "")
    {
        var window = new TextPromptWindow(title, prompt, initialValue: initial) { Owner = Application.Current.MainWindow };
        return window.ShowDialog() == true ? window.Value : null;
    }

    public static bool Confirm(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    /// <summary>A labelled tab strip: buttons where the selected one is the primary style.</summary>
    public static StackPanel Tabs(IReadOnlyList<(string Key, string Label)> tabs, string selected, Action<string> choose)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (key, label) in tabs)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 0), Tag = key };
            if (key == selected) button.Style = StyleOf("PrimaryButton");
            button.Click += (_, _) => choose(key);
            panel.Children.Add(button);
        }
        return panel;
    }

    /// <summary>The file extension a download of this codec is served as (anything converted is AAC in .m4a).</summary>
    public static string ExtensionFor(string? codec) => codec?.ToLowerInvariant() switch
    {
        "flac" => ".flac",
        "mp3" => ".mp3",
        "opus" => ".opus",
        "vorbis" => ".ogg",
        "wav" => ".wav",
        _ => ".m4a",
    };

    /// <summary>"12 songs · 48 min".</summary>
    public static string Count(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count:N0} {many}";
}
