import ImageIO
import SwiftUI
import UIKit

// The dashboard's Weather tab on the television: where the forecast is for, alerts and storms
// heading for the house, conditions now, a radar map centred on home with storm tracking, the
// storms near home, the severe outlook, rain and snow totals, the next hours and the week, and
// the forecasters' discussion - all from the server's /api/weather endpoints (the server talks to
// the National Weather Service and friends and caches it - nothing here reaches weather.gov
// directly). The same sections as the iPhone/Mac app's WeatherView, laid out for across the room
// and driven by focus. Read-only: the location is set on the dashboard or in the phone and
// computer apps' Settings.
//
// Fetches only while the tab is on screen, like the dashboard: the forecast every ten minutes,
// the radar frames and the storms every two. A server too old for the radar map gets the NWS's
// animated radar picture instead.
struct WeatherTVView: View {
    @EnvironmentObject private var model: AppModel

    let isActive: Bool

    @State private var report: WeatherReport?
    @State private var details: WeatherDetails?
    @State private var error: String?
    @State private var missingOnServer = false
    @State private var loading = false
    @State private var refreshFailed = false
    @StateObject private var radar = WeatherRadarModel()
    @State private var shownAlert: WeatherAlert?
    @State private var shownDay: WeatherDay?
    @State private var shownHour: WeatherHour?
    @State private var discussionOpen = false
    @State private var radarFullScreen = false

    private static let refreshInterval = Duration.seconds(10 * 60)

    private var zone: TimeZone { report?.timeZone ?? .current }

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 44) {
                    content
                }
                .padding(.horizontal, 60)
                .padding(.bottom, 60)
            }
            #if DEBUG
            // Simulator testing without a remote: `-debugWeatherScrollTo cards|hourly|days|footer`.
            .onChange(of: details != nil) { _, ready in
                if ready, let id = UserDefaults.standard.string(forKey: "debugWeatherScrollTo") {
                    Task {
                        try? await Task.sleep(for: .seconds(1))
                        proxy.scrollTo(id, anchor: .top)
                    }
                }
            }
            #endif
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
        .sheet(item: $shownAlert) { alert in
            WeatherAlertSheet(alert: alert, zone: zone)
        }
        .sheet(item: $shownDay) { day in
            WeatherDaySheet(day: day)
        }
        .sheet(item: $shownHour) { hour in
            WeatherHourSheet(hour: hour, extra: details?.hours?.first { $0.startTime == hour.startTime }, zone: zone)
        }
        .sheet(isPresented: $discussionOpen) {
            if let discussion = details?.discussion {
                WeatherDiscussionSheet(discussion: discussion, zone: zone)
            }
        }
        .fullScreenCover(isPresented: $radarFullScreen) {
            WeatherRadarFullScreen(radar: radar, zone: zone)
        }
        #if DEBUG
        // Simulator testing without a remote: `-debugSection weather -debugWeatherRadarFullScreen YES`.
        .onChange(of: radar.info != nil) { _, ready in
            if ready, UserDefaults.standard.bool(forKey: "debugWeatherRadarFullScreen") { radarFullScreen = true }
        }
        #endif
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
                         + "Set the house's location under Settings → Location on the dashboard, iPhone, Mac or Android app. The forecast comes from the US National Weather Service.")
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
                    Text((report.message ?? "The server couldn't reach the National Weather Service.")
                         + " This tab tries again on its own every ten minutes.")
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
        problems(report)
        WeatherStormBanner(radar: radar, zone: zone)
        if let alerts = report.alerts, !alerts.isEmpty {
            VStack(alignment: .leading, spacing: 20) {
                ForEach(alerts) { alert in
                    Button { shownAlert = alert } label: { AlertRow(alert: alert, zone: zone) }
                        .buttonStyle(.card)
                }
            }
        }
        HStack(alignment: .top, spacing: 40) {
            NowCard(report: report, details: details, zone: zone)
                .frame(width: 640)
            if radar.mapMissingOnServer {
                WeatherRadarPictureCard(isActive: isActive, hasStation: report.radar != nil)
            } else {
                WeatherRadarCard(radar: radar, zone: zone) { radarFullScreen = true }
            }
        }
        HStack(alignment: .top, spacing: 40) {
            WeatherStormsCard(radar: radar)
            WeatherOutlookCard(details: details, zone: zone)
            WeatherTotalsCard(details: details)
        }
        .id("cards")
        if let hours = report.hourly, !hours.isEmpty {
            hourly(hours).id("hourly")
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
            .id("days")
        }
        if let discussion = details?.discussion, !discussion.text.isEmpty {
            Button { discussionOpen = true } label: {
                HStack(spacing: 20) {
                    Image(systemName: "text.bubble").font(.title2)
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Forecaster Discussion").font(.headline)
                        Text(discussionMeta(discussion)).font(.callout).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Image(systemName: "chevron.right").foregroundStyle(.secondary)
                }
                .padding(28)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .buttonStyle(.card)
        }
        Text(footer(report))
            .font(.caption)
            .foregroundStyle(.secondary)
            .focusable()
            .id("footer")
    }

    @ViewBuilder private func hourly(_ hours: [WeatherHour]) -> some View {
        let extras = Dictionary((details?.hours ?? []).map { ($0.startTime, $0) }, uniquingKeysWith: { a, _ in a })
        section("Next \(hours.count) Hours") {
            VStack(alignment: .leading, spacing: 8) {
                WeatherHourlyChart(hours: hours, feels: hours.map { extras[$0.startTime]?.feelsLikeF }, zone: zone)
                    .frame(height: 200)
                HStack(spacing: 28) {
                    legendKey(color: SentinelTheme.green, dashed: false, label: "temperature")
                    legendKey(color: SentinelTheme.amber, dashed: true, label: "feels like")
                    HStack(spacing: 8) {
                        RoundedRectangle(cornerRadius: 2).fill(SentinelTheme.cyan.opacity(0.5)).frame(width: 18, height: 12)
                        Text("chance of rain")
                    }
                }
                .font(.caption)
                .foregroundStyle(.secondary)
                ScrollView(.horizontal) {
                    LazyHStack(spacing: 24) {
                        ForEach(Array(hours.enumerated()), id: \.element.id) { index, hour in
                            let newDay = index == 0 || !sameDay(hours[index - 1].startTime, hour.startTime)
                            Button { shownHour = hour } label: {
                                HourCell(hour: hour, extra: extras[hour.startTime], dayLabel: newDay ? weekday(hour.startTime) : nil, zone: zone)
                            }
                            .buttonStyle(.card)
                        }
                    }
                    .padding(.vertical, 20)
                }
                .scrollClipDisabled()
            }
        }
    }

    // MARK: - Pieces

    // Where this forecast is for - the house's name for it, the nearest town, the coordinates
    // and the forecast office - so it's plain at a glance which place the TV is showing.
    private func header(_ report: WeatherReport) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            if let location = report.location {
                Label(location.displayName.uppercased(), systemImage: "location.fill")
                    .font(.title2.bold())
                    .foregroundStyle(SentinelTheme.green)
                Text(placeMeta(location))
                    .font(.callout)
                    .foregroundStyle(.secondary)
            } else {
                Text("Weather").font(.title2.bold())
            }
            if let fetched = report.fetchedAt {
                Text(updatedText(fetched, stale: report.stale == true))
                    .font(.callout)
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
        if let station = location.radarStation { parts.append("radar \(station)") }
        return parts.joined(separator: " · ")
    }

    private func updatedText(_ fetched: Date, stale: Bool) -> String {
        var text = "Updated \(fetched.formatted(timeStyle))"
        if stale { text += " · showing the last answer weather.gov gave" }
        if refreshFailed { text += " · refresh failed, showing the last answer" }
        return text
    }

    @ViewBuilder private func problems(_ report: WeatherReport) -> some View {
        let all = (report.problems ?? []) + (details?.problems ?? [])
        if !all.isEmpty {
            VStack(alignment: .leading, spacing: 6) {
                ForEach(all, id: \.self) { Label($0, systemImage: "exclamationmark.triangle") }
            }
            .font(.callout)
            .foregroundStyle(SentinelTheme.amber)
        }
    }

    private func section<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(title.uppercased())
                .font(.headline)
                .foregroundStyle(.secondary)
            content()
        }
    }

    private func legendKey(color: Color, dashed: Bool, label: String) -> some View {
        HStack(spacing: 8) {
            Path { path in
                path.move(to: CGPoint(x: 0, y: 6))
                path.addLine(to: CGPoint(x: 24, y: 6))
            }
            .stroke(color, style: StrokeStyle(lineWidth: 3, dash: dashed ? [5, 3] : []))
            .frame(width: 24, height: 12)
            Text(label)
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

    private func discussionMeta(_ discussion: WeatherDiscussion) -> String {
        var text = "Why the forecast says what it does, from the \(discussion.office) office"
        if let issued = discussion.issuedAt {
            text += " · issued \(issued.formatted(Date.FormatStyle(timeZone: zone).weekday(.abbreviated).hour().minute()))"
        }
        return text
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
            // Keep showing the last forecast through a blip; only an empty tab shows the error.
            if report == nil { self.error = error.localizedDescription } else { refreshFailed = true }
        }
    }
}

private struct TabKey: Equatable {
    let active: Bool
    let server: String
}

private struct WeatherCardTitle: View {
    let title: String
    var body: some View {
        Text(title.uppercased()).font(.headline).foregroundStyle(.secondary)
    }
}

// MARK: - Storm banner

// The banner that matters most on a stormy day: a tracked storm on course for the house, or the
// house inside a warning outline. Choosing a storm moves the map to it.
private struct WeatherStormBanner: View {
    @ObservedObject var radar: WeatherRadarModel
    let zone: TimeZone

    var body: some View {
        let covering = radar.warningsCoveringHome
        let incoming = Array(radar.incomingCells.prefix(3))
        if !covering.isEmpty || !incoming.isEmpty {
            let severe = covering.contains { $0.phenomena == "TO" } || incoming.contains { $0.threat >= 3 }
            let color = severe ? SentinelTheme.red : SentinelTheme.amber
            VStack(alignment: .leading, spacing: 16) {
                ForEach(covering) { warning in
                    Label { Text(warningLine(warning)) } icon: {
                        Image(systemName: "exclamationmark.octagon.fill").foregroundStyle(color)
                    }
                    .padding(24)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(color.opacity(0.16), in: RoundedRectangle(cornerRadius: 16))
                    .focusable()
                }
                ForEach(incoming) { cell in
                    Button { radar.focus(cell) } label: {
                        Label { Text(cellLine(cell)) } icon: {
                            Image(systemName: "cloud.bolt.rain.fill").symbolRenderingMode(.multicolor)
                        }
                        .padding(24)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .background(color.opacity(0.16))
                    }
                    .buttonStyle(.card)
                }
            }
        }
    }

    private func warningLine(_ warning: WeatherWarningArea) -> AttributedString {
        var line = AttributedString("Home is inside a \(warning.name)")
        line.font = .title3.bold()
        var rest = ""
        if let expires = warning.expires {
            rest += " until \(expires.formatted(Date.FormatStyle(date: .omitted, time: .shortened, timeZone: zone)))"
        }
        if !warning.tags.isEmpty { rest += " · \(warning.tags)" }
        return line + AttributedString(rest)
    }

    private func cellLine(_ cell: WeatherStormCell) -> AttributedString {
        var line = AttributedString("\(cell.title) heading this way")
        line.font = .title3.bold()
        let passing = (cell.closestMiles ?? 0) < 1 ? "overhead" : String(format: "%.1f mi away", cell.closestMiles ?? 0)
        var rest = " · about \(cell.etaMinutes ?? 0) min out, passing \(passing)"
        if let distance = cell.distanceMiles { rest += " · now \(Int(distance.rounded())) mi \(cell.bearingCompass ?? "")" }
        if !cell.hazards.isEmpty { rest += " · \(cell.hazards)" }
        return line + AttributedString(rest)
    }
}

// MARK: - Radar

// The radar map card: the map centred on home with its time stamp and colour key, and the loop,
// home, zoom, layer and overlay controls under it. Choosing the map opens it full screen.
private struct WeatherRadarCard: View {
    @EnvironmentObject private var model: AppModel
    @ObservedObject var radar: WeatherRadarModel
    let zone: TimeZone
    let openFullScreen: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            WeatherCardTitle(title: radarTitle(radar))
                .lineLimit(1)
            if let error = radar.loadError, radar.info == nil {
                ContentUnavailableView {
                    Label("Radar Unavailable", systemImage: "dot.radiowaves.left.and.right")
                } description: {
                    Text(error)
                } actions: {
                    Button("Try Again") { Task { await radar.refresh() } }
                }
                .frame(height: 520)
            } else {
                Button(action: openFullScreen) {
                    WeatherMapFrame(radar: radar, zone: zone, serverURL: model.activeServerURL)
                        .frame(height: 520)
                }
                .buttonStyle(.card)
                WeatherRadarControls(radar: radar)
                WeatherRadarLegend(radar: radar)
            }
        }
        .frame(maxWidth: .infinity)
    }
}

@MainActor private func radarTitle(_ radar: WeatherRadarModel) -> String {
    guard let layer = radar.currentLayer else { return "Radar" }
    return "Radar · \(layer.isStation ? "\(radar.station ?? "") " : "")\(layer.name)"
}

// The map with what sits on it: the frame's time and the storm picked from the list.
private struct WeatherMapFrame: View {
    @ObservedObject var radar: WeatherRadarModel
    let zone: TimeZone
    let serverURL: String

    var body: some View {
        WeatherRadarMapView(radar: radar, serverRoot: URL(string: serverURL.trimmingCharacters(in: .whitespacesAndNewlines)))
            .overlay(alignment: .topTrailing) { timeBadge.padding(16) }
            .overlay(alignment: .bottomLeading) {
                if case .cell(let cell) = radar.selection {
                    WeatherCellCard(cell: cell, zone: zone).padding(16)
                }
            }
    }

    private var timeBadge: some View {
        Group {
            if let time = radar.frameTime {
                let minutes = Int(Date().timeIntervalSince(time) / 60)
                Text("\(time.formatted(Date.FormatStyle(date: .omitted, time: .shortened, timeZone: zone))) · \(minutes <= 1 ? "just now" : "\(minutes) min ago")")
            } else {
                Text("latest")
            }
        }
        .font(.callout.monospacedDigit().weight(.semibold))
        .foregroundStyle(radar.isOldFrame ? SentinelTheme.amber : .primary)
        .padding(.horizontal, 14)
        .padding(.vertical, 6)
        .background(.regularMaterial, in: Capsule())
    }
}

private struct WeatherCellCard: View {
    let cell: WeatherStormCell
    let zone: TimeZone

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 10) {
                Circle().fill(cell.threatColor).frame(width: 16, height: 16)
                Text(cell.title).font(.headline)
            }
            Text(lines.joined(separator: "\n")).font(.caption)
        }
        .padding(18)
        .frame(maxWidth: 520, alignment: .leading)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 14))
    }

    private var lines: [String] {
        var lines = ["\(Int((cell.distanceMiles ?? 0).rounded())) mi \(cell.bearingCompass ?? "") of home · \(cell.motion)"]
        if cell.towardHome, let eta = cell.etaMinutes {
            let passing = (cell.closestMiles ?? 0) < 1 ? "overhead" : String(format: "%.1f mi away", cell.closestMiles ?? 0)
            lines.append("Heading this way: about \(eta) min out, passing \(passing)")
        }
        if !cell.hazards.isEmpty { lines.append(cell.hazards.prefix(1).uppercased() + cell.hazards.dropFirst()) }
        var radar = ["Radar \(cell.radar) · cell \(cell.stormId)"]
        if let tops = cell.topKft { radar.append("tops \(Int(tops.rounded()))k ft") }
        if let dbz = cell.maxDbz { radar.append("\(dbz) dBZ") }
        lines.append(radar.joined(separator: " · "))
        return lines
    }
}

// Loop, home, zoom, the layer and what's drawn on top - buttons, since the remote can't pinch
// or drag the map.
private struct WeatherRadarControls: View {
    @ObservedObject var radar: WeatherRadarModel

    var body: some View {
        HStack(spacing: 20) {
            Button { radar.togglePlay() } label: {
                ControlLabel(Image(systemName: radar.playing ? "pause.fill" : "play.fill"))
            }
            .disabled(radar.frames.count < 2)
            .accessibilityLabel(radar.playing ? "Pause the loop" : "Play the loop")
            Button { radar.goHome() } label: { ControlLabel(Image(systemName: "house.fill")) }
                .accessibilityLabel("Back to home")
            Button { radar.zoom(by: -1) } label: { ControlLabel(Image(systemName: "minus.magnifyingglass")) }
                .accessibilityLabel("Zoom out")
            Button { radar.zoom(by: 1) } label: { ControlLabel(Image(systemName: "plus.magnifyingglass")) }
                .accessibilityLabel("Zoom in")
            Spacer(minLength: 0)
            Menu {
                ForEach(radar.info?.layers ?? []) { layer in
                    Button {
                        radar.selectLayer(layer.id)
                    } label: {
                        if layer.id == radar.currentLayer?.id {
                            Label(layer.name, systemImage: "checkmark")
                        } else {
                            Text(layer.name)
                        }
                    }
                }
            } label: {
                ControlLabel(Label(radar.currentLayer?.name ?? "Layer", systemImage: "square.3.layers.3d").lineLimit(1))
            }
            Menu {
                toggle("Storm cells & tracks", isOn: radar.showCells) { radar.showCells.toggle() }
                toggle("Warning outlines", isOn: radar.showWarnings) { radar.showWarnings.toggle() }
                toggle("SPC outlook today", isOn: radar.showOutlook) { radar.showOutlook.toggle() }
                Divider()
                ForEach([0.5, 0.75, 1.0], id: \.self) { value in
                    toggle("Radar \(Int(value * 100))% opaque", isOn: radar.opacity == value) { radar.opacity = value }
                }
            } label: {
                ControlLabel(Image(systemName: "slider.horizontal.3"))
            }
            .accessibilityLabel("What the map shows")
        }
        .font(.callout)
    }

    private func toggle(_ title: String, isOn: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            if isOn { Label(title, systemImage: "checkmark") } else { Text(title) }
        }
    }
}

// Under the app's green tint a plain tvOS button draws its label in the tint too - a blank green
// pill - and a white label vanishes once focus turns the pill white. So: white, dark when focused.
private struct ControlLabel<Content: View>: View {
    let content: Content

    init(_ content: Content) { self.content = content }

    var body: some View {
        content.focusContrastForeground(unfocused: .white)
    }
}

private struct WeatherRadarLegend: View {
    @ObservedObject var radar: WeatherRadarModel

    var body: some View {
        if let data = radar.legend, let image = UIImage(data: data) {
            VStack(alignment: .leading, spacing: 6) {
                Image(uiImage: image)
                    .resizable()
                    .interpolation(.high)
                    .aspectRatio(contentMode: .fit)
                    .frame(maxHeight: 40)
                if let layer = radar.currentLayer, !layer.description.isEmpty {
                    Text(layer.description).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                }
            }
        }
    }
}

private struct WeatherRadarFullScreen: View {
    @EnvironmentObject private var model: AppModel
    @ObservedObject var radar: WeatherRadarModel
    let zone: TimeZone

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            HStack {
                Text(radarTitle(radar)).font(.title3.bold())
                Spacer()
                Text("Press Menu to close").font(.caption).foregroundStyle(.secondary)
            }
            WeatherMapFrame(radar: radar, zone: zone, serverURL: model.activeServerURL)
                .clipShape(RoundedRectangle(cornerRadius: 20))
                .frame(maxHeight: .infinity)
            HStack(alignment: .center, spacing: 40) {
                WeatherRadarControls(radar: radar)
                WeatherRadarLegend(radar: radar).frame(maxWidth: 700)
            }
        }
        .padding(50)
        .background(Color.black.ignoresSafeArea())
    }
}

// An older server without the radar map: the NWS's own animated picture, local or national.
private struct WeatherRadarPictureCard: View {
    @EnvironmentObject private var model: AppModel
    let isActive: Bool
    let hasStation: Bool

    @State private var scope = "local"
    @State private var kind = "loop"
    @State private var picture: UIImage?
    @State private var pictureError: String?
    @State private var fullScreen = false

    private static let refreshInterval = Duration.seconds(2 * 60)

    // No local radar station for this point (rare - some territories) means the national map.
    private var effectiveScope: String { hasStation ? scope : "national" }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                WeatherCardTitle(title: "Radar").fixedSize()
                Spacer()
                Picker("Area", selection: $scope) {
                    Text("Local").tag("local")
                    Text("National").tag("national")
                }
                .pickerStyle(.segmented)
                .frame(width: 320)
                .disabled(!hasStation)
                Picker("Motion", selection: $kind) {
                    Text("Loop").tag("loop")
                    Text("Still").tag("still")
                }
                .pickerStyle(.segmented)
                .frame(width: 260)
            }
            Button { if picture != nil { fullScreen = true } } label: {
                ZStack {
                    Color.black
                    if let picture {
                        AnimatedImageView(image: picture)
                    } else if let pictureError {
                        Text(pictureError).font(.callout).foregroundStyle(.secondary).padding()
                    } else {
                        ProgressView()
                    }
                }
                .frame(height: 520)
            }
            .buttonStyle(.card)
        }
        .frame(maxWidth: .infinity)
        .task(id: PictureRequest(active: isActive, scope: effectiveScope, kind: kind)) {
            guard isActive else { return }
            while !Task.isCancelled {
                await load()
                try? await Task.sleep(for: Self.refreshInterval)
            }
        }
        .fullScreenCover(isPresented: $fullScreen) {
            ZStack {
                Color.black.ignoresSafeArea()
                if let picture { AnimatedImageView(image: picture).padding(40) }
            }
            // Something focusable so Menu on the remote dismisses the cover.
            .focusable()
        }
    }

    private func load() async {
        do {
            let data = try await model.client.weatherRadar(scope: effectiveScope, kind: kind)
            if Task.isCancelled { return }
            if let image = await AnimatedImageView.decode(data) {
                picture = image
                pictureError = nil
            } else {
                pictureError = "The radar picture couldn't be read."
            }
        } catch {
            if error is CancellationError || (error as? URLError)?.code == .cancelled { return }
            if picture == nil { pictureError = error.localizedDescription }
        }
    }
}

private struct PictureRequest: Equatable {
    let active: Bool
    let scope: String
    let kind: String
}

// MARK: - Cards under the map

private struct WeatherStormsCard: View {
    @ObservedObject var radar: WeatherRadarModel

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            WeatherCardTitle(title: "Storms near home")
            // Four rather than the phone's six: the TV's rows are tall and this card sets the
            // height of the row it sits in.
            let cells = Array(radar.nearbyCells.prefix(4))
            if radar.mapMissingOnServer {
                Text("Needs a newer server.").font(.callout).foregroundStyle(.secondary).focusable()
            } else if radar.storms == nil {
                Text("loading…").font(.callout).foregroundStyle(.secondary).focusable()
            } else if cells.isEmpty {
                Text("No storms tracked within 100 miles.").font(.callout).foregroundStyle(.secondary).focusable()
            } else {
                ForEach(cells) { cell in
                    Button { radar.focus(cell) } label: { row(cell) }
                        .buttonStyle(.card)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .topLeading)
        .sentinelCard()
    }

    private func row(_ cell: WeatherStormCell) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 14) {
            Circle().fill(cell.threatColor).frame(width: 16, height: 16)
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 10) {
                    Text(cell.title).font(.callout.weight(.semibold))
                    if cell.towardHome, let eta = cell.etaMinutes {
                        Text("~\(eta) min")
                            .font(.caption.bold())
                            .padding(.horizontal, 8)
                            .background(SentinelTheme.red.opacity(0.2), in: Capsule())
                            .foregroundStyle(SentinelTheme.red)
                    }
                }
                Text(meta(cell)).font(.caption).foregroundStyle(.secondary).lineLimit(2)
            }
            Spacer(minLength: 0)
        }
        .padding(16)
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
        VStack(alignment: .leading, spacing: 14) {
            WeatherCardTitle(title: "Severe outlook")
            if let severe = details?.severe {
                ForEach(severe.days) { day in
                    HStack(spacing: 14) {
                        Text(day.category ?? "NONE")
                            .font(.caption.bold())
                            .frame(minWidth: 80)
                            .padding(.vertical, 4)
                            .foregroundStyle(day.category == nil ? Color.secondary : day.category == "MDT" ? .white : .black)
                            .background(day.category == nil ? Color.clear : day.chipColor, in: RoundedRectangle(cornerRadius: 6))
                            .overlay(RoundedRectangle(cornerRadius: 6).stroke(day.category == nil ? Color.secondary.opacity(0.5) : .clear))
                        VStack(alignment: .leading, spacing: 2) {
                            Text(dayName(day.day)).font(.callout.weight(.semibold))
                            Text(day.scaleText).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
                HStack(spacing: 28) {
                    hazard("Tornado", severe.tornadoPercent, severe.significantTornado)
                    hazard("Hail", severe.hailPercent, severe.significantHail)
                    hazard("Wind", severe.windPercent, severe.significantWind)
                }
                .padding(.top, 4)
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
        .frame(maxWidth: .infinity, alignment: .topLeading)
        .sentinelCard()
        .focusable()
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
            HStack(spacing: 6) {
                Text("\(percent ?? 0)%").font(.callout.bold().monospacedDigit())
                if significant {
                    Text("sig")
                        .font(.caption2.bold())
                        .padding(.horizontal, 6)
                        .background(SentinelTheme.red.opacity(0.2), in: Capsule())
                        .foregroundStyle(SentinelTheme.red)
                }
            }
        }
    }
}

private struct WeatherTotalsCard: View {
    let details: WeatherDetails?

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            WeatherCardTitle(title: "Rain & snow ahead")
            if let totals = details?.totals {
                Grid(alignment: .leadingFirstTextBaseline, horizontalSpacing: 24, verticalSpacing: 8) {
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
        .frame(maxWidth: .infinity, alignment: .topLeading)
        .sentinelCard()
        .focusable()
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

// MARK: - Now, hours, days

private struct AlertRow: View {
    let alert: WeatherAlert
    let zone: TimeZone

    var body: some View {
        HStack(alignment: .top, spacing: 24) {
            Image(systemName: "exclamationmark.triangle.fill")
                .font(.title2)
                .foregroundStyle(alert.color)
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text(alert.event).font(.title3.bold()).foregroundStyle(alert.color)
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
        .background(alert.color.opacity(0.14))
    }
}

private struct NowCard: View {
    let report: WeatherReport
    let details: WeatherDetails?
    let zone: TimeZone

    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            Text("NOW").font(.headline).foregroundStyle(.secondary)
            let next = WeatherText.nextPrecipitation(report.hourly ?? [], zone: zone)
            if let current = report.current {
                HStack(spacing: 28) {
                    Image(systemName: WeatherSymbol.name(current.icon, isDaytime: current.isDaytime))
                        .symbolRenderingMode(.multicolor)
                        .font(.system(size: 100))
                    VStack(alignment: .leading, spacing: 4) {
                        Text("\(current.temperatureF)°F")
                            .font(.system(size: 80, weight: .bold, design: .rounded))
                        Text(current.description.isEmpty ? "—" : current.description)
                            .font(.title3)
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
                Grid(alignment: .leading, horizontalSpacing: 28, verticalSpacing: 8) {
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
            } else {
                Text("No current conditions from the nearest station.").foregroundStyle(.secondary)
                if let next {
                    Label(next, systemImage: "umbrella").font(.callout).foregroundStyle(SentinelTheme.cyan)
                }
            }
        }
        .padding(32)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 20))
        .focusable()
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

// Temperature and feels-like lines over chance-of-rain bars, with day dividers and hour labels -
// the iPhone's chart (a port of the dashboard's hourly SVG), stretched to the screen's width
// instead of scrolling.
private struct WeatherHourlyChart: View {
    let hours: [WeatherHour]
    let feels: [Int?]
    let zone: TimeZone

    var body: some View {
        Canvas { context, size in
            let temps = hours.compactMap(\.temperatureF)
            guard temps.count >= 2 else { return }
            let step = size.width / CGFloat(hours.count)
            let top: CGFloat = 34, tempBottom: CGFloat = 120, barBottom: CGFloat = 172
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
                let height = CGFloat(chance) / 100 * 44
                let bar = CGRect(x: x(index) - step * 0.35, y: barBottom - height, width: step * 0.7, height: height)
                context.fill(Path(roundedRect: bar, cornerRadius: 2), with: .color(SentinelTheme.cyan.opacity(0.45)))
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
                               style: StrokeStyle(lineWidth: 2.5, lineCap: .round, lineJoin: .round, dash: [6, 4]))
            }
            context.stroke(line(hours.map(\.temperatureF)), with: .color(SentinelTheme.green),
                           style: StrokeStyle(lineWidth: 4, lineCap: .round, lineJoin: .round))

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
                    context.stroke(divider, with: .color(.secondary.opacity(0.35)), style: StrokeStyle(lineWidth: 1, dash: [3, 4]))
                    context.draw(Text(hour.startTime.formatted(weekday).uppercased()).font(.system(size: 18, weight: .bold)).foregroundStyle(SentinelTheme.green),
                                 at: CGPoint(x: left + 8, y: 14), anchor: .leading)
                }
                if index % 3 == 0 {
                    context.draw(Text(hour.startTime.formatted(hourFormat)).font(.system(size: 16)).foregroundStyle(.secondary),
                                 at: CGPoint(x: x(index), y: size.height - 10))
                    if let temp = hour.temperatureF {
                        context.draw(Text("\(temp)°").font(.system(size: 19, weight: .semibold)),
                                     at: CGPoint(x: x(index), y: y(temp) - 16))
                    }
                }
            }
        }
        .accessibilityLabel("Temperature and chance of precipitation for the next \(hours.count) hours")
    }
}

private struct HourCell: View {
    let hour: WeatherHour
    let extra: WeatherHourDetail?
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
            Text(windText)
                .font(.caption2)
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .minimumScaleFactor(0.7)
        }
        .frame(width: 130)
        .padding(.vertical, 18)
    }

    private var chanceText: String {
        guard let chance = hour.precipitationChance, chance > 0 else { return " " }
        return "\(chance)%"
    }

    private var windText: String {
        let speed = hour.windSpeed?.replacingOccurrences(of: " mph", with: "") ?? ""
        let text = [hour.windDirection ?? "", speed].filter { !$0.isEmpty }.joined(separator: " ")
        return text.isEmpty ? " " : text
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

// MARK: - Sheets

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
                    let wind = [period.windDirection, period.windSpeed].compactMap { $0 }.joined(separator: " ")
                    if !wind.isEmpty {
                        Label("Wind \(wind)", systemImage: "wind").font(.callout).foregroundStyle(.secondary)
                    }
                }
                .focusable()
            }
        }
        .padding(60)
        .frame(maxWidth: 1200, alignment: .leading)
    }
}

private struct WeatherHourSheet: View {
    let hour: WeatherHour
    let extra: WeatherHourDetail?
    let zone: TimeZone

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Label(hour.startTime.formatted(Date.FormatStyle(timeZone: zone).weekday(.wide).hour()),
                  systemImage: WeatherSymbol.name(hour.icon, isDaytime: hour.isDaytime))
                .font(.title3.bold())
                .symbolRenderingMode(.multicolor)
            ForEach(lines, id: \.self) { Text($0) }
        }
        .padding(60)
        .frame(minWidth: 700, alignment: .leading)
        .focusable()
    }

    private var lines: [String] {
        let wind = [hour.windDirection, hour.windSpeed].compactMap { $0 }.joined(separator: " ")
        let lines: [String?] = [
            hour.temperatureF.map { "\($0)°F" },
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

// The forecasters' notes, kept in the NWS's own fixed-width lines and split at its blank lines
// so the remote can scroll through them a block at a time.
private struct WeatherDiscussionSheet: View {
    let discussion: WeatherDiscussion
    let zone: TimeZone

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                Text("Forecaster Discussion · \(discussion.office)").font(.title2.bold())
                if let issued = discussion.issuedAt {
                    Text("Issued \(issued.formatted(Date.FormatStyle(timeZone: zone).weekday(.wide).hour().minute()))")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
                let blocks = discussion.text.components(separatedBy: "\n\n")
                    .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
                    .filter { !$0.isEmpty && $0 != "&&" }
                ForEach(Array(blocks.enumerated()), id: \.offset) { _, block in
                    Text(block)
                        .font(.system(.callout, design: .monospaced))
                        .fixedSize(horizontal: false, vertical: true)
                        .focusable()
                }
            }
            .padding(60)
        }
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

// The radar picture comes as an animated GIF, which SwiftUI's Image shows as a still. UIImageView
// plays an animated UIImage.
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
