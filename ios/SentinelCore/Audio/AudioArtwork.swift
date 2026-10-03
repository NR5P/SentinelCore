import AVKit
import ImageIO
import SwiftUI

// Album, artist and show art. Fetched with URLSession.shared (it carries the session cookie),
// asked for at the size it is drawn (`?size=`, §4 Art), downsampled with ImageIO and kept in a
// process-wide cache. No art - a null path or a 404 - draws a tinted gradient with a glyph, the
// same idea as Movies' text cards.
struct ArtworkImage: View {
    let path: String?
    var size: CGFloat? = nil
    var corner: CGFloat = 8
    var placeholder: String = "music.note"
    var circle = false

    @State private var image: CGImage?
    @State private var failed = false

    private var pixels: Int {
        let points = size ?? 300
        return min(1200, max(64, Int(points * 2.5)))
    }

    private var url: URL? { AppModel.shared.client.audioArtURL(path, size: pixels) }

    var body: some View {
        Color.clear
            .aspectRatio(1, contentMode: .fit)
            .overlay {
                if let image {
                    Image(decorative: image, scale: 1).resizable().scaledToFill()
                } else {
                    ArtworkPlaceholder(seed: path ?? placeholder, symbol: placeholder)
                }
            }
            .clipShape(RoundedRectangle(cornerRadius: circle ? 9999 : corner, style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: circle ? 9999 : corner, style: .continuous)
                    .strokeBorder(.primary.opacity(0.08), lineWidth: 0.5)
            }
            .frame(width: size, height: size)
            .task(id: url) { await load() }
            .accessibilityHidden(true)
    }

    private func load() async {
        guard let url else { image = nil; return }
        if let cached = ArtworkLoader.cached(url) {
            image = cached
            return
        }
        image = await ArtworkLoader.cgImage(url, maxPixel: CGFloat(pixels))
    }
}

// Up to four album covers in a 2×2 grid (§3 Playlist.artUrls); one cover fills the square.
struct MosaicArtwork: View {
    let paths: [String]
    var size: CGFloat? = nil
    var corner: CGFloat = 8
    var placeholder = "music.note.list"

    var body: some View {
        if paths.count >= 4 {
            Color.clear
                .aspectRatio(1, contentMode: .fit)
                .overlay {
                    Grid(horizontalSpacing: 0, verticalSpacing: 0) {
                        GridRow {
                            ArtworkImage(path: paths[0], corner: 0)
                            ArtworkImage(path: paths[1], corner: 0)
                        }
                        GridRow {
                            ArtworkImage(path: paths[2], corner: 0)
                            ArtworkImage(path: paths[3], corner: 0)
                        }
                    }
                }
                .clipShape(RoundedRectangle(cornerRadius: corner, style: .continuous))
                .frame(width: size, height: size)
        } else {
            ArtworkImage(path: paths.first, size: size, corner: corner, placeholder: placeholder)
        }
    }
}

struct ArtworkPlaceholder: View {
    let seed: String
    var symbol = "music.note"

    var body: some View {
        // A stable hash (hashValue changes every launch), so a cover keeps its colour.
        let hash = seed.unicodeScalars.reduce(0) { ($0 &* 31 &+ Int($1.value)) & 0xFFFFFF }
        let hue = Double(hash % 360) / 360
        LinearGradient(
            colors: [Color(hue: hue, saturation: 0.45, brightness: 0.55), Color(hue: (hue + 0.08).truncatingRemainder(dividingBy: 1), saturation: 0.55, brightness: 0.28)],
            startPoint: .topLeading, endPoint: .bottomTrailing)
            .overlay {
                GeometryReader { proxy in
                    Image(systemName: symbol)
                        .font(.system(size: max(12, min(proxy.size.width, proxy.size.height) * 0.36), weight: .light))
                        .foregroundStyle(.white.opacity(0.75))
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                }
            }
    }
}

enum ArtworkLoader {
    @MainActor private static let cache: NSCache<NSURL, CGImage> = {
        let cache = NSCache<NSURL, CGImage>()
        cache.countLimit = 500
        return cache
    }()
    @MainActor private static var missing: Set<URL> = []

    @MainActor static func cached(_ url: URL) -> CGImage? { cache.object(forKey: url as NSURL) }

    @MainActor static func cgImage(_ url: URL, maxPixel: CGFloat) async -> CGImage? {
        if let hit = cache.object(forKey: url as NSURL) { return hit }
        if missing.contains(url) { return nil }
        if let image = await fetch(url, maxPixel: maxPixel) {
            cache.setObject(image, forKey: url as NSURL)
            return image
        }
        if !Task.isCancelled { missing.insert(url) }
        return nil
    }

    nonisolated private static func fetch(_ url: URL, maxPixel: CGFloat) async -> CGImage? {
        guard let (data, response) = try? await URLSession.shared.data(from: url),
              (response as? HTTPURLResponse)?.statusCode ?? 200 == 200,
              let source = CGImageSourceCreateWithData(data as CFData, nil) else { return nil }
        return CGImageSourceCreateThumbnailAtIndex(source, 0, [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixel,
        ] as CFDictionary)
    }

    // For the lock screen artwork.
    @MainActor static func platformImage(_ url: URL) async -> PlatformImage? {
        guard let cg = await cgImage(url, maxPixel: 600) else { return nil }
        #if os(iOS)
        return UIImage(cgImage: cg)
        #else
        return NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height))
        #endif
    }
}

// The system AirPlay / Bluetooth route picker.
#if os(iOS)
struct AirPlayButton: UIViewRepresentable {
    func makeUIView(context: Context) -> AVRoutePickerView {
        let view = AVRoutePickerView()
        view.prioritizesVideoDevices = false
        view.tintColor = .label
        view.activeTintColor = UIColor(SentinelTheme.green)
        return view
    }
    func updateUIView(_ view: AVRoutePickerView, context: Context) {}
}
#else
struct AirPlayButton: NSViewRepresentable {
    func makeNSView(context: Context) -> AVRoutePickerView {
        let view = AVRoutePickerView()
        view.player = AudioPlayer.shared.avPlayer
        view.isRoutePickerButtonBordered = false
        return view
    }
    func updateNSView(_ view: AVRoutePickerView, context: Context) {}
}
#endif
