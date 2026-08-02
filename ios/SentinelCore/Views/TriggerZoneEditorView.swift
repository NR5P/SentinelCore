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

// The detector works on a fixed 320x180 grid (MotionDetector.GridWidth/GridHeight), so a size
// expressed as a fraction of the frame turns into this many pixels - the number the trigger
// threshold is actually compared against, and far easier to reason about than "0.0015". For
// reference, a person standing at a gate roughly 30 feet away measures around 160.
private let detectorPixels = 320.0 * 180.0

private func detectorPixelCount(_ minTriggerArea: Double) -> Int {
    Int((minTriggerArea * detectorPixels).rounded())
}

struct TriggerZoneEditorView: View {
    @EnvironmentObject private var model: AppModel
    let camera: Camera

    @State private var referenceImage: PlatformImage?
    @State private var loadError: String?
    @State private var isLoading = false

    // Always at least one entry - see TriggerConfig.resolvedZones. A camera with no zone at all
    // has nothing to mirror into the legacy zonePolygon/minTriggerArea fields.
    @State private var zones: [TriggerZone] = [TriggerZone(name: TriggerZone.defaultName, polygon: [], minTriggerArea: 0.02)]
    @State private var activeZone = 0
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

    // Not editable here, only carried: the sensor binding is set up on the web dashboard, and the
    // PUT overwrites the whole record, so dropping these would unbind the camera's sensor every
    // time someone nudged a polygon from their phone.
    @State private var sensorDeviceId = ""
    @State private var sensorTriggerOn = "on"
    @State private var sensorCooldownSeconds = 30

    @State private var isRenaming = false
    @State private var renameText = ""

    @State private var isSaving = false
    @State private var errorMessage: String?

    var body: some View {
        Group {
            if let referenceImage {
                Form {
                    Section("Zones") {
                        Text("Each zone has its own minimum object size. Draw one zone per distance: something far from the camera covers far fewer pixels than the same thing up close, so a single size can't work for the whole view.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        zoneSelector
                        PolygonZoneOverlay(image: referenceImage, zones: $zones, activeZone: activeZone)
                            .aspectRatio(imageAspectRatio(referenceImage), contentMode: .fit)
                            .clipShape(RoundedRectangle(cornerRadius: 8))
                        HStack {
                            Button("Undo Last Point") { withActiveZone { $0.polygon.removeLast() } }
                                .disabled(activeZonePolygon.isEmpty)
                            Spacer()
                            Button("Clear All", role: .destructive) { withActiveZone { $0.polygon.removeAll() } }
                                .disabled(activeZonePolygon.isEmpty)
                        }
                        HStack {
                            Button("Add Zone", action: addZone)
                            Spacer()
                            Button("Rename", action: startRenaming)
                            Spacer()
                            Button("Delete Zone", role: .destructive, action: deleteActiveZone)
                                .disabled(zones.count <= 1)
                        }
                        Toggle("Enabled", isOn: $enabled)
                    }

                    Section("Minimum Trigger Size") {
                        Text("Drag anywhere on the image to resize the circle to the smallest object size that should count as motion. This applies to \(activeZoneName) only.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        if zones.indices.contains(activeZone) {
                            MinAreaCircleOverlay(
                                image: referenceImage,
                                minTriggerArea: $zones[activeZone].minTriggerArea
                            )
                            .aspectRatio(imageAspectRatio(referenceImage), contentMode: .fit)
                            .clipShape(RoundedRectangle(cornerRadius: 8))
                            Text("Ignores anything under \(detectorPixelCount(zones[activeZone].minTriggerArea)) detector pixels.")
                                .font(.caption2)
                                .foregroundStyle(.secondary)
                        }
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
        .alert("Rename Zone", isPresented: $isRenaming) {
            TextField("Zone name", text: $renameText)
            Button("Cancel", role: .cancel) {}
            Button("Rename") { commitRename() }
        } message: {
            Text("The name appears on trigger cards, so you can tell which area fired.")
        }
        .task { await load() }
    }

    // A row rather than a Picker: zone names are user-typed and carry a pixel size alongside, which
    // a segmented control would truncate to nothing once there are more than two or three.
    private var zoneSelector: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) {
                ForEach(Array(zones.enumerated()), id: \.offset) { index, zone in
                    let isActive = index == activeZone
                    Button {
                        activeZone = index
                    } label: {
                        Text("\(zone.name) · \(detectorPixelCount(zone.minTriggerArea))px")
                            .font(.caption)
                            .padding(.horizontal, 10)
                            .padding(.vertical, 6)
                            .background(
                                RoundedRectangle(cornerRadius: 6)
                                    .fill(isActive ? SentinelTheme.green.opacity(0.25) : Color.secondary.opacity(0.12))
                            )
                            .overlay(
                                RoundedRectangle(cornerRadius: 6)
                                    .stroke(isActive ? SentinelTheme.green : Color.clear, lineWidth: 1)
                            )
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding(.vertical, 2)
        }
    }

    private var activeZoneName: String {
        zones.indices.contains(activeZone) ? zones[activeZone].name : TriggerZone.defaultName
    }

    private var activeZonePolygon: [Point2D] {
        zones.indices.contains(activeZone) ? zones[activeZone].polygon : []
    }

    private func withActiveZone(_ mutate: (inout TriggerZone) -> Void) {
        guard zones.indices.contains(activeZone) else { return }
        mutate(&zones[activeZone])
    }

    // A new zone starts empty and selected, and inherits the current zone's size as a starting
    // point - you're usually adding a zone for a different distance, so it's a more useful default
    // than the stock 2% would be.
    private func addZone() {
        let inherited = zones.indices.contains(activeZone) ? zones[activeZone].minTriggerArea : 0.02
        zones.append(TriggerZone(name: "Zone \(zones.count + 1)", polygon: [], minTriggerArea: inherited))
        activeZone = zones.count - 1
        errorMessage = nil
    }

    private func startRenaming() {
        renameText = activeZoneName
        isRenaming = true
    }

    private func commitRename() {
        // 50 characters is the server's limit; trimming to it here turns a save-time rejection
        // into something the user never sees.
        let trimmed = String(renameText.trimmingCharacters(in: .whitespacesAndNewlines).prefix(50))
        guard !trimmed.isEmpty else { return }
        withActiveZone { $0.name = trimmed }
    }

    // A camera always keeps at least one zone: the legacy zonePolygon/minTriggerArea fields are
    // mirrored from the primary, so there has to be a primary to mirror.
    private func deleteActiveZone() {
        guard zones.count > 1 else {
            errorMessage = "A camera needs at least one zone - clear its points instead."
            return
        }
        guard zones.indices.contains(activeZone) else { return }
        zones.remove(at: activeZone)
        activeZone = max(0, activeZone - 1)
        errorMessage = nil
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
        // resolvedZones, not zones: a server predating multi-zone sends no list at all, and this
        // editor needs one zone to work with either way.
        zones = config.resolvedZones
        activeZone = min(activeZone, zones.count - 1)
        cooldownSeconds = config.cooldownSeconds
        maxTriggersPerMinute = config.maxTriggersPerMinute
        shadowSensitivity = Double(config.shadowSensitivity)
        recordClips = config.recordClips
        clipPreSeconds = config.clipPreSeconds
        clipPostSeconds = config.clipPostSeconds
        sendToAlerts = config.sendToAlerts
        alertProfile = config.alertProfile
        sensorDeviceId = config.sensorDeviceId
        sensorTriggerOn = config.sensorTriggerOn
        sensorCooldownSeconds = config.sensorCooldownSeconds
    }

    // Mirrors the server's own validation so the common mistakes are caught before a round-trip,
    // named per zone - a message about the fourth zone shouldn't read as though it were about the
    // one on screen.
    private func validationError() -> String? {
        if enabled {
            if let unfinished = zones.first(where: { $0.polygon.count < 3 }) {
                return "\"\(unfinished.name)\" needs at least 3 points, or turn off Enabled."
            }
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
        // zonePolygon/minTriggerArea go out as well as zones, mirroring the primary: the server
        // and any other client still read those two, and the server treats zones as authoritative
        // when it's present. Sending the full list every time is the contract for an editor that
        // understands zones - see core/docs/MULTI_ZONE_CLIENT_HANDOFF.md.
        let primary = zones.first ?? TriggerZone(name: TriggerZone.defaultName, polygon: [], minTriggerArea: 0.02)
        let config = TriggerConfig(
            enabled: enabled,
            zonePolygon: primary.polygon,
            minTriggerArea: primary.minTriggerArea,
            zones: zones,
            cooldownSeconds: cooldownSeconds,
            maxTriggersPerMinute: maxTriggersPerMinute,
            shadowSensitivity: Int(shadowSensitivity),
            recordClips: recordClips,
            clipPreSeconds: clipPreSeconds,
            clipPostSeconds: clipPostSeconds,
            sendToAlerts: sendToAlerts,
            alertProfile: alertProfile,
            sensorDeviceId: sensorDeviceId,
            sensorTriggerOn: sensorTriggerOn,
            sensorCooldownSeconds: sensorCooldownSeconds
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

// Every zone is drawn, but only the selected one is solid and shows its vertices - the others are
// dashed and faint so you can see how they line up without mistaking them for the shape your taps
// are going into. The selected zone is drawn last so it sits on top.
private struct PolygonZoneOverlay: View {
    let image: PlatformImage
    @Binding var zones: [TriggerZone]
    let activeZone: Int

    var body: some View {
        GeometryReader { geo in
            let imageRect = aspectFitRect(imageSize: image.size, in: geo.size)
            ZStack(alignment: .topLeading) {
                Image(platformImage: image)
                    .resizable()
                    .scaledToFit()
                Canvas { context, _ in
                    for index in drawOrder {
                        draw(zone: zones[index], isActive: index == activeZone, in: imageRect, context: &context)
                    }
                }
            }
            .contentShape(Rectangle())
            .onTapGesture { location in
                guard imageRect.contains(location), zones.indices.contains(activeZone) else { return }
                let fractionX = (location.x - imageRect.minX) / imageRect.width
                let fractionY = (location.y - imageRect.minY) / imageRect.height
                zones[activeZone].polygon.append(Point2D(
                    x: min(max(fractionX, 0), 1),
                    y: min(max(fractionY, 0), 1)
                ))
            }
        }
    }

    private var drawOrder: [Int] {
        let others = zones.indices.filter { $0 != activeZone }
        return zones.indices.contains(activeZone) ? others + [activeZone] : others
    }

    private func draw(zone: TriggerZone, isActive: Bool, in imageRect: CGRect, context: inout GraphicsContext) {
        guard !zone.polygon.isEmpty else { return }

        func position(_ point: Point2D) -> CGPoint {
            CGPoint(
                x: imageRect.minX + point.x * imageRect.width,
                y: imageRect.minY + point.y * imageRect.height
            )
        }

        var path = Path()
        for (index, point) in zone.polygon.enumerated() {
            if index == 0 { path.move(to: position(point)) } else { path.addLine(to: position(point)) }
        }
        if zone.polygon.count >= 3 { path.closeSubpath() }

        let color = SentinelTheme.green
        context.fill(path, with: .color(color.opacity(isActive ? 0.18 : 0.06)))
        context.stroke(
            path,
            with: .color(isActive ? color : color.opacity(0.45)),
            style: StrokeStyle(lineWidth: 2, dash: isActive ? [] : [4, 3])
        )

        if let first = zone.polygon.first {
            context.draw(
                Text(zone.name).font(.caption2).foregroundStyle(color.opacity(isActive ? 1 : 0.5)),
                at: CGPoint(x: position(first).x + 4, y: position(first).y - 10),
                anchor: .topLeading
            )
        }

        guard isActive else { return }
        for point in zone.polygon {
            let p = position(point)
            context.fill(
                Path(ellipseIn: CGRect(x: p.x - 5, y: p.y - 5, width: 10, height: 10)),
                with: .color(color)
            )
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
