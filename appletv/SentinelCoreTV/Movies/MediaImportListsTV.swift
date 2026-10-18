import SwiftUI

// "Import a list" on the television: there are no files to pick on a TV, so lists are imported
// from the iPhone, Mac or dashboard, and the Music and Movies Downloads areas here follow them -
// how far each has got, with Pause / Resume and Search Missing Again. Contract:
// core/docs/MOVIE_DOWNLOADS.md "Import a list"; the iPhone's version is
// ios/SentinelCore/Movies/MediaImportViews.swift.

struct MediaImportListsTVSection: View {
    let kind: MediaImportKind
    @EnvironmentObject private var model: AppModel
    @State private var jobs: [MediaImportJob]?
    @State private var loadError: String?
    @State private var acting: MediaImportJob?
    @State private var working: Set<String> = []
    @State private var actionError: String?

    var body: some View {
        Section {
            // Always the first row, so the loop and dialogs hang off something that is always there.
            Text("Lists are imported from the iPhone, Mac or website. Their progress shows here.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .task(id: model.activeServerURL) {
                    while !Task.isCancelled {
                        await load()
                        let busy = (jobs ?? []).contains(where: \.isBusy)
                        try? await Task.sleep(for: .seconds(busy ? 5 : 30))
                    }
                }
                .confirmationDialog(
                    acting?.displayName ?? "Imported list",
                    isPresented: Binding(get: { acting != nil }, set: { if !$0 { acting = nil } }),
                    titleVisibility: .visible,
                    presenting: acting
                ) { job in
                    if job.canPause {
                        Button(job.isPaused ? "Resume" : "Pause") {
                            Task { await change(job) { try await $0.pauseMediaImport(kind: kind, id: $1, paused: !job.isPaused) } }
                        }
                    }
                    if job.canRetry {
                        Button("Search Missing Again") {
                            Task { await change(job) { try await $0.retryMediaImport(kind: kind, id: $1) } }
                        }
                    }
                    Button("Close", role: .cancel) {}
                } message: { job in
                    Text(([job.statusWord] + job.summaryBits).joined(separator: " · "))
                }
                .alert("Imported Lists", isPresented: Binding(get: { actionError != nil }, set: { if !$0 { actionError = nil } })) {
                    Button("OK", role: .cancel) {}
                } message: {
                    Text(actionError ?? "")
                }

            if let jobs {
                ForEach(jobs) { job in
                    Button {
                        acting = job
                    } label: {
                        MediaImportJobRowTV(kind: kind, job: job, working: working.contains(job.id ?? ""))
                    }
                    .disabled(working.contains(job.id ?? ""))
                }
            } else if let loadError {
                Text(loadError).foregroundStyle(SentinelTheme.red)
            }
        } header: {
            Text("Imported Lists")
        }
    }

    private func load() async {
        do {
            jobs = try await model.client.mediaImports(kind: kind)
            loadError = nil
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            loadError = error.localizedDescription
        }
    }

    private func change(_ job: MediaImportJob, _ action: (SentinelClient, String) async throws -> MediaImportJob) async {
        guard let id = job.id else { return }
        working.insert(id)
        defer { working.remove(id) }
        do {
            _ = try await action(model.client, id)
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            actionError = error.localizedDescription
        }
        await load()
    }
}

// One list: its name and size, how it is going, and the bar.
private struct MediaImportJobRowTV: View {
    let kind: MediaImportKind
    let job: MediaImportJob
    let working: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 12) {
                Text(job.displayName).font(.headline).lineLimit(1)
                MediaChipTV(text: kind.count(job.total))
                MediaChipTV(text: job.statusWord, highlighted: job.isBusy)
                if working { ProgressView() }
            }
            if !job.summaryBits.isEmpty {
                Text(job.summaryBits.joined(separator: " · "))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
            }
            ProgressView(value: job.settledFraction)
                .tint(SentinelTheme.green)
            if let message = job.shownMessage {
                Text(message)
                    .font(.caption)
                    .foregroundStyle(SentinelTheme.amber)
            }
        }
        .padding(.vertical, 8)
    }
}
