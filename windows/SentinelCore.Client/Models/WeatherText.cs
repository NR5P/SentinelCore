using System.Globalization;

namespace SentinelCore.Client.Models;

// Everything the Weather section works out from the server's answers before drawing anything:
// which half-days make a calendar day, what the next rain looks like, the moon, the words for the
// numbers. A port of the logic in core/SentinelCore.Web/wwwroot/weather.js, kept here rather than in
// the WPF view so it can be tested on any machine.

/// <summary>
/// Times in the location's own time zone (the NWS reports it with the forecast), so the forecast
/// for the house reads right even from a laptop two zones away - the dashboard does the same.
/// </summary>
public sealed class WeatherClock
{
    public WeatherClock(TimeZoneInfo zone, CultureInfo? culture = null)
    {
        Zone = zone;
        Culture = culture ?? CultureInfo.CurrentCulture;
    }

    public TimeZoneInfo Zone { get; }
    public CultureInfo Culture { get; }

    /// <summary>
    /// The clock for an IANA zone name ("America/Chicago"). An unknown or missing zone falls back
    /// to this machine's own rather than failing - the times are still right, just labelled locally.
    /// </summary>
    public static WeatherClock For(string? timeZoneId, CultureInfo? culture = null)
    {
        var zone = TimeZoneInfo.Local;
        if (!string.IsNullOrWhiteSpace(timeZoneId) && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var found))
        {
            zone = found;
        }
        return new WeatherClock(zone, culture);
    }

    public DateTime Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Zone).DateTime;

    private bool TwelveHour => Culture.DateTimeFormat.ShortTimePattern.Contains('h');

    /// <summary>"3:45 PM" (or "15:45" where the culture uses a 24-hour clock).</summary>
    public string Clock(DateTimeOffset at) => Local(at).ToString(TwelveHour ? "h:mm tt" : "HH:mm", Culture);

    /// <summary>"3 PM" (or "15:00").</summary>
    public string Hour(DateTimeOffset at) => Local(at).ToString(TwelveHour ? "h tt" : "HH:00", Culture);

    /// <summary>"Mon".</summary>
    public string Weekday(DateTimeOffset at) => Local(at).ToString("ddd", Culture);

    /// <summary>"Monday".</summary>
    public string LongWeekday(DateTimeOffset at) => Local(at).ToString("dddd", Culture);

    /// <summary>"Mon 3:45 PM".</summary>
    public string WeekdayClock(DateTimeOffset at) => $"{Weekday(at)} {Clock(at)}";

    /// <summary>A key that groups times by the local calendar day they fall on.</summary>
    public string DateKey(DateTimeOffset at) => Local(at).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// One card of the seven-day forecast. The NWS forecast is a list of half-days; the card shows one
/// per calendar day, the day period leading and the night giving the low.
/// </summary>
public sealed class WeatherDay
{
    public WeatherDay(string key) => Key = key;

    public string Key { get; }
    public WeatherPeriod? Day { get; internal set; }
    public WeatherPeriod? Night { get; internal set; }

    /// <summary>The period the card is named and pictured after: the day, or the night when the day has passed.</summary>
    public WeatherPeriod Lead => Day ?? Night!;
    public int? High => Day?.TemperatureF;
    public int? Low => Night?.TemperatureF;
    public int Chance => Math.Max(Day?.PrecipitationChance ?? 0, Night?.PrecipitationChance ?? 0);

    public string Wind => string.Join(" ", new[] { Lead.WindDirection, Lead.WindSpeed }.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>"💧 40% · SW 10 to 15 mph", or empty.</summary>
    public string Meta => string.Join(" · ", new[] { Chance > 0 ? $"💧 {Chance}%" : null, Wind.Length > 0 ? Wind : null }.Where(part => part is not null));

    /// <summary>Both halves' full forecasts, in order.</summary>
    public IEnumerable<WeatherPeriod> Periods => new[] { Day, Night }.OfType<WeatherPeriod>();

    public static List<WeatherDay> Group(IEnumerable<WeatherPeriod> periods, WeatherClock clock)
    {
        var days = new List<WeatherDay>();
        foreach (var period in periods)
        {
            var key = clock.DateKey(period.StartTime);
            var day = days.FirstOrDefault(existing => existing.Key == key);
            if (day is null)
            {
                day = new WeatherDay(key);
                days.Add(day);
            }
            if (period.IsDaytime) day.Day = period;
            else day.Night = period;
        }
        return days;
    }

    /// <summary>
    /// Where this day's range sits within the week's, as fractions of the bar: (left, width). The
    /// width never drops below 3% so a day with one temperature still shows a mark.
    /// </summary>
    public (double Left, double Width)? RangeWithin(int weekMin, int weekMax)
    {
        var from = Low ?? High;
        var to = High ?? Low;
        if (from is null || to is null) return null;
        var span = (double)Math.Max(weekMax - weekMin, 1);
        var width = Math.Clamp(Math.Abs(to.Value - from.Value) / span, 0.03, 1);
        var left = Math.Clamp((Math.Min(from.Value, to.Value) - weekMin) / span, 0, 1 - width);
        return (left, width);
    }

    /// <summary>The lowest and highest temperatures across every period, for the range bars.</summary>
    public static (int Min, int Max)? WeekRange(IEnumerable<WeatherPeriod> periods)
    {
        var temps = periods.Select(period => period.TemperatureF).OfType<int>().ToList();
        return temps.Count == 0 ? null : (temps.Min(), temps.Max());
    }
}

/// <summary>The moon's phase, worked out rather than fetched.</summary>
public readonly record struct MoonPhase(string Name, string Icon, int IlluminationPercent, int DaysToFull)
{
    private const double SynodicDays = 29.530588853;

    // A known new moon: 6 Jan 2000 18:14 UTC. Good to within a few hours, which is all a name needs.
    private static readonly DateTimeOffset KnownNew = new(2000, 1, 6, 18, 14, 0, TimeSpan.Zero);

    private static readonly (double Limit, string Name, string Icon)[] Phases =
    {
        (1.84566, "New moon", "🌑"),
        (5.53699, "Waxing crescent", "🌒"),
        (9.22831, "First quarter", "🌓"),
        (12.91963, "Waxing gibbous", "🌔"),
        (16.61096, "Full moon", "🌕"),
        (20.30228, "Waning gibbous", "🌖"),
        (23.99361, "Last quarter", "🌗"),
        (27.68493, "Waning crescent", "🌘"),
        (SynodicDays + 1, "New moon", "🌑"),
    };

    public static MoonPhase At(DateTimeOffset at)
    {
        var age = Modulo((at - KnownNew).TotalDays, SynodicDays);
        var illumination = (int)Math.Round((1 - Math.Cos(2 * Math.PI * age / SynodicDays)) / 2 * 100, MidpointRounding.AwayFromZero);
        var (_, name, icon) = Phases.First(phase => age < phase.Limit);
        var toFull = (int)Math.Round(Modulo(14.765 - age, SynodicDays), MidpointRounding.AwayFromZero);
        return new MoonPhase(name, icon, illumination, toFull);
    }

    private static double Modulo(double value, double by) => ((value % by) + by) % by;
}

/// <summary>A tracked storm heading for the house, or the house inside a warning outline.</summary>
public sealed class WeatherStormBanner
{
    public List<WeatherWarningArea> Covering { get; init; } = new();
    public List<WeatherStormCell> Incoming { get; init; } = new();

    /// <summary>"severe" when a tornado warning covers home or a rotating storm is coming; else "moderate".</summary>
    public string Level { get; init; } = "moderate";
}

public static class WeatherText
{
    // NWS icon codes (api.weather.gov/icons) to a glyph, (day, night). The server sends the code
    // rather than the NWS picture, so nothing here loads from outside the server. The same map as
    // the dashboard's, minus the emoji variation selectors WPF has no use for.
    private static readonly Dictionary<string, (string Day, string Night)> Glyphs = new()
    {
        ["skc"] = ("☀", "🌙"),
        ["few"] = ("🌤", "🌙"),
        ["sct"] = ("⛅", "☁"),
        ["bkn"] = ("🌥", "☁"),
        ["ovc"] = ("☁", "☁"),
        ["wind_skc"] = ("💨", "💨"),
        ["wind_few"] = ("💨", "💨"),
        ["wind_sct"] = ("💨", "💨"),
        ["wind_bkn"] = ("💨", "💨"),
        ["wind_ovc"] = ("💨", "💨"),
        ["snow"] = ("🌨", "🌨"),
        ["rain_snow"] = ("🌨", "🌨"),
        ["rain_sleet"] = ("🌨", "🌨"),
        ["snow_sleet"] = ("🌨", "🌨"),
        ["fzra"] = ("🧊", "🧊"),
        ["rain_fzra"] = ("🧊", "🧊"),
        ["snow_fzra"] = ("🧊", "🧊"),
        ["sleet"] = ("🌨", "🌨"),
        ["rain"] = ("🌧", "🌧"),
        ["rain_showers"] = ("🌦", "🌧"),
        ["rain_showers_hi"] = ("🌦", "🌧"),
        ["tsra"] = ("⛈", "⛈"),
        ["tsra_sct"] = ("⛈", "⛈"),
        ["tsra_hi"] = ("🌩", "🌩"),
        ["tornado"] = ("🌪", "🌪"),
        ["hurricane"] = ("🌀", "🌀"),
        ["tropical_storm"] = ("🌀", "🌀"),
        ["dust"] = ("🌫", "🌫"),
        ["smoke"] = ("🌫", "🌫"),
        ["haze"] = ("🌫", "🌫"),
        ["fog"] = ("🌫", "🌫"),
        ["hot"] = ("🥵", "🥵"),
        ["cold"] = ("🥶", "🥶"),
        ["blizzard"] = ("🌨", "🌨"),
    };

    /// <summary>The glyph for an NWS icon code; anything unrecognised gets a thermometer.</summary>
    public static string Glyph(string? code, bool? isDaytime)
    {
        if (code is null || !Glyphs.TryGetValue(code, out var pair)) return "🌡";
        return isDaytime == false ? pair.Night : pair.Day;
    }

    /// <summary>The place heading's second line: "near City, ST · 41.2500, -96.0000 · forecast office OAX".</summary>
    public static string PlaceMeta(WeatherLocation location)
    {
        var parts = new List<string>();
        var nearest = location.Nearest;
        if (!string.IsNullOrWhiteSpace(location.Label) && nearest.Length > 0
            && !string.Equals(nearest, location.Label, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"near {nearest}");
        }
        parts.Add(string.Create(CultureInfo.InvariantCulture, $"{location.Latitude:0.0000}, {location.Longitude:0.0000}"));
        if (!string.IsNullOrWhiteSpace(location.ForecastOffice)) parts.Add($"forecast office {location.ForecastOffice}");
        return string.Join(" · ", parts);
    }

    /// <summary>Rain or snow amounts: "—", "0\"", "trace" or "0.25\"".</summary>
    public static string Inches(double? value, int digits = 2)
    {
        if (value is null) return "—";
        if (value.Value == 0) return "0\"";
        if (value.Value < 0.01) return "trace";
        return value.Value.ToString("F" + digits, CultureInfo.InvariantCulture) + "\"";
    }

    public static string? WindText(WeatherCurrent current)
    {
        if (current.WindMph is not { } mph) return null;
        if (mph == 0) return "calm";
        return $"{(string.IsNullOrWhiteSpace(current.WindDirection) ? "" : current.WindDirection + " ")}{mph} mph";
    }

    /// <summary>How the dew point feels - the number meteorologists actually use for "muggy".</summary>
    public static string DewComfort(int dewpointF) => dewpointF switch
    {
        >= 70 => " (oppressive)",
        >= 65 => " (muggy)",
        >= 60 => " (sticky)",
        <= 30 => " (dry)",
        _ => "",
    };

    public static string PressureArrow(string? trend) => trend switch
    {
        "rising fast" => "⇈",
        "rising" => "↑",
        "steady" => "→",
        "falling" => "↓",
        "falling fast" => "⇊",
        _ => "",
    };

    /// <summary>"30.02 inHg ↓ falling", or null with no reading.</summary>
    public static string? PressureText(WeatherCurrent current, WeatherPressureTrend? trend)
    {
        if (current.PressureInHg is not { } pressure) return null;
        var text = pressure.ToString("0.00", CultureInfo.InvariantCulture) + " inHg";
        if (trend is not null) text += $" {PressureArrow(trend.Trend)} {trend.Trend}";
        return text;
    }

    /// <summary>"14h 2m" between sunrise and sunset, or null.</summary>
    public static string? DaylightText(WeatherSun? sun)
    {
        if (sun?.Sunrise is not { } rise || sun.Sunset is not { } set) return null;
        var minutes = (int)Math.Round((set - rise).TotalMinutes);
        if (minutes <= 0) return null;
        return $"{minutes / 60}h {minutes % 60}m";
    }

    /// <summary>"H 78°  L 61°" from the first day and first night of the forecast.</summary>
    public static string HighLow(IReadOnlyList<WeatherPeriod> daily)
    {
        var today = daily.FirstOrDefault(period => period.IsDaytime);
        var tonight = daily.FirstOrDefault(period => !period.IsDaytime);
        var parts = new List<string>();
        if (today?.TemperatureF is { } high) parts.Add($"H {high}°");
        if (tonight?.TemperatureF is { } low) parts.Add($"L {low}°");
        return string.Join("  ", parts);
    }

    private static string PrecipKind(WeatherHour hour)
    {
        var text = hour.ShortForecast.ToLowerInvariant();
        if (text.Contains("thunder")) return "Storms";
        if (text.Contains("snow") || text.Contains("flurr")) return "Snow";
        if (text.Contains("sleet") || text.Contains("freezing")) return "Wintry mix";
        if (text.Contains("drizzle")) return "Drizzle";
        return "Rain";
    }

    /// <summary>
    /// A one-line "what's next" from the hourly forecast: when rain (or snow, or storms) starts or
    /// stops in the next twelve hours.
    /// </summary>
    public static string? NextPrecipitation(IReadOnlyList<WeatherHour> hourly, WeatherClock clock)
    {
        var hours = hourly.Take(12).ToList();
        if (hours.Count == 0) return null;
        static bool Wet(WeatherHour hour) => (hour.PrecipitationChance ?? 0) >= 50;

        if (Wet(hours[0]))
        {
            var stop = hours.FindIndex(hour => !Wet(hour));
            return stop < 0
                ? $"{PrecipKind(hours[0])} likely for the next 12 hours."
                : $"{PrecipKind(hours[0])} likely until around {clock.Hour(hours[stop].StartTime)}.";
        }

        var start = hours.FirstOrDefault(Wet);
        if (start is not null)
            return $"{PrecipKind(start)} likely from around {clock.Hour(start.StartTime)} ({start.PrecipitationChance}%).";

        var peak = hours.Aggregate((best, hour) => (hour.PrecipitationChance ?? 0) > (best.PrecipitationChance ?? 0) ? hour : best);
        if ((peak.PrecipitationChance ?? 0) >= 20)
            return $"Slight chance of {PrecipKind(peak).ToLowerInvariant()} around {clock.Hour(peak.StartTime)} ({peak.PrecipitationChance}%).";
        return "No rain expected in the next 12 hours.";
    }

    /// <summary>The details endpoint's hours, by start time, to join onto the report's.</summary>
    public static Dictionary<long, WeatherHourDetail> HourDetailsByTime(WeatherDetails? details)
    {
        var map = new Dictionary<long, WeatherHourDetail>();
        foreach (var hour in details?.Hours ?? new List<WeatherHourDetail>()) map[hour.StartTime.UtcTicks] = hour;
        return map;
    }

    /// <summary>Everything about an hour that doesn't fit in its column, for its tooltip.</summary>
    public static string HourTip(WeatherHour hour, WeatherHourDetail? extra)
    {
        var wind = string.Join(" ", new[] { hour.WindDirection, hour.WindSpeed }.Where(part => !string.IsNullOrWhiteSpace(part)));
        var parts = new[]
        {
            string.IsNullOrWhiteSpace(hour.ShortForecast) ? null : hour.ShortForecast,
            wind.Length > 0 ? $"wind {wind}" : null,
            extra?.GustMph is { } gust and > 0 ? $"gusts {gust} mph" : null,
            hour.HumidityPercent is { } humidity ? $"humidity {humidity}%" : null,
            extra?.SkyCoverPercent is { } sky ? $"cloud {sky}%" : null,
            extra is { RainInches: > 0 } ? $"rain {Inches(extra.RainInches)}" : null,
            extra is { SnowInches: > 0 } ? $"snow {Inches(extra.SnowInches, 1)}" : null,
        };
        return string.Join(" · ", parts.Where(part => part is not null));
    }

    /// <summary>The hour column's short wind: "10 SW" from "10 mph" and "SW".</summary>
    public static string HourWind(WeatherHour hour)
    {
        var speed = (hour.WindSpeed ?? "").Replace(" mph", "", StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(hour.WindDirection) ? speed : $"{speed} {hour.WindDirection}".Trim();
    }

    // ---- The severe outlook ----

    /// <summary>The SPC's words for a category on its 1-to-5 scale.</summary>
    public static string OutlookScale(string? category) => category switch
    {
        "TSTM" => "thunderstorms possible",
        "MRGL" => "1 of 5 · marginal",
        "SLGT" => "2 of 5 · slight",
        "ENH" => "3 of 5 · enhanced",
        "MDT" => "4 of 5 · moderate",
        "HIGH" => "5 of 5 · high",
        _ => "no severe storms expected",
    };

    /// <summary>0 for none or general thunder, up to 5 for HIGH - how loudly to show the chip.</summary>
    public static int OutlookLevel(string? category) => category switch
    {
        "MRGL" => 1,
        "SLGT" => 2,
        "ENH" => 3,
        "MDT" => 4,
        "HIGH" => 5,
        _ => 0,
    };

    /// <summary>"Today", "Tomorrow", then the weekday.</summary>
    public static string OutlookDayName(int day, DateTimeOffset now, WeatherClock clock) => day switch
    {
        1 => "Today",
        2 => "Tomorrow",
        _ => clock.LongWeekday(now.AddDays(day - 1)),
    };

    // ---- Storms ----

    public static string CellTitle(WeatherStormCell cell)
    {
        if (cell.Tornadic) return "Possible tornado";
        if (cell.Rotation) return "Rotating storm";
        return cell.Threat switch
        {
            2 => "Severe hail storm",
            1 => "Strong storm",
            _ => "Storm",
        };
    }

    public static string CellHazards(WeatherStormCell cell)
    {
        var parts = new List<string>();
        if (cell.Tornadic) parts.Add("tornado vortex signature");
        if (cell.Rotation) parts.Add($"mesocyclone (rank {cell.MesoStrength})");
        if (cell.MaxHailInches is { } hail and > 0) parts.Add($"hail to {hail.ToString("0.00", CultureInfo.InvariantCulture)}\"");
        else if (cell.HailProbability is { } chance and > 0) parts.Add($"{chance}% hail");
        return string.Join(" · ", parts);
    }

    /// <summary>"12 mi SW · moving NE 35 mph · tops 45k ft · hail to 1.25\"".</summary>
    public static string CellMeta(WeatherStormCell cell)
    {
        var motion = cell.SpeedMph is { } speed and > 0 ? $"moving {cell.HeadingCompass} {speed} mph" : "nearly stationary";
        var parts = new List<string>
        {
            $"{(cell.DistanceMiles ?? 0).ToString("0", CultureInfo.InvariantCulture)} mi {cell.BearingCompass}".TrimEnd(),
            motion,
        };
        if (cell.TopKft is { } top and > 0) parts.Add($"tops {top.ToString("0", CultureInfo.InvariantCulture)}k ft");
        var hazards = CellHazards(cell);
        if (hazards.Length > 0) parts.Add(hazards);
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The storms card: cells within 100 miles, the ones heading home first, then the most
    /// dangerous, then the nearest - six at most.
    /// </summary>
    public static List<WeatherStormCell> StormsNearHome(WeatherStorms? storms) =>
        (storms?.Cells ?? new List<WeatherStormCell>())
            .Where(cell => cell.DistanceMiles is { } miles && miles <= 100)
            .OrderByDescending(cell => cell.TowardHome)
            .ThenByDescending(cell => cell.Threat)
            .ThenBy(cell => cell.DistanceMiles)
            .Take(6)
            .ToList();

    // Warnings worth a banner when home is inside one: tornado, severe thunderstorm, flash flood,
    // extreme wind, snow squall, special marine.
    private static readonly HashSet<string> BannerPhenomena = new() { "TO", "SV", "FF", "EW", "SQ", "MA" };

    /// <summary>The banner at the top of the section, or null when nothing is on its way.</summary>
    public static WeatherStormBanner? StormBanner(WeatherStorms? storms)
    {
        if (storms is null) return null;
        var incoming = storms.Cells
            .Where(cell => cell.TowardHome && cell.EtaMinutes is not null)
            .OrderByDescending(cell => cell.Threat)
            .ThenBy(cell => cell.EtaMinutes)
            .ToList();
        var covering = storms.Warnings.Where(warning => warning.ContainsHome && BannerPhenomena.Contains(warning.Phenomena)).ToList();
        if (incoming.Count == 0 && covering.Count == 0) return null;
        var severe = covering.Any(warning => warning.Phenomena == "TO") || incoming.Any(cell => cell.Threat >= 3);
        return new WeatherStormBanner
        {
            Covering = covering,
            Incoming = incoming.Take(3).ToList(),
            Level = severe ? "severe" : "moderate",
        };
    }

    public static string WarningTags(WeatherWarningArea warning)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(warning.TornadoTag)) parts.Add($"tornado {warning.TornadoTag.ToLowerInvariant()}");
        if (warning.HailInches is { } hail and > 0) parts.Add($"hail {hail.ToString("0.##", CultureInfo.InvariantCulture)}\"");
        if (warning.WindMph is { } wind and > 0) parts.Add($"wind {wind.ToString("0", CultureInfo.InvariantCulture)} mph");
        if (!string.IsNullOrWhiteSpace(warning.DamageTag)) parts.Add($"{warning.DamageTag.ToLowerInvariant()} damage threat");
        return string.Join(" · ", parts);
    }

    /// <summary>"Home is inside a Tornado Warning until 4:15 PM · tornado radar indicated".</summary>
    public static string WarningLine(WeatherWarningArea warning, WeatherClock clock)
    {
        var until = warning.Expires is { } expires ? $" until {clock.Clock(expires)}" : "";
        var tags = WarningTags(warning);
        return $"Home is inside a {warning.Name}{until}{(tags.Length > 0 ? " · " + tags : "")}";
    }

    /// <summary>"Rotating storm heading this way · about 25 min out, passing 2.3 mi away · now 18 mi SW".</summary>
    public static string IncomingLine(WeatherStormCell cell)
    {
        var passing = cell.ClosestMiles is { } closest && closest >= 1
            ? $"{closest.ToString("0.0", CultureInfo.InvariantCulture)} mi away"
            : "overhead";
        var hazards = CellHazards(cell);
        return $"{CellTitle(cell)} heading this way · about {cell.EtaMinutes} min out, passing {passing}" +
               $" · now {(cell.DistanceMiles ?? 0).ToString("0", CultureInfo.InvariantCulture)} mi {cell.BearingCompass}".TrimEnd() +
               (hazards.Length > 0 ? " · " + hazards : "");
    }
}
