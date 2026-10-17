using System.Security.Cryptography;
using System.Text;

namespace SentinelCore.Web;

public sealed class ScheduleWeatherTestDto
{
    public string? Metric { get; set; }
    public string? Op { get; set; }
    public double? Value { get; set; }
    public int? Window { get; set; }
}

public sealed class ScheduleWeatherRequest
{
    public List<ScheduleWeatherTestDto> Tests { get; set; } = [];
    // The main app's schedule time zone, so "today" and "the last 3 days" are its days.
    public string? TimeZone { get; set; }
}

// Weather tests for the main app's Schedules (ScheduleWorker in the SentinelCore project). The
// schedules run there, but the weather - WeatherService and WeatherRuleMetrics - lives here, so
// the worker asks this endpoint before a firing: "every morning at 6, sprinklers on, only if
// there's been half an inch of rain or less in the last 3 days".
public static class ScheduleWeatherEndpoints
{
    public static void MapScheduleWeatherEndpoints(this WebApplication app)
    {
        // For the main app, not a browser: no login, the shared Bridge:ApiKey instead - the same
        // key the dashboard sends the other way. Fails closed when no key is configured.
        app.MapPost("/api/internal/schedule-weather", async (
            ScheduleWeatherRequest request, HttpContext http, AppSettingsFile mainAppSettings,
            IAutomationWeather weather, ILogger<ScheduleProxyLog> logger, CancellationToken ct) =>
        {
            var bridge = await mainAppSettings.GetSectionAsync<BridgeSettingsDto>("Bridge", ct);
            if (!KeyMatches(bridge?.ApiKey, http.Request.Headers["X-Api-Key"].ToString()))
                return Results.Json(new { error = "Missing or invalid X-Api-Key." }, statusCode: 401);

            return Results.Ok(new { results = await MeasureAsync(request, weather, logger, ct) });
        });

        // The editor's "check now": what each test measures at this moment, so whoever is setting
        // up the sprinklers can see "0.3 inches of rain in the last 3 days - passes" before saving.
        app.MapPost("/api/schedules/weather-check", async (
            ScheduleWeatherRequest request, IAutomationWeather weather, ILogger<ScheduleProxyLog> logger, CancellationToken ct) =>
            Results.Ok(new { results = await MeasureAsync(request, weather, logger, ct) }))
            .RequireAuthorization();
    }

    public static async Task<List<ScheduleWeatherResultDto>> MeasureAsync(
        ScheduleWeatherRequest request, IAutomationWeather weather, ILogger logger, CancellationToken ct,
        DateTimeOffset? now = null)
    {
        WeatherOutlook? outlook = null;
        try
        {
            outlook = await weather.GetOutlookAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogInformation(exception, "A schedule weather check couldn't get the weather");
        }

        return Evaluate(request, outlook, now ?? DateTimeOffset.UtcNow);
    }

    // No network: the tests feed it fixtures.
    public static List<ScheduleWeatherResultDto> Evaluate(ScheduleWeatherRequest request, WeatherOutlook? outlook, DateTimeOffset now)
    {
        var zone = OrganizerAutomationSchedule.ZoneNamed(request.TimeZone);
        return request.Tests.Select(test =>
        {
            var condition = new BoardAutomationCondition
            {
                Type = "weather",
                Metric = test.Metric,
                Op = test.Op == "atMost" ? "atMost" : "atLeast",
                Value = test.Value,
                Window = test.Window,
            };
            var wanted = WeatherRuleMetrics.Describe(condition);
            if (!WeatherRuleMetrics.All.Contains(test.Metric))
                return new ScheduleWeatherResultDto(null, "", wanted, $"\"{test.Metric}\" isn't something this server can measure", null);

            var measure = WeatherRuleMetrics.Measure(outlook, test.Metric, WeatherRuleMetrics.WindowOf(condition), now, zone);
            if (measure.Value is null)
                return new ScheduleWeatherResultDto(null, "", wanted, measure.Problem ?? "nothing to measure", null);

            return new ScheduleWeatherResultDto(
                WeatherRuleMetrics.Holds(condition, measure), WeatherRuleMetrics.Observed(condition, measure), wanted, null, measure.Value);
        }).ToList();
    }

    private static bool KeyMatches(string? configured, string supplied)
    {
        if (string.IsNullOrEmpty(configured)) return false;
        var expected = Encoding.UTF8.GetBytes(configured);
        var actual = Encoding.UTF8.GetBytes(supplied);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

// Matches ScheduleWeatherResult in the main app. Holds is null when the weather couldn't be
// measured, and Problem then says why.
public sealed record ScheduleWeatherResultDto(bool? Holds, string Observed, string Wanted, string? Problem, double? Value);
