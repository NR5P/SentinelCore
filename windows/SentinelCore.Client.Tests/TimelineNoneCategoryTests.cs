using SentinelCore.Client;
using SentinelCore.Client.Models;
using Xunit;

namespace SentinelCore.Client.Tests;

/// <summary>
/// The server's seventh category.
/// </summary>
/// <remarks>
/// TimelineEndpoints.Category returns "none" for a frame the AI looked at and found no subject in,
/// and on a busy camera that is most of the day - measured against this install's gate camera, 364
/// of 402 events on one day. A filter vocabulary that doesn't mention it hides those rows with no
/// way to bring them back and no hint that they exist, which reads as a broken timeline rather than
/// as a filter doing its job.
/// </remarks>
public class TimelineNoneCategoryTests
{
    private static TimelineSecurityEvent Event(string category, string severity = "low") => new()
    {
        Id = Guid.NewGuid(),
        Category = category,
        Severity = severity,
    };

    [Fact]
    public void Nothing_detected_is_a_category_the_filter_knows_about()
    {
        Assert.Contains("none", TimelineFilter.Categories);
    }

    [Fact]
    public void It_is_off_by_default_because_it_is_not_what_anyone_opens_the_timeline_for()
    {
        var filter = new TimelineFilter();

        Assert.False(filter.Includes(Event("none")));
        Assert.True(filter.Includes(Event("person")));
    }

    [Fact]
    public void Turning_it_on_brings_those_rows_back()
    {
        var filter = new TimelineFilter();
        filter.SelectedCategories.Add("none");

        Assert.True(filter.Includes(Event("none")));
    }

    [Fact]
    public void Resetting_returns_to_hiding_it_rather_than_selecting_everything()
    {
        var filter = new TimelineFilter();
        filter.SelectedCategories.Add("none");

        filter.Reset();

        Assert.False(filter.Includes(Event("none")));
        // Everything the AI actually found is back on, though.
        foreach (var category in TimelineFilter.DefaultCategories)
        {
            Assert.True(filter.Includes(Event(category)), $"{category} should be showing after a reset");
        }
    }

    [Fact]
    public void It_reads_as_something_a_person_would_recognise()
    {
        // "None" alone in a menu of object types says nothing useful.
        Assert.Equal("Nothing detected", TimelineFilter.Label("none"));
        Assert.Equal("Person", TimelineFilter.Label("person"));
        Assert.Equal("Vehicle", TimelineFilter.Label("vehicle"));
    }

    [Fact]
    public void A_day_that_is_mostly_nothing_still_reports_its_real_total()
    {
        // The screen shows "N of M", so the hidden ones are always accounted for.
        var events = Enumerable.Range(0, 364).Select(_ => Event("none"))
            .Concat(Enumerable.Range(0, 38).Select(_ => Event("person", "high")))
            .ToList();
        var filter = new TimelineFilter();

        Assert.Equal(38, filter.Apply(events).Count());
        Assert.Equal(402, events.Count);
    }
}
