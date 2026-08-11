import AVFoundation
import AVKit
import SwiftUI

#if os(macOS)
import AppKit
#endif

// The video surface for a *live* feed: picture only, no transport controls.
//
// On macOS this deliberately avoids SwiftUI's VideoPlayer. VideoPlayer always builds AVKit's
// inline playback controls, and on macOS 26/27 assembling those over a live stream aborts inside
// AVKit itself - AVAuxiliaryControlsView adds a control, that resizes the controls bar, the
// resulting synchronous re-layout re-enters AVKit's own SwiftUI body, and its
// AVTimelineScrubberViewModel binding traps (EXC_BREAKPOINT in AVKit, no app frames above
// SentinelCoreApp.main). It happens as soon as the Live grid draws, so the whole tab was
// unreachable. There is no public knob to hide VideoPlayer's controls, so live playback renders
// straight into an AVPlayerLayer instead.
//
// Nothing is lost by it: a live feed has no timeline to scrub and no end to seek to, and the grid
// tiles already refused hits so their controls were only ever decoration.
//
// Recorded playback (RecordingTimelineView, ActivityDetailView) still uses VideoPlayer - those
// clips have a real duration, the scrubber is the point, and they don't take this path.
struct LiveVideoView: View {
    let player: AVPlayer

    var body: some View {
        #if os(macOS)
        PlayerLayerView(player: player)
        #else
        VideoPlayer(player: player)
        #endif
    }
}

#if os(macOS)
private struct PlayerLayerView: NSViewRepresentable {
    let player: AVPlayer

    func makeNSView(context: Context) -> PlayerLayerBackingView {
        let view = PlayerLayerBackingView()
        view.player = player
        return view
    }

    func updateNSView(_ view: PlayerLayerBackingView, context: Context) {
        // LiveStreamPlayer keeps one AVPlayer for its whole lifetime and swaps items underneath
        // it, so this normally does nothing - but a tile re-bound to a different camera's player
        // has to follow.
        if view.player !== player { view.player = player }
    }

    static func dismantleNSView(_ view: PlayerLayerBackingView, coordinator: ()) {
        // Leaving a torn-down layer attached to a live player keeps a renderer on it, which is
        // what LiveStreamPlayer.stop() is trying to get rid of.
        view.player = nil
    }
}

final class PlayerLayerBackingView: NSView {
    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        // Set before anything reads `layer`, so makeBackingLayer() below is what answers.
        wantsLayer = true
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    // Making the AVPlayerLayer the view's own backing layer rather than a sublayer is what keeps
    // it sized to the view; a sublayer would need manual resizing on every layout.
    override func makeBackingLayer() -> CALayer {
        let layer = AVPlayerLayer()
        // Matches what VideoPlayer does, so a tile letterboxes exactly as it did before rather
        // than cropping the top and bottom off a 4:3 camera.
        layer.videoGravity = .resizeAspect
        return layer
    }

    var player: AVPlayer? {
        get { (layer as? AVPlayerLayer)?.player }
        set { (layer as? AVPlayerLayer)?.player = newValue }
    }
}
#endif
