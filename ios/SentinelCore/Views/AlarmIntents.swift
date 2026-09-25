import AppIntents
import Foundation

// The alarm from Siri, Shortcuts and the Action button: "Arm Sentinel Core away". Arming only -
// disarming needs the PIN, and there is no safe way to say a PIN out loud to a phone on the
// kitchen counter, so that stays a tap in the app. Every phrase names the app, as Apple requires.

enum AlarmArmMode: String, AppEnum {
    case home, night, away

    static let typeDisplayRepresentation = TypeDisplayRepresentation(name: "Alarm Mode")
    static let caseDisplayRepresentations: [AlarmArmMode: DisplayRepresentation] = [
        .home: "Home",
        .night: "Night",
        .away: "Away",
    ]
}

struct ArmAlarmIntent: AppIntent {
    static let title: LocalizedStringResource = "Arm the Alarm"
    static let description = IntentDescription(
        "Arms the Sentinel Core home alarm: Home or Night watch the doors and windows, Away watches everything after the exit delay.")
    static let openAppWhenRun = false

    @Parameter(title: "Mode", requestValueDialog: "Home, night or away?")
    var mode: AlarmArmMode

    static var parameterSummary: some ParameterSummary {
        Summary("Arm the alarm \(\.$mode)")
    }

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog {
        let model = try await VoiceAlarm.ready()
        do {
            let reply = try await model.client.armAlarm(mode: mode.rawValue, force: false, pin: nil)
            let said = mode == .away
                ? "Arming away. You have a moment to leave."
                : "Armed \(mode.rawValue)."
            return .result(dialog: "\(reply.note.map { said + " " + $0 } ?? said)")
        } catch let ClientError.server(status, message) {
            switch status {
            case 403: throw VoiceAlarmError.needsPin
            case 409 where message?.contains("open") == true: throw VoiceAlarmError.somethingOpen(message ?? "Something is open.")
            default: throw VoiceAlarmError.refused(message ?? "The alarm couldn't be armed.")
            }
        }
    }
}

struct AlarmStatusIntent: AppIntent {
    static let title: LocalizedStringResource = "Alarm Status"
    static let description = IntentDescription("Says whether the Sentinel Core home alarm is armed, and what's open.")
    static let openAppWhenRun = false

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog {
        let model = try await VoiceAlarm.ready()
        let status = try await model.client.alarmStatus()
        guard status.enabled else { throw VoiceAlarmError.turnedOff }
        let sensors = status.sensors ?? []
        let open = sensors.filter { $0.enabled && $0.isOpen }
        let openText = open.isEmpty ? "" : " Open: \(open.map(\.name).joined(separator: ", "))."
        let said: String
        switch status.status {
        case "triggered": said = "The alarm is going off. \(status.triggeredBy ?? "")"
        case "arming": said = "The alarm is arming away."
        case "pending": said = "An entry door is open and the alarm is about to go off."
        case "armed": said = "The alarm is armed \(AlarmMode.label(for: status.mode ?? "").lowercased()).\(openText)"
        default: said = "The alarm is disarmed.\(openText)"
        }
        return .result(dialog: "\(said)")
    }
}

enum VoiceAlarmError: Error, CustomLocalizedStringResourceConvertible {
    case notSetUp
    case offline
    case turnedOff
    case needsPin
    case somethingOpen(String)
    case refused(String)

    var localizedStringResource: LocalizedStringResource {
        switch self {
        case .notSetUp: "Sentinel Core isn't connected to a server yet. Open the app to set it up."
        case .offline: "Sentinel Core can't reach your server right now."
        case .turnedOff: "The alarm is turned off. It's in the dashboard's Settings, under Alarm."
        case .needsPin: "The alarm needs its PIN to arm. Open Sentinel Core to enter it."
        case .somethingOpen(let message): "\(message) Close it, or arm from the app to skip it."
        case .refused(let message): "\(message)"
        }
    }
}

@MainActor
enum VoiceAlarm {
    // The model, connected. Unlike the Organizer nothing can wait on the device: an arm that
    // doesn't reach the server didn't happen, so being offline is a refusal.
    static func ready() async throws -> AppModel {
        let model = AppModel.shared
        guard model.isConfigured else { throw VoiceAlarmError.notSetUp }
        if model.connectionState != .online {
            _ = await VoiceOrganizer.finishes(within: VoiceOrganizer.connectWait) {
                await model.connect()
                var polls = 0
                while model.connectionState == .checking, polls < 50 {
                    try? await Task.sleep(for: .milliseconds(200))
                    polls += 1
                }
            }
        }
        guard model.connectionState == .online else { throw VoiceAlarmError.offline }
        guard model.alarmEnabled else { throw VoiceAlarmError.turnedOff }
        return model
    }
}
