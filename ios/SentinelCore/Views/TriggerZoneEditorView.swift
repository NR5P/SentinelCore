// TODO: multi-zone. This editor still edits a single zone; the server supports several per
// camera, each with its own minimum object size. See core/docs/MULTI_ZONE_CLIENT_HANDOFF.md
// for the wire format and the two rules that must not be broken.
import SwiftUI
#if os(iOS)
import UIKit
typealias PlatformImage = UIImage
#elseif os(macOS)
import AppKit
typealias PlatformImage = NSImage
#endif

extension Image {
    fileprivate init(platformImage: PlatformImage) {
        #if os(iOS)
        self.init(uiImage: platformImage)
        #elseif os(macOS)
        self.init(nsImage: platformImage)
        #endif
    }
}

struct TriggerZoneEditorView: View {
    @EnvironmentObject private var model: AppModel
    let camera: Camera

    @State private var referenceImage: PlatformImage?
    @State private var loadError: String?
    @State private var isLoading = false

    @State private var polygonPoints: [Point2D] = []
    @State private var minTriggerArea: Double = 0.02
    @State private var enabled = false
    @State private var cooldownSeconds = 5
    @State private var maxTriggersPerMinute = 6
    @State private var shadowSensitivity: Double = 50
    @State private var recordClips = false
    @State private var clipPreSeconds = 5
    @State private var clipPostSeconds = 10
    @State private var sendToAlerts = false
    @State private var alertProfile = "default"
    @State private var profileNames: [PromptProfileName] = []

    @State private var isSaving = false
    @State private var errorMessage: String?

    var body: some View {
        Group {
            if let referenceImage {
                Form {
                    Section("Zone") {
                        PolygonZoneOverlay(image: referenceImage, points: $polygonPoints)
                            .aspectRatio(imageAspectRatio(referenceImage), contentMode: .fit)
                            .clipShape(RoundedRectangle(cornerRadius: 8))
                        HStack {
                            Button("Undo Last Point") { polygonPoints.removeLast() }
                                .disabled(polygonPoints.isEmpty)
                            Spacer()
                            Button("Clear All", role: .destructive) { polygonPoints.removeAll() }
                                .disabled(polygonPoints.isEmpty)
                        }
                        Toggle("Enabled", isOn: $enabled)
                    }

                    Section("Minimum Trigger Size") {
                        Text("Drag anywhere on the image to resize the circle to the smallest object size that should count as motion.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        MinAreaCircleOverlay(image: referenceImage, minTriggerArea: $minTriggerArea)
                            .aspectRatio(imageAspectRatio(referenceImage), contentMode: .fit)
                            .clipShape(RoundedRectangle(cornerRadius: 8))
                    }

                    Section("Detection") {
                        Stepper("Cooldown: \(cooldownSeconds)s", value: $cooldownSeconds, in: 0...300)
                        Stepper("Max Triggers / Minute: \(maxTriggersPerMinute)", value: $maxTriggersPerMinute, in: 1...60)
                        VStack(alignment: .leading) {
                            Text("Shadow Sensitivity: \(Int(shadowSensitivity))")
                            Slider(value: $shadowSensitivity, in: 0...100)
                        }
                    }

                    Section("Clips & Alerts") {
                        Toggle("Record Clips", isOn: $recordClips)
                        if recordClips {
                            Stepper("Pre-roll: \(clipPreSeconds)s", value: $clipPreSeconds, in: 0...60)
                            Stepper("Post-roll: \(clipPostSeconds)s", value: $clipPostSeconds, in: 0...120)
                        }
                        Toggle("Send to Alerts", isOn: $sendToAlerts)
                        Picker("Alert Profile", selection: $alertProfile) {
                            ForEach(profileOptions) { profile in
                                Text(profile.displayName).tag(profile.key)
                            }
                        }
                    }

                    if let errorMessage {
                        Section {
                            Text(errorMessage).foregroundStyle(SentinelTheme.red)
                        }
                    }
                }
                .formStyle(.grouped)
            } else if isLoading {
                ProgressView("Loading reference frame…")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ContentUnavailableView(
                    "Couldn't load reference frame",
                    systemImage: "video.slash",
                    description: Text(loadError ?? "Unknown error")
                )
                .overlay(alignment: .bottom) {
                    Button("Retry") { Task { await load() } }
                        .buttonStyle(.borderedProminent)
                        .padding(.bottom, 32)
                }
            }
        }
        .navigationTitle("Trigger Zone")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItem(placement: .confirmationAction) {
                Button(isSaving ? "Saving…" : "Save") {
                    Task { await save() }
                }
                .disabled(isSaving)
            }
        }
        .task { await load() }
    }

    // Falls back to alertProfile appearing as its own option if it's no longer in the fetched
    // list (a renamed/deleted profile) - otherwise saving would silently revert it to "default."
    private var profileOptions: [PromptProfileName] {
        var options = profileNames
        if !alertProfile.isEmpty, !options.contains(where: { $0.key == alertProfile }) {
            options.append(PromptProfileName(key: alertProfile, displayName: alertProfile))
        }
        return options
    }

    private func imageAspectRatio(_ image: PlatformImage) -> CGFloat {
        let size = image.size
        return size.height > 0 ? size.width / size.height : 16 / 9
    }

    private func load() async {
        isLoading = true
        loadError = nil
        defer { isLoading = false }
        do {
            async let imageDataTask = model.client.fetchReferenceFrame(cameraId: camera.id)
            async let configTask = model.client.fetchTriggerConfig(cameraId: camera.id)
            async let profilesTask = model.client.fetchPromptProfileNames()
            let (imageData, config, profiles) = try await (imageDataTask, configTask, profilesTask)
            guard let image = PlatformImage(data: imageData) else {
                loadError = "The camera's reference frame couldn't be decoded."
                return
            }
            profileNames = profiles
            apply(config)
            referenceImage = image
        } catch {
            loadError = error.localizedDescription
        }
    }

    private func apply(_ config: TriggerConfig) {
        enabled = config.enabled
        polygonPoints = config.zonePolygon
        minTriggerArea = config.minTriggerArea
        cooldownSeconds = config.cooldownSeconds
        maxTriggersPerMinute = config.maxTriggersPerMinute
        shadowSensitivity = Double(config.shadowSensitivity)
        recordClips = config.recordClips
        clipPreSeconds = config.clipPreSeconds
        clipPostSeconds = config.clipPostSeconds
        sendToAlerts = config.sendToAlerts
        alertProfile = config.alertProfile
    }

    private func validationError() -> String? {
        if enabled && polygonPoints.count < 3 {
            return "Draw a zone with at least 3 points, or turn off Enabled."
        }
        if recordClips && clipPreSeconds + clipPostSeconds <= 0 {
            return "Set at least one second of pre-roll or post-roll to record clips."
        }
        return nil
    }

    private func save() async {
        if let validationError = validationError() {
            errorMessage = validationError
            return
        }
        isSaving = true
        defer { isSaving = false }
        errorMessage = nil
        let config = TriggerConfig(
            enabled: enabled,
            zonePolygon: polygonPoints,
            minTriggerArea: minTriggerArea,
            cooldownSeconds: cooldownSeconds,
            maxTriggersPerMinute: maxTriggersPerMinute,
            shadowSensitivity: Int(shadowSensitivity),
            recordClips: recordClips,
            clipPreSeconds: clipPreSeconds,
            clipPostSeconds: clipPostSeconds,
            sendToAlerts: sendToAlerts,
            alertProfile: alertProfile
        )
        do {
            let saved = try await model.client.saveTriggerConfig(cameraId: camera.id, config)
            apply(saved)
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

// Fits `imageSize` into `containerSize` the same way `.scaledToFit()` does, so tap/drag
// locations (measured in the container's coordinate space) can be mapped back to fractional
// image coordinates - naive `location / containerSize` would misplace every point whenever the
// camera's aspect ratio doesn't exactly match the container's.
private func aspectFitRect(imageSize: CGSize, in containerSize: CGSize) -> CGRect {
    guard imageSize.width > 0, imageSize.height > 0, containerSize.width > 0, containerSize.height > 0 else {
        return CGRect(origin: .zero, size: containerSize)
    }
    let scale = min(containerSize.width / imageSize.width, containerSize.height / imageSize.height)
    let renderedSize = CGSize(width: imageSize.width * scale, height: imageSize.height * scale)
    let origin = CGPoint(
        x: (containerSize.width - renderedSize.width) / 2,
        y: (containerSize.height - renderedSize.height) / 2
    )
    return CGRect(origin: origin, size: renderedSize)
}

private struct PolygonZoneOverlay: View {
    let image: PlatformImage
    @Binding var points: [Point2D]

    var body: some View {
        GeometryReader { geo in
            let imageRect = aspectFitRect(imageSize: image.size, in: geo.size)
            ZStack(alignment: .topLeading) {
                Image(platformImage: image)
                    .resizable()
                    .scaledToFit()
                Canvas { context, _ in
                    guard !points.isEmpty else { return }
                    var path = Path()
                    for (index, point) in points.enumerated() {
                        let p = CGPoint(
                            x: imageRect.minX + point.x * imageRect.width,
                            y: imageRect.minY + point.y * imageRect.height
                        )
                        if index == 0 { path.move(to: p) } else { path.addLine(to: p) }
                    }
                    if points.count >= 3 { path.closeSubpath() }
                    context.fill(path, with: .color(SentinelTheme.green.opacity(0.18)))
                    context.stroke(path, with: .color(SentinelTheme.green), lineWidth: 2)
                    for point in points {
                        let p = CGPoint(
                            x: imageRect.minX + point.x * imageRect.width,
                            y: imageRect.minY + point.y * imageRect.height
                        )
                        context.fill(
                            Path(ellipseIn: CGRect(x: p.x - 5, y: p.y - 5, width: 10, height: 10)),
                            with: .color(SentinelTheme.green)
                        )
                    }
                }
            }
            .contentShape(Rectangle())
            .onTapGesture { location in
                guard imageRect.contains(location) else { return }
                let fractionX = (location.x - imageRect.minX) / imageRect.width
                let fractionY = (location.y - imageRect.minY) / imageRect.height
                points.append(Point2D(
                    x: min(max(fractionX, 0), 1),
                    y: min(max(fractionY, 0), 1)
                ))
            }
        }
    }
}

private struct MinAreaCircleOverlay: View {
    let image: PlatformImage
    @Binding var minTriggerArea: Double

    var body: some View {
        GeometryReader { geo in
            let imageRect = aspectFitRect(imageSize: image.size, in: geo.size)
            let center = CGPoint(x: imageRect.midX, y: imageRect.midY)
            let radius = radius(for: minTriggerArea, in: imageRect)
            ZStack {
                Image(platformImage: image)
                    .resizable()
                    .scaledToFit()
                Canvas { context, _ in
                    let circleRect = CGRect(
                        x: center.x - radius, y: center.y - radius,
                        width: radius * 2, height: radius * 2
                    )
                    context.fill(Path(ellipseIn: circleRect), with: .color(SentinelTheme.cyan.opacity(0.15)))
                    context.stroke(Path(ellipseIn: circleRect), with: .color(SentinelTheme.cyan), lineWidth: 2)
                }
            }
            .contentShape(Rectangle())
            // Dragging anywhere in this pane resizes the circle - simpler and more reliable on
            // touch than requiring a precise grab on a small handle, and this control has no
            // other interactive purpose.
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { value in
                        let dx = value.location.x - center.x
                        let dy = value.location.y - center.y
                        let maxRadius = min(imageRect.width, imageRect.height) / 2
                        let newRadius = min(max(sqrt(dx * dx + dy * dy), 10), maxRadius)
                        let area = (Double.pi * newRadius * newRadius) / (imageRect.width * imageRect.height)
                        minTriggerArea = min(max(area, 0.0001), 1)
                    }
            )
        }
    }

    private func radius(for area: Double, in rect: CGRect) -> CGFloat {
        CGFloat(sqrt(area * rect.width * rect.height / .pi))
    }
}
