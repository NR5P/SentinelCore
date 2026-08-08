using SentinelCore;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The spider-web advisory: noticing that a camera spent the night triggering on silk and moths
// rather than on anything real, and saying so. See LensNuisance for the physics.
//
// The numbers in the "measured" tests below are the real ones from the gate camera on the night
// of 2026-08-06, which is the case this feature was built from: 235 dusk-to-dawn triggers, none
// of them confirmed, against 74 in the daylight either side of which 7 were real.
public sealed class NuisanceAdvisorTests
{
    private static readonly Guid CameraId = Guid.NewGuid();
    private static readonly DateOnly Night = new(2026, 8, 6);

    // Dusk to dawn, ~8 hours, and the daylight before it, ~14.
    private static readonly DateTimeOffset DarkStart = new(2026, 8, 7, 02, 00, 00, TimeSpan.Zero);
    private static readonly DateTimeOffset DarkEnd = new(2026, 8, 7, 10, 00, 00, TimeSpan.Zero);
    private static readonly DateTimeOffset DaylightStart = new(2026, 8, 6, 12, 00, 00, TimeSpan.Zero);
    private static readonly DateTimeOffset DaylightEnd = DarkStart;

    private static readonly NuisanceAdvisorOptions Defaults = new();

    private static List<TriggerEvent> Events(
        (DateTimeOffset Window, int Count, int Confirmed, double Area)[] blocks)
    {
        var events = new List<TriggerEvent>();
        foreach (var (window, count, confirmed, area) in blocks)
        {
            for (var i = 0; i < count; i++)
            {
                events.Add(new TriggerEvent(
                    Guid.NewGuid(), CameraId,
                    // Spread within the window's first few hours; only which window they land in
                    // matters, and minutes apart keeps them all comfortably inside it.
                    window.AddMinutes(i),
                    $"{i}.jpg", area, null,
                    Confirmation: i < confirmed ? TriggerConfirmation.Confirmed : TriggerConfirmation.Rejected));
            }
        }

        return events;
    }

    private static NuisanceFinding? Assess(List<TriggerEvent> events, NuisanceAdvisorOptions? options = null) =>
        LensNuisance.Assess(
            CameraId, "gate camera front door", Night, events,
            DarkStart, DarkEnd, DaylightStart, DaylightEnd, options ?? Defaults);

    // --- The night this was built for ---------------------------------------------------------

    [Fact]
    public void TheMeasuredWebNight_IsReported()
    {
        var finding = Assess(Events([
            (DarkStart, 235, 0, 0.003),
            (DaylightStart, 74, 7, 0.0019)
        ]));

        Assert.NotNull(finding);
        Assert.Equal(235, finding.Dark.Triggers);
        Assert.Equal(0, finding.Dark.Confirmed);

        // 235 over 8 hours against 74 over 14 - the contrast that says the dark is the problem.
        Assert.True(finding.DarkToDaylightRatio > 5, $"ratio was {finding.DarkToDaylightRatio}");
    }

    [Fact]
    public void TheMeasuredWebNight_ProducesAMessageQuotingTheEvidence()
    {
        var finding = Assess(Events([
            (DarkStart, 235, 0, 0.003),
            (DaylightStart, 74, 7, 0.0019)
        ]));

        var body = NuisanceAdvisoryText.Body(finding!);

        // The reader has to be able to check the claim against their own Triggers tab, so the
        // count, the camera and the advice all have to actually be in there.
        Assert.Contains("235", NuisanceAdvisoryText.Subject(finding!));
        Assert.Contains("gate camera front door", body);
        Assert.Contains("spider web", body);
        Assert.Contains("infrared", body);
        Assert.Contains("0.30%", body);
    }

    // Every sentence in the message carries an interpolated number whose width isn't known until
    // it's formatted, which is why the text is wrapped on the way out instead of by hand in the
    // source - a hard-wrapped line break would land somewhere different for every install.
    [Fact]
    public void TheMessage_IsWrappedForReadingRegardlessOfHowWideTheNumbersAre()
    {
        var narrow = Assess(Events([(DarkStart, 200, 0, 0.003)]))!;
        var wide = Assess(Events([(DarkStart, 2000, 0, 0.003), (DaylightStart, 100, 9, 0.002)]))!;

        foreach (var body in new[] { NuisanceAdvisoryText.Body(narrow), NuisanceAdvisoryText.Body(wide) })
        {
            Assert.All(body.Split('\n'), line => Assert.True(line.Length <= 78, $"too long: {line}"));

            // The advice list has to survive wrapping as a list - a bullet whose continuation
            // lines fall back to column zero reads as prose.
            var lines = body.Split('\n');
            var bullet = Array.FindIndex(lines, l => l.StartsWith("  - Move the infrared"));
            Assert.True(bullet >= 0, "the illuminator advice went missing");
            Assert.StartsWith("    ", lines[bullet + 1]);
            Assert.DoesNotContain("- ", lines[bullet + 1][..6]);
        }
    }

    // --- The three ways a night talks the advisor out of it ------------------------------------

    // A night where things were genuinely happening. Same volume as the web night, but the AI
    // kept finding real subjects, so nothing is wrong with the camera.
    [Fact]
    public void ANightWithRealSubjectsInIt_IsNotReported()
    {
        var finding = Assess(Events([
            (DarkStart, 235, 90, 0.003),
            (DaylightStart, 74, 7, 0.0019)
        ]));

        Assert.Null(finding);
    }

    // Quiet nights are just nights. Telling someone to go clean a lens over twenty triggers would
    // be a false alert about false alerts.
    [Fact]
    public void AQuietNight_IsNotReported()
    {
        var finding = Assess(Events([
            (DarkStart, 24, 1, 0.0026),
            (DaylightStart, 137, 27, 0.0023)
        ]));

        Assert.Null(finding);
    }

    // The camera pointed at a busy road: floods of rejected motion, but around the clock. That
    // wants its zone redrawn, not a broom, and blaming a spider would send the owner outside for
    // nothing.
    [Fact]
    public void ACameraThatIsJustAsBusyInDaylight_IsNotReported()
    {
        var finding = Assess(Events([
            (DarkStart, 240, 0, 0.003),
            (DaylightStart, 420, 5, 0.003)
        ]));

        Assert.Null(finding);
    }

    // The opposite extreme, and the strongest possible version of the signal: nothing at all in
    // daylight, hundreds of rejected triggers after dark. Dividing by a zero daylight rate must
    // read as "infinitely more" rather than throwing or quietly declining.
    [Fact]
    public void ACameraWithNoDaylightTriggersAtAll_IsStillReported()
    {
        var finding = Assess(Events([(DarkStart, 200, 0, 0.003)]));

        Assert.NotNull(finding);
        Assert.Equal(0, finding.Daylight.Triggers);
        Assert.Equal(double.PositiveInfinity, finding.DarkToDaylightRatio);
    }

    // --- Details that decide whether the message reads as true ---------------------------------

    // One genuine car among two hundred strands of silk would drag a mean far enough to
    // misrepresent the night, and "the typical overnight trigger covered X%" is the line in the
    // message that does most of the convincing.
    [Fact]
    public void TheReportedTriggerSize_IsTheMedianAndNotDraggedByOneLargeTrigger()
    {
        var events = Events([(DarkStart, 100, 0, 0.002)]);
        events.Add(new TriggerEvent(
            Guid.NewGuid(), CameraId, DarkStart.AddHours(3), "car.jpg", 0.4, null,
            Confirmation: TriggerConfirmation.Rejected));

        var finding = Assess(events);

        Assert.NotNull(finding);
        Assert.Equal(0.002, finding.MedianDarkAreaFraction, precision: 6);
    }

    // Triggers outside both windows belong to a different night and must not be counted into
    // either - the query that feeds this deliberately fetches a wide range.
    [Fact]
    public void TriggersOutsideTheWindows_AreIgnored()
    {
        var events = Events([(DarkStart, 200, 0, 0.003)]);
        events.AddRange(Events([(DarkEnd.AddHours(1), 500, 0, 0.003)]));

        var finding = Assess(events);

        Assert.NotNull(finding);
        Assert.Equal(200, finding.Dark.Triggers);
    }

    // Every threshold is a setting because every install's normal is different. Raising the floor
    // above a real flood has to actually silence it.
    [Fact]
    public void RaisingTheTriggerFloorAboveTheNight_SilencesIt()
    {
        var events = Events([(DarkStart, 235, 0, 0.003), (DaylightStart, 74, 7, 0.0019)]);

        Assert.NotNull(Assess(events));
        Assert.Null(Assess(events, new NuisanceAdvisorOptions { MinimumNightTriggers = 300 }));
    }

    // --- Working out which night has just finished ---------------------------------------------

    private static readonly LocationOptions Austin = new() { Latitude = 30.27, Longitude = -97.74 };

    // Mid-morning, the night that ended at dawn is the one to judge - named for the evening it
    // began on, which is yesterday.
    [Fact]
    public void AtMidMorning_TheNightJustGoneIsTheOneAssessed()
    {
        var now = new DateTimeOffset(2026, 8, 7, 09, 00, 00, TimeSpan.FromHours(-5));

        var window = NightWindow.MostRecentlyEnded(now, Austin, TimeSpan.FromMinutes(30));

        Assert.NotNull(window);
        Assert.Equal(new DateOnly(2026, 8, 6), window.Night);
        Assert.True(window.DarkStart < window.DarkEnd);
        Assert.True(window.DaylightEnd <= window.DarkStart);
    }

    // Just before dawn there is no finished night yet, and judging one mid-way through would
    // report half a night's triggers as if it were the whole thing.
    [Fact]
    public void BeforeDawn_TheNightInProgressIsNotAssessedYet()
    {
        var now = new DateTimeOffset(2026, 8, 7, 03, 00, 00, TimeSpan.FromHours(-5));

        var window = NightWindow.MostRecentlyEnded(now, Austin, TimeSpan.FromMinutes(30));

        // Whatever it returns, it must not be the night currently under way.
        Assert.NotEqual(new DateOnly(2026, 8, 6), window?.Night);
    }

    // Dusk and dawn move by well over an hour across the year, which is the whole reason this
    // asks the sun rather than using a clock time.
    [Fact]
    public void TheDarkWindow_FollowsTheSeasonRatherThanTheClock()
    {
        var summer = NightWindow.For(new DateOnly(2026, 6, 21), TimeSpan.FromHours(-5), Austin);
        var winter = NightWindow.For(new DateOnly(2026, 12, 21), TimeSpan.FromHours(-5), Austin);

        Assert.NotNull(summer);
        Assert.NotNull(winter);
        Assert.True(
            winter.DarkEnd - winter.DarkStart > summer.DarkEnd - summer.DarkStart,
            "a December night should be longer than a June one");
    }

    // With no location configured the sun's times are unknowable, and the fallback has to still
    // produce a usable window rather than nothing at all.
    [Fact]
    public void WithNoLocationConfigured_TheDarkWindowFallsBackToFixedHours()
    {
        var window = NightWindow.For(new DateOnly(2026, 8, 6), TimeSpan.FromHours(-5), new LocationOptions());

        Assert.NotNull(window);
        Assert.Equal(21, window.DarkStart.Hour);
        Assert.Equal(5, window.DarkEnd.Hour);
        Assert.Equal(new DateOnly(2026, 8, 7), DateOnly.FromDateTime(window.DarkEnd.Date));
    }
}

// The two things the store had to grow for the advisory: reading a window's rejected rows back,
// and remembering that a camera has already been told.
public sealed class NuisanceAdvisorStoreTests
{
    private static SqliteTriggerStore NewStore() =>
        new(Path.Combine(Path.GetTempPath(), $"trig-{Guid.NewGuid():N}"));

    // The entire point: the Triggers tab's query hides rejected rows, and those rows *are* the
    // evidence here. A camera behind a web would otherwise look like it had a quiet night.
    [Fact]
    public async Task GetAllEventsInRangeAsync_IncludesTheRejectedRowsTheTabHides()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = NewStore();
        var cameraId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddHours(-2);

        await store.AddAsync(cameraId, "a.jpg", 0.003, null, TriggerConfirmation.Rejected, ct, occurredAt: start.AddMinutes(10));
        await store.AddAsync(cameraId, "b.jpg", 0.003, null, TriggerConfirmation.Rejected, ct, occurredAt: start.AddMinutes(20));
        await store.AddAsync(cameraId, "c.jpg", 0.003, null, TriggerConfirmation.Confirmed, ct, occurredAt: start.AddMinutes(30));

        var all = await store.GetAllEventsInRangeAsync(cameraId, start, start.AddHours(1), ct);
        var confirmedOnly = await store.GetEventsInRangeAsync(cameraId, start, start.AddHours(1), ct);

        Assert.Equal(3, all.Count);
        Assert.Single(confirmedOnly);
    }

    // The cooldown lives in the database rather than in the worker because the check runs once a
    // day: held in memory, a restart between two mornings would re-send the same advice.
    [Fact]
    public async Task ANuisanceAdvisory_IsRememberedAcrossAReopen()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"trig-{Guid.NewGuid():N}");
        var cameraId = Guid.NewGuid();
        var sentAt = new DateTimeOffset(2026, 8, 7, 12, 00, 00, TimeSpan.Zero);

        using (var store = new SqliteTriggerStore(root))
        {
            Assert.Null(await store.GetLastNuisanceAdvisoryAsync(cameraId, ct));
            await store.RecordNuisanceAdvisoryAsync(cameraId, new DateOnly(2026, 8, 6), sentAt, ct);
        }

        using (var reopened = new SqliteTriggerStore(root))
        {
            var last = await reopened.GetLastNuisanceAdvisoryAsync(cameraId, ct);
            Assert.Equal(sentAt, last);
        }
    }

    // A second night's advisory for the same camera has to move the cooldown forward, not sit
    // behind the first one - MAX(SentAt), not the earliest row.
    [Fact]
    public async Task TheLatestAdvisory_IsTheOneTheCooldownReads()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = NewStore();
        var cameraId = Guid.NewGuid();
        var first = new DateTimeOffset(2026, 8, 1, 12, 00, 00, TimeSpan.Zero);
        var second = new DateTimeOffset(2026, 8, 7, 12, 00, 00, TimeSpan.Zero);

        await store.RecordNuisanceAdvisoryAsync(cameraId, new DateOnly(2026, 7, 31), first, ct);
        await store.RecordNuisanceAdvisoryAsync(cameraId, new DateOnly(2026, 8, 6), second, ct);

        Assert.Equal(second, await store.GetLastNuisanceAdvisoryAsync(cameraId, ct));
    }

    // One camera being advised must not start another camera's cooldown - two cameras can
    // perfectly well both have webs.
    [Fact]
    public async Task OneCamerasAdvisory_DoesNotSilenceAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = NewStore();
        var advised = Guid.NewGuid();
        var other = Guid.NewGuid();

        await store.RecordNuisanceAdvisoryAsync(advised, new DateOnly(2026, 8, 6), DateTimeOffset.UtcNow, ct);

        Assert.NotNull(await store.GetLastNuisanceAdvisoryAsync(advised, ct));
        Assert.Null(await store.GetLastNuisanceAdvisoryAsync(other, ct));
    }
}
