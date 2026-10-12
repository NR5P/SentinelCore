using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// WALL KIOSK WEATHER WARNINGS: which NWS alerts the kiosk banner gets.
public sealed class KioskWeatherAlertTests
{
    private static WeatherAlert Alert(string id, string eventName, string severity = "Moderate", string messageType = "Alert") => new()
    {
        Id = id,
        Event = eventName,
        Severity = severity,
        MessageType = messageType,
        Headline = eventName + " headline",
        Ends = DateTimeOffset.UtcNow.AddHours(1)
    };

    [Fact]
    public void Defaults_ShowEverything_WarningsFirst()
    {
        var alerts = new[]
        {
            Alert("a", "Wind Advisory", "Minor"),
            Alert("b", "Tornado Watch", "Severe"),
            Alert("c", "Dense Fog Advisory", "Minor"),
            Alert("d", "Severe Thunderstorm Warning", "Severe")
        };

        var response = KioskWeatherAlerts.Response(new KioskWeatherSettingsDto(), alerts);

        Assert.True(response.Enabled);
        Assert.Equal(["d", "b", "a", "c"], response.Alerts.Select(a => a.Id));
        Assert.Equal("warning", response.Alerts[0].Level);
        Assert.Equal("severeThunderstorm", response.Alerts[0].Category);
        Assert.Equal("watch", response.Alerts[1].Level);
        Assert.Equal("advisory", response.Alerts[2].Level);
    }

    [Fact]
    public void Settings_FilterByLevelAndKind()
    {
        var options = new KioskWeatherSettingsDto { MinimumLevel = WeatherAlertOptions.LevelWatch, Wind = false };
        var alerts = new[]
        {
            Alert("a", "Wind Advisory"),
            Alert("b", "High Wind Warning"),
            Alert("c", "Flood Watch"),
            Alert("d", "Special Weather Statement")
        };

        var response = KioskWeatherAlerts.Response(options, alerts);

        Assert.Equal(["c"], response.Alerts.Select(a => a.Id));
    }

    [Fact]
    public void Cancellations_AndDisabled_ShowNothing()
    {
        var alerts = new[] { Alert("a", "Tornado Warning", "Extreme", "Cancel") };
        Assert.Empty(KioskWeatherAlerts.Response(new KioskWeatherSettingsDto(), alerts).Alerts);

        var off = KioskWeatherAlerts.Response(new KioskWeatherSettingsDto { Enabled = false }, [Alert("b", "Tornado Warning")]);
        Assert.False(off.Enabled);
        Assert.Empty(off.Alerts);
    }
}
