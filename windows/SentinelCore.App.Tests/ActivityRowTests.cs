using System.Windows;
using SentinelCore.App.Views;
using SentinelCore.Client.Models;
using Xunit;

namespace SentinelCore.App.Tests;

// How a row of the Events list reads. Every one of these phrasings matches the Apple client's
// EventCard, so the same moment reads the same on a phone, a Mac and this - and the badge rules in
// particular carry real meaning that a wrong default would quietly erase.
public class ActivityRowTests
{
    private static ActivityItem Analyzed(string? summary = "A courier left a parcel.") => new()
    {
        Id = "8f14e45f-ceea-467a-9575-9f9a4b3d1f2c",
        Kind = "analyzed",
        Camera = "Front Door",
        EventType = "package_delivery",
        Summary = summary,
        Confidence = 0.92,
    };

    private static ActivityItem Motion(string? zone = "Back gate") => new()
    {
        Id = "trigger:1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed",
        Kind = "motion",
        Camera = "Garden",
        ZoneName = zone,
    };

    [Fact]
    public void An_analysed_row_shows_its_description_and_its_event_type()
    {
        var row = new ActivityRow(Analyzed());

        Assert.Equal("A courier left a parcel.", row.Description);
        Assert.Equal("Package delivery", row.FooterLabel);
        Assert.Equal("92%", row.ConfidenceText);
        Assert.Equal(FontStyles.Normal, row.DescriptionStyle);
    }

    [Fact]
    public void A_motion_row_stands_its_zone_in_for_a_description_and_says_it_is_only_motion()
    {
        // Nothing analysed this frame, so the zone that caught it stands in - italic and dimmed, so
        // a mixed list reads as "described" versus "not looked at".
        var row = new ActivityRow(Motion());

        Assert.Equal("Back gate", row.Description);
        Assert.Equal(FontStyles.Italic, row.DescriptionStyle);
        Assert.Equal("Motion only", row.FooterLabel);
        Assert.Equal("", row.ConfidenceText);   // no confidence, because nothing judged it
    }

    [Fact]
    public void A_motion_row_with_no_zone_still_says_something()
    {
        Assert.Equal("Motion in zone", new ActivityRow(Motion(zone: null)).Description);
    }

    [Fact]
    public void An_analysed_row_with_an_empty_summary_says_so_rather_than_showing_a_blank()
    {
        Assert.Equal("No description", new ActivityRow(Analyzed(summary: "")).Description);
        Assert.Equal("No description", new ActivityRow(Analyzed(summary: null)).Description);
    }

    [Fact]
    public void An_unknown_event_type_is_still_readable()
    {
        var item = Analyzed();
        var row = new ActivityRow(new ActivityItem
        {
            Id = item.Id,
            Kind = "analyzed",
            Camera = item.Camera,
            Summary = item.Summary,
        });

        Assert.Equal("Unknown", row.FooterLabel);
    }

    [Theory]
    [InlineData("ready", "Clip")]
    [InlineData("recording", "Recording…")]
    [InlineData("missing", "No clip")]
    // A deliberate setting, not a fault - not worth a badge on every row.
    [InlineData("off", "")]
    public void The_clip_badge_tells_the_three_states_apart(string state, string expected)
    {
        var row = new ActivityRow(new ActivityItem { Id = "x", Kind = "analyzed", ClipState = state });

        Assert.Equal(expected, row.ClipBadge);
    }

    [Fact]
    public void A_server_too_old_to_send_a_clip_state_still_badges_a_clip_it_has()
    {
        var withClip = new ActivityRow(new ActivityItem
        {
            Id = "x",
            Kind = "analyzed",
            ClipUrl = "/api/recordings/1/video",
        });
        var without = new ActivityRow(new ActivityItem { Id = "y", Kind = "analyzed" });

        Assert.Equal("Clip", withClip.ClipBadge);
        Assert.Equal("", without.ClipBadge);
    }

    [Fact]
    public void A_clip_still_being_written_is_amber_rather_than_the_ready_colour()
    {
        Assert.Equal(Palette.Amber, new ActivityRow(new ActivityItem { Id = "x", ClipState = "recording" }).ClipBadgeBrush);
        Assert.Equal(Palette.Amber, new ActivityRow(new ActivityItem { Id = "x", ClipState = "missing" }).ClipBadgeBrush);
        Assert.Equal(Palette.Cyan, new ActivityRow(new ActivityItem { Id = "x", ClipState = "ready" }).ClipBadgeBrush);
    }

    [Fact]
    public void The_notified_bar_is_only_filled_on_a_row_that_notified()
    {
        var notified = new ActivityRow(new ActivityItem { Id = "x", Kind = "analyzed", Notified = true });
        var quiet = new ActivityRow(new ActivityItem { Id = "y", Kind = "analyzed", Notified = false });

        Assert.Equal(Palette.Green, notified.NotifiedBarBrush);
        // Transparent rather than absent, so every row keeps the same gutter and the list doesn't
        // shift sideways around a notified one.
        Assert.Equal(System.Windows.Media.Brushes.Transparent, quiet.NotifiedBarBrush);
    }

    [Fact]
    public void A_rejected_verdict_and_a_pending_one_are_marked_differently()
    {
        var rejected = new ActivityRow(new ActivityItem { Id = "x", Confirmation = "rejected" });
        var pending = new ActivityRow(new ActivityItem { Id = "y", Confirmation = "pending" });
        var confirmed = new ActivityRow(new ActivityItem { Id = "z", Confirmation = "confirmed" });

        Assert.NotEqual("", rejected.ConfirmationGlyph);
        Assert.Equal("AI found nothing", rejected.ConfirmationTip);
        Assert.NotEqual("", pending.ConfirmationGlyph);
        // Same words the Apple client uses for the same state, so the two read alike.
        Assert.Equal("Awaiting AI verdict", pending.ConfirmationTip);

        // A confirmed row is the normal case and gets no mark at all.
        Assert.Equal("", confirmed.ConfirmationGlyph);
        Assert.Equal("", new ActivityRow(new ActivityItem { Id = "w" }).ConfirmationGlyph);
    }

    [Fact]
    public void A_camera_with_AI_analysis_off_is_flagged_and_one_without_the_field_is_not()
    {
        // Absent reads as "analysis is on", which is what every install did before the field existed.
        Assert.False(new ActivityRow(new ActivityItem { Id = "x" }).IsAiAnalysisOff);
        Assert.True(new ActivityRow(new ActivityItem { Id = "y", AiAnalysisOff = true }).IsAiAnalysisOff);
    }
}
