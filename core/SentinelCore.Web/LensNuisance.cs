using SentinelCore;

namespace SentinelCore.Web;

// Spotting the one false-alert cause no amount of tuning can fix: a spider web strung across the
// lens, or the moths and gnats that gather in front of it after dark.
//
// The physical story is always the same. A camera's own infrared illuminator is the brightest
// thing for a hundred yards, so it draws insects, and where insects gather spiders build. Both
// then sit centimetres from a lens focused on infinity, where they are wildly out of focus,
// blown to pure white by the IR, and - because a strand of silk moves in air that a tree branch
// wouldn't notice - never still. Every frame differs from the last, so motion detection fires
// all night on an empty driveway.
//
// Nothing downstream can undo that. On the gate camera a person standing at the gate covers only
// ~161px on the 320x180 detector grid (see TriggerZone), and a swaying strand covers about the
// same, so no minimum object size separates them; the AI correctly rejects frame after frame of
// empty driveway, which keeps the Triggers tab clean but still burns an analysis on each one.
// The only real fix is physical - clear the web, and keep it from coming back - and the system
// can't do that. What it can do is notice the pattern and say so, which is what this is for.
//
// Deliberately statistical, never per-trigger. One trigger tells you nothing; a night's worth
// tells you everything, and the aggregate is also the only honest basis for advice that amounts
// to "go outside and look at your camera".

// One camera's tally over a stretch of a single day - built once for the dark hours and once for
// the daylight either side, so the two can be compared.
public sealed record NuisanceTally(int Triggers, int Confirmed, double Hours)
{
    public static readonly NuisanceTally Empty = new(0, 0, 0);

    // Rate rather than raw count, because the two windows being compared are never the same
    // length and the gap between them swings by hours across the year.
    public double PerHour => Hours <= 0 ? 0 : Triggers / Hours;

    public double ConfirmedFraction => Triggers == 0 ? 0 : (double)Confirmed / Triggers;
}

// A camera that behaved like it has a web on it for one night. Carries the evidence, not just
// the verdict: the message quotes these numbers back, and a claim like this one is worth nothing
// to the reader if they can't check it against what they see in the Triggers tab.
public sealed record NuisanceFinding(
    Guid CameraId,
    string CameraName,
    // The night this describes, named for the evening it began - the night that runs from the
    // evening of the 6th to the morning of the 7th is "2026-08-06", which is how someone
    // remembering "last Thursday night" would name it too.
    DateOnly Night,
    NuisanceTally Dark,
    NuisanceTally Daylight,
    // Typical size of a dark-hours trigger, as a fraction of the whole frame. Reported because it
    // is the single most convincing number in the message: a reader who sees that the average
    // thing that woke their camera 235 times covered 0.3% of the frame stops looking for a
    // prowler.
    double MedianDarkAreaFraction)
{
    public double DarkToDaylightRatio =>
        Daylight.PerHour <= 0 ? double.PositiveInfinity : Dark.PerHour / Daylight.PerHour;
}

public static class LensNuisance
{
    // Splits a camera's triggers for one 24-hour stretch into the dark hours and the daylight
    // either side, then decides whether the dark hours look like a web rather than a night when
    // something was genuinely going on.
    //
    // `darkStart`/`darkEnd` are the actual dusk and dawn for the server's location where that's
    // configured - see NightNuisanceWorker. Everything here is UTC.
    public static NuisanceFinding? Assess(
        Guid cameraId,
        string cameraName,
        DateOnly night,
        IReadOnlyList<TriggerEvent> dayAndNight,
        DateTimeOffset darkStart,
        DateTimeOffset darkEnd,
        DateTimeOffset daylightStart,
        DateTimeOffset daylightEnd,
        NuisanceAdvisorOptions options)
    {
        var dark = Tally(dayAndNight, darkStart, darkEnd);
        var daylight = Tally(dayAndNight, daylightStart, daylightEnd);

        // Test 1 - volume. A handful of triggers is a normal night whatever caused them, and
        // telling someone to go clean their camera over twelve triggers would be noise in its own
        // right. The measured flood was 235 in one night against a default floor of 60.
        if (dark.Triggers < options.MinimumNightTriggers) return null;

        // Test 2 - the AI found nothing, over and over. This is what separates a web from a real
        // problem: a night with a person, a deer, or a car in it produces confirmations, and a
        // night behind a web produces none at all (the measured flood confirmed 0 of 235).
        //
        // Note this can only ever pass for a camera whose triggers are actually analysed. With
        // 'Send to alerts' off there is no verdict to read, every row is Confirmed by default,
        // and this test correctly declines to guess - see the caller, which says so out loud
        // rather than letting the camera silently never qualify.
        if (dark.ConfirmedFraction * 100 > options.MaximumConfirmedPercent) return null;

        // Test 3 - the same camera is fine in daylight. A camera pointed at a busy road triggers
        // constantly around the clock and needs a zone redrawn, not a broom; a camera that only
        // loses its mind once the IR cuts in is being triggered by something the IR brought.
        // Measured: 29.4/hour after dark against 4.6/hour in daylight, a ratio of 6.4.
        //
        // A camera with no daylight triggers at all divides to infinity, which passes - that is
        // the correct reading of "quiet all day, 200 triggers at night", not a special case.
        var daylightRate = daylight.PerHour;
        if (daylightRate > 0 && dark.PerHour < daylightRate * options.MinimumNightDayRatio) return null;

        var median = MedianArea(dayAndNight, darkStart, darkEnd);
        return new NuisanceFinding(cameraId, cameraName, night, dark, daylight, median);
    }

    private static NuisanceTally Tally(IReadOnlyList<TriggerEvent> events, DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start) return NuisanceTally.Empty;

        var triggers = 0;
        var confirmed = 0;
        foreach (var e in events)
        {
            if (e.OccurredAt < start || e.OccurredAt >= end) continue;
            triggers++;
            if (e.Confirmation == TriggerConfirmation.Confirmed) confirmed++;
        }

        return new NuisanceTally(triggers, confirmed, (end - start).TotalHours);
    }

    // Median rather than mean: one genuine car at 3am among two hundred strands of silk drags a
    // mean upward by enough to misrepresent the whole night, and the number exists precisely to
    // say "the typical thing here was tiny".
    private static double MedianArea(IReadOnlyList<TriggerEvent> events, DateTimeOffset start, DateTimeOffset end)
    {
        var areas = events
            .Where(e => e.OccurredAt >= start && e.OccurredAt < end)
            .Select(e => e.TriggerAreaFraction)
            .OrderBy(a => a)
            .ToArray();

        if (areas.Length == 0) return 0;
        return areas.Length % 2 == 1
            ? areas[areas.Length / 2]
            : (areas[areas.Length / 2 - 1] + areas[areas.Length / 2]) / 2;
    }
}

// The wording of the advisory, kept apart from both the detection and the sending so the text
// can be read and adjusted without touching either, and asserted on in tests.
//
// Paragraphs are written here as single logical lines and wrapped on the way out rather than
// being hand-wrapped in the source. Almost every sentence carries an interpolated number whose
// width isn't known until it's formatted, so a source-wrapped line break lands in a different
// place for every install - which is how you get a message that reads raggedly for the one
// reader who actually gets it.
public static class NuisanceAdvisoryText
{
    // Plain-text email convention, and comfortably inside the width of a phone's Telegram window.
    private const int WrapColumns = 78;

    public static string Subject(NuisanceFinding finding) =>
        $"{finding.CameraName} triggered {finding.Dark.Triggers} times overnight with nothing there";

    public static string Body(NuisanceFinding finding)
    {
        var night = finding.Night.ToString("dddd d MMMM");
        var confirmed = finding.Dark.Confirmed == 0
            ? "not one of them"
            : $"only {finding.Dark.Confirmed} of them";

        var paragraphs = new[]
        {
            $"{finding.CameraName} fired {finding.Dark.Triggers} times between dusk and dawn on the night of {night}, and the AI found a person, vehicle, package, or animal in {confirmed}.",
            Comparison(finding),
            $"The typical overnight trigger covered {finding.MedianDarkAreaFraction * 100:0.00}% of the frame - far too small to be anything that walked up your driveway.",
            "That combination - only after dark, almost never anything real, and always tiny - is what a spider web across the lens or insects in front of it looks like. Both are drawn by the camera's own infrared light, and both sit close enough to the lens to be hugely out of focus and lit up white, so every small movement reads as motion.",
            "No setting will fix this one, because at that distance a strand of silk and a person standing at the gate cover about the same number of pixels - raise the minimum object size enough to ignore the web and the camera stops seeing people too.",
            "What actually helps, roughly in order of how well it works:",
            "  - Clear the web and wipe the lens housing. It buys a few nights, and it is worth doing first just to confirm this is the cause - if the next night is quiet, it was.",
            "  - Move the infrared illuminator away from the lens, or switch it off and light the area with a separate lamp sited some distance from the camera. This is the real fix: it is the light beside the lens that gathers the insects that bring the spiders, so the camera stops baiting itself.",
            "  - Treat the housing and mount. A spray-on repellent, or a smear of the sort of insect-repellent gel sold for this, keeps webs off for weeks at a time.",
            "  - If none of that is practical, narrow the trigger zone so it excludes the part of the frame the web occupies - but do that last, since it is the only option here that costs you real coverage.",
            "You will get at most one of these per camera per cooldown window; turn them off entirely under Settings if you would rather not hear about it."
        };

        return string.Join("\n\n", paragraphs.Select(Wrap));
    }

    private static string Comparison(NuisanceFinding finding) =>
        finding.Daylight.Triggers == 0
            ? "The same camera triggered not at all during the daylight either side of that."
            : $"The same camera averaged {finding.Dark.PerHour:0.0} triggers an hour after dark against {finding.Daylight.PerHour:0.0} an hour in daylight, when {finding.Daylight.ConfirmedFraction * 100:0}% of what it saw turned out to be real.";

    // Greedy word wrap. A paragraph that begins with a bullet keeps its marker on the first line
    // and has its continuations indented past it, so the list above still reads as a list once
    // the widths have moved.
    private const string BulletMarker = "  - ";

    private static string Wrap(string paragraph)
    {
        var bulleted = paragraph.StartsWith(BulletMarker);
        var indent = bulleted ? new string(' ', BulletMarker.Length) : "";

        var lines = new List<string>();
        var line = bulleted ? BulletMarker : "";

        foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // Skip the marker itself - it's already in `line`, and splitting put it back in the
            // word list as a stray "-".
            if (bulleted && lines.Count == 0 && line == BulletMarker && word == "-") continue;

            var separator = line.Length == 0 || line == BulletMarker ? "" : " ";
            if (line.Length > indent.Length && line.Length + separator.Length + word.Length > WrapColumns)
            {
                lines.Add(line);
                line = indent + word;
                continue;
            }

            line += separator + word;
        }

        if (line.Length > indent.Length) lines.Add(line);
        return string.Join("\n", lines);
    }
}
