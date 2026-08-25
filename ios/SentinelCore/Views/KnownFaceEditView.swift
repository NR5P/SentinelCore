import SwiftUI
import PhotosUI

struct KnownFaceEditView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss
    let face: KnownFace?

    @State private var label = ""
    @State private var enrollments: [KnownFaceEnrollment] = []
    @State private var pendingImageData: Data?
    @State private var photosPickerItem: PhotosPickerItem?
    @State private var showPhotosPicker = false
    #if os(iOS)
    @State private var showCamera = false
    #endif
    @State private var showSourceDialog = false
    @State private var isSaving = false
    @State private var errorMessage: String?

    private var isNew: Bool { face == nil }

    var body: some View {
        Form {
            Section("Name") {
                // Renaming an enrolled person isn't supported by the server yet - the field is
                // read-only once created rather than silently doing nothing on save.
                SentinelTextField(label: "Name", prompt: "e.g. Nathan", text: $label)
                    .disabled(!isNew)
            }

            if isNew {
                Section("Photo") {
                    photoPickerArea
                    Text("Use a clear, front-facing photo of just this person.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            } else {
                Section("Photos") {
                    photoGrid
                    photoPickerButton(label: "Add Another Photo")
                    Text("More photos, from different angles or lighting, improve matching.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }

            if let errorMessage {
                Section {
                    Text(errorMessage).foregroundStyle(SentinelTheme.red)
                }
            }
        }
        .formStyle(.grouped)
        .navigationTitle(isNew ? "Add Person" : (face?.label ?? "Known Face"))
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            if isNew {
                ToolbarItem(placement: .confirmationAction) {
                    Button(isSaving ? "Saving…" : "Save") {
                        Task { await createFace() }
                    }
                    .disabled(isSaving || label.trimmingCharacters(in: .whitespaces).isEmpty || pendingImageData == nil)
                }
            }
        }
        .onAppear {
            guard let face else { return }
            label = face.label
            enrollments = face.enrollments
        }
        .confirmationDialog("Add a Photo", isPresented: $showSourceDialog) {
            #if os(iOS)
            Button("Take Photo") { showCamera = true }
            #endif
            Button("Choose from Library") { showPhotosPicker = true }
            Button("Cancel", role: .cancel) {}
        }
        .photosPicker(isPresented: $showPhotosPicker, selection: $photosPickerItem, matching: .images)
        .onChange(of: photosPickerItem) { _, newValue in
            guard let newValue else { return }
            Task {
                if let data = try? await newValue.loadTransferable(type: Data.self) {
                    await handlePickedPhoto(data)
                }
                photosPickerItem = nil
            }
        }
        #if os(iOS)
        .fullScreenCover(isPresented: $showCamera) {
            CameraCaptureView { data in
                showCamera = false
                if let data { Task { await handlePickedPhoto(data) } }
            }
            .ignoresSafeArea()
        }
        #endif
    }

    @ViewBuilder
    private var photoPickerArea: some View {
        if let pendingImageData, let preview = image(from: pendingImageData) {
            preview
                .resizable()
                .aspectRatio(contentMode: .fit)
                .frame(maxHeight: 200)
                .clipShape(RoundedRectangle(cornerRadius: 8))
        }
        photoPickerButton(label: pendingImageData == nil ? "Choose Photo" : "Choose a Different Photo")
    }

    private func photoPickerButton(label text: String) -> some View {
        Button(text) { showSourceDialog = true }
    }

    @ViewBuilder
    private var photoGrid: some View {
        let columns = [GridItem(.adaptive(minimum: 80))]
        LazyVGrid(columns: columns, spacing: 8) {
            ForEach(enrollments) { enrollment in
                ZStack(alignment: .topTrailing) {
                    if let url = model.client.url(path: enrollment.photoUrl) {
                        AsyncImage(url: url) { remoteImage in
                            remoteImage.resizable().aspectRatio(contentMode: .fill)
                        } placeholder: {
                            Color.secondary.opacity(0.2)
                        }
                        .frame(width: 80, height: 80)
                        .clipShape(RoundedRectangle(cornerRadius: 6))
                    }
                    Button {
                        Task { await deleteEnrollment(enrollment) }
                    } label: {
                        Image(systemName: "xmark.circle.fill")
                            .foregroundStyle(.white, SentinelTheme.red)
                    }
                    .offset(x: 6, y: -6)
                }
            }
        }
    }

    // Reuses the PlatformImage typealias TriggerZoneEditorView.swift already declares
    // (UIImage/NSImage) rather than redeclaring it - its Image(platformImage:) init is
    // file-private there, so this is a small local equivalent instead of extending Image again.
    private func image(from data: Data) -> Image? {
        guard let platformImage = PlatformImage(data: data) else { return nil }
        #if os(iOS)
        return Image(uiImage: platformImage)
        #elseif os(macOS)
        return Image(nsImage: platformImage)
        #endif
    }

    private func handlePickedPhoto(_ data: Data) async {
        if isNew {
            pendingImageData = data
            return
        }
        guard let face else { return }
        errorMessage = nil
        isSaving = true
        defer { isSaving = false }
        do {
            let updated = try await model.client.addKnownFaceEnrollment(faceId: face.id, imageData: data)
            enrollments = updated.enrollments
            if let index = model.knownFaces.firstIndex(where: { $0.id == face.id }) {
                model.knownFaces[index] = updated
            }
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func createFace() async {
        guard let pendingImageData else { return }
        isSaving = true
        defer { isSaving = false }
        errorMessage = nil
        do {
            let created = try await model.client.createKnownFace(
                label: label.trimmingCharacters(in: .whitespaces), imageData: pendingImageData
            )
            model.knownFaces.append(created)
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func deleteEnrollment(_ enrollment: KnownFaceEnrollment) async {
        guard let face else { return }
        let previous = enrollments
        enrollments.removeAll { $0.id == enrollment.id }
        do {
            try await model.client.deleteKnownFaceEnrollment(faceId: face.id, enrollmentId: enrollment.id)
            if let index = model.knownFaces.firstIndex(where: { $0.id == face.id }) {
                model.knownFaces[index] = KnownFace(id: face.id, label: face.label, enrollments: enrollments)
            }
        } catch {
            enrollments = previous
            errorMessage = error.localizedDescription
        }
    }
}
