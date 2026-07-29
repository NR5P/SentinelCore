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

    var body: some View {
        Form {
            Section("Camera") {
                TextField("Name", text: $name)
                TextField("Stream URL or address", text: $streamUrl)
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    .keyboardType(.URL)
                    #endif
                TextField("Username (optional)", text: $username)
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    #endif
                SecureField("Password (optional)", text: $password)
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
                    || streamUrl.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .onAppear {
            guard let camera else {
                if let prefillStreamUrl { streamUrl = prefillStreamUrl }
                return
            }
            name = camera.name
            streamUrl = camera.streamUrl ?? ""
            includeAudio = camera.includeAudio ?? false
            ptzEnabled = camera.ptzEnabled ?? false
            onvifPtzServiceUrl = camera.onvifPtzServiceUrl
            onvifProfileToken = camera.onvifProfileToken
        }
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
                    password: password.isEmpty ? nil : password
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
            onvifProfileToken: onvifProfileToken
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
                        onvifProfileToken: input.onvifProfileToken
                    )
                }
            } else {
                let created = try await model.client.createCamera(input)
                model.cameras.append(created)
            }
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}
