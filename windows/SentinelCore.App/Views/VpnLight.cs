using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using SentinelCore.Client;

namespace SentinelCore.App.Views;

/// <summary>
/// How the movie-download VPN light is painted, shared by the pill on Movies and the line on
/// Downloads: a dot in the tone's colour, pulsing while it connects or checks. The wording comes
/// from <see cref="MediaDownloadText.VpnLook"/>.
/// </summary>
internal static class VpnLight
{
    public static Brush Brush(MediaDownloadText.VpnTone tone) => tone switch
    {
        MediaDownloadText.VpnTone.On => Palette.Green,
        MediaDownloadText.VpnTone.Busy => Palette.Cyan,
        MediaDownloadText.VpnTone.Disabled => Palette.Amber,
        MediaDownloadText.VpnTone.Problem => Palette.Red,
        _ => Palette.SecondaryText,
    };

    /// <summary>Colours the dot, and pulses it for Busy (stopping any earlier pulse otherwise).</summary>
    public static void Paint(Ellipse dot, MediaDownloadText.VpnTone tone)
    {
        dot.Fill = Brush(tone);
        if (tone == MediaDownloadText.VpnTone.Busy)
        {
            dot.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.8))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }
        else
        {
            dot.BeginAnimation(UIElement.OpacityProperty, null);
            dot.Opacity = 1;
        }
    }
}
