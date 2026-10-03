import SwiftUI
#if os(iOS)
import UIKit
#endif

// "Use this device as a camera": the phone or Mac this app is on becomes one of the dashboard's
// cameras, in a few taps - pick (or create) the camera it should be, check the preview, start.
// The web dashboard's publish page does the same from a browser; this is the native version,
// with the things a browser can't do: the app keeps the device awake itself, resumes after a
// relaunch, and on a phone can dim the screen instead of lighting the room all night.
//
// The streaming itself lives in DevicePublisher and outlives this screen. Closing this is not
// stopping; the stop button is.
struct DeviceCameraView: View {
    @EnvironmentObject private var model: AppModel
    @ObservedObject private var publisher = DevicePublisher.shared

    @State private var targets: [Camera] = []
    @State private var selectedTargetId: UUID?
    @State private var newName = ""
    @State private var selectedDeviceId = ""
    @State private var includeMic = false
    @State private var isBusy = false
    @State private var loadError: String?
    #if os(iOS)
    @State private var isDimmed = false
    @State private var brightnessBeforeDim: CGFloat = 1
    #endif

    // The selection as far as the server is concerned: a remembered or resumed camera that this
    // server doesn't have (the app was pointed at another server, the camera was deleted) reads
    // as "New camera…" rather than as a blank picker with a hidden name field.
    private var effectiveTargetId: UUID? {
        guard let selectedTargetId, targets.contains(where: { $0.id == selectedTargetId }) else { return nil }
        return selectedTargetId
    }

    var body: some View {
        Form {
            previewSection
            if !model.isAdmin {
                Section {
                    Text("Only an Admin account can add a device as a camera. Sign in as an Admin on this device to use it here.")
                        .foregroundStyle(.secondary)
                }
            } else {
                targetSection
                deviceSection
                controlSection
                keepAwakeSection
            }
        }
        .formStyle(.grouped)
        .navigationTitle("This Device as a Camera")
        #if os(iOS)
        // Inline, not large: the large title truncates this to "This Device as a C…" on an iPhone.
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .task { await load() }
        .onDisappear { publisher.endPreview() }
        .onChange(of: publisher.target) { _, target in
            // Streaming that started elsewhere (auto-resume after a relaunch) should read here as
            // what it is, with the right camera selected.
            if let target { selectedTargetId = target.id }
        }
        #if os(iOS)
        .overlay { if isDimmed { dimOverlay } }
        .onChange(of: publisher.target) { _, target in if target == nil { undim() } }
        #endif
    }

    // ---------------- Sections ----------------

    private var previewSection: some View {
        Section {
            ZStack {
                PublisherPreviewView(track: publisher.previewTrack)
                    .aspectRatio(4 / 3, contentMode: .fit)
                    .clipShape(RoundedRectangle(cornerRadius: 10))
                if publisher.previewTrack == nil {
                    Text(publisher.phase == .paused ? "paused" : "no camera yet")
                        .font(.system(.caption, design: .monospaced))
                        .foregroundStyle(.secondary)
                }
            }
            .frame(maxWidth: .infinity)
            .listRowBackground(Color.clear)
            .listRowInsets(EdgeInsets())
        }
    }

    private var targetSection: some View {
        Section {
            // A Menu, deliberately not a Picker. A Picker here crashed three launches in a row on
            // macOS 26 (EXC_BAD_ACCESS in the Swift executor check, from SwiftUI's
            // PickerCurrentValueLabel evaluating the item list); a Menu draws its own label and
            // never takes that path.
            Menu {
                ForEach(targets) { camera in
                    Button {
                        selectedTargetId = camera.id
                    } label: {
                        if effectiveTargetId == camera.id { Label(camera.name, systemImage: "checkmark") } else { Text(camera.name) }
                    }
                }
                Button {
                    selectedTargetId = nil
                } label: {
                    if effectiveTargetId == nil { Label("New camera…", systemImage: "checkmark") } else { Text("New camera…") }
                }
            } label: {
                HStack {
                    Text("Streams to")
                    Spacer()
                    Text(targets.first(where: { $0.id == effectiveTargetId })?.name ?? "New camera…")
                        .foregroundStyle(.secondary)
                    Image(systemName: "chevron.up.chevron.down").font(.caption).foregroundStyle(.secondary)
                }
            }
            .disabled(publisher.isPublishing)
            if effectiveTargetId == nil {
                TextField("Name on the dashboard", text: $newName)
                    .disabled(publisher.isPublishing)
            }
        } header: {
            Text("Dashboard camera")
        } footer: {
            Text(targets.isEmpty
                 ? "A new camera is created for this device the moment you start. It appears on the Live tab and records, fires trigger zones and gets analyzed like any other."
                 : "Pick the dashboard camera this device should be, or make a new one. Each device streams to its own camera.")
        }
    }

    private var deviceSection: some View {
        Section("This device") {
            if publisher.devices.isEmpty {
                Text("No camera found on this device.").foregroundStyle(.secondary)
            } else {
                // A Menu, not a Picker, for the same reason as the target list above: a Picker
                // over this list crashed on macOS 26 inside SwiftUI's PickerCurrentValueLabel.
                Menu {
                    ForEach(publisher.devices) { device in
                        Button {
                            selectedDeviceId = device.id
                        } label: {
                            if selectedDeviceId == device.id { Label(device.name, systemImage: "checkmark") } else { Text(device.name) }
                        }
                    }
                } label: {
                    HStack {
                        Text("Camera")
                        Spacer()
                        Text(publisher.devices.first(where: { $0.id == selectedDeviceId })?.name ?? "Choose…")
                            .foregroundStyle(.secondary)
                        Image(systemName: "chevron.up.chevron.down").font(.caption).foregroundStyle(.secondary)
                    }
                }
                .onChange(of: selectedDeviceId) { _, id in
                    guard !id.isEmpty, id != publisher.deviceId else { return }
                    Task {
                        if publisher.isPublishing { await restart() } else { await publisher.preview(deviceId: id) }
                    }
                }
            }
            Toggle("Include this device's microphone", isOn: $includeMic)
                .onChange(of: includeMic) { _, _ in
                    if publisher.isPublishing { Task { await restart() } }
                }
        }
    }

    private var controlSection: some View {
        Section {
            Button {
                Task { await toggle() }
            } label: {
                HStack {
                    Spacer()
                    Label(publisher.isPublishing ? "Stop Streaming" : "Start Streaming",
                          systemImage: publisher.isPublishing ? "stop.circle.fill" : "record.circle")
                        .font(.headline)
                    Spacer()
                }
            }
            .buttonStyle(.sentinelProminent)
            .tint(publisher.isPublishing ? SentinelTheme.red : SentinelTheme.green)
            // No camera means nothing to start - and starting would still create the dashboard
            // camera first, leaving an empty one behind. The iOS Simulator is the usual way to hit this.
            .disabled(isBusy || (!publisher.isPublishing && (publisher.devices.isEmpty
                || (effectiveTargetId == nil && newName.trimmingCharacters(in: .whitespaces).isEmpty))))
            .listRowBackground(Color.clear)
            .listRowInsets(EdgeInsets())

            if !publisher.status.isEmpty {
                Label(publisher.status, systemImage: statusIcon)
                    .font(.system(.footnote, design: .monospaced))
                    .foregroundStyle(statusColor)
            }
            if let error = publisher.errorMessage ?? loadError {
                Text(error).font(.footnote).foregroundStyle(SentinelTheme.red)
            }
        }
    }

    private var keepAwakeSection: some View {
        Section {
            ForEach(KeepAwake.handled, id: \.self) { line in
                Label { Text(line) } icon: { Image(systemName: "checkmark.circle").foregroundStyle(SentinelTheme.green) }
                    .font(.footnote)
            }
            ForEach(KeepAwake.todo, id: \.self) { line in
                Label { Text(line) } icon: { Image(systemName: "hand.point.right").foregroundStyle(SentinelTheme.amber) }
                    .font(.footnote)
            }
            #if os(iOS)
            if publisher.isPublishing {
                Button { dim() } label: { Label("Dim screen", systemImage: "moon.fill") }
            }
            #endif
        } header: {
            Text("Keeping it going")
        } footer: {
            Text("The connection recovers on its own after a network blip, a sleep, or a server restart. If the server stops receiving video for two minutes it sends you a message (email, Telegram and push - whichever are set up) and another when it is back.")
        }
    }

    #if os(iOS)
    private var dimOverlay: some View {
        Color.black
            .ignoresSafeArea()
            .overlay(alignment: .bottom) {
                Text("streaming · tap to wake the screen")
                    .font(.system(.caption2, design: .monospaced))
                    .foregroundStyle(.white.opacity(0.35))
                    .padding(.bottom, 24)
            }
            .contentShape(Rectangle())
            .onTapGesture { undim() }
    }

    private var screen: UIScreen? {
        UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first?.screen
    }

    private func dim() {
        guard let screen else { return }
        brightnessBeforeDim = screen.brightness
        screen.brightness = 0.02
        isDimmed = true
    }

    private func undim() {
        guard isDimmed else { return }
        screen?.brightness = brightnessBeforeDim
        isDimmed = false
    }
    #endif

    private var statusIcon: String {
        switch publisher.phase {
        case .streaming: "dot.radiowaves.left.and.right"
        case .connected, .connecting, .starting: "ellipsis.circle"
        case .reconnecting, .paused: "exclamationmark.arrow.circlepath"
        case .previewing, .idle: "eye"
        }
    }

    private var statusColor: Color {
        switch publisher.phase {
        case .streaming: SentinelTheme.green
        case .reconnecting, .paused: SentinelTheme.amber
        default: .secondary
        }
    }

    // ---------------- Actions ----------------

    private func load() async {
        loadError = nil
        do {
            let cameras = try await model.client.cameras()
            model.cameras = cameras
            targets = cameras.filter(\.isDevicePublished)
        } catch {
            loadError = error.localizedDescription
        }
        if let target = publisher.target {
            selectedTargetId = target.id
            includeMic = publisher.includeMic
        } else if selectedTargetId == nil {
            // The camera this device streamed to last time, if it still exists; otherwise "New
            // camera…". Never "the only one there is": on a first run that one is usually another
            // device's (a laptop on the browser page), and Start must not take it over.
            if let last = publisher.lastTargetId, targets.contains(where: { $0.id == last }) {
                selectedTargetId = last
            }
        }
        if newName.isEmpty { newName = Self.deviceName }
        publisher.refreshDevices()
        if publisher.isPublishing {
            selectedDeviceId = publisher.deviceId
        } else {
            selectedDeviceId = publisher.defaultDeviceId()
            await publisher.preview(deviceId: selectedDeviceId)
            selectedDeviceId = publisher.deviceId
        }
    }

    private func toggle() async {
        if publisher.isPublishing {
            publisher.stop()
            return
        }
        isBusy = true
        defer { isBusy = false }
        loadError = nil
        let target: DevicePublisher.Target
        if let id = effectiveTargetId, let existing = targets.first(where: { $0.id == id }) {
            target = .init(id: existing.id, name: existing.name)
        } else {
            do {
                let created = try await model.client.createCamera(CameraInput(
                    name: newName.trimmingCharacters(in: .whitespaces),
                    streamUrl: "",
                    username: nil,
                    password: nil,
                    includeAudio: includeMic,
                    ptzEnabled: false,
                    onvifPtzServiceUrl: nil,
                    onvifProfileToken: nil,
                    isBrowserPublished: true))
                model.cameras.append(created)
                targets.append(created)
                selectedTargetId = created.id
                target = .init(id: created.id, name: created.name)
            } catch {
                loadError = error.localizedDescription
                return
            }
        }
        await syncIncludeAudio(for: target)
        await publisher.start(client: model.client, target: target, deviceId: selectedDeviceId, includeMic: includeMic)
        selectedDeviceId = publisher.deviceId
    }

    // Changing camera or microphone mid-stream restarts it - a second of video, not a lost session.
    private func restart() async {
        guard let target = publisher.target else { return }
        await syncIncludeAudio(for: target)
        await publisher.start(client: model.client, target: target, deviceId: selectedDeviceId, includeMic: includeMic)
        selectedDeviceId = publisher.deviceId
    }

    // The camera's own "include audio" setting is what makes viewers ask for the audio track, so
    // it has to follow the microphone switch here - otherwise a mic turned on later streams sound
    // that nothing plays. Best effort: a failure to save it must not stop the stream.
    private func syncIncludeAudio(for target: DevicePublisher.Target) async {
        guard let index = targets.firstIndex(where: { $0.id == target.id }),
              (targets[index].includeAudio ?? false) != includeMic else { return }
        let camera = targets[index]
        let input = CameraInput(
            name: camera.name, streamUrl: "", username: camera.username, password: camera.password,
            includeAudio: includeMic, ptzEnabled: false,
            onvifPtzServiceUrl: camera.onvifPtzServiceUrl, onvifProfileToken: camera.onvifProfileToken,
            isBrowserPublished: true)
        do {
            try await model.client.updateCamera(id: camera.id, input)
            let updated = Camera(
                id: camera.id, name: camera.name, streamUrl: camera.streamUrl, includeAudio: includeMic,
                ptzEnabled: camera.ptzEnabled, onvifPtzServiceUrl: camera.onvifPtzServiceUrl,
                onvifProfileToken: camera.onvifProfileToken, username: camera.username, password: camera.password,
                isBrowserPublished: camera.isBrowserPublished)
            targets[index] = updated
            if let modelIndex = model.cameras.firstIndex(where: { $0.id == camera.id }) { model.cameras[modelIndex] = updated }
        } catch {
            loadError = "The microphone streams, but the camera's audio setting couldn't be saved: \(error.localizedDescription)"
        }
    }

    private static var deviceName: String {
        #if os(iOS)
        UIDevice.current.name
        #else
        Host.current().localizedName ?? "Mac"
        #endif
    }
}

// The screen as the app presents it: from the Live toolbar and from Settings, and on its own
// when streaming resumed after a relaunch - so the person sees that it did.
struct DeviceCameraSheet: View {
    // The macOS Window scene this is shown in - see SentinelCoreApp and DeviceCameraPresenter.
    static let windowId = "device-camera"

    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            DeviceCameraView()
                .toolbar {
                    ToolbarItem(placement: .confirmationAction) {
                        Button("Done") { dismiss() }
                    }
                }
        }
        #if os(macOS)
        .frame(minWidth: 560, minHeight: 720)
        #endif
    }
}
