using System.Globalization;
using System.Windows;
using SentinelCore.App.Design;
using Xunit;

namespace SentinelCore.App.Tests;

public class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "now")]
    [InlineData(3, "now")]          // inside the "close enough" window
    [InlineData(30, "30s ago")]
    [InlineData(90, "1m ago")]
    [InlineData(60 * 29, "29m ago")]
    [InlineData(60 * 60 * 3, "3h ago")]
    [InlineData(60 * 60 * 24 * 2, "2d ago")]
    [InlineData(60 * 60 * 24 * 10, "1w ago")]
    [InlineData(60 * 60 * 24 * 400, "1y ago")]
    public void A_past_moment_reads_the_way_the_dashboard_reads_it(int secondsAgo, string expected) =>
        Assert.Equal(expected, RelativeTime.Describe(Now.AddSeconds(-secondsAgo), Now));

    [Fact]
    public void A_moment_slightly_in_the_future_reads_as_now_rather_than_as_a_countdown()
    {
        // Clock skew between the server and this PC can put a just-received event a second or two
        // into the future. "in 2s" on a security event reads as a bug, or worse, as a prediction.
        Assert.Equal("now", RelativeTime.Describe(Now.AddSeconds(2), Now));
        Assert.Equal("now", RelativeTime.Describe(Now.AddSeconds(59), Now));
    }

    [Fact]
    public void A_moment_genuinely_in_the_future_is_not_hidden()
    {
        // Beyond plausible skew it is a real future time - a scheduled run, say - and saying "now"
        // would be a lie.
        Assert.Equal("in 2h", RelativeTime.Describe(Now.AddHours(2), Now));
    }
}

public class ConverterTests
{
    private static object Convert(System.Windows.Data.IValueConverter converter, object? value) =>
        converter.Convert(value, typeof(object), null, CultureInfo.InvariantCulture);

    [Fact]
    public void A_bool_becomes_a_visibility_both_ways_round()
    {
        var direct = new BoolToVisibilityConverter();
        var inverse = new InverseBoolToVisibilityConverter();

        Assert.Equal(Visibility.Visible, Convert(direct, true));
        Assert.Equal(Visibility.Collapsed, Convert(direct, false));
        Assert.Equal(Visibility.Collapsed, Convert(inverse, true));
        Assert.Equal(Visibility.Visible, Convert(inverse, false));

        // A binding that has not resolved yet arrives as null, and must not throw mid-layout.
        Assert.Equal(Visibility.Collapsed, Convert(direct, null));
        Assert.Equal(Visibility.Visible, Convert(inverse, null));
    }

    [Fact]
    public void An_empty_or_whitespace_string_collapses_the_chip_that_would_hold_it()
    {
        var converter = new EmptyStringToVisibilityConverter();

        Assert.Equal(Visibility.Collapsed, Convert(converter, ""));
        Assert.Equal(Visibility.Collapsed, Convert(converter, "   "));
        Assert.Equal(Visibility.Collapsed, Convert(converter, null));
        Assert.Equal(Visibility.Visible, Convert(converter, "Clip"));
    }

    [Fact]
    public void A_confidence_fraction_renders_as_a_whole_percentage()
    {
        var converter = new PercentConverter();

        Assert.Equal("92%", Convert(converter, 0.92));
        Assert.Equal("100%", Convert(converter, 1.0));
        Assert.Equal("0%", Convert(converter, 0.0));
        Assert.Equal("", Convert(converter, null));
    }

    [Theory]
    [InlineData(0L, "0 bytes")]
    [InlineData(512L, "512 bytes")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1073741824L, "1 GB")]
    public void Byte_counts_use_binary_units_like_the_Apple_client(long bytes, string expected) =>
        Assert.Equal(expected, ByteSizeConverter.FormatBytes(bytes));

    [Fact]
    public void An_event_type_reads_as_a_sentence_rather_than_as_a_key()
    {
        var converter = new TitleCaseConverter();

        Assert.Equal("Package delivery", Convert(converter, "package_delivery"));
        Assert.Equal("Person", Convert(converter, "person"));
        Assert.Equal("", Convert(converter, ""));
        Assert.Equal("", Convert(converter, null));
    }

    [Fact]
    public void The_watermark_scales_with_the_tile_but_stays_between_its_limits()
    {
        var converter = new WatermarkFontSizeConverter();

        // A thumbnail must stay legible; a full-screen player must not be shouted at.
        Assert.Equal(11d, Convert(converter, 40d));
        Assert.Equal(48d, Convert(converter, 2000d));
        Assert.Equal(36d, Convert(converter, 327.27272727272725d));
        // No size yet, mid-layout.
        Assert.Equal(14d, Convert(converter, null));
    }
}
