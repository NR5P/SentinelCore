using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace SentinelCore;

// What the dashboard said about one weather test: whether it held, what was actually measured
// ("1.2 inches of rain in the last 3 days") and what the test asked for. Holds is null when the
// weather couldn't be measured, and Problem then says why.
public sealed record ScheduleWeatherResult(bool? Holds, string Observed, string Wanted, string? Problem);

// The whole set of tests, boiled down: Holds is true only when every test held, false when one
// clearly didn't, and null when the weather couldn't be had - which a schedule treats as "don't
// run" rather than guessing, since sprinklers on in a downpour is worse than a missed watering.
public sealed record ScheduleWeatherVerdict(bool? Holds, string Detail)
{
    public static ScheduleWeatherVerdict From(IReadOnlyList<ScheduleWeatherResult> results)
    {
        if (results.Count == 0) return new(null, "the dashboard returned no weather");

        var problem = results.FirstOrDefault(r => r.Holds is null);
        if (problem is not null)
            return new(null, $"couldn't check the weather: {problem.Problem ?? "no answer from the dashboard"}");

        var failed = results.Where(r => r.Holds == false).ToList();
        if (failed.Count > 0)
            return new(false, string.Join("; ", failed.Select(r => $"{r.Observed} (needs {r.Wanted})")));

        return new(true, string.Join("; ", results.Select(r => r.Observed)));
    }
}

// The weather lives in the dashboard (SentinelCore.Web's WeatherService and WeatherRuleMetrics),
// so this project asks it rather than growing a second copy of the NWS and mesonet code.
public interface IScheduleWeather
{
    Task<ScheduleWeatherVerdict> CheckAsync(
        IReadOnlyList<ScheduleWeatherTest> tests, TimeZoneInfo zone, CancellationToken cancellationToken);
}

// Calls the dashboard's /api/internal/schedule-weather with this app's own API key - the same
// shared Bridge:ApiKey the dashboard uses to call this app, pointed the other way.
public sealed class DashboardScheduleWeather(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<ScheduleOptions> scheduleOptions,
    IOptionsMonitor<BridgeOptions> bridgeOptions,
    ILogger<DashboardScheduleWeather> logger) : IScheduleWeather
{
    public const string HttpClientName = nameof(DashboardScheduleWeather);
    public const string Path = "/api/internal/schedule-weather";

    public async Task<ScheduleWeatherVerdict> CheckAsync(
        IReadOnlyList<ScheduleWeatherTest> tests, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var baseUrl = scheduleOptions.CurrentValue.DashboardUrl.TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + Path)
        {
            Content = JsonContent.Create(new { tests, timeZone = zone.Id })
        };
        request.Headers.TryAddWithoutValidation("X-Api-Key", bridgeOptions.CurrentValue.ApiKey);

        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("The dashboard at {BaseUrl} answered {Status} to a schedule weather check", baseUrl, (int)response.StatusCode);
                return new(null, $"couldn't check the weather: the dashboard answered {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadFromJsonAsync<ScheduleWeatherResponse>(cancellationToken);
            return ScheduleWeatherVerdict.From(body?.Results ?? []);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                                   && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Couldn't reach the dashboard at {BaseUrl} for a schedule weather check", baseUrl);
            return new(null, $"couldn't check the weather: the dashboard at {baseUrl} didn't answer");
        }
    }

    private sealed record ScheduleWeatherResponse(List<ScheduleWeatherResult> Results);
}
