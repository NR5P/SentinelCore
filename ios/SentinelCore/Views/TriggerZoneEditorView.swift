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

// The smallest size a zone can be set to. Four detector pixels is about as small as the grid can
// meaningfully express - below a couple of pixels a blob is indistinguishable from sensor noise -
// and it is far below what this editor used to allow. The old drag-only circle clamped its radius
// to 10 *view points*, so the floor depended on how big the pane happened to be: on a phone that
// bottomed out around 250 detector pixels, which is roughly a person at 40 feet. Anything smaller
// or further away simply couldn't be asked for.
private let minDetectorPixels = 4.0

private func detectorPixelCount(_ minTriggerArea: Double) -> Int {
    Int((minTriggerArea * detectorPixels).rounded())
}

// The server rejects anything outside (0, 1], and everything that writes minTriggerArea goes
// through here so neither end can be reached by accident.
private func triggerArea(detectorPixels pixels: Double) -> Double {
    min(max(pixels, minDetectorPixels), detectorPixels) / detectorPixels
}

// The slider is logarithmic. A linear one over a range this wide (4 to 57600) would put every
// useful small size inside its first millimetre of travel, which is exactly the end of the range
// this control exists to reach.
private func sliderPosition(area: Double) -> Double {
    let pixels = min(max(area * detectorPixels, minDetectorPixels), detectorPixels)
    return log(pixels / minDetectorPixels) / log(detectorPixels / minDetectorPixels)
}

private func triggerArea(sliderPosition position: Double) -> Double {
    let clamped = min(max(position, 0), 1)
    let pixels = minDetectorPixels * pow(detectorPixels / minDetectorPixels, clamped)
    return triggerArea(detectorPixels: pixels.rounded())
}

// A single detector pixel at the small end, 5% further up. Even a log slider can't be dragged to
// an exact value of 6 with a fingertip, and 6 versus 7 is a real difference down there.
private func nudgedArea(_ area: Double, by direction: Double) -> Double {
    let pixels = (area * detectorPixels).rounded()
    let step = pixels < 50 ? 1 : (pixels * 0.05).rounded()
    return triggerArea(detectorPixels: pixels + direction * step)
}

// Whole hours on a 24-hour clock, which is how the server stores and reads them. Written out
// rather than run through a DateFormatter because these are bare hour numbers with no date behind
// them, and a locale that formats 21 as "9 PM" would still have to round-trip back to 21.
private func hourLabel(_ hour: Int) -> String {
    String(format: "%02d:00", hour)
}

// Length of the pause, wrapping past midnight exactly as TriggerNightWindow.IsActive does: 21 -> 7
// is ten hours, not minus fourteen. Zero means start == end, the zero-width window the server
// refuses to save while the feature is on.
private func nightWindowHours(from start: Int, to end: Int) -> Int {
    let span = end - start
    return span > 0 ? span : span + 24
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

    // Whole-camera, not per-zone: the server holds one window per trigger config. Defaults match
    // TriggerConfig's - off, 21:00-07:00 - so a server that doesn't know about the feature shows
    // the same thing a freshly migrated one does.
    @State private var nightWindowEnabled = false
    @State private var nightStartHour = 21
    @State private var nightEndHour = 7

    // Whole-camera too, and off by default on both counts - matching TriggerConfig's own
    // defaults, so a server that predates either column shows what a freshly migrated one does.
    @State private var bypassFastLocal = false
    @State private var keepOnlyAlertable = false

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
    @StateObject private var feedback = SaveFeedback()

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
                        // A row of its own rather than a third button beside Undo/Clear: those
                        // three titles measure ~313pt together, which is wider than a Form row on
                        // a small phone before Dynamic Type is turned up at all, and the ends
                        // would truncate.
                        Button("Cover Everything") {
                            withActiveZone { $0.polygon = TriggerZone.wholeFramePolygon }
                        }
                        .disabled(activeZonePolygon == TriggerZone.wholeFramePolygon)
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
                        Text("The circle is the smallest object size that should count as motion in \(activeZoneName). Set it with the slider below, or drag on the image for a rough size.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        if zones.indices.contains(activeZone) {
                            MinAreaCircleOverlay(
                                image: referenceImage,
                                minTriggerArea: $zones[activeZone].minTriggerArea
                            )
                            .aspectRatio(imageAspectRatio(referenceImage), contentMode: .fit)
                            .clipShape(RoundedRectangle(cornerRadius: 8))
                            MinAreaSlider(minTriggerArea: $zones[activeZone].minTriggerArea)
                            Text("Ignores anything under \(detectorPixelCount(zones[activeZone].minTriggerArea)) detector pixels. Sizes in the low tens will also pick up rain, insects and camera noise.")
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

                    Section("Overnight Pause") {
                        Toggle("Pause Triggers Overnight", isOn: $nightWindowEnabled)
                        Text("Holds back every motion trigger between the hours below. Meant for a camera whose IR light picks up a spider web, insects or condensation close to the lens after dark: those blobs measure the same on screen as a person further away, so no minimum size can tell them apart - but the time of day can. Nothing is captured, recorded or analysed while paused. A sensor binding still fires normally.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        if nightWindowEnabled {
                            // A picker rather than a text field: there are 24 legal values and no
                            // reason to let someone type 47 and find out at save time.
                            Picker("Pause From", selection: $nightStartHour) {
                                ForEach(0..<24, id: \.self) { hour in
                                    Text(hourLabel(hour)).tag(hour)
                                }
                            }
                            Picker("Resume At", selection: $nightEndHour) {
                                ForEach(0..<24, id: \.self) { hour in
                                    Text(hourLabel(hour)).tag(hour)
                                }
                            }
                            Text(nightWindowSummary)
                                .font(.caption2)
                                .foregroundStyle(nightStartHour == nightEndHour ? SentinelTheme.red : .secondary)
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

                    // Both only do anything while "Send to Alerts" is on - nothing analyses the
                    // frame otherwise - and both are worth turning on for very few cameras, so
                    // they sit in their own section rather than adding two more switches to the
                    // list above.
                    Section("AI Routing") {
                        Toggle("Always Use the Full AI", isOn: $bypassFastLocal)
                        Text("Skips the fast local detector and sends every trigger from this camera straight to the full AI. Only matters while Send to Alerts is on, and only when Fast Local Detection is the primary backend. The detector is trained on a fixed class list with no raccoon, fox, opossum or weasel in it, so at a chicken coop it clears those frames as empty and the model that could name the predator never sees them. The cost is real: every trigger becomes a full analysis of around two minutes, so raise the cooldown above to match or the queue backs up and starts dropping frames unanalysed.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        Toggle("Only Keep What the AI Would Alert On", isOn: $keepOnlyAlertable)
                        Text("Judges this camera's triggers by whether the AI would have alerted on the frame, instead of by whether anything was in it. The normal bar is \"a person, vehicle, package or animal is there\", which is right for a driveway but wrong at a chicken coop: the chickens are animals, they are always in shot, and they confirm every trigger. With this on, ordinary chicken activity is rejected - no event, no video kept, no alert - while a predator still gets all three. The snapshot is kept either way, so Show Hidden still shows everything that fired. Nothing is rejected because the AI was slow, unreachable or errored.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
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
        .saveFeedback(feedback)
        .navigationTitle("Trigger Zone")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItem(placement: .confirmationAction) {
                SaveButton(feedback: feedback, isSaving: isSaving) { Task { await save() } }
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

    // Spelling the window back out is the whole point of this line: 21 -> 7 wraps past midnight,
    // and "10 hours a night" is the sanity check on whether the two numbers say what was meant.
    // The hours are the server's clock, not this device's - a phone in another timezone would
    // otherwise read them as its own.
    private var nightWindowSummary: String {
        if nightStartHour == nightEndHour {
            return "Start and end are the same hour, which is a window zero hours long - triggers would never pause. Pick different hours."
        }
        let hours = nightWindowHours(from: nightStartHour, to: nightEndHour)
        return "Triggers pause at \(hourLabel(nightStartHour)) and resume at \(hourLabel(nightEndHour)) - \(hours) hour\(hours == 1 ? "" : "s") a night, on the server's clock."
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
        // resolvedNightWindow, not the raw optionals: a server predating the feature sends none of
        // the three, and the editor still has to show a window.
        (nightWindowEnabled, nightStartHour, nightEndHour) = config.resolvedNightWindow
        // Resolved, not raw: a server older than either column sends neither key, and the two
        // switches still have to show something.
        bypassFastLocal = config.resolvedBypassFastLocal
        keepOnlyAlertable = config.resolvedKeepOnlyAlertable
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
        // The server refuses this combination outright rather than reading it as "all day" - see
        // TriggerNightWindow. Catching it here says so next to the two pickers that caused it.
        if nightWindowEnabled && nightStartHour == nightEndHour {
            return "The overnight pause can't start and end at the same hour - that window is zero hours long."
        }
        return nil
    }

    private func save() async {
        if let validationError = validationError() {
            errorMessage = validationError
            feedback.failed("Not saved")
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
            sensorCooldownSeconds: sensorCooldownSeconds,
            // All three, always, and never nil from here: the server merges each key it doesn't
            // receive against the stored row, so sending a partial set would save half of what's
            // on screen. See core/docs/NIGHT_WINDOW_HANDOFF.md.
            nightWindowEnabled: nightWindowEnabled,
            nightStartHour: nightStartHour,
            nightEndHour: nightEndHour,
            // Never nil from here either, and for the same reason: this editor knows about both
            // switches, so it owns their value. Omitting them would save half of what's on screen.
            bypassFastLocal: bypassFastLocal,
            keepOnlyAlertable: keepOnlyAlertable
        )
        do {
            let saved = try await model.client.saveTriggerConfig(cameraId: camera.id, config)
            apply(saved)
            feedback.saved()
        } catch {
            errorMessage = error.localizedDescription
            feedback.failed("Not saved")
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

// Why this exists alongside the drag-on-the-image control: on a phone the circle is the thing you
// are aiming at and your fingertip is sitting on top of it, so the smaller it gets the less you
// can see of what you are setting - and small is where this control now spends most of its range.
// The slider is off to one side of the picture, and the -/+ buttons land on an exact value.
private struct MinAreaSlider: View {
    @Binding var minTriggerArea: Double

    var body: some View {
        HStack(spacing: 12) {
            Button {
                minTriggerArea = nudgedArea(minTriggerArea, by: -1)
            } label: {
                Image(systemName: "minus.circle")
            }
            .accessibilityLabel("Smaller")

            Slider(
                value: Binding(
                    get: { sliderPosition(area: minTriggerArea) },
                    set: { minTriggerArea = triggerArea(sliderPosition: $0) }
                ),
                in: 0...1
            )
            .accessibilityValue("\(detectorPixelCount(minTriggerArea)) detector pixels")

            Button {
                minTriggerArea = nudgedArea(minTriggerArea, by: 1)
            } label: {
                Image(systemName: "plus.circle")
            }
            .accessibilityLabel("Larger")
        }
        // Without this the whole row reads as one button inside a Form and either end nudges
        // whichever the platform decides is the row's action.
        .buttonStyle(.borderless)
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
                    // At the small end of the range the circle is a couple of points across and
                    // effectively invisible against a busy frame. The dashed ring says "the size
                    // you set is in here" without pretending to be the size itself.
                    if radius < locatorRadius {
                        context.stroke(
                            Path(ellipseIn: CGRect(
                                x: center.x - locatorRadius, y: center.y - locatorRadius,
                                width: locatorRadius * 2, height: locatorRadius * 2
                            )),
                            with: .color(SentinelTheme.cyan.opacity(0.5)),
                            style: StrokeStyle(lineWidth: 1, dash: [3, 3])
                        )
                    }
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
                        // No floor on the radius any more, only the domain floor triggerArea
                        // applies: a 10-point minimum here is what made small sizes unreachable,
                        // and it did it differently on every screen size.
                        let newRadius = min(max(sqrt(dx * dx + dy * dy), 0), maxRadius)
                        let area = (Double.pi * newRadius * newRadius) / (imageRect.width * imageRect.height)
                        minTriggerArea = triggerArea(detectorPixels: area * detectorPixels)
                    }
            )
        }
    }

    private func radius(for area: Double, in rect: CGRect) -> CGFloat {
        CGFloat(sqrt(area * rect.width * rect.height / .pi))
    }
}

// Radius of the dashed "the circle is in here" ring drawn when the real circle is too small to
// find by eye.
private let locatorRadius: CGFloat = 9
