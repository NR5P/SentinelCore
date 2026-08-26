using System.Windows;
using System.Windows.Media;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>
/// One row of the Events list, dressed for the screen: the wording, the badge and the colour that
/// go with an <see cref="ActivityItem"/>.
/// </summary>
/// <remarks>
/// Kept out of the client library on purpose - the wire types stay free of brushes and font styles,
/// so they remain testable without a UI framework and reusable by anything else that talks to this
/// server. Every phrasing choice here matches the Apple client's EventCard, so the same moment reads
/// the same on a phone, a Mac and this.
/// </remarks>
public sealed class ActivityRow
{
    public ActivityRow(ActivityItem item) => Item = item;

    public ActivityItem Item { get; }

    public string Id => Item.Id;
    public string Camera => Item.Camera;
    public DateTimeOffset OccurredAt => Item.OccurredAt;
    public string? ImageUrl => Item.ImageUrl;
    public bool Notified => Item.Notified;
    public bool IsAiAnalysisOff => Item.IsAiAnalysisOff;

    public Brush NotifiedBarBrush => Notified ? Palette.Green : Brushes.Transparent;

    /// <summary>
    /// Nothing analysed a motion row, so the zone that caught it stands in for a description -
    /// dimmed and italic, so a mixed list reads as "described" versus "not looked at".
    /// </summary>
    public string Description => Item.IsAnalyzed
        ? string.IsNullOrWhiteSpace(Item.Summary) ? "No description" : Item.Summary!
        : Item.ZoneName is { Length: > 0 } zone ? zone : "Motion in zone";

    public FontStyle DescriptionStyle => Item.IsAnalyzed ? FontStyles.Normal : FontStyles.Italic;

    public Brush DescriptionBrush => Item.IsAnalyzed ? Palette.PrimaryText : Palette.SecondaryText;

    public string FooterLabel => Item.IsAnalyzed
        ? TitleCase((Item.EventType ?? "unknown").Replace('_', ' '))
        : "Motion only";

    public Brush FooterBrush => Item.IsAnalyzed ? Palette.Cyan : Palette.SecondaryText;

    public string ConfidenceText =>
        Item.IsAnalyzed && Item.Confidence is { } confidence ? $"{Math.Round(confidence * 100)}%" : "";

    /// <summary>
    /// A row that simply shows nothing where a clip would be cannot be told apart from one whose
    /// clip is still being written, or one on a camera that was never recording them - and those
    /// want three different reactions. Same set, in the same words, as the dashboard.
    /// </summary>
    public string ClipBadge => Item.Clip switch
    {
        ActivityClipState.Ready => "Clip",
        ActivityClipState.Recording => "Recording…",
        ActivityClipState.Missing => "No clip",
        // "off" is a deliberate setting and no state at all is a row clips never applied to;
        // neither is worth a badge on every row. HasClip covers a server too old to send clipState.
        ActivityClipState.Off => "",
        _ => Item.HasClip ? "Clip" : "",
    };

    public Brush ClipBadgeBrush => Item.Clip switch
    {
        ActivityClipState.Recording or ActivityClipState.Missing => Palette.Amber,
        _ => Palette.Cyan,
    };

    public string ConfirmationGlyph => Item.Confirmation switch
    {
        "rejected" => "",      // warning triangle
        "pending" => "",       // ellipsis
        null or "confirmed" => "",
        _ => "",
    };

    public string ConfirmationTip => Item.Confirmation switch
    {
        "rejected" => "AI found nothing",
        "confirmed" or null => "",
        _ => "Awaiting AI verdict",
    };

    private static string TitleCase(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 ? "" : char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }
}

/// <summary>
/// The brand colours, reachable from code as well as from XAML. Frozen so they can be shared
/// across the whole visual tree without WPF copying them per use.
/// </summary>
public static class Palette
{
    public static Brush Green { get; } = Frozen(0x38, 0xFF, 0x9C);
    public static Brush Cyan { get; } = Frozen(0x4A, 0xE8, 0xFF);
    public static Brush Amber { get; } = Frozen(0xFF, 0xB5, 0x45);
    public static Brush Red { get; } = Frozen(0xFF, 0x4D, 0x5E);
    public static Brush Purple { get; } = Frozen(0xC9, 0x8B, 0xFF);
    public static Brush Orange { get; } = Frozen(0xFF, 0x9A, 0x52);
    public static Brush PrimaryText { get; } = Frozen(0xEC, 0xF1, 0xF4);
    public static Brush SecondaryText { get; } = Frozen(0x93, 0xA2, 0xAD);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
