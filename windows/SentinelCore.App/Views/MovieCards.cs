using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SentinelCore.App.Design;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>
/// The pieces the Movies screens share: a 2:3 poster card with its badges and progress bar, a
/// poster frame with a text placeholder, and the horizontal shelf the Home tab stacks.
/// </summary>
internal static class MovieCards
{
    public const double PosterWidth = 150;
    public const double PosterHeight = 225;

    private static Brush Resource(FrameworkElement owner, string key) => (Brush)owner.FindResource(key);

    /// <summary>
    /// A button that draws nothing but its content - a whole card that takes focus, Enter and
    /// Space, and a click, without the app's button chrome around it.
    /// </summary>
    public static Button PlainButton(UIElement content, Action onClick, string? toolTip = null)
    {
        var template = new ControlTemplate(typeof(Button))
        {
            VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)),
        };
        var button = new Button
        {
            Content = content,
            Template = template,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            FocusVisualStyle = null,
            ToolTip = toolTip,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>
    /// Artwork in a rounded frame, with the title written underneath it: a library without TMDB
    /// artwork (or a fetch that failed) shows a readable card rather than a grey hole.
    /// </summary>
    public static Border Artwork(FrameworkElement owner, string? path, string placeholder, double width, double height, int decodeWidth)
    {
        var grid = new Grid
        {
            Width = width,
            Height = height,
            Clip = new RectangleGeometry(new Rect(0, 0, width, height), 8, 8),
        };
        grid.Children.Add(new TextBlock
        {
            Text = placeholder,
            Foreground = Palette.SecondaryText,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(12),
        });
        if (!string.IsNullOrEmpty(path))
        {
            grid.Children.Add(new RemoteImage
            {
                DecodeWidth = decodeWidth,
                Stretch = Stretch.UniformToFill,
                SourcePath = path,
            });
        }

        return new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(8),
            Background = Resource(owner, "CardBackground"),
            BorderBrush = Resource(owner, "CardBorder"),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
    }

    /// <summary>A thin progress bar laid over the bottom of a poster or still.</summary>
    public static FrameworkElement ProgressStrip(double fraction, double width)
    {
        var track = new Grid
        {
            Height = 4,
            Width = width - 16,
            Margin = new Thickness(8, 0, 8, 8),
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        track.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), CornerRadius = new CornerRadius(2) });
        track.Children.Add(new Border
        {
            Background = Palette.Green,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = Math.Max(4, (width - 16) * Math.Clamp(fraction, 0, 1)),
        });
        return track;
    }

    /// <summary>A small rounded label in a poster's corner.</summary>
    public static Border Badge(string text, Brush background, Brush foreground, HorizontalAlignment horizontal, VerticalAlignment vertical) =>
        new()
        {
            Background = background,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 2, 7, 3),
            Margin = new Thickness(7),
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            Child = new TextBlock { Text = text, Foreground = foreground, FontSize = 11, FontWeight = FontWeights.SemiBold },
        };

    /// <summary>
    /// A 2:3 poster card: artwork, a watched tick or an unwatched-episode count, a progress bar,
    /// and the title and one line of facts underneath.
    /// </summary>
    public static Button PosterCard(FrameworkElement owner, MediaItemSummary item, Action onOpen)
    {
        var title = item.IsEpisode && !string.IsNullOrWhiteSpace(item.ShowTitle) ? item.ShowTitle! : item.Title;
        var caption = item.IsEpisode
            ? item.EpisodeLabel
            : item.IsShow
                ? ShowCaption(item)
                : item.Year?.ToString() ?? MediaPlaybackRules.RuntimeText(item);

        var frame = Artwork(owner, item.PosterUrl ?? item.ThumbUrl, title, PosterWidth, PosterHeight, 300);
        var overlay = new Grid();
        overlay.Children.Add(frame);

        if (item.IsWatched)
        {
            overlay.Children.Add(Badge("✓", Palette.Green, new SolidColorBrush(Color.FromRgb(0x04, 0x12, 0x0A)),
                HorizontalAlignment.Right, VerticalAlignment.Top));
        }
        else if (item.IsShow && item.UnwatchedEpisodeCount is { } unwatched && unwatched > 0)
        {
            overlay.Children.Add(Badge(unwatched.ToString(), Palette.Cyan, new SolidColorBrush(Color.FromRgb(0x04, 0x12, 0x14)),
                HorizontalAlignment.Right, VerticalAlignment.Top));
        }

        if (item.IsEpisode && item.EpisodeCode.Length > 0)
        {
            overlay.Children.Add(Badge(item.EpisodeCode, new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), Brushes.White,
                HorizontalAlignment.Left, VerticalAlignment.Top));
        }

        if (MediaPlaybackRules.Fraction(item.Progress) is { } fraction && fraction > 0)
        {
            overlay.Children.Add(ProgressStrip(fraction, PosterWidth));
        }

        var body = new StackPanel { Width = PosterWidth };
        body.Children.Add(overlay);
        body.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Palette.PrimaryText,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 8, 2, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        body.Children.Add(new TextBlock
        {
            Text = caption,
            Foreground = Palette.SecondaryText,
            FontSize = 11.5,
            Margin = new Thickness(2, 2, 2, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var button = PlainButton(body, onOpen, item.IsEpisode ? $"{title} · {item.EpisodeLabel}" : title);
        button.Margin = new Thickness(0, 0, 16, 18);
        button.VerticalAlignment = VerticalAlignment.Top;
        Highlight(button, frame, owner);
        return button;
    }

    private static string ShowCaption(MediaItemSummary show)
    {
        var parts = new List<string>();
        if (show.Year is { } year) parts.Add(year.ToString());
        if (show.SeasonCount is { } seasons && seasons > 0) parts.Add(seasons == 1 ? "1 season" : $"{seasons} seasons");
        return string.Join(" · ", parts);
    }

    /// <summary>The poster's border lights up green under the mouse and under keyboard focus.</summary>
    public static void Highlight(Control control, Border frame, FrameworkElement owner)
    {
        var normal = Resource(owner, "CardBorder");
        void Update() => frame.BorderBrush = control.IsMouseOver || control.IsKeyboardFocused ? Palette.Green : normal;
        control.MouseEnter += (_, _) => Update();
        control.MouseLeave += (_, _) => Update();
        control.GotKeyboardFocus += (_, _) => Update();
        control.LostKeyboardFocus += (_, _) => Update();
    }

    /// <summary>
    /// One row of the Home tab: a heading and a sideways-scrolling strip of cards. The mouse wheel
    /// keeps scrolling the page, not the strip - Shift+wheel moves the strip.
    /// </summary>
    public static FrameworkElement Shelf(FrameworkElement owner, string heading, IEnumerable<UIElement> cards, ScrollViewer page)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var card in cards) strip.Children.Add(card);

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = strip,
            Padding = new Thickness(0, 0, 0, 4),
        };
        scroller.PreviewMouseWheel += (_, args) =>
        {
            args.Handled = true;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - args.Delta);
            else page.ScrollToVerticalOffset(page.VerticalOffset - args.Delta);
        };

        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        section.Children.Add(new TextBlock
        {
            Text = heading.ToUpperInvariant(),
            Style = (Style)owner.FindResource("GroupHeading"),
            Margin = new Thickness(2, 6, 0, 10),
        });
        section.Children.Add(scroller);
        return section;
    }
}
