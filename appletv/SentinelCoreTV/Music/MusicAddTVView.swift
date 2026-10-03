import SwiftUI

// "+ Add" for albums (core/docs/MUSIC_AND_PODCASTS.md §4 Getting music), shown only when the
// server says canDownload. Same shape as the Movies Add flow on this TV: find the album on
// MusicBrainz, pick it, the server searches the sites in the background (the VPN may take a
// minute), pick a release, confirm, and it lands in the library. Every row is one big button.
struct MusicAddTVView: View {
    @EnvironmentObject private var audio: AudioCenter
    @State private var status: MusicDownloadsStatus?
    @State private var query = ""
    @State private var results: [MusicLookupResult]?
    @State private var looking = false
    @State private var errorMessage: String?
    @State private var picked: MusicLookupResult?
    @State private var search: MusicDownloadSearch?
    @State private var showOthers = false
    @State private var confirming: MusicRelease?
    @State private var downloads = MusicDownloadList()
    @State private var notice: String?
    @State private var acting: MediaDownload?

    private var trimmed: String { query.trimmingCharacters(in: .whitespacesAndNewlines) }

    var body: some View {
        List {
            if status?.musicLibrary == false {
                Section {
                    Label("Add a music library on this server's own disk first (Settings → Music Libraries).", systemImage: "exclamationmark.triangle.fill")
                        .foregroundStyle(SentinelTheme.amber)
                }
            }
            if let picked {
                releasesSection(picked)
            } else {
                Section {
                    TextField("Artist and album, e.g. Fleetwood Mac Rumours", text: $query)
                        .autocorrectionDisabled()
                        .onSubmit { Task { await lookup() } }
                    Button {
                        Task { await lookup() }
                    } label: {
                        Label("Find", systemImage: "magnifyingglass")
                    }
                    .disabled(trimmed.isEmpty || looking)
                } header: {
                    Text("Add an Album")
                } footer: {
                    Text("Searches MusicBrainz. The download goes through the same VPN as Movies and is filed as Artist/Album (Year).")
                }
                if looking {
                    Section { HStack(spacing: 16) { ProgressView(); Text("Searching…").foregroundStyle(.secondary) } }
                } else if let errorMessage {
                    Section { Text(errorMessage).foregroundStyle(SentinelTheme.red) }
                } else if let results {
                    Section {
                        if results.isEmpty { Text("Nothing called that on MusicBrainz. Try the artist and album.").foregroundStyle(.secondary) }
                        ForEach(results) { result in
                            Button { Task { await start(result) } } label: { LookupRow(result: result) }
                        }
                    }
                }
            }
            if let notice {
                Section { Text(notice).foregroundStyle(SentinelTheme.green) }
            }
            downloadsSection
        }
        .confirmationDialog(confirming?.title ?? "Download", isPresented: Binding(get: { confirming != nil }, set: { if !$0 { confirming = nil } }),
                            titleVisibility: .visible) {
            Button("Download") { if let release = confirming { Task { await grab(release) } } }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text([confirming?.format, AudioFormat.bytes(confirming?.sizeBytes), confirming?.seeders.map { "\($0) sharing" }, confirming?.warning]
                .compactMap { $0 }.joined(separator: " · "))
        }
        .confirmationDialog(acting?.headline ?? "Download", isPresented: Binding(get: { acting != nil }, set: { if !$0 { acting = nil } }),
                            titleVisibility: .visible) {
            if acting?.isFailed == true {
                Button("Try Again") { if let id = acting?.id { Task { _ = try? await audio.api?.retryMusicDownload(id: id); await loadDownloads() } } }
            }
            Button(acting?.isActive == true ? "Cancel Download" : "Remove from List", role: .destructive) {
                if let id = acting?.id { Task { try? await audio.api?.deleteMusicDownload(id: id); await loadDownloads() } }
            }
        }
        .task {
            status = try? await audio.api?.musicDownloadsStatus()
            #if DEBUG
            if let typed = UserDefaults.standard.string(forKey: "debugMusicAddQuery"), query.isEmpty {
                query = typed
                await lookup()
                if UserDefaults.standard.bool(forKey: "debugMusicAddPick"), let first = results?.first { await start(first) }
            }
            #endif
        }
        // Follows the queue while this pane is open.
        .task {
            while !Task.isCancelled {
                await loadDownloads()
                try? await Task.sleep(for: .seconds(4))
            }
        }
    }

    @ViewBuilder
    private func releasesSection(_ result: MusicLookupResult) -> some View {
        Section {
            LookupRow(result: result)
            Button {
                picked = nil
                search = nil
            } label: {
                Label("Back to Results", systemImage: "chevron.left")
            }
        }
        if let search {
            if !search.isFinished {
                Section {
                    HStack(spacing: 16) {
                        ProgressView()
                        Text(search.message ?? (search.phase == "connecting" ? "Connecting to the VPN…" : "Searching the sites…")).foregroundStyle(.secondary)
                    }
                }
            } else if search.phase == "failed" {
                Section { Text(search.error ?? "The search failed.").foregroundStyle(SentinelTheme.red) }
            } else {
                Section("Releases") {
                    if (search.results ?? []).isEmpty { Text("Nobody is sharing a matching release right now.").foregroundStyle(.secondary) }
                    ForEach(search.results ?? []) { release in
                        Button { confirming = release } label: { ReleaseRow(release: release) }
                    }
                }
                if let others = search.others, !others.isEmpty {
                    Section {
                        if showOthers {
                            ForEach(others) { release in
                                Button { confirming = release } label: { ReleaseRow(release: release) }
                            }
                        } else {
                            Button("Show \(others.count) More") { showOthers = true }
                        }
                    } header: { Text("Other Releases") }
                }
            }
        } else {
            Section { ProgressView() }
        }
    }

    @ViewBuilder
    private var downloadsSection: some View {
        let all = (downloads.active ?? []) + (downloads.recent ?? [])
        if !all.isEmpty {
            Section("Album Downloads") {
                ForEach(all, id: \.self) { download in
                    Button { acting = download } label: { DownloadRow(download: download) }
                }
            }
        }
    }

    private func lookup() async {
        guard !trimmed.isEmpty, let api = audio.api else { return }
        looking = true
        errorMessage = nil
        defer { looking = false }
        do {
            results = try await api.musicLookup(trimmed)
        } catch {
            if !AudioAPI.isCancellation(error) { errorMessage = error.localizedDescription }
        }
    }

    // Starts the background search and polls it every 2 s until done or failed.
    private func start(_ result: MusicLookupResult) async {
        guard let api = audio.api else { return }
        picked = result
        search = nil
        showOthers = false
        do {
            var current = try await api.startMusicSearch(MusicDownloadSearchInput(
                artist: result.artist ?? "", album: result.title, year: result.year, mbid: result.mbid, artUrl: result.artUrl))
            search = current
            while !current.isFinished, picked == result {
                try await Task.sleep(for: .seconds(2))
                current = try await api.musicSearch(id: current.id)
                if picked == result { search = current }
            }
        } catch {
            if !AudioAPI.isCancellation(error) {
                search = MusicDownloadSearch(id: "", phase: "failed", error: error.localizedDescription)
            }
        }
    }

    private func grab(_ release: MusicRelease) async {
        guard let api = audio.api, let search else { return }
        do {
            let download = try await api.musicGrab(MusicGrabInput(searchId: search.id, releaseId: release.id))
            notice = "Downloading \(download.headline). It appears under Recently Added when it's done."
            picked = nil
            self.search = nil
            results = nil
            query = ""
            await loadDownloads()
        } catch {
            notice = nil
            self.search?.error = error.localizedDescription
            errorMessage = error.localizedDescription
        }
    }

    private func loadDownloads() async {
        if let list = try? await audio.api?.musicDownloads() { downloads = list }
    }
}

private struct LookupRow: View {
    let result: MusicLookupResult

    var body: some View {
        HStack(spacing: 24) {
            PosterImage(url: URL(string: result.artUrl ?? ""), title: result.title, maxPixel: 240)
                .frame(width: 110, height: 110)
                .clipShape(RoundedRectangle(cornerRadius: 8))
            VStack(alignment: .leading, spacing: 6) {
                Text(result.title).font(.headline)
                Text([result.artist, result.year.map(String.init), result.type, result.trackCount.map { "\($0) tracks" }]
                    .compactMap { $0 }.joined(separator: " · "))
                    .font(.caption).foregroundStyle(.secondary)
                if result.inLibrary == true {
                    Label("Already in your library", systemImage: "checkmark.circle.fill").font(.caption).foregroundStyle(SentinelTheme.green)
                }
            }
        }
    }
}

private struct ReleaseRow: View {
    let release: MusicRelease

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(release.title ?? "Untitled release").lineLimit(2)
            HStack(spacing: 14) {
                if let format = release.format {
                    Text(format).font(.caption2.bold())
                        .padding(.horizontal, 10).padding(.vertical, 3)
                        .background((release.lossless == true ? SentinelTheme.green : Color.gray).opacity(0.3), in: Capsule())
                }
                Text([AudioFormat.bytes(release.sizeBytes), release.seeders.map { "\($0) seeders" }, release.site]
                    .compactMap { $0 }.joined(separator: " · "))
            }
            .font(.caption)
            .foregroundStyle(.secondary)
            if let warning = release.warning {
                Label(warning, systemImage: "exclamationmark.triangle").font(.caption).foregroundStyle(SentinelTheme.amber)
            }
        }
    }
}

private struct DownloadRow: View {
    let download: MediaDownload

    var body: some View {
        HStack(spacing: 20) {
            PosterImage(url: URL(string: download.posterUrl ?? ""), title: download.displayTitle, maxPixel: 200)
                .frame(width: 80, height: 80)
                .clipShape(RoundedRectangle(cornerRadius: 6))
            VStack(alignment: .leading, spacing: 6) {
                Text(download.headline).lineLimit(1)
                if download.isActive {
                    ProgressView(value: download.fraction).tint(SentinelTheme.green)
                }
                Text(line).font(.caption).foregroundStyle(download.isFailed ? SentinelTheme.red : .secondary).lineLimit(2)
            }
        }
    }

    private var line: String {
        if download.isFailed { return download.error ?? "Failed" }
        if download.isDone { return "Added to the library" }
        if let error = download.error { return error }
        return "\(download.state?.capitalized ?? "Queued") · \(Int(download.fraction * 100))%"
    }
}
