import ImageIO
import SwiftUI
import UIKit

// The dashboard's Weather tab on the television: alerts, conditions now, the radar loop, the next
// hours and the week, all from the server's /api/weather (the server talks to the National
// Weather Service and caches it - nothing here reaches weather.gov directly). Read-only: the
// location is set on the dashboard or in the phone and computer apps' Settings.
//
// Fetches only while the tab is on screen, like the dashboard: the forecast every ten minutes,
// the radar picture every two.
struct WeatherTVView: View {
    @EnvironmentObject private var model: AppModel

    let isActive: Bool

    @State private var report: WeatherReport?
    @State private var error: String?
    @State private var missingOnServer = false
    @State private var radarScope = "local"
    @State private var radarKind = "loop"
    @State private var radar: UIImage?
    @State private var radarError: String?
    @State private var shownAlert: WeatherAlert?
    @State private var shownDay: WeatherDay?
    @State private var radarFullScreen = false

    private static let refreshInterval = Duration.seconds(10 * 60)
    private static let radarRefreshInterval = Duration.seconds(2 * 60)

    private var zone: TimeZone { report?.timeZone ?? .current }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 44) {
                content
            }
            .padding(.horizontal, 60)
            .padding(.bottom, 60)
        }
        // Keyed on the server too: at launch the tab can appear before the app has its address.
        .task(id: TabKey(active: isActive, server: model.activeServerURL)) {
            guard isActive, !model.activeServerURL.isEmpty else { return }
            await load()
            while !Task.isCancelled {
                try? await Task.sleep(for: Self.refreshInterval)
                if Task.isCancelled { return }
                await load()
            }
        }
        .task(id: RadarRequest(active: isActive && report?.state == "ok", scope: effectiveScope, kind: radarKind)) {
            guard isActive, report?.state == "ok" else { return }
            while !Task.isCancelled {
                await loadRadar()
                try? await Task.sleep(for: Self.radarRefreshInterval)
            }
        }
        .sheet(item: $shownAlert) { alert in
            WeatherAlertSheet(alert: alert, zone: zone)
        }
        .sheet(item: $shownDay) { day in
            WeatherDaySheet(day: day)
        }
        .fullScreenCover(isPresented: $radarFullScreen) {
            ZStack {
                Color.black.ignoresSafeArea()
                if let radar { AnimatedImageView(image: radar).padding(40) }
            }
            // Something focusable so Menu on the remote dismisses the cover.
            .focusable()
        }
    }

    // No local radar station for this point (rare - some territories) means the national map.
    private var effectiveScope: String {
        report?.radar == nil ? "national" : radarScope
    }

    @ViewBuilder private var content: some View {
        if missingOnServer {
            ContentUnavailableView(
                "Weather Needs a Newer Server",
                systemImage: "cloud.sun",
                description: Text("Update Sentinel Core on the server to see the forecast here.")
            )
            .frame(maxWidth: .infinity)
        } else if let report {
            switch report.state {
            case "unconfigured":
                ContentUnavailableView {
                    Label("No Location Set", systemImage: "location.slash")
                } description: {
                    Text("Set the house's location under Settings → Location on the dashboard, iPhone, Mac or Android app. The forecast comes from the US National Weather Service.")
                }
                .frame(maxWidth: .infinity)
            case "unsupported":
                header(report)
                ContentUnavailableView("No Forecast for This Location", systemImage: "globe.americas",
                                       description: Text(report.message ?? "The National Weather Service only covers the United States and its territories."))
                    .frame(maxWidth: .infinity)
            case "unreachable":
                ContentUnavailableView {
                    Label("Weather.gov Unreachable", systemImage: "icloud.slash")
                } description: {
                    Text(report.message ?? "The server couldn't reach the National Weather Service.")
                } actions: {
                    Button("Try Again") { Task { await load() } }
                }
                .frame(maxWidth: .infinity)
            default:
                forecast(report)
            }
        } else if let error {
            ContentUnavailableView {
                Label("Couldn't Load the Weather", systemImage: "exclamationmark.triangle")
            } description: {
                Text(error)
            } actions: {
                Button("Try Again") { Task { await load() } }
            }
            .frame(maxWidth: .infinity)
        } else {
            ProgressView().frame(maxWidth: .infinity)
        }
    }

    @ViewBuilder private func forecast(_ report: WeatherReport) -> some View {
        header(report)
        if let problems = report.problems, !problems.isEmpty {
            VStack(alignment: .leading, spacing: 6) {
                ForEach(problems, id: \.self) { Label($0, systemImage: "exclamationmark.triangle") }
            }
            .font(.callout)
            .foregroundStyle(SentinelTheme.amber)
        }
        if let alerts = report.alerts, !alerts.isEmpty {
            VStack(alignment: .leading, spacing: 20) {
                ForEach(alerts) { alert in
                    Button { shownAlert = alert } label: { AlertRow(alert: alert, zone: zone) }
                        .buttonStyle(.card)
                }
            }
        }
        HStack(alignment: .top, spacing: 40) {
            NowCard(current: report.current, sun: report.sun, zone: zone)
                .frame(width: 720)
            radarCard(report)
        }
        if let hours = report.hourly, !hours.isEmpty {
            section("Next \(hours.count) Hours") {
                ScrollView(.horizontal) {
                    LazyHStack(spacing: 24) {
                        ForEach(Array(hours.enumerated()), id: \.element.id) { index, hour in
                            let newDay = index == 0 || !sameDay(hours[index - 1].startTime, hour.startTime)
                            // Focusable so the remote can scroll the strip; there is nothing more to open.
                            Button {} label: { HourCell(hour: hour, dayLabel: newDay ? weekday(hour.startTime) : nil, zone: zone) }
                                .buttonStyle(.card)
                        }
                    }
                    .padding(.vertical, 20)
                }
                .scrollClipDisabled()
            }
        }
        let days = WeatherDay.group(report.daily ?? [], in: zone)
        if !days.isEmpty {
            section("\(days.count)-Day Forecast") {
                // Scrolls: the NWS's 14 half-days often span eight calendar days, wider than the screen.
                ScrollView(.horizontal) {
                    LazyHStack(alignment: .top, spacing: 24) {
                        ForEach(days) { day in
                            Button { shownDay = day } label: { DayCard(day: day) }
                                .buttonStyle(.card)
                        }
                    }
                    .padding(.vertical, 20)
                }
                .scrollClipDisabled()
            }
        }
        Text(footer(report))
            .font(.caption)
            .foregroundStyle(.secondary)
            .focusable()
    }

    private func header(_ report: WeatherReport) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(report.location?.displayName ?? "Weather")
                .font(.title2.bold())
            if let fetched = report.fetchedAt {
                Text("Updated \(fetched.formatted(timeStyle))" + (report.stale == true ? " · showing the last answer weather.gov gave" : ""))
                    .font(.callout)
                    .foregroundStyle(report.stale == true ? SentinelTheme.amber : .secondary)
            }
        }
    }

    private func radarCard(_ report: WeatherReport) -> some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                Text(report.radar.map { "Radar · \($0.station)" } ?? "Radar")
                    .font(.headline)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .fixedSize()
                Spacer()
                Picker("Area", selection: $radarScope) {
                    Text("Local").tag("local")
                    Text("National").tag("national")
                }
                .pickerStyle(.segmented)
                .frame(width: 320)
                .disabled(report.radar == nil)
                Picker("Motion", selection: $radarKind) {
                    Text("Loop").tag("loop")
                    Text("Still").tag("still")
                }
                .pickerStyle(.segmented)
                .frame(width: 260)
            }
            Button { if radar != nil { radarFullScreen = true } } label: {
                ZStack {
                    Color.black
                    if let radar {
                        AnimatedImageView(image: radar)
                    } else if let radarError {
                        Text(radarError).font(.callout).foregroundStyle(.secondary).padding()
                    } else {
                        ProgressView()
                    }
                }
                .frame(height: 520)
            }
            .buttonStyle(.card)
        }
        .frame(maxWidth: .infinity)
    }

    private func section<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(title.uppercased())
                .font(.headline)
                .foregroundStyle(.secondary)
            content()
        }
    }

    private var timeStyle: Date.FormatStyle {
        Date.FormatStyle(date: .omitted, time: .shortened, timeZone: zone)
    }

    private func weekday(_ date: Date) -> String {
        date.formatted(Date.FormatStyle(timeZone: zone).weekday(.abbreviated))
    }

    private func sameDay(_ a: Date, _ b: Date) -> Bool {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = zone
        return calendar.isDate(a, inSameDayAs: b)
    }

    private func footer(_ report: WeatherReport) -> String {
        var text = "Forecast, observations and radar from the US National Weather Service (weather.gov)"
        if let issued = report.forecastUpdatedAt {
            text += ", forecast issued \(issued.formatted(Date.FormatStyle(timeZone: zone).weekday(.abbreviated).hour().minute()))"
        }
        text += "."
        if let name = report.location?.timeZone {
            text += " Times are shown in \(name.replacingOccurrences(of: "_", with: " "))."
        }
        return text
    }

    private func load() async {
        do {
            report = try await model.client.weather()
            error = nil
            missingOnServer = false
        } catch {
            if error is CancellationError || (error as? URLError)?.code == .cancelled { return }
            if case ClientError.server(status: 404, _) = error {
                missingOnServer = true
                return
            }
            // Keep showing the last forecast through a blip; only an empty tab shows the error.
            self.error = error.localizedDescription
        }
    }

    private func loadRadar() async {
        do {
            let data = try await model.client.weatherRadar(scope: effectiveScope, kind: radarKind)
            if Task.isCancelled { return }
            if let image = await AnimatedImageView.decode(data) {
                radar = image
                radarError = nil
            } else {
                radarError = "The radar picture couldn't be read."
            }
        } catch {
            if error is CancellationError || (error as? URLError)?.code == .cancelled { return }
            if radar == nil { radarError = error.localizedDescription }
        }
    }
}

private struct TabKey: Equatable {
    let active: Bool
    let server: String
}

private struct RadarRequest: Equatable {
    let active: Bool
    let scope: String
    let kind: String
}

// MARK: - Pieces

private struct AlertRow: View {
    let alert: WeatherAlert
    let zone: TimeZone

    private var color: Color {
        alert.isSevere ? SentinelTheme.red : alert.severity == "Moderate" ? SentinelTheme.amber : SentinelTheme.cyan
    }

    var body: some View {
        HStack(alignment: .top, spacing: 24) {
            Image(systemName: "exclamationmark.triangle.fill")
                .font(.title2)
                .foregroundStyle(color)
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text(alert.event).font(.title3.bold()).foregroundStyle(color)
                    Spacer()
                    if let ends = alert.ends {
                        Text("until \(ends.formatted(Date.FormatStyle(timeZone: zone).weekday(.abbreviated).hour().minute()))")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    }
                }
                if !alert.headline.isEmpty {
                    Text(alert.headline).font(.callout).lineLimit(2)
                }
            }
        }
        .padding(28)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(color.opacity(0.14))
    }
}

private struct NowCard: View {
    let current: WeatherCurrent?
    let sun: WeatherSun?
    let zone: TimeZone

    var body: some View {
        VStack(alignment: .leading, spacing: 24) {
            Text("NOW").font(.headline).foregroundStyle(.secondary)
            if let current {
                HStack(spacing: 28) {
                    Image(systemName: WeatherSymbol.name(current.icon, isDaytime: current.isDaytime))
                        .symbolRenderingMode(.multicolor)
                        .font(.system(size: 110))
                    VStack(alignment: .leading, spacing: 4) {
                        Text("\(current.temperatureF)°F")
                            .font(.system(size: 88, weight: .bold, design: .rounded))
                        Text(current.description.isEmpty ? "—" : current.description)
                            .font(.title3)
                        if let feels = current.feelsLikeF, feels != current.temperatureF {
                            Text("Feels like \(feels)°").font(.callout).foregroundStyle(.secondary)
                        }
                    }
                }
                Grid(alignment: .leading, horizontalSpacing: 28, verticalSpacing: 10) {
                    ForEach(details(current), id: \.0) { label, value in
                        GridRow {
                            Text(label).foregroundStyle(.secondary)
                            Text(value)
                        }
                    }
                }
                .font(.callout)
                Text(source(current))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else {
                Text("No current conditions from the nearest station.").foregroundStyle(.secondary)
            }
        }
        .padding(32)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 20))
    }

    private func details(_ current: WeatherCurrent) -> [(String, String)] {
        let clock = Date.FormatStyle(date: .omitted, time: .shortened, timeZone: zone)
        let wind: String? = current.windMph.map { mph in
            mph == 0 ? "Calm" : [current.windDirection, "\(mph) mph"].compactMap { $0 }.joined(separator: " ")
        }
        let rows: [(String, String?)] = [
            ("Humidity", current.humidityPercent.map { "\($0)%" }),
            ("Dew point", current.dewpointF.map { "\($0)°" }),
            ("Wind", wind),
            ("Gusts", current.windGustMph.map { "\($0) mph" }),
            ("Pressure", current.pressureInHg.map { String(format: "%.2f inHg", $0) }),
            ("Visibility", current.visibilityMiles.map { "\($0.formatted(.number.precision(.fractionLength(0...1)))) mi" }),
            ("Sunrise", sun?.sunrise.map { $0.formatted(clock) }),
            ("Sunset", sun?.sunset.map { $0.formatted(clock) }),
        ]
        return rows.compactMap { label, value in value.map { (label, $0) } }
    }

    private func source(_ current: WeatherCurrent) -> String {
        var text = "\(current.station) · \(current.stationName)"
        if let observed = current.observedAt {
            text += " · observed \(observed.formatted(Date.FormatStyle(date: .omitted, time: .shortened, timeZone: zone)))"
        }
        return text
    }
}

private struct HourCell: View {
    let hour: WeatherHour
    let dayLabel: String?
    let zone: TimeZone

    var body: some View {
        VStack(spacing: 10) {
            Text(dayLabel ?? " ").font(.caption.bold()).foregroundStyle(SentinelTheme.green)
            Text(hour.startTime.formatted(Date.FormatStyle(timeZone: zone).hour()))
                .font(.callout)
                .foregroundStyle(.secondary)
            Image(systemName: WeatherSymbol.name(hour.icon, isDaytime: hour.isDaytime))
                .symbolRenderingMode(.multicolor)
                .font(.system(size: 44))
                .frame(height: 56)
            Text(hour.temperatureF.map { "\($0)°" } ?? "—").font(.title3.bold())
            Text(chanceText).font(.caption).foregroundStyle(SentinelTheme.cyan)
        }
        .frame(width: 130)
        .padding(.vertical, 18)
    }

    private var chanceText: String {
        guard let chance = hour.precipitationChance, chance > 0 else { return " " }
        return "\(chance)%"
    }
}

private struct DayCard: View {
    let day: WeatherDay

    var body: some View {
        let lead = day.lead
        VStack(spacing: 12) {
            Text(lead.name).font(.headline).lineLimit(1).minimumScaleFactor(0.7)
            Image(systemName: WeatherSymbol.name(lead.icon, isDaytime: lead.isDaytime))
                .symbolRenderingMode(.multicolor)
                .font(.system(size: 56))
                .frame(height: 70)
            HStack(spacing: 10) {
                if let high = day.day?.temperatureF { Text("\(high)°").bold() }
                if let low = day.night?.temperatureF { Text("\(low)°").foregroundStyle(.secondary) }
            }
            .font(.title3)
            Text(lead.shortForecast)
                .font(.caption)
                .multilineTextAlignment(.center)
                .lineLimit(3, reservesSpace: true)
            Text(day.chance > 0 ? "\(day.chance)% chance" : " ")
                .font(.caption)
                .foregroundStyle(SentinelTheme.cyan)
        }
        .padding(20)
        .frame(width: 228)
    }
}

// Sheets scroll by moving focus, so the text is split into focusable paragraphs - a single block
// of text longer than the screen could never be scrolled with the remote.
private struct WeatherAlertSheet: View {
    let alert: WeatherAlert
    let zone: TimeZone

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                Text(alert.event).font(.title2.bold())
                    .foregroundStyle(alert.isSevere ? SentinelTheme.red : SentinelTheme.amber)
                if !alert.headline.isEmpty { Text(alert.headline).font(.headline) }
                FocusableParagraphs(text: alert.description)
                if !alert.instruction.isEmpty {
                    Text("WHAT TO DO").font(.headline).foregroundStyle(.secondary)
                    FocusableParagraphs(text: alert.instruction)
                }
                Text([alert.areaDescription, alert.sender].filter { !$0.isEmpty }.joined(separator: " · "))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .focusable()
            }
            .padding(60)
        }
    }
}

private struct WeatherDaySheet: View {
    let day: WeatherDay

    var body: some View {
        VStack(alignment: .leading, spacing: 32) {
            ForEach([day.day, day.night].compactMap { $0 }) { period in
                VStack(alignment: .leading, spacing: 10) {
                    Label(period.name, systemImage: WeatherSymbol.name(period.icon, isDaytime: period.isDaytime))
                        .font(.title3.bold())
                        .symbolRenderingMode(.multicolor)
                    Text(period.detailedForecast)
                }
                .focusable()
            }
        }
        .padding(60)
        .frame(maxWidth: 1200, alignment: .leading)
    }
}

private struct FocusableParagraphs: View {
    let text: String

    var body: some View {
        // The NWS wraps its text at ~70 columns; blank lines are the real paragraph breaks.
        let paragraphs = text.components(separatedBy: "\n\n")
            .map { $0.replacingOccurrences(of: "\n", with: " ").trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
        ForEach(Array(paragraphs.enumerated()), id: \.offset) { _, paragraph in
            Text(paragraph).focusable()
        }
    }
}

// The radar comes as an animated GIF, which SwiftUI's Image shows as a still. UIImageView plays
// an animated UIImage.
struct AnimatedImageView: UIViewRepresentable {
    let image: UIImage

    func makeUIView(context: Context) -> UIImageView {
        let view = UIImageView()
        view.contentMode = .scaleAspectFit
        view.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        view.setContentCompressionResistancePriority(.defaultLow, for: .vertical)
        return view
    }

    func updateUIView(_ view: UIImageView, context: Context) {
        if view.image !== image { view.image = image }
    }

    // Off the main actor: a loop is a dozen or so 600px frames. Each frame is repeated to honour
    // its own delay (the NWS holds the newest frame longer), since UIImage.animatedImage spaces
    // frames evenly.
    nonisolated static func decode(_ data: Data) async -> UIImage? {
        await Task.detached(priority: .utility) { () -> UIImage? in
            guard let source = CGImageSourceCreateWithData(data as CFData, nil) else { return nil }
            let count = CGImageSourceGetCount(source)
            guard count > 1 else {
                return CGImageSourceCreateImageAtIndex(source, 0, nil).map { UIImage(cgImage: $0) }
            }
            let unit = 0.1
            var frames: [UIImage] = []
            for index in 0..<count {
                guard let frame = CGImageSourceCreateImageAtIndex(source, index, nil) else { continue }
                let image = UIImage(cgImage: frame)
                let repeats = min(30, max(1, Int((delay(source, index) / unit).rounded())))
                frames.append(contentsOf: repeatElement(image, count: repeats))
            }
            return UIImage.animatedImage(with: frames, duration: Double(frames.count) * unit)
        }.value
    }

    private nonisolated static func delay(_ source: CGImageSource, _ index: Int) -> Double {
        let properties = CGImageSourceCopyPropertiesAtIndex(source, index, nil) as? [CFString: Any]
        let gif = properties?[kCGImagePropertyGIFDictionary] as? [CFString: Any]
        let value = (gif?[kCGImagePropertyGIFUnclampedDelayTime] as? Double) ?? (gif?[kCGImagePropertyGIFDelayTime] as? Double) ?? 0.1
        return value < 0.02 ? 0.1 : value
    }
}
