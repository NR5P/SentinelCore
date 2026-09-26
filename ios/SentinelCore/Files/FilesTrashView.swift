import SwiftUI

// The trash: everything deleted from the library or removed as a duplicate, in the batches the
// action made them in, so one mistaken delete of a folder goes back in a single tap. Anyone can
// put things back; only an admin can empty it. The nightly pass clears batches older than the
// trashKeepDays setting on its own.
struct FilesTrashView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var session: FilesSession

    @State private var batches: [FamilyTrashBatch] = []
    @State private var loaded = false
    @State private var error: String?
    @State private var note: String?
    @State private var busy = false
    @State private var confirmingEmpty = false

    var body: some View {
        List {
            if let error {
                Text(error).foregroundStyle(SentinelTheme.red)
            }
            if let note {
                Text(note).foregroundStyle(SentinelTheme.green)
            }
            if batches.isEmpty && loaded {
                ContentUnavailableView {
                    Label("The Trash Is Empty", systemImage: "trash")
                } description: {
                    Text("Files you delete, and duplicate copies the nightly check removes, wait here until the trash is emptied.")
                }
            }
            ForEach(batches) { batch in
                Section {
                    ForEach(batch.files) { file in
                        HStack(spacing: 10) {
                            Image(systemName: "doc").foregroundStyle(.secondary)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(file.name).lineLimit(1)
                                Text(file.path).font(.caption2).foregroundStyle(.tertiary).lineLimit(1)
                                if let kept = file.keptCopy {
                                    Text("Same as \(kept)").font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                                }
                            }
                            Spacer()
                            Text(FamilyFilesFormat.bytes(file.size)).font(.caption).foregroundStyle(.secondary)
                        }
                        .swipeActions(edge: .leading) {
                            Button { Task { await restore(batch, file: file) } } label: {
                                Label("Put Back", systemImage: "arrow.uturn.backward")
                            }
                            .tint(SentinelTheme.green)
                        }
                        .contextMenu {
                            Button { Task { await restore(batch, file: file) } } label: {
                                Label("Put Back This File", systemImage: "arrow.uturn.backward")
                            }
                        }
                    }
                    Button {
                        Task { await restore(batch, file: nil) }
                    } label: {
                        Label(batch.files.count == 1 ? "Put Back" : "Put Back All \(batch.files.count)", systemImage: "arrow.uturn.backward")
                    }
                    .disabled(busy)
                } header: {
                    HStack {
                        Text(batch.reasonLabel)
                        Spacer()
                        Text(FamilyFilesFormat.date(batch.deletedUtc))
                    }
                } footer: {
                    if let by = batch.by, !by.isEmpty {
                        Text("By \(by)")
                    }
                }
            }
        }
        .navigationTitle("Trash")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            if session.isAdmin && !batches.isEmpty {
                ToolbarItem(placement: .primaryAction) {
                    Button(role: .destructive) {
                        confirmingEmpty = true
                    } label: {
                        Label("Empty Trash", systemImage: "trash.slash")
                    }
                    .disabled(busy)
                }
            }
        }
        .safeAreaInset(edge: .top, spacing: 0) { FilesBanners() }
        .task(id: session.version) { await load() }
        .refreshable { await load() }
        .confirmationDialog("Empty the trash?", isPresented: $confirmingEmpty, titleVisibility: .visible) {
            Button("Empty Trash", role: .destructive) { Task { await empty() } }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Everything in it is deleted for good. This can't be undone, and the next backup removes these files from the drives too.")
        }
    }

    private func load() async {
        do {
            batches = try await model.client.familyTrash()
            error = nil
        } catch {
            if error is CancellationError || (error as? URLError)?.code == .cancelled { return }
            self.error = error.localizedDescription
        }
        loaded = true
    }

    private func restore(_ batch: FamilyTrashBatch, file: FamilyTrashFile?) async {
        busy = true
        defer { busy = false }
        do {
            let result = try await model.client.familyRestore(batchId: batch.id, path: file?.path)
            note = result.detail ?? "Put back \(result.restored) file(s)."
            error = nil
            session.changed()
        } catch {
            self.error = error.localizedDescription
        }
    }

    private func empty() async {
        busy = true
        defer { busy = false }
        do {
            let result = try await model.client.familyEmptyTrash()
            note = result.detail
            error = nil
            session.changed()
        } catch {
            self.error = error.localizedDescription
        }
    }
}
