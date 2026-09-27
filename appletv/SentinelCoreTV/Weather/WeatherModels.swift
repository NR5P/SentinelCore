import Foundation

// /api/weather, as core/SentinelCore.Web/WeatherService.cs sends it (camelCase, always 200 with a
// `state`). Everything past `state` is optional so a report that only explains itself - no
// location set, outside the NWS's coverage, weather.gov unreachable - still decodes.
struct WeatherReport: Decodable {
    let state: String
    let message: String?
    let location: WeatherLocation?
    let current: WeatherCurrent?
    let daily: [WeatherPeriod]?
    let hourly: [WeatherHour]?
    let alerts: [WeatherAlert]?
    let sun: WeatherSun?
    let radar: WeatherRadar?
    let forecastUpdatedAt: Date?
    let fetchedAt: Date?
    let stale: Bool?
    let problems: [String]?

    // Times read in the house's own zone, like the dashboard, so the forecast is right even when
    // the TV's clock is set somewhere else.
    var timeZone: TimeZone {
        location?.timeZone.flatMap(TimeZone.init(identifier:)) ?? .current
    }
}

struct WeatherLocation: Decodable {
    let latitude: Double
    let longitude: Double
    let label: String
    let city: String?
    let state: String?
    let timeZone: String?
    let radarStation: String?
    let forecastOffice: String?

    var displayName: String {
        let nearest = [city, state].compactMap { $0 }.joined(separator: ", ")
        if !label.isEmpty { return label }
        return nearest.isEmpty ? "This location" : nearest
    }
}

struct WeatherCurrent: Decodable {
    let observedAt: Date?
    let station: String
    let stationName: String
    let description: String
    let icon: String?
    let isDaytime: Bool?
    let temperatureF: Int
    let feelsLikeF: Int?
    let dewpointF: Int?
    let humidityPercent: Int?
    let windMph: Int?
    let windGustMph: Int?
    let windDirection: String?
    let pressureInHg: Double?
    let visibilityMiles: Double?
}

struct WeatherPeriod: Decodable, Identifiable {
    let name: String
    let startTime: Date
    let endTime: Date
    let isDaytime: Bool
    let temperatureF: Int?
    let precipitationChance: Int?
    let windSpeed: String?
    let windDirection: String?
    let icon: String?
    let shortForecast: String
    let detailedForecast: String

    var id: Date { startTime }
}

struct WeatherHour: Decodable, Identifiable {
    let startTime: Date
    let isDaytime: Bool
    let temperatureF: Int?
    let precipitationChance: Int?
    let humidityPercent: Int?
    let windSpeed: String?
    let windDirection: String?
    let icon: String?
    let shortForecast: String

    var id: Date { startTime }
}

struct WeatherAlert: Decodable, Identifiable {
    let id: String
    let event: String
    let severity: String
    let urgency: String
    let headline: String
    let description: String
    let instruction: String
    let ends: Date?
    let areaDescription: String
    let sender: String

    var isSevere: Bool { severity == "Extreme" || severity == "Severe" }
}

struct WeatherSun: Decodable {
    let sunrise: Date?
    let sunset: Date?
}

struct WeatherRadar: Decodable {
    let station: String
}

// The NWS forecast is a list of half-days (Today, Tonight, Saturday, Saturday Night, ...); the
// cards show one per calendar day, day period leading and the night giving the low - the same
// grouping as the dashboard's weather.js.
struct WeatherDay: Identifiable {
    let key: String
    var day: WeatherPeriod?
    var night: WeatherPeriod?

    var id: String { key }
    var lead: WeatherPeriod { (day ?? night)! }
    var chance: Int { max(day?.precipitationChance ?? 0, night?.precipitationChance ?? 0) }

    static func group(_ periods: [WeatherPeriod], in zone: TimeZone) -> [WeatherDay] {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = zone
        var days: [WeatherDay] = []
        for period in periods {
            let parts = calendar.dateComponents([.year, .month, .day], from: period.startTime)
            let key = "\(parts.year ?? 0)-\(parts.month ?? 0)-\(parts.day ?? 0)"
            var index = days.firstIndex { $0.key == key }
            if index == nil {
                days.append(WeatherDay(key: key))
                index = days.count - 1
            }
            if period.isDaytime { days[index!].day = period } else { days[index!].night = period }
        }
        return days
    }
}

// NWS icon codes (api.weather.gov/icons) to an SF Symbol, [day, night]. The server sends the code
// rather than the NWS picture; the dashboard maps the same codes to emoji.
enum WeatherSymbol {
    private static let symbols: [String: (String, String)] = [
        "skc": ("sun.max.fill", "moon.stars.fill"),
        "few": ("sun.max.fill", "moon.fill"),
        "sct": ("cloud.sun.fill", "cloud.moon.fill"),
        "bkn": ("cloud.sun.fill", "cloud.moon.fill"),
        "ovc": ("cloud.fill", "cloud.fill"),
        "wind_skc": ("wind", "wind"),
        "wind_few": ("wind", "wind"),
        "wind_sct": ("wind", "wind"),
        "wind_bkn": ("wind", "wind"),
        "wind_ovc": ("wind", "wind"),
        "snow": ("cloud.snow.fill", "cloud.snow.fill"),
        "rain_snow": ("cloud.sleet.fill", "cloud.sleet.fill"),
        "rain_sleet": ("cloud.sleet.fill", "cloud.sleet.fill"),
        "snow_sleet": ("cloud.sleet.fill", "cloud.sleet.fill"),
        "fzra": ("cloud.hail.fill", "cloud.hail.fill"),
        "rain_fzra": ("cloud.hail.fill", "cloud.hail.fill"),
        "snow_fzra": ("cloud.hail.fill", "cloud.hail.fill"),
        "sleet": ("cloud.sleet.fill", "cloud.sleet.fill"),
        "rain": ("cloud.rain.fill", "cloud.rain.fill"),
        "rain_showers": ("cloud.sun.rain.fill", "cloud.moon.rain.fill"),
        "rain_showers_hi": ("cloud.sun.rain.fill", "cloud.moon.rain.fill"),
        "tsra": ("cloud.bolt.rain.fill", "cloud.bolt.rain.fill"),
        "tsra_sct": ("cloud.sun.bolt.fill", "cloud.moon.bolt.fill"),
        "tsra_hi": ("cloud.sun.bolt.fill", "cloud.moon.bolt.fill"),
        "tornado": ("tornado", "tornado"),
        "hurricane": ("hurricane", "hurricane"),
        "tropical_storm": ("tropicalstorm", "tropicalstorm"),
        "dust": ("sun.dust.fill", "sun.dust.fill"),
        "smoke": ("smoke.fill", "smoke.fill"),
        "haze": ("sun.haze.fill", "moon.haze.fill"),
        "fog": ("cloud.fog.fill", "cloud.fog.fill"),
        "hot": ("thermometer.sun.fill", "thermometer.sun.fill"),
        "cold": ("thermometer.snowflake", "thermometer.snowflake"),
        "blizzard": ("wind.snow", "wind.snow"),
    ]

    static func name(_ code: String?, isDaytime: Bool?) -> String {
        guard let code, let pair = symbols[code] else { return "thermometer.medium" }
        return isDaytime == false ? pair.1 : pair.0
    }
}
