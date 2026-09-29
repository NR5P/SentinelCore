import SwiftUI

// The dashboard's Weather tab on the iPhone, iPad and Mac: alerts, conditions now, a zoomable
// radar map with storm tracking, the storms near home, the severe outlook, rain and snow totals,
// the next hours and the week, and the forecasters' discussion - all from the server's
// /api/weather endpoints (the server talks to the National Weather Service and friends and caches
// the answers; nothing here reaches weather.gov directly). Read-only: the location is set under
// Settings.
//
// Fetches only while the section is on screen, like the dashboard: the forecast every ten
// minutes, the radar frames and the storms every two.
struct WeatherView: View {
    @EnvironmentObject private var model: AppModel

    let isActive: Bool

    @State private var report: WeatherReport?
    @State private var details: WeatherDetails?
    @State private var error: String?
    @State private var missingOnServer = false
    @State private var loading = false
    @State private var refreshFailed = false
    @StateObject private var radar = WeatherRadarModel()
    // Which alerts and days are open, by id, so a ten-minute refresh doesn't fold up the warning
    // somebody is in the middle of reading.
    @State private var openAlerts: Set<String> = []
    @State private var openDays: Set<String> = []
    @State private var discussionOpen = false
    @State private var width: CGFloat = 0

    private static let refreshInterval = Duration.seconds(10 * 60)

    private var zone: TimeZone { report?.timeZone ?? .current }
    private var wide: Bool { width >= 860 }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                content
            }
            .padding()
            .frame(maxWidth: 1500, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        .background {
            GeometryReader { proxy in
                Color.clear
                    .onAppear { width = proxy.size.width }
                    .onChange(of: proxy.size.width) { _, value in width = value }
            }
        }
        .navigationTitle("Weather")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        .refreshable { await refreshAll() }
        #endif
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button {
                    Task { await refreshAll() }
                } label: {
                    Label("Refresh", systemImage: "arrow.clockwise")
                }
                .disabled(loading)
                .keyboardShortcut("r", modifiers: .command)
            }
        }
        // Keyed on the server too: at launch the section can appear before the app has its address.
        .task(id: TabKey(active: isActive, server: model.activeServerURL)) {
            guard isActive, !model.activeServerURL.isEmpty else { return }
            await load()
            while !Task.isCancelled {
                try? await Task.sleep(for: Self.refreshInterval)
                if Task.isCancelled { return }
                await load()
            }
        }
        .task(id: TabKey(active: isActive && report?.state == "ok", server: model.activeServerURL)) {
            guard isActive, report?.state == "ok" else {
                radar.stop()
                return
            }
            radar.configure(client: model.client, home: report?.location)
            await radar.refresh()
            while !Task.isCancelled {
                try? await Task.sleep(for: WeatherRadarModel.refreshInterval)
                if Task.isCancelled { break }
                await radar.refresh()
            }
            radar.stop()
        }
    }

    // MARK: - States

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
                    Text((report.message.map { $0 + "\n\n" } ?? "")
                         + "Set the house's location under Settings → Server Settings → Location (an admin can), or on the dashboard. The forecast comes from the US National Weather Service, so this works for places in the United States and its territories.")
                }
                .frame(maxWidth: .infinity)
            case "unsupported":
                placeHeader(report)
                ContentUnavailableView("No Forecast for This Location", systemImage: "globe.americas",
                                       description: Text(report.message ?? "The National Weather Service only covers the United States and its territories."))
                    .frame(maxWidth: .infinity)
            case "unreachable":
                ContentUnavailableView {
                    Label("Weather.gov Unreachable", systemImage: "icloud.slash")
                } description: {
                    Text((report.message ?? "The server couldn't reach the National Weather Service.")
                         + " This section tries again on its own every ten minutes.")
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
            ProgressView().frame(maxWidth: .infinity, minHeight: 300)
        }
    }

    @ViewBuilder private func forecast(_ report: WeatherReport) -> some View {
        placeHeader(report)
        problems(report)
        WeatherStormBanner(radar: radar, zone: zone)
        if let alerts = report.alerts, !alerts.isEmpty {
            VStack(alignment: .leading, spacing: 10) {
                ForEach(alerts) { alert in
                    WeatherAlertCard(alert: alert, zone: zone, isOpen: Binding(
                        get: { openAlerts.contains(alert.id) },
                        set: { open in if open { openAlerts.insert(alert.id) } else { openAlerts.remove(alert.id) } }))
                }
            }
        }
        if wide {
            HStack(alignment: .top, spacing: 16) {
                WeatherNowCard(report: report, details: details, zone: zone)
                    .frame(width: 340)
                radarPanel(report, height: 460)
            }
        } else {
            WeatherNowCard(report: report, details: details, zone: zone)
            radarPanel(report, height: 360)
        }
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 280), spacing: 16, alignment: .top)], alignment: .leading, spacing: 16) {
            WeatherStormsCard(radar: radar)
            WeatherOutlookCard(details: details, zone: zone)
            WeatherTotalsCard(details: details)
        }
        if let hours = report.hourly, !hours.isEmpty {
            WeatherHourlySection(hours: hours, details: details, zone: zone)
        }
        let days = WeatherDay.group(report.daily ?? [], in: zone)
        if !days.isEmpty {
            WeatherDailySection(days: days, periods: report.daily ?? [], compact: width > 0 && width < 600, openDays: $openDays)
        }
        if let discussion = details?.discussion, !discussion.text.isEmpty {
            WeatherDiscussionSection(discussion: discussion, zone: zone, isOpen: $discussionOpen)
        }
        Text(footer(report))
            .font(.caption)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
    }

    private func radarPanel(_ report: WeatherReport, height: CGFloat) -> some View {
        WeatherRadarPanel(radar: radar, zone: zone, reportHasStation: report.radar != nil, mapHeight: height)
    }

    // MARK: - Pieces

    private func placeHeader(_ report: WeatherReport) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            if let location = report.location {
                Text(location.displayName.uppercased())
                    .font(.title3.bold())
                    .foregroundStyle(SentinelTheme.green)
                Text(placeMeta(location))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            if let fetched = report.fetchedAt {
                Text(updatedText(fetched, stale: report.stale == true))
                    .font(.caption)
                    .foregroundStyle(report.stale == true || refreshFailed ? SentinelTheme.amber : .secondary)
            }
        }
    }

    private func placeMeta(_ location: WeatherLocation) -> String {
        var parts: [String] = []
        if !location.label.isEmpty, !location.nearest.isEmpty, location.nearest.lowercased() != location.label.lowercased() {
            parts.append("near \(location.nearest)")
        }
        parts.append(String(format: "%.4f, %.4f", location.latitude, location.longitude))
        if let office = location.forecastOffice { parts.append("forecast office \(office)") }
        return parts.joined(separator: " · ")
    }

    private func updatedText(_ fetched: Date, stale: Bool) -> String {
        var text = "Updated \(fetched.formatted(Date.FormatStyle(date: .omitted, time: .shortened, timeZone: zone)))"
        if stale { text += " · showing the last answer weather.gov gave" }
        if refreshFailed { text += " · refresh failed, showing the last answer" }
        return text
    }

    @ViewBuilder private func problems(_ report: WeatherReport) -> some View {
        let all = (report.problems ?? []) + (details?.problems ?? [])
        if !all.isEmpty {
            VStack(alignment: .leading, spacing: 4) {
                ForEach(all, id: \.self) { Label($0, systemImage: "exclamationmark.triangle") }
            }
            .font(.caption)
            .foregroundStyle(SentinelTheme.amber)
        }
    }

    private func footer(_ report: WeatherReport) -> String {
        var text = "Forecast, observations, radar and discussion from the US National Weather Service (weather.gov)"
        if let issued = report.forecastUpdatedAt {
            text += ", forecast issued \(issued.formatted(Date.FormatStyle(timeZone: zone).weekday(.abbreviated).hour().minute()))"
        }
        text += ". Severe outlook from the NOAA Storm Prediction Center; storm tracks and warning outlines via the Iowa Environmental Mesonet; map © Apple."
        if let name = report.location?.timeZone {
            text += " Times are shown in \(name.replacingOccurrences(of: "_", with: " "))."
        }
        return text
    }

    // MARK: - Loading

    private func refreshAll() async {
        await load()
        if report?.state == "ok" { await radar.refresh() }
    }

    private func load() async {
        guard !loading else { return }
        loading = true
        defer { loading = false }
        do {
            let next = try await model.client.weather()
            report = next
            error = nil
            missingOnServer = false
            refreshFailed = false
            if next.state == "ok" {
                radar.configure(client: model.client, home: next.location)
                if let answer = try? await model.client.weatherDetails() { details = answer }
            }
        } catch {
            if error is CancellationError || (error as? URLError)?.code == .cancelled { return }
            if case ClientError.server(status: 404, _) = error {
                missingOnServer = true
                return
            }
            // Keep showing the last forecast through a blip; only an empty section shows the error.
            if report == nil { self.error = error.localizedDescription } else { refreshFailed = true }
        }
    }
}

private struct TabKey: Equatable {
    let active: Bool
    let server: String
}

// MARK: - Section heading

struct WeatherSectionTitle: View {
    let title: String
    var body: some View {
        Text(title.uppercased())
            .font(.caption.weight(.semibold))
            .foregroundStyle(.secondary)
    }
}

// MARK: - Storm banner

// The banner that matters most on a stormy day: a tracked storm on course for the house, or the
// house inside a warning outline. Tapping a storm flies the map to it.
private struct WeatherStormBanner: View {
    @ObservedObject var radar: WeatherRadarModel
    let zone: TimeZone

    var body: some View {
        let covering = radar.warningsCoveringHome
        let incoming = Array(radar.incomingCells.prefix(3))
        if !covering.isEmpty || !incoming.isEmpty {
            let severe = covering.contains { $0.phenomena == "TO" } || incoming.contains { $0.threat >= 3 }
            let color = severe ? SentinelTheme.red : SentinelTheme.amber
            HStack(alignment: .top, spacing: 12) {
                Image(systemName: "cloud.bolt.rain.fill")
                    .font(.title2)
                    .symbolRenderingMode(.multicolor)
                VStack(alignment: .leading, spacing: 6) {
                    ForEach(covering) { warning in
                        Text(warningLine(warning)).font(.callout)
                    }
                    ForEach(incoming) { cell in
                        Button { radar.focus(cell) } label: {
                            Text(cellLine(cell)).font(.callout).multilineTextAlignment(.leading)
                        }
                        .buttonStyle(.plain)
                        .help("Show this storm on the map.")
                    }
                }
                Spacer(minLength: 0)
            }
            .padding(12)
            .background(color.opacity(0.16), in: RoundedRectangle(cornerRadius: 12))
            .overlay(RoundedRectangle(cornerRadius: 12).stroke(color.opacity(0.6)))
        }
    }

    private func warningLine(_ warning: WeatherWarningArea) -> AttributedString {
        var line = AttributedString("Home is inside a \(warning.name)")
        line.font = .callout.bold()
        var rest = ""
        if let expires = warning.expires {
            rest += " until \(expires.formatted(Date.FormatStyle(date: .omitted, time: .shortened, timeZone: zone)))"
        }
        if !warning.tags.isEmpty { rest += " · \(warning.tags)" }
        return line + AttributedString(rest)
    }

    private func cellLine(_ cell: WeatherStormCell) -> AttributedString {
        var line = AttributedString("\(cell.title) heading this way")
        line.font = .callout.bold()
        let passing = (cell.closestMiles ?? 0) < 1 ? "overhead" : String(format: "%.1f mi away", cell.closestMiles ?? 0)
        var rest = " · about \(cell.etaMinutes ?? 0) min out, passing \(passing)"
        if let distance = cell.distanceMiles { rest += " · now \(Int(distance.rounded())) mi \(cell.bearingCompass ?? "")" }
        if !cell.hazards.isEmpty { rest += " · \(cell.hazards)" }
        return line + AttributedString(rest)
    }
}

// MARK: - Alerts

private struct WeatherAlertCard: View {
    let alert: WeatherAlert
    let zone: TimeZone
    @Binding var isOpen: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Button {
                withAnimation(.easeOut(duration: 0.15)) { isOpen.toggle() }
            } label: {
                HStack(alignment: .firstTextBaseline, spacing: 10) {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .foregroundStyle(alert.color)
                    Text(alert.event)
                        .font(.headline)
                        .foregroundStyle(alert.color)
                    Spacer(minLength: 8)
                    if let ends = alert.ends {
                        Text("until \(ends.formatted(Date.FormatStyle(timeZone: zone).weekday(.abbreviated).hour().minute()))")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    Image(systemName: "chevron.right")
                        .font(.caption.bold())
                        .foregroundStyle(.secondary)
                        .rotationEffect(.degrees(isOpen ? 90 : 0))
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help("The full text of this \(alert.severity.lowercased()) \(alert.urgency.lowercased())-urgency alert.")

            if !alert.headline.isEmpty {
                Text(alert.headline).font(.callout)
            }
            if isOpen {
                WeatherParagraphs(text: alert.description)
                if !alert.instruction.isEmpty {
                    WeatherSectionTitle(title: "What to do")
                    WeatherParagraphs(text: alert.instruction)
                        .fontWeight(.medium)
                }
                let meta = [alert.areaDescription, alert.sender].filter { !$0.isEmpty }.joined(separator: " · ")
                if !meta.isEmpty {
                    Text(meta).font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(alert.color.opacity(alert.isSevere ? 0.16 : 0.10), in: RoundedRectangle(cornerRadius: 12))
        .overlay(alignment: .leading) {
            UnevenRoundedRectangle(topLeadingRadius: 12, bottomLeadingRadius: 12)
                .fill(alert.color)
                .frame(width: 4)
        }
        .textSelection(.enabled)
    }
}

// The NWS wraps its text at ~70 columns; blank lines are the real paragraph breaks.
struct WeatherParagraphs: View {
    let text: String

    var body: some View {
        let paragraphs = text.components(separatedBy: "\n\n")
            .map { $0.replacingOccurrences(of: "\n", with: " ").trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
        VStack(alignment: .leading, spacing: 8) {
            ForEach(Array(paragraphs.enumerated()), id: \.offset) { _, paragraph in
                Text(paragraph)
                    .font(.callout)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

// MARK: - Now

private struct WeatherNowCard: View {
    let report: WeatherReport
    let details: WeatherDetails?
    let zone: TimeZone

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            WeatherSectionTitle(title: "Now")
            let next = WeatherText.nextPrecipitation(report.hourly ?? [], zone: zone)
            if let current = report.current {
                HStack(alignment: .center, spacing: 16) {
                    Image(systemName: WeatherSymbol.name(current.icon, isDaytime: current.isDaytime))
                        .symbolRenderingMode(.multicolor)
                        .font(.system(size: 54))
                        .frame(width: 70)
                    VStack(alignment: .leading, spacing: 2) {
                        Text("\(current.temperatureF)°F")
                            .font(.system(size: 44, weight: .bold, design: .rounded))
                        Text(current.description.isEmpty ? "—" : current.description)
                            .font(.headline)
                        if let feels = current.feelsLikeF, feels != current.temperatureF {
                            Text("Feels like \(feels)°").font(.callout).foregroundStyle(.secondary)
                        }
                        if !hiLo.isEmpty {
                            Text(hiLo).font(.callout.monospacedDigit()).foregroundStyle(.secondary)
                        }
                    }
                }
                if let next {
                    Label(next, systemImage: "umbrella")
                        .font(.callout)
                        .foregroundStyle(SentinelTheme.cyan)
                }
                Grid(alignment: .leadingFirstTextBaseline, horizontalSpacing: 16, verticalSpacing: 6) {
                    ForEach(rows(current), id: \.0) { label, value in
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
                    .help("Current conditions come from the nearest National Weather Service observation station, which is usually an airport a few miles away - so it can differ a degree or two from the porch.")
            } else {
                Text("No current conditions from the nearest station.").foregroundStyle(.secondary)
                if let next {
                    Label(next, systemImage: "umbrella").font(.callout).foregroundStyle(SentinelTheme.cyan)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .sentinelCard()
    }

    private var hiLo: String {
        let today = report.daily?.first { $0.isDaytime }?.temperatureF
        let tonight = report.daily?.first { !$0.isDaytime }?.temperatureF
        return [today.map { "H \($0)°" }, tonight.map { "L \($0)°" }].compactMap { $0 }.joined(separator: "  ")
    }

    private func rows(_ current: WeatherCurrent) -> [(String, String)] {
        let clock = Date.FormatStyle(date: .omitted, time: .shortened, timeZone: zone)
        let wind: String? = current.windMph.map { mph in
            mph == 0 ? "Calm" : [current.windDirection, "\(mph) mph"].compactMap { $0 }.joined(separator: " ")
        }
        let pressure: String? = current.pressureInHg.map { value in
            var text = String(format: "%.2f inHg", value)
            if let trend = details?.pressure { text += " \(trend.arrow) \(trend.trend)" }
            return text
        }
        let moon = MoonPhase(date: Date())
        let rows: [(String, String?)] = [
            ("Humidity", current.humidityPercent.map { "\($0)%" }),
            ("Dew point", current.dewpointF.map { "\($0)°\(WeatherText.dewComfort($0))" }),
            ("Wind", wind),
            ("Gusts", current.windGustMph.map { "\($0) mph" }),
            ("Pressure", pressure),
            ("Visibility", current.visibilityMiles.map { "\($0.formatted(.number.precision(.fractionLength(0...1)))) mi" }),
            ("Sunrise", report.sun?.sunrise.map { $0.formatted(clock) }),
            ("Sunset", report.sun?.sunset.map { $0.formatted(clock) }),
            ("Daylight", WeatherText.daylight(report.sun)),
            ("Moon", "\(moon.name), \(moon.illumination)% lit"),
        ]
        return rows.compactMap { label, value in value.map { (label, $0) } }
    }

    private func source(_ current: WeatherCurrent) -> String {
        var text = "\(current.station) · \(current.stationName)"
        if let observed = current.observedAt {
            text += " · observed \(observed.formatted(Date.FormatStyle(timeZone: zone).weekday(.abbreviated).hour().minute()))"
        }
        return text
    }
}

// MARK: - Cards under the map

private struct WeatherStormsCard: View {
    @ObservedObject var radar: WeatherRadarModel

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            WeatherSectionTitle(title: "Storms near home")
                .help("Storm cells the NEXRAD radars are tracking right now, nearest and most dangerous first. A time means the storm's current track passes within 10 miles of home.")
            let cells = radar.nearbyCells
            if radar.storms == nil {
                Text("loading…").font(.callout).foregroundStyle(.secondary)
            } else if cells.isEmpty {
                Text("No storms tracked within 100 miles.").font(.callout).foregroundStyle(.secondary)
            } else {
                ForEach(cells) { cell in
                    Button { radar.focus(cell) } label: { row(cell) }
                        .buttonStyle(.plain)
                        .help("Show this storm on the map.")
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .sentinelCard()
    }

    private func row(_ cell: WeatherStormCell) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Circle().fill(cell.threatColor).frame(width: 10, height: 10)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(cell.title).font(.callout.weight(.semibold))
                    if cell.towardHome, let eta = cell.etaMinutes {
                        Text("~\(eta) min")
                            .font(.caption.bold())
                            .padding(.horizontal, 5)
                            .background(SentinelTheme.red.opacity(0.2), in: Capsule())
                            .foregroundStyle(SentinelTheme.red)
                    }
                }
                Text(meta(cell)).font(.caption).foregroundStyle(.secondary)
            }
            Spacer(minLength: 0)
        }
        .contentShape(Rectangle())
    }

    private func meta(_ cell: WeatherStormCell) -> String {
        var parts = ["\(Int((cell.distanceMiles ?? 0).rounded())) mi \(cell.bearingCompass ?? "")", cell.motion]
        if let tops = cell.topKft { parts.append("tops \(Int(tops.rounded()))k ft") }
        if !cell.hazards.isEmpty { parts.append(cell.hazards) }
        return parts.joined(separator: " · ")
    }
}

private struct WeatherOutlookCard: View {
    let details: WeatherDetails?
    let zone: TimeZone

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            WeatherSectionTitle(title: "Severe outlook")
                .help("The NOAA Storm Prediction Center's convective outlook for home: the chance of severe thunderstorms today and the next two days, on its 1-to-5 scale, and today's odds of each hazard within 25 miles.")
            if let severe = details?.severe {
                ForEach(severe.days) { day in
                    HStack(spacing: 10) {
                        Text(day.category ?? "NONE")
                            .font(.caption2.bold())
                            .tracking(1)
                            .frame(minWidth: 48)
                            .padding(.vertical, 2)
                            .foregroundStyle(day.category == nil ? Color.secondary : day.category == "MDT" ? .white : .black)
                            .background(day.category == nil ? Color.clear : day.chipColor, in: RoundedRectangle(cornerRadius: 3))
                            .overlay(RoundedRectangle(cornerRadius: 3).stroke(day.category == nil ? Color.secondary.opacity(0.5) : .clear))
                        Text(dayName(day.day)).font(.callout.weight(.semibold)).frame(width: 90, alignment: .leading)
                        Text(day.scaleText).font(.caption).foregroundStyle(.secondary)
                    }
                }
                HStack(spacing: 16) {
                    hazard("Tornado", severe.tornadoPercent, severe.significantTornado)
                    hazard("Hail", severe.hailPercent, severe.significantHail)
                    hazard("Wind", severe.windPercent, severe.significantWind)
                }
                .padding(.top, 2)
                if let issued = severe.issuedAt {
                    Text("SPC day 1 issued \(issued.formatted(Date.FormatStyle(timeZone: zone).weekday(.abbreviated).hour().minute()))")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            } else {
                Text(details == nil ? "loading…" : "The Storm Prediction Center's outlook is unavailable right now.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .sentinelCard()
    }

    private func dayName(_ day: Int) -> String {
        if day == 1 { return "Today" }
        if day == 2 { return "Tomorrow" }
        let date = Date().addingTimeInterval(Double(day - 1) * 86_400)
        return date.formatted(Date.FormatStyle(timeZone: zone).weekday(.wide))
    }

    private func hazard(_ label: String, _ percent: Int?, _ significant: Bool) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(label).font(.caption).foregroundStyle(.secondary)
            HStack(spacing: 4) {
                Text("\(percent ?? 0)%").font(.callout.bold().monospacedDigit())
                if significant {
                    Text("sig")
                        .font(.caption2.bold())
                        .padding(.horizontal, 4)
                        .background(SentinelTheme.red.opacity(0.2), in: Capsule())
                        .foregroundStyle(SentinelTheme.red)
                        .help("Hatched area: the SPC expects the significant kind here (EF2+ tornadoes, 2-inch hail or 75 mph gusts).")
                }
            }
        }
    }
}

private struct WeatherTotalsCard: View {
    let details: WeatherDetails?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            WeatherSectionTitle(title: "Rain & snow ahead")
                .help("From the National Weather Service's forecast grid for this spot: expected liquid rain, snowfall and ice, and the highest chance of thunder.")
            if let totals = details?.totals {
                Grid(alignment: .leadingFirstTextBaseline, horizontalSpacing: 16, verticalSpacing: 6) {
                    ForEach(rows(totals), id: \.0) { label, value in
                        GridRow {
                            Text(label).foregroundStyle(.secondary)
                            Text(value).monospacedDigit()
                        }
                    }
                }
                .font(.callout)
            } else {
                Text(details == nil ? "loading…" : "Precipitation totals are unavailable right now.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .sentinelCard()
    }

    private func rows(_ totals: WeatherPrecipTotals) -> [(String, String)] {
        var rows: [(String, String)] = [
            ("Rain, next 24 h", WeatherText.inches(totals.rainNext24HoursInches)),
            ("Rain, next 7 days", WeatherText.inches(totals.rainNext7DaysInches)),
        ]
        if totals.snowNext7DaysInches > 0 {
            rows.append(("Snow, next 24 h", WeatherText.inches(totals.snowNext24HoursInches, digits: 1)))
            rows.append(("Snow, next 7 days", WeatherText.inches(totals.snowNext7DaysInches, digits: 1)))
        }
        if totals.iceNext7DaysInches > 0 {
            rows.append(("Ice, next 7 days", WeatherText.inches(totals.iceNext7DaysInches)))
        }
        rows.append(("Thunder, next 24 h", "\(totals.thunderChanceNext24Hours)%"))
        rows.append(("Thunder, next 7 days", "\(totals.thunderChanceNext7Days)%"))
        return rows
    }
}

// MARK: - Hourly

private struct WeatherHourlySection: View {
    let hours: [WeatherHour]
    let details: WeatherDetails?
    let zone: TimeZone

    private static let step: CGFloat = 54

    var body: some View {
        let extras = Dictionary((details?.hours ?? []).map { ($0.startTime, $0) }, uniquingKeysWith: { a, _ in a })
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 14) {
                WeatherSectionTitle(title: "Next \(hours.count) hours")
                Spacer(minLength: 0)
                legendKey(color: SentinelTheme.green, dashed: false, label: "temperature")
                legendKey(color: SentinelTheme.amber, dashed: true, label: "feels like")
                HStack(spacing: 4) {
                    RoundedRectangle(cornerRadius: 1).fill(SentinelTheme.cyan.opacity(0.5)).frame(width: 10, height: 8)
                    Text("chance of rain")
                }
            }
            .font(.caption2)
            .foregroundStyle(.secondary)
            ScrollView(.horizontal) {
                VStack(alignment: .leading, spacing: 4) {
                    WeatherHourlyChart(hours: hours, feels: hours.map { extras[$0.startTime]?.feelsLikeF }, zone: zone, step: Self.step)
                        .frame(width: CGFloat(hours.count) * Self.step, height: 150)
                    HStack(spacing: 0) {
                        ForEach(Array(hours.enumerated()), id: \.element.id) { index, hour in
                            let newDay = index == 0 || !sameDay(hours[index - 1].startTime, hour.startTime)
                            WeatherHourCell(hour: hour, extra: extras[hour.startTime], newDay: newDay, zone: zone)
                                .frame(width: Self.step)
                        }
                    }
                }
                .padding(.bottom, 6)
            }
        }
        .sentinelCard()
    }

    private func legendKey(color: Color, dashed: Bool, label: String) -> some View {
        HStack(spacing: 4) {
            Path { path in
                path.move(to: CGPoint(x: 0, y: 4))
                path.addLine(to: CGPoint(x: 14, y: 4))
            }
            .stroke(color, style: StrokeStyle(lineWidth: 2, dash: dashed ? [3, 2] : []))
            .frame(width: 14, height: 8)
            Text(label)
        }
    }

    private func sameDay(_ a: Date, _ b: Date) -> Bool {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = zone
        return calendar.isDate(a, inSameDayAs: b)
    }
}

// Temperature and feels-like lines over chance-of-rain bars, day dividers and hour labels - a port
// of the dashboard's hourly SVG chart.
private struct WeatherHourlyChart: View {
    let hours: [WeatherHour]
    let feels: [Int?]
    let zone: TimeZone
    let step: CGFloat

    var body: some View {
        Canvas { context, size in
            let temps = hours.compactMap(\.temperatureF)
            guard temps.count >= 2 else { return }
            let top: CGFloat = 24, tempBottom: CGFloat = 92, barBottom: CGFloat = 132
            let all = temps + feels.compactMap { $0 }
            let low = Double(all.min() ?? 0), high = Double(all.max() ?? 1)
            let span = max(high - low, 1)
            func y(_ value: Int) -> CGFloat { tempBottom - CGFloat((Double(value) - low) / span) * (tempBottom - top) }
            func x(_ index: Int) -> CGFloat { CGFloat(index) * step + step / 2 }

            var base = Path()
            base.move(to: CGPoint(x: 0, y: barBottom))
            base.addLine(to: CGPoint(x: size.width, y: barBottom))
            context.stroke(base, with: .color(.secondary.opacity(0.4)), lineWidth: 1)

            for (index, hour) in hours.enumerated() {
                let chance = hour.precipitationChance ?? 0
                guard chance > 0 else { continue }
                let height = CGFloat(chance) / 100 * 34
                let bar = CGRect(x: x(index) - step * 0.35, y: barBottom - height, width: step * 0.7, height: height)
                context.fill(Path(roundedRect: bar, cornerRadius: 2), with: .color(SentinelTheme.cyan.opacity(0.45)))
                if chance >= 20 {
                    context.draw(Text("\(chance)").font(.system(size: 9)).foregroundStyle(SentinelTheme.cyan),
                                 at: CGPoint(x: x(index), y: barBottom - height - 7))
                }
            }

            func line(_ values: [Int?]) -> Path {
                var path = Path()
                var started = false
                for (index, value) in values.enumerated() {
                    guard let value else { continue }
                    let point = CGPoint(x: x(index), y: y(value))
                    if started { path.addLine(to: point) } else { path.move(to: point); started = true }
                }
                return path
            }

            if feels.enumerated().contains(where: { index, value in value != nil && value != hours[index].temperatureF }) {
                context.stroke(line(feels), with: .color(SentinelTheme.amber),
                               style: StrokeStyle(lineWidth: 1.5, lineCap: .round, lineJoin: .round, dash: [4, 3]))
            }
            context.stroke(line(hours.map(\.temperatureF)), with: .color(SentinelTheme.green),
                           style: StrokeStyle(lineWidth: 2.5, lineCap: .round, lineJoin: .round))

            var calendar = Calendar(identifier: .gregorian)
            calendar.timeZone = zone
            let hourFormat = Date.FormatStyle(timeZone: zone).hour()
            let weekday = Date.FormatStyle(timeZone: zone).weekday(.abbreviated)
            for (index, hour) in hours.enumerated() {
                if index > 0, !calendar.isDate(hours[index - 1].startTime, inSameDayAs: hour.startTime) {
                    var divider = Path()
                    let left = CGFloat(index) * step
                    divider.move(to: CGPoint(x: left, y: 4))
                    divider.addLine(to: CGPoint(x: left, y: barBottom))
                    context.stroke(divider, with: .color(.secondary.opacity(0.35)), style: StrokeStyle(lineWidth: 1, dash: [2, 3]))
                    context.draw(Text(hour.startTime.formatted(weekday).uppercased()).font(.system(size: 9, weight: .bold)).foregroundStyle(SentinelTheme.green),
                                 at: CGPoint(x: left + 4, y: 10), anchor: .leading)
                }
                if index % 3 == 0 {
                    context.draw(Text(hour.startTime.formatted(hourFormat)).font(.system(size: 9)).foregroundStyle(.secondary),
                                 at: CGPoint(x: x(index), y: size.height - 6))
                    if let temp = hour.temperatureF {
                        context.draw(Text("\(temp)°").font(.system(size: 10, weight: .semibold)),
                                     at: CGPoint(x: x(index), y: y(temp) - 9))
                    }
                }
            }
        }
        .accessibilityLabel("Temperature and chance of precipitation for the next \(hours.count) hours")
    }
}

private struct WeatherHourCell: View {
    let hour: WeatherHour
    let extra: WeatherHourDetail?
    let newDay: Bool
    let zone: TimeZone

    @State private var showingDetail = false

    var body: some View {
        Button { showingDetail = true } label: {
            VStack(spacing: 5) {
                Text(newDay ? hour.startTime.formatted(Date.FormatStyle(timeZone: zone).weekday(.abbreviated)) : " ")
                    .font(.caption2.bold())
                    .foregroundStyle(SentinelTheme.green)
                Text(hour.startTime.formatted(Date.FormatStyle(timeZone: zone).hour()))
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                Image(systemName: WeatherSymbol.name(hour.icon, isDaytime: hour.isDaytime))
                    .symbolRenderingMode(.multicolor)
                    .font(.title3)
                    .frame(height: 26)
                Text(hour.temperatureF.map { "\($0)°" } ?? "—")
                    .font(.callout.bold())
                Text(chanceText)
                    .font(.caption2)
                    .foregroundStyle(SentinelTheme.cyan)
                Text(windText)
                    .font(.system(size: 9))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.7)
            }
            .padding(.vertical, 6)
            .frame(maxWidth: .infinity)
            .background(newDay ? SentinelTheme.green.opacity(0.06) : .clear, in: RoundedRectangle(cornerRadius: 6))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(detailLines.joined(separator: " · "))
        .popover(isPresented: $showingDetail) {
            VStack(alignment: .leading, spacing: 4) {
                Text(hour.startTime.formatted(Date.FormatStyle(timeZone: zone).weekday(.wide).hour()))
                    .font(.headline)
                ForEach(detailLines, id: \.self) { Text($0).font(.callout) }
            }
            .padding()
            .frame(minWidth: 220, alignment: .leading)
            .presentationCompactAdaptation(.popover)
        }
    }

    private var chanceText: String {
        guard let chance = hour.precipitationChance, chance > 0 else { return " " }
        return "\(chance)%"
    }

    private var windText: String {
        let speed = hour.windSpeed?.replacingOccurrences(of: " mph", with: "") ?? ""
        return [speed, hour.windDirection ?? ""].filter { !$0.isEmpty }.joined(separator: " ")
    }

    private var detailLines: [String] {
        let wind = [hour.windDirection, hour.windSpeed].compactMap { $0 }.joined(separator: " ")
        let lines: [String?] = [
            hour.shortForecast,
            wind.isEmpty ? nil : "Wind \(wind)",
            extra?.gustMph.map { "Gusts \($0) mph" },
            hour.precipitationChance.map { "\($0)% chance of precipitation" },
            hour.humidityPercent.map { "Humidity \($0)%" },
            extra?.feelsLikeF.map { "Feels like \($0)°" },
            extra?.skyCoverPercent.map { "Cloud \($0)%" },
            (extra?.rainInches ?? 0) > 0 ? "Rain \(WeatherText.inches(extra?.rainInches))" : nil,
            (extra?.snowInches ?? 0) > 0 ? "Snow \(WeatherText.inches(extra?.snowInches, digits: 1))" : nil,
        ]
        return lines.compactMap { $0 }.filter { !$0.isEmpty }
    }
}

// MARK: - Daily

// One row per calendar day, the day period leading and the night giving the low, with a bar
// placing the day's range within the week's. Tapping a row opens the full forecast for the day and
// the night.
private struct WeatherDailySection: View {
    let days: [WeatherDay]
    let periods: [WeatherPeriod]
    let compact: Bool
    @Binding var openDays: Set<String>

    var body: some View {
        let temps = periods.compactMap(\.temperatureF)
        let weekLow = temps.min() ?? 0
        let weekHigh = temps.max() ?? 1
        VStack(alignment: .leading, spacing: 4) {
            WeatherSectionTitle(title: "\(days.count)-day forecast")
                .padding(.bottom, 6)
            ForEach(Array(days.enumerated()), id: \.element.id) { index, day in
                if index > 0 { Divider() }
                WeatherDayRow(day: day, weekLow: weekLow, weekHigh: weekHigh, compact: compact, isOpen: Binding(
                    get: { openDays.contains(day.key) },
                    set: { open in if open { openDays.insert(day.key) } else { openDays.remove(day.key) } }))
            }
        }
        .sentinelCard()
    }
}

private struct WeatherDayRow: View {
    let day: WeatherDay
    let weekLow: Int
    let weekHigh: Int
    // An iPhone's width: the short forecast moves under the day's name and the columns narrow.
    let compact: Bool
    @Binding var isOpen: Bool

    var body: some View {
        let lead = day.lead
        VStack(alignment: .leading, spacing: 6) {
            Button {
                withAnimation(.easeOut(duration: 0.15)) { isOpen.toggle() }
            } label: {
                HStack(spacing: compact ? 6 : 10) {
                    if compact {
                        VStack(alignment: .leading, spacing: 1) {
                            Text(lead.name)
                                .font(.callout.weight(.semibold))
                                .lineLimit(1)
                                .minimumScaleFactor(0.8)
                            Text(lead.shortForecast)
                                .font(.caption2)
                                .foregroundStyle(.secondary)
                                .lineLimit(1)
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                    } else {
                        Text(lead.name)
                            .font(.callout.weight(.semibold))
                            .lineLimit(1)
                            .minimumScaleFactor(0.8)
                            .frame(width: 104, alignment: .leading)
                    }
                    Image(systemName: WeatherSymbol.name(lead.icon, isDaytime: lead.isDaytime))
                        .symbolRenderingMode(.multicolor)
                        .font(.title3)
                        .frame(width: 30)
                    Text(day.chance > 0 ? "\(day.chance)%" : "")
                        .font(.caption.bold())
                        .foregroundStyle(SentinelTheme.cyan)
                        .frame(width: compact ? 30 : 36, alignment: .leading)
                    if !compact {
                        Text(lead.shortForecast)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(2)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    Text(day.night?.temperatureF.map { "\($0)°" } ?? "")
                        .font(.callout.monospacedDigit())
                        .foregroundStyle(.secondary)
                        .frame(width: 32, alignment: .trailing)
                    rangeBar.frame(width: compact ? 44 : 80, height: 5)
                    Text(day.day?.temperatureF.map { "\($0)°" } ?? "")
                        .font(.callout.bold().monospacedDigit())
                        .frame(width: 32, alignment: .leading)
                    Image(systemName: "chevron.right")
                        .font(.caption2.bold())
                        .foregroundStyle(.secondary)
                        .rotationEffect(.degrees(isOpen ? 90 : 0))
                }
                .padding(.vertical, 6)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help("The full forecast for this day and night.")
            if isOpen {
                VStack(alignment: .leading, spacing: 8) {
                    ForEach([day.day, day.night].compactMap { $0 }) { period in
                        (Text("\(period.name). ").bold() + Text(period.detailedForecast))
                            .font(.callout)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    let wind = [lead.windDirection, lead.windSpeed].compactMap { $0 }.joined(separator: " ")
                    if !wind.isEmpty {
                        Label("Wind \(wind)", systemImage: "wind").font(.caption).foregroundStyle(.secondary)
                    }
                }
                .padding(.bottom, 8)
                .textSelection(.enabled)
            }
        }
    }

    // The day's low-to-high placed within the whole week's range.
    private var rangeBar: some View {
        GeometryReader { proxy in
            let span = Double(max(weekHigh - weekLow, 1))
            let from = day.night?.temperatureF ?? day.day?.temperatureF
            let to = day.day?.temperatureF ?? day.night?.temperatureF
            ZStack(alignment: .leading) {
                Capsule().fill(.primary.opacity(0.1))
                if let from, let to {
                    let start = Double(min(from, to) - weekLow) / span
                    let length = max(Double(abs(to - from)) / span, 0.04)
                    Capsule()
                        .fill(LinearGradient(colors: [SentinelTheme.cyan, SentinelTheme.amber], startPoint: .leading, endPoint: .trailing))
                        .frame(width: proxy.size.width * length)
                        .offset(x: proxy.size.width * start)
                }
            }
        }
    }
}

// MARK: - Discussion

private struct WeatherDiscussionSection: View {
    let discussion: WeatherDiscussion
    let zone: TimeZone
    @Binding var isOpen: Bool

    var body: some View {
        DisclosureGroup(isExpanded: $isOpen) {
            ScrollView(.horizontal) {
                Text(discussion.text.trimmingCharacters(in: .whitespacesAndNewlines))
                    .font(.system(.caption, design: .monospaced))
                    .textSelection(.enabled)
                    .fixedSize(horizontal: true, vertical: true)
                    .padding(.top, 8)
            }
        } label: {
            WeatherSectionTitle(title: title)
                .help("The forecasters' own notes on why the forecast says what it does - how sure they are, what could change, and what they're watching. Written by the local National Weather Service office a few times a day.")
        }
        .sentinelCard()
    }

    private var title: String {
        var text = "Forecaster discussion · \(discussion.office)"
        if let issued = discussion.issuedAt {
            text += " · issued \(issued.formatted(Date.FormatStyle(timeZone: zone).weekday(.abbreviated).hour().minute()))"
        }
        return text
    }
}
