import SwiftUI

struct KnownFacesAdminView: View {
    @EnvironmentObject private var model: AppModel
    @State private var isLoading = false
    @State private var errorMessage: String?

    var body: some View {
        List {
            if let errorMessage {
                Text(errorMessage).foregroundStyle(SentinelTheme.red)
            }
            Section {
                ForEach(model.knownFaces) { face in
                    NavigationLink {
                        KnownFaceEditView(face: face)
                    } label: {
                        HStack(spacing: 12) {
                            thumbnail(for: face)
                            VStack(alignment: .leading, spacing: 3) {
                                Text(face.label).bold()
                                Text("\(face.enrollments.count) photo\(face.enrollments.count == 1 ? "" : "s")")
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                            }
                        }
                    }
                    .swipeActions(edge: .trailing) {
                        Button(role: .destructive) {
                            Task { await delete(face) }
                        } label: {
                            Label("Delete", systemImage: "trash")
                        }
                    }
                }
            }
        }
        .navigationTitle("Known Faces")
        .toolbar {
            ToolbarItem {
                NavigationLink {
                    KnownFaceEditView(face: nil)
                } label: {
                    Label("Add Person", systemImage: "plus")
                }
            }
        }
        .refreshable { await load() }
        .task { await load() }
        .overlay {
            if model.knownFaces.isEmpty && isLoading {
                ProgressView()
            } else if model.knownFaces.isEmpty {
                ContentUnavailableView(
                    "No known faces",
                    systemImage: "person.crop.circle.badge.questionmark",
                    description: Text("Enroll a family member or regular visitor from a clear photo, so alerts can note when it's them.")
                )
            }
        }
    }

    @ViewBuilder
    private func thumbnail(for face: KnownFace) -> some View {
        if let first = face.enrollments.first, let url = model.client.url(path: first.photoUrl) {
            AsyncImage(url: url) { image in
                image.resizable().aspectRatio(contentMode: .fill)
            } placeholder: {
                Color.secondary.opacity(0.2)
            }
            .frame(width: 40, height: 40)
            .clipShape(RoundedRectangle(cornerRadius: 6))
        } else {
            Image(systemName: "person.crop.circle")
                .font(.system(size: 32))
                .foregroundStyle(.secondary)
                .frame(width: 40, height: 40)
        }
    }

    private func load() async {
        isLoading = true
        defer { isLoading = false }
        do {
            model.knownFaces = try await model.client.knownFaces()
            errorMessage = nil
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func delete(_ face: KnownFace) async {
        let previous = model.knownFaces
        model.knownFaces.removeAll { $0.id == face.id }
        do {
            try await model.client.deleteKnownFace(id: face.id)
        } catch {
            model.knownFaces = previous
            errorMessage = error.localizedDescription
        }
    }
}
