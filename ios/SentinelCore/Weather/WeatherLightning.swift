import Foundation

// Lightning on the radar map: the flashes NOAA's GOES-19 satellite saw over the loop window, from
// the server's /api/weather/lightning, and the arithmetic the map and its status line need - what
// is visible at the moment the loop is showing, how each flash is drawn by age, and the nearest
// one to home. The dashboard's weather.js does the same ("---- Lightning ----"). Plain Foundation
// so it can be compiled and exercised on its own.

// One answer from the server. `flashes` rows are [latitude, longitude, seconds after `origin`,
// energy in femtojoules], oldest first. With `epoch` and `after` from the previous answer the
// server sends only the files that arrived since (`incremental`).
struct WeatherLightningAnswer: Decodable {
    let state: String
    let epoch: String
    let sequence: Int
    let incremental: Bool
    let origin: Double
    let flashes: [[Double]]
}

struct LightningStrike: Equatable, Hashable {
    let latitude: Double
    let longitude: Double
    let time: Date
    let energy: Double
}

enum LightningLook: Equatable {
    case bolt
    case dot(hex: String, alpha: Double, radius: Double)
}

struct LightningStore: Equatable {
    // How far back the map shows flashes from the moment on screen, and which ones are new
    // enough to be bolts.
    static let trail: TimeInterval = 15 * 60
    static let boltAge: TimeInterval = 2 * 60
    // A little past the server's 90-minute window, so the oldest frame still has its trail.
    static let keep: TimeInterval = 95 * 60
    // Within ten miles is close enough to be struck - "when thunder roars, go indoors".
    static let closeMiles = 10.0
    static let nearMiles = 25.0

    private(set) var strikes: [LightningStrike] = []
    private(set) var epoch: String?
    private(set) var sequence: Int?
    private(set) var state: String?

    var isReady: Bool { state == "ready" }

    // Takes in an answer: appended when it only carries what's new, otherwise in place of what
    // was held. Anything past the window is dropped either way.
    mutating func merge(_ answer: WeatherLightningAnswer, now: Date) {
        let fresh = answer.flashes.compactMap { row -> LightningStrike? in
            guard row.count >= 4 else { return nil }
            return LightningStrike(latitude: row[0], longitude: row[1],
                                   time: Date(timeIntervalSince1970: (answer.origin + row[2] * 1000) / 1000),
                                   energy: row[3])
        }
        let cutoff = now.addingTimeInterval(-Self.keep)
        strikes = ((answer.incremental ? strikes : []) + fresh).filter { $0.time >= cutoff }.sorted { $0.time < $1.time }
        epoch = answer.epoch
        sequence = answer.sequence
        state = answer.state
    }

    // The flashes to draw for `moment`: the trail before it, oldest first.
    func visible(at moment: Date) -> [LightningStrike] {
        strikes.filter { $0.time <= moment && moment.timeIntervalSince($0.time) <= Self.trail }
    }

    static func look(_ strike: LightningStrike, at moment: Date) -> LightningLook {
        let age = moment.timeIntervalSince(strike.time)
        if age < boltAge { return .bolt }
        let hex = age < 5 * 60 ? "#FFEB3B" : age < 10 * 60 ? "#FF9800" : "#F44336"
        let radius = 2 + min(2.5, max(0, log10(max(strike.energy, 1)) - 1.5) * 0.9)
        return .dot(hex: hex, alpha: 1 - 0.55 * (age / trail), radius: radius)
    }

    // The flash nearest home inside the trail, and how far it is.
    func nearest(to home: (latitude: Double, longitude: Double), at moment: Date) -> (strike: LightningStrike, miles: Double)? {
        visible(at: moment)
            .map { ($0, Self.miles(from: home, to: ($0.latitude, $0.longitude))) }
            .min { $0.1 < $1.1 }
    }

    // ⚡ 1,260 flashes in view, last 15 min · nearest home 6.2 mi NE, 3 min ago
    func status(home: (latitude: Double, longitude: Double)?, at moment: Date,
                inView: (LightningStrike) -> Bool) -> (text: String, nearestMiles: Double?) {
        let recent = visible(at: moment)
        var parts: [String] = []
        if !isReady && strikes.isEmpty {
            parts.append("loading lightning…")
        } else {
            let count = recent.filter(inView).count
            parts.append("\(count.formatted()) flash\(count == 1 ? "" : "es") in view, last 15 min")
        }
        var nearestMiles: Double?
        if let home {
            if let nearest = nearest(to: home, at: moment), nearest.miles <= 100 {
                nearestMiles = nearest.miles
                parts.append("nearest home \(Self.distanceText(from: home, to: nearest.strike)), \(Self.ageText(moment.timeIntervalSince(nearest.strike.time))) ago")
            } else if isReady {
                parts.append("none within 100 mi of home")
            }
        }
        if !isReady && !strikes.isEmpty { parts.append("still filling in") }
        return ("⚡ " + parts.joined(separator: " · "), nearestMiles)
    }

    // MARK: - Distances

    static func miles(from a: (latitude: Double, longitude: Double), to b: (latitude: Double, longitude: Double)) -> Double {
        let radians = Double.pi / 180
        let dLat = (b.latitude - a.latitude) * radians
        let dLon = (b.longitude - a.longitude) * radians
        let h = sin(dLat / 2) * sin(dLat / 2) + cos(a.latitude * radians) * cos(b.latitude * radians) * sin(dLon / 2) * sin(dLon / 2)
        return 2 * 3958.8 * asin(min(1, sqrt(h)))
    }

    static func bearing(from a: (latitude: Double, longitude: Double), to b: (latitude: Double, longitude: Double)) -> Double {
        let radians = Double.pi / 180
        let y = sin((b.longitude - a.longitude) * radians) * cos(b.latitude * radians)
        let x = cos(a.latitude * radians) * sin(b.latitude * radians)
            - sin(a.latitude * radians) * cos(b.latitude * radians) * cos((b.longitude - a.longitude) * radians)
        return atan2(y, x) / radians
    }

    static func compass(_ degrees: Double) -> String {
        let points = ["N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"]
        let normalized = (degrees.truncatingRemainder(dividingBy: 360) + 360).truncatingRemainder(dividingBy: 360)
        return points[Int((normalized / 22.5).rounded()) % 16]
    }

    // "6.2 mi NE" under ten miles, "34 mi NW" past it.
    static func distanceText(from home: (latitude: Double, longitude: Double), to strike: LightningStrike) -> String {
        let point = (strike.latitude, strike.longitude)
        let miles = miles(from: home, to: point)
        let amount = miles < 10 ? String(format: "%.1f", miles) : "\(Int(miles.rounded()))"
        return "\(amount) mi \(compass(bearing(from: home, to: point)))"
    }

    static func ageText(_ seconds: TimeInterval) -> String {
        let minutes = Int((seconds / 60).rounded())
        return minutes < 1 ? "under a minute" : "\(minutes) min"
    }
}
