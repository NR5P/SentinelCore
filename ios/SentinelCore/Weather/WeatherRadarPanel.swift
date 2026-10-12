import ImageIO
import SwiftUI
#if os(iOS)
import UIKit
#else
import AppKit
#endif

// The radar card: the map with its time stamp, home and full-screen buttons, the loop controls,
// the layer, radar and overlay pickers, and the colour key - the dashboard's radar panel.
struct WeatherRadarPanel: View {
    @EnvironmentObject private var model: AppModel
    @ObservedObject var radar: WeatherRadarModel
    let zone: TimeZone
    // The report's own radar station, for the picture fallback on an older server.
    let reportHasStation: Bool
    var mapHeight: CGFloat = 420

    @State private var fullScreen = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .firstTextBaseline) {
                Text(title)
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                Spacer(minLength: 8)
                if !radar.mapMissingOnServer, radar.info != nil {
                    layerMenu
                    overlaysMenu
                }
            }
            if radar.mapMissingOnServer {
                WeatherRadarPicture(hasStation: reportHasStation)
            } else if let error = radar.loadError, radar.info == nil {
                ContentUnavailableView {
                    Label("Radar Unavailable", systemImage: "dot.radiowaves.left.and.right")
                } description: {
                    Text(error)
                } actions: {
                    Button("Try Again") { Task { await radar.refresh() } }
                }
                .frame(height: mapHeight)
            } else {
                map(height: mapHeight, fullScreen: false)
                timeline
                stationRow
                legend
            }
        }
        .sentinelCard()
        // The weather warning banner was tapped: straight to the radar, full screen.
        .onAppear { if WeatherWarningsModel.takeRadarRequest() { fullScreen = true } }
        .onReceive(NotificationCenter.default.publisher(for: .sentinelWeatherRadarRequested)) { _ in
            if WeatherWarningsModel.takeRadarRequest() { fullScreen = true }
        }
        #if os(iOS)
        .fullScreenCover(isPresented: $fullScreen) { fullScreenMap }
        #else
        .sheet(isPresented: $fullScreen) {
            fullScreenMap.frame(minWidth: 900, idealWidth: 1200, minHeight: 650, idealHeight: 850)
        }
        #endif
    }

    private var title: String {
        guard let layer = radar.currentLayer else { return "RADAR" }
        return "RADAR · \(layer.isStation ? "\(radar.station ?? "") " : "")\(layer.name.uppercased())"
    }

    private var fullScreenMap: some View {
        NavigationStack {
            VStack(spacing: 10) {
                map(height: nil, fullScreen: true)
                timeline
            }
            .padding()
            .navigationTitle(title.capitalized)
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") { fullScreen = false }
                }
                ToolbarItem(placement: .primaryAction) { layerMenu }
                ToolbarItem(placement: .primaryAction) { overlaysMenu }
            }
        }
    }

    // MARK: - The map

    @ViewBuilder
    private func map(height: CGFloat?, fullScreen isFull: Bool) -> some View {
        let shape = RoundedRectangle(cornerRadius: 10)
        WeatherRadarMapView(radar: radar, serverRoot: URL(string: model.activeServerURL.trimmingCharacters(in: .whitespacesAndNewlines)))
            .frame(height: height)
            .frame(maxHeight: isFull ? .infinity : nil)
            .clipShape(shape)
            .overlay(shape.stroke(.primary.opacity(0.1)))
            .overlay(alignment: .topTrailing) { timeBadge.padding(8) }
            .overlay(alignment: .topLeading) {
                VStack(spacing: 6) {
                    mapButton("house.fill", help: "Back to home") { radar.goHome() }
                    if !isFull {
                        mapButton("arrow.up.left.and.arrow.down.right", help: "Full screen") { fullScreen = true }
                    }
                }
                .padding(8)
            }
            .overlay(alignment: .bottomLeading) {
                if let selection = radar.selection {
                    WeatherMapSelectionCard(selection: selection, radar: radar, zone: zone)
                        .padding(8)
                        .transition(.opacity)
                }
            }
    }

    private func mapButton(_ symbol: String, help: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 13, weight: .semibold))
                .frame(width: 30, height: 30)
                .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 7))
        }
        .buttonStyle(.plain)
        .help(help)
        .accessibilityLabel(help)
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
        .font(.caption.monospacedDigit().weight(.semibold))
        .foregroundStyle(radar.isOldFrame ? SentinelTheme.amber : .primary)
        .padding(.horizontal, 8)
        .padding(.vertical, 4)
        .background(.regularMaterial, in: Capsule())
    }

    // MARK: - Controls

    private var timeline: some View {
        HStack(spacing: 12) {
            Button {
                radar.togglePlay()
            } label: {
                Label(radar.playing ? "Pause" : "Play", systemImage: radar.playing ? "pause.fill" : "play.fill")
                    .frame(minWidth: 64)
            }
            .buttonStyle(.bordered)
            .disabled(radar.frames.count < 2)
            .help("Play the last hour or so of radar as a loop.")

            Slider(value: Binding(
                get: { Double(radar.frameIndex) },
                set: { radar.scrub(to: Int($0.rounded())) }),
                   in: 0...Double(max(radar.frames.count - 1, 1)), step: 1)
                .disabled(radar.frames.count < 2)
                .accessibilityLabel("Radar frame")

            Image(systemName: "circle.lefthalf.filled")
                .foregroundStyle(.secondary)
                .help("How see-through the radar is over the map.")
            Slider(value: $radar.opacity, in: 0.2...1)
                .frame(width: 90)
                .accessibilityLabel("Radar opacity")
        }
        .controlSize(.small)
    }

    private var layerMenu: some View {
        Menu {
            Section("National") {
                ForEach(radar.info?.layers.filter { !$0.isStation } ?? []) { layer in layerButton(layer) }
            }
            Section(radar.station.map { "Radar \($0)" } ?? "Radar") {
                ForEach(radar.info?.layers.filter(\.isStation) ?? []) { layer in
                    layerButton(layer).disabled(radar.station == nil)
                }
            }
        } label: {
            Label(radar.currentLayer?.name ?? "Layer", systemImage: "square.3.layers.3d")
                .font(.caption)
        }
        .menuStyle(.button)
        .buttonStyle(.bordered)
        .controlSize(.small)
        .fixedSize()
        .help(radar.currentLayer?.description ?? "Radar layer")
    }

    private func layerButton(_ layer: WeatherMapLayer) -> some View {
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

    private var overlaysMenu: some View {
        Menu {
            Toggle("Storm Tracks", isOn: $radar.showCells)
            Toggle("Warnings", isOn: $radar.showWarnings)
            Toggle("SPC Outlook", isOn: $radar.showOutlook)
            Toggle("Radar Sites", isOn: $radar.showSites)
        } label: {
            Label("Overlays", systemImage: "square.2.layers.3d.top.filled")
                .font(.caption)
        }
        .menuStyle(.button)
        .buttonStyle(.bordered)
        .controlSize(.small)
        .fixedSize()
    }

    private var stationRow: some View {
        HStack(spacing: 8) {
            Menu {
                Picker("Radar", selection: Binding(
                    get: { radar.station ?? "" },
                    set: { station in Task { await radar.selectStation(station) } })) {
                    ForEach(radar.sitesByDistance, id: \.site.id) { entry in
                        Text("\(entry.site.id) · \(entry.site.name)\(entry.miles.map { " (\(Int($0.rounded())) mi)" } ?? "")")
                            .tag(entry.site.id)
                    }
                }
                .pickerStyle(.inline)
            } label: {
                Label(radar.station.map { "Radar \($0)" } ?? "Choose a Radar", systemImage: "dot.radiowaves.left.and.right")
                    .font(.caption)
            }
            .menuStyle(.button)
            .buttonStyle(.bordered)
            .controlSize(.small)
            .fixedSize()
            .help("Which radar the per-radar layers come from. Defaults to the one nearest home; tapping a radar dish on the map picks it too.")
            if let problems = radar.info?.problems, !problems.isEmpty {
                Label(problems.joined(separator: " "), systemImage: "exclamationmark.triangle")
                    .font(.caption2)
                    .foregroundStyle(SentinelTheme.amber)
                    .lineLimit(2)
            }
            Spacer(minLength: 0)
        }
    }

    @ViewBuilder private var legend: some View {
        if let layer = radar.currentLayer {
            VStack(alignment: .leading, spacing: 6) {
                if let data = radar.legend, let image = PlatformImage(data: data) {
                    platformImage(image)
                        .resizable()
                        .interpolation(.high)
                        .aspectRatio(contentMode: .fit)
                        .frame(maxWidth: 500, maxHeight: 30, alignment: .leading)
                        .accessibilityLabel("\(layer.name) colour key")
                }
                Text("\(layer.description) \(layer.isStation ? "From radar \(radar.station ?? ""); it sees about 150 miles out." : "Updated every few minutes.") \(SentinelInputHint.zoom)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

private enum SentinelInputHint {
    #if os(iOS)
    static let zoom = "Pinch to zoom, drag to pan; tap a storm or warning for details."
    #else
    static let zoom = "Scroll or pinch to zoom, drag to pan; click a storm or warning for details."
    #endif
}

func platformImage(_ image: PlatformImage) -> Image {
    #if os(iOS)
    Image(uiImage: image)
    #else
    Image(nsImage: image)
    #endif
}

// MARK: - The card for a tapped storm, warning or radar site

struct WeatherMapSelectionCard: View {
    let selection: WeatherRadarModel.Selection
    @ObservedObject var radar: WeatherRadarModel
    let zone: TimeZone

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .firstTextBaseline) {
                Text(heading)
                    .font(.subheadline.bold())
                    .foregroundStyle(headingColor)
                Spacer(minLength: 8)
                Button {
                    radar.selection = nil
                } label: {
                    Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary)
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Close")
            }
            ScrollView {
                VStack(alignment: .leading, spacing: 8) {
                    if let badge { Text(badge).font(.caption.bold()).foregroundStyle(SentinelTheme.red) }
                    Grid(alignment: .leadingFirstTextBaseline, horizontalSpacing: 10, verticalSpacing: 4) {
                        ForEach(rows, id: \.0) { label, value in
                            GridRow {
                                Text(label).foregroundStyle(.secondary)
                                Text(value)
                            }
                        }
                    }
                    .font(.caption)
                    if case .site(let site) = selection, site.id != radar.station {
                        Button("Use This Radar") { Task { await radar.selectStation(site.id) } }
                            .buttonStyle(.sentinelProminent)
                            .controlSize(.small)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(maxHeight: 190)
            .fixedSize(horizontal: false, vertical: true)
        }
        .padding(12)
        .frame(maxWidth: 330, alignment: .leading)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(.primary.opacity(0.1)))
    }

    private var clock: Date.FormatStyle { Date.FormatStyle(date: .omitted, time: .shortened, timeZone: zone) }

    private var heading: String {
        switch selection {
        case .cell(let cell): cell.title
        case .warning(let warning): warning.name
        case .site(let site): "\(site.id) · \(site.name)"
        }
    }

    private var headingColor: Color {
        switch selection {
        case .cell(let cell): cell.threat >= 3 ? SentinelTheme.red : .primary
        case .warning(let warning): Color(weatherHex: warning.hexColor)
        case .site: .primary
        }
    }

    private var badge: String? {
        guard case .warning(let warning) = selection else { return nil }
        let badges = [warning.emergency ? "EMERGENCY" : nil, warning.particularlyDangerous ? "PARTICULARLY DANGEROUS SITUATION" : nil].compactMap { $0 }
        return badges.isEmpty ? nil : badges.joined(separator: " · ")
    }

    private var rows: [(String, String)] {
        switch selection {
        case .cell(let cell):
            let hail = "\(cell.hailProbability ?? 0)% chance, \(cell.severeHailProbability ?? 0)% severe"
                + (cell.maxHailInches.map { $0 > 0 ? String(format: ", up to %.2f\"", $0) : "" } ?? "")
            let rows: [(String, String)?] = [
                ("tracked by", "\(cell.radar) radar · cell \(cell.stormId)"),
                ("motion", cell.speedMph.map { $0 > 0 ? "toward the \(cell.headingCompass ?? "") at \($0) mph" : "nearly stationary" } ?? "nearly stationary"),
                cell.distanceMiles.map { ("from home", String(format: "%.1f mi ", $0) + (cell.bearingCompass ?? "")) },
                cell.towardHome ? ("reaches home", "in about \(cell.etaMinutes ?? 0) min, passing \(String(format: "%.1f", cell.closestMiles ?? 0)) mi away") : nil,
                cell.tornadic ? ("tornado", "tornado vortex signature - strong rotation near the ground") : nil,
                cell.mesoStrength.map { ("rotation", "mesocyclone rank \($0) of 25\(cell.rotation ? "" : " (weak)")") },
                ("hail", hail),
                cell.maxDbz.map { ("max reflectivity", "\($0) dBZ") },
                cell.topKft.map { ("storm top", "\(Int($0.rounded())),000 ft") },
                cell.vil.map { ("VIL", "\($0) kg/m² (water in the column; 40+ often means hail)") },
                cell.track.isEmpty ? nil : ("track", cell.track.map { "+\($0.minutes) min" }.joined(separator: ", ")),
                cell.validAt.map { ("scan", $0.formatted(clock)) },
            ]
            return rows.compactMap { $0 }
        case .warning(let warning):
            let rows: [(String, String)?] = [
                ("in effect", "\(warning.issued?.formatted(clock) ?? "") - \(warning.expires?.formatted(clock) ?? "")"),
                warning.tags.isEmpty ? nil : ("threats", warning.tags),
                warning.office.map { ("issued by", "NWS \($0)") },
                warning.containsHome ? ("home", "inside this warning") : nil,
            ]
            return rows.compactMap { $0 }
        case .site(let site):
            return [("radar", site.id == radar.station ? "selected - the per-radar layers come from here" : "tap Use This Radar for its per-radar layers"),
                    ("location", String(format: "%.3f, %.3f", site.latitude, site.longitude))]
        }
    }
}

// MARK: - Picture fallback

// A server from before the radar map still serves the NWS's own animated radar picture: local
// (the nearest radar) or national, as a loop or the latest still.
struct WeatherRadarPicture: View {
    @EnvironmentObject private var model: AppModel
    let hasStation: Bool

    @State private var scope = "local"
    @State private var kind = "loop"
    @State private var data: Data?
    @State private var error: String?

    private var effectiveScope: String { hasStation ? scope : "national" }

    var body: some View {
        VStack(spacing: 10) {
            HStack {
                Picker("Area", selection: $scope) {
                    Text("Local").tag("local")
                    Text("National").tag("national")
                }
                .pickerStyle(.segmented)
                .disabled(!hasStation)
                Picker("Motion", selection: $kind) {
                    Text("Loop").tag("loop")
                    Text("Still").tag("still")
                }
                .pickerStyle(.segmented)
            }
            .labelsHidden()
            ZStack {
                Color.black
                if let data {
                    AnimatedGIFView(data: data)
                } else if let error {
                    Text(error).font(.callout).foregroundStyle(.secondary).padding()
                } else {
                    ProgressView()
                }
            }
            .aspectRatio(1, contentMode: .fit)
            .frame(maxWidth: 620)
            .clipShape(RoundedRectangle(cornerRadius: 10))
        }
        .frame(maxWidth: .infinity)
        // Every two minutes, like the dashboard's radar.
        .task(id: "\(effectiveScope)|\(kind)") {
            while !Task.isCancelled {
                do {
                    data = try await model.client.weatherRadar(scope: effectiveScope, kind: kind)
                    error = nil
                } catch {
                    if error is CancellationError || (error as? URLError)?.code == .cancelled { return }
                    if data == nil { self.error = error.localizedDescription }
                }
                try? await Task.sleep(for: WeatherRadarModel.refreshInterval)
            }
        }
    }
}

// The radar comes as an animated GIF, which SwiftUI's Image shows as a still. UIImageView plays an
// animated UIImage (each frame repeated to honour its own delay, since UIImage.animatedImage
// spaces frames evenly); NSImageView plays a GIF by itself.
#if os(iOS)
struct AnimatedGIFView: UIViewRepresentable {
    let data: Data

    func makeUIView(context: Context) -> UIImageView {
        let view = UIImageView()
        view.contentMode = .scaleAspectFit
        view.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        view.setContentCompressionResistancePriority(.defaultLow, for: .vertical)
        return view
    }

    func updateUIView(_ view: UIImageView, context: Context) {
        guard context.coordinator.shown != data else { return }
        context.coordinator.shown = data
        view.image = Self.decode(data)
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    final class Coordinator {
        var shown: Data?
    }

    static func decode(_ data: Data) -> UIImage? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil) else { return nil }
        let count = CGImageSourceGetCount(source)
        guard count > 1 else { return CGImageSourceCreateImageAtIndex(source, 0, nil).map { UIImage(cgImage: $0) } }
        let unit = 0.1
        var frames: [UIImage] = []
        for index in 0..<count {
            guard let frame = CGImageSourceCreateImageAtIndex(source, index, nil) else { continue }
            let properties = CGImageSourceCopyPropertiesAtIndex(source, index, nil) as? [CFString: Any]
            let gif = properties?[kCGImagePropertyGIFDictionary] as? [CFString: Any]
            var delay = (gif?[kCGImagePropertyGIFUnclampedDelayTime] as? Double) ?? (gif?[kCGImagePropertyGIFDelayTime] as? Double) ?? 0.1
            if delay < 0.02 { delay = 0.1 }
            let repeats = min(30, max(1, Int((delay / unit).rounded())))
            frames.append(contentsOf: repeatElement(UIImage(cgImage: frame), count: repeats))
        }
        return UIImage.animatedImage(with: frames, duration: Double(frames.count) * unit)
    }
}
#else
struct AnimatedGIFView: NSViewRepresentable {
    let data: Data

    func makeNSView(context: Context) -> NSImageView {
        let view = NSImageView()
        view.imageScaling = .scaleProportionallyUpOrDown
        view.animates = true
        view.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        view.setContentCompressionResistancePriority(.defaultLow, for: .vertical)
        return view
    }

    func updateNSView(_ view: NSImageView, context: Context) {
        guard context.coordinator.shown != data else { return }
        context.coordinator.shown = data
        view.image = NSImage(data: data)
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    final class Coordinator {
        var shown: Data?
    }
}
#endif
