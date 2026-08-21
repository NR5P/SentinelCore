import AVFoundation
import AVKit
import SwiftUI

#if os(macOS)
import AppKit
#else
import UIKit
#endif

// The video surface for a *live* feed: picture only, no transport controls.
//
// Twin of ios/SentinelCore/Views/LiveVideoView.swift - keep the two in sync. The macOS branch is
// dead code in this target and is carried only so the two files stay diffable.
//
// The tvOS reason for owning the layer is the third one: the MSE rung renders into an
// AVSampleBufferDisplayLayer, and AVKit's VideoPlayer can host an AVPlayer and nothing else. The
// live screens here previously used VideoPlayer and lost nothing by leaving it - a live feed has
// no timeline to scrub and no end to seek to, and the grid tiles already refused hits.
//
// This deliberately avoids SwiftUI's VideoPlayer, on both platforms, for two separate reasons.
//
// On macOS it is a crash. VideoPlayer always builds AVKit's inline playback controls, and on
// macOS 26/27 assembling those over a live stream aborts inside AVKit itself -
// AVAuxiliaryControlsView adds a control, that resizes the controls bar, the resulting synchronous
// re-layout re-enters AVKit's own SwiftUI body, and its AVTimelineScrubberViewModel binding traps
// (EXC_BREAKPOINT in AVKit, no app frames above SentinelCoreApp.main). It happens as soon as the
// Live grid draws, so the whole tab was unreachable. There is no public knob to hide VideoPlayer's
// controls.
//
// On iOS it is digital zoom. Zooming is a transform on the video surface, and AVKit's controls
// live *inside* that surface - a pinch would blow the transport bar up with the picture and then
// clip it against the tile. Owning the layer keeps the transform on the picture alone.
//
// Nothing is lost by it: a live feed has no timeline to scrub and no end to seek to, and the grid
// tiles already refused hits so their controls were only ever decoration. The one control that did
// something on the full-screen live view was volume, and LivePlayerView now draws its own mute
// button (bound to LiveStreamPlayer.isMuted) on both platforms.
//
// Recorded playback (RecordingTimelineView, ActivityDetailView) still uses VideoPlayer - those
// clips have a real duration, the scrubber is the point, and they don't take this path.
//
// It draws whichever transport the ladder settled on. Both are CALayers with the same
// `videoGravity` letterboxing, so the digital-zoom transform and the PTZ overlay that sit around
// this view behave identically either way - which is what made moving off AVPlayer possible at all.
struct LiveVideoView: View {
    @ObservedObject var stream: LiveStreamPlayer

    var body: some View {
        switch stream.transport {
        case .mse(let mse):
            SampleBufferLayerView(layer: mse.displayLayer)
        case .hls:
            PlayerLayerView(player: stream.player)
        }
    }
}

// The MSE rung renders into an AVSampleBufferDisplayLayer that MseLiveStream owns and keeps across
// reconnects, so unlike PlayerLayerView below this can't make the layer its own backing layer -
// it hosts it and follows its own bounds.
#if os(macOS)
private struct SampleBufferLayerView: NSViewRepresentable {
    let layer: AVSampleBufferDisplayLayer

    func makeNSView(context: Context) -> SampleBufferBackingView {
        let view = SampleBufferBackingView()
        view.videoLayer = layer
        return view
    }

    func updateNSView(_ view: SampleBufferBackingView, context: Context) {
        if view.videoLayer !== layer { view.videoLayer = layer }
    }

    static func dismantleNSView(_ view: SampleBufferBackingView, coordinator: ()) {
        view.videoLayer = nil
    }
}

final class SampleBufferBackingView: NSView {
    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        layer?.backgroundColor = .black
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    var videoLayer: AVSampleBufferDisplayLayer? {
        didSet {
            oldValue?.removeFromSuperlayer()
            guard let videoLayer else { return }
            videoLayer.frame = bounds
            layer?.addSublayer(videoLayer)
        }
    }

    override func layout() {
        super.layout()
        // The hosted layer isn't the backing layer, so nothing resizes it for us. Without the
        // transaction the resize animates, which on a rotation or a window drag shows the picture
        // sliding into place a third of a second behind the frame around it.
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        videoLayer?.frame = bounds
        CATransaction.commit()
    }
}
#else
private struct SampleBufferLayerView: UIViewRepresentable {
    let layer: AVSampleBufferDisplayLayer

    func makeUIView(context: Context) -> SampleBufferBackingView {
        let view = SampleBufferBackingView()
        view.videoLayer = layer
        return view
    }

    func updateUIView(_ view: SampleBufferBackingView, context: Context) {
        if view.videoLayer !== layer { view.videoLayer = layer }
    }

    static func dismantleUIView(_ view: SampleBufferBackingView, coordinator: ()) {
        view.videoLayer = nil
    }
}

final class SampleBufferBackingView: UIView {
    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = .black
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    var videoLayer: AVSampleBufferDisplayLayer? {
        didSet {
            oldValue?.removeFromSuperlayer()
            guard let videoLayer else { return }
            videoLayer.frame = bounds
            layer.addSublayer(videoLayer)
        }
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        videoLayer?.frame = bounds
        CATransaction.commit()
    }
}
#endif

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
#else
private struct PlayerLayerView: UIViewRepresentable {
    let player: AVPlayer

    func makeUIView(context: Context) -> PlayerLayerBackingView {
        let view = PlayerLayerBackingView()
        view.player = player
        return view
    }

    func updateUIView(_ view: PlayerLayerBackingView, context: Context) {
        if view.player !== player { view.player = player }
    }

    static func dismantleUIView(_ view: PlayerLayerBackingView, coordinator: ()) {
        view.player = nil
    }
}

final class PlayerLayerBackingView: UIView {
    // layerClass, so the AVPlayerLayer *is* the view's layer and follows its bounds for free.
    override static var layerClass: AnyClass { AVPlayerLayer.self }

    var player: AVPlayer? {
        get { (layer as? AVPlayerLayer)?.player }
        set {
            guard let playerLayer = layer as? AVPlayerLayer else { return }
            // Matches what VideoPlayer did, so a tile letterboxes as it always has rather than
            // cropping the top and bottom off a 4:3 camera.
            playerLayer.videoGravity = .resizeAspect
            playerLayer.player = newValue
        }
    }
}
#endif
