using System.Globalization;

namespace SentinelCore.Web;

// The weather side of automations: "only if it has rained less than an inch in the last 7 days",
// "when a freeze shows up in the next 7 days". WeatherService gathers a WeatherOutlook; this file
// turns one into a number for a rule to compare, with no network, so the tests can feed it
// fixtures. Everything is in US units, the same as the Weather tab.
public interface IAutomationWeather
{
    Task<WeatherOutlook?> GetOutlookAsync(CancellationToken cancellationToken);
}

public sealed class WeatherServiceAutomationWeather(WeatherService weather) : IAutomationWeather
{
    public Task<WeatherOutlook?> GetOutlookAsync(CancellationToken cancellationToken) => weather.GetOutlookAsync(cancellationToken);
}

public sealed class WeatherOutlook
{
    public DateTimeOffset FetchedAt { get; init; }
    public string? TimeZone { get; init; }
    // The seven-day forecast's day and night periods.
    public IReadOnlyList<WeatherPeriod> Periods { get; init; } = [];
    // The forecast grid: rain and snow amounts hour by hour. Null when it couldn't be fetched.
    public GridSet? Grid { get; init; }
    public int? TemperatureNowF { get; init; }
    public string? Station { get; init; }
    // Daily totals for the last couple of weeks, today (so far) included. Null when the history
    // couldn't be fetched - not the same as "no rain".
    public IReadOnlyList<WeatherHistoryDay>? Past { get; init; }
}

public sealed record WeatherHistoryDay(DateOnly Date, double? RainInches, double? SnowInches, double? LowF, double? HighF);

// What a weather test measured: the number, the day it's about (the night of the freeze), and
// the words a reminder can quote. Value is null when there was nothing to measure it from.
public sealed record WeatherMeasure(double? Value, DateOnly? Day, string Words, string? Problem = null)
{
    // The day is really that day's night - a low comes overnight - so "{weatherDay}" says "tonight"
    // or "Tuesday night".
    public bool Night { get; init; }
}

public static class WeatherRuleMetrics
{
    public const string RainPast = "rainPast";
    public const string RainNext = "rainNext";
    public const string SnowNext = "snowNext";
    public const string LowNext = "lowNext";
    public const string HighNext = "highNext";
    public const string RainChanceNext = "rainChanceNext";
    public const string TempNow = "tempNow";

    public static readonly string[] All = [RainPast, RainNext, SnowNext, LowNext, HighNext, RainChanceNext, TempNow];
    public static readonly string[] Ops = ["atLeast", "atMost"];

    public const int MaxPastDays = WeatherService.RainHistoryDays;
    public const int MaxForecastDays = 7;

    public static int WindowOf(BoardAutomationCondition condition) =>
        Math.Clamp(condition.Window ?? 7, 1, condition.Metric == RainPast ? MaxPastDays : MaxForecastDays);

    public static WeatherMeasure Measure(WeatherOutlook? outlook, string? metric, int window, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (outlook is null) return new WeatherMeasure(null, null, "", "there's no weather for this server's location (Settings → LOCATION)");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var until = now + TimeSpan.FromDays(window);

        switch (metric)
        {
            case RainPast:
            {
                if (outlook.Past is null) return new WeatherMeasure(null, null, "", "the rain history couldn't be fetched");
                var from = today.AddDays(1 - window);
                var days = outlook.Past.Where(d => d.Date >= from && d.Date <= today).ToList();
                // A day missing altogether is a station gap; a day with no number is a dry or
                // not-yet-reported day. Both count as no rain - but with no days at all, say so.
                if (days.Count == 0) return new WeatherMeasure(null, null, "", "the rain history has nothing for those days");
                var total = Math.Round(days.Sum(d => Math.Max(0, d.RainInches ?? 0)), 2);
                return new WeatherMeasure(total, null, Inches(total));
            }

            case RainNext or SnowNext:
            {
                if (outlook.Grid is null) return new WeatherMeasure(null, null, "", "the forecast rain and snow amounts couldn't be fetched");
                var series = metric == RainNext ? outlook.Grid.Rain : outlook.Grid.Snow;
                var total = Math.Round(WeatherService.ToInches(WeatherService.AmountBetween(series, now, until), series.FirstOrDefault()?.Unit), 2);
                // The day most of it falls on, for "{day}".
                DateOnly? wettest = null;
                double most = 0;
                for (var day = today; day <= today.AddDays(window); day = day.AddDays(1))
                {
                    var start = OrganizerAutomationSchedule.ToUtc(day, TimeSpan.Zero, zone);
                    var amount = WeatherService.AmountBetween(series, start > now ? start : now, start.AddDays(1) < until ? start.AddDays(1) : until);
                    if (amount > most) { most = amount; wettest = day; }
                }
                return new WeatherMeasure(total, wettest, Inches(total));
            }

            case LowNext or HighNext or RainChanceNext:
            {
                var periods = outlook.Periods.Where(p => p.EndTime > now && p.StartTime < until).ToList();
                if (metric == LowNext) periods = [.. periods.Where(p => !p.IsDaytime)];
                if (metric == HighNext) periods = [.. periods.Where(p => p.IsDaytime)];
                var withValue = periods
                    .Select(p => (Period: p, Value: metric == RainChanceNext ? (double?)p.PrecipitationChance : p.TemperatureF))
                    .Where(x => x.Value is not null)
                    .ToList();
                if (withValue.Count == 0)
                {
                    // No chance given anywhere means the NWS expects none.
                    if (metric == RainChanceNext && periods.Count > 0) return new WeatherMeasure(0, null, "0%");
                    return new WeatherMeasure(null, null, "", "the forecast couldn't be fetched");
                }
                var pick = metric == LowNext ? withValue.MinBy(x => x.Value) : withValue.MaxBy(x => x.Value);
                var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(pick.Period.StartTime, zone).DateTime);
                var words = metric == RainChanceNext ? $"{pick.Value:0}%" : $"{pick.Value:0}°F";
                return new WeatherMeasure(pick.Value, day, words) { Night = !pick.Period.IsDaytime };
            }

            case TempNow:
                return outlook.TemperatureNowF is { } temperature
                    ? new WeatherMeasure(temperature, today, $"{temperature}°F")
                    : new WeatherMeasure(null, null, "", "no nearby station has reported a temperature");

            default:
                return new WeatherMeasure(null, null, "", $"\"{metric}\" isn't something this server can measure");
        }
    }

    public static bool Holds(BoardAutomationCondition condition, WeatherMeasure measure) =>
        measure.Value is { } value && condition.Value is { } threshold &&
        (condition.Op == "atMost" ? value <= threshold : value >= threshold);

    // "a low of 32°F or colder in the next 7 days" - for run logs and rule summaries.
    public static string Describe(BoardAutomationCondition condition)
    {
        var n = WindowOf(condition);
        var span = condition.Metric == RainPast
            ? (n == 1 ? "today" : $"in the last {n} days")
            : (n == 1 ? "in the next day" : $"in the next {n} days");
        var value = condition.Value ?? 0;
        var most = condition.Op == "atMost";
        var amount = most ? "or less" : "or more";
        return condition.Metric switch
        {
            RainPast => $"{Inches(value)} of rain {amount} {span}",
            RainNext => $"{Inches(value)} of rain {amount} forecast {span}",
            SnowNext => $"{Inches(value)} of snow {amount} forecast {span}",
            LowNext => most ? $"a low of {value:0}°F or colder {span}" : $"lows staying at {value:0}°F or warmer {span}",
            HighNext => most ? $"highs staying at {value:0}°F or cooler {span}" : $"a high of {value:0}°F or hotter {span}",
            RainChanceNext => most ? $"no more than a {value:0}% chance of rain {span}" : $"a {value:0}% chance of rain or more {span}",
            TempNow => $"{value:0}°F {(most ? "or colder" : "or warmer")} right now",
            _ => condition.Metric ?? "weather",
        };
    }

    public static string Inches(double value) =>
        value == 1 ? "1 inch" : $"{value.ToString("0.##", CultureInfo.InvariantCulture)} inches";
}
