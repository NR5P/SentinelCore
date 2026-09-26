using SentinelCore.Client;
using SentinelCore.Client.Models;
using Xunit;

namespace SentinelCore.Client.Tests;

public class MediaPlaybackRulesTests
{
    private static MediaProgress Progress(double position, double duration = 6000, bool watched = false) =>
        new() { PositionSeconds = position, DurationSeconds = duration, Watched = watched, Percent = position / duration * 100 };

    private static MediaPlayback Hls(double start) => new() { Mode = "hls", StartSeconds = start, DurationSeconds = 6000 };

    private static MediaPlayback Direct(double start) => new() { Mode = "direct", StartSeconds = start, DurationSeconds = 6000 };

    [Fact]
    public void Resume_is_offered_only_for_a_real_unfinished_point()
    {
        Assert.False(MediaPlaybackRules.CanResume(null));
        Assert.False(MediaPlaybackRules.CanResume(Progress(10)));                 // under 30 s: server never saved it
        Assert.True(MediaPlaybackRules.CanResume(Progress(30)));
        Assert.True(MediaPlaybackRules.CanResume(Progress(1200)));
        Assert.False(MediaPlaybackRules.CanResume(Progress(5600)));               // past 92%: that is "watched"
        Assert.False(MediaPlaybackRules.CanResume(Progress(1200, watched: true)));
        Assert.True(MediaPlaybackRules.CanResume(Progress(1200, duration: 0)));   // duration unknown
    }

    [Fact]
    public void Start_is_omitted_to_resume_and_zero_to_start_over()
    {
        Assert.Null(MediaPlaybackRules.StartFor(Progress(1200), startOver: false));
        Assert.Equal(0, MediaPlaybackRules.StartFor(Progress(1200), startOver: true));
        Assert.Equal(0, MediaPlaybackRules.StartFor(null, startOver: false));
        Assert.Equal(0, MediaPlaybackRules.StartFor(Progress(1200, watched: true), startOver: false));
    }

    [Fact]
    public void An_HLS_clock_starts_at_the_requested_start()
    {
        var playback = Hls(1200);

        // Where the player says 0:00, the movie is at 20:00 - report the raw value and every resume
        // point slides back to the start.
        Assert.Equal(1200, MediaPlaybackRules.AbsolutePosition(playback, 0));
        Assert.Equal(1290.5, MediaPlaybackRules.AbsolutePosition(playback, 90.5));
        Assert.Equal(90.5, MediaPlaybackRules.PlayerPosition(playback, 1290.5));
        Assert.Equal(0, MediaPlaybackRules.InitialSeek(playback));
        Assert.True(MediaPlaybackRules.SeekNeedsNewSession(playback, 600));
        Assert.False(MediaPlaybackRules.SeekNeedsNewSession(playback, 1500));
    }

    [Fact]
    public void Direct_play_seeks_itself_and_reports_the_real_position()
    {
        var playback = Direct(1200);

        Assert.Equal(1200, MediaPlaybackRules.InitialSeek(playback));
        Assert.Equal(1290.5, MediaPlaybackRules.AbsolutePosition(playback, 1290.5));
        Assert.Equal(1290.5, MediaPlaybackRules.PlayerPosition(playback, 1290.5));
        Assert.False(MediaPlaybackRules.SeekNeedsNewSession(playback, 0));
    }

    [Fact]
    public void Positions_never_go_negative()
    {
        Assert.Equal(0, MediaPlaybackRules.AbsolutePosition(Direct(0), -3));
        Assert.Equal(0, MediaPlaybackRules.PlayerPosition(Hls(1200), 600));
    }

    [Fact]
    public void Nothing_under_thirty_seconds_is_worth_reporting()
    {
        Assert.False(MediaPlaybackRules.IsWorthReporting(29.9));
        Assert.True(MediaPlaybackRules.IsWorthReporting(30));
    }

    [Fact]
    public void Card_progress_is_a_fraction_or_nothing()
    {
        Assert.Null(MediaPlaybackRules.Fraction(null));
        Assert.Null(MediaPlaybackRules.Fraction(Progress(1200, watched: true)));
        Assert.Equal(0.2, MediaPlaybackRules.Fraction(Progress(1200))!.Value, 3);
        Assert.Equal(0.5, MediaPlaybackRules.Fraction(new MediaProgress { PositionSeconds = 10, Percent = 50 })!.Value, 3);
        Assert.Equal(1, MediaPlaybackRules.Fraction(Progress(7000))!.Value, 3);
    }

    [Theory]
    [InlineData(102, "1h 42m")]
    [InlineData(42, "42m")]
    [InlineData(120, "2h")]
    [InlineData(0, "")]
    [InlineData(null, "")]
    public void Runtime_reads_like_a_listing(int? minutes, string expected) =>
        Assert.Equal(expected, MediaPlaybackRules.FormatRuntime(minutes));

    [Fact]
    public void Runtime_falls_back_to_the_file_duration()
    {
        Assert.Equal("1h 42m", MediaPlaybackRules.RuntimeText(new MediaItemSummary { RuntimeMinutes = 102, DurationSeconds = 10 }));
        Assert.Equal("1h 42m", MediaPlaybackRules.RuntimeText(new MediaItemSummary { DurationSeconds = 6120.4 }));
        Assert.Equal("", MediaPlaybackRules.RuntimeText(new MediaItemSummary()));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(59.9, "0:59")]
    [InlineData(1200, "20:00")]
    [InlineData(3735, "1:02:15")]
    [InlineData(-5, "0:00")]
    [InlineData(double.NaN, "0:00")]
    public void Clock_readout(double seconds, string expected) =>
        Assert.Equal(expected, MediaPlaybackRules.FormatClock(seconds));

    [Fact]
    public void Resume_label_names_the_point()
    {
        Assert.Equal("Resume from 20:00", MediaPlaybackRules.ResumeLabel(Progress(1200)));
        Assert.Equal("Resume", MediaPlaybackRules.ResumeLabel(null));
    }

    [Fact]
    public void Meta_line_leaves_out_what_is_missing()
    {
        Assert.Equal("2009 · 1h 42m · ★ 6.2", MediaPlaybackRules.MetaLine(
            new MediaItemSummary { Kind = "movie", Year = 2009, RuntimeMinutes = 102, Rating = 6.2 }));
        Assert.Equal("2011 · 6 seasons · 27 episodes", MediaPlaybackRules.MetaLine(
            new MediaItemSummary { Kind = "show", Year = 2011, SeasonCount = 6, EpisodeCount = 27, RuntimeMinutes = 60 }));
        Assert.Equal("1 season · 1 episode", MediaPlaybackRules.MetaLine(
            new MediaItemSummary { Kind = "show", SeasonCount = 1, EpisodeCount = 1 }));
        Assert.Equal("", MediaPlaybackRules.MetaLine(new MediaItemSummary { Kind = "movie" }));
    }

    [Fact]
    public void The_start_query_value_is_culture_proof()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1234.5", MediaPlaybackRules.StartQueryValue(1234.5));
            Assert.Equal("0", MediaPlaybackRules.StartQueryValue(0));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
