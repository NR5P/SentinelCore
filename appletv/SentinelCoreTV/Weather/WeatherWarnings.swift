import SwiftUI

// The app-wide weather warning banner - the Apple TV side of the wall kiosk's
// (core/SentinelCore.Web/wwwroot/kiosk-weather-alert.js), and of the iPhone app's
// (ios/SentinelCore/Weather/WeatherWarnings.swift, which this mirrors). A tornado warning, a severe
// thunderstorm watch or whatever else the server's weather-warning settings ask for gets a
// flashing banner across the top of the app, over every tab, just under the tab bar. It never
// takes focus by itself: it is one press down from the tab bar, and Select on it opens the radar
// full screen while Dismiss beside it just clears it. Either way it counts as seen and goes away,
// and the Weather tab keeps a "!" badge until the alert ends.
//
// "Seen" is remembered per device in UserDefaults. The NWS reissues a warning every so often as it
// tracks the storm, each reissue a new id that references the old one - a reissue of something
// already seen is seen too, so one storm is one banner. An upgrade (watch to warning) is a new
// alert and flashes again.

// MARK: - /api/weather/warnings

struct WeatherWarningsResponse: Decodable {
    let enabled: Bool?
    let alerts: [WeatherWarning]?
    // wakeMinutes and openRadar are for the wall tablet only.
}

struct WeatherWarning: Decodable, Identifiable, Equatable {
    let id: String
    let event: String
    // "warning", "watch" or "advisory".
    let level: String?
    let category: String?
    let severity: String?
    let headline: String?
    let instruction: String?
    let ends: Date?
    let references: [String]?
}

// MARK: - The model

@MainActor
final class WeatherWarningsModel: ObservableObject {
    // Every alert in force, seen or not (the Weather badge), and the ones the banner shows.
    @Published private(set) var active: [WeatherWarning] = []
    @Published private(set) var unseen: [WeatherWarning] = []

    static let pollInterval = Duration.seconds(60)
    private static let seenKey = "weatherWarningsSeen"
    // How long a seen alert with no end time is remembered.
    private static let seenFallback: TimeInterval = 24 * 3600

    private var alerts: [WeatherWarning] = []
    // id -> when it can be forgotten.
    private var seen: [String: Date]

    init() {
        let stored = UserDefaults.standard.dictionary(forKey: Self.seenKey) as? [String: Double] ?? [:]
        let now = Date()
        seen = stored.mapValues { Date(timeIntervalSince1970: $0) }.filter { $0.value > now }
        #if DEBUG
        // `-debugWeatherWarningsResetSeen YES`: forget what this device has seen, so a stub's alert
        // flashes again on every launch.
        if UserDefaults.standard.bool(forKey: "debugWeatherWarningsResetSeen") { seen = [:] }
        #endif
    }

    // Polls while the app is on screen; RootView runs it in a task keyed on the scene phase and
    // the server, so it stops in the background and starts over (with a poll straight away) on
    // coming back.
    func run(client: SentinelClient) async {
        while !Task.isCancelled {
            await poll(client: client)
            try? await Task.sleep(for: Self.pollInterval)
        }
    }

    func poll(client: SentinelClient) async {
        do {
            alerts = try await client.weatherWarnings().alerts ?? []
        } catch ClientError.server(status: 404, _) {
            alerts = []   // a server from before the banner: nothing to show
        } catch {
            return        // unreachable, signed out: keep what's showing
        }
        update()
    }

    // Tapped or dismissed: whatever the banner is showing now is seen.
    func markShownSeen() {
        for alert in unseen {
            seen[alert.id] = forgetAt(alert)
            for ref in alert.references ?? [] { seen[ref] = forgetAt(alert) }
        }
        saveSeen()
        update()
    }

    private func update() {
        let now = Date()
        // The server drops ended alerts, but a poll can be a minute old.
        let current = alerts.filter { ($0.ends ?? .distantFuture) > now }
        let prunable = seen.contains { $0.value <= now }
        if prunable { seen = seen.filter { $0.value > now } }
        var learned = false
        let fresh = current.filter { alert in
            if seen[alert.id] != nil { return false }
            if (alert.references ?? []).contains(where: { seen[$0] != nil }) {
                // A reissue of something already seen: remember it in its own right, so the next
                // reissue (which references this one) is seen too after the original is forgotten.
                seen[alert.id] = forgetAt(alert)
                learned = true
                return false
            }
            return true
        }
        if learned || prunable { saveSeen() }
        if current != active { active = current }
        if fresh != unseen { unseen = fresh }
    }

    private func forgetAt(_ alert: WeatherWarning) -> Date {
        alert.ends ?? Date().addingTimeInterval(Self.seenFallback)
    }

    private func saveSeen() {
        UserDefaults.standard.set(seen.mapValues(\.timeIntervalSince1970), forKey: Self.seenKey)
    }

    // MARK: Opening the radar

    // The banner switches to the Weather tab and asks it for the radar full screen. The map may
    // not be ready yet (the forecast loads first), so the request waits here until it is and the
    // tab takes it.
    private(set) static var radarRequested = false

    static func requestRadar() {
        radarRequested = true
        NotificationCenter.default.post(name: .sentinelWeatherRadarRequested, object: nil)
    }

    static func takeRadarRequest() -> Bool {
        defer { radarRequested = false }
        return radarRequested
    }
}

extension Notification.Name {
    static let sentinelWeatherRadarRequested = Notification.Name("sentinelWeatherRadarRequested")
}

extension WeatherWarning {
    // "until 6:30 PM" today, "until Tue 6:30 PM" otherwise; empty with no end time.
    var untilText: String {
        guard let ends else { return "" }
        let time = ends.formatted(date: .omitted, time: .shortened)
        if Calendar.current.isDateInToday(ends) { return "until \(time)" }
        return "until \(ends.formatted(.dateTime.weekday(.abbreviated))) \(time)"
    }

    // The banner's colours: bright (the border and the flash's peak) and dark (its trough).
    var colors: (bright: Color, dark: Color) {
        switch level {
        case "watch": (Color(red: 1, green: 0x8a / 255, blue: 0x2a / 255), Color(red: 0x6e / 255, green: 0x33 / 255, blue: 0))
        case "advisory": (Color(red: 1, green: 0xb5 / 255, blue: 0x45 / 255), Color(red: 0x5c / 255, green: 0x41 / 255, blue: 0))
        default: (Color(red: 1, green: 0x4d / 255, blue: 0x5e / 255), Color(red: 0x7a / 255, green: 0x0f / 255, blue: 0x1a / 255))
        }
    }
}

// MARK: - The banner

// Centred under the tab bar, narrow and short so the screen under it still shows. Flashes between
// the dark and the bright colour until it is dealt with (steady with Reduce Motion). Two buttons in
// the app-wide FocusContrastButtonStyle: the labels are never forced white - the banner is drawn in
// the dark appearance instead, so they are light until focus fills the button, then dark.
struct WeatherWarningBanner: View {
    @ObservedObject var warnings: WeatherWarningsModel
    let openRadar: () -> Void

    // Clear of the tab bar at the top of the screen.
    static let topInset: CGFloat = 110

    var body: some View {
        if let top = warnings.unseen.first {
            let colors = top.colors
            HStack(spacing: 20) {
                Button {
                    warnings.markShownSeen()
                    openRadar()
                } label: {
                    HStack(spacing: 22) {
                        Image(systemName: "exclamationmark.triangle")
                            .font(.system(size: 44, weight: .semibold))
                        VStack(alignment: .leading, spacing: 4) {
                            WeatherWarningTitle(
                                event: top.event,
                                until: top.untilText,
                                more: warnings.unseen.count > 1 ? "+\(warnings.unseen.count - 1) more" : "",
                                eventFont: .title3.weight(.bold), detailFont: .headline, tracking: 1, spacing: 14)
                            if let headline = top.headline, !headline.isEmpty {
                                Text(headline)
                                    .font(.callout)
                                    .opacity(0.92)
                                    .lineLimit(1)
                            }
                        }
                        Spacer(minLength: 0)
                        Text("RADAR")
                            .font(.caption.weight(.semibold))
                            .tracking(1.5)
                    }
                    .padding(.vertical, 4)
                }
                .accessibilityLabel("\(top.event) \(top.untilText). \(top.headline ?? "")")
                .accessibilityHint("Opens the weather radar")

                Button(action: warnings.markShownSeen) {
                    Label("Dismiss", systemImage: "xmark")
                }
            }
            .padding(16)
            .background { WeatherWarningFlash(bright: colors.bright, dark: colors.dark) }
            .clipShape(RoundedRectangle(cornerRadius: 24))
            .overlay(RoundedRectangle(cornerRadius: 24).stroke(colors.bright, lineWidth: 4))
            .shadow(color: .black.opacity(0.6), radius: 30, y: 10)
            .shadow(color: colors.bright.opacity(0.6), radius: 24)
            .environment(\.colorScheme, .dark)
            // A focused button fills white, not the app's mint, which would read as "all clear".
            .tint(.white)
            .frame(maxWidth: 1480)
            .padding(.horizontal, 80)
            .transition(.move(edge: .top).combined(with: .opacity))
        }
    }
}

// The banner's first line: the event, "until ...", "+N more". When it doesn't all fit, "until ..."
// goes before anything is cut (never "u…"), and only then is the event's name truncated.
private struct WeatherWarningTitle: View {
    let event: String
    let until: String
    let more: String
    let eventFont: Font
    let detailFont: Font
    let tracking: CGFloat
    let spacing: CGFloat

    var body: some View {
        ViewThatFits(in: .horizontal) {
            line(until: until).fixedSize()
            line(until: "").fixedSize()
            line(until: "")
        }
    }

    private func line(until: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: spacing) {
            Text(event.uppercased())
                .font(eventFont)
                .tracking(tracking)
                .lineLimit(1)
            if !until.isEmpty { Text(until).font(detailFont).fixedSize() }
            if !more.isEmpty { Text(more).font(detailFont).opacity(0.85).fixedSize() }
        }
    }
}

// The flashing fill, on its own so only it redraws every frame.
private struct WeatherWarningFlash: View {
    let bright: Color
    let dark: Color
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        Rectangle().fill(dark).overlay {
            if !reduceMotion {
                // The bright colour fading in and out over the dark one: dark -> bright -> dark.
                Rectangle().fill(bright)
                    .phaseAnimator([0.0, 1.0]) { layer, lit in
                        layer.opacity(lit)
                    } animation: { _ in .easeInOut(duration: 1.1) }
            }
        }
    }
}
