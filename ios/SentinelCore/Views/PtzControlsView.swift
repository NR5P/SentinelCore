import SwiftUI

// Continuous-move semantics: pressing a direction starts the camera moving at a fixed moderate
// speed, and it keeps moving until released - there's no ONVIF "move this far and stop
// automatically" in this mode. Errors are swallowed on purpose (fire-and-forget) - a dropped PTZ
// command just means the camera didn't move that instant, which is self-evident from the video
// itself, not something worth surfacing as an error over.
struct PtzControlsView: View {
    @EnvironmentObject private var model: AppModel
    let cameraId: UUID

    private static let speed = 0.5

    var body: some View {
        HStack(alignment: .bottom, spacing: 6) {
            padGrid
            zoomStack
        }
        .padding(8)
        .background(.black.opacity(0.35), in: RoundedRectangle(cornerRadius: 8))
    }

    private var padGrid: some View {
        let directions: [(pan: Double, tilt: Double, icon: String)?] = [
            (-1, 1, "arrow.up.left"), (0, 1, "arrow.up"), (1, 1, "arrow.up.right"),
            (-1, 0, "arrow.left"), nil, (1, 0, "arrow.right"),
            (-1, -1, "arrow.down.left"), (0, -1, "arrow.down"), (1, -1, "arrow.down.right")
        ]
        return LazyVGrid(columns: Array(repeating: GridItem(.fixed(30), spacing: 3), count: 3), spacing: 3) {
            ForEach(Array(directions.enumerated()), id: \.offset) { _, dir in
                if let dir {
                    PtzButton(systemImage: dir.icon) {
                        move(pan: dir.pan * Self.speed, tilt: dir.tilt * Self.speed, zoom: 0)
                    } onRelease: {
                        stop()
                    }
                } else {
                    PtzButton(systemImage: "house.fill", tint: SentinelTheme.cyan) {
                        Task { try? await model.client.ptzHome(cameraId: cameraId) }
                    } onRelease: {}
                }
            }
        }
    }

    private var zoomStack: some View {
        VStack(spacing: 3) {
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
        Task { try? await model.client.ptzMove(cameraId: cameraId, pan: pan, tilt: tilt, zoom: zoom) }
    }

    private func stop() {
        Task { try? await model.client.ptzStop(cameraId: cameraId) }
    }
}

// A drag gesture with zero minimum distance, rather than a plain Button, so press/release map
// directly to onPress/onRelease (start-moving/stop-moving) instead of only firing once per tap -
// SwiftUI's Button only exposes a single completed action, no separate "still held down" state.
private struct PtzButton: View {
    let systemImage: String
    var tint: Color = SentinelTheme.green
    let onPress: () -> Void
    let onRelease: () -> Void

    @State private var isPressed = false

    var body: some View {
        Image(systemName: systemImage)
            .font(.system(size: 13, weight: .semibold))
            .foregroundStyle(tint)
            .frame(width: 30, height: 30)
            .background(.black.opacity(0.55), in: RoundedRectangle(cornerRadius: 4))
            .overlay(RoundedRectangle(cornerRadius: 4).stroke(tint.opacity(0.4), lineWidth: 1))
            .contentShape(Rectangle())
            .simultaneousGesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { _ in
                        guard !isPressed else { return }
                        isPressed = true
                        onPress()
                    }
                    .onEnded { _ in
                        isPressed = false
                        onRelease()
                    }
            )
    }
}
