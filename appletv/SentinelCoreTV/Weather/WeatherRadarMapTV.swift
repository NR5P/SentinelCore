import ImageIO
import MapKit
import SwiftUI
import UIKit
import UniformTypeIdentifiers

// The radar map on the television: Apple Maps underneath, the server's radar tiles over the roads
// and under the place names, and on top the storm cells with their forecast tracks, the warning
// outlines, the SPC outlook and home. A trimmed copy of the iPhone/Mac app's WeatherRadarMapView:
// the remote can't tap a storm or drag the map, so the map takes no touches at all and is moved by
// the model's requests (home, zoom buttons, a storm picked from the list).
//
// One tile overlay per frame of the loop, all on the map with only the current one visible, so
// each frame's tiles are already loaded by the time it comes round - the dashboard's approach.
// Only the newest frame is added up front; the rest join when the loop is played.
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
        configuration.urlCache = URLCache(memoryCapacity: 48 * 1024 * 1024, diskCapacity: 64 * 1024 * 1024,
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

// All the lightning on one world-sized overlay whose renderer draws the flashes itself - a few
// thousand annotations would bring MapKit to its knees. The renderer draws tiles on background
// threads, so what it draws is a snapshot swapped under a lock.
final class LightningOverlay: NSObject, MKOverlay, @unchecked Sendable {
    struct Snapshot {
        var strikes: [LightningStrike] = []
        var moment = Date()
    }

    let coordinate = CLLocationCoordinate2D(latitude: 0, longitude: 0)
    let boundingMapRect = MKMapRect.world
    private let lock = NSLock()
    private var current = Snapshot()

    var snapshot: Snapshot {
        get { lock.withLock { current } }
        set { lock.withLock { current = newValue } }
    }
}

final class LightningRenderer: MKOverlayRenderer, @unchecked Sendable {
    override func draw(_ mapRect: MKMapRect, zoomScale: MKZoomScale, in context: CGContext) {
        guard let overlay = overlay as? LightningOverlay else { return }
        let snapshot = overlay.snapshot
        // Sizes are in screen points; one point is 1/zoomScale map points.
        let unit = 1 / zoomScale
        let margin = 12 * unit
        let area = mapRect.insetBy(dx: -margin, dy: -margin)
        var bolts: [CGPoint] = []
        for strike in snapshot.strikes {
            let mapPoint = MKMapPoint(CLLocationCoordinate2D(latitude: strike.latitude, longitude: strike.longitude))
            guard area.contains(mapPoint) else { continue }
            let point = self.point(for: mapPoint)
            switch LightningStore.look(strike, at: snapshot.moment) {
            case .bolt:
                bolts.append(point)
            case .dot(let hex, let alpha, let radius):
                let r = CGFloat(radius) * unit
                let rect = CGRect(x: point.x - r, y: point.y - r, width: r * 2, height: r * 2)
                context.setAlpha(CGFloat(alpha))
                context.setFillColor(WeatherRadarMapCoordinator.cgColor(hex))
                context.fillEllipse(in: rect)
                context.setStrokeColor(CGColor(gray: 0, alpha: 0.7))
                context.setLineWidth(0.75 * unit)
                context.strokeEllipse(in: rect)
            }
        }
        context.setAlpha(1)
        // Bolts last, so the newest flashes sit on top of the older ones around them.
        for point in bolts {
            context.saveGState()
            context.translateBy(x: point.x, y: point.y)
            context.scaleBy(x: unit, y: unit)
            let path = CGMutablePath()
            path.move(to: CGPoint(x: 2, y: -9))
            path.addLine(to: CGPoint(x: -5, y: 1))
            path.addLine(to: CGPoint(x: -0.5, y: 1))
            path.addLine(to: CGPoint(x: -2.5, y: 9))
            path.addLine(to: CGPoint(x: 5, y: -2))
            path.addLine(to: CGPoint(x: 0.5, y: -2))
            path.closeSubpath()
            context.setShadow(offset: .zero, blur: 6, color: WeatherRadarMapCoordinator.cgColor("#FFEB3B"))
            context.addPath(path)
            context.setFillColor(WeatherRadarMapCoordinator.cgColor("#FFFDE7"))
            context.fillPath()
            context.setShadow(offset: .zero, blur: 0, color: nil)
            context.addPath(path)
            context.setStrokeColor(CGColor(gray: 0, alpha: 0.85))
            context.setLineWidth(1.2)
            context.strokePath()
            context.restoreGState()
        }
    }
}

final class CellPin: MKPointAnnotation {
    var cell: WeatherStormCell?
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
    private var lastRequest: UUID?
    private var centeredOnHome = false
    private var home: HomePin?
    private var lightningOverlay: LightningOverlay?
    private var lightningKey = ""
    private var markerImages: [String: UIImage] = [:]

    init(radar: WeatherRadarModel) {
        self.radar = radar
    }

    func makeMap() -> MKMapView {
        let map = MKMapView()
        map.delegate = self
        map.pointOfInterestFilter = .excludingAll
        map.preferredConfiguration = MKStandardMapConfiguration(elevationStyle: .flat, emphasisStyle: .muted)
        // Moved only by the buttons around it - see the note at the top.
        map.isUserInteractionEnabled = false
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
        syncLightning(map)
        if let request = radar.request, request.token != lastRequest {
            lastRequest = request.token
            fly(map, to: request.center, minimumZoom: request.minimumZoom, exactZoom: request.zoom)
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

    private func fly(_ map: MKMapView, to center: CLLocationCoordinate2D, minimumZoom: Double, exactZoom: Double?) {
        let width = Double(map.bounds.width > 0 ? map.bounds.width : 1000)
        let wanted = exactZoom ?? max(radar.zoom, minimumZoom)
        let lonDelta = 360 * width / (pow(2, wanted) * 256)
        let latDelta = lonDelta * cos(center.latitude * .pi / 180) * Double(map.bounds.height > 0 ? map.bounds.height / map.bounds.width : 0.56)
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
            renderer.lineWidth = shape.warning?.phenomena == "TO" ? 4 : 3
            return renderer
        }
        if let shape = overlay as? OutlookPolygon {
            let renderer = MKPolygonRenderer(polygon: shape)
            renderer.strokeColor = Self.platformColor(shape.strokeHex)
            renderer.fillColor = Self.platformColor(shape.fillHex).withAlphaComponent(0.18)
            renderer.lineWidth = 2
            return renderer
        }
        if overlay is LightningOverlay {
            return LightningRenderer(overlay: overlay)
        }
        if let line = overlay as? TrackLine {
            let renderer = MKPolylineRenderer(polyline: line)
            let hex = ["#CFD8DC", "#FFD54F", "#FF9800", "#FF4D5E", "#FF00E5"][min(max(line.threat, 0), 4)]
            renderer.strokeColor = Self.platformColor(hex)
            renderer.lineWidth = line.incoming ? 4 : 3
            renderer.lineDashPattern = [8, 5]
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
            view.glyphImage = UIImage(systemName: "house.fill")
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
        return nil
    }

    func mapView(_ mapView: MKMapView, regionDidChangeAnimated animated: Bool) {
        radar.mapMoved(region: mapView.region, width: Double(mapView.bounds.width))
        syncStorms(mapView)
    }

    // MARK: Lightning

    private func syncLightning(_ map: MKMapView) {
        let moment = radar.lightningMoment
        let key = "\(radar.showLightning)|\(radar.lightningVersion)|\(Int(moment.timeIntervalSince1970))"
        guard key != lightningKey else { return }
        lightningKey = key
        guard radar.showLightning else {
            if let overlay = lightningOverlay { map.removeOverlay(overlay) }
            lightningOverlay = nil
            return
        }
        let overlay = lightningOverlay ?? LightningOverlay()
        overlay.snapshot = LightningOverlay.Snapshot(strikes: radar.lightning.visible(at: moment), moment: moment)
        if lightningOverlay == nil {
            lightningOverlay = overlay
            map.addOverlay(overlay, level: .aboveLabels)
        } else {
            map.renderer(for: overlay)?.setNeedsDisplay()
        }
    }

    // MARK: Pictures

    private func cellImage(_ cell: WeatherStormCell) -> UIImage? {
        let selected: Bool = if case .cell(let open) = radar.selection { open.id == cell.id } else { false }
        let key = "cell|\(cell.threat)|\(cell.marker)|\(cell.headingDegrees ?? -1)|\(cell.towardHome)|\(selected)"
        if let cached = markerImages[key] { return cached }
        let renderer = ImageRenderer(content: StormCellMarker(cell: cell, selected: selected))
        renderer.scale = 2
        let image = renderer.uiImage
        markerImages[key] = image
        return image
    }

    nonisolated static func cgColor(_ hex: String) -> CGColor {
        var value: UInt64 = 0
        Scanner(string: hex.trimmingCharacters(in: CharacterSet(charactersIn: "#"))).scanHexInt64(&value)
        return CGColor(srgbRed: CGFloat((value >> 16) & 0xFF) / 255, green: CGFloat((value >> 8) & 0xFF) / 255,
                       blue: CGFloat(value & 0xFF) / 255, alpha: 1)
    }

    static func platformColor(_ hex: String) -> UIColor { UIColor(Color(weatherHex: hex)) }
}

// A storm on the map, the way the dashboard draws one: a dot coloured by threat with T, M or H
// for a tornado signature, rotation or big hail, and an arrow pointing where it's going. Drawn a
// size up from the phone's for the across-the-room view; the storm picked from the list gets a
// ring, since the remote has no callout to open.
private struct StormCellMarker: View {
    let cell: WeatherStormCell
    let selected: Bool

    var body: some View {
        let size: CGFloat = cell.threat == 0 ? 14 : 22
        ZStack {
            if selected {
                Circle().stroke(Color.white, lineWidth: 3).frame(width: size + 16, height: size + 16)
            }
            if let heading = cell.headingDegrees {
                Image(systemName: "arrowtriangle.up.fill")
                    .font(.system(size: 10, weight: .bold))
                    .foregroundStyle(cell.towardHome ? Color(weatherHex: "#FF4D5E") : .black)
                    .offset(y: -(size / 2 + 7))
                    .rotationEffect(.degrees(Double(heading)))
            }
            Circle()
                .fill(cell.threatColor)
                .overlay(Circle().stroke(cell.towardHome ? Color.white : Color.black, lineWidth: cell.threat == 0 ? 2 : 2.5))
                .frame(width: size, height: size)
            if !cell.marker.isEmpty {
                Text(cell.marker)
                    .font(.system(size: 12, weight: .bold))
                    .foregroundStyle(cell.threat >= 3 ? .white : .black)
            }
        }
        .frame(width: 50, height: 50)
    }
}
