import Foundation

// Field clocks: "<13-digit unix ms>-<4-digit counter>-<device id>", compared as plain strings.
// Hybrid logical: never runs backwards and never falls behind a clock this device has seen, so an
// edit made here always sorts after the version of the field it replaced - even if this device's
// wall clock is a little behind the one that wrote that version.
public struct HybridClock: Sendable {
    public let deviceId: String
    private(set) var lastMillis: Int64 = 0
    private(set) var lastCounter: Int = 0

    public init(deviceId: String) {
        // The server accepts [A-Za-z0-9_.:-]; anything else in a device name is dropped.
        let allowed = deviceId.unicodeScalars.filter { CharacterSet.alphanumerics.contains($0) || "_.:-".unicodeScalars.contains($0) }
        let cleaned = String(String.UnicodeScalarView(allowed)).prefix(64)
        self.deviceId = cleaned.isEmpty ? "apple" : String(cleaned)
    }

    public mutating func next(now: Date = Date()) -> String {
        let millis = Int64((now.timeIntervalSince1970 * 1000).rounded(.down))
        if millis > lastMillis {
            lastMillis = millis
            lastCounter = 0
        } else {
            lastCounter = (lastCounter + 1) % 10_000
        }
        return Self.format(millis: lastMillis, counter: lastCounter, device: deviceId)
    }

    public mutating func observe(_ clock: String) {
        guard let (millis, counter) = Self.parse(clock) else { return }
        if millis > lastMillis || (millis == lastMillis && counter > lastCounter) {
            lastMillis = millis
            lastCounter = counter
        }
    }

    public static func format(millis: Int64, counter: Int, device: String) -> String {
        String(format: "%013lld-%04d-", millis, counter) + device
    }

    static func parse(_ clock: String) -> (Int64, Int)? {
        let parts = clock.split(separator: "-", maxSplits: 2, omittingEmptySubsequences: false)
        guard parts.count == 3, parts[0].count == 13, parts[1].count == 4,
              let millis = Int64(parts[0]), let counter = Int(parts[1]) else { return nil }
        return (millis, counter)
    }
}
