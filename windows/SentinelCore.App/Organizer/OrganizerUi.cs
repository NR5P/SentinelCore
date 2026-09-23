using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SentinelCore.App.Views;
using SentinelCore.Client.Organizer;

namespace SentinelCore.App.Organizer;

/// <summary>
/// Small builders shared by the Organizer's screens. Like the rest of this app the screens are
/// code-behind that builds its controls, and these keep that from being a wall of property setters.
/// </summary>
internal static class Ui
{
    public static TimeZoneInfo Zone => TimeZoneInfo.Local;

    public static Style Style(string key) => (Style)Application.Current.FindResource(key);

    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    public static TextBlock Text(string text, string style = "BodyText", double? size = null)
    {
        var block = new TextBlock { Text = text, Style = Style(style), TextWrapping = TextWrapping.Wrap };
        if (size is { } fontSize) block.FontSize = fontSize;
        return block;
    }

    public static TextBlock Caption(string text) => Text(text, "Caption");

    public static TextBlock Heading(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        Style = Style("GroupHeading"),
    };

    public static Button Button(string content, Action onClick, string? style = null, string? tooltip = null)
    {
        var button = new Button { Content = content, ToolTip = tooltip };
        if (style is not null) button.Style = Style(style);
        button.Click += (_, _) => onClick();
        return button;
    }

    public static Button Icon(string glyph, string tooltip, Action onClick)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12 },
            ToolTip = tooltip,
            Padding = new Thickness(8, 5, 8, 5),
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    public static TextBlock Glyph(string glyph, Brush? brush = null, double size = 12) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        FontSize = size,
        Foreground = brush ?? Palette.SecondaryText,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Border Chip(string text, Brush? background = null, Brush? foreground = null) => new()
    {
        Style = Style("Chip"),
        Background = background ?? new SolidColorBrush(Color.FromRgb(0x1E, 0x26, 0x2C)),
        Margin = new Thickness(0, 0, 6, 4),
        Padding = new Thickness(7, 2, 7, 2),
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = foreground ?? Palette.PrimaryText },
    };

    /// <summary>"#RRGGBB" as a brush; anything else is no color.</summary>
    public static Brush? ColorBrush(string? hex, byte alpha = 255)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            color.A = alpha;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The colors offered wherever something can be colored - the dashboard's set.</summary>
    public static readonly (string Name, string? Hex)[] Colors =
    {
        ("None", null), ("Green", "#38ff9c"), ("Blue", "#4a9dff"), ("Cyan", "#4ae8ff"), ("Purple", "#c98bff"),
        ("Pink", "#ff6fb5"), ("Red", "#ff4d5e"), ("Orange", "#ff9a52"), ("Yellow", "#ffd84a"),
    };

    public static string SpaceLabel(OrganizerEngine engine, string spaceId) => engine.Space(spaceId) switch
    {
        { Kind: OrganizerSpaceKind.Personal } => "Only me",
        { } space => space.Name,
        _ => spaceId,
    };

    /// <summary>A picker of the spaces this person can put something in.</summary>
    public static ComboBox SpacePicker(OrganizerEngine engine, string selected)
    {
        var picker = new ComboBox { MinWidth = 180 };
        foreach (var space in engine.VisibleSpaces)
        {
            picker.Items.Add(new ComboBoxItem { Content = SpaceLabel(engine, space.Id), Tag = space.Id });
        }
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == selected)
                              ?? picker.Items.OfType<ComboBoxItem>().FirstOrDefault();
        return picker;
    }

    public static string? SelectedTag(ComboBox picker) => (picker.SelectedItem as ComboBoxItem)?.Tag as string;

    public static ComboBoxItem Option(string label, object? tag) => new() { Content = label, Tag = tag };

    public static void Select(ComboBox picker, object? tag) =>
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, tag))
                              ?? picker.Items.OfType<ComboBoxItem>().FirstOrDefault();

    public static string When(DateTimeOffset moment, bool allDay)
    {
        var local = OrganizerDates.InZone(moment, Zone);
        var today = DateTime.Today;
        var day = local.Date == today ? "Today"
            : local.Date == today.AddDays(1) ? "Tomorrow"
            : local.Date == today.AddDays(-1) ? "Yesterday"
            : local.Year == today.Year ? local.ToString("ddd d MMM", CultureInfo.CurrentCulture)
            : local.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        return allDay ? day : $"{day}, {local.ToString("t", CultureInfo.CurrentCulture)}";
    }

    public static string Size(double bytes) => bytes switch
    {
        < 1024 => $"{bytes:0} B",
        < 1024 * 1024 => $"{bytes / 1024:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024 / 1024:0.#} MB",
        _ => $"{bytes / 1024 / 1024 / 1024:0.##} GB",
    };

    /// <summary>Opens a link the way a browser would, and nothing that isn't a web or mail link.</summary>
    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url.Contains("://") || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url,
                UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "mailto"))
        {
            return;
        }
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public static void ShowError(DependencyObject? near, Exception error) =>
        MessageBox.Show(
            near is null ? null : Window.GetWindow(near) ?? Application.Current.MainWindow,
            error.Message,
            "Organizer",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

    public static bool Confirm(DependencyObject near, string message, string title = "Organizer") =>
        MessageBox.Show(Window.GetWindow(near) ?? Application.Current.MainWindow, message, title,
            MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    public static string ContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".heic" => "image/heic",
        ".bmp" => "image/bmp",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        ".csv" => "text/csv",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".mp3" => "audio/mpeg",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// Runs a UI refresh on the dispatcher, at most once per burst: the engine raises Changed from
    /// background threads, sometimes dozens of times for one sync.
    /// </summary>
    public sealed class Coalescer(FrameworkElement owner, Action refresh)
    {
        private int _queued;

        public void Request()
        {
            if (Interlocked.Exchange(ref _queued, 1) == 1) return;
            owner.Dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _queued, 0);
                refresh();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }
}
