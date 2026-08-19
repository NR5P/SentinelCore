import SwiftUI

// Continuous-move semantics: pressing a direction starts the camera moving at a fixed moderate
// speed, and it keeps moving until released - there's no ONVIF "move this far and stop
// automatically" in this mode. That makes the release command the load-bearing one: a lost move
// means the camera didn't budge, which is self-evident from the video, but a lost stop means a
// camera that keeps turning until something else stops it. Everything below exists because of
// that asymmetry.
struct PtzControlsView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.scenePhase) private var scenePhase
    let cameraId: UUID

    private static let speed = 0.5

    // Commands are queued rather than fired independently. Two unstructured Tasks - one per
    // press, one per release - are two HTTP requests with no ordering between them, and on a
    // quick tap they are in flight at the same time. Whenever the stop won that race the camera
    // was sent stop-then-move and kept turning, which is exactly the "sometimes it sticks and
    // spins" this control was reported for: a coin flip on request timing, harmless on a long
    // press (where the release lands long after the move) and frequent on a quick tap.
    @StateObject private var commands = PtzCommandQueue()

    var body: some View {
        HStack(alignment: .bottom, spacing: 6) {
            padGrid
            zoomStack
        }
        .padding(8)
        .background(.black.opacity(0.35), in: RoundedRectangle(cornerRadius: 8))
        // Two backstops for a press whose release never arrives as a release. Sending a stop when
        // nothing is moving is harmless - Stop on an idle camera is a no-op - so neither of these
        // needs to know whether a button is actually held.
        .onDisappear { stop() }
        .onChange(of: scenePhase) { _, phase in
            // Backgrounding mid-press: the gesture is torn down with the view hierarchy, and a
            // camera left turning while the phone is in a pocket is the worst version of this bug.
            if phase != .active { stop() }
        }
    }

    private var padGrid: some View {
        let directions: [(pan: Double, tilt: Double, icon: String)?] = [
            (-1, 1, "arrow.up.left"), (0, 1, "arrow.up"), (1, 1, "arrow.up.right"),
            (-1, 0, "arrow.left"), nil, (1, 0, "arrow.right"),
            (-1, -1, "arrow.down.left"), (0, -1, "arrow.down"), (1, -1, "arrow.down.right")
        ]
        let columns = Array(
            repeating: GridItem(.fixed(PtzButton.side), spacing: PtzButton.gap),
            count: 3)
        return LazyVGrid(columns: columns, spacing: PtzButton.gap) {
            ForEach(Array(directions.enumerated()), id: \.offset) { _, dir in
                if let dir {
                    PtzButton(systemImage: dir.icon) {
                        move(pan: dir.pan * Self.speed, tilt: dir.tilt * Self.speed, zoom: 0)
                    } onRelease: {
                        stop()
                    }
                } else {
                    PtzButton(systemImage: "house.fill", tint: SentinelTheme.cyan) {
                        // Home is a one-shot goto, not a continuous move, so it needs no release -
                        // but it still goes through the queue so it can't overtake a stop.
                        let client = model.client
                        commands.enqueue { try? await client.ptzHome(cameraId: cameraId) }
                    } onRelease: {}
                }
            }
        }
    }

    private var zoomStack: some View {
        VStack(spacing: PtzButton.gap) {
            PtzButton(systemImage: "plus") {
                move(pan: 0, tilt: 0, zoom: Self.speed)
            } onRelease: {
                stop()
            }
            PtzButton(systemImage: "minus") {
                move(pan: 0, tilt: 0, zoom: -Self.speed)
            } onRelease: {
                stop()
            }
        }
    }

    private func move(pan: Double, tilt: Double, zoom: Double) {
        let client = model.client
        let id = cameraId
        commands.enqueue { try? await client.ptzMove(cameraId: id, pan: pan, tilt: tilt, zoom: zoom) }
    }

    // Unlike a move, a stop that fails is retried. The rest of this app treats PTZ as
    // fire-and-forget because a command that didn't land is obvious from the picture - but that
    // reasoning only holds for commands whose failure means "nothing happened". A stop that
    // doesn't land means the camera never stops, which no amount of looking at the video fixes.
    private func stop() {
        let client = model.client
        let id = cameraId
        commands.enqueue {
            for attempt in 0..<ptzStopAttempts {
                do {
                    try await client.ptzStop(cameraId: id)
                    return
                } catch {
                    guard attempt < ptzStopAttempts - 1 else { return }
                    // Short backoff, doubling. The whole sequence is under a second, because a
                    // camera that is still turning is not something to be patient about.
                    try? await Task.sleep(for: .milliseconds(150 << attempt))
                }
            }
        }
    }

}

// File scope rather than a static on the view: the retry runs inside a detached, Sendable closure,
// and a static on a SwiftUI View is MainActor-isolated and unreachable from there.
private let ptzStopAttempts = 3

// Serialises PTZ commands so a release can never overtake the press it belongs to. Each enqueued
// operation waits for the previous one to finish, which is what makes "move then stop" arrive at
// the camera in that order no matter how fast the two events fire.
//
// Chaining onto the previous task rather than using a queue of closures keeps this to a few
// lines, and the chain is naturally bounded: a finished task's value is already available, so a
// burst of taps collapses as fast as the requests complete rather than accumulating.
@MainActor
private final class PtzCommandQueue: ObservableObject {
    private var pending: Task<Void, Never>?

    func enqueue(_ operation: @escaping @Sendable () async -> Void) {
        let previous = pending
        pending = Task { @MainActor in
            await previous?.value
            await operation()
        }
    }
}

// A press-and-hold button. onPress fires when the finger goes down, onRelease when it comes up -
// or when the press is cancelled, which is the case a plain DragGesture cannot report.
//
// This used to be a DragGesture(minimumDistance: 0) reading press from onChanged and release from
// onEnded. onEnded does not fire when a gesture is *cancelled* rather than ended - the parent
// claiming the touch, the app backgrounding, a system gesture starting - and every one of those
// left the camera turning with no stop behind it. The fullscreen camera view makes that a live
// concern rather than a theoretical one: it layers digital zoom's own drag and magnify gestures
// over the same surface, so a finger that slides off an arrow can genuinely have its touch taken
// away. onLongPressGesture's `pressing` closure reports the transition either way, which is the
// same guarantee the Android control already gets from a finally block.
private struct PtzButton: View {
    static let side: CGFloat = 44 // Apple's minimum touch target; these were 30x30 and easy to miss
    static let gap: CGFloat = 6

    // Long enough that the press never "completes" on its own - this gesture is only ever used
    // for its pressing/not-pressing transitions, and `perform` is deliberately empty.
    private static let holdIndefinitely: Double = 86_400

    // How far the finger may stray before the press is treated as over. Roughly half a button, so
    // sliding onto a neighbouring arrow ends the move rather than silently steering the camera in
    // a direction nobody is pressing.
    private static let slipDistance: CGFloat = 24

    let systemImage: String
    var tint: Color = SentinelTheme.green
    let onPress: () -> Void
    let onRelease: () -> Void

    @State private var isPressed = false

    var body: some View {
        Image(systemName: systemImage)
            .font(.system(size: 17, weight: .semibold))
            .foregroundStyle(tint)
            .frame(width: Self.side, height: Self.side)
            // Visible confirmation of what the finger actually caught, which a 3x3 grid of arrows
            // on a phone badly needs - previously nothing about the button changed on press.
            .background(
                .black.opacity(isPressed ? 0.85 : 0.55),
                in: RoundedRectangle(cornerRadius: 6))
            .overlay(
                RoundedRectangle(cornerRadius: 6)
                    .stroke(tint.opacity(isPressed ? 1 : 0.4), lineWidth: isPressed ? 2 : 1))
            .scaleEffect(isPressed ? 0.94 : 1)
            .animation(.easeOut(duration: 0.08), value: isPressed)
            .contentShape(Rectangle())
            .sensoryFeedback(.impact(weight: .light), trigger: isPressed)
            .onLongPressGesture(
                minimumDuration: Self.holdIndefinitely,
                maximumDistance: Self.slipDistance,
                pressing: { pressing in
                    if pressing {
                        guard !isPressed else { return }
                        isPressed = true
                        onPress()
                    } else {
                        guard isPressed else { return }
                        isPressed = false
                        onRelease()
                    }
                },
                perform: { /* never reached - see holdIndefinitely */ })
    }
}
