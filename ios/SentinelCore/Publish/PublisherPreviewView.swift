import SwiftUI
import WebRTC

// The live picture from this device's own camera, drawn by WebRTC's Metal renderer straight from
// the track being (or about to be) streamed - so what is on screen is exactly what the server
// gets, camera choice included.
#if os(iOS)
struct PublisherPreviewView: UIViewRepresentable {
    let track: RTCVideoTrack?

    func makeUIView(context: Context) -> RTCMTLVideoView {
        let view = RTCMTLVideoView()
        view.videoContentMode = .scaleAspectFit
        view.backgroundColor = .black
        return view
    }

    func updateUIView(_ view: RTCMTLVideoView, context: Context) {
        context.coordinator.attach(track, to: view)
    }

    static func dismantleUIView(_ view: RTCMTLVideoView, coordinator: Coordinator) {
        coordinator.attach(nil, to: view)
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    @MainActor
    final class Coordinator {
        private var attached: RTCVideoTrack?

        func attach(_ track: RTCVideoTrack?, to view: RTCMTLVideoView) {
            guard track !== attached else { return }
            attached?.remove(view)
            track?.add(view)
            attached = track
        }
    }
}
#elseif os(macOS)
struct PublisherPreviewView: NSViewRepresentable {
    let track: RTCVideoTrack?

    func makeNSView(context: Context) -> RTCMTLNSVideoView {
        let view = RTCMTLNSVideoView()
        view.wantsLayer = true
        view.layer?.backgroundColor = NSColor.black.cgColor
        return view
    }

    func updateNSView(_ view: RTCMTLNSVideoView, context: Context) {
        context.coordinator.attach(track, to: view)
    }

    static func dismantleNSView(_ view: RTCMTLNSVideoView, coordinator: Coordinator) {
        coordinator.attach(nil, to: view)
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    @MainActor
    final class Coordinator {
        private var attached: RTCVideoTrack?

        func attach(_ track: RTCVideoTrack?, to view: RTCMTLNSVideoView) {
            guard track !== attached else { return }
            attached?.remove(view)
            track?.add(view)
            attached = track
        }
    }
}
#endif
