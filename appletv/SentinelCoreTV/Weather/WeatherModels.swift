import Foundation
import SwiftUI

// The Weather tab's data, as core/SentinelCore.Web sends it (WeatherService.cs and
// WeatherMapService.cs, camelCase JSON). Kept in step with the iPhone/Mac app's
// ios/SentinelCore/Weather/WeatherModels.swift - the same file but for the hex colour helper at
// the bottom. Everything the server can leave out is optional, so a report that only explains
// itself - no location set, outside the NWS's coverage, weather.gov unreachable - still decodes.

// MARK: - /api/weather

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

    // Times read in the house's own zone, like the dashboard, so the forecast for home reads
    // right even from a hotel two zones away.
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

    var nearest: String { [city, state].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: ", ") }

    var displayName: String {
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

    var color: Color {
        isSevere ? SentinelTheme.red : severity == "Moderate" ? SentinelTheme.amber : SentinelTheme.cyan
    }
}

struct WeatherSun: Decodable {
    let sunrise: Date?
    let sunset: Date?
}

struct WeatherRadar: Decodable {
    let station: String
}

// MARK: - /api/weather/details

struct WeatherDetails: Decodable {
    let state: String
    let totals: WeatherPrecipTotals?
    let hours: [WeatherHourDetail]?
    let pressure: WeatherPressureTrend?
    let discussion: WeatherDiscussion?
    let severe: WeatherSevereOutlook?
    let fetchedAt: Date?
    let problems: [String]?
}

struct WeatherPrecipTotals: Decodable {
    let rainNext24HoursInches: Double
    let rainNext7DaysInches: Double
    let snowNext24HoursInches: Double
    let snowNext7DaysInches: Double
    let iceNext7DaysInches: Double
    let thunderChanceNext24Hours: Int
    let thunderChanceNext7Days: Int
}

struct WeatherHourDetail: Decodable {
    let startTime: Date
    let feelsLikeF: Int?
    let gustMph: Int?
    let skyCoverPercent: Int?
    let rainInches: Double?
    let snowInches: Double?
}

struct WeatherPressureTrend: Decodable {
    let changeInHgPer3Hours: Double
    let trend: String

    var arrow: String {
        switch trend {
        case "rising fast": "⇈"
        case "rising": "↑"
        case "falling": "↓"
        case "falling fast": "⇊"
        default: "→"
        }
    }
}

struct WeatherDiscussion: Decodable {
    let office: String
    let issuedAt: Date?
    let text: String
}

struct WeatherSevereOutlook: Decodable {
    let days: [WeatherOutlookDay]
    let tornadoPercent: Int?
    let hailPercent: Int?
    let windPercent: Int?
    let significantTornado: Bool
    let significantHail: Bool
    let significantWind: Bool
    let issuedAt: Date?
}

struct WeatherOutlookDay: Decodable, Identifiable {
    let day: Int
    let category: String?
    let name: String
    let rank: Int
    let color: String?

    var id: Int { day }

    // The SPC's 1-to-5 scale, as the dashboard words it.
    var scaleText: String {
        switch category {
        case "TSTM": "thunderstorms possible"
        case "MRGL": "1 of 5 · marginal"
        case "SLGT": "2 of 5 · slight"
        case "ENH": "3 of 5 · enhanced"
        case "MDT": "4 of 5 · moderate"
        case "HIGH": "5 of 5 · high"
        default: "no severe storms expected"
        }
    }

    // The SPC's own category colours, the same ones the dashboard's risk chips use.
    var chipColor: Color {
        switch category {
        case "TSTM": Color(weatherHex: "#C1E9C1")
        case "MRGL": Color(weatherHex: "#66A366")
        case "SLGT": Color(weatherHex: "#FFE066")
        case "ENH": Color(weatherHex: "#FFA366")
        case "MDT": Color(weatherHex: "#E06666")
        case "HIGH": Color(weatherHex: "#EE99EE")
        default: .secondary
        }
    }
}

// MARK: - /api/weather/map

struct WeatherMapInfo: Decodable {
    let home: WeatherMapHome?
    let homeStation: String?
    let station: String?
    let stations: [WeatherRadarSite]
    let layers: [WeatherMapLayer]
    let maximumRadarZoom: Int
    let maximumBaseZoom: Int?
    let problems: [String]?
}

struct WeatherMapHome: Decodable {
    let latitude: Double
    let longitude: Double
    let label: String
}

struct WeatherRadarSite: Decodable, Identifiable, Hashable {
    let id: String
    let name: String
    let latitude: Double
    let longitude: Double
}

struct WeatherMapLayer: Decodable, Identifiable, Equatable {
    let id: String
    let name: String
    let scope: String
    let description: String
    let times: [String]

    var isStation: Bool { scope == "station" }
}

// MARK: - /api/weather/storms

struct WeatherStorms: Decodable {
    let cells: [WeatherStormCell]
    let warnings: [WeatherWarningArea]
    let outlook: WeatherOutlookMap?
    let fetchedAt: Date?
    let problems: [String]?
}

struct WeatherStormCell: Decodable, Identifiable, Equatable {
    let id: String
    let radar: String
    let stormId: String
    let latitude: Double
    let longitude: Double
    // 0 ordinary, 1 strong, 2 severe hail, 3 rotating, 4 tornado vortex signature.
    let threat: Int
    let tornadic: Bool
    let rotation: Bool
    let mesoStrength: String?
    let hailProbability: Int?
    let severeHailProbability: Int?
    let maxHailInches: Double?
    let vil: Int?
    let maxDbz: Int?
    let topKft: Double?
    let headingDegrees: Int?
    let headingCompass: String?
    let speedMph: Int?
    let track: [WeatherTrackPoint]
    let distanceMiles: Double?
    let bearingCompass: String?
    let towardHome: Bool
    let closestMiles: Double?
    let etaMinutes: Int?
    let validAt: Date?

    var title: String {
        if tornadic { return "Possible tornado" }
        if rotation { return "Rotating storm" }
        if threat == 2 { return "Severe hail storm" }
        if threat == 1 { return "Strong storm" }
        return "Storm"
    }

    var hazards: String {
        var parts: [String] = []
        if tornadic { parts.append("tornado vortex signature") }
        if rotation { parts.append("mesocyclone (rank \(mesoStrength ?? "?"))") }
        if let hail = maxHailInches, hail > 0 { parts.append("hail to \(String(format: "%.2f", hail))\"") }
        else if let chance = hailProbability, chance > 0 { parts.append("\(chance)% hail") }
        return parts.joined(separator: " · ")
    }

    var motion: String {
        if let speed = speedMph, speed > 0 { return "moving \(headingCompass ?? "") \(speed) mph" }
        return "nearly stationary"
    }

    // The marker letter the dashboard draws: T tornado, M mesocyclone, H hail.
    var marker: String {
        tornadic ? "T" : rotation ? "M" : threat == 2 ? "H" : ""
    }

    var threatColor: Color {
        switch threat {
        case 4: Color(weatherHex: "#FF00E5")
        case 3: Color(weatherHex: "#FF4D5E")
        case 2: Color(weatherHex: "#FF9800")
        case 1: Color(weatherHex: "#FFD54F")
        default: Color(weatherHex: "#CFD8DC")
        }
    }
}

struct WeatherTrackPoint: Decodable, Equatable {
    let minutes: Int
    let latitude: Double
    let longitude: Double
}

struct WeatherWarningArea: Decodable, Identifiable, Equatable {
    let id: String
    let phenomena: String
    let significance: String
    let name: String
    let office: String?
    let issued: Date?
    let expires: Date?
    let hailInches: Double?
    let windMph: Double?
    let tornadoTag: String?
    let damageTag: String?
    let emergency: Bool
    let particularlyDangerous: Bool
    let containsHome: Bool
    // Polygons, each a list of rings of [lon, lat] - GeoJSON order.
    let polygons: [[[[Double]]]]

    var tags: String {
        var parts: [String] = []
        if let tag = tornadoTag, !tag.isEmpty { parts.append("tornado \(tag.lowercased())") }
        if let hail = hailInches, hail > 0 { parts.append("hail \(hail.formatted())\"") }
        if let wind = windMph, wind > 0 { parts.append("wind \(Int(wind)) mph") }
        if let damage = damageTag, !damage.isEmpty { parts.append("\(damage.lowercased()) damage threat") }
        return parts.joined(separator: " · ")
    }

    // Weakest first so a tornado warning draws on top of the severe thunderstorm warning it sits
    // inside.
    var drawOrder: Int {
        ["TO": 9, "EW": 8, "SV": 7, "SQ": 6, "FF": 5, "MA": 4][phenomena] ?? 0
    }

    // The warning types that matter enough to put a banner over the page.
    var isDangerous: Bool { ["TO", "SV", "FF", "EW", "SQ", "MA"].contains(phenomena) }

    var hexColor: String {
        [
            "TO": "#FF2D3D", "SV": "#FFC400", "FF": "#00E676", "FA": "#2E9D4F", "FL": "#2E9D4F",
            "MA": "#FF8C00", "EW": "#FF3DF5", "SQ": "#D63384", "DS": "#D9B38C", "SS": "#B388FF",
        ][phenomena] ?? "#4BE7FF"
    }
}

struct WeatherOutlookMap: Decodable {
    let areas: [WeatherOutlookArea]
    let issuedAt: Date?
}

struct WeatherOutlookArea: Decodable {
    let label: String
    let name: String
    let rank: Int
    let fill: String?
    let stroke: String?
    let polygons: [[[[Double]]]]
}

// MARK: - Grouping and sky arithmetic

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

enum WeatherText {
    // Rain in inches as the dashboard writes it: 0", trace, 0.25".
    static func inches(_ value: Double?, digits: Int = 2) -> String {
        guard let value else { return "—" }
        if value == 0 { return "0\"" }
        if value < 0.01 { return "trace" }
        return String(format: "%.\(digits)f\"", value)
    }

    // How the dew point feels, the number meteorologists actually use for "muggy".
    static func dewComfort(_ dewpoint: Int) -> String {
        if dewpoint >= 70 { return " (oppressive)" }
        if dewpoint >= 65 { return " (muggy)" }
        if dewpoint >= 60 { return " (sticky)" }
        if dewpoint <= 30 { return " (dry)" }
        return ""
    }

    static func daylight(_ sun: WeatherSun?) -> String? {
        guard let rise = sun?.sunrise, let set = sun?.sunset else { return nil }
        let minutes = Int((set.timeIntervalSince(rise) / 60).rounded())
        guard minutes > 0 else { return nil }
        return "\(minutes / 60)h \(minutes % 60)m"
    }

    // A one-line "what's next" from the hourly forecast: when rain (or snow, or storms) starts or
    // stops in the next twelve hours.
    static func nextPrecipitation(_ hourly: [WeatherHour], zone: TimeZone) -> String? {
        let hours = Array(hourly.prefix(12))
        guard let first = hours.first else { return nil }
        let hour = Date.FormatStyle(timeZone: zone).hour()
        func wet(_ h: WeatherHour) -> Bool { (h.precipitationChance ?? 0) >= 50 }
        func kind(_ h: WeatherHour) -> String {
            let text = h.shortForecast.lowercased()
            if text.contains("thunder") { return "Storms" }
            if text.contains("snow") || text.contains("flurr") { return "Snow" }
            if text.contains("sleet") || text.contains("freezing") { return "Wintry mix" }
            if text.contains("drizzle") { return "Drizzle" }
            return "Rain"
        }
        if wet(first) {
            if let stop = hours.first(where: { !wet($0) }) {
                return "\(kind(first)) likely until around \(stop.startTime.formatted(hour))."
            }
            return "\(kind(first)) likely for the next 12 hours."
        }
        if let start = hours.first(where: wet) {
            return "\(kind(start)) likely from around \(start.startTime.formatted(hour)) (\(start.precipitationChance ?? 0)%)."
        }
        let peak = hours.max { ($0.precipitationChance ?? 0) < ($1.precipitationChance ?? 0) } ?? first
        if (peak.precipitationChance ?? 0) >= 20 {
            return "Slight chance of \(kind(peak).lowercased()) around \(peak.startTime.formatted(hour)) (\(peak.precipitationChance ?? 0)%)."
        }
        return "No rain expected in the next 12 hours."
    }
}

// The moon's phase from its age since a known new moon (6 Jan 2000 18:14 UTC). Good to within a
// few hours, which is all a phase name needs.
struct MoonPhase {
    let name: String
    let symbol: String
    let illumination: Int
    let daysToFull: Int

    init(date: Date) {
        let synodic = 29.530588853
        let knownNew = Date(timeIntervalSince1970: 947_182_440) // 2000-01-06 18:14 UTC
        let days = date.timeIntervalSince(knownNew) / 86_400
        let age = (days.truncatingRemainder(dividingBy: synodic) + synodic).truncatingRemainder(dividingBy: synodic)
        illumination = Int(((1 - cos(2 * .pi * age / synodic)) / 2 * 100).rounded())
        let phases: [(Double, String, String)] = [
            (1.84566, "New moon", "moonphase.new.moon"),
            (5.53699, "Waxing crescent", "moonphase.waxing.crescent"),
            (9.22831, "First quarter", "moonphase.first.quarter"),
            (12.91963, "Waxing gibbous", "moonphase.waxing.gibbous"),
            (16.61096, "Full moon", "moonphase.full.moon"),
            (20.30228, "Waning gibbous", "moonphase.waning.gibbous"),
            (23.99361, "Last quarter", "moonphase.last.quarter"),
            (27.68493, "Waning crescent", "moonphase.waning.crescent"),
            (synodic + 1, "New moon", "moonphase.new.moon"),
        ]
        let phase = phases.first { age < $0.0 } ?? phases[0]
        name = phase.1
        symbol = phase.2
        daysToFull = Int((((14.765 - age).truncatingRemainder(dividingBy: synodic) + synodic).truncatingRemainder(dividingBy: synodic)).rounded())
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

extension Color {
    // The server's warning and outlook colours ("#RRGGBB"); anything unreadable is grey rather
    // than missing.
    init(weatherHex hex: String) {
        var text = hex.trimmingCharacters(in: .whitespaces)
        if text.hasPrefix("#") { text.removeFirst() }
        guard text.count == 6, let value = UInt32(text, radix: 16) else {
            self = .gray
            return
        }
        self.init(red: Double((value >> 16) & 0xFF) / 255, green: Double((value >> 8) & 0xFF) / 255, blue: Double(value & 0xFF) / 255)
    }
}
