namespace SentinelCore;

// Where the server is, for working out when the sun rises and sets there. The web host's Weather
// tab reads the same pair to fetch a forecast - see LocationOptions for where it does and doesn't go.
public sealed record GeoCoordinate(double Latitude, double Longitude)
{
    public bool IsValid =>
        Latitude is >= -90 and <= 90 &&
        Longitude is >= -180 and <= 180 &&
        // (0, 0) is in the Gulf of Guinea and is what an unset pair of coordinates looks like.
        // Treating it as "not configured" costs someone on that exact spot nothing they can't
        // work around, and saves everyone else from silently getting equatorial sunrise times.
        (Latitude != 0 || Longitude != 0);
}

// Sunrise and sunset from a date and a position, with no network and no dependency - the
// standard low-precision sunrise equation (the same one NOAA's public calculator publishes),
// which is good to a minute or two. That is far more precision than a light switch needs, and
// being pure arithmetic it can be tested against published times for real cities.
//
// Everything here is UTC. Turning "sunset on the 4th" into a local wall-clock instant is
// ScheduleTiming's job, not this class's.
public static class SolarCalculator
{
    // 2000-01-01 12:00 TT, the epoch every term below is measured from.
    private const double J2000 = 2451545.0;

    // Julian date of the Unix epoch, for the conversion back to a DateTimeOffset.
    private const double UnixEpochJulianDate = 2440587.5;

    // The sun's centre sits this far below the horizon at the moment we call sunrise: half the
    // solar disc (0.267°) plus average atmospheric refraction (0.566°). Using 0 instead would
    // report sunrise several minutes late everywhere.
    public const double SunriseElevationDegrees = -0.833;

    // How far below the horizon the sun has to be for a given amount of darkness. These are the
    // standard twilight definitions, and they are what makes "at this darkness, every night"
    // expressible at all: the clock time they correspond to moves through the year by well over
    // an hour, which is the whole point of asking for a darkness level instead of a time.
    public const double CivilTwilightDegrees = -6;
    public const double NauticalTwilightDegrees = -12;
    public const double AstronomicalTwilightDegrees = -18;

    // Below this the sun is nowhere near the horizon and the equation stops describing anything
    // useful; above it we would be asking when the sun is above its own noon altitude.
    public const double MinimumElevationDegrees = -18;
    public const double MaximumElevationDegrees = 10;

    // Obliquity of the ecliptic - the tilt that causes seasons, and with it the whole annual
    // swing in day length this class exists to track.
    private const double EarthObliquityDegrees = 23.4397;

    public static DateTimeOffset? Sunrise(DateOnly date, GeoCoordinate at) =>
        Calculate(date, at, evening: false, SunriseElevationDegrees);

    public static DateTimeOffset? Sunset(DateOnly date, GeoCoordinate at) =>
        Calculate(date, at, evening: true, SunriseElevationDegrees);

    // The evening moment the sun drops to `elevationDegrees` - dusk at whatever darkness was
    // asked for. Its morning mirror is Dawn.
    public static DateTimeOffset? Dusk(DateOnly date, GeoCoordinate at, double elevationDegrees) =>
        Calculate(date, at, evening: true, elevationDegrees);

    public static DateTimeOffset? Dawn(DateOnly date, GeoCoordinate at, double elevationDegrees) =>
        Calculate(date, at, evening: false, elevationDegrees);

    // `evening` picks which of the day's two crossings of `elevationDegrees` is wanted: the sun
    // passes the same altitude once on the way up and once on the way down.
    public static DateTimeOffset? Calculate(DateOnly date, GeoCoordinate at, bool evening, double elevationDegrees)
    {
        if (!at.IsValid) return null;
        if (elevationDegrees is < MinimumElevationDegrees or > MaximumElevationDegrees) return null;

        // Days from J2000 to this date's solar noon. Integer because the whole point of the next
        // few lines is to find the solar noon *nearest this day* and work outwards from it.
        var n = date.DayNumber - new DateOnly(2000, 1, 1).DayNumber;

        // Mean solar noon at this longitude. East of Greenwich the sun peaks earlier, hence the
        // subtraction; 0.0009 is the small leap-second/epoch correction the method carries.
        var meanSolarNoon = J2000 + n + 0.0009 - at.Longitude / 360.0;

        // The sun's mean anomaly: where Earth is in its orbit, ignoring the orbit's eccentricity.
        var meanAnomaly = Normalize(357.5291 + 0.98560028 * (meanSolarNoon - J2000));

        // Equation of the centre - the correction for that eccentricity, i.e. the difference
        // between where the sun would be on a circular orbit and where it actually is.
        var equationOfCentre =
            1.9148 * Sin(meanAnomaly) +
            0.0200 * Sin(2 * meanAnomaly) +
            0.0003 * Sin(3 * meanAnomaly);

        // Ecliptic longitude: the sun's position along the ecliptic, which fixes its declination.
        var eclipticLongitude = Normalize(meanAnomaly + equationOfCentre + 180 + 102.9372);

        // True solar noon, corrected for the two effects that make sundials disagree with clocks.
        var solarTransit =
            meanSolarNoon +
            0.0053 * Sin(meanAnomaly) -
            0.0069 * Sin(2 * eclipticLongitude);

        // How far north or south of the equator the sun is overhead today.
        var declinationSin = Sin(eclipticLongitude) * Sin(EarthObliquityDegrees);
        var declinationCos = Math.Cos(Math.Asin(declinationSin));

        // The hour angle: how far the Earth must turn between solar noon and the horizon
        // crossing. Half the length of the day, expressed as an angle.
        var hourAngleCos =
            (Sin(elevationDegrees) - Sin(at.Latitude) * declinationSin) /
            (Cos(at.Latitude) * declinationCos);

        // Out of range means the sun never reaches that altitude on this date at this latitude -
        // polar night or midnight sun for a sunrise, and in midsummer at higher latitudes also
        // "it never actually gets that dark". There is no time to return, and inventing one
        // would fire a schedule at an arbitrary moment for months on end.
        if (double.IsNaN(hourAngleCos) || hourAngleCos is < -1 or > 1) return null;

        var hourAngle = RadiansToDegrees(Math.Acos(hourAngleCos));
        var julianDate = evening
            ? solarTransit + hourAngle / 360.0
            : solarTransit - hourAngle / 360.0;

        return FromJulianDate(julianDate);
    }

    private static DateTimeOffset FromJulianDate(double julianDate)
    {
        var unixSeconds = (julianDate - UnixEpochJulianDate) * 86400.0;

        // Whole seconds: the method's own error is measured in minutes, so sub-second precision
        // here would be false confidence, and a round number reads better in a log line.
        return DateTimeOffset.FromUnixTimeSeconds((long)Math.Round(unixSeconds));
    }

    private static double Sin(double degrees) => Math.Sin(DegreesToRadians(degrees));

    private static double Cos(double degrees) => Math.Cos(DegreesToRadians(degrees));

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static double RadiansToDegrees(double radians) => radians * 180.0 / Math.PI;

    // Math.IEEERemainder can return a negative result, and every angle here has to stay in
    // [0, 360) for the trigonometry above to mean what it says.
    private static double Normalize(double degrees)
    {
        var wrapped = degrees % 360.0;
        return wrapped < 0 ? wrapped + 360.0 : wrapped;
    }
}
