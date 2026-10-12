using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>
/// The flashing NWS warning banner across the top of the main window - the Windows twin of the
/// dashboard kiosk's kiosk-weather-alert.js (and its .kwx-* styles). It floats over every section
/// without pushing anything down or blocking the rest of the window. Click it for the radar; ×
/// dismisses it. Either way the caller marks what it showed as seen.
/// </summary>
/// <remarks>
/// A borderless window owned by the main one rather than an element inside it: the live video and
/// the radar map are WebView2 controls, which always draw on top of WPF content in the same window
/// (the "airspace" rule), so an in-window overlay would vanish behind exactly the views that matter
/// most during a storm. Owned, it stays above the main window, minimises with it and never sits on
/// top of other apps. It never takes focus (WS_EX_NOACTIVATE), so clicking it leaves the main
/// window active.
/// </remarks>
public sealed class WeatherWarningBanner : Window
{
    private const double MaxBannerWidth = 760;
    private const double ShadowRoom = 24;   // transparent margin the glow draws into
    private const double TopGap = 10;

    private readonly Window _owner;
    private readonly FrameworkElement _anchor;
    private readonly Border _card;
    private readonly DropShadowEffect _glow;
    private readonly Run _event = new() { FontWeight = FontWeights.Bold, FontSize = 17 };
    private readonly Run _until = new() { FontSize = 14 };
    private readonly Run _more = new() { FontSize = 14 };
    private readonly TextBlock _headline;
    private readonly TextBlock _hint;
    private Storyboard? _pulse;
    private string? _level;
    private bool _wanted;

    /// <summary>The banner itself was clicked: show the radar.</summary>
    public event Action? Opened;

    /// <summary>× was clicked.</summary>
    public event Action? Dismissed;

    /// <param name="owner">The main window.</param>
    /// <param name="anchor">The element whose top edge the banner centres on (the window's content).</param>
    public WeatherWarningBanner(Window owner, FrameworkElement anchor)
    {
        _owner = owner;
        _anchor = anchor;

        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.Height;
        Title = "Weather warning";
        FontFamily = owner.FontFamily;

        var icon = new Viewbox
        {
            Width = 30,
            Height = 30,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Canvas
            {
                Width = 24,
                Height = 24,
                Children =
                {
                    IconStroke("M12,3 L22,21 L2,21 Z"),
                    IconStroke("M12,10 L12,15"),
                    IconStroke("M12,18 L12.01,18"),
                },
            },
        };

        _more.Foreground = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));
        var line = new TextBlock
        {
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Effect = TextShadow(),
        };
        line.Inlines.Add(_event);
        line.Inlines.Add(new Run("  "));
        line.Inlines.Add(_until);
        line.Inlines.Add(new Run("  "));
        line.Inlines.Add(_more);

        _headline = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF)),
            FontSize = 12.5,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Effect = TextShadow(),
        };

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        text.Children.Add(line);
        text.Children.Add(_headline);

        _hint = new TextBlock { Foreground = Brushes.White, FontSize = 11.5, FontWeight = FontWeights.SemiBold };
        var hint = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 5, 10, 5),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _hint,
        };

        var mainContent = new Grid();
        mainContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mainContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mainContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(hint, 2);
        mainContent.Children.Add(icon);
        mainContent.Children.Add(text);
        mainContent.Children.Add(hint);

        var main = new Border
        {
            Background = Brushes.Transparent,   // hit-testable across its whole area
            Padding = new Thickness(14, 8, 14, 8),
            MinHeight = 56,
            Cursor = Cursors.Hand,
            Child = mainContent,
            ToolTip = "Show the radar",
        };
        main.MouseLeftButtonUp += (_, e) => { e.Handled = true; Opened?.Invoke(); };

        var close = new Border
        {
            Width = 56,
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1, 0, 0, 0),
            CornerRadius = new CornerRadius(0, 10, 10, 0),
            Cursor = Cursors.Hand,
            ToolTip = "Dismiss",
            Child = new TextBlock
            {
                Text = "×",
                Foreground = Brushes.White,
                FontSize = 26,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -4, 0, 0),
            },
        };
        close.MouseLeftButtonUp += (_, e) => { e.Handled = true; Dismissed?.Invoke(); };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(close, 1);
        row.Children.Add(main);
        row.Children.Add(close);

        _glow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 8, Opacity = 0.9 };
        _card = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(2),
            Child = row,
            Effect = _glow,
        };

        Content = new Border { Padding = new Thickness(ShadowRoom), Child = _card };

        SourceInitialized += (_, _) => MakeNonActivating();
        owner.LocationChanged += (_, _) => Reposition();
        owner.SizeChanged += (_, _) => Reposition();
        owner.StateChanged += (_, _) => ApplyVisibility();
        owner.IsVisibleChanged += (_, _) => ApplyVisibility();
        anchor.SizeChanged += (_, _) => Reposition();
        anchor.IsVisibleChanged += (_, _) => ApplyVisibility();
    }

    /// <summary>Shows (or updates) the banner for the worst unseen alert.</summary>
    /// <param name="top">The alert it names.</param>
    /// <param name="until">"until 3:45 PM", or "".</param>
    /// <param name="more">"+2 more", or "".</param>
    /// <param name="onRadar">The radar is already on screen, so a click just says "got it".</param>
    public void ShowWarning(WeatherWarning top, string until, string more, bool onRadar)
    {
        _event.Text = WeatherWarningTracker.EventText(top);
        _until.Text = until;
        _more.Text = more;
        var headline = top.Headline.Trim();
        _headline.Text = headline;
        _headline.Visibility = headline.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _hint.Text = onRadar ? "GOT IT" : "CLICK FOR RADAR";
        SetLevel(top.Level);

        _wanted = true;
        ApplyVisibility();
    }

    public void HideWarning()
    {
        _wanted = false;
        ApplyVisibility();
    }

    /// <summary>The kiosk's colours: warning red, watch orange, advisory amber - each a dark and a bright.</summary>
    public static (Color Bright, Color Dark) Colours(string? level) => level switch
    {
        WeatherWarningLevels.Watch => (Color.FromRgb(0xFF, 0x8A, 0x2A), Color.FromRgb(0x6E, 0x33, 0x00)),
        WeatherWarningLevels.Advisory => (Color.FromRgb(0xFF, 0xB5, 0x45), Color.FromRgb(0x5C, 0x41, 0x00)),
        _ => (Color.FromRgb(0xFF, 0x4D, 0x5E), Color.FromRgb(0x7A, 0x0F, 0x1A)),
    };

    private void SetLevel(string level)
    {
        if (_level == level && _pulse is not null) return;
        _level = level;
        var (bright, dark) = Colours(level);

        _pulse?.Stop(_card);
        _card.Background = new SolidColorBrush(dark);
        _card.BorderBrush = new SolidColorBrush(bright);
        _glow.Color = bright;

        // Windows' "Show animations" off: hold the dark colour, as the dashboard does for
        // prefers-reduced-motion.
        if (!SystemParameters.ClientAreaAnimation)
        {
            _pulse = null;
            return;
        }

        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var duration = new Duration(TimeSpan.FromSeconds(1.1));
        var colour = new ColorAnimation(dark, bright, duration) { EasingFunction = ease };
        Storyboard.SetTarget(colour, _card);
        Storyboard.SetTargetProperty(colour, new PropertyPath("(Border.Background).(SolidColorBrush.Color)"));
        var glow = new DoubleAnimation(8, 34, duration) { EasingFunction = ease };
        Storyboard.SetTarget(glow, _card);
        Storyboard.SetTargetProperty(glow, new PropertyPath("(UIElement.Effect).(DropShadowEffect.BlurRadius)"));

        _pulse = new Storyboard { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        _pulse.Children.Add(colour);
        _pulse.Children.Add(glow);
        _pulse.Begin(_card, isControllable: true);
    }

    private void ApplyVisibility()
    {
        var show = _wanted
                   && _owner.IsVisible
                   && _owner.WindowState != WindowState.Minimized
                   && _anchor.IsVisible;
        if (show)
        {
            if (!IsVisible) Show();
            _pulse?.Resume(_card);
            Reposition();
        }
        else if (IsVisible)
        {
            _pulse?.Pause(_card);
            Hide();
        }
    }

    private void Reposition()
    {
        if (!IsVisible || PresentationSource.FromVisual(_anchor) is not { CompositionTarget: { } target }) return;

        // The anchor's top-left and size in screen DIPs.
        var topLeft = target.TransformFromDevice.Transform(_anchor.PointToScreen(new Point(0, 0)));
        var width = Math.Max(200, Math.Min(MaxBannerWidth, _anchor.ActualWidth - 32));

        Width = width + ShadowRoom * 2;
        Left = topLeft.X + (_anchor.ActualWidth - width) / 2 - ShadowRoom;
        Top = topLeft.Y + TopGap - ShadowRoom;
    }

    // ---------------------------------------------------------------- Pieces

    private static Path IconStroke(string data) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = Brushes.White,
        StrokeThickness = 2,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
    };

    private static DropShadowEffect TextShadow() =>
        new() { ShadowDepth = 1, BlurRadius = 2, Opacity = 0.6, Color = Colors.Black, Direction = 270 };

    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    private void MakeNonActivating()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style | WsExNoActivate | WsExToolWindow));
    }
}
