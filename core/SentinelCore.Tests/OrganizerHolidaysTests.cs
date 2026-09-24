extern alias WebHost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SentinelCore.Web;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// Holiday calendars, checked against published dates - including the cases that need a real
// calendar system: Hebrew and Chinese leap years, Saturday federal holidays observed on Friday.
public sealed class OrganizerHolidaysTests : IDisposable
{
    private readonly string _settingsPath;
    private readonly string _dataDirectory;

    public OrganizerHolidaysTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"organizer-holidays-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);
        _settingsPath = Path.Combine(_dataDirectory, "appsettings.json");
        File.WriteAllText(_settingsPath, WebHostTestSettings.LoginOffSettings);
    }

    public void Dispose()
    {
        try { TempDirectories.Delete(_dataDirectory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string On(string set, int year, string name) =>
        OrganizerHolidays.Between([set], year, year).Single(holiday => holiday.Name == name).Date.ToString("yyyy-MM-dd");

    [Theory]
    [InlineData(2024, "2024-03-31", "2024-05-05")]
    [InlineData(2025, "2025-04-20", "2025-04-20")]
    [InlineData(2026, "2026-04-05", "2026-04-12")]
    [InlineData(2027, "2027-03-28", "2027-05-02")]
    public void Easter_WesternAndOrthodox(int year, string western, string orthodox)
    {
        Assert.Equal(western, On("christian", year, "Easter Sunday"));
        Assert.Equal(orthodox, On("orthodox", year, "Orthodox Easter (Pascha)"));
    }

    [Fact]
    public void Christian_AdventAndGoodFriday()
    {
        Assert.Equal("2025-11-30", On("christian", 2025, "First Sunday of Advent"));
        Assert.Equal("2026-11-29", On("christian", 2026, "First Sunday of Advent"));
        Assert.Equal("2026-04-03", On("christian", 2026, "Good Friday"));
        Assert.Equal("2026-02-18", On("christian", 2026, "Ash Wednesday"));
    }

    [Fact]
    public void UsFederal_2026_IncludingTheObservedFriday()
    {
        Assert.Equal("2026-01-19", On("us-federal", 2026, "Martin Luther King Jr. Day"));
        Assert.Equal("2026-02-16", On("us-federal", 2026, "Washington's Birthday (Presidents' Day)"));
        Assert.Equal("2026-05-25", On("us-federal", 2026, "Memorial Day"));
        Assert.Equal("2026-07-03", On("us-federal", 2026, "Independence Day (observed)"));
        Assert.Equal("2026-09-07", On("us-federal", 2026, "Labor Day"));
        Assert.Equal("2026-10-12", On("us-federal", 2026, "Columbus Day / Indigenous Peoples' Day"));
        Assert.Equal("2026-11-26", On("us-federal", 2026, "Thanksgiving Day"));
        Assert.Equal(12, OrganizerHolidays.Between(["us-federal"], 2026, 2026).Count);
    }

    [Fact]
    public void UsFederal_ASaturdayNewYearIsObservedTheDayBefore_InTheYearBefore()
    {
        var names = OrganizerHolidays.Between(["us-federal"], 2027, 2027).Select(h => $"{h.Date:MM-dd} {h.Name}").ToList();
        Assert.Contains("12-24 Christmas Day (observed)", names);
        Assert.Contains("12-31 New Year's Day (observed)", names);
    }

    [Fact]
    public void UsObservances()
    {
        Assert.Equal("2026-05-10", On("us-observances", 2026, "Mother's Day"));
        Assert.Equal("2026-06-21", On("us-observances", 2026, "Father's Day"));
        Assert.Equal("2026-03-08", On("us-observances", 2026, "Daylight saving time begins"));
        Assert.Equal("2026-11-01", On("us-observances", 2026, "Daylight saving time ends"));
        Assert.Equal("2026-11-03", On("us-observances", 2026, "Election Day"));
        Assert.DoesNotContain(OrganizerHolidays.Between(["us-observances"], 2027, 2027), h => h.Name == "Election Day");
    }

    [Theory]
    [InlineData(2025, "2025-09-23", "2025-10-02", "2025-04-13", "2025-12-15", "2025-03-14", "2025-06-02")]
    [InlineData(2026, "2026-09-12", "2026-09-21", "2026-04-02", "2026-12-05", "2026-03-03", "2026-05-22")]
    // 5784 is a Hebrew leap year: Purim is in Adar II, and Passover a month later than it would be.
    [InlineData(2024, "2024-10-03", "2024-10-12", "2024-04-23", "2024-12-26", "2024-03-24", "2024-06-12")]
    public void Jewish(int year, string roshHashanah, string yomKippur, string passover, string hanukkah, string purim, string shavuot)
    {
        Assert.Equal(roshHashanah, On("jewish", year, "Rosh Hashanah"));
        Assert.Equal(yomKippur, On("jewish", year, "Yom Kippur"));
        Assert.Equal(passover, On("jewish", year, "Passover"));
        Assert.Equal(hanukkah, On("jewish", year, "Hanukkah"));
        Assert.Equal(purim, On("jewish", year, "Purim"));
        Assert.Equal(shavuot, On("jewish", year, "Shavuot"));
        var hanukkahDays = OrganizerHolidays.Between(["jewish"], year, year).Single(h => h.Name == "Hanukkah");
        Assert.Equal(8, hanukkahDays.Days);
        Assert.Equal("Begins at sundown the evening before.", hanukkahDays.Note);
    }

    [Fact]
    public void Jewish_TishaBAvOnShabbatMovesToSunday()
    {
        // 9 Av 5785 was Saturday 2 August 2025; the fast was kept on the Sunday.
        Assert.Equal("2025-08-03", On("jewish", 2025, "Tisha B'Av"));
        Assert.Equal("2026-07-23", On("jewish", 2026, "Tisha B'Av"));
    }

    [Theory]
    [InlineData(2025, "2025-03-01", "2025-03-30", "2025-06-06")]
    [InlineData(2026, "2026-02-18", "2026-03-20", "2026-05-27")]
    public void Islamic(int year, string ramadan, string eidAlFitr, string eidAlAdha)
    {
        Assert.Equal(ramadan, On("islamic", year, "Ramadan begins"));
        Assert.Equal(eidAlFitr, On("islamic", year, "Eid al-Fitr"));
        Assert.Equal(eidAlAdha, On("islamic", year, "Eid al-Adha"));
    }

    [Fact]
    public void Islamic_PastTheEndOfTheCalendarIsEmptyNotAnError() =>
        Assert.Empty(OrganizerHolidays.Between(["islamic"], 2090, 2090));

    [Theory]
    [InlineData(2025, "2025-01-29", "Snake", "2025-05-31", "2025-10-06")]
    [InlineData(2026, "2026-02-17", "Horse", "2026-06-19", "2026-09-25")]
    // 2023 has a leap second month, so everything after it is one index later in .NET's numbering.
    [InlineData(2023, "2023-01-22", "Rabbit", "2023-06-22", "2023-09-29")]
    public void Chinese(int year, string newYear, string animal, string dragonBoat, string midAutumn)
    {
        Assert.Equal(newYear, On("chinese", year, $"Lunar New Year ({animal})"));
        Assert.Equal(dragonBoat, On("chinese", year, "Dragon Boat Festival"));
        Assert.Equal(midAutumn, On("chinese", year, "Mid-Autumn Festival"));
    }

    [Fact]
    public void Hindu_FromTheTable_AndNothingOutsideIt()
    {
        Assert.Equal("2026-11-08", On("hindu", 2026, "Diwali"));
        Assert.Equal("2026-03-04", On("hindu", 2026, "Holi"));
        Assert.Empty(OrganizerHolidays.Between(["hindu"], 2031, 2031));
    }

    [Fact]
    public void IdsAreStableAndUnique()
    {
        var all = OrganizerHolidays.Between(OrganizerHolidays.Sets.Select(set => set.Id), 2024, 2028);
        Assert.Equal(all.Count, all.Select(h => h.Id).Distinct().Count());
        Assert.Contains(all, h => h.Id == "holiday:christian:20260405:easter-sunday");
    }

    [Fact]
    public async Task Endpoints_SetsHolidaysPreferencesAndFeed()
    {
        using var factory = new WebApplicationFactory<WebProgram>()
            .WithWebHostBuilder(builder => builder.UseTestSettings(_settingsPath, _dataDirectory));
        using var client = factory.CreateClient();

        var sets = await client.GetFromJsonAsync<JsonElement>("/api/organizer/holidays/sets", Ct);
        Assert.Contains("jewish", sets.GetProperty("sets").EnumerateArray().Select(set => set.GetProperty("id").GetString()));

        var holidays = await client.GetFromJsonAsync<JsonElement>("/api/organizer/holidays?sets=christian,jewish&from=2026&to=2026", Ct);
        var list = holidays.GetProperty("holidays").EnumerateArray().ToList();
        Assert.Contains(list, h => h.GetProperty("name").GetString() == "Easter Sunday" && h.GetProperty("date").GetString() == "2026-04-05");
        Assert.Contains(list, h => h.GetProperty("setId").GetString() == "jewish" && h.GetProperty("days").GetInt32() == 8);
        using (var tooMany = await client.GetAsync("/api/organizer/holidays?sets=christian&from=2000&to=2020", Ct))
            Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);

        // Never saved: null, so a device holding a choice from before a rebuild knows to send it.
        var before = await client.GetFromJsonAsync<JsonElement>("/api/organizer/preferences", Ct);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("holidaySets").ValueKind);
        using (var bad = await client.PutAsJsonAsync("/api/organizer/preferences", new { holidaySets = new[] { "martian" } }, Ct))
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using (var saved = await client.PutAsJsonAsync("/api/organizer/preferences", new { holidaySets = new[] { "us-federal", "jewish", "jewish" } }, Ct))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var after = await client.GetFromJsonAsync<JsonElement>("/api/organizer/preferences", Ct);
        Assert.Equal(["us-federal", "jewish"], after.GetProperty("holidaySets").EnumerateArray().Select(set => set.GetString()));

        // The subscription feed carries the chosen holidays.
        var path = (await client.GetFromJsonAsync<JsonElement>("/api/organizer/calendar-feed", Ct)).GetProperty("path").GetString()!;
        var ics = await client.GetStringAsync(path, Ct);
        Assert.Contains("SUMMARY:Thanksgiving Day", ics);
        Assert.Contains("SUMMARY:Yom Kippur", ics);
        Assert.DoesNotContain("SUMMARY:Easter Sunday", ics);
    }
}
