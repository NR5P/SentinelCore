import Foundation

// What the Alarm widget shows: the last state the app saw. Written by the app whenever it reads
// /api/alarm/status (on launch, while the Alarm section is open, and when an alarm push arrives),
// read by the widget extension. Lives here, beside the Organizer's snapshot, because this package
// is the one both the app and the widget extension already import.
public struct AlarmWidgetSnapshot: Codable, Equatable, Sendable {
    public var enabled: Bool
    // disarmed | home | night | away
    public var mode: String
    // disarmed | arming | armed | pending | triggered
    public var status: String
    public var exitUntil: Date?
    public var pendingUntil: Date?
    public var triggeredBy: String?
    public var sensorCount: Int
    public var openCount: Int
    public var problemCount: Int
    public var updatedAt: Date

    public init(enabled: Bool, mode: String, status: String, exitUntil: Date?, pendingUntil: Date?, triggeredBy: String?,
                sensorCount: Int, openCount: Int, problemCount: Int, updatedAt: Date) {
        self.enabled = enabled
        self.mode = mode
        self.status = status
        self.exitUntil = exitUntil
        self.pendingUntil = pendingUntil
        self.triggeredBy = triggeredBy
        self.sensorCount = sensorCount
        self.openCount = openCount
        self.problemCount = problemCount
        self.updatedAt = updatedAt
    }

    public static let empty = AlarmWidgetSnapshot(
        enabled: false, mode: "disarmed", status: "disarmed", exitUntil: nil, pendingUntil: nil, triggeredBy: nil,
        sensorCount: 0, openCount: 0, problemCount: 0, updatedAt: .distantPast)

    public static let sample = AlarmWidgetSnapshot(
        enabled: true, mode: "away", status: "armed", exitUntil: nil, pendingUntil: nil, triggeredBy: nil,
        sensorCount: 6, openCount: 0, problemCount: 0, updatedAt: Date())

    public var isArmed: Bool { status != "disarmed" }

    public var modeLabel: String {
        switch mode {
        case "home": "Home"
        case "night": "Night"
        case "away": "Away"
        default: "Disarmed"
        }
    }

    public var headline: String {
        switch status {
        case "arming": "Arming"
        case "pending": "Entry delay"
        case "triggered": "Alarm!"
        case "armed": "Armed \(modeLabel.lowercased())"
        default: "Disarmed"
        }
    }
}

public struct AlarmWidgetStore: Sendable {
    private let url: URL

    public init?() {
        guard let container = FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: OrganizerWidgetStore.appGroup) else { return nil }
        url = container.appendingPathComponent("alarm-widget.json")
    }

    public func read() -> AlarmWidgetSnapshot {
        guard let data = try? Data(contentsOf: url) else { return .empty }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return (try? decoder.decode(AlarmWidgetSnapshot.self, from: data)) ?? .empty
    }

    public func write(_ snapshot: AlarmWidgetSnapshot) {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        guard let data = try? encoder.encode(snapshot) else { return }
        try? data.write(to: url, options: .atomic)
    }
}
