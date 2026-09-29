import ImageIO
import MapKit
import SwiftUI
import UniformTypeIdentifiers
#if os(iOS)
import UIKit
#else
import AppKit
#endif

// The radar map: Apple Maps underneath (it follows light and dark mode by itself), the server's
// radar tiles over the roads and under the place names, and on top the storm cells with their
// forecast tracks, the warning outlines, the SPC outlook, the radar sites and home.
//
// One tile overlay per frame of the loop, all on the map with only the current one visible, so
// each frame's tiles are already loaded by the time it comes round - the dashboard's approach.
// Only the newest frame is added up front; the rest join when the loop is played.
#if os(iOS)
struct WeatherRadarMapView: UIViewRepresentable {
    @ObservedObject var radar: WeatherRadarModel
    let serverRoot: URL?

    func makeCoordinator() -> WeatherRadarMapCoordinator { WeatherRadarMapCoordinator(radar: radar) }

    func makeUIView(context: Context) -> MKMapView {
        context.coordinator.makeMap()
    }

    func updateUIView(_ map: MKMapView, context: Context) {
        context.coordinator.radar = radar
        context.coordinator.sync(map, serverRoot: serverRoot)
    }
}
#else
struct WeatherRadarMapView: NSViewRepresentable {
    @ObservedObject var radar: WeatherRadarModel
    let serverRoot: URL?

    func makeCoordinator() -> WeatherRadarMapCoordinator { WeatherRadarMapCoordinator(radar: radar) }

    func makeNSView(context: Context) -> MKMapView {
        context.coordinator.makeMap()
    }

    func updateNSView(_ map: MKMapView, context: Context) {
        context.coordinator.radar = radar
        context.coordinator.sync(map, serverRoot: serverRoot)
    }
}
#endif

// MARK: - Map shapes

final class RadarFrameOverlay: MKTileOverlay, @unchecked Sendable {
    let root: URL
    let layer: String
    let station: String?
    let time: String?
    let nativeMaximumZ: Int

    init(root: URL, layer: String, station: String?, time: String?, nativeMaximumZ: Int) {
        self.root = root
        self.layer = layer
        self.station = station
        self.time = time
        self.nativeMaximumZ = nativeMaximumZ
        super.init(urlTemplate: nil)
        canReplaceMapContent = false
        tileSize = CGSize(width: 256, height: 256)
        minimumZ = 0
        maximumZ = 20
    }

    // Signed in the same way as everything else: the shared cookie store holds the session the
    // app logged in with. A cache of its own so a loop's frames play from memory the second time
    // round; the server marks dated frames as good for hours.
    static let session: URLSession = {
        let configuration = URLSessionConfiguration.default
        configuration.httpCookieStorage = .shared
        configuration.httpShouldSetCookies = true
        configuration.timeoutIntervalForRequest = 20
        configuration.waitsForConnectivity = false
        configuration.httpMaximumConnectionsPerHost = 6
        configuration.urlCache = URLCache(memoryCapacity: 48 * 1024 * 1024, diskCapacity: 96 * 1024 * 1024,
                                          directory: FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first?
                                              .appendingPathComponent("WeatherRadarTiles"))
        configuration.requestCachePolicy = .useProtocolCachePolicy
        return URLSession(configuration: configuration)
    }()

    override func url(forTilePath path: MKTileOverlayPath) -> URL {
        tileURL(z: path.z, x: path.x, y: path.y)
    }

    private func tileURL(z: Int, x: Int, y: Int) -> URL {
        var components = URLComponents(url: root, resolvingAgainstBaseURL: false) ?? URLComponents()
        components.path = "/api/weather/map/tile/\(layer)/\(z)/\(x)/\(y)"
        var query: [URLQueryItem] = []
        if let station { query.append(URLQueryItem(name: "station", value: station)) }
        if let time { query.append(URLQueryItem(name: "time", value: time)) }
        components.queryItems = query.isEmpty ? nil : query
        return components.url ?? root
    }

    // Past the radar's own resolution (zoom 12) the server refuses the tile, so a deeper one is
    // cut out of its zoom-12 ancestor and scaled up - what Leaflet's maxNativeZoom does on the
    // dashboard.
    override func loadTile(at path: MKTileOverlayPath, result: @escaping (Data?, (any Error)?) -> Void) {
        let reply = TileReply(result)
        let depth = path.z - nativeMaximumZ
        if depth <= 0 {
            fetch(tileURL(z: path.z, x: path.x, y: path.y)) { data, error in reply.send(data, error) }
            return
        }
        let parentX = path.x >> depth
        let parentY = path.y >> depth
        let span = 1 << depth
        let offsetX = path.x - parentX * span
        let offsetY = path.y - parentY * span
        fetch(tileURL(z: nativeMaximumZ, x: parentX, y: parentY)) { data, error in
            guard let data else { return reply.send(nil, error) }
            reply.send(Self.crop(data, span: span, column: offsetX, row: offsetY), nil)
        }
    }

    private func fetch(_ url: URL, completion: @escaping @Sendable (Data?, (any Error)?) -> Void) {
        Self.session.dataTask(with: url) { data, response, error in
            if let http = response as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
                // A missing tile is a blank square, not an error MapKit should keep retrying.
                completion(nil, nil)
                return
            }
            completion(data, error)
        }.resume()
    }

    private static func crop(_ data: Data, span: Int, column: Int, row: Int) -> Data? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else { return nil }
        let piece = CGFloat(image.width) / CGFloat(span)
        let rect = CGRect(x: CGFloat(column) * piece, y: CGFloat(row) * piece, width: piece, height: piece).integral
        guard let cropped = image.cropping(to: rect),
              let context = CGContext(data: nil, width: 256, height: 256, bitsPerComponent: 8, bytesPerRow: 0,
                                      space: CGColorSpaceCreateDeviceRGB(),
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        context.interpolationQuality = .medium
        context.draw(cropped, in: CGRect(x: 0, y: 0, width: 256, height: 256))
        guard let scaled = context.makeImage() else { return nil }
        let output = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(output, UTType.png.identifier as CFString, 1, nil) else { return nil }
        CGImageDestinationAddImage(destination, scaled, nil)
        guard CGImageDestinationFinalize(destination) else { return nil }
        return output as Data
    }
}

// MapKit's tile callback isn't Sendable; it is safe to call from any thread.
private final class TileReply: @unchecked Sendable {
    private let result: (Data?, (any Error)?) -> Void
    init(_ result: @escaping (Data?, (any Error)?) -> Void) { self.result = result }
    func send(_ data: Data?, _ error: (any Error)?) { result(data, error) }
}

final class WarningPolygon: MKPolygon {
    var warning: WeatherWarningArea?
}

final class OutlookPolygon: MKPolygon {
    var fillHex = "#888888"
    var strokeHex = "#888888"
}

final class TrackLine: MKPolyline {
    var threat = 0
    var incoming = false
}

final class HomePin: MKPointAnnotation {}

final class CellPin: MKPointAnnotation {
    var cell: WeatherStormCell?
}

final class SitePin: MKPointAnnotation {
    var site: WeatherRadarSite?
    var selected = false
}

// MARK: - Coordinator

@MainActor
final class WeatherRadarMapCoordinator: NSObject, MKMapViewDelegate {
    var radar: WeatherRadarModel

    private var frameOverlays: [RadarFrameOverlay?] = []
    private var framesKey = ""
    private var shownFrame = -1
    private var shownOpacity = -1.0
    private var stormsKey = ""
    private var sitesKey = ""
    private var lastRequest: UUID?
    private var centeredOnHome = false
    private var home: HomePin?
    private var markerImages: [String: PlatformImage] = [:]

    init(radar: WeatherRadarModel) {
        self.radar = radar
    }

    func makeMap() -> MKMapView {
        let map = MKMapView()
        map.delegate = self
        map.pointOfInterestFilter = .excludingAll
        map.showsCompass = true
        map.showsScale = true
        #if os(iOS)
        map.preferredConfiguration = MKStandardMapConfiguration(elevationStyle: .flat, emphasisStyle: .muted)
        map.isPitchEnabled = false
        let tap = UITapGestureRecognizer(target: self, action: #selector(tapped(_:)))
        tap.cancelsTouchesInView = false
        tap.delegate = self
        map.addGestureRecognizer(tap)
        #else
        map.preferredConfiguration = MKStandardMapConfiguration(elevationStyle: .flat, emphasisStyle: .muted)
        map.isPitchEnabled = false
        map.showsZoomControls = true
        let click = NSClickGestureRecognizer(target: self, action: #selector(clicked(_:)))
        click.delaysPrimaryMouseButtonEvents = false
        map.addGestureRecognizer(click)
        #endif
        // The lower 48 until home is known.
        map.setRegion(MKCoordinateRegion(center: CLLocationCoordinate2D(latitude: 39.5, longitude: -98.35),
                                         span: MKCoordinateSpan(latitudeDelta: 24, longitudeDelta: 50)), animated: false)
        return map
    }

    // Brings the map in line with the model. Called on every model change - including every
    // frame of a playing loop - so each part only does work when its own inputs changed.
    func sync(_ map: MKMapView, serverRoot: URL?) {
        syncHome(map)
        syncFrames(map, serverRoot: serverRoot)
        syncStorms(map)
        syncSites(map)
        if let request = radar.request, request.token != lastRequest {
            lastRequest = request.token
            fly(map, to: request.center, minimumZoom: request.minimumZoom)
        }
    }

    // MARK: Home

    private func syncHome(_ map: MKMapView) {
        guard let coordinate = radar.homeCoordinate else { return }
        if home == nil {
            let pin = HomePin()
            home = pin
            map.addAnnotation(pin)
        }
        if home?.coordinate.latitude != coordinate.latitude || home?.coordinate.longitude != coordinate.longitude {
            home?.coordinate = coordinate
        }
        home?.title = radar.homeLabel
        if !centeredOnHome {
            centeredOnHome = true
            map.setRegion(MKCoordinateRegion(center: coordinate, span: MKCoordinateSpan(latitudeDelta: 2.6, longitudeDelta: 3.4)), animated: false)
        }
    }

    private func fly(_ map: MKMapView, to center: CLLocationCoordinate2D, minimumZoom: Double) {
        let width = Double(map.bounds.width > 0 ? map.bounds.width : 600)
        let wanted = max(radar.zoom, minimumZoom)
        let lonDelta = 360 * width / (pow(2, wanted) * 256)
        let latDelta = lonDelta * cos(center.latitude * .pi / 180) * Double(map.bounds.height > 0 ? map.bounds.height / map.bounds.width : 0.75)
        map.setRegion(MKCoordinateRegion(center: center, span: MKCoordinateSpan(latitudeDelta: max(latDelta, 0.01), longitudeDelta: lonDelta)), animated: true)
    }

    // MARK: Frames

    private func syncFrames(_ map: MKMapView, serverRoot: URL?) {
        let layer = radar.currentLayer
        let key = "\(serverRoot?.absoluteString ?? "")|\(radar.framesVersion)|\(layer?.id ?? "")|\(radar.station ?? "")"
        if key != framesKey {
            framesKey = key
            map.removeOverlays(frameOverlays.compactMap { $0 })
            frameOverlays = Array(repeating: nil, count: radar.frames.count)
            shownFrame = -1
        }
        guard let serverRoot, let layer, !radar.frames.isEmpty else { return }

        if radar.playing {
            for index in radar.frames.indices { ensureFrame(index, map: map, root: serverRoot, layer: layer) }
        }
        ensureFrame(radar.frameIndex, map: map, root: serverRoot, layer: layer)

        guard shownFrame != radar.frameIndex || shownOpacity != radar.opacity else { return }
        shownFrame = radar.frameIndex
        shownOpacity = radar.opacity
        for (index, overlay) in frameOverlays.enumerated() {
            guard let overlay, let renderer = map.renderer(for: overlay) else { continue }
            renderer.alpha = index == radar.frameIndex ? radar.opacity : 0
        }
    }

    private func ensureFrame(_ index: Int, map: MKMapView, root: URL, layer: WeatherMapLayer) {
        guard frameOverlays.indices.contains(index), frameOverlays[index] == nil else { return }
        let overlay = RadarFrameOverlay(
            root: root, layer: layer.id,
            station: layer.isStation ? radar.station : nil,
            time: radar.frames[index],
            nativeMaximumZ: radar.info?.maximumRadarZoom ?? 12)
        frameOverlays[index] = overlay
        // Over the roads, under the place names, like the dashboard's label pane. Frames are
        // kept above the outlook, which is inserted at the bottom of the same level.
        map.addOverlay(overlay, level: .aboveRoads)
        shownFrame = -1
    }

    // MARK: Storms, warnings, outlook

    private func syncStorms(_ map: MKMapView) {
        let selectedCell: String? = if case .cell(let cell) = radar.selection { cell.id } else { nil }
        let drawn = (radar.storms?.cells ?? []).filter { radar.showCells && radar.cellIsDrawn($0) }
        let tracks = drawn.filter { radar.trackIsDrawn($0) }.map(\.id)
        let key = [
            "\(radar.stormsVersion)", "\(radar.showCells)", "\(radar.showWarnings)", "\(radar.showOutlook)",
            drawn.map(\.id).joined(separator: ","), tracks.joined(separator: ","), selectedCell ?? "",
        ].joined(separator: "|")
        guard key != stormsKey else { return }
        stormsKey = key

        map.removeAnnotations(map.annotations.filter { $0 is CellPin })
        map.removeOverlays(map.overlays.filter { $0 is WarningPolygon || $0 is OutlookPolygon || $0 is TrackLine })

        if radar.showOutlook {
            for area in (radar.storms?.outlook?.areas ?? []).sorted(by: { $0.rank < $1.rank }) {
                for polygon in area.polygons {
                    guard let (outer, holes) = Self.rings(polygon) else { continue }
                    let shape = OutlookPolygon(coordinates: outer, count: outer.count, interiorPolygons: holes)
                    shape.fillHex = area.fill ?? "#888888"
                    shape.strokeHex = area.stroke ?? area.fill ?? "#888888"
                    shape.title = "SPC today: \(area.name)"
                    map.insertOverlay(shape, at: 0, level: .aboveRoads)
                }
            }
        }

        if radar.showWarnings {
            for warning in (radar.storms?.warnings ?? []).sorted(by: { $0.drawOrder < $1.drawOrder }) {
                for polygon in warning.polygons {
                    guard let (outer, holes) = Self.rings(polygon) else { continue }
                    let shape = WarningPolygon(coordinates: outer, count: outer.count, interiorPolygons: holes)
                    shape.warning = warning
                    map.addOverlay(shape, level: .aboveLabels)
                }
            }
        }

        guard radar.showCells else { return }
        for cell in drawn {
            if radar.trackIsDrawn(cell) {
                var points = [CLLocationCoordinate2D(latitude: cell.latitude, longitude: cell.longitude)]
                points += cell.track.map { CLLocationCoordinate2D(latitude: $0.latitude, longitude: $0.longitude) }
                let line = TrackLine(coordinates: points, count: points.count)
                line.threat = cell.threat
                line.incoming = cell.towardHome
                map.addOverlay(line, level: .aboveLabels)
            }
            let pin = CellPin()
            pin.cell = cell
            pin.coordinate = CLLocationCoordinate2D(latitude: cell.latitude, longitude: cell.longitude)
            pin.title = cell.title
            map.addAnnotation(pin)
        }
    }

    // One GeoJSON polygon - rings of [lon, lat], the first the outline and the rest holes.
    private static func rings(_ rings: [[[Double]]]) -> ([CLLocationCoordinate2D], [MKPolygon]?)? {
        func coordinates(_ ring: [[Double]]) -> [CLLocationCoordinate2D] {
            ring.compactMap { $0.count >= 2 ? CLLocationCoordinate2D(latitude: $0[1], longitude: $0[0]) : nil }
        }
        guard let outer = rings.first.map(coordinates), outer.count >= 3 else { return nil }
        let holes = rings.dropFirst().map(coordinates).filter { $0.count >= 3 }.map { MKPolygon(coordinates: $0, count: $0.count) }
        return (outer, holes.isEmpty ? nil : holes)
    }

    // MARK: Radar sites

    private func syncSites(_ map: MKMapView) {
        let key = "\(radar.showSites)|\(radar.station ?? "")|\(radar.info?.stations.count ?? 0)"
        guard key != sitesKey else { return }
        sitesKey = key
        map.removeAnnotations(map.annotations.filter { $0 is SitePin })
        guard radar.showSites else { return }
        for site in radar.info?.stations ?? [] {
            let pin = SitePin()
            pin.site = site
            pin.selected = site.id == radar.station
            pin.coordinate = CLLocationCoordinate2D(latitude: site.latitude, longitude: site.longitude)
            pin.title = "\(site.id) · \(site.name)"
            map.addAnnotation(pin)
        }
    }

    // MARK: Delegate

    func mapView(_ mapView: MKMapView, rendererFor overlay: any MKOverlay) -> MKOverlayRenderer {
        if let frame = overlay as? RadarFrameOverlay {
            let renderer = MKTileOverlayRenderer(tileOverlay: frame)
            let index = frameOverlays.firstIndex { $0 === frame }
            renderer.alpha = index == radar.frameIndex ? radar.opacity : 0
            return renderer
        }
        if let shape = overlay as? WarningPolygon {
            let renderer = MKPolygonRenderer(polygon: shape)
            let color = Self.platformColor(shape.warning?.hexColor ?? "#4BE7FF")
            renderer.strokeColor = color
            renderer.fillColor = color.withAlphaComponent(0.12)
            renderer.lineWidth = shape.warning?.phenomena == "TO" ? 3 : 2
            return renderer
        }
        if let shape = overlay as? OutlookPolygon {
            let renderer = MKPolygonRenderer(polygon: shape)
            renderer.strokeColor = Self.platformColor(shape.strokeHex)
            renderer.fillColor = Self.platformColor(shape.fillHex).withAlphaComponent(0.18)
            renderer.lineWidth = 1.5
            return renderer
        }
        if let line = overlay as? TrackLine {
            let renderer = MKPolylineRenderer(polyline: line)
            let hex = ["#CFD8DC", "#FFD54F", "#FF9800", "#FF4D5E", "#FF00E5"][min(max(line.threat, 0), 4)]
            renderer.strokeColor = Self.platformColor(hex)
            renderer.lineWidth = line.incoming ? 3 : 2
            renderer.lineDashPattern = [6, 4]
            return renderer
        }
        return MKOverlayRenderer(overlay: overlay)
    }

    func mapView(_ mapView: MKMapView, viewFor annotation: any MKAnnotation) -> MKAnnotationView? {
        if annotation is HomePin {
            let view = mapView.dequeueReusableAnnotationView(withIdentifier: "home") as? MKMarkerAnnotationView
                ?? MKMarkerAnnotationView(annotation: annotation, reuseIdentifier: "home")
            view.annotation = annotation
            view.markerTintColor = Self.platformColor("#1FA463")
            view.glyphImage = Self.symbol("house.fill")
            view.titleVisibility = .visible
            view.displayPriority = .required
            view.zPriority = .max
            view.canShowCallout = false
            return view
        }
        if let pin = annotation as? CellPin, let cell = pin.cell {
            let view = mapView.dequeueReusableAnnotationView(withIdentifier: "cell")
                ?? MKAnnotationView(annotation: annotation, reuseIdentifier: "cell")
            view.annotation = annotation
            view.image = cellImage(cell)
            view.canShowCallout = false
            view.displayPriority = .required
            view.zPriority = MKAnnotationViewZPriority(rawValue: 500 + Float(cell.threat) * 10)
            return view
        }
        if let pin = annotation as? SitePin {
            let view = mapView.dequeueReusableAnnotationView(withIdentifier: "site")
                ?? MKAnnotationView(annotation: annotation, reuseIdentifier: "site")
            view.annotation = annotation
            view.image = siteImage(selected: pin.selected)
            view.canShowCallout = false
            view.displayPriority = pin.selected ? .required : .defaultLow
            view.zPriority = .min
            return view
        }
        return nil
    }

    func mapView(_ mapView: MKMapView, didSelect view: MKAnnotationView) {
        guard let annotation = view.annotation else { return }
        if let pin = annotation as? CellPin, let cell = pin.cell {
            radar.selection = .cell(cell)
        } else if let pin = annotation as? SitePin, let site = pin.site {
            radar.selection = .site(site)
        }
        // Deselected straight away so tapping the same storm again reopens its card.
        mapView.deselectAnnotation(annotation, animated: false)
    }

    func mapView(_ mapView: MKMapView, regionDidChangeAnimated animated: Bool) {
        radar.mapMoved(region: mapView.region, width: Double(mapView.bounds.width))
        syncStorms(mapView)
    }

    // A tap on the map that isn't on a pin: the warning under it, strongest first, or nothing -
    // which closes whatever card was open.
    private func handleTap(at point: CGPoint, in map: MKMapView) {
        let coordinate = map.convert(point, toCoordinateFrom: map)
        let warnings = map.overlays.compactMap { $0 as? WarningPolygon }
            .compactMap { shape in shape.warning.map { (shape, $0) } }
            .sorted { $0.1.drawOrder > $1.1.drawOrder }
        for (shape, warning) in warnings where Self.contains(shape, coordinate) {
            radar.selection = .warning(warning)
            return
        }
        radar.selection = nil
    }

    private static func contains(_ polygon: MKPolygon, _ coordinate: CLLocationCoordinate2D) -> Bool {
        let point = MKMapPoint(coordinate)
        func inside(_ shape: MKPolygon) -> Bool {
            let points = shape.points()
            var result = false
            var j = shape.pointCount - 1
            for i in 0..<shape.pointCount {
                let a = points[i], b = points[j]
                if (a.y > point.y) != (b.y > point.y),
                   point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x {
                    result.toggle()
                }
                j = i
            }
            return result
        }
        guard inside(polygon) else { return false }
        return !(polygon.interiorPolygons ?? []).contains(where: inside)
    }

    #if os(iOS)
    @objc private func tapped(_ recognizer: UITapGestureRecognizer) {
        guard let map = recognizer.view as? MKMapView else { return }
        let point = recognizer.location(in: map)
        if Self.annotationView(at: point, in: map) { return }
        handleTap(at: point, in: map)
    }

    private static func annotationView(at point: CGPoint, in map: MKMapView) -> Bool {
        var view = map.hitTest(point, with: nil)
        while let current = view {
            if current is MKAnnotationView { return true }
            view = current.superview
        }
        return false
    }
    #else
    @objc private func clicked(_ recognizer: NSClickGestureRecognizer) {
        guard let map = recognizer.view as? MKMapView else { return }
        let point = recognizer.location(in: map)
        var view = map.hitTest(map.convert(point, to: map.superview))
        while let current = view {
            if current is MKAnnotationView { return }
            view = current.superview
        }
        handleTap(at: point, in: map)
    }
    #endif

    // MARK: Pictures

    private func cellImage(_ cell: WeatherStormCell) -> PlatformImage? {
        let key = "cell|\(cell.threat)|\(cell.marker)|\(cell.headingDegrees ?? -1)|\(cell.towardHome)"
        if let cached = markerImages[key] { return cached }
        let image = Self.render(StormCellMarker(cell: cell))
        markerImages[key] = image
        return image
    }

    private func siteImage(selected: Bool) -> PlatformImage? {
        let key = "site|\(selected)"
        if let cached = markerImages[key] { return cached }
        let image = Self.render(
            Circle()
                .fill(selected ? Color(weatherHex: "#39FF9C") : Color.gray)
                .overlay(Circle().stroke(Color.black.opacity(0.6), lineWidth: 1))
                .frame(width: selected ? 12 : 8, height: selected ? 12 : 8)
                .padding(2))
        markerImages[key] = image
        return image
    }

    private static func render<Content: View>(_ view: Content) -> PlatformImage? {
        let renderer = ImageRenderer(content: view)
        renderer.scale = 2
        #if os(iOS)
        return renderer.uiImage
        #else
        return renderer.nsImage
        #endif
    }

    private static func symbol(_ name: String) -> PlatformImage? {
        #if os(iOS)
        UIImage(systemName: name)
        #else
        NSImage(systemSymbolName: name, accessibilityDescription: nil)
        #endif
    }

    #if os(iOS)
    static func platformColor(_ hex: String) -> UIColor { UIColor(Color(weatherHex: hex)) }
    #else
    static func platformColor(_ hex: String) -> NSColor { NSColor(Color(weatherHex: hex)) }
    #endif
}

#if os(iOS)
extension WeatherRadarMapCoordinator: UIGestureRecognizerDelegate {
    nonisolated func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer,
                                       shouldRecognizeSimultaneouslyWith other: UIGestureRecognizer) -> Bool {
        true
    }
}
#endif

// A storm on the map, the way the dashboard draws one: a dot coloured by threat with T, M or H
// for a tornado signature, rotation or big hail, and an arrow pointing where it's going.
private struct StormCellMarker: View {
    let cell: WeatherStormCell

    var body: some View {
        let size: CGFloat = cell.threat == 0 ? 10 : 16
        ZStack {
            if let heading = cell.headingDegrees {
                Image(systemName: "arrowtriangle.up.fill")
                    .font(.system(size: 7, weight: .bold))
                    .foregroundStyle(cell.towardHome ? Color(weatherHex: "#FF4D5E") : .black)
                    .offset(y: -(size / 2 + 5))
                    .rotationEffect(.degrees(Double(heading)))
            }
            Circle()
                .fill(cell.threatColor)
                .overlay(Circle().stroke(cell.towardHome ? Color.white : Color.black, lineWidth: cell.threat == 0 ? 1.5 : 2))
                .frame(width: size, height: size)
            if !cell.marker.isEmpty {
                Text(cell.marker)
                    .font(.system(size: 9, weight: .bold))
                    .foregroundStyle(cell.threat >= 3 ? .white : .black)
            }
        }
        .frame(width: 36, height: 36)
    }
}
