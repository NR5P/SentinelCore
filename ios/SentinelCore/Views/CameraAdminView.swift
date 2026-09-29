import SwiftUI

struct CameraAdminView: View {
    @EnvironmentObject private var model: AppModel
    @State private var isLoading = false
    @State private var errorMessage: String?
    @State private var pendingDelete: Camera?

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
                    HStack {
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
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    #if os(macOS)
                    // Swipe needs a trackpad on the Mac, so give every row a visible delete.
                    Button {
                        pendingDelete = camera
                    } label: {
                        Image(systemName: "trash")
                    }
                    .buttonStyle(.borderless)
                    .foregroundStyle(SentinelTheme.red)
                    .help("Delete \(camera.name)")
                    #endif
                    }
                    .swipeActions(edge: .trailing) {
                        Button(role: .destructive) {
                            Task { await delete(camera) }
                        } label: {
                            Label("Delete", systemImage: "trash")
                        }
                    }
                    .contextMenu {
                        Button(role: .destructive) {
                            pendingDelete = camera
                        } label: {
                            Label("Delete Camera…", systemImage: "trash")
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
        .confirmationDialog(
            "Delete \(pendingDelete?.name ?? "camera")?",
            isPresented: Binding(get: { pendingDelete != nil }, set: { if !$0 { pendingDelete = nil } }),
            titleVisibility: .visible
        ) {
            Button("Delete", role: .destructive) {
                if let camera = pendingDelete {
                    Task { await delete(camera) }
                }
                pendingDelete = nil
            }
        } message: {
            Text("This removes the camera from SentinelCore on every device.")
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
