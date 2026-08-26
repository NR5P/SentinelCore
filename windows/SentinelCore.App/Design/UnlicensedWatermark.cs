using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace SentinelCore.App.Design;

/// <summary>
/// The "UNLICENSED" mark drawn across camera images and live video when the server has no valid
/// licence file. Twin of the dashboard's body.unlicensed rules in wwwroot/styles.css and of the
/// Apple client's UnlicensedWatermark - same word, same amber, same rotation - so a customer who
/// sees it in one place recognises it in the others.
/// </summary>
/// <remarks>
/// Deliberately cosmetic, matching the "nag, never gate" rule everywhere else licensing touches:
/// the mark is not hit-testable, and nothing about it stops an image or a stream from loading,
/// playing, or being opened full screen.
///
/// The look lives in Theme.xaml as a template on this type; this class is only the two properties
/// it needs. IsLicensed defaults to true, which is also the right answer when the check itself
/// fails - this is visibility, not enforcement, and falsely accusing a paying customer is a worse
/// failure than missing a nag.
/// </remarks>
public sealed class UnlicensedWatermark : ContentControl
{
    public static readonly DependencyProperty IsLicensedProperty = DependencyProperty.Register(
        nameof(IsLicensed),
        typeof(bool),
        typeof(UnlicensedWatermark),
        new PropertyMetadata(true));

    public bool IsLicensed
    {
        get => (bool)GetValue(IsLicensedProperty);
        set => SetValue(IsLicensedProperty, value);
    }
}

/// <summary>
/// Scales the mark with the tile the way the dashboard's clamp() does, so it reads the same on a
/// thumbnail in a feed and on a full-screen player.
/// </summary>
public sealed class WatermarkFontSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double width ? Math.Clamp(width * 0.11, 11, 48) : 14d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
