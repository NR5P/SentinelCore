using System.Globalization;
using System.Text;

namespace SentinelCore.Web;

// Holiday calendars people can add to the Organizer's calendar - US federal holidays, the major
// Christian, Jewish, Islamic and other religious holidays, and so on. None is shown unless someone
// turns it on (see OrganizerEndpoints: /holidays and /preferences).
//
// Worked out here rather than in each app because most of them need a real calendar system: the
// Jewish holidays follow the Hebrew calendar, the Islamic ones the Umm al-Qura calendar, Lunar New
// Year the Chinese lunisolar calendar. .NET has all three built in; doing it once here means every
// client shows the same days. The apps cache what they fetch, so holidays show offline too.
//
// Holidays are all-day and never stored as Organizer items - they aren't anyone's to edit.
public sealed record HolidaySet(string Id, string Name, string Description);

public sealed record Holiday(string SetId, DateOnly Date, string Name, int Days = 1, string? Note = null)
{
    // Stable across fetches and servers, so a client can tell one it already has from a new one.
    public string Id => $"holiday:{SetId}:{Date:yyyyMMdd}:{Slug(Name)}";

    private static string Slug(string name)
    {
        var builder = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }
        return builder.ToString().Trim('-');
    }
}

public static class OrganizerHolidays
{
    public static readonly IReadOnlyList<HolidaySet> Sets =
    [
        new("us-federal", "US federal holidays",
            "The eleven days federal offices close. When one falls on a weekend, the weekday taken off instead is shown too."),
        new("us-observances", "US observances",
            "Days that aren't time off: Mother's and Father's Day, Valentine's Day, Halloween, the daylight saving changes and others."),
        new("christian", "Christian holidays",
            "Christmas, Easter and the days around it, Advent, Epiphany, Pentecost and more, on the Western calendar."),
        new("orthodox", "Orthodox Christian holidays",
            "Pascha, Christmas, Theophany and Pentecost on the Julian calendar most Orthodox churches keep."),
        new("jewish", "Jewish holidays",
            "The High Holy Days, Sukkot, Hanukkah, Purim, Passover, Shavuot and more, from the Hebrew calendar. Each begins at sundown the evening before."),
        new("islamic", "Islamic holidays",
            "Ramadan, Eid al-Fitr, Eid al-Adha, the Islamic New Year and more, from the Umm al-Qura calendar. Local moon sighting can move them by a day."),
        new("hindu", "Hindu festivals",
            "Holi and Diwali, from published Indian calendars - currently 2024 to 2028."),
        new("chinese", "Lunar New Year and Chinese festivals",
            "Lunar New Year and the Lantern, Dragon Boat, Qixi, Mid-Autumn and Double Ninth festivals, from the Chinese lunisolar calendar."),
    ];

    public static bool IsKnown(string setId) => Sets.Any(set => set.Id == setId);

    // Everything in the chosen sets between two years, inclusive, oldest first.
    public static List<Holiday> Between(IEnumerable<string> setIds, int fromYear, int toYear)
    {
        var result = new List<Holiday>();
        foreach (var setId in setIds.Distinct().Where(IsKnown))
        {
            // One year either side, then trimmed: a Saturday New Year's Day is observed the Friday
            // before, which is in the previous year.
            for (var year = fromYear - 1; year <= toYear + 1; year++)
            {
                result.AddRange(ForYear(setId, year).Where(holiday => holiday.Date.Year >= fromYear && holiday.Date.Year <= toYear));
            }
        }
        return result.DistinctBy(holiday => holiday.Id).OrderBy(holiday => holiday.Date).ThenBy(holiday => holiday.SetId).ToList();
    }

    public static IEnumerable<Holiday> ForYear(string setId, int year)
    {
        if (year < 1902 || year > 2099) return [];
        try
        {
            // Materialized inside the try: most of these are iterators, which would otherwise throw
            // out of range only later, wherever the result happened to be enumerated.
            IEnumerable<Holiday> days = setId switch
            {
                "us-federal" => UsFederal(year),
                "us-observances" => UsObservances(year),
                "christian" => Christian(year),
                "orthodox" => Orthodox(year),
                "jewish" => Jewish(year),
                "islamic" => Islamic(year),
                "hindu" => Hindu(year),
                "chinese" => Chinese(year),
                _ => [],
            };
            return days.ToList();
        }
        catch (ArgumentOutOfRangeException)
        {
            // Past the end of a calendar's supported range (Umm al-Qura stops in 2077).
            return [];
        }
    }

    // ---- US ------------------------------------------------------------------------------------

    private static IEnumerable<Holiday> UsFederal(int year)
    {
        const string set = "us-federal";
        var fixedDays = new (DateOnly Date, string Name)[]
        {
            (new DateOnly(year, 1, 1), "New Year's Day"),
            (new DateOnly(year, 6, 19), "Juneteenth"),
            (new DateOnly(year, 7, 4), "Independence Day"),
            (new DateOnly(year, 11, 11), "Veterans Day"),
            (new DateOnly(year, 12, 25), "Christmas Day"),
        };
        foreach (var (date, name) in fixedDays)
        {
            yield return new Holiday(set, date, name);
            // A Saturday holiday is observed the Friday before, a Sunday one the Monday after.
            if (date.DayOfWeek == DayOfWeek.Saturday) yield return new Holiday(set, date.AddDays(-1), $"{name} (observed)");
            if (date.DayOfWeek == DayOfWeek.Sunday) yield return new Holiday(set, date.AddDays(1), $"{name} (observed)");
        }
        yield return new Holiday(set, Nth(year, 1, DayOfWeek.Monday, 3), "Martin Luther King Jr. Day");
        yield return new Holiday(set, Nth(year, 2, DayOfWeek.Monday, 3), "Washington's Birthday (Presidents' Day)");
        yield return new Holiday(set, Last(year, 5, DayOfWeek.Monday), "Memorial Day");
        yield return new Holiday(set, Nth(year, 9, DayOfWeek.Monday, 1), "Labor Day");
        yield return new Holiday(set, Nth(year, 10, DayOfWeek.Monday, 2), "Columbus Day / Indigenous Peoples' Day");
        yield return new Holiday(set, Nth(year, 11, DayOfWeek.Thursday, 4), "Thanksgiving Day");
    }

    private static IEnumerable<Holiday> UsObservances(int year)
    {
        const string set = "us-observances";
        yield return new Holiday(set, new DateOnly(year, 2, 2), "Groundhog Day");
        yield return new Holiday(set, new DateOnly(year, 2, 14), "Valentine's Day");
        yield return new Holiday(set, Nth(year, 3, DayOfWeek.Sunday, 2), "Daylight saving time begins", Note: "Clocks go forward an hour at 2 a.m.");
        yield return new Holiday(set, new DateOnly(year, 3, 17), "St. Patrick's Day");
        yield return new Holiday(set, new DateOnly(year, 4, 22), "Earth Day");
        yield return new Holiday(set, Nth(year, 5, DayOfWeek.Sunday, 2), "Mother's Day");
        yield return new Holiday(set, new DateOnly(year, 6, 14), "Flag Day");
        yield return new Holiday(set, Nth(year, 6, DayOfWeek.Sunday, 3), "Father's Day");
        yield return new Holiday(set, new DateOnly(year, 10, 31), "Halloween");
        yield return new Holiday(set, Nth(year, 11, DayOfWeek.Sunday, 1), "Daylight saving time ends", Note: "Clocks go back an hour at 2 a.m.");
        // Federal general elections are in even years: the Tuesday after the first Monday.
        if (year % 2 == 0) yield return new Holiday(set, Nth(year, 11, DayOfWeek.Monday, 1).AddDays(1), "Election Day");
        yield return new Holiday(set, new DateOnly(year, 12, 31), "New Year's Eve");
    }

    // ---- Christian -----------------------------------------------------------------------------

    private static IEnumerable<Holiday> Christian(int year)
    {
        const string set = "christian";
        var easter = WesternEaster(year);
        yield return new Holiday(set, new DateOnly(year, 1, 6), "Epiphany");
        yield return new Holiday(set, easter.AddDays(-46), "Ash Wednesday", Note: "Lent begins.");
        yield return new Holiday(set, easter.AddDays(-7), "Palm Sunday");
        yield return new Holiday(set, easter.AddDays(-3), "Maundy Thursday");
        yield return new Holiday(set, easter.AddDays(-2), "Good Friday");
        yield return new Holiday(set, easter, "Easter Sunday");
        yield return new Holiday(set, easter.AddDays(39), "Ascension Day");
        yield return new Holiday(set, easter.AddDays(49), "Pentecost");
        yield return new Holiday(set, easter.AddDays(56), "Trinity Sunday");
        yield return new Holiday(set, new DateOnly(year, 11, 1), "All Saints' Day");
        // Advent begins on the fourth Sunday before Christmas.
        var christmas = new DateOnly(year, 12, 25);
        var sundayBefore = christmas.AddDays(-(((int)christmas.DayOfWeek + 6) % 7 + 1));
        yield return new Holiday(set, sundayBefore.AddDays(-21), "First Sunday of Advent");
        yield return new Holiday(set, new DateOnly(year, 12, 24), "Christmas Eve");
        yield return new Holiday(set, christmas, "Christmas Day");
    }

    private static IEnumerable<Holiday> Orthodox(int year)
    {
        const string set = "orthodox";
        var pascha = OrthodoxEaster(year);
        // Julian 25 December and 6 January, which fall on 7 and 19 January this century.
        yield return new Holiday(set, new DateOnly(year, 1, 7), "Orthodox Christmas");
        yield return new Holiday(set, new DateOnly(year, 1, 19), "Theophany");
        yield return new Holiday(set, pascha.AddDays(-48), "Clean Monday", Note: "Great Lent begins.");
        yield return new Holiday(set, pascha.AddDays(-7), "Orthodox Palm Sunday");
        yield return new Holiday(set, pascha.AddDays(-2), "Orthodox Good Friday");
        yield return new Holiday(set, pascha, "Orthodox Easter (Pascha)");
        yield return new Holiday(set, pascha.AddDays(39), "Orthodox Ascension");
        yield return new Holiday(set, pascha.AddDays(49), "Orthodox Pentecost");
    }

    // The anonymous Gregorian computus (Meeus/Jones/Butcher).
    public static DateOnly WesternEaster(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(year, month, day);
    }

    // Meeus's Julian computus, moved to the Gregorian calendar (13 days, valid 1900-2099).
    public static DateOnly OrthodoxEaster(int year)
    {
        var a = year % 4;
        var b = year % 7;
        var c = year % 19;
        var d = (19 * c + 15) % 30;
        var e = (2 * a + 4 * b - d + 34) % 7;
        var month = (d + e + 114) / 31;
        var day = (d + e + 114) % 31 + 1;
        return new DateOnly(year, month, day).AddDays(13);
    }

    // ---- Jewish --------------------------------------------------------------------------------

    private static readonly HebrewCalendar Hebrew = new();

    private static IEnumerable<Holiday> Jewish(int year)
    {
        const string set = "jewish";
        const string sundown = "Begins at sundown the evening before.";
        var result = new List<Holiday>();
        // The Hebrew years that overlap this Gregorian one: the year begins in September.
        foreach (var hebrewYear in new[] { year + 3760, year + 3761 })
        {
            DateOnly On(int month, int day) => HebrewDate(hebrewYear, month, day);
            result.Add(new Holiday(set, On(1, 1), "Rosh Hashanah", 2, sundown));
            result.Add(new Holiday(set, On(1, 10), "Yom Kippur", Note: sundown));
            result.Add(new Holiday(set, On(1, 15), "Sukkot", 7, sundown));
            result.Add(new Holiday(set, On(1, 22), "Shemini Atzeret", Note: sundown));
            result.Add(new Holiday(set, On(1, 23), "Simchat Torah", Note: sundown));
            result.Add(new Holiday(set, On(3, 25), "Hanukkah", 8, sundown));
            result.Add(new Holiday(set, On(5, 15), "Tu BiShvat", Note: sundown));
            result.Add(new Holiday(set, On(6, 14), "Purim", Note: sundown));
            result.Add(new Holiday(set, On(7, 15), "Passover", 8, sundown));
            result.Add(new Holiday(set, On(9, 6), "Shavuot", 2, sundown));
            // A fast can't fall on Shabbat; Tisha B'Av then moves to the Sunday.
            var tishaBAv = On(11, 9);
            if (tishaBAv.DayOfWeek == DayOfWeek.Saturday) tishaBAv = tishaBAv.AddDays(1);
            result.Add(new Holiday(set, tishaBAv, "Tisha B'Av", Note: sundown));
        }
        return result;
    }

    // Months counted as in an ordinary year: 1 Tishrei ... 6 Adar, 7 Nisan ... 12 Elul. In a leap
    // year .NET numbers Adar I as 6 and Adar II as 7, pushing Nisan onwards up by one; Purim is
    // kept in Adar II.
    private static DateOnly HebrewDate(int hebrewYear, int month, int day)
    {
        if (Hebrew.IsLeapYear(hebrewYear) && month >= 6) month++;
        return DateOnly.FromDateTime(Hebrew.ToDateTime(hebrewYear, month, day, 0, 0, 0, 0));
    }

    // ---- Islamic -------------------------------------------------------------------------------

    private static readonly UmAlQuraCalendar Hijri = new();

    private static IEnumerable<Holiday> Islamic(int year)
    {
        const string set = "islamic";
        var first = Hijri.GetYear(new DateTime(year, 1, 1));
        var last = Hijri.GetYear(new DateTime(year, 12, 31));
        var result = new List<Holiday>();
        for (var hijriYear = first; hijriYear <= last; hijriYear++)
        {
            DateOnly On(int month, int day) => DateOnly.FromDateTime(Hijri.ToDateTime(hijriYear, month, day, 0, 0, 0, 0));
            result.Add(new Holiday(set, On(1, 1), "Islamic New Year"));
            result.Add(new Holiday(set, On(1, 10), "Ashura"));
            result.Add(new Holiday(set, On(3, 12), "Mawlid al-Nabi"));
            result.Add(new Holiday(set, On(7, 27), "Isra and Mi'raj"));
            result.Add(new Holiday(set, On(9, 1), "Ramadan begins"));
            result.Add(new Holiday(set, On(9, 27), "Laylat al-Qadr"));
            result.Add(new Holiday(set, On(10, 1), "Eid al-Fitr"));
            result.Add(new Holiday(set, On(12, 9), "Day of Arafah"));
            result.Add(new Holiday(set, On(12, 10), "Eid al-Adha"));
        }
        return result;
    }

    // ---- Hindu ---------------------------------------------------------------------------------

    // Hindu festivals follow tithis that depend on sunrise and sunset in India, and the published
    // calendars settle the edge cases - so these are taken from them rather than computed.
    private static readonly Dictionary<int, (DateOnly Holi, DateOnly Diwali)> HinduDates = new()
    {
        [2024] = (new DateOnly(2024, 3, 25), new DateOnly(2024, 11, 1)),
        [2025] = (new DateOnly(2025, 3, 14), new DateOnly(2025, 10, 20)),
        [2026] = (new DateOnly(2026, 3, 4), new DateOnly(2026, 11, 8)),
        [2027] = (new DateOnly(2027, 3, 22), new DateOnly(2027, 10, 29)),
        [2028] = (new DateOnly(2028, 3, 11), new DateOnly(2028, 10, 17)),
    };

    private static IEnumerable<Holiday> Hindu(int year)
    {
        if (!HinduDates.TryGetValue(year, out var dates)) yield break;
        yield return new Holiday("hindu", dates.Holi, "Holi");
        yield return new Holiday("hindu", dates.Diwali, "Diwali");
    }

    // ---- Chinese -------------------------------------------------------------------------------

    private static readonly ChineseLunisolarCalendar Lunar = new();

    private static IEnumerable<Holiday> Chinese(int year)
    {
        const string set = "chinese";
        DateOnly On(int month, int day) => LunarDate(year, month, day);
        yield return new Holiday(set, On(1, 1), $"Lunar New Year ({Animals[(year - 4) % 12]})");
        yield return new Holiday(set, On(1, 15), "Lantern Festival");
        yield return new Holiday(set, On(5, 5), "Dragon Boat Festival");
        yield return new Holiday(set, On(7, 7), "Qixi Festival");
        yield return new Holiday(set, On(8, 15), "Mid-Autumn Festival");
        yield return new Holiday(set, On(9, 9), "Double Ninth Festival");
    }

    private static readonly string[] Animals =
        ["Rat", "Ox", "Tiger", "Rabbit", "Dragon", "Snake", "Horse", "Goat", "Monkey", "Rooster", "Dog", "Pig"];

    // .NET numbers a leap month in sequence: with a leap month after month 4, the leap month is 5
    // and the ordinary fifth month is 6. Festivals are never in the leap month.
    private static DateOnly LunarDate(int year, int month, int day)
    {
        var leap = Lunar.GetLeapMonth(year);
        if (leap > 0 && month >= leap) month++;
        return DateOnly.FromDateTime(Lunar.ToDateTime(year, month, day, 0, 0, 0, 0));
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static DateOnly Nth(int year, int month, DayOfWeek day, int n)
    {
        var first = new DateOnly(year, month, 1);
        var offset = ((int)day - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + 7 * (n - 1));
    }

    private static DateOnly Last(int year, int month, DayOfWeek day)
    {
        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        return last.AddDays(-(((int)last.DayOfWeek - (int)day + 7) % 7));
    }
}
