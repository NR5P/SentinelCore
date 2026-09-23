import SwiftUI

struct CameraEditView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss
    let camera: Camera?
    let prefillStreamUrl: String?

    @State private var name = ""
    @State private var streamUrl = ""
    @State private var username = ""
    @State private var password = ""
    @State private var includeAudio = false
    @State private var ptzEnabled = false
    // Not shown directly - just carries whatever Test Connection last detected (or this camera's
    // existing values, if editing) through to save(), the same way streamUrl/name get silently
    // filled in from a probe result rather than the user typing them.
    @State private var onvifPtzServiceUrl: String?
    @State private var onvifProfileToken: String?

    // The password box holds SettingsSecrets.Mask until someone presses Show, at which point the
    // real one is fetched. Until then it is not editable: leaving the mask in place is what tells
    // the server to keep the stored password, and letting someone type onto the end of it would
    // save "••••••••newpass" as the credential.
    @State private var passwordRevealed = false
    @State private var isRevealing = false

    @State private var isTesting = false
    @State private var isSaving = false
    @State private var probeResult: CameraProbeResult?
    @State private var errorMessage: String?

    // `prefillStreamUrl` is used by the discovery flow: tapping a scan result opens this view
    // pre-filled with just the bare IP (per the web behavior) - the user still runs Test
    // Connection here to resolve the real, credentialed stream URL before saving.
    init(camera: Camera?, prefillStreamUrl: String? = nil) {
        self.camera = camera
        self.prefillStreamUrl = prefillStreamUrl
    }

    private var isNew: Bool { camera == nil }
    private var isDevicePublished: Bool { camera?.isDevicePublished ?? false }

    var body: some View {
        Form {
            Section("Camera") {
                SentinelTextField(label: "Name", prompt: "e.g. Driveway", text: $name)
                if isDevicePublished {
                    // No address to edit: the device streams to the server, not the other way.
                    Text("Streams from a device (a phone, a Mac, or a browser's publish page). The device picks this camera when it starts streaming.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                } else {
                SentinelTextField(
                    label: "Stream URL or address",
                    prompt: "rtsp://192.168.1.64:554/h264",
                    text: $streamUrl)
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    .keyboardType(.URL)
                    #endif
                }
                SentinelTextField(label: "Username (optional)", text: $username)
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    #endif
                if isNew || passwordRevealed {
                    SentinelTextField(
                        label: "Password (optional)", text: $password, secure: !passwordRevealed)
                } else {
                    SentinelTextField(label: "Password (optional)", text: .constant(password), secure: true)
                        .disabled(true)
                    Button {
                        Task { await revealPassword() }
                    } label: {
                        if isRevealing { ProgressView() } else { Text("Show Password") }
                    }
                    .disabled(isRevealing || password.isEmpty)
                    Text(password.isEmpty
                        ? "This camera has no password saved."
                        : "The saved password is kept on the server and isn't sent with the camera list. Show it to check or change it.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Toggle("Include Audio", isOn: $includeAudio)
                Toggle("Pan/Tilt/Zoom (PTZ) Joystick", isOn: $ptzEnabled)
                Text("Shows on-screen directional controls in Live view for cameras that can physically move. Turned on automatically when Test Connection detects an ONVIF PTZ service - you can also turn it on by hand, but the controls won't do anything without one.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section {
                Button {
                    Task { await testConnection() }
                } label: {
                    if isTesting {
                        ProgressView()
                    } else {
                        Text("Test Connection")
                    }
                }
                .disabled(isTesting || streamUrl.trimmingCharacters(in: .whitespaces).isEmpty)

                if let probeResult {
                    switch probeResult.status {
                    case "Found":
                        Label(
                            probeResult.message ?? "Camera found.",
                            systemImage: "checkmark.circle.fill"
                        )
                        .foregroundStyle(SentinelTheme.green)
                    case "NeedsCredentials":
                        Label(
                            "This camera needs credentials. Enter them above and test again.",
                            systemImage: "lock.fill"
                        )
                        .foregroundStyle(SentinelTheme.amber)
                    default:
                        Label(
                            probeResult.message ?? "Camera not found.",
                            systemImage: "xmark.circle.fill"
                        )
                        .foregroundStyle(SentinelTheme.red)
                    }
                }
            }

            if let errorMessage {
                Section {
                    Text(errorMessage).foregroundStyle(SentinelTheme.red)
                }
            }

            if let camera {
                Section {
                    NavigationLink("Configure Trigger Zone") {
                        TriggerZoneEditorView(camera: camera)
                    }
                }
            }
        }
        .formStyle(.grouped)
        .navigationTitle(isNew ? "Add Camera" : "Edit Camera")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItem(placement: .confirmationAction) {
                Button(isSaving ? "Saving…" : "Save") {
                    Task { await save() }
                }
                .disabled(isSaving || name.trimmingCharacters(in: .whitespaces).isEmpty
                    || (streamUrl.trimmingCharacters(in: .whitespaces).isEmpty && !isDevicePublished))
            }
        }
        .onAppear {
            guard let camera else {
                if let prefillStreamUrl { streamUrl = prefillStreamUrl }
                return
            }
            name = camera.name
            streamUrl = camera.streamUrl ?? ""
            // Both of these used to be left blank, so every save from this screen sent no
            // credentials and the PUT - which replaces the record rather than merging it - wiped
            // whatever the camera had. `password` is the mask, not the real value.
            username = camera.username ?? ""
            password = camera.password ?? ""
            includeAudio = camera.includeAudio ?? false
            ptzEnabled = camera.ptzEnabled ?? false
            onvifPtzServiceUrl = camera.onvifPtzServiceUrl
            onvifProfileToken = camera.onvifProfileToken
        }
    }

    private func revealPassword() async {
        guard let camera else { return }
        isRevealing = true
        defer { isRevealing = false }
        do {
            password = try await model.client.cameraPassword(id: camera.id)
            passwordRevealed = true
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    // Test Connection dials the camera for real, so the mask would simply fail its login - fetch
    // the stored password for that one call rather than making the user reveal it first.
    private func passwordForProbe() async -> String? {
        if password != SentinelSecrets.mask { return password.isEmpty ? nil : password }
        guard let camera, let real = try? await model.client.cameraPassword(id: camera.id) else { return nil }
        return real.isEmpty ? nil : real
    }

    private func testConnection() async {
        isTesting = true
        defer { isTesting = false }
        errorMessage = nil
        do {
            let result = try await model.client.probeCamera(
                CameraProbeRequest(
                    address: streamUrl.trimmingCharacters(in: .whitespaces),
                    username: username.isEmpty ? nil : username,
                    password: await passwordForProbe()
                )
            )
            probeResult = result
            if result.status == "Found" {
                // Only overwrite what the user hasn't already deliberately filled in.
                if !streamUrl.contains("://"), let resolved = result.streamUrl {
                    streamUrl = resolved
                }
                if name.trimmingCharacters(in: .whitespaces).isEmpty, let suggested = result.suggestedName {
                    name = suggested
                }
                // Always carry through the latest detection, but only ever turn the toggle ON
                // here, never off - a user who manually enabled PTZ for a camera this particular
                // test didn't detect it on shouldn't have that silently undone by re-testing.
                onvifPtzServiceUrl = result.onvifPtzServiceUrl
                onvifProfileToken = result.onvifProfileToken
                if result.supportsPtz == true { ptzEnabled = true }
            }
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func save() async {
        isSaving = true
        defer { isSaving = false }
        errorMessage = nil
        let input = CameraInput(
            name: name.trimmingCharacters(in: .whitespaces),
            streamUrl: streamUrl.trimmingCharacters(in: .whitespaces),
            username: username.isEmpty ? nil : username,
            password: password.isEmpty ? nil : password,
            includeAudio: includeAudio,
            ptzEnabled: ptzEnabled,
            onvifPtzServiceUrl: onvifPtzServiceUrl,
            onvifProfileToken: onvifProfileToken,
            isBrowserPublished: camera?.isDevicePublished ?? false
        )
        do {
            if let camera {
                try await model.client.updateCamera(id: camera.id, input)
                if let index = model.cameras.firstIndex(where: { $0.id == camera.id }) {
                    model.cameras[index] = Camera(
                        id: camera.id,
                        name: input.name,
                        streamUrl: input.streamUrl,
                        includeAudio: input.includeAudio,
                        ptzEnabled: input.ptzEnabled,
                        onvifPtzServiceUrl: input.onvifPtzServiceUrl,
                        onvifProfileToken: input.onvifProfileToken,
                        username: input.username,
                        // Back to the mask, never the revealed value: this list is what the next
                        // edit loads from, and it should hold no more than the server just sent.
                        password: (input.password?.isEmpty ?? true) ? nil : SentinelSecrets.mask,
                        isBrowserPublished: camera.isBrowserPublished
                    )
                }
            } else {
                let created = try await model.client.createCamera(input)
                model.cameras.append(created)
            }
            SaveFeedback.hapticSuccess()
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}
