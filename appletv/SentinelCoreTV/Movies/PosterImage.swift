import ImageIO
import SwiftUI

// Posters, backdrops and episode stills from /api/media. Not SentinelRemoteImage: that one draws
// the UNLICENSED watermark meant for camera imagery, and film artwork is not camera imagery.
//
// Fetched with URLSession.shared, which carries the session cookie from HTTPCookieStorage.shared,
// and downsampled with ImageIO to the size it is drawn at - a TMDB backdrop is 1920px or more, and
// a shelf of twenty of them decoded at full size is what makes scrolling stutter on an Apple TV.
// No artwork (a null url or a 404) draws a text card: the title on a dark gradient.
// Twin of ios/SentinelCore/Movies/PosterImage.swift - keep the two identical.
struct PosterImage: View {
    let url: URL?
    let title: String
    var subtitle: String? = nil
    // Longest edge to decode at, in pixels.
    var maxPixel: CGFloat = 600

    @State private var image: CGImage?
    @State private var failed = false

    var body: some View {
        // Color.clear takes exactly the size the caller proposes; the picture fills it and is
        // clipped to it, so a poster of a slightly different ratio never pushes the layout around.
        Color.clear
            .overlay {
                if let image {
                    Image(decorative: image, scale: 1)
                        .resizable()
                        .scaledToFill()
                } else {
                    MediaTextCard(title: title, subtitle: subtitle, loading: url != nil && !failed)
                }
            }
            .clipped()
            .task(id: url) { await load() }
    }

    private func load() async {
        guard let url else {
            image = nil
            failed = true
            return
        }
        let key = "\(url.absoluteString)#\(Int(maxPixel))" as NSString
        if let cached = ArtworkCache.images.object(forKey: key) {
            image = cached
            return
        }
        if ArtworkCache.missing.contains(key as String) {
            failed = true
            return
        }
        failed = false
        // One retry: a shelf asks for the same picture several times at once, and a busy server
        // can fail one of them (a 5xx or a dropped connection) - that isn't "no artwork".
        var outcome = await Self.fetch(url, maxPixel: maxPixel)
        if case .failed = outcome, !Task.isCancelled {
            try? await Task.sleep(for: .milliseconds(400))
            outcome = await Self.fetch(url, maxPixel: maxPixel)
        }
        guard !Task.isCancelled else { return }
        switch outcome {
        case .image(let loaded):
            ArtworkCache.images.setObject(loaded, forKey: key)
            image = loaded
        case .missing:
            // Only a real "not here" is remembered for the rest of the run.
            ArtworkCache.missing.insert(key as String)
            image = nil
            failed = true
        case .failed:
            image = nil
            failed = true
        }
    }

    private enum Outcome { case image(CGImage), missing, failed }

    // Off the main actor: the download and the decode.
    nonisolated private static func fetch(_ url: URL, maxPixel: CGFloat) async -> Outcome {
        guard let (data, response) = try? await URLSession.shared.data(from: url),
              let status = (response as? HTTPURLResponse)?.statusCode else { return .failed }
        if status == 404 || status == 204 { return .missing }
        guard status == 200, let source = CGImageSourceCreateWithData(data as CFData, nil),
              let image = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                  kCGImageSourceCreateThumbnailFromImageAlways: true,
                  kCGImageSourceCreateThumbnailWithTransform: true,
                  kCGImageSourceThumbnailMaxPixelSize: maxPixel,
              ] as CFDictionary) else { return status == 200 ? .missing : .failed }
        return .image(image)
    }
}

@MainActor
private enum ArtworkCache {
    static let images: NSCache<NSString, CGImage> = {
        let cache = NSCache<NSString, CGImage>()
        cache.countLimit = 400
        return cache
    }()
    // Artwork the server said it doesn't have, so a grid scrolled back and forth doesn't ask again.
    // Cleared with the process; a rescan that finds new artwork shows up on the next launch.
    static var missing: Set<String> = []
}

// What a tile shows with no artwork: the title on a dark gradient, like a DVD spine.
struct MediaTextCard: View {
    let title: String
    var subtitle: String? = nil
    var loading = false

    var body: some View {
        ZStack {
            LinearGradient(
                colors: [Color(white: 0.22), Color(white: 0.08)],
                startPoint: .top, endPoint: .bottom)
            if !loading {
                VStack(spacing: 6) {
                    Text(title)
                        .font(.headline)
                        .multilineTextAlignment(.center)
                        .lineLimit(4)
                        .minimumScaleFactor(0.6)
                    if let subtitle {
                        Text(subtitle)
                            .font(.caption)
                            .foregroundStyle(.white.opacity(0.7))
                            .lineLimit(2)
                    }
                }
                .foregroundStyle(.white)
                .padding(12)
            }
        }
    }
}
