import Foundation
import MapKit
import SwiftUI

// Everything the radar map shows and the controls under it drive: which layer, the frames of the
// loop and which one is on screen, the storm cells and warning outlines for the part of the map
// in view, and the overlay switches. A copy of the iPhone/Mac app's WeatherRadarModel (itself the
// dashboard's RadarMap in weather.js, minus Leaflet), plus zoom steps for the remote, which has
// no pinch: the map itself is an MKMapView (WeatherRadarMapTV) that reads this and draws it.
//
// Refreshes the frame list and the storms every two minutes while the tab is on screen, like the
// dashboard.
@MainActor
final class WeatherRadarModel: ObservableObject {
    enum Selection: Equatable {
        case cell(WeatherStormCell)
        case warning(WeatherWarningArea)
        case site(WeatherRadarSite)
    }

    // A one-shot instruction to the map (fly home, fly to a storm), told apart by its token so
    // asking twice for the same place still moves the map.
    struct MapRequest: Equatable {
        let token = UUID()
        let center: CLLocationCoordinate2D
        let minimumZoom: Double
        // Set by the zoom buttons: this zoom exactly, in or out.
        var zoom: Double? = nil

        static func == (a: MapRequest, b: MapRequest) -> Bool { a.token == b.token }
    }

    static let refreshInterval = Duration.seconds(2 * 60)
    private static let frameInterval = Duration.milliseconds(550)
    private static let lastFrameHold = Duration.milliseconds(1800)

    @Published private(set) var info: WeatherMapInfo?
    @Published private(set) var storms: WeatherStorms?
    @Published private(set) var loadError: String?
    // An older server with /api/weather/radar but no radar map: the section falls back to the
    // NWS's animated picture.
    @Published private(set) var mapMissingOnServer = false
    @Published private(set) var legend: Data?

    @Published private(set) var frames: [String?] = []
    @Published private(set) var frameIndex = 0
    @Published private(set) var playing = false
    // Bumped whenever the set of frames changes (a new layer, radar, or frame list), so the map
    // knows to throw its tile overlays away.
    @Published private(set) var framesVersion = 0
    @Published private(set) var stormsVersion = 0

    @Published var selection: Selection?
    @Published private(set) var request: MapRequest?

    // Per-viewer preferences, remembered on this device the way the dashboard keeps them in
    // localStorage.
    @Published var opacity: Double { didSet { defaults.set(opacity, forKey: Keys.opacity) } }
    @Published var showCells: Bool { didSet { defaults.set(showCells, forKey: Keys.cells) } }
    @Published var showWarnings: Bool { didSet { defaults.set(showWarnings, forKey: Keys.warnings) } }
    @Published var showOutlook: Bool { didSet { defaults.set(showOutlook, forKey: Keys.outlook) } }
    @Published var showSites: Bool { didSet { defaults.set(showSites, forKey: Keys.sites) } }
    @Published private(set) var layerId: String { didSet { defaults.set(layerId, forKey: Keys.layer) } }
    @Published private(set) var stationPreference: String? { didSet { defaults.set(stationPreference, forKey: Keys.station) } }

    // What the map is showing, for the storms request and for which storms are worth drawing.
    private(set) var visibleRegion: MKCoordinateRegion?
    private(set) var zoom: Double = 8

    private var home: WeatherLocation?
    private var client: SentinelClient?
    private var playTask: Task<Void, Never>?
    private var stormsTask: Task<Void, Never>?
    private let defaults = UserDefaults.standard

    private enum Keys {
        static let layer = "weatherMap.layer"
        static let station = "weatherMap.station"
        static let opacity = "weatherMap.opacity"
        static let cells = "weatherMap.cells"
        static let warnings = "weatherMap.warnings"
        static let outlook = "weatherMap.outlook"
        static let sites = "weatherMap.sites"
    }

    init() {
        let defaults = UserDefaults.standard
        layerId = defaults.string(forKey: Keys.layer) ?? "reflectivity"
        stationPreference = defaults.string(forKey: Keys.station)
        opacity = defaults.object(forKey: Keys.opacity) as? Double ?? 0.75
        showCells = defaults.object(forKey: Keys.cells) as? Bool ?? true
        showWarnings = defaults.object(forKey: Keys.warnings) as? Bool ?? true
        showOutlook = defaults.object(forKey: Keys.outlook) as? Bool ?? false
        showSites = defaults.object(forKey: Keys.sites) as? Bool ?? true
    }

    // MARK: - Derived

    var currentLayer: WeatherMapLayer? {
        guard let info else { return nil }
        return info.layers.first { $0.id == layerId } ?? info.layers.first { $0.id == "reflectivity" } ?? info.layers.first
    }

    var station: String? { info?.station }

    var homeCoordinate: CLLocationCoordinate2D? {
        if let home = info?.home { return CLLocationCoordinate2D(latitude: home.latitude, longitude: home.longitude) }
        return home.map { CLLocationCoordinate2D(latitude: $0.latitude, longitude: $0.longitude) }
    }

    var homeLabel: String {
        let label = info?.home?.label ?? home?.displayName ?? "Home"
        return label.uppercased()
    }

    // The radar sites nearest home first, as the dashboard's picker lists them.
    var sitesByDistance: [(site: WeatherRadarSite, miles: Double?)] {
        let sites = info?.stations ?? []
        guard let home = homeCoordinate else { return sites.map { ($0, nil) } }
        let from = CLLocation(latitude: home.latitude, longitude: home.longitude)
        return sites
            .map { ($0, CLLocation(latitude: $0.latitude, longitude: $0.longitude).distance(from: from) / 1609.344) }
            .sorted { ($0.1 ?? 0) < ($1.1 ?? 0) }
    }

    var frameTime: Date? {
        guard frames.indices.contains(frameIndex), let time = frames[frameIndex] else { return nil }
        return Self.parseFrameTime(time)
    }

    var isOldFrame: Bool { frameIndex < frames.count - 1 }

    // "Storms near home": within 100 miles, the ones heading this way and the dangerous ones
    // first - the dashboard's list.
    var nearbyCells: [WeatherStormCell] {
        (storms?.cells ?? [])
            .filter { ($0.distanceMiles ?? .infinity) <= 100 }
            .sorted {
                if $0.towardHome != $1.towardHome { return $0.towardHome }
                if $0.threat != $1.threat { return $0.threat > $1.threat }
                return ($0.distanceMiles ?? 0) < ($1.distanceMiles ?? 0)
            }
            .prefix(6)
            .map { $0 }
    }

    var incomingCells: [WeatherStormCell] {
        (storms?.cells ?? [])
            .filter { $0.towardHome && $0.etaMinutes != nil }
            .sorted { $0.threat != $1.threat ? $0.threat > $1.threat : ($0.etaMinutes ?? 0) < ($1.etaMinutes ?? 0) }
    }

    var warningsCoveringHome: [WeatherWarningArea] {
        (storms?.warnings ?? []).filter { $0.containsHome && $0.isDangerous }
    }

    static func parseFrameTime(_ value: String) -> Date? {
        (try? Date.ISO8601FormatStyle(includingFractionalSeconds: true).parse(value))
            ?? (try? Date.ISO8601FormatStyle().parse(value))
    }

    // MARK: - Loading

    func configure(client: SentinelClient, home: WeatherLocation?) {
        self.client = client
        self.home = home
    }

    func refresh() async {
        await loadInfo()
        await loadStorms()
    }

    func loadInfo() async {
        guard let client else { return }
        do {
            let next = try await client.weatherMap(station: stationPreference)
            let sameFrames = info.map { current in
                current.station == next.station && current.layers.map(\.times) == next.layers.map(\.times)
            } ?? false
            info = next
            loadError = nil
            mapMissingOnServer = false
            if !sameFrames || frames.isEmpty { rebuildFrames() }
        } catch {
            if error is CancellationError || (error as? URLError)?.code == .cancelled { return }
            if case ClientError.server(status: 404, _) = error {
                mapMissingOnServer = true
                return
            }
            if info == nil { loadError = error.localizedDescription }
        }
    }

    // The storms in (a padded) view, plus always home's surroundings so the "near home" list and
    // the banner stay right while someone is looking at a storm three states away.
    func loadStorms() async {
        guard let client else { return }
        var boxes: [(Double, Double, Double, Double)] = []
        if let region = visibleRegion {
            let latPad = region.span.latitudeDelta * 0.8
            let lonPad = region.span.longitudeDelta * 0.8
            boxes.append((region.center.longitude - lonPad, region.center.latitude - latPad,
                          region.center.longitude + lonPad, region.center.latitude + latPad))
        }
        if let home = homeCoordinate {
            boxes.append((home.longitude - 2.5, home.latitude - 2, home.longitude + 2.5, home.latitude + 2))
        }
        guard !boxes.isEmpty else { return }
        do {
            var answers: [WeatherStorms] = []
            for box in boxes {
                answers.append(try await client.weatherStorms(west: box.0, south: box.1, east: box.2, north: box.3))
            }
            guard let view = answers.first else { return }
            var cells: [String: WeatherStormCell] = [:]
            var order: [String] = []
            var warnings: [String: WeatherWarningArea] = [:]
            var warningOrder: [String] = []
            for answer in answers {
                for cell in answer.cells {
                    if cells[cell.id] == nil { order.append(cell.id) }
                    cells[cell.id] = cell
                }
                for warning in answer.warnings {
                    if warnings[warning.id] == nil { warningOrder.append(warning.id) }
                    warnings[warning.id] = warning
                }
            }
            storms = WeatherStorms(
                cells: order.compactMap { cells[$0] },
                warnings: warningOrder.compactMap { warnings[$0] },
                outlook: view.outlook,
                fetchedAt: view.fetchedAt,
                problems: view.problems)
            stormsVersion += 1
            // A storm whose card is open keeps its card current, or loses it once it's gone.
            if case .cell(let open) = selection {
                selection = cells[open.id].map { .cell($0) }
            }
        } catch {
            // The storms are extra; the map still works without them.
        }
    }

    // Called by the map as it moves. The storms request waits for the map to settle.
    func mapMoved(region: MKCoordinateRegion, width: Double) {
        visibleRegion = region
        if region.span.longitudeDelta > 0, width > 0 {
            zoom = log2(360 * width / (region.span.longitudeDelta * 256))
        }
        stormsTask?.cancel()
        stormsTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(400))
            guard !Task.isCancelled else { return }
            await self?.loadStorms()
        }
    }

    // MARK: - Layers and frames

    func selectLayer(_ id: String) {
        guard id != layerId else { return }
        layerId = id
        rebuildFrames()
    }

    func selectStation(_ station: String) async {
        stationPreference = station == info?.homeStation ? nil : station
        if currentLayer?.isStation != true { layerId = "station-reflectivity" }
        selection = nil
        await loadInfo()
        rebuildFrames()
    }

    private func rebuildFrames() {
        let wasPlaying = playing
        stop()
        guard let layer = currentLayer, !(layer.isStation && info?.station == nil) else {
            frames = []
            frameIndex = 0
            framesVersion += 1
            legend = nil
            return
        }
        frames = layer.times.isEmpty ? [nil] : layer.times.map { Optional($0) }
        frameIndex = frames.count - 1
        framesVersion += 1
        Task { await loadLegend() }
        if wasPlaying { play() }
    }

    private func loadLegend() async {
        guard let client, let layer = currentLayer else { return }
        let station = layer.isStation ? info?.station : nil
        legend = try? await client.weatherLegend(layer: layer.id, station: station)
    }

    func showFrame(_ index: Int) {
        guard !frames.isEmpty else { return }
        frameIndex = max(0, min(index, frames.count - 1))
    }

    func scrub(to index: Int) {
        stop()
        showFrame(index)
    }

    func togglePlay() {
        playing ? stop() : play()
    }

    func play() {
        guard frames.count > 1 else { return }
        playing = true
        if frameIndex == frames.count - 1 { showFrame(0) }
        playTask?.cancel()
        playTask = Task { [weak self] in
            try? await Task.sleep(for: Self.frameInterval)
            while !Task.isCancelled, let self, self.playing {
                let next = self.frameIndex >= self.frames.count - 1 ? 0 : self.frameIndex + 1
                self.showFrame(next)
                try? await Task.sleep(for: next == self.frames.count - 1 ? Self.lastFrameHold : Self.frameInterval)
            }
        }
    }

    func stop() {
        playing = false
        playTask?.cancel()
        playTask = nil
    }

    // MARK: - Map requests

    func goHome() {
        guard let home = homeCoordinate else { return }
        request = MapRequest(center: home, minimumZoom: 8)
    }

    // The remote's stand-in for pinching: one zoom level in or out about the middle of the map,
    // kept between the whole country and the radar's sharpest.
    func zoom(by step: Double) {
        guard let center = visibleRegion?.center ?? homeCoordinate else { return }
        let next = min(max(zoom + step, 4), Double(info?.maximumRadarZoom ?? 12) + 1)
        request = MapRequest(center: center, minimumZoom: next, zoom: next)
    }

    // Clicking a storm in the list or the banner flies the map to it and opens its card.
    func focus(_ cell: WeatherStormCell) {
        if !showCells { showCells = true }
        selection = .cell(cell)
        request = MapRequest(center: CLLocationCoordinate2D(latitude: cell.latitude, longitude: cell.longitude), minimumZoom: 9)
    }

    // Cells drawn at this zoom: anything notable, anything heading home, and everything once
    // zoomed in far enough that they don't turn the map into a hairball.
    func cellIsDrawn(_ cell: WeatherStormCell) -> Bool {
        if case .cell(let open) = selection, open.id == cell.id { return true }
        return !(cell.threat == 0 && zoom < 9 && !cell.towardHome)
    }

    func trackIsDrawn(_ cell: WeatherStormCell) -> Bool {
        !cell.track.isEmpty && (cell.threat >= 1 || cell.towardHome || zoom >= 10)
    }
}
