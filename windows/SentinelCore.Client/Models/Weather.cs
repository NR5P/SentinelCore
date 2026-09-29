namespace SentinelCore.Client.Models;

// The Weather section, as core/SentinelCore.Web serves it (WeatherService.cs and
// WeatherMapService.cs; camelCase JSON). /api/weather always answers 200 with a `state`: "no
// location set", "outside the NWS's coverage" and "couldn't reach weather.gov" are states the
// section explains, not errors - so everything past State and Message is optional.

public static class WeatherStates
{
    public const string Ok = "ok";
    public const string Unconfigured = "unconfigured";
    public const string Unsupported = "unsupported";
    public const string Unreachable = "unreachable";
}

public sealed class WeatherReport
{
    public string State { get; init; } = WeatherStates.Ok;
    public string? Message { get; init; }
    public WeatherLocation? Location { get; init; }
    public WeatherCurrent? Current { get; init; }
    public List<WeatherPeriod> Daily { get; init; } = new();
    public List<WeatherHour> Hourly { get; init; } = new();
    public List<WeatherAlert> Alerts { get; init; } = new();
    public WeatherSun? Sun { get; init; }
    public WeatherRadar? Radar { get; init; }
    public DateTimeOffset? ForecastUpdatedAt { get; init; }
    public DateTimeOffset? FetchedAt { get; init; }
    public bool Stale { get; init; }
    public List<string> Problems { get; init; } = new();

    public bool IsOk => State == WeatherStates.Ok;
}

public sealed class WeatherLocation
{
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public string Label { get; init; } = "";
    public string? City { get; init; }
    public string? State { get; init; }
    public string? TimeZone { get; init; }
    public string? RadarStation { get; init; }
    public string? ForecastOffice { get; init; }

    /// <summary>"City, ST" from the NWS, or empty.</summary>
    public string Nearest => string.Join(", ", new[] { City, State }.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>The name the owner gave the place, else the nearest town, else "this location".</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Label) ? Label : Nearest.Length > 0 ? Nearest : "this location";
}

public sealed class WeatherCurrent
{
    public DateTimeOffset? ObservedAt { get; init; }
    public string Station { get; init; } = "";
    public string StationName { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Icon { get; init; }
    public bool? IsDaytime { get; init; }
    public int TemperatureF { get; init; }
    public int? FeelsLikeF { get; init; }
    public int? DewpointF { get; init; }
    public int? HumidityPercent { get; init; }
    public int? WindMph { get; init; }
    public int? WindGustMph { get; init; }
    public string? WindDirection { get; init; }
    public double? PressureInHg { get; init; }
    public double? VisibilityMiles { get; init; }
}

/// <summary>One half-day of the NWS forecast: "Today", "Tonight", "Saturday", "Saturday Night"...</summary>
public sealed class WeatherPeriod
{
    public string Name { get; init; } = "";
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; init; }
    public bool IsDaytime { get; init; }
    public int? TemperatureF { get; init; }
    public int? PrecipitationChance { get; init; }
    public string? WindSpeed { get; init; }
    public string? WindDirection { get; init; }
    public string? Icon { get; init; }
    public string ShortForecast { get; init; } = "";
    public string DetailedForecast { get; init; } = "";
}

public sealed class WeatherHour
{
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; init; }
    public bool IsDaytime { get; init; }
    public int? TemperatureF { get; init; }
    public int? PrecipitationChance { get; init; }
    public int? HumidityPercent { get; init; }
    public string? WindSpeed { get; init; }
    public string? WindDirection { get; init; }
    public string? Icon { get; init; }
    public string ShortForecast { get; init; } = "";
}

public sealed class WeatherAlert
{
    public string Id { get; init; } = "";
    public string Event { get; init; } = "";
    public string Severity { get; init; } = "Unknown";
    public string Urgency { get; init; } = "Unknown";
    public string Certainty { get; init; } = "Unknown";
    public string Headline { get; init; } = "";
    public string Description { get; init; } = "";
    public string Instruction { get; init; } = "";
    public DateTimeOffset? Onset { get; init; }
    public DateTimeOffset? Ends { get; init; }
    public DateTimeOffset? Expires { get; init; }
    public string AreaDescription { get; init; } = "";
    public string Sender { get; init; } = "";

    /// <summary>"severe" (Extreme or Severe), "moderate" or "minor" - how loudly to show it.</summary>
    public string Level => Severity switch
    {
        "Extreme" or "Severe" => "severe",
        "Moderate" => "moderate",
        _ => "minor",
    };

    public bool IsSevere => Level == "severe";
}

public sealed class WeatherSun
{
    public DateTimeOffset? Sunrise { get; init; }
    public DateTimeOffset? Sunset { get; init; }
}

public sealed class WeatherRadar
{
    public string Station { get; init; } = "";
}

// ---- /api/weather/details ----

public sealed class WeatherDetails
{
    public string State { get; init; } = WeatherStates.Ok;
    public WeatherPrecipTotals? Totals { get; init; }
    public List<WeatherHourDetail> Hours { get; init; } = new();
    public WeatherPressureTrend? Pressure { get; init; }
    public WeatherDiscussion? Discussion { get; init; }
    public WeatherSevereOutlook? Severe { get; init; }
    public DateTimeOffset? FetchedAt { get; init; }
    public List<string> Problems { get; init; } = new();
}

public sealed class WeatherPrecipTotals
{
    public double RainNext24HoursInches { get; init; }
    public double RainNext7DaysInches { get; init; }
    public double SnowNext24HoursInches { get; init; }
    public double SnowNext7DaysInches { get; init; }
    public double IceNext7DaysInches { get; init; }
    public int ThunderChanceNext24Hours { get; init; }
    public int ThunderChanceNext7Days { get; init; }
}

public sealed class WeatherHourDetail
{
    public DateTimeOffset StartTime { get; init; }
    public int? FeelsLikeF { get; init; }
    public int? GustMph { get; init; }
    public int? SkyCoverPercent { get; init; }
    public double RainInches { get; init; }
    public double SnowInches { get; init; }
}

public sealed class WeatherPressureTrend
{
    public double ChangeInHgPer3Hours { get; init; }

    /// <summary>"rising fast", "rising", "steady", "falling" or "falling fast".</summary>
    public string Trend { get; init; } = "steady";
}

public sealed class WeatherDiscussion
{
    public string Office { get; init; } = "";
    public DateTimeOffset? IssuedAt { get; init; }
    public string Text { get; init; } = "";
}

public sealed class WeatherSevereOutlook
{
    public List<WeatherOutlookDay> Days { get; init; } = new();
    public int? TornadoPercent { get; init; }
    public int? HailPercent { get; init; }
    public int? WindPercent { get; init; }
    public bool SignificantTornado { get; init; }
    public bool SignificantHail { get; init; }
    public bool SignificantWind { get; init; }
    public DateTimeOffset? IssuedAt { get; init; }
}

public sealed class WeatherOutlookDay
{
    public int Day { get; init; }

    /// <summary>TSTM, MRGL, SLGT, ENH, MDT, HIGH - or null for nothing expected.</summary>
    public string? Category { get; init; }
    public string Name { get; init; } = "";
    public int Rank { get; init; }
    public string? Color { get; init; }
}

// ---- /api/weather/map and /api/weather/storms ----

public sealed class WeatherMapInfo
{
    public WeatherMapHome? Home { get; init; }
    public string? HomeStation { get; init; }
    public string? Station { get; init; }
    public List<WeatherRadarSite> Stations { get; init; } = new();
    public List<WeatherMapLayer> Layers { get; init; } = new();
    public int MaximumRadarZoom { get; init; }
    public int MaximumBaseZoom { get; init; }
    public List<string> Problems { get; init; } = new();
}

public sealed class WeatherMapHome
{
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public string Label { get; init; } = "";
}

public sealed class WeatherRadarSite
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public double Latitude { get; init; }
    public double Longitude { get; init; }
}

public sealed class WeatherMapLayer
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>"national" or "station".</summary>
    public string Scope { get; init; } = "national";
    public string Description { get; init; } = "";
    public List<string> Times { get; init; } = new();
}

public sealed class WeatherStorms
{
    public List<WeatherStormCell> Cells { get; init; } = new();
    public List<WeatherWarningArea> Warnings { get; init; } = new();
    public WeatherOutlookMap? Outlook { get; init; }
    public DateTimeOffset? FetchedAt { get; init; }
    public List<string> Problems { get; init; } = new();
}

public sealed class WeatherStormCell
{
    public string Id { get; init; } = "";
    public string Radar { get; init; } = "";
    public string StormId { get; init; } = "";
    public double Latitude { get; init; }
    public double Longitude { get; init; }

    /// <summary>0 ordinary, 1 strong, 2 severe hail, 3 rotating, 4 tornado vortex signature.</summary>
    public int Threat { get; init; }
    public bool Tornadic { get; init; }
    public bool Rotation { get; init; }
    public string? MesoStrength { get; init; }
    public int? HailProbability { get; init; }
    public int? SevereHailProbability { get; init; }
    public double? MaxHailInches { get; init; }
    public int? Vil { get; init; }
    public int? MaxDbz { get; init; }
    public double? TopKft { get; init; }
    public int? HeadingDegrees { get; init; }
    public string? HeadingCompass { get; init; }
    public int? SpeedMph { get; init; }
    public List<WeatherTrackPoint> Track { get; init; } = new();
    public double? DistanceMiles { get; init; }
    public string? BearingCompass { get; init; }
    public bool TowardHome { get; init; }
    public double? ClosestMiles { get; init; }
    public int? EtaMinutes { get; init; }
    public DateTimeOffset? ValidAt { get; init; }
}

public sealed class WeatherTrackPoint
{
    public int Minutes { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
}

public sealed class WeatherWarningArea
{
    public string Id { get; init; } = "";

    /// <summary>The VTEC phenomenon: TO tornado, SV severe thunderstorm, FF flash flood, ...</summary>
    public string Phenomena { get; init; } = "";
    public string Significance { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Office { get; init; }
    public DateTimeOffset? Issued { get; init; }
    public DateTimeOffset? Expires { get; init; }
    public double? HailInches { get; init; }
    public double? WindMph { get; init; }
    public string? TornadoTag { get; init; }
    public string? DamageTag { get; init; }
    public bool Emergency { get; init; }
    public bool ParticularlyDangerous { get; init; }
    public bool ContainsHome { get; init; }

    /// <summary>Polygons, each a list of rings of [lon, lat] - GeoJSON order. Drawn by the map page.</summary>
    public List<List<List<double[]>>> Polygons { get; init; } = new();
}

public sealed class WeatherOutlookMap
{
    public List<WeatherOutlookArea> Areas { get; init; } = new();
    public DateTimeOffset? IssuedAt { get; init; }
}

public sealed class WeatherOutlookArea
{
    public string Label { get; init; } = "";
    public string Name { get; init; } = "";
    public int Rank { get; init; }
    public string? Fill { get; init; }
    public string? Stroke { get; init; }
    public List<List<List<double[]>>> Polygons { get; init; } = new();
}
