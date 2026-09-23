import SwiftUI

struct CameraAdminView: View {
    @EnvironmentObject private var model: AppModel
    @State private var isLoading = false
    @State private var errorMessage: String?

    var body: some View {
        List {
            if let errorMessage {
                Text(errorMessage).foregroundStyle(SentinelTheme.red)
            }
            Section {
                NavigationLink {
                    CameraDiscoveryView()
                } label: {
                    Label("Discover Cameras", systemImage: "wifi")
                }
            }
            Section {
                ForEach(model.cameras) { camera in
                    NavigationLink {
                        CameraEditView(camera: camera)
                    } label: {
                        VStack(alignment: .leading, spacing: 3) {
                            Text(camera.name).bold()
                            if camera.isDevicePublished {
                                Text("streams from a device - a phone, a Mac, or a browser's publish page")
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                                    .lineLimit(1)
                            } else if let streamUrl = camera.streamUrl {
                                Text(streamUrl)
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                                    .lineLimit(1)
                            }
                        }
                    }
                    .swipeActions(edge: .trailing) {
                        Button(role: .destructive) {
                            Task { await delete(camera) }
                        } label: {
                            Label("Delete", systemImage: "trash")
                        }
                    }
                }
                .onDelete { offsets in
                    Task {
                        for index in offsets {
                            await delete(model.cameras[index])
                        }
                    }
                }
            }
        }
        .navigationTitle("Cameras")
        .toolbar {
            ToolbarItem {
                NavigationLink {
                    CameraEditView(camera: nil)
                } label: {
                    Label("Add Camera", systemImage: "plus")
                }
            }
        }
        .refreshable { await load() }
        .task { await load() }
        .overlay {
            if model.cameras.isEmpty && isLoading {
                ProgressView()
            } else if model.cameras.isEmpty {
                ContentUnavailableView(
                    "No cameras",
                    systemImage: "video.slash",
                    description: Text("Add one manually, or discover cameras on your network.")
                )
            }
        }
    }

    private func load() async {
        isLoading = true
        defer { isLoading = false }
        do {
            model.cameras = try await model.client.cameras()
            errorMessage = nil
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func delete(_ camera: Camera) async {
        let previous = model.cameras
        model.cameras.removeAll { $0.id == camera.id }
        do {
            try await model.client.deleteCamera(id: camera.id)
        } catch {
            model.cameras = previous
            errorMessage = error.localizedDescription
        }
    }
}
